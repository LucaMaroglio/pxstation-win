using System.Threading.Channels;
using SpiceKit.Channels;
using SpiceKit.Transport;
using SpiceKit.VVFile;
using SpiceKit.Wire;

namespace SpiceKit.Session;

/// <summary>Notifiche della sessione per l'interfaccia.</summary>
public abstract record SessionEvent
{
    public sealed record StateChanged(SessionState State) : SessionEvent;

    public sealed record MouseModeChanged(MouseModes Mode) : SessionEvent;

    public sealed record AgentChanged(bool Connected) : SessionEvent;

    public sealed record GuestNameChanged(string Name) : SessionEvent;

    /// <summary>Canali offerti dal server e quelli collegati da questo client.</summary>
    public sealed record ChannelsLinked(IReadOnlyList<ChannelEntry> Offered, IReadOnlyList<ChannelEntry> Linked) : SessionEvent;
}

/// <summary>
/// Facciata pubblica: una sessione SPICE (porting di <c>SpiceSession.swift</c>).
/// </summary>
/// <remarks>
/// <para>Collega il canale main (<c>connection_id</c> 0), prende l'id di sessione da
/// <c>SPICE_MSG_MAIN_INIT</c> e collega con quello gli altri canali elencati da
/// <c>CHANNELS_LIST</c>, tutti subito: i ticket Proxmox scadono in circa 30 secondi. Poi il
/// ticket viene azzerato.</para>
/// <para>M2: display, inputs e cursor vengono collegati e i loro messaggi scartati (il
/// protocollo base, ACK e PING, resta attivo); il loro contenuto arriva con M3–M4.</para>
/// </remarks>
public sealed class SpiceSession : IAsyncDisposable
{
    private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ChannelOpener _opener;
    private readonly TransportException? _configurationError;
    private readonly SecretTicket? _ticket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<ChannelConnection> _channels = [];
    private readonly List<Task> _tasks = [];
    private SessionState _state = SessionState.Idle;
    private MainChannel? _main;
    private uint? _sessionId;
    private bool _channelsRequested;

    public SpiceSession(ConnectionProfile profile, IStreamConnector connector, ITicketEncryptor encryptor, TimeSpan? linkTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _ticket = profile.Password;
        SpiceEndpoint endpoint;
        try
        {
            endpoint = SpiceEndpoint.From(profile);
        }
        catch (TransportException error)
        {
            // Mai ripiegare su controlli più deboli: la sessione fallisce in ConnectAsync.
            endpoint = new SpiceEndpoint(profile.Host);
            _configurationError = error;
        }
        _opener = new ChannelOpener(endpoint, connector, encryptor, CopyTicket) { LinkTimeout = linkTimeout ?? TimeSpan.FromSeconds(20) };
    }

    /// <summary>Con socket TCP, <see cref="System.Net.Security.SslStream"/> e RSA di sistema.</summary>
    public SpiceSession(ConnectionProfile profile) : this(profile, new TcpConnector(), new RsaTicketEncryptor())
    {
    }

    /// <summary>Eventi per l'interfaccia, in ordine; si completa alla fine della sessione.</summary>
    public ChannelReader<SessionEvent> Events => _events.Reader;

    public SessionState State
    {
        get
        {
            lock (_lock) return _state;
        }
    }


    private int CopyTicket(byte[] buffer) => _ticket?.WithBytes((bytes, present) =>
    {
        if (!present) return 0;
        if (bytes.Length > buffer.Length - 1) return -1;
        bytes.CopyTo(buffer);
        return bytes.Length;
    }) ?? 0;

    /// <summary>Collega il canale main; gli altri seguono quando il server li elenca.</summary>
    public async Task ConnectAsync()
    {
        lock (_lock)
        {
            if (_state.Kind != SessionStateKind.Idle) return;
        }
        SetState(SessionState.Connecting);
        if (_configurationError is not null)
        {
            await TeardownAsync(SessionFailure.From(_configurationError)).ConfigureAwait(false);
            return;
        }
        try
        {
            var connection = await _opener.OpenAsync(ChannelType.Main, 0, 0, MainChannel.Capabilities, _stop.Token).ConfigureAwait(false);
            var main = new MainChannel(connection);
            lock (_lock)
            {
                // L'utente può aver chiuso mentre si collegava.
                if (_state.Kind != SessionStateKind.Connecting)
                {
                    _ = connection.DisposeAsync().AsTask();
                    return;
                }
                _main = main;
            }
            Spawn("main", critical: true, () => main.RunAsync(_stop.Token));
            Spawn("main-events", critical: false, () => ConsumeMainEventsAsync(main));
        }
        catch (Exception error) when (error is TransportException or ChannelException or OperationCanceledException)
        {
            await FailAsync("main", error).ConfigureAwait(false);
        }
    }

    public Task DisconnectAsync() => TeardownAsync(null);

    public async ValueTask DisposeAsync()
    {
        await TeardownAsync(null).ConfigureAwait(false);
        Task[] tasks;
        lock (_lock) tasks = [.. _tasks];
        // I cicli terminano con la cancellazione e la chiusura dei flussi.
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task ConsumeMainEventsAsync(MainChannel main)
    {
        await foreach (var mainEvent in main.Events.ReadAllAsync().ConfigureAwait(false))
        {
            switch (mainEvent)
            {
                case MainEvent.Initialized init:
                    _sessionId = init.SessionId;
                    Emit(new SessionEvent.MouseModeChanged(init.CurrentMode));
                    Emit(new SessionEvent.AgentChanged(init.HasAgent));
                    break;
                case MainEvent.Channels channels:
                    await OpenChannelsAsync(channels.List).ConfigureAwait(false);
                    break;
                case MainEvent.MouseMode mode:
                    Emit(new SessionEvent.MouseModeChanged(mode.Current));
                    break;
                case MainEvent.AgentConnected agent:
                    Emit(new SessionEvent.AgentChanged(agent.Connected));
                    break;
                case MainEvent.GuestName name:
                    Emit(new SessionEvent.GuestNameChanged(name.Name));
                    break;
            }
        }
    }

    /// <summary>
    /// Canali da collegare. L'elenco viene dal server: duplicati scartati e numero di connessioni
    /// limitato (un canale per tipo).
    /// </summary>
    internal static IReadOnlyList<ChannelEntry> Wanted(IReadOnlyList<ChannelEntry> offered) =>
        [.. offered.Distinct().Where(e => e.Id == 0 && e.Type is ChannelType.Display or ChannelType.Inputs or ChannelType.Cursor)];

    private async Task OpenChannelsAsync(IReadOnlyList<ChannelEntry> offered)
    {
        if (_channelsRequested || _sessionId is not { } sessionId) return;
        _channelsRequested = true;
        var wanted = Wanted(offered);
        SpiceLog.Info("session", $"il server offre {offered.Count} canali ({string.Join(", ", offered)}); se ne collegano {wanted.Count}");
        try
        {
            var opened = await Task.WhenAll(wanted.Select(entry =>
                _opener.OpenAsync(entry.Type, entry.Id, sessionId, new CapabilitySet(), _stop.Token))).ConfigureAwait(false);
            lock (_lock)
            {
                if (_state.Kind != SessionStateKind.Connecting)
                {
                    foreach (var connection in opened) _ = connection.DisposeAsync().AsTask();
                    return;
                }
                _channels.AddRange(opened);
            }
            foreach (var connection in opened)
            {
                // Display (M3), inputs e cursor (M4): per ora si tiene vivo il canale e si scartano i messaggi.
                Spawn($"{connection.Type.Name()}:{connection.Id}", critical: connection.Type != ChannelType.Cursor,
                    () => DrainAsync(connection));
            }
        }
        catch (Exception error) when (error is TransportException or ChannelException or OperationCanceledException)
        {
            await FailAsync("canali", error).ConfigureAwait(false);
            return;
        }
        // Tutti i canali si sono autenticati con il ticket monouso: lo si dimentica.
        _ticket?.Wipe();
        SetState(SessionState.Connected);
        Emit(new SessionEvent.ChannelsLinked(offered, wanted));
        SpiceLog.Info("session", "sessione connessa");
    }

    private async Task DrainAsync(ChannelConnection connection)
    {
        while (true)
        {
            var message = await connection.NextMessageAsync(_stop.Token).ConfigureAwait(false);
            SpiceLog.Debug($"{connection.Type.Name()}:{connection.Id}", $"messaggio {message.Type} ({message.Payload.Length} byte) non ancora gestito");
        }
    }

    private void Spawn(string name, bool critical, Func<Task> body)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await body().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception error) when (error is TransportException or ChannelException or OperationCanceledException)
            {
                if (critical) await FailAsync(name, error).ConfigureAwait(false);
                else SpiceLog.Info("session", $"canale {name} chiuso: {error.Message}");
            }
        });
        lock (_lock) _tasks.Add(task);
    }

    private Task FailAsync(string name, Exception error)
    {
        if (State.Kind == SessionStateKind.Disconnected) return Task.CompletedTask;
        SpiceLog.Info("session", $"{name}: {error.GetType().Name}: {error.Message}");
        return TeardownAsync(SessionFailure.From(error is OperationCanceledException ? TransportException.TimedOut() : error));
    }

    private async Task TeardownAsync(SessionFailure? failure)
    {
        List<ChannelConnection> channels;
        MainChannel? main;
        lock (_lock)
        {
            if (_state.Kind == SessionStateKind.Disconnected) return;
            _state = SessionState.Disconnected(failure);
            Emit(new SessionEvent.StateChanged(_state));
            channels = [.. _channels];
            _channels.Clear();
            main = _main;
            _main = null;
        }
        _ticket?.Wipe();
        await _stop.CancelAsync().ConfigureAwait(false);
        if (main is not null) await main.DisposeAsync().ConfigureAwait(false);
        foreach (var channel in channels) await channel.DisposeAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
    }

    private void SetState(SessionState state)
    {
        lock (_lock)
        {
            if (_state.Kind == SessionStateKind.Disconnected) return; // stato finale
            _state = state;
            // Sotto il lock: l'ordine degli stati non si inverte con una chiusura concorrente.
            Emit(new SessionEvent.StateChanged(state));
        }
    }

    private void Emit(SessionEvent value) => _events.Writer.TryWrite(value);
}

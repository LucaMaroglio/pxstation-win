using System.Threading.Channels;
using SpiceKit.Agent;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>Eventi del canale main.</summary>
public abstract record MainEvent
{
    public sealed record Initialized(uint SessionId, MouseModes MouseModes, MouseModes CurrentMode, bool HasAgent) : MainEvent;

    public sealed record Channels(IReadOnlyList<ChannelEntry> List) : MainEvent;

    public sealed record MouseMode(MouseModes Current, MouseModes Supported) : MainEvent;

    public sealed record AgentConnected(bool Connected) : MainEvent;

    /// <summary>Capacità annunciate dall'agente dell'ospite.</summary>
    public sealed record AgentCapabilities(CapabilitySet Capabilities) : MainEvent;

    /// <summary>Messaggio dell'agente (appunti, stato dei trasferimenti…), già ricomposto.</summary>
    public sealed record AgentMessageReceived(AgentMessage Message) : MainEvent;

    public sealed record GuestName(string Name) : MainEvent;

    public sealed record GuestUuid(Guid Uuid) : MainEvent;
}

/// <summary>
/// Canale main (porting di <c>MainChannel.swift</c>): avvio della sessione, elenco dei canali,
/// modalità del mouse e il canale dell'agente (vdagent) con il suo controllo di flusso.
/// </summary>
/// <remarks>
/// Agente: con l'agente collegato si invia <c>AGENT_START</c> (gettoni di ricezione) e si
/// annunciano le capacità; i messaggi in arrivo si ricompongono e i gettoni si restituiscono man
/// mano. L'invio passa da <see cref="AgentOutbox"/>, limitata e svuotata in ordine.
/// </remarks>
public sealed class MainChannel(ChannelConnection connection, MultimediaClock? clock = null) : IAsyncDisposable
{
    /// <summary>Capacità annunciate dal client.</summary>
    public static CapabilitySet Capabilities => CapabilitySet.FromBits(
        (int)MainCapability.NameAndUuid, (int)MainCapability.AgentConnectedTokens);

    /// <summary>Capacità vdagent annunciate dal client.</summary>
    public static IReadOnlyList<AgentCapability> AgentCapabilities { get; } =
    [
        AgentCapability.MouseState, AgentCapability.MonitorsConfig, AgentCapability.Reply,
        AgentCapability.SparseMonitorsConfig, AgentCapability.MonitorsConfigPosition,
        AgentCapability.ClipboardByDemand, AgentCapability.ClipboardSelection,
    ];

    /// <summary>Messaggi dell'agente che possiamo tenere in coda (restituiti man mano).</summary>
    internal const uint AgentReceiveTokens = 10;

    private readonly Channel<MainEvent> _events = Channel.CreateUnbounded<MainEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly AgentOutbox _outbox = new();
    private readonly AgentMessageAssembler _assembler = new();
    private MouseModes _supportedModes;
    private MouseModes _currentMode;
    private uint _consumedAgentMessages;
    private volatile bool _agentConnected;
    private int _capabilityReplyQueued;

    public ChannelReader<MainEvent> Events => _events.Reader;

    public bool IsAgentConnected => _agentConnected;

    /// <summary>Byte per l'agente in coda e non ancora inviati (per dosare i trasferimenti di file).</summary>
    public int PendingAgentBytes => _outbox.PendingBytes;

    /// <summary>Elabora i messaggi finché il canale non si chiude (lancia per errore o disconnessione).</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = _outbox.PumpAsync((chunk, ct) => connection.SendAsync(MainClientEncoder.AgentData(chunk), ct), stop.Token);
        try
        {
            while (true)
            {
                if (pump.IsFaulted) await pump.ConfigureAwait(false); // errore di invio: chiude il canale
                var message = await connection.NextMessageAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await HandleAsync(message, cancellationToken).ConfigureAwait(false);
                }
                catch (WireException error)
                {
                    throw ChannelException.Malformed($"messaggio main {message.Type}", error);
                }
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or Transport.TransportException)
            {
            }
            _events.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();

    /// <summary>Chiede la modalità client (puntatore assoluto) o server (relativo).</summary>
    public async Task RequestMouseModeAsync(MouseModes mode, CancellationToken cancellationToken = default)
    {
        if (!_supportedModes.HasFlag(mode) || mode == _currentMode) return;
        await connection.SendAsync(MainClientEncoder.MouseModeRequest(mode), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Accoda un messaggio per l'agente. <see langword="false"/> senza agente o con la coda piena
    /// (l'agente non consuma): il messaggio si scarta, mai una coda senza limiti.
    /// </summary>
    public bool SendToAgent(AgentMessage message)
    {
        if (!_agentConnected) return false;
        if (_outbox.Enqueue(message)) return true;
        SpiceLog.Info("main", $"coda dell'agente piena: messaggio di tipo {message.Type} scartato");
        return false;
    }

    private async Task HandleAsync(SpiceMessage message, CancellationToken cancellationToken)
    {
        switch ((MainServerMessage)message.Type)
        {
            case MainServerMessage.Init:
                var init = MainInit.Decode(message.Payload);
                _supportedModes = init.SupportedMouseModes;
                _currentMode = init.CurrentMouseMode;
                clock?.Set(init.MultimediaTime);
                _outbox.SetTokens(init.AgentTokens);
                Emit(new MainEvent.Initialized(init.SessionId, _supportedModes, _currentMode, init.AgentConnected));
                if (init.AgentConnected) await AgentDidConnectAsync(cancellationToken).ConfigureAwait(false);
                await connection.SendAsync(MainClientEncoder.AttachChannels(), cancellationToken).ConfigureAwait(false);
                // Modalità client (puntatore assoluto) ogni volta che il server la offre.
                if (_supportedModes.HasFlag(MouseModes.Client) && _currentMode != MouseModes.Client)
                {
                    await connection.SendAsync(MainClientEncoder.MouseModeRequest(MouseModes.Client), cancellationToken).ConfigureAwait(false);
                }
                break;
            case MainServerMessage.ChannelsList:
                Emit(new MainEvent.Channels(ChannelsList.Decode(message.Payload)));
                break;
            case MainServerMessage.MouseMode:
                var mode = MainMouseMode.Decode(message.Payload);
                var supportChanged = mode.Supported != _supportedModes;
                _supportedModes = mode.Supported;
                _currentMode = mode.Current;
                Emit(new MainEvent.MouseMode(mode.Current, mode.Supported));
                // Una richiesta ogni volta che la modalità client diventa disponibile (niente cicli).
                if (supportChanged && _supportedModes.HasFlag(MouseModes.Client) && _currentMode != MouseModes.Client)
                {
                    await connection.SendAsync(MainClientEncoder.MouseModeRequest(MouseModes.Client), cancellationToken).ConfigureAwait(false);
                }
                break;
            case MainServerMessage.AgentConnected:
                await AgentDidConnectAsync(cancellationToken).ConfigureAwait(false);
                break;
            case MainServerMessage.AgentConnectedTokens:
                _outbox.SetTokens(MainDecoders.UInt32(message.Payload));
                await AgentDidConnectAsync(cancellationToken).ConfigureAwait(false);
                break;
            case MainServerMessage.AgentDisconnected:
                AgentDidDisconnect();
                break;
            case MainServerMessage.AgentToken:
                _outbox.AddTokens(MainDecoders.UInt32(message.Payload));
                break;
            case MainServerMessage.AgentData:
                HandleAgentData(message.Payload);
                if (++_consumedAgentMessages >= AgentReceiveTokens / 2)
                {
                    await connection.SendAsync(MainClientEncoder.AgentToken(_consumedAgentMessages), cancellationToken).ConfigureAwait(false);
                    _consumedAgentMessages = 0;
                }
                break;
            case MainServerMessage.MultiMediaTime:
                clock?.Set(MainDecoders.UInt32(message.Payload));
                break;
            case MainServerMessage.Name:
                Emit(new MainEvent.GuestName(MainDecoders.Name(message.Payload)));
                break;
            case MainServerMessage.Uuid:
                Emit(new MainEvent.GuestUuid(MainDecoders.Uuid(message.Payload)));
                break;
            case MainServerMessage.MigrateBegin:
            case MainServerMessage.MigrateBeginSeamless:
            case MainServerMessage.MigrateSwitchHost:
                SpiceLog.Info("main", "migrazione richiesta: non supportata");
                throw ChannelException.MigrationUnsupported();
            default:
                // Fine o annullo della migrazione: nessun effetto.
                SpiceLog.Debug("main", $"messaggio main {message.Type} ignorato");
                break;
        }
    }

    private async Task AgentDidConnectAsync(CancellationToken cancellationToken)
    {
        if (_agentConnected) return;
        _agentConnected = true;
        await connection.SendAsync(MainClientEncoder.AgentStart(AgentReceiveTokens), cancellationToken).ConfigureAwait(false);
        Emit(new MainEvent.AgentConnected(true));
        SendToAgent(AgentMessage.AnnounceCapabilities(request: true, AgentCapabilities));
    }

    private void AgentDidDisconnect()
    {
        if (!_agentConnected) return;
        _agentConnected = false;
        _consumedAgentMessages = 0;
        _outbox.Clear();
        _assembler.Reset();
        Volatile.Write(ref _capabilityReplyQueued, 0);
        Emit(new MainEvent.AgentConnected(false));
    }

    private void HandleAgentData(ReadOnlySpan<byte> chunk)
    {
        List<AgentMessage> messages;
        try
        {
            messages = _assembler.Feed(chunk);
        }
        catch (WireException error)
        {
            // Dati dell'agente non validi: si scartano, la sessione continua.
            SpiceLog.Info("main", $"dati dell'agente scartati: {error.Message}");
            return;
        }
        foreach (var message in messages)
        {
            if (message.KnownType == AgentMessageType.AnnounceCapabilities)
            {
                bool request;
                CapabilitySet caps;
                try
                {
                    (request, caps) = AgentMessage.DecodeCapabilities(message.Data);
                }
                catch (WireException)
                {
                    continue;
                }
                Emit(new MainEvent.AgentCapabilities(caps));
                // Le nostre capacità non cambiano: una sola risposta in coda risponde a tutte le richieste.
                if (request && Interlocked.Exchange(ref _capabilityReplyQueued, 1) == 0)
                {
                    SendToAgent(AgentMessage.AnnounceCapabilities(request: false, AgentCapabilities));
                }
                continue;
            }
            Emit(new MainEvent.AgentMessageReceived(message));
        }
        if (_outbox.PendingBytes == 0) Volatile.Write(ref _capabilityReplyQueued, 0);
    }

    private void Emit(MainEvent value) => _events.Writer.TryWrite(value);
}

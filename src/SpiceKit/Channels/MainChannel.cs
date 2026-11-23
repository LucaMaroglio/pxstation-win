using System.Threading.Channels;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>Eventi del canale main.</summary>
public abstract record MainEvent
{
    public sealed record Initialized(uint SessionId, MouseModes MouseModes, MouseModes CurrentMode, bool HasAgent) : MainEvent;

    public sealed record Channels(IReadOnlyList<ChannelEntry> List) : MainEvent;

    public sealed record MouseMode(MouseModes Current, MouseModes Supported) : MainEvent;

    public sealed record AgentConnected(bool Connected) : MainEvent;

    public sealed record GuestName(string Name) : MainEvent;

    public sealed record GuestUuid(Guid Uuid) : MainEvent;
}

/// <summary>
/// Canale main: avvio della sessione, elenco dei canali, modalità del mouse, stato dell'agente.
/// </summary>
/// <remarks>
/// Porting di <c>MainChannel.swift</c> senza il canale dell'agente (vdagent), che arriva con
/// M6: per ora si registra solo se l'agente è presente, senza inviare <c>AGENT_START</c>.
/// </remarks>
public sealed class MainChannel(ChannelConnection connection) : IAsyncDisposable
{
    /// <summary>Capacità annunciate dal client.</summary>
    public static CapabilitySet Capabilities => CapabilitySet.FromBits(
        (int)MainCapability.NameAndUuid, (int)MainCapability.AgentConnectedTokens);

    private readonly Channel<MainEvent> _events = Channel.CreateUnbounded<MainEvent>(new UnboundedChannelOptions { SingleReader = true });
    private MouseModes _supportedModes;
    private MouseModes _currentMode;

    public ChannelReader<MainEvent> Events => _events.Reader;

    public bool IsAgentConnected { get; private set; }

    /// <summary>Elabora i messaggi finché il canale non si chiude (lancia per errore o disconnessione).</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
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

    private async Task HandleAsync(SpiceMessage message, CancellationToken cancellationToken)
    {
        switch ((MainServerMessage)message.Type)
        {
            case MainServerMessage.Init:
                var init = MainInit.Decode(message.Payload);
                _supportedModes = init.SupportedMouseModes;
                _currentMode = init.CurrentMouseMode;
                IsAgentConnected = init.AgentConnected;
                Emit(new MainEvent.Initialized(init.SessionId, _supportedModes, _currentMode, init.AgentConnected));
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
            case MainServerMessage.AgentConnectedTokens:
                SetAgent(true);
                break;
            case MainServerMessage.AgentDisconnected:
                SetAgent(false);
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
                // AGENT_DATA/AGENT_TOKEN (M6), MULTI_MEDIA_TIME (M7), fine/annullo migrazione.
                SpiceLog.Debug("main", $"messaggio main {message.Type} ignorato");
                break;
        }
    }

    private void SetAgent(bool connected)
    {
        if (IsAgentConnected == connected) return;
        IsAgentConnected = connected;
        Emit(new MainEvent.AgentConnected(connected));
    }

    private void Emit(MainEvent value) => _events.Writer.TryWrite(value);
}

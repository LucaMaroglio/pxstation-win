using System.Globalization;
using SpiceKit.Transport;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

public enum ChannelErrorKind
{
    ServerDisconnected,
    MigrationUnsupported,
    MessageTooLarge,
    Malformed,
}

/// <summary>Perché un canale si è fermato.</summary>
public sealed class ChannelException : Exception
{
    private ChannelException(ChannelErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    public ChannelErrorKind Kind { get; }

    public static ChannelException ServerDisconnected(uint reason) => new(ChannelErrorKind.ServerDisconnected,
        "Il server ha terminato la sessione (motivo " + reason.ToString(CultureInfo.InvariantCulture) + ").");

    public static ChannelException MigrationUnsupported() =>
        new(ChannelErrorKind.MigrationUnsupported, "La macchina virtuale si sta spostando su un altro host.");

    public static ChannelException MessageTooLarge(uint size) => new(ChannelErrorKind.MessageTooLarge,
        "Il server ha inviato un messaggio troppo grande (" + size.ToString(CultureInfo.InvariantCulture) + " byte).");

    public static ChannelException Malformed(string what, Exception? inner = null) =>
        new(ChannelErrorKind.Malformed, "Il server ha inviato un messaggio non valido (" + what + ").", inner);
}

/// <summary>
/// Un canale SPICE stabilito: framing con mini-header e protocollo base (<c>SET_ACK</c>/<c>ACK</c>,
/// <c>PING</c>/<c>PONG</c>, <c>NOTIFY</c>, <c>DISCONNECTING</c>, <c>MIGRATE*</c>).
/// </summary>
/// <remarks>
/// <see cref="NextMessageAsync"/> gestisce da sé i messaggi base e restituisce solo quelli del
/// canale (tipo ≥ 101). Un solo lettore; <see cref="SendAsync"/> si può chiamare da più task.
/// </remarks>
public sealed class ChannelConnection(ChannelType type, byte id, ISpiceTransport stream, CapabilitySet serverCaps)
    : IAsyncDisposable
{
    /// <summary>Messaggio più grande accettato dal server (limite contro input ostili).</summary>
    public const uint MaxMessageSize = 128 * 1024 * 1024;

    private readonly byte[] _header = new byte[MiniHeader.WireSize];
    private uint _ackWindow;
    private uint _messagesSinceAck;

    public ChannelType Type => type;
    public byte Id => id;
    public CapabilitySet ServerCaps => serverCaps;
    private string Category => $"{type.Name()}:{id}";

    public Task SendAsync(SpiceMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return stream.SendAsync(message.Encode(), cancellationToken);
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();

    /// <summary>Prossimo messaggio specifico del canale (tipo ≥ 101).</summary>
    public async Task<SpiceMessage> NextMessageAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var message = await ReceiveRawAsync(cancellationToken).ConfigureAwait(false);
            if (await HandleBaseAsync(message, cancellationToken).ConfigureAwait(false)) continue;
            return message;
        }
    }

    private async Task<SpiceMessage> ReceiveRawAsync(CancellationToken cancellationToken)
    {
        await stream.ReceiveExactlyAsync(_header, cancellationToken).ConfigureAwait(false);
        var header = MiniHeader.Decode(_header);
        if (header.Size > MaxMessageSize) throw ChannelException.MessageTooLarge(header.Size);
        var payload = await stream.ReceiveAsync((int)header.Size, cancellationToken).ConfigureAwait(false);
        if (_ackWindow > 0 && ++_messagesSinceAck >= _ackWindow)
        {
            _messagesSinceAck = 0;
            await SendAsync(BaseClientEncoder.Ack(), cancellationToken).ConfigureAwait(false);
        }
        return new SpiceMessage(header.Type, payload);
    }

    /// <summary><see langword="true"/> se il messaggio era del protocollo base ed è stato gestito.</summary>
    private async Task<bool> HandleBaseAsync(SpiceMessage message, CancellationToken cancellationToken)
    {
        if (message.Type >= 101) return false;
        try
        {
            switch ((BaseServerMessage)message.Type)
            {
                case BaseServerMessage.SetAck:
                    var ack = SetAck.Decode(message.Payload);
                    _ackWindow = ack.Window;
                    _messagesSinceAck = 0;
                    await SendAsync(BaseClientEncoder.AckSync(ack.Generation), cancellationToken).ConfigureAwait(false);
                    break;
                case BaseServerMessage.Ping:
                    var ping = Ping.Decode(message.Payload);
                    await SendAsync(BaseClientEncoder.Pong(ping.Id, ping.Timestamp), cancellationToken).ConfigureAwait(false);
                    break;
                case BaseServerMessage.Notify:
                    var notify = Notify.Decode(message.Payload);
                    SpiceLog.Info(Category, $"avviso del server (gravità {notify.Severity}): {WireText.Sanitize(notify.Message)}");
                    break;
                case BaseServerMessage.Disconnecting:
                    throw ChannelException.ServerDisconnected(Disconnecting.Decode(message.Payload).Reason);
                case BaseServerMessage.WaitForChannels:
                    // Ordine fra canali non imposto in v1: ogni canale procede in ordine.
                    SpiceLog.Debug(Category, "wait_for_channels ignorato");
                    break;
                case BaseServerMessage.Migrate:
                case BaseServerMessage.MigrateData:
                    SpiceLog.Info(Category, "migrazione richiesta: non supportata");
                    throw ChannelException.MigrationUnsupported();
                default:
                    // SPICE_MSG_LIST si usa solo con l'intestazione completa; tipi base ignoti: ignorati.
                    SpiceLog.Debug(Category, $"messaggio base {message.Type} ignorato");
                    break;
            }
        }
        catch (WireException error)
        {
            throw ChannelException.Malformed($"messaggio base {message.Type}", error);
        }
        return true;
    }
}

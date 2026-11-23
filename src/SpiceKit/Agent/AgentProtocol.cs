// Protocollo di spice-vdagent, trasportato dai messaggi AGENT_DATA del canale main.
// Costanti trascritte da spice-protocol 0.14.3 spice/vd_agent.h (licenza BSD).

using SpiceKit.Wire;

namespace SpiceKit.Agent;

/// <summary><c>VD_AGENT_*</c>: tipi di messaggio.</summary>
public enum AgentMessageType : uint
{
    MouseState = 1,
    MonitorsConfig,
    Reply,
    Clipboard,
    DisplayConfig,
    AnnounceCapabilities,
    ClipboardGrab,
    ClipboardRequest,
    ClipboardRelease,
    FileXferStart,
    FileXferStatus,
    FileXferData,
    ClientDisconnected,
    MaxClipboard,
    AudioVolumeSync,
    GraphicsDeviceInfo,
}

/// <summary><c>VD_AGENT_CAP_*</c>: indici di bit.</summary>
public enum AgentCapability
{
    MouseState = 0,
    MonitorsConfig,
    Reply,
    Clipboard,
    DisplayConfig,
    ClipboardByDemand,
    ClipboardSelection,
    SparseMonitorsConfig,
    GuestLineEndLf,
    GuestLineEndCrlf,
    MaxClipboard,
    AudioVolumeSync,
    MonitorsConfigPosition,
    FileXferDisabled,
    FileXferDetailedErrors,
    GraphicsDeviceInfo,
    ClipboardNoReleaseOnRegrab,
    ClipboardGrabSerial,
}

/// <summary>Un messaggio completo dell'agente (intestazione <c>VDAgentMessage</c> + dati).</summary>
public sealed record AgentMessage(uint Type, ulong Opaque, byte[] Data)
{
    /// <summary><c>VD_AGENT_PROTOCOL</c>.</summary>
    public const uint Protocol = 1;

    public const int HeaderSize = 20;

    /// <summary><c>VD_AGENT_MAX_DATA_SIZE</c>: carico massimo di un AGENT_DATA (intestazione compresa).</summary>
    public const int MaxChunkSize = 2048;

    public AgentMessage(AgentMessageType type, byte[] data) : this((uint)type, 0, data)
    {
    }

    public AgentMessageType? KnownType => Type is >= 1 and <= 16 ? (AgentMessageType)Type : null;

    /// <summary>Intestazione e dati, divisi in carichi AGENT_DATA di al massimo <see cref="MaxChunkSize"/> byte.</summary>
    public IReadOnlyList<byte[]> EncodeChunks()
    {
        var w = new ByteWriter(HeaderSize + Data.Length);
        w.WriteU32(Protocol);
        w.WriteU32(Type);
        w.WriteU64(Opaque);
        w.WriteU32((uint)Data.Length);
        w.WriteBytes(Data);
        var bytes = w.WrittenSpan;
        var chunks = new List<byte[]>((bytes.Length + MaxChunkSize - 1) / MaxChunkSize);
        for (var offset = 0; offset < bytes.Length; offset += MaxChunkSize)
        {
            chunks.Add(bytes.Slice(offset, Math.Min(MaxChunkSize, bytes.Length - offset)).ToArray());
        }
        return chunks;
    }

    /// <summary><c>VD_AGENT_ANNOUNCE_CAPABILITIES</c>.</summary>
    public static AgentMessage AnnounceCapabilities(bool request, IEnumerable<AgentCapability> capabilities)
    {
        var caps = CapabilitySet.FromBits([.. capabilities.Select(c => (int)c)]);
        var w = new ByteWriter();
        w.WriteU32(request ? 1u : 0u);
        foreach (var word in caps.Words) w.WriteU32(word);
        return new AgentMessage(AgentMessageType.AnnounceCapabilities, w.ToArray());
    }

    /// <summary>
    /// <c>VD_AGENT_MONITORS_CONFIG</c> (misure in pixel dell'ospite), con
    /// <c>VD_AGENT_CONFIG_MONITORS_FLAG_USE_POS</c>.
    /// </summary>
    public static AgentMessage MonitorsConfig(IReadOnlyList<(uint Width, uint Height, int X, int Y)> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var w = new ByteWriter(8 + (monitors.Count * 20));
        w.WriteU32((uint)monitors.Count);
        w.WriteU32(1);
        foreach (var (width, height, x, y) in monitors)
        {
            w.WriteU32(height);
            w.WriteU32(width);
            w.WriteU32(32); // profondità di colore
            w.WriteI32(x);
            w.WriteI32(y);
        }
        return new AgentMessage(AgentMessageType.MonitorsConfig, w.ToArray());
    }

    /// <summary>Capacità annunciate dall'agente (dopo il campo <c>request</c>), al massimo 16 parole.</summary>
    public static (bool Request, CapabilitySet Capabilities) DecodeCapabilities(ReadOnlySpan<byte> data)
    {
        var r = new ByteReader(data);
        var request = r.ReadU32() != 0;
        var words = new List<uint>();
        while (words.Count < 16 && r.Remaining >= 4) words.Add(r.ReadU32());
        return (request, new CapabilitySet(words));
    }
}

/// <summary>Ricompone i messaggi dell'agente che il server divide fra più AGENT_DATA.</summary>
public sealed class AgentMessageAssembler
{
    /// <summary>Messaggio più grande accettato dall'agente (appunti al massimo + margine).</summary>
    public const int MaxMessageSize = ClipboardCodec.MaxDataSize + (1024 * 1024);

    private byte[] _pending = [];
    private int _length;

    /// <summary>Aggiunge un carico AGENT_DATA; restituisce i messaggi completati.</summary>
    public List<AgentMessage> Feed(ReadOnlySpan<byte> chunk)
    {
        if (_length + chunk.Length > _pending.Length) Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _length + chunk.Length));
        chunk.CopyTo(_pending.AsSpan(_length));
        _length += chunk.Length;
        var messages = new List<AgentMessage>();
        while (true)
        {
            var buffer = _pending.AsSpan(0, _length);
            if (buffer.Length < AgentMessage.HeaderSize) break;
            var r = new ByteReader(buffer);
            var protocol = r.ReadU32();
            var type = r.ReadU32();
            var opaque = r.ReadU64();
            var size = r.ReadU32();
            if (protocol != AgentMessage.Protocol)
            {
                Reset();
                throw WireException.InvalidValue($"protocollo dell'agente {protocol}");
            }
            if (size > MaxMessageSize)
            {
                Reset();
                throw WireException.LimitExceeded($"messaggio dell'agente di {size} byte");
            }
            if (buffer.Length < AgentMessage.HeaderSize + (int)size) break;
            var data = buffer.Slice(AgentMessage.HeaderSize, (int)size).ToArray();
            messages.Add(new AgentMessage(type, opaque, data));
            var consumed = AgentMessage.HeaderSize + (int)size;
            buffer[consumed..].CopyTo(_pending);
            _length -= consumed;
        }
        // La memoria si restituisce quando non c'è più nulla in sospeso (dopo un appunto grande).
        if (_length == 0 && _pending.Length > 64 * 1024) _pending = [];
        return messages;
    }

    public void Reset()
    {
        _length = 0;
        _pending = [];
    }
}

// Appunti del protocollo vdagent (vd_agent.h, licenza BSD): VD_AGENT_CLIPBOARD_GRAB / _REQUEST /
// VD_AGENT_CLIPBOARD / _RELEASE. Con VD_AGENT_CAP_CLIPBOARD_SELECTION ogni messaggio inizia con
// 4 byte: selezione (uint8) + 3 riservati; senza, non c'è intestazione ed esiste solo CLIPBOARD.

using System.Text;
using SpiceKit.Wire;

namespace SpiceKit.Agent;

/// <summary><c>VD_AGENT_CLIPBOARD_*</c>: tipi di dati.</summary>
public enum ClipboardDataType : uint
{
    None = 0,
    Utf8Text,
    Png,
    Bmp,
    Tiff,
    Jpeg,
    FileList,
}

/// <summary><c>VD_AGENT_CLIPBOARD_SELECTION_*</c>. Windows ha solo gli appunti normali.</summary>
public enum ClipboardSelection : byte
{
    Clipboard = 0,
    Primary,
    Secondary,
}

/// <summary>Messaggio degli appunti decodificato.</summary>
public abstract record ClipboardMessage(ClipboardSelection Selection)
{
    public sealed record Grab(ClipboardSelection Selection, IReadOnlyList<ClipboardDataType> Types) : ClipboardMessage(Selection);

    public sealed record Request(ClipboardSelection Selection, ClipboardDataType Type) : ClipboardMessage(Selection);

    public sealed record Data(ClipboardSelection Selection, ClipboardDataType Type, byte[] Bytes) : ClipboardMessage(Selection);

    public sealed record Release(ClipboardSelection Selection) : ClipboardMessage(Selection);
}

public static class ClipboardCodec
{
    /// <summary>Dati degli appunti più grandi accettati dall'ospite o inviati (32 MiB).</summary>
    public const int MaxDataSize = 32 * 1024 * 1024;

    /// <summary>Tipi annunciati al massimo in un GRAB.</summary>
    private const int MaxTypes = 64;

    /// <summary>Decodifica un messaggio degli appunti; <see langword="null"/> per altri tipi o selezioni ignote.</summary>
    public static ClipboardMessage? Decode(AgentMessage message, bool selectionHeader)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.KnownType is not { } kind || kind is not (AgentMessageType.ClipboardGrab or AgentMessageType.ClipboardRequest
            or AgentMessageType.Clipboard or AgentMessageType.ClipboardRelease))
        {
            return null;
        }
        var r = new ByteReader(message.Data);
        var selection = ClipboardSelection.Clipboard;
        if (selectionHeader)
        {
            var raw = r.ReadU8();
            if (raw > (byte)ClipboardSelection.Secondary) return null;
            selection = (ClipboardSelection)raw;
            r.Skip(3);
        }
        switch (kind)
        {
            case AgentMessageType.ClipboardGrab:
                var types = new List<ClipboardDataType>();
                while (r.Remaining >= 4 && types.Count < MaxTypes)
                {
                    var type = r.ReadU32();
                    if (type is > 0 and <= (uint)ClipboardDataType.FileList) types.Add((ClipboardDataType)type);
                }
                return new ClipboardMessage.Grab(selection, types);
            case AgentMessageType.ClipboardRequest:
                var requested = r.ReadU32();
                return requested <= (uint)ClipboardDataType.FileList ? new ClipboardMessage.Request(selection, (ClipboardDataType)requested) : null;
            case AgentMessageType.Clipboard:
                var dataType = r.ReadU32();
                if (dataType > (uint)ClipboardDataType.FileList) return null;
                if (r.Remaining > MaxDataSize) throw WireException.LimitExceeded($"appunti di {r.Remaining} byte");
                return new ClipboardMessage.Data(selection, (ClipboardDataType)dataType, r.ReadRemaining().ToArray());
            default:
                return new ClipboardMessage.Release(selection);
        }
    }

    public static AgentMessage Grab(IReadOnlyList<ClipboardDataType> types, bool selectionHeader)
    {
        ArgumentNullException.ThrowIfNull(types);
        var w = Header(selectionHeader);
        foreach (var type in types) w.WriteU32((uint)type);
        return new AgentMessage(AgentMessageType.ClipboardGrab, w.ToArray());
    }

    public static AgentMessage Request(ClipboardDataType type, bool selectionHeader)
    {
        var w = Header(selectionHeader);
        w.WriteU32((uint)type);
        return new AgentMessage(AgentMessageType.ClipboardRequest, w.ToArray());
    }

    /// <summary>Dati per l'ospite; oltre <see cref="MaxDataSize"/> si invia vuoto (mai un'immagine troncata).</summary>
    public static AgentMessage Data(ClipboardDataType type, ReadOnlySpan<byte> bytes, bool selectionHeader)
    {
        var w = Header(selectionHeader);
        w.WriteU32((uint)type);
        if (bytes.Length <= MaxDataSize) w.WriteBytes(bytes);
        return new AgentMessage(AgentMessageType.Clipboard, w.ToArray());
    }

    public static AgentMessage Release(bool selectionHeader) =>
        new(AgentMessageType.ClipboardRelease, Header(selectionHeader).ToArray());

    private static ByteWriter Header(bool present)
    {
        var w = new ByteWriter();
        if (present) w.WriteBytes([(byte)ClipboardSelection.Clipboard, 0, 0, 0]);
        return w;
    }

    /// <summary>Testo di Windows → ospite: a capo CRLF solo per gli ospiti che lo chiedono (Windows), altrimenti LF.</summary>
    public static byte[] TextToGuest(string text, bool guestUsesCrlf)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(guestUsesCrlf ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : normalized);
    }

    /// <summary>Testo dell'ospite → Windows: senza NUL finali, a capo CRLF; l'UTF-8 non valido si ripara.</summary>
    public static string TextFromGuest(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty && bytes[^1] == 0) bytes = bytes[..^1];
        return Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }
}

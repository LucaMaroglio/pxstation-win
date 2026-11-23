// Trasferimento di file verso l'agente (vd_agent.h, licenza BSD): FILE_XFER_START (id + testo
// in formato key-file), FILE_XFER_STATUS (id, risultato[, dettaglio]), FILE_XFER_DATA (id, u64, byte).

using System.Globalization;
using System.Text;
using SpiceKit.Wire;

namespace SpiceKit.Agent;

/// <summary><c>VD_AGENT_FILE_XFER_STATUS_*</c>.</summary>
public enum FileTransferResult : uint
{
    CanSendData = 0,
    Cancelled,
    Error,
    Success,
    NotEnoughSpace,
    SessionLocked,
    AgentNotConnected,
    Disabled,
}

public static class FileTransferCodec
{
    /// <summary>Byte per messaggio FILE_XFER_DATA.</summary>
    public const int ChunkSize = 32 * 1024;

    /// <summary>START: key-file <c>[vdagent-file-xfer]</c> con nome e misura, terminato da NUL.</summary>
    public static AgentMessage Start(uint id, string name, ulong size)
    {
        var w = new ByteWriter();
        w.WriteU32(id);
        w.WriteBytes(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"[vdagent-file-xfer]\nname={EscapedName(name)}\nsize={size}\n")));
        w.WriteU8(0);
        return new AgentMessage(AgentMessageType.FileXferStart, w.ToArray());
    }

    public static AgentMessage Data(uint id, ReadOnlySpan<byte> bytes)
    {
        var w = new ByteWriter(12 + bytes.Length);
        w.WriteU32(id);
        w.WriteU64((ulong)bytes.Length);
        w.WriteBytes(bytes);
        return new AgentMessage(AgentMessageType.FileXferData, w.ToArray());
    }

    public static AgentMessage Status(uint id, FileTransferResult result)
    {
        var w = new ByteWriter(8);
        w.WriteU32(id);
        w.WriteU32((uint)result);
        return new AgentMessage(AgentMessageType.FileXferStatus, w.ToArray());
    }

    /// <summary>(id, risultato) di un FILE_XFER_STATUS dell'ospite; risultati ignoti valgono errore.</summary>
    public static (uint Id, FileTransferResult Result)? DecodeStatus(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.KnownType != AgentMessageType.FileXferStatus || message.Data.Length < 8) return null;
        var r = new ByteReader(message.Data);
        var id = r.ReadU32();
        var raw = r.ReadU32();
        return (id, raw <= (uint)FileTransferResult.Disabled ? (FileTransferResult)raw : FileTransferResult.Error);
    }

    /// <summary>
    /// Nome per il key-file: separatori e caratteri di controllo diventano <c>_</c>, così l'ospite
    /// può creare solo un file semplice nella sua cartella dei download.
    /// </summary>
    public static string EscapedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            builder.Append(Rune.IsControl(rune) || rune.Value is '/' or '\\' ? "_" : rune.ToString());
        }
        var cleaned = builder.ToString();
        if (cleaned.StartsWith('.')) cleaned = "_" + cleaned[1..];
        if (cleaned.Length == 0) cleaned = "file";
        if (cleaned.StartsWith(' ')) cleaned = "\\s" + cleaned[1..];
        return cleaned;
    }
}

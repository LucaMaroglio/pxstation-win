using System.Text;

namespace SpiceKit.Wire;

// Messaggi comuni a tutti i canali (spice.proto, "channel BaseChannel").

/// <summary><c>SPICE_MSG_SET_ACK</c>.</summary>
public readonly record struct SetAck(uint Generation, uint Window)
{
    public static SetAck Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        return new SetAck(reader.ReadU32(), reader.ReadU32());
    }
}

/// <summary><c>SPICE_MSG_PING</c>. I dati in coda servono solo a misurare la banda e si ignorano.</summary>
public readonly record struct Ping(uint Id, ulong Timestamp)
{
    public static Ping Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        return new Ping(reader.ReadU32(), reader.ReadU64());
    }
}

/// <summary><c>SPICE_MSG_NOTIFY</c>.</summary>
public sealed record Notify(ulong Timestamp, uint Severity, uint Visibility, uint What, string Message)
{
    public static Notify Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        var timestamp = reader.ReadU64();
        var severity = reader.ReadU32();
        var visibility = reader.ReadU32();
        var what = reader.ReadU32();
        var length = reader.ReadU32();
        var text = reader.ReadBytes((int)Math.Min(length, (uint)reader.Remaining));
        return new Notify(timestamp, severity, visibility, what, WireText.Decode(text));
    }
}

/// <summary><c>SPICE_MSG_DISCONNECTING</c>.</summary>
public readonly record struct Disconnecting(ulong Timestamp, uint Reason)
{
    public static Disconnecting Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        return new Disconnecting(reader.ReadU64(), reader.ReadU32());
    }
}

/// <summary>Messaggi base dal client al server.</summary>
public static class BaseClientEncoder
{
    public static SpiceMessage AckSync(uint generation)
    {
        var writer = new ByteWriter(4);
        writer.WriteU32(generation);
        return new SpiceMessage((ushort)BaseClientMessage.AckSync, writer.ToArray());
    }

    public static SpiceMessage Ack() => SpiceMessage.Empty((ushort)BaseClientMessage.Ack);

    public static SpiceMessage Pong(uint id, ulong timestamp)
    {
        var writer = new ByteWriter(12);
        writer.WriteU32(id);
        writer.WriteU64(timestamp);
        return new SpiceMessage((ushort)BaseClientMessage.Pong, writer.ToArray());
    }
}

/// <summary>Testo inviato dal server: UTF-8 non verificato, eventuali NUL finali tolti.</summary>
public static class WireText
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty && bytes[^1] == 0) bytes = bytes[..^1];
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Versione adatta ai log: testo del server non attendibile, quindi senza caratteri di
    /// controllo e troncato (AGENTS.md §6).
    /// </summary>
    public static string Sanitize(string text, int maxLength = 200)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var c in text)
        {
            if (builder.Length >= maxLength) break;
            builder.Append(char.IsControl(c) ? ' ' : c);
        }
        return builder.ToString();
    }
}

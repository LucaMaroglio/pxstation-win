// Canale cursor (spice.proto, channel CursorChannel).

namespace SpiceKit.Wire;

/// <summary><c>SpiceCursorType</c> (enums.h).</summary>
public enum CursorType : byte
{
    Alpha = 0,
    Mono,
    Color4,
    Color8,
    Color16,
    Color24,
    Color32,
}

/// <summary><c>SpiceCursorFlags</c> (enums.h).</summary>
[Flags]
public enum CursorOptions : ushort
{
    None = 0,

    /// <summary>Nessuna forma inclusa (cursore nascosto).</summary>
    NoShape = 1 << 0,
    CacheMe = 1 << 1,
    FromCache = 1 << 2,
}

/// <summary><c>SpiceCursorHeader</c>.</summary>
public readonly record struct CursorHeader(ulong Unique, byte RawType, ushort Width, ushort Height, ushort HotSpotX, ushort HotSpotY)
{
    public CursorType? Type => RawType <= (byte)CursorType.Color32 ? (CursorType)RawType : null;
}

/// <summary><c>SpiceCursor</c>: flag, intestazione facoltativa e dati della forma fino alla fine del messaggio.</summary>
public sealed record CursorShape(CursorOptions Options, CursorHeader? Header, ReadOnlyMemory<byte> Data)
{
    internal static CursorShape Decode(ReadOnlyMemory<byte> message, ref ByteReader reader)
    {
        var options = (CursorOptions)reader.ReadU16();
        CursorHeader? header = null;
        if (!options.HasFlag(CursorOptions.NoShape))
        {
            header = new CursorHeader(reader.ReadU64(), reader.ReadU8(), reader.ReadU16(), reader.ReadU16(),
                reader.ReadU16(), reader.ReadU16());
        }
        return new CursorShape(options, header, message[reader.Offset..]);
    }
}

/// <summary><c>SPICE_MSG_CURSOR_INIT</c>.</summary>
public sealed record CursorInit(short X, short Y, ushort TrailLength, ushort TrailFrequency, bool Visible, CursorShape Cursor)
{
    public static CursorInit Decode(ReadOnlyMemory<byte> payload)
    {
        var reader = new ByteReader(payload.Span);
        var x = reader.ReadI16();
        var y = reader.ReadI16();
        var length = reader.ReadU16();
        var frequency = reader.ReadU16();
        var visible = reader.ReadU8() != 0;
        return new CursorInit(x, y, length, frequency, visible, CursorShape.Decode(payload, ref reader));
    }
}

/// <summary><c>SPICE_MSG_CURSOR_SET</c>.</summary>
public sealed record CursorSet(short X, short Y, bool Visible, CursorShape Cursor)
{
    public static CursorSet Decode(ReadOnlyMemory<byte> payload)
    {
        var reader = new ByteReader(payload.Span);
        var x = reader.ReadI16();
        var y = reader.ReadI16();
        var visible = reader.ReadU8() != 0;
        return new CursorSet(x, y, visible, CursorShape.Decode(payload, ref reader));
    }
}

/// <summary><c>SPICE_MSG_CURSOR_MOVE</c> e <c>SPICE_MSG_CURSOR_INVAL_ONE</c>.</summary>
public static class CursorDecoders
{
    public static (short X, short Y) Move(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        return (reader.ReadI16(), reader.ReadI16());
    }

    public static ulong InvalidateOne(ReadOnlySpan<byte> bytes) => new ByteReader(bytes).ReadU64();
}

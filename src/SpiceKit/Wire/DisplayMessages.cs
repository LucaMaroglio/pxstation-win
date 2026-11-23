// Corpi dei messaggi del canale display (spice.proto "channel DisplayChannel").
// I campi puntatore (Image *, Palette *) sono offset uint32 dall'inizio del messaggio.

namespace SpiceKit.Wire;

/// <summary><c>SpiceRopd</c> (enums.h): descrittore dell'operazione raster.</summary>
[Flags]
public enum Ropd : ushort
{
    None = 0,
    InversSource = 1 << 0,
    InversBrush = 1 << 1,
    InversDest = 1 << 2,
    OpPut = 1 << 3,
    OpOr = 1 << 4,
    OpAnd = 1 << 5,
    OpXor = 1 << 6,
    OpBlackness = 1 << 7,
    OpWhiteness = 1 << 8,
    OpInvers = 1 << 9,
    InversResult = 1 << 10,
}

/// <summary><c>SpiceImageScaleMode</c> (enums.h).</summary>
public enum ScaleMode : byte
{
    Interpolate = 0,
    Nearest = 1,
}

/// <summary><c>SpiceSurfaceFmt</c> (enums.h).</summary>
public enum SurfaceFormat : uint
{
    Invalid = 0,
    A1 = 1,
    A8 = 8,
    X555 = 16,
    Xrgb32 = 32,
    R565 = 80,
    Argb32 = 96,
}

/// <summary><c>SpiceImageCompression</c> (enums.h).</summary>
public enum ImageCompression : byte
{
    Invalid = 0,
    Off,
    AutoGlz,
    AutoLz,
    Quic,
    Glz,
    Lz,
    Lz4,
}

/// <summary><c>SpiceVideoCodecType</c> (enums.h).</summary>
public enum VideoCodecType : byte
{
    Mjpeg = 1,
    Vp8,
    H264,
    Vp9,
    H265,
}

/// <summary><c>SpiceBrush</c>.</summary>
public abstract record Brush
{
    public sealed record None : Brush;

    /// <summary>Colore xRGB.</summary>
    public sealed record Solid(uint Color) : Brush;

    public sealed record Pattern(SpiceImage? Image, SpicePoint Position) : Brush;

    internal static Brush Decode(ReadOnlyMemory<byte> message, ref ByteReader r)
    {
        var type = r.ReadU8();
        switch (type)
        {
            case 0:
                return new None();
            case 1:
                return new Solid(r.ReadU32());
            case 2:
                var pointer = r.ReadU32();
                var position = SpicePoint.Decode(ref r);
                return new Pattern(SpiceImage.DecodeAt(pointer, message, r), position);
            default:
                throw WireException.InvalidValue($"tipo di pennello {type}");
        }
    }
}

/// <summary><c>SpiceQMask</c>: maschera facoltativa a 1 bit per pixel.</summary>
public sealed record QMask(bool Inverted, SpicePoint Position, SpiceImage? Bitmap)
{
    internal static QMask Decode(ReadOnlyMemory<byte> message, ref ByteReader r)
    {
        var flags = r.ReadU8();
        var position = SpicePoint.Decode(ref r);
        var pointer = r.ReadU32();
        return new QMask((flags & 1) != 0, position, SpiceImage.DecodeAt(pointer, message, r));
    }
}

/// <summary><c>SpiceMsgDisplayBase</c>.</summary>
public sealed record DisplayBase(uint SurfaceId, SpiceRect Box, SpiceClip Clip)
{
    internal static DisplayBase Decode(ref ByteReader r) =>
        new(r.ReadU32(), SpiceRect.Decode(ref r), SpiceClip.Decode(ref r));
}

/// <summary><c>SPICE_MSG_DISPLAY_SURFACE_CREATE</c>.</summary>
public sealed record SurfaceCreate(uint SurfaceId, uint Width, uint Height, uint RawFormat, uint Flags)
{
    /// <summary><c>SPICE_SURFACE_FLAGS_PRIMARY</c> = 1.</summary>
    public bool IsPrimary => (Flags & 1) != 0;

    public SurfaceFormat Format => (SurfaceFormat)RawFormat;

    public static SurfaceCreate Decode(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        return new SurfaceCreate(r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32());
    }
}

/// <summary><c>SPICE_MSG_DISPLAY_DRAW_FILL</c>.</summary>
public sealed record DrawFill(DisplayBase Base, Brush Brush, Ropd Rop, QMask Mask)
{
    public static DrawFill Decode(ReadOnlyMemory<byte> payload)
    {
        var r = new ByteReader(payload.Span);
        var displayBase = DisplayBase.Decode(ref r);
        var brush = Brush.Decode(payload, ref r);
        var rop = (Ropd)r.ReadU16();
        return new DrawFill(displayBase, brush, rop, QMask.Decode(payload, ref r));
    }
}

/// <summary>
/// <c>SPICE_MSG_DISPLAY_DRAW_COPY</c>, <c>DRAW_BLEND</c> (stessa struttura) e <c>DRAW_OPAQUE</c>
/// (che ha in più un pennello, letto e ignorato come su macOS).
/// </summary>
public sealed record DrawCopy(DisplayBase Base, SpiceImage? Source, SpiceRect SourceArea, Ropd Rop, ScaleMode ScaleMode, QMask Mask)
{
    public static DrawCopy Decode(ReadOnlyMemory<byte> payload, bool withBrush = false)
    {
        var r = new ByteReader(payload.Span);
        var displayBase = DisplayBase.Decode(ref r);
        var pointer = r.ReadU32();
        var source = SpiceImage.DecodeAt(pointer, payload, r);
        var area = SpiceRect.Decode(ref r);
        if (withBrush) _ = Brush.Decode(payload, ref r);
        var rop = (Ropd)r.ReadU16();
        var scale = r.ReadU8() == 0 ? ScaleMode.Interpolate : ScaleMode.Nearest;
        return new DrawCopy(displayBase, source, area, rop, scale, QMask.Decode(payload, ref r));
    }
}

/// <summary><c>SPICE_MSG_DISPLAY_DRAW_BLACKNESS</c> / <c>WHITENESS</c> / <c>INVERS</c>.</summary>
public sealed record DrawMaskOnly(DisplayBase Base, QMask Mask)
{
    public static DrawMaskOnly Decode(ReadOnlyMemory<byte> payload)
    {
        var r = new ByteReader(payload.Span);
        var displayBase = DisplayBase.Decode(ref r);
        return new DrawMaskOnly(displayBase, QMask.Decode(payload, ref r));
    }
}

/// <summary><c>SPICE_MSG_DISPLAY_DRAW_TRANSPARENT</c>: i pixel uguali a <c>SourceColor</c> non si disegnano.</summary>
public sealed record DrawTransparent(DisplayBase Base, SpiceImage? Source, SpiceRect SourceArea, uint SourceColor, uint TrueColor)
{
    public static DrawTransparent Decode(ReadOnlyMemory<byte> payload)
    {
        var r = new ByteReader(payload.Span);
        var displayBase = DisplayBase.Decode(ref r);
        var pointer = r.ReadU32();
        var source = SpiceImage.DecodeAt(pointer, payload, r);
        return new DrawTransparent(displayBase, source, SpiceRect.Decode(ref r), r.ReadU32(), r.ReadU32());
    }
}

/// <summary><c>SPICE_MSG_DISPLAY_DRAW_ALPHA_BLEND</c>.</summary>
public sealed record DrawAlphaBlend(DisplayBase Base, byte AlphaFlags, byte Alpha, SpiceImage? Source, SpiceRect SourceArea)
{
    /// <summary><c>SPICE_ALPHA_FLAGS_SRC_SURFACE_HAS_ALPHA</c> = 2.</summary>
    public bool SourceHasAlpha => (AlphaFlags & 2) != 0;

    public static DrawAlphaBlend Decode(ReadOnlyMemory<byte> payload)
    {
        var r = new ByteReader(payload.Span);
        var displayBase = DisplayBase.Decode(ref r);
        var flags = r.ReadU8();
        var alpha = r.ReadU8();
        var pointer = r.ReadU32();
        var source = SpiceImage.DecodeAt(pointer, payload, r);
        return new DrawAlphaBlend(displayBase, flags, alpha, source, SpiceRect.Decode(ref r));
    }
}

/// <summary><c>SPICE_MSG_DISPLAY_COPY_BITS</c>: copia all'interno della stessa superficie.</summary>
public sealed record CopyBits(DisplayBase Base, SpicePoint SourcePosition)
{
    public static CopyBits Decode(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        var displayBase = DisplayBase.Decode(ref r);
        return new CopyBits(displayBase, SpicePoint.Decode(ref r));
    }
}

/// <summary><c>SpiceHead</c> in <c>SPICE_MSG_DISPLAY_MONITORS_CONFIG</c>.</summary>
public readonly record struct MonitorHead(uint MonitorId, uint SurfaceId, uint Width, uint Height, uint X, uint Y, uint Flags);

/// <summary>Messaggi brevi del canale display.</summary>
public static class DisplayDecoders
{
    /// <summary>Teste annunciate al massimo (il protocollo ne prevede poche: limite contro input ostili).</summary>
    internal const int MaxHeads = 64;

    /// <summary>Id singolo: <c>SURFACE_DESTROY</c>, <c>STREAM_DESTROY</c>, <c>INVAL_PALETTE</c> (u64) ecc.</summary>
    public static uint UInt32(ReadOnlySpan<byte> payload) => new ByteReader(payload).ReadU32();

    public static ulong UInt64(ReadOnlySpan<byte> payload) => new ByteReader(payload).ReadU64();

    /// <summary><c>SPICE_MSG_DISPLAY_INVAL_LIST</c>: id delle risorse da dimenticare.</summary>
    public static ulong[] InvalidateList(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        var count = r.ReadU16();
        if (count * 9 > r.Remaining) throw WireException.Truncated(count * 9, r.Remaining);
        var ids = new ulong[count];
        for (var i = 0; i < ids.Length; i++)
        {
            _ = r.ReadU8(); // tipo di risorsa (sempre pixmap)
            ids[i] = r.ReadU64();
        }
        return ids;
    }

    /// <summary><c>SPICE_MSG_DISPLAY_MONITORS_CONFIG</c>.</summary>
    public static MonitorHead[] MonitorsConfig(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        var count = r.ReadU16();
        _ = r.ReadU16(); // max_allowed
        if (count > MaxHeads) throw WireException.LimitExceeded($"monitor: {count}");
        var heads = new MonitorHead[count];
        for (var i = 0; i < heads.Length; i++)
        {
            heads[i] = new MonitorHead(r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32());
        }
        return heads;
    }
}

/// <summary>Messaggi client → server del canale display.</summary>
public static class DisplayClientEncoder
{
    /// <summary><c>SPICE_MSGC_DISPLAY_INIT</c>.</summary>
    public static SpiceMessage Initialize(byte pixmapCacheId, long pixmapCacheSize, byte glzDictionaryId, int glzWindowSize)
    {
        var w = new ByteWriter(14);
        w.WriteU8(pixmapCacheId);
        w.WriteI64(pixmapCacheSize);
        w.WriteU8(glzDictionaryId);
        w.WriteI32(glzWindowSize);
        return new SpiceMessage((ushort)DisplayClientMessage.Init, w.ToArray());
    }

    /// <summary><c>SPICE_MSGC_DISPLAY_PREFERRED_COMPRESSION</c>.</summary>
    public static SpiceMessage PreferredCompression(ImageCompression compression) =>
        new((ushort)DisplayClientMessage.PreferredCompression, [(byte)compression]);
}

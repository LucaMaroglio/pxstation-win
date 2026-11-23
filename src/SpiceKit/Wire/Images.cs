namespace SpiceKit.Wire;

/// <summary><c>SpiceImageType</c> (enums.h).</summary>
public enum ImageType : byte
{
    Bitmap = 0,
    Quic = 1,
    Reserved = 2,
    LzPlt = 100,
    LzRgb,
    GlzRgb,
    FromCache,
    Surface,
    Jpeg,
    FromCacheLossless,
    ZlibGlzRgb,
    JpegAlpha,
    Lz4,
}

/// <summary><c>SpiceImageFlags</c> (enums.h).</summary>
[Flags]
public enum ImageOptions : byte
{
    None = 0,
    CacheMe = 1 << 0,
    HighBitsSet = 1 << 1,
    CacheReplaceMe = 1 << 2,
}

/// <summary><c>SpiceBitmapFmt</c> (enums.h).</summary>
public enum BitmapFormat : byte
{
    Invalid = 0,
    OneBitLE,
    OneBitBE,
    FourBitLE,
    FourBitBE,
    EightBit,

    /// <summary>x555.</summary>
    SixteenBit,

    /// <summary>BGR.</summary>
    TwentyFourBit,

    /// <summary>xRGB little-endian (BGRx in memoria).</summary>
    ThirtyTwoBit,

    /// <summary>ARGB little-endian (BGRA in memoria).</summary>
    Rgba,
    EightBitAlpha,
}

/// <summary><c>SpiceBitmapFlags</c> (enums.h).</summary>
[Flags]
public enum BitmapOptions : byte
{
    None = 0,
    PaletteCacheMe = 1 << 0,
    PaletteFromCache = 1 << 1,
    TopDown = 1 << 2,
}

public static class BitmapFormats
{
    public static bool IsPaletted(this BitmapFormat format) =>
        format is BitmapFormat.OneBitLE or BitmapFormat.OneBitBE or BitmapFormat.FourBitLE
            or BitmapFormat.FourBitBE or BitmapFormat.EightBit;
}

/// <summary><c>SpicePalette</c>: colori xRGB (0x00RRGGBB).</summary>
public sealed record Palette(ulong Unique, uint[] Entries)
{
    internal static Palette Decode(ref ByteReader reader)
    {
        var unique = reader.ReadU64();
        var count = reader.ReadU16();
        if (count * 4 > reader.Remaining) throw WireException.Truncated(count * 4, reader.Remaining);
        var entries = new uint[count];
        for (var i = 0; i < entries.Length; i++) entries[i] = reader.ReadU32();
        return new Palette(unique, entries);
    }
}

/// <summary>Da dove un'immagine con palette prende la palette.</summary>
public abstract record PaletteReference
{
    public sealed record None : PaletteReference;

    public sealed record Inline(Palette Palette) : PaletteReference;

    public sealed record Cached(ulong Id) : PaletteReference;

    public static PaletteReference Absent { get; } = new None();
}

/// <summary><c>SpiceBitmap</c>: immagine non compressa (<c>Data</c> = <c>Stride × Height</c> byte).</summary>
public sealed record BitmapData(BitmapFormat Format, BitmapOptions Flags, uint Width, uint Height, uint Stride,
    PaletteReference Palette, ReadOnlyMemory<byte> Data);

/// <summary><c>SpiceImageDescriptor</c>.</summary>
public readonly record struct ImageDescriptor(ulong Id, byte RawType, ImageOptions Flags, uint Width, uint Height)
{
    public ImageType Type => (ImageType)RawType;
}

/// <summary>Contenuto di un'immagine secondo il tipo.</summary>
public abstract record ImagePayload
{
    public sealed record Bitmap(BitmapData Data) : ImagePayload;

    public sealed record Quic(ReadOnlyMemory<byte> Data) : ImagePayload;

    public sealed record LzRgb(ReadOnlyMemory<byte> Data) : ImagePayload;

    public sealed record GlzRgb(ReadOnlyMemory<byte> Data) : ImagePayload;

    public sealed record Jpeg(ReadOnlyMemory<byte> Data) : ImagePayload;

    public sealed record Lz4(ReadOnlyMemory<byte> Data) : ImagePayload;

    public sealed record LzPlt(BitmapOptions Flags, PaletteReference Palette, ReadOnlyMemory<byte> Data) : ImagePayload;

    public sealed record ZlibGlzRgb(uint GlzDataSize, ReadOnlyMemory<byte> Data) : ImagePayload;

    public sealed record JpegAlpha(bool TopDown, uint JpegSize, ReadOnlyMemory<byte> Data) : ImagePayload;

    public sealed record Surface(uint Id) : ImagePayload;

    /// <summary><c>FROM_CACHE</c> / <c>FROM_CACHE_LOSSLESS</c>: si cerca <c>Descriptor.Id</c>.</summary>
    public sealed record FromCache(bool Lossless) : ImagePayload;
}

/// <summary><c>SpiceImage</c>: descrittore + contenuto.</summary>
public sealed record SpiceImage(ImageDescriptor Descriptor, ImagePayload Payload)
{
    /// <summary>Larghezza e altezza massime di un'immagine (come il limite delle superfici).</summary>
    public const uint MaxDimension = 16_384;

    /// <summary>L'immagine a cui punta un campo puntatore; <see langword="null"/> se il puntatore è nullo.</summary>
    internal static SpiceImage? DecodeAt(uint pointer, ReadOnlyMemory<byte> message, in ByteReader reader)
    {
        if (pointer == 0) return null;
        var r = reader.AtPointer(pointer);
        return Decode(message, ref r);
    }

    internal static SpiceImage Decode(ReadOnlyMemory<byte> message, ref ByteReader r)
    {
        var descriptor = new ImageDescriptor(r.ReadU64(), r.ReadU8(), (ImageOptions)r.ReadU8(), r.ReadU32(), r.ReadU32());
        if (descriptor.Width > MaxDimension || descriptor.Height > MaxDimension)
        {
            throw WireException.LimitExceeded($"immagine {descriptor.Width}x{descriptor.Height}");
        }

        ImagePayload payload;
        switch (descriptor.Type)
        {
            case ImageType.Bitmap:
                var format = r.ReadU8();
                var flags = (BitmapOptions)r.ReadU8();
                var width = r.ReadU32();
                var height = r.ReadU32();
                var stride = r.ReadU32();
                var palette = DecodePaletteReference(flags, ref r);
                if (format is 0 or > (byte)BitmapFormat.EightBitAlpha) throw WireException.InvalidValue($"formato bitmap {format}");
                if (width > MaxDimension || height > MaxDimension) throw WireException.LimitExceeded($"bitmap {width}x{height}");
                var size = (ulong)stride * height;
                if (size > (ulong)r.Remaining) throw WireException.Truncated((int)Math.Min(size, int.MaxValue), r.Remaining);
                payload = new ImagePayload.Bitmap(new BitmapData((BitmapFormat)format, flags, width, height, stride, palette,
                    Slice(message, ref r, (int)size)));
                break;
            case ImageType.Quic:
                payload = new ImagePayload.Quic(Binary(message, ref r));
                break;
            case ImageType.LzRgb:
                payload = new ImagePayload.LzRgb(Binary(message, ref r));
                break;
            case ImageType.GlzRgb:
                payload = new ImagePayload.GlzRgb(Binary(message, ref r));
                break;
            case ImageType.Jpeg:
                payload = new ImagePayload.Jpeg(Binary(message, ref r));
                break;
            case ImageType.Lz4:
                payload = new ImagePayload.Lz4(Binary(message, ref r));
                break;
            case ImageType.LzPlt:
                var lzFlags = (BitmapOptions)r.ReadU8();
                var lzSize = r.ReadU32();
                var lzPalette = DecodePaletteReference(lzFlags, ref r);
                payload = new ImagePayload.LzPlt(lzFlags, lzPalette, Slice(message, ref r, ToLength(lzSize)));
                break;
            case ImageType.ZlibGlzRgb:
                var glzSize = r.ReadU32();
                var zlibSize = r.ReadU32();
                payload = new ImagePayload.ZlibGlzRgb(glzSize, Slice(message, ref r, ToLength(zlibSize)));
                break;
            case ImageType.JpegAlpha:
                var alphaFlags = r.ReadU8();
                var jpegSize = r.ReadU32();
                var totalSize = r.ReadU32();
                if (jpegSize > totalSize) throw WireException.InvalidValue("dimensione jpeg alpha");
                payload = new ImagePayload.JpegAlpha((alphaFlags & 1) != 0, jpegSize, Slice(message, ref r, ToLength(totalSize)));
                break;
            case ImageType.Surface:
                payload = new ImagePayload.Surface(r.ReadU32());
                break;
            case ImageType.FromCache:
                payload = new ImagePayload.FromCache(false);
                break;
            case ImageType.FromCacheLossless:
                payload = new ImagePayload.FromCache(true);
                break;
            default:
                throw WireException.InvalidValue($"tipo di immagine {descriptor.RawType}");
        }
        return new SpiceImage(descriptor, payload);
    }

    private static PaletteReference DecodePaletteReference(BitmapOptions flags, ref ByteReader r)
    {
        if (flags.HasFlag(BitmapOptions.PaletteFromCache)) return new PaletteReference.Cached(r.ReadU64());
        var pointer = r.ReadU32();
        if (pointer == 0) return PaletteReference.Absent;
        var paletteReader = r.AtPointer(pointer);
        return new PaletteReference.Inline(Palette.Decode(ref paletteReader));
    }

    private static ReadOnlyMemory<byte> Binary(ReadOnlyMemory<byte> message, ref ByteReader r) =>
        Slice(message, ref r, ToLength(r.ReadU32()));

    /// <summary>Le lunghezze oltre int32 sono comunque più lunghe di qualsiasi messaggio accettato.</summary>
    private static int ToLength(uint size) => size > int.MaxValue ? int.MaxValue : (int)size;

    internal static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> message, ref ByteReader r, int count)
    {
        var start = r.Offset;
        r.Skip(count);
        return message.Slice(start, count);
    }
}

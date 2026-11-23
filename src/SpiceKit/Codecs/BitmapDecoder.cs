using SpiceKit.Wire;

namespace SpiceKit.Codecs;

/// <summary>Maschera a 1 bit per pixel decodificata (dall'alto in basso).</summary>
public sealed record MaskBits(int Width, int Height, bool[] Bits);

/// <summary>Decodifica i <c>SpiceBitmap</c> non compressi (tutti i <c>SPICE_BITMAP_FMT_*</c>) in BGRA.</summary>
public static class BitmapDecoder
{
    /// <param name="bitmap">Bitmap ricevuta.</param>
    /// <param name="palette">Palette già risolta per i formati con palette (altrimenti <see langword="null"/>).</param>
    public static PixelBuffer Decode(BitmapData bitmap, Palette? palette)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int width = (int)bitmap.Width, height = (int)bitmap.Height;
        CodecLimits.Check(width, height);
        var stride = (long)bitmap.Stride;
        if (stride < MinimumStride(bitmap.Format, width))
        {
            throw CodecException.Corrupt($"stride {stride} troppo piccolo per la larghezza {width}");
        }
        if (bitmap.Data.Length < stride * height) throw CodecException.Truncated();
        if (bitmap.Format.IsPaletted() && palette is null) throw CodecException.MissingPalette();

        var entries = palette?.Entries ?? [];
        var output = new PixelBuffer(width, height, hasAlpha: bitmap.Format is BitmapFormat.Rgba or BitmapFormat.EightBitAlpha);
        var topDown = bitmap.Flags.HasFlag(BitmapOptions.TopDown);
        var data = bitmap.Data.Span;
        var pixels = output.Pixels.AsSpan();

        for (var row = 0; row < height; row++)
        {
            var src = data.Slice((int)(row * stride), (int)stride);
            var dst = pixels.Slice((topDown ? row : height - 1 - row) * width, width);
            switch (bitmap.Format)
            {
                case BitmapFormat.ThirtyTwoBit:
                    for (var x = 0; x < width; x++) dst[x] = PixelBuffer.Opaque(src[(x * 4) + 2], src[(x * 4) + 1], src[x * 4]);
                    break;
                case BitmapFormat.Rgba:
                    for (var x = 0; x < width; x++)
                    {
                        var p = x * 4;
                        dst[x] = src[p] | ((uint)src[p + 1] << 8) | ((uint)src[p + 2] << 16) | ((uint)src[p + 3] << 24);
                    }
                    break;
                case BitmapFormat.TwentyFourBit:
                    for (var x = 0; x < width; x++) dst[x] = PixelBuffer.Opaque(src[(x * 3) + 2], src[(x * 3) + 1], src[x * 3]);
                    break;
                case BitmapFormat.SixteenBit:
                    for (var x = 0; x < width; x++) dst[x] = PixelBuffer.From555(src[x * 2] | ((uint)src[(x * 2) + 1] << 8));
                    break;
                case BitmapFormat.EightBit:
                    for (var x = 0; x < width; x++) dst[x] = Lookup(entries, src[x]);
                    break;
                case BitmapFormat.FourBitBE:
                    for (var x = 0; x < width; x++)
                    {
                        var value = src[x / 2];
                        dst[x] = Lookup(entries, (x & 1) == 0 ? value >> 4 : value & 0x0F);
                    }
                    break;
                case BitmapFormat.FourBitLE:
                    for (var x = 0; x < width; x++)
                    {
                        var value = src[x / 2];
                        dst[x] = Lookup(entries, (x & 1) == 0 ? value & 0x0F : value >> 4);
                    }
                    break;
                case BitmapFormat.OneBitBE:
                    for (var x = 0; x < width; x++) dst[x] = Lookup(entries, (src[x / 8] >> (7 - (x & 7))) & 1);
                    break;
                case BitmapFormat.OneBitLE:
                    for (var x = 0; x < width; x++) dst[x] = Lookup(entries, (src[x / 8] >> (x & 7)) & 1);
                    break;
                case BitmapFormat.EightBitAlpha:
                    // A8: solo alfa; il colore premoltiplicato è nero.
                    for (var x = 0; x < width; x++) dst[x] = (uint)src[x] << 24;
                    break;
            }
        }
        return output;
    }

    private static uint Lookup(uint[] entries, int index) =>
        index < entries.Length ? 0xFF00_0000 | entries[index] : 0xFF00_0000;

    internal static long MinimumStride(BitmapFormat format, long width) => format switch
    {
        BitmapFormat.OneBitLE or BitmapFormat.OneBitBE => (width + 7) / 8,
        BitmapFormat.FourBitLE or BitmapFormat.FourBitBE => (width + 1) / 2,
        BitmapFormat.EightBit or BitmapFormat.EightBitAlpha => width,
        BitmapFormat.SixteenBit => width * 2,
        BitmapFormat.TwentyFourBit => width * 3,
        BitmapFormat.ThirtyTwoBit or BitmapFormat.Rgba => width * 4,
        _ => long.MaxValue,
    };

    /// <summary>Legge una maschera a 1 bit per pixel (un booleano per pixel, dall'alto in basso).</summary>
    public static MaskBits DecodeMask(BitmapData bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int width = (int)bitmap.Width, height = (int)bitmap.Height;
        CodecLimits.Check(width, height);
        if (bitmap.Format is not (BitmapFormat.OneBitBE or BitmapFormat.OneBitLE))
        {
            throw CodecException.Unsupported($"maschera {bitmap.Format}");
        }
        var stride = (long)bitmap.Stride;
        if (stride < (width + 7) / 8 || bitmap.Data.Length < stride * height) throw CodecException.Truncated();
        var bits = new bool[width * height];
        var topDown = bitmap.Flags.HasFlag(BitmapOptions.TopDown);
        var bigEndian = bitmap.Format == BitmapFormat.OneBitBE;
        var data = bitmap.Data.Span;
        for (var row = 0; row < height; row++)
        {
            var src = data.Slice((int)(row * stride));
            var dst = (topDown ? row : height - 1 - row) * width;
            for (var x = 0; x < width; x++)
            {
                var value = src[x / 8];
                bits[dst + x] = ((bigEndian ? value >> (7 - (x & 7)) : value >> (x & 7)) & 1) != 0;
            }
        }
        return new MaskBits(width, height, bits);
    }
}

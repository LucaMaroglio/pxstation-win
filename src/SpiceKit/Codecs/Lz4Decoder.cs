namespace SpiceKit.Codecs;

/// <summary>
/// Decoder del formato a blocchi LZ4 (senza intestazione di frame), usato da spicevmc
/// <c>COMPRESSED_DATA</c> e da <c>SPICE_IMAGE_TYPE_LZ4</c>. Scritto dalla descrizione pubblica del
/// formato a blocchi; sicuro con input ostili (porting di <c>SpiceCodecs/LZ4Decoder.swift</c>).
/// </summary>
public static class Lz4Decoder
{
    public const int MaxOutput = 256 * 1024 * 1024;

    /// <summary>Decomprime un blocco di dimensione decompressa nota.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int uncompressedSize)
    {
        if (uncompressedSize is < 0 or > MaxOutput) throw CodecException.TooLarge(uncompressedSize, 1);
        var output = new byte[uncompressedSize];
        var written = DecompressInto(input, output, 0);
        if (written != uncompressedSize) throw CodecException.Corrupt($"lz4 dimensione {written} invece di {uncompressedSize}");
        return output;
    }

    /// <summary>
    /// Decodifica un blocco scrivendo da <paramref name="start"/> in poi; i rimandi possono
    /// raggiungere i byte già presenti in <paramref name="output"/> (semantica "continua" di LZ4).
    /// Restituisce la nuova lunghezza scritta.
    /// </summary>
    internal static int DecompressInto(ReadOnlySpan<byte> input, Span<byte> output, int start)
    {
        var i = 0;
        var o = start;
        var limit = output.Length;
        while (i < input.Length)
        {
            var token = input[i++];
            var literals = ReadLength(input, ref i, token >> 4, limit);
            if (literals > input.Length - i || literals > limit - o) throw CodecException.Corrupt("lz4 letterali");
            input.Slice(i, literals).CopyTo(output[o..]);
            i += literals;
            o += literals;
            if (i == input.Length) break; // l'ultima sequenza ha solo letterali

            if (input.Length - i < 2) throw CodecException.Truncated();
            var offset = input[i] | (input[i + 1] << 8);
            i += 2;
            if (offset == 0 || offset > o) throw CodecException.Corrupt("lz4 offset");
            var matchLength = ReadLength(input, ref i, token & 0x0F, limit) + 4;
            if (matchLength > limit - o) throw CodecException.Corrupt("lz4 rimando");
            var from = o - offset;
            // Può sovrapporsi (ripetizioni): copia in avanti un byte alla volta.
            for (var k = 0; k < matchLength; k++) output[o + k] = output[from + k];
            o += matchLength;
        }
        return o;
    }

    private static int ReadLength(ReadOnlySpan<byte> input, ref int i, int baseLength, int limit)
    {
        var length = baseLength;
        if (baseLength != 15) return length;
        while (true)
        {
            if (i >= input.Length) throw CodecException.Truncated();
            var value = input[i++];
            length += value;
            if (length > limit) throw CodecException.Corrupt("lz4 lunghezza");
            if (value != 255) return length;
        }
    }
}

/// <summary>
/// Immagini <c>SPICE_IMAGE_TYPE_LZ4</c>: un flag top-down, un formato bitmap SPICE, poi blocchi
/// LZ4 (ognuno preceduto dalla dimensione u32 big-endian) decodificati come un unico flusso.
/// Righe compatte (stride = larghezza × byte per pixel); la dimensione viene dal descrittore.
/// </summary>
public static class Lz4ImageDecoder
{
    public static PixelBuffer Decode(ReadOnlySpan<byte> data, int width, int height)
    {
        CodecLimits.Check(width, height);
        if (data.Length < 2) throw CodecException.Truncated();
        var topDown = data[0] != 0;
        var format = data[1];
        var bytesPerPixel = format switch
        {
            6 => 2, // 16BIT
            7 => 3, // 24BIT
            8 or 9 => 4, // 32BIT, RGBA
            _ => throw CodecException.Unsupported($"lz4 formato bitmap {format}"),
        };
        var raw = new byte[(long)width * height * bytesPerPixel];
        var written = 0;
        var index = 2;
        while (index < data.Length)
        {
            if (data.Length - index < 4) throw CodecException.Truncated();
            var size = (data[index] << 24) | (data[index + 1] << 16) | (data[index + 2] << 8) | data[index + 3];
            index += 4;
            if (size < 0 || size > data.Length - index) throw CodecException.Truncated();
            written = Lz4Decoder.DecompressInto(data.Slice(index, size), raw, written);
            index += size;
        }
        if (written != raw.Length) throw CodecException.Corrupt($"lz4 immagine di {written} byte invece di {raw.Length}");

        var output = new PixelBuffer(width, height, 0, format == 9);
        var pixels = output.Pixels;
        for (var i = 0; i < pixels.Length; i++)
        {
            var o = i * bytesPerPixel;
            pixels[i] = format switch
            {
                6 => 0xFF00_0000 | Expand555(raw[o] | ((uint)raw[o + 1] << 8)),
                7 or 8 => 0xFF00_0000 | raw[o] | ((uint)raw[o + 1] << 8) | ((uint)raw[o + 2] << 16),
                _ => raw[o] | ((uint)raw[o + 1] << 8) | ((uint)raw[o + 2] << 16) | ((uint)raw[o + 3] << 24),
            };
        }
        if (!topDown) output.FlipVertically();
        return output;
    }

    /// <summary>x1r5g5b5 → xRGB8888, ogni canale espanso come <c>(c &lt;&lt; 3) | (c &gt;&gt; 2)</c>.</summary>
    internal static uint Expand555(uint v)
    {
        uint r = (v >> 10) & 31, g = (v >> 5) & 31, b = v & 31;
        return (PixelBuffer.Expand5(r) << 16) | (PixelBuffer.Expand5(g) << 8) | PixelBuffer.Expand5(b);
    }
}

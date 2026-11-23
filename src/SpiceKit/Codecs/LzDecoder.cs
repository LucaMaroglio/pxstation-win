using SpiceKit.Wire;

namespace SpiceKit.Codecs;

/// <summary>
/// Decoder delle immagini SPICE "LZ" (<c>SPICE_IMAGE_TYPE_LZ_RGB</c> e <c>LZ_PLT</c>), porting di
/// <c>SpiceCodecs/LZDecoder.swift</c>: scritto dal comportamento osservabile del formato e
/// verificato con le fixture del codificatore di riferimento, senza codice di riferimento.
/// </summary>
/// <remarks>
/// Intestazione (parole big-endian): magic "LZ  " (0x20205a4c), versione 0x00010001, tipo,
/// larghezza, altezza, stride, top_down. Poi byte di controllo:
/// <list type="bullet">
/// <item><c>ctrl &lt; 32</c>: seguono <c>ctrl + 1</c> unità letterali.</item>
/// <item><c>ctrl ≥ 32</c>: rimando all'indietro. Lunghezza = <c>ctrl &gt;&gt; 5</c> (7 = estesa con
///   byte da 255), distanza = <c>(ctrl &amp; 31) &lt;&lt; 8 | byte</c>, con distanza lunga a 16 bit
///   quando entrambe le parti sono tutte a uno. Lunghezza e distanza hanno uno scarto che
///   dipende dal tipo.</item>
/// </list>
/// </remarks>
public static class LzDecoder
{
    /// <summary>Valori di <c>LzImageType</c>.</summary>
    public enum ImageKind : uint
    {
        Plt1LE = 1,
        Plt1BE,
        Plt4LE,
        Plt4BE,
        Plt8,
        Rgb16,
        Rgb24,
        Rgb32,
        Rgba,
        Xxxa,
        A8,
    }

    internal const uint Magic = 0x2020_5A4C;
    internal const uint Version = 0x0001_0001;
    private const int MaxNearDistance = 8191;

    /// <summary>Decodifica un flusso LZ completo in BGRA.</summary>
    /// <param name="data">Il flusso.</param>
    /// <param name="palette">Obbligatoria per i flussi <c>LZ_PLT</c>.</param>
    public static PixelBuffer Decode(ReadOnlySpan<byte> data, Palette? palette = null)
    {
        var input = new Input(data);
        if (input.ReadBE32() != Magic) throw CodecException.BadMagic();
        var version = input.ReadBE32();
        if (version != Version) throw CodecException.Version(version);
        var rawType = input.ReadBE32();
        if (rawType is 0 or > (uint)ImageKind.A8) throw CodecException.Unsupported($"lz tipo {rawType}");
        var kind = (ImageKind)rawType;
        var width = (long)input.ReadBE32();
        var height = (long)input.ReadBE32();
        var stride = (long)input.ReadBE32();
        var topDown = input.ReadBE32() != 0;
        CodecLimits.Check(width, height);

        PixelBuffer image;
        if (kind <= ImageKind.Plt8)
        {
            if (palette is null) throw CodecException.MissingPalette();
            image = DecodePaletted(kind, (int)width, (int)height, stride, palette, ref input);
        }
        else
        {
            image = DecodeRgb(kind, (int)width, (int)height, ref input);
        }
        if (!topDown) image.FlipVertically();
        return image;
    }

    private static PixelBuffer DecodeRgb(ImageKind kind, int width, int height, ref Input input)
    {
        var count = width * height;
        var pixels = new uint[count];
        var hasAlpha = false;
        switch (kind)
        {
            case ImageKind.Rgb24 or ImageKind.Rgb32:
                Decompress(ref input, pixels, LiteralUnit.Bgr, lengthBias: 1, opaque: true);
                break;
            case ImageKind.Rgb16:
                Decompress(ref input, pixels, LiteralUnit.Rgb555, lengthBias: 2, opaque: true);
                break;
            case ImageKind.Rgba:
                Decompress(ref input, pixels, LiteralUnit.Bgr, lengthBias: 1, opaque: false);
                Decompress(ref input, pixels, LiteralUnit.Alpha, lengthBias: 3, opaque: false);
                hasAlpha = true;
                break;
            case ImageKind.Xxxa:
                Decompress(ref input, pixels, LiteralUnit.Alpha, lengthBias: 3, opaque: false);
                hasAlpha = true;
                break;
            case ImageKind.A8:
                Decompress(ref input, pixels, LiteralUnit.AlphaOnly, lengthBias: 3, opaque: false);
                hasAlpha = true;
                break;
            default:
                throw CodecException.Unsupported($"lz rgb {kind}");
        }
        return new PixelBuffer(width, height, pixels, hasAlpha);
    }

    private enum LiteralUnit
    {
        /// <summary>3 byte B G R.</summary>
        Bgr,

        /// <summary>2 byte big-endian x555.</summary>
        Rgb555,

        /// <summary>1 byte di alfa nel byte alto; il colore resta.</summary>
        Alpha,

        /// <summary>1 byte di alfa; il colore (premoltiplicato) è nero.</summary>
        AlphaOnly,
    }

    /// <summary>Decompressione LZ in cui un'unità letterale produce un pixel.</summary>
    private static void Decompress(ref Input input, uint[] output, LiteralUnit unit, int lengthBias, bool opaque)
    {
        var count = output.Length;
        var alphaOnly = unit == LiteralUnit.Alpha;
        var op = 0;
        while (op < count)
        {
            int ctrl = input.Read();
            if (ctrl >= 32)
            {
                var (length, distance) = ReadMatch(ctrl, ref input, lengthBias);
                var reference = op - distance;
                if (reference < 0 || op + length > count) throw CodecException.Corrupt("lz rimando fuori intervallo");
                if (alphaOnly)
                {
                    for (var k = 0; k < length; k++)
                    {
                        output[op + k] = (output[op + k] & 0x00FF_FFFF) | (output[reference + k] & 0xFF00_0000);
                    }
                }
                else
                {
                    // Le sovrapposizioni sono volute (ripetizioni): copia in avanti un elemento alla volta.
                    for (var k = 0; k < length; k++) output[op + k] = output[reference + k];
                }
                op += length;
            }
            else
            {
                var literals = ctrl + 1;
                if (op + literals > count) throw CodecException.Corrupt("lz troppi letterali");
                for (var k = 0; k < literals; k++, op++)
                {
                    switch (unit)
                    {
                        case LiteralUnit.Bgr:
                            uint b = input.Read(), g = input.Read(), r = input.Read();
                            output[op] = (opaque ? 0xFF00_0000 : 0) | (r << 16) | (g << 8) | b;
                            break;
                        case LiteralUnit.Rgb555:
                            uint high = input.Read(), low = input.Read();
                            output[op] = PixelBuffer.From555((high << 8) | low);
                            break;
                        case LiteralUnit.Alpha:
                            output[op] = (output[op] & 0x00FF_FFFF) | ((uint)input.Read() << 24);
                            break;
                        case LiteralUnit.AlphaOnly:
                            output[op] = (uint)input.Read() << 24;
                            break;
                    }
                }
            }
        }
    }

    /// <summary>Lunghezza e distanza di un rimando (scarti già applicati).</summary>
    private static (int Length, int Distance) ReadMatch(int ctrl, ref Input input, int lengthBias)
    {
        var length = (ctrl >> 5) - 1;
        var distance = (ctrl & 31) << 8;
        if (length == 6)
        {
            byte code;
            do
            {
                code = input.Read();
                length += code;
                if (length > CodecLimits.MaxPixels) throw CodecException.Corrupt("lz lunghezza");
            }
            while (code == 255);
        }
        int last = input.Read();
        distance += last;
        if (last == 255 && distance - last == 31 << 8)
        {
            distance = input.Read() << 8;
            distance += input.Read();
            distance += MaxNearDistance;
        }
        return (length + lengthBias, distance + 1);
    }

    private static PixelBuffer DecodePaletted(ImageKind kind, int width, int height, long stride, Palette palette, ref Input input)
    {
        var pixelsPerUnit = kind switch
        {
            ImageKind.Plt1LE or ImageKind.Plt1BE => 8,
            ImageKind.Plt4LE or ImageKind.Plt4BE => 2,
            _ => 1,
        };
        if (stride * pixelsPerUnit < width) throw CodecException.Corrupt("lz stride troppo piccolo");
        var units = stride * height;
        if (units > CodecLimits.MaxPixels) throw CodecException.TooLarge(width, height);
        var unitsPerRow = (int)stride;

        // Prima i byte compattati: i rimandi lavorano su byte interi.
        var packed = new byte[units];
        var op = 0;
        while (op < packed.Length)
        {
            int ctrl = input.Read();
            if (ctrl >= 32)
            {
                var (length, distance) = ReadMatch(ctrl, ref input, 3);
                var reference = op - distance;
                if (reference < 0 || op + length > packed.Length) throw CodecException.Corrupt("lz plt rimando fuori intervallo");
                for (var k = 0; k < length; k++) packed[op + k] = packed[reference + k];
                op += length;
            }
            else
            {
                var literals = ctrl + 1;
                if (op + literals > packed.Length) throw CodecException.Corrupt("lz plt troppi letterali");
                for (var k = 0; k < literals; k++) packed[op++] = input.Read();
            }
        }

        var entries = palette.Entries;
        if (entries.Length == 0) throw CodecException.MissingPalette();
        var output = new PixelBuffer(width, height);
        var pixels = output.Pixels;
        for (var row = 0; row < height; row++)
        {
            var src = row * unitsPerRow;
            var dst = row * width;
            for (var x = 0; x < width; x++)
            {
                int index;
                switch (kind)
                {
                    case ImageKind.Plt8:
                        index = packed[src + x];
                        pixels[dst + x] = index < entries.Length ? 0xFF00_0000 | entries[index] : 0xFF00_0000;
                        continue;
                    case ImageKind.Plt4BE:
                        index = (x & 1) == 0 ? packed[src + (x / 2)] >> 4 : packed[src + (x / 2)] & 0x0F;
                        break;
                    case ImageKind.Plt4LE:
                        index = (x & 1) == 0 ? packed[src + (x / 2)] & 0x0F : packed[src + (x / 2)] >> 4;
                        break;
                    case ImageKind.Plt1BE:
                        index = (packed[src + (x / 8)] >> (7 - (x & 7))) & 1;
                        break;
                    default:
                        index = (packed[src + (x / 8)] >> (x & 7)) & 1;
                        break;
                }
                pixels[dst + x] = 0xFF00_0000 | entries[index % entries.Length];
            }
        }
        return output;
    }

    /// <summary>Lettore di byte con controllo dei limiti.</summary>
    private ref struct Input(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        public byte Read()
        {
            if ((uint)_position >= (uint)_bytes.Length) throw CodecException.Truncated();
            return _bytes[_position++];
        }

        public uint ReadBE32()
        {
            uint value = 0;
            for (var i = 0; i < 4; i++) value = (value << 8) | Read();
            return value;
        }
    }
}

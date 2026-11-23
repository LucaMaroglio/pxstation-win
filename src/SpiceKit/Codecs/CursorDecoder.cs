using SpiceKit.Wire;

namespace SpiceKit.Codecs;

/// <summary>Forma del cursore decodificata (BGRA premoltiplicato) con il punto attivo.</summary>
public sealed record CursorImage(PixelBuffer Image, int HotSpotX, int HotSpotY);

/// <summary>Converte le bitmap dei cursori SPICE in BGRA premoltiplicato (porting di <c>CursorDecoder</c> macOS).</summary>
public static class CursorDecoder
{
    /// <summary>Lato massimo accettato (contro server ostili; i cursori reali sono ≤ 256).</summary>
    public const int MaxSize = 512;

    /// <summary><see langword="null"/> per tipi non supportati, misure fuori limite o dati troncati.</summary>
    public static CursorImage? Decode(CursorHeader header, ReadOnlySpan<byte> data)
    {
        int width = header.Width, height = header.Height;
        if (width <= 0 || height <= 0 || width > MaxSize || height > MaxSize) return null;
        var maskStride = (width + 7) / 8;
        var pixels = new uint[width * height];

        switch (header.Type)
        {
            case CursorType.Alpha:
                if (data.Length < width * height * 4) return null;
                for (var i = 0; i < width * height; i++)
                {
                    var p = i * 4;
                    uint b = data[p], g = data[p + 1], r = data[p + 2], a = data[p + 3];
                    // I cursori alpha di SPICE sono ARGB non premoltiplicati.
                    pixels[i] = (a << 24) | (r * a / 255 << 16) | (g * a / 255 << 8) | (b * a / 255);
                }
                break;
            case CursorType.Mono:
                // Maschera AND seguita dalla maschera XOR, entrambe a 1 bit per pixel.
                if (data.Length < 2 * maskStride * height) return null;
                var xor = data[(maskStride * height)..];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var and = Bit(data, maskStride, x, y);
                        var flip = Bit(xor, maskStride, x, y);
                        pixels[(y * width) + x] = (and, flip) switch
                        {
                            (false, false) => 0xFF00_0000, // nero
                            (false, true) => 0xFFFF_FFFF, // bianco
                            (true, false) => 0, // trasparente
                            (true, true) => 0xFF00_0000, // "inverti": approssimato con il nero
                        };
                    }
                }
                break;
            case CursorType.Color32 or CursorType.Color24:
                var bytesPerPixel = header.Type == CursorType.Color32 ? 4 : 3;
                var colorStride = width * bytesPerPixel;
                if (data.Length < (colorStride * height) + (maskStride * height)) return null;
                var mask = data[(colorStride * height)..];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var p = (y * colorStride) + (x * bytesPerPixel);
                        var color = 0xFF00_0000u | ((uint)data[p + 2] << 16) | ((uint)data[p + 1] << 8) | data[p];
                        pixels[(y * width) + x] = Bit(mask, maskStride, x, y) ? 0 : color;
                    }
                }
                break;
            default:
                return null;
        }
        return new CursorImage(new PixelBuffer(width, height, pixels, hasAlpha: true),
            Math.Min((int)header.HotSpotX, width - 1), Math.Min((int)header.HotSpotY, height - 1));
    }

    private static bool Bit(ReadOnlySpan<byte> data, int stride, int x, int y) =>
        (data[(y * stride) + (x / 8)] & (0x80 >> (x & 7))) != 0;
}

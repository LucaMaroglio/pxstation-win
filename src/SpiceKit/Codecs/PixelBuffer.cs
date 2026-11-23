using SpiceKit.Wire;

namespace SpiceKit.Codecs;

/// <summary>
/// Immagine a 32 bit in BGRA8 premoltiplicato (porting di <c>SpiceCodecs/PixelBuffer.swift</c>).
/// </summary>
/// <remarks>
/// Ogni pixel è un <see cref="uint"/> <c>0xAARRGGBB</c>: in memoria little-endian è la sequenza
/// B, G, R, A, cioè <c>DXGI_FORMAT_B8G8R8A8_UNORM</c>. Righe dall'alto in basso, senza padding.
/// </remarks>
public sealed partial class PixelBuffer
{
    public PixelBuffer(int width, int height, uint fill = 0xFF00_0000, bool hasAlpha = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        Pixels = new uint[width * height];
        if (fill != 0) Array.Fill(Pixels, fill);
        HasAlpha = hasAlpha;
    }

    public PixelBuffer(int width, int height, uint[] pixels, bool hasAlpha)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height) throw new ArgumentException("Numero di pixel errato.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
        HasAlpha = hasAlpha;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>I pixel, riga per riga.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819",
        Justification = "Buffer di lavoro dei decoder e del canvas: una copia per accesso sarebbe proibitiva.")]
    public uint[] Pixels { get; }

    /// <summary><see langword="false"/> se la sorgente non aveva canale alfa (alfa forzato a 0xFF).</summary>
    public bool HasAlpha { get; set; }

    public SpiceRect Bounds => SpiceRect.FromSize(0, 0, Width, Height);

    public uint this[int x, int y]
    {
        get => Pixels[(y * Width) + x];
        set => Pixels[(y * Width) + x] = value;
    }

    /// <summary>Inverte l'ordine delle righe (dal basso in alto → dall'alto in basso).</summary>
    public void FlipVertically()
    {
        if (Height < 2) return;
        var temp = new uint[Width];
        for (int top = 0, bottom = Height - 1; top < bottom; top++, bottom--)
        {
            var a = Pixels.AsSpan(top * Width, Width);
            var b = Pixels.AsSpan(bottom * Width, Width);
            a.CopyTo(temp);
            b.CopyTo(a);
            temp.CopyTo(b);
        }
    }

    /// <summary>Copia di un sotto-rettangolo (limitato al buffer).</summary>
    public PixelBuffer Cropped(int x, int y, int width, int height)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = (int)Math.Min(Width, (long)x + width), y1 = (int)Math.Min(Height, (long)y + height);
        if (x1 <= x0 || y1 <= y0) return new PixelBuffer(0, 0);
        var output = new uint[(x1 - x0) * (y1 - y0)];
        for (var row = y0; row < y1; row++)
        {
            Pixels.AsSpan((row * Width) + x0, x1 - x0).CopyTo(output.AsSpan((row - y0) * (x1 - x0)));
        }
        return new PixelBuffer(x1 - x0, y1 - y0, output, HasAlpha);
    }

    internal static uint Opaque(uint r, uint g, uint b) => 0xFF00_0000 | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);

    /// <summary>Espande un canale a 5 bit in 8 bit.</summary>
    internal static uint Expand5(uint value) => (value << 3) | (value >> 2);

    /// <summary>x555 (<c>0RRRRRGG GGGBBBBB</c>) → BGRA opaco.</summary>
    internal static uint From555(uint value) =>
        Opaque(Expand5((value >> 10) & 0x1F), Expand5((value >> 5) & 0x1F), Expand5(value & 0x1F));
}

/// <summary>Tipo di errore di un decoder di immagini.</summary>
public enum CodecErrorKind
{
    BadMagic,
    UnsupportedVersion,
    UnsupportedType,
    Truncated,
    Corrupt,
    MissingPalette,
    TooLarge,
}

/// <summary>
/// Errore di un decoder. I dati del server sono ostili: ogni errore è recuperabile (si perde
/// l'immagine, non la sessione).
/// </summary>
public sealed class CodecException(CodecErrorKind kind, string message) : Exception(message)
{
    public CodecErrorKind Kind { get; } = kind;

    public static CodecException Truncated() => new(CodecErrorKind.Truncated, "Immagine troncata.");

    public static CodecException Corrupt(string what) => new(CodecErrorKind.Corrupt, "Immagine non valida: " + what + ".");

    internal static CodecException BadMagic() => new(CodecErrorKind.BadMagic, "Intestazione dell'immagine non riconosciuta.");

    internal static CodecException MissingPalette() => new(CodecErrorKind.MissingPalette, "Palette mancante.");

    internal static CodecException Unsupported(string what) => new(CodecErrorKind.UnsupportedType, "Formato non supportato: " + what + ".");

    internal static CodecException Version(uint version) =>
        new(CodecErrorKind.UnsupportedVersion, $"Versione del formato non supportata: 0x{version:X8}.");

    internal static CodecException TooLarge(long width, long height) =>
        new(CodecErrorKind.TooLarge, $"Immagine troppo grande: {width}x{height}.");
}

/// <summary>Limiti di sicurezza delle immagini decodificate.</summary>
public static class CodecLimits
{
    public const int MaxDimension = 16_384;
    public const int MaxPixels = 64 * 1024 * 1024;

    public static void Check(long width, long height)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension || width * height > MaxPixels)
        {
            throw CodecException.TooLarge(width, height);
        }
    }
}

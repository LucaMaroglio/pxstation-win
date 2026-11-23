namespace SpiceKit.Codecs;

/// <summary>Matrice di conversione YUV → RGB.</summary>
public enum YuvMatrix
{
    /// <summary>BT.601 (definizione standard; predefinita dei codificatori per i formati piccoli).</summary>
    Bt601,

    /// <summary>BT.709 (alta definizione).</summary>
    Bt709,
}

/// <summary>
/// NV12 (piano Y, poi piano UV interlacciato a metà risoluzione) a intervallo limitato
/// (16–235 / 16–240) → BGRA opaco, il formato che escono dai decoder video di Windows.
/// </summary>
public static class Nv12Converter
{
    /// <summary>
    /// Converte <paramref name="width"/> × <paramref name="height"/> pixel: <paramref name="y"/> è il
    /// piano della luminanza e <paramref name="uv"/> quello della crominanza (U, V alternati, metà
    /// delle righe), entrambi con <paramref name="pitch"/> byte per riga.
    /// </summary>
    public static PixelBuffer Convert(ReadOnlySpan<byte> y, ReadOnlySpan<byte> uv, int pitch, int width, int height, YuvMatrix matrix)
    {
        CodecLimits.Check(width, height);
        var chromaRows = (height + 1) / 2;
        if (pitch < width + (width & 1) || y.Length < pitch * (height - 1) + width || uv.Length < pitch * (chromaRows - 1) + ((width + 1) & ~1))
        {
            throw CodecException.Truncated();
        }
        // Coefficienti in virgola fissa (×256) per l'intervallo limitato.
        var (rv, gu, gv, bu) = matrix == YuvMatrix.Bt709 ? (459, -55, -136, 541) : (409, -100, -208, 516);
        var pixels = new uint[width * height];
        for (var row = 0; row < height; row++)
        {
            var luma = y.Slice(row * pitch, width);
            var chroma = uv.Slice(row / 2 * pitch);
            for (var x = 0; x < width; x++)
            {
                var c = 298 * (luma[x] - 16);
                var u = chroma[x & ~1] - 128;
                var v = chroma[(x & ~1) + 1] - 128;
                var r = Clamp((c + (rv * v) + 128) >> 8);
                var g = Clamp((c + (gu * u) + (gv * v) + 128) >> 8);
                var b = Clamp((c + (bu * u) + 128) >> 8);
                pixels[(row * width) + x] = 0xFF00_0000u | (r << 16) | (g << 8) | b;
            }
        }
        return new PixelBuffer(width, height, pixels, hasAlpha: false);
    }

    private static uint Clamp(int value) => (uint)Math.Clamp(value, 0, 255);
}

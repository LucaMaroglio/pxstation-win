using SpiceKit.Wire;

namespace SpiceKit.Codecs;

/// <summary>
/// Decoder JPEG (<c>SPICE_IMAGE_TYPE_JPEG</c>, <c>JPEG_ALPHA</c>, fotogrammi MJPEG).
/// Restituisce BGRA premoltiplicato e lancia <see cref="CodecException"/> per dati non validi.
/// </summary>
public interface IJpegDecoder
{
    PixelBuffer Decode(ReadOnlySpan<byte> jpeg);
}

/// <summary>Decoder di un flusso video H.264/H.265 (un'istanza per flusso, usata da un solo thread).</summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>
    /// Decodifica un'unità di accesso in forma Annex B. <see langword="null"/> finché manca il primo
    /// fotogramma chiave o se il decoder trattiene il fotogramma; <see cref="CodecException"/> per
    /// dati non validi (il decoder riparte dal fotogramma chiave successivo).
    /// </summary>
    PixelBuffer? Decode(ReadOnlySpan<byte> accessUnit);
}

/// <summary>
/// Decoder offerti dalla piattaforma: SpiceKit è puro, l'implementazione Windows (WIC, Media
/// Foundation) sta in SpiceKit.Windows. Senza decoder il client non annuncia codec video e le
/// immagini JPEG risultano non decodificabili.
/// </summary>
public interface IMediaDecoders
{
    IJpegDecoder? Jpeg { get; }

    /// <summary>Codec video decodificabili, in ordine di preferenza (MJPEG richiede <see cref="Jpeg"/>).</summary>
    IReadOnlyList<VideoCodecType> VideoCodecs { get; }

    /// <summary>Nuovo decoder per un codec di <see cref="VideoCodecs"/> diverso da MJPEG.</summary>
    IVideoDecoder? CreateVideoDecoder(VideoCodecType codec);
}

/// <summary>JPEG con canale alfa: JPEG per il colore seguito da un piano alfa LZ (<c>XXXA</c>).</summary>
public static class JpegAlphaDecoder
{
    public static PixelBuffer Decode(IJpegDecoder jpeg, ReadOnlySpan<byte> data, int jpegSize)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (jpegSize < 0 || jpegSize > data.Length) throw CodecException.Truncated();
        var color = jpeg.Decode(data[..jpegSize]);
        var alpha = LzDecoder.Decode(data[jpegSize..]);
        if (alpha.Width != color.Width || alpha.Height != color.Height) throw CodecException.Corrupt("jpeg alpha: misure diverse");
        var pixels = color.Pixels;
        for (var i = 0; i < pixels.Length; i++)
        {
            // Il JPEG è opaco: si applica l'alfa premoltiplicando il colore.
            var a = alpha.Pixels[i] >> 24;
            var p = pixels[i];
            uint r = ((p >> 16) & 0xFF) * a / 255, g = ((p >> 8) & 0xFF) * a / 255, b = (p & 0xFF) * a / 255;
            pixels[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }
        return new PixelBuffer(color.Width, color.Height, pixels, hasAlpha: true);
    }
}

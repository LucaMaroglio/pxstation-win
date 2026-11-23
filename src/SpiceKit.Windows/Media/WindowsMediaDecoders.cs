using SpiceKit.Codecs;
using SpiceKit.Wire;

namespace SpiceKit.Windows.Media;

/// <summary>
/// Decoder della piattaforma per <see cref="Session.SpiceSession"/>: JPEG con WIC, H.264/H.265 con
/// Media Foundation quando Windows ha il decoder (H.265 richiede l'estensione HEVC dallo Store;
/// le edizioni N di Windows senza Media Feature Pack non hanno nemmeno H.264).
/// </summary>
/// <remarks>
/// Preferenza dei codec: H.264, H.265, poi MJPEG (sempre disponibile). VP8/VP9 non sono supportati.
/// La disponibilità si controlla una volta sola per processo.
/// </remarks>
public sealed class WindowsMediaDecoders : IMediaDecoders
{
    private static readonly Lazy<WindowsMediaDecoders> Default = new(() => new WindowsMediaDecoders());

    private WindowsMediaDecoders()
    {
        var codecs = new List<VideoCodecType>();
        if (MediaFoundationVideoDecoder.IsSupported(VideoCodecType.H264)) codecs.Add(VideoCodecType.H264);
        if (MediaFoundationVideoDecoder.IsSupported(VideoCodecType.H265)) codecs.Add(VideoCodecType.H265);
        codecs.Add(VideoCodecType.Mjpeg);
        // Diagnostica: WSC_VIDEO_CODECS=mjpeg,h264 limita i codec annunciati ("none": nessun flusso video).
        if (Environment.GetEnvironmentVariable("WSC_VIDEO_CODECS") is { Length: > 0 } wanted)
        {
            var names = wanted.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            codecs.RemoveAll(c => !names.Contains(c.ToString(), StringComparer.OrdinalIgnoreCase));
        }
        VideoCodecs = codecs;
    }

    public static WindowsMediaDecoders Shared => Default.Value;

    public IJpegDecoder? Jpeg { get; } = new WicJpegDecoder();

    public IReadOnlyList<VideoCodecType> VideoCodecs { get; }

    public IVideoDecoder? CreateVideoDecoder(VideoCodecType codec) => MediaFoundationVideoDecoder.Create(codec);
}

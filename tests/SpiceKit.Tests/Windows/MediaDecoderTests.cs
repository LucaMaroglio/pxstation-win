using System.Runtime.InteropServices.WindowsRuntime;
using SpiceKit.Codecs;
using SpiceKit.Wire;
using SpiceKit.Windows.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SpiceKit.Tests.Windows;

/// <summary>Decoder della piattaforma: JPEG con WIC, NV12 → BGRA, H.264 con Media Foundation.</summary>
public class MediaDecoderTests
{
    /// <summary>Immagine di prova 32×16: quattro bande verticali rosso, verde, blu, bianco.</summary>
    internal static uint ColorAt(int x) => (x / 8) switch
    {
        0 => 0xFFFF_0000,
        1 => 0xFF00_FF00,
        2 => 0xFF00_00FF,
        _ => 0xFFFF_FFFF,
    };

    private static byte[] EncodeJpeg(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++) BitConverter.GetBytes(ColorAt(x * 32 / width)).CopyTo(bgra, ((y * width) + x) * 4);
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream).AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask().GetAwaiter().GetResult();
        return bytes;
    }

    internal static void AssertClose(uint expected, uint actual, int tolerance)
    {
        for (var shift = 0; shift < 24; shift += 8)
        {
            var e = (int)((expected >> shift) & 0xFF);
            var a = (int)((actual >> shift) & 0xFF);
            Assert.True(Math.Abs(e - a) <= tolerance, $"0x{expected:X8} atteso, 0x{actual:X8} ottenuto");
        }
    }

    [Fact]
    public void Jpeg_decodes_to_opaque_bgra()
    {
        var image = new WicJpegDecoder().Decode(EncodeJpeg(32, 16));
        Assert.Equal((32, 16), (image.Width, image.Height));
        foreach (var x in new[] { 3, 11, 19, 27 }) AssertClose(ColorAt(x), image[x, 8], 24);
        Assert.All(image.Pixels, p => Assert.Equal(0xFFu, p >> 24));
    }

    [Fact]
    public void Corrupt_and_oversized_jpegs_are_rejected()
    {
        var decoder = new WicJpegDecoder();
        Assert.Throws<CodecException>(() => decoder.Decode([1, 2, 3, 4]));
        var jpeg = EncodeJpeg(32, 16);
        Assert.Throws<CodecException>(() => decoder.Decode(jpeg.AsSpan(0, 40)));

        // Intestazione SOF0 modificata: 20000×20000 supera i limiti prima di decodificare i pixel.
        var sof = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        Assert.True(sof > 0);
        var huge = (byte[])jpeg.Clone();
        huge[sof + 5] = 0x4E;
        huge[sof + 6] = 0x20;
        huge[sof + 7] = 0x4E;
        huge[sof + 8] = 0x20;
        var error = Assert.Throws<CodecException>(() => decoder.Decode(huge));
        Assert.Equal(CodecErrorKind.TooLarge, error.Kind);
    }

    [Fact]
    public void Jpeg_alpha_combines_color_and_lz_alpha()
    {
        // Piano alfa LZ (XXXA) da una fixture macOS; il JPEG si genera della stessa misura.
        var lz = File.ReadAllBytes(Support.Fixtures.PathOf("codecs", "lz-xxxa-desktop.bin"));
        var alpha = LzDecoder.Decode(lz);
        var jpeg = EncodeJpeg(alpha.Width, alpha.Height);
        byte[] data = [.. jpeg, .. lz];
        var image = JpegAlphaDecoder.Decode(new WicJpegDecoder(), data, jpeg.Length);
        Assert.True(image.HasAlpha);
        for (var i = 0; i < image.Pixels.Length; i++)
        {
            Assert.Equal(alpha.Pixels[i] >> 24, image.Pixels[i] >> 24);
            // Premoltiplicato: nessun canale supera l'alfa.
            Assert.True(((image.Pixels[i] >> 16) & 0xFF) <= image.Pixels[i] >> 24);
        }

        var other = EncodeJpeg(alpha.Width + 1, alpha.Height);
        Assert.Throws<CodecException>(() => JpegAlphaDecoder.Decode(new WicJpegDecoder(), [.. other, .. lz], other.Length));
        Assert.Throws<CodecException>(() => JpegAlphaDecoder.Decode(new WicJpegDecoder(), data, data.Length + 1));
    }

    [Theory]
    [InlineData(235, 128, 128, 0xFFFF_FFFFu)] // bianco
    [InlineData(16, 128, 128, 0xFF00_0000u)] // nero
    [InlineData(81, 90, 240, 0xFFFF_0000u)] // rosso BT.601
    [InlineData(145, 54, 34, 0xFF00_FF00u)] // verde BT.601
    [InlineData(41, 240, 110, 0xFF00_00FFu)] // blu BT.601
    public void Nv12_limited_range_bt601(byte y, byte u, byte v, uint expected)
    {
        // 2×2 pixel uguali, passo 4 (con riempimento).
        byte[] luma = [y, y, 0, 0, y, y, 0, 0];
        byte[] chroma = [u, v, 0, 0];
        var image = Nv12Converter.Convert(luma, chroma, 4, 2, 2, YuvMatrix.Bt601);
        Assert.All(image.Pixels, p => AssertClose(expected, p, 3));
    }

    [Fact]
    public void Nv12_rejects_short_planes()
    {
        Assert.Throws<CodecException>(() => Nv12Converter.Convert(new byte[3], new byte[2], 2, 2, 2, YuvMatrix.Bt601));
        Assert.Throws<CodecException>(() => Nv12Converter.Convert(new byte[4], new byte[2], 1, 2, 2, YuvMatrix.Bt601));
    }

    [Fact]
    public void Windows_decoders_offer_mjpeg_and_report_h264()
    {
        var decoders = WindowsMediaDecoders.Shared;
        Assert.NotNull(decoders.Jpeg);
        Assert.Equal(VideoCodecType.Mjpeg, decoders.VideoCodecs[^1]);
        TestContext.Current.TestOutputHelper?.WriteLine($"codec video: {string.Join(", ", decoders.VideoCodecs)}");
    }

    [Fact]
    public void H264_from_the_windows_encoder_decodes_to_the_same_picture()
    {
        if (!MediaFoundationVideoDecoder.IsSupported(VideoCodecType.H264)) Assert.Skip("decoder H.264 di Windows non disponibile");
        var frames = H264TestEncoder.Encode(128, 64, count: 5);
        if (frames is null) Assert.Skip("codificatore H.264 di Windows non disponibile");
        Assert.True(AnnexB.HasKeyFrame(frames[0], hevc: false), "il primo fotogramma deve essere un IDR");

        using var decoder = MediaFoundationVideoDecoder.Create(VideoCodecType.H264)!;
        // Senza fotogramma chiave non si decodifica nulla.
        if (frames.Count > 1 && !AnnexB.HasKeyFrame(frames[1], hevc: false)) Assert.Null(decoder.Decode(frames[1]));
        PixelBuffer? picture = null;
        foreach (var frame in frames) picture = decoder.Decode(frame) ?? picture;
        Assert.NotNull(picture);
        Assert.Equal((128, 64), (picture.Width, picture.Height));
        foreach (var x in new[] { 12, 44, 76, 108 }) AssertClose(ColorAt(x / 4), picture[x, 32], 40);

        // Dati non validi: errore o nessun fotogramma, mai un'eccezione diversa.
        try
        {
            decoder.Decode([0, 0, 0, 1, 0x65, 0xFF, 0xFF, 0xFF, 0xFF]);
        }
        catch (CodecException)
        {
        }
    }
}

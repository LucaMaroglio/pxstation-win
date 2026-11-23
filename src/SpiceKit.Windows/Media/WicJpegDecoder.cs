using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using SpiceKit.Codecs;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using BitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using Buffer = System.Buffer;

namespace SpiceKit.Windows.Media;

/// <summary>
/// JPEG con il decoder di Windows (<c>Windows.Graphics.Imaging</c>, cioè WIC): immagini JPEG,
/// JPEG con alfa e fotogrammi MJPEG. Restituisce BGRA premoltiplicato.
/// </summary>
/// <remarks>
/// Le API sono asincrone: qui si attendono in modo sincrono, perché si chiama dal ciclo del canale
/// display (mai dal thread dell'interfaccia). La misura si controlla prima di decodificare i pixel.
/// </remarks>
public sealed class WicJpegDecoder : IJpegDecoder
{
    public PixelBuffer Decode(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw CodecException.Corrupt("jpeg: intestazione");
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            stream.WriteAsync(jpeg.ToArray().AsBuffer()).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);
            var decoder = BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, stream).AsTask().GetAwaiter().GetResult();
            int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
            CodecLimits.Check(width, height);
            var data = decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
            var bytes = data.DetachPixelData();
            if (bytes.Length < width * height * 4) throw CodecException.Truncated();
            var pixels = new uint[width * height];
            Buffer.BlockCopy(bytes, 0, pixels, 0, pixels.Length * 4);
            // Il JPEG non ha trasparenza: opaco anche se WIC lascia l'alfa a zero.
            for (var i = 0; i < pixels.Length; i++) pixels[i] |= 0xFF00_0000;
            return new PixelBuffer(width, height, pixels, hasAlpha: false);
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
        {
            throw CodecException.Corrupt("jpeg: " + error.Message);
        }
    }
}

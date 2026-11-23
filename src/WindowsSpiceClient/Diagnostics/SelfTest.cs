using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WindowsSpiceClient.Diagnostics;

/// <summary>
/// Autoverifica dell'interfaccia, attivata da <c>--self-test &lt;cartella&gt;</c>.
/// </summary>
/// <remarks>
/// Non dipende dalla cattura dello schermo (che in una sessione remota o in una VM senza GPU può
/// restituire fotogrammi vecchi o neri): disegna l'albero XAML con <see cref="RenderTargetBitmap"/>
/// in <c>ui.png</c> e scrive l'esito in <c>self-test.txt</c>. <see cref="RenderTargetBitmap"/> non
/// include il contenuto degli SwapChainPanel: il renderer Direct3D si verifica rileggendo il
/// buffer (<see cref="Display.D3D11Presenter.ClearAndProbe"/>).
/// </remarks>
internal static class SelfTest
{
    public static string? OutputDirectory { get; } = ParseArguments(Environment.GetCommandLineArgs());

    public static bool IsEnabled => OutputDirectory is not null;

    private static string? ParseArguments(string[] args)
    {
        var index = Array.IndexOf(args, "--self-test");
        return index < 0 ? null : Path.GetFullPath(index + 1 < args.Length ? args[index + 1] : "self-test");
    }

    public static async Task CaptureAsync(UIElement root, IEnumerable<string> results)
    {
        var directory = OutputDirectory!;
        Directory.CreateDirectory(directory);
        var lines = new List<string>(results);
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root);
            var pixels = await bitmap.GetPixelsAsync();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            var png = new byte[stream.Size];
            stream.Seek(0);
            await stream.ReadAsync(png.AsBuffer(), (uint)png.Length, InputStreamOptions.None);
            await File.WriteAllBytesAsync(Path.Combine(directory, "ui.png"), png);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"xaml: OK {bitmap.PixelWidth}x{bitmap.PixelHeight} -> ui.png"));
        }
        catch (Exception error) when (error is InvalidOperationException or IOException
                                          or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            lines.Add($"xaml: ERRORE {error.Message}");
        }

        await File.WriteAllLinesAsync(Path.Combine(directory, "self-test.txt"), lines);
        AppLog.Info("Autoverifica: " + string.Join(" | ", lines));
    }
}

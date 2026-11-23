using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Display;

namespace WindowsSpiceClient;

/// <summary>
/// Finestra di M0: verifica che la catena Direct3D 11 → swap chain → SwapChainPanel funzioni
/// (anche in software con WARP, come in una VM senza GPU) e mostra quale adattatore usa.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "Una Window non è IDisposable: il presenter viene rilasciato nell'evento Closed.")]
public sealed partial class MainWindow : Window
{
    private readonly D3D11Presenter? _presenter;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1024, 720));
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
            Root.Background = null; // lascia trasparire Mica
        }

        AppLog.Info($"Mica supportata: {Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()}");

        // Diagnostica: WSC_RENDERER=none esclude Direct3D (per distinguere problemi del renderer da quelli di WinUI).
        if (string.Equals(Environment.GetEnvironmentVariable("WSC_RENDERER"), "none", StringComparison.OrdinalIgnoreCase))
        {
            RendererInfo.Message = "Renderer disattivato (WSC_RENDERER=none)";
            AppLog.Info("Renderer disattivato da WSC_RENDERER=none");
            return;
        }

        try
        {
            _presenter = new D3D11Presenter(DisplayPanel);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException
                                          or SharpGen.Runtime.SharpGenException)
        {
            AppLog.Error("Creazione del renderer Direct3D non riuscita", error);
            RendererInfo.Severity = InfoBarSeverity.Error;
            RendererInfo.Message = error.Message;
            return;
        }

        DisplayPanel.SizeChanged += (_, _) => Render();
        DisplayPanel.CompositionScaleChanged += (_, _) => Render();
        Closed += (_, _) => _presenter.Dispose();

        var info = _presenter.Info;
        AppLog.Info($"Renderer: {info.AdapterName}, {info.FeatureLevel}, software={info.IsSoftware}");
        RendererInfo.Message = string.Create(CultureInfo.InvariantCulture,
            $"{info.AdapterName} · {info.FeatureLevel}{(info.IsSoftware ? " · software (WARP)" : string.Empty)}");
        RendererInfo.Severity = info.IsSoftware ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
    }

    /// <summary>Con <c>--self-test</c>: verifica renderer e interfaccia, poi chiude l'app.</summary>
    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (!SelfTest.IsEnabled)
        {
            return;
        }

        await Task.Delay(1500); // primo layout, ridimensionamento della swap chain
        var results = new List<string> { $"renderer: {RendererInfo.Message}" };
        if (_presenter is not null)
        {
            // Colore di prova saturo e riconoscibile: (R 1.0, G 0.5, B 0.0) → 0xFFFF8000 (G 127 o 128).
            var pixel = _presenter.ClearAndProbe(1f, 0.5f, 0f);
            var ok = (pixel & 0xFFFF0000) == 0xFFFF0000 && ((pixel >> 8) & 0xFF) is 127 or 128 && (pixel & 0xFF) == 0;
            results.Add(string.Create(CultureInfo.InvariantCulture,
                $"direct3d: {(ok ? "OK" : "ERRORE")} pixel centrale 0x{pixel:X8} (atteso 0xFFFF7F00/0xFFFF8000)"));
        }

        // Mica è trasparente per RenderTargetBitmap: per l'immagine si usa lo sfondo pieno del tema.
        Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        await SelfTest.CaptureAsync(Root, results);
        Close();
    }

    private void Render()
    {
        if (_presenter is null)
        {
            return;
        }

        var width = DisplayPanel.ActualWidth;
        var height = DisplayPanel.ActualHeight;
        if (width < 1 || height < 1)
        {
            return;
        }

        _presenter.Resize(width, height, DisplayPanel.CompositionScaleX, DisplayPanel.CompositionScaleY);
        // Colore di prova: il grigio-blu dell'icona. In M3 qui si disegna la superficie primaria.
        _presenter.Clear(0.13f, 0.18f, 0.27f);
    }
}

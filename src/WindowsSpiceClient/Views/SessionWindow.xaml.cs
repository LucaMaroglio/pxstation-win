using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpiceKit.Proxmox;
using SpiceKit.VVFile;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Display;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>
/// Finestra di una sessione. In M1 mostra il profilo di connessione letto (senza segreti) e
/// prepara la superficie Direct3D 11; il protocollo SPICE arriva con M2/M3.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "Una Window non è IDisposable: presenter e ticket vengono rilasciati nell'evento Closed.")]
public sealed partial class SessionWindow : Window
{
    private readonly D3D11Presenter? _presenter;

    internal SessionWindow(ConnectionProfile? profile, ProxmoxVMRef? reference, string? vmName)
    {
        InitializeComponent();
        AppTitleBar.Title = vmName ?? profile?.DisplayTitle ?? AppPaths.AppName;
        WindowChrome.Apply(this, Root, AppTitleBar, 1024, 720, minWidth: 480, minHeight: 360);
        ProfileInfo.Message = profile is null ? "" : Summary(profile, reference);
        // Finché non c'è il protocollo il ticket non serve: lo si azzera subito.
        profile?.Password?.Wipe();

        // Diagnostica: WSC_RENDERER=none esclude Direct3D (per distinguere problemi del renderer da quelli di WinUI).
        if (string.Equals(Environment.GetEnvironmentVariable("WSC_RENDERER"), "none", StringComparison.OrdinalIgnoreCase))
        {
            RendererInfo.Message = "Renderer disattivato (WSC_RENDERER=none)";
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

    internal Grid RootElement => Root;

    /// <summary>Riepilogo senza ticket né CA; l'host Proxmox (che contiene un ticket del proxy) non viene mostrato.</summary>
    private static string Summary(ConnectionProfile profile, ProxmoxVMRef? reference)
    {
        var parts = new List<string>();
        if (reference is not null)
        {
            parts.Add(Strings.Format("SummaryProxmox", reference.VmId, reference.Node, reference.ApiHost));
        }
        else
        {
            parts.Add(Strings.Format("SummaryHost", profile.Host));
        }
        if (profile.TlsPort is { } tls) parts.Add(Strings.Format("SummaryTls", tls));
        if (profile.Port is { } plain) parts.Add(Strings.Format("SummaryPlain", plain));
        if (profile.Proxy is { } proxy) parts.Add(Strings.Format("SummaryProxy", proxy.Host, proxy.Port));
        if (profile.HostSubject is not null || profile.CaPem is not null) parts.Add(Strings.Get("SummaryVerified"));
        return string.Join(" · ", parts);
    }

    /// <summary>Verifica del renderer per l'autoverifica: clear di colore noto e rilettura del pixel centrale.</summary>
    internal string ProbeRenderer()
    {
        if (_presenter is null) return $"direct3d: non disponibile ({RendererInfo.Message})";
        // Colore saturo e riconoscibile: (R 1.0, G 0.5, B 0.0) → 0xFFFF8000 (G 127 o 128).
        var pixel = _presenter.ClearAndProbe(1f, 0.5f, 0f);
        var ok = (pixel & 0xFFFF0000) == 0xFFFF0000 && ((pixel >> 8) & 0xFF) is 127 or 128 && (pixel & 0xFF) == 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"direct3d: {(ok ? "OK" : "ERRORE")} pixel centrale 0x{pixel:X8} (atteso 0xFFFF7F00/0xFFFF8000) · {RendererInfo.Message}");
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e) => Render();

    private void Render()
    {
        if (_presenter is null) return;
        var width = DisplayPanel.ActualWidth;
        var height = DisplayPanel.ActualHeight;
        if (width < 1 || height < 1) return;
        _presenter.Resize(width, height, DisplayPanel.CompositionScaleX, DisplayPanel.CompositionScaleY);
        // Colore di prova: il grigio-blu dell'icona. In M3 qui si disegna la superficie primaria.
        _presenter.Clear(0.13f, 0.18f, 0.27f);
    }
}

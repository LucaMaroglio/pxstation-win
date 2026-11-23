using Microsoft.UI.Xaml;
using WindowsSpiceClient.Diagnostics;

namespace WindowsSpiceClient;

/// <summary>
/// Punto d'ingresso dell'app. In M0 apre solo la finestra di diagnostica del renderer;
/// benvenuto, istanza unica e apertura dei .vv arrivano con M1 e M9.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => AppLog.Error("Eccezione non gestita (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Eccezione non gestita", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("Task non osservato", e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Info($"Avvio {typeof(App).Assembly.GetName().Version} su {Environment.OSVersion.VersionString}");
        _window = new MainWindow();
        _window.Activate();
    }
}

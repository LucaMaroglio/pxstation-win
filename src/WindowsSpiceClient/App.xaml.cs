using Microsoft.UI.Xaml;
using SpiceKit.Proxmox;
using SpiceKit.VVFile;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;
using WindowsSpiceClient.Views;

namespace WindowsSpiceClient;

/// <summary>
/// Punto d'ingresso e gestione delle finestre: Benvenuto, Macchine virtuali, Preferenze e
/// sessioni. All'avvio apre i <c>.vv</c> passati sulla riga di comando, altrimenti Macchine
/// virtuali (se ci sono server) o Benvenuto, come i client Linux e macOS.
/// </summary>
public partial class App : Application
{
    private WelcomeWindow? _welcome;
    private VmListWindow? _vmList;
    private PreferencesWindow? _preferences;
    private readonly List<SessionWindow> _sessions = [];

    internal AppController Controller { get; }

    public App()
    {
        InitializeComponent();
        Controller = new AppController(this);
        // L'autoverifica apre e chiude le finestre una alla volta: l'app termina solo con Exit().
        if (SelfTest.IsEnabled) DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => AppLog.Error("Eccezione non gestita (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Eccezione non gestita", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("Task non osservato", e.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Info($"Avvio {AppPaths.Version} su {Environment.OSVersion.VersionString}");
        if (SelfTest.IsEnabled)
        {
            await SelfTest.RunAsync(this);
            Exit();
            return;
        }

        var files = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => a.EndsWith(FileAssociation.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a)).ToList();
        var opened = false;
        foreach (var file in files) opened |= await Controller.OpenPathAsync(file, owner: null);
        if (!opened) Activate();
    }

    /// <summary>Macchine virtuali se ci sono server, altrimenti Benvenuto.</summary>
    internal void Activate()
    {
        if (Controller.Store.Servers.Count > 0) ShowVmList();
        else ShowWelcome();
    }

    internal WelcomeWindow ShowWelcome()
    {
        _welcome ??= Track(new WelcomeWindow(this), () => _welcome = null);
        WindowChrome.BringToFront(_welcome);
        return _welcome;
    }

    internal void CloseWelcome() => _welcome?.Close();

    internal VmListWindow ShowVmList()
    {
        _vmList ??= Track(new VmListWindow(this), () => _vmList = null);
        WindowChrome.BringToFront(_vmList);
        return _vmList;
    }

    internal PreferencesWindow ShowPreferences(PreferencesPage page = PreferencesPage.General, string? addHost = null)
    {
        _preferences ??= Track(new PreferencesWindow(this), () => _preferences = null);
        _preferences.ShowPage(page);
        WindowChrome.BringToFront(_preferences);
        if (addHost is not null && Controller.Store.ServerForHost(addHost) is null)
        {
            _ = _preferences.EditAsync(null, addHost);
        }
        return _preferences;
    }

    internal SessionWindow OpenSession(ConnectionProfile profile, ProxmoxVMRef? reference, string? vmName)
    {
        var window = new SessionWindow(profile, reference, vmName);
        _sessions.Add(window);
        window.Closed += (_, _) => _sessions.Remove(window);
        window.Activate();
        return window;
    }

    /// <summary>Mostra un errore sulla finestra indicata, o su una finestra aperta, o su Benvenuto.</summary>
    internal async Task ShowErrorAsync(Window? owner, string title, string text)
    {
        var window = owner ?? (Window?)_vmList ?? (Window?)_welcome ?? (Window?)_preferences ?? ShowWelcome();
        if (window.Content?.XamlRoot is not { } root)
        {
            var loaded = new TaskCompletionSource();
            ((FrameworkElement)window.Content!).Loaded += (_, _) => loaded.TrySetResult();
            await loaded.Task;
            root = window.Content.XamlRoot;
        }
        await Dialogs.MessageAsync(root, title, text);
    }

    internal void Quit()
    {
        foreach (var session in _sessions.ToList()) session.Close();
        _preferences?.Close();
        _welcome?.Close();
        _vmList?.Close();
        Exit();
    }

    private static T Track<T>(T window, Action onClosed) where T : Window
    {
        window.Closed += (_, _) => onClosed();
        return window;
    }
}

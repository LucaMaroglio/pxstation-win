using Microsoft.UI.Xaml;
using SpiceKit.Proxmox;
using SpiceKit.VVFile;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;
using WindowsSpiceClient.Shell;
using WindowsSpiceClient.Views;

namespace WindowsSpiceClient;

/// <summary>
/// Punto d'ingresso e gestione delle finestre: Benvenuto, Macchine virtuali, Preferenze e
/// sessioni. All'avvio apre i <c>.vv</c> passati sulla riga di comando, altrimenti Macchine
/// virtuali (se ci sono server) o Benvenuto, come i client Linux e macOS.
/// </summary>
/// <remarks>
/// Integrazione con Windows (M9): icona nell'area di notifica con le sessioni, notifiche, attività
/// della jump list. Con l'icona attiva il programma resta in esecuzione anche senza finestre; senza,
/// termina alla chiusura dell'ultima finestra.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "L'Application vive quanto il processo: l'icona si rilascia in Shutdown() o quando la si spegne.")]
public partial class App : Application
{
    private WelcomeWindow? _welcome;
    private VmListWindow? _vmList;
    private PreferencesWindow? _preferences;
    private readonly List<SessionWindow> _sessions = [];
    private TrayIcon? _tray;
    private bool _quitting;

    internal AppController Controller { get; }

    /// <summary>Finestre di sessione aperte (l'ultima è la più recente).</summary>
    internal IReadOnlyList<SessionWindow> Sessions => _sessions;

    /// <summary>Icona nell'area di notifica, se attiva (per l'autoverifica).</summary>
    internal TrayIcon? Tray => _tray;

    public App()
    {
        InitializeComponent();
        AppLog.AttachSpiceKit();
        Controller = new AppController(this);
        // L'uscita la decide l'app: l'icona nell'area di notifica può tenerla aperta senza finestre.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        Controller.Notifier.Invoked += OnNotificationInvoked;
        UnhandledException += (_, e) => AppLog.Error("Eccezione non gestita (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Eccezione non gestita", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("Task non osservato", e.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Info($"Avvio {AppPaths.Version} su {Environment.OSVersion.VersionString}");
        UpdateTray();
        if (SelfTest.IsEnabled)
        {
            await SelfTest.RunAsync(this);
            Shutdown();
            return;
        }
        if (!AppPaths.IsIsolated) JumpList.Publish();
        await HandleAsync(CommandLine.Parse(Environment.GetCommandLineArgs().Skip(1)));
    }

    /// <summary>Avvio, secondo avvio (istanza unica) o attività della jump list.</summary>
    internal async Task HandleAsync(LaunchRequest request)
    {
        var opened = false;
        foreach (var file in request.Files) opened |= await Controller.OpenPathAsync(file, owner: null);
        if (opened) return;
        switch (request.Command)
        {
            case CommandLine.VmList:
                ShowVmList();
                break;
            case CommandLine.Preferences:
                ShowPreferences();
                break;
            case CommandLine.OpenFile:
                await ChooseAndOpenAsync();
                break;
            default:
                Activate();
                break;
        }
    }

    /// <summary>Macchine virtuali se ci sono server, altrimenti Benvenuto.</summary>
    internal Window Activate() => Controller.Store.Servers.Count > 0 ? ShowVmList() : ShowWelcome();

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

    /// <summary>«Apri file di connessione…» dall'icona o dalla jump list: la scelta ha bisogno di una finestra.</summary>
    internal Task ChooseAndOpenAsync()
    {
        var owner = (Window?)_vmList ?? (Window?)_welcome ?? Activate();
        WindowChrome.BringToFront(owner);
        return Controller.ChooseAndOpenAsync(owner);
    }

    internal SessionWindow OpenSession(ConnectionProfile profile, ProxmoxVMRef? reference, string? vmName)
    {
        // Schermo intero se lo chiede il .vv o le Preferenze (mai nell'autoverifica, che legge le finestre).
        var fullScreen = !SelfTest.IsEnabled && (profile.Fullscreen || Settings.Load().OpenFullScreen);
        var window = new SessionWindow(profile, reference, vmName, fullScreen);
        _sessions.Add(window);
        window.StatusChanged += (_, _) => UpdateTooltip();
        window.Closed += (_, _) =>
        {
            _sessions.Remove(window);
            Controller.Notifier.Withdraw(window.Id);
            UpdateTooltip();
            ExitWhenIdle();
        };
        window.Activate();
        UpdateTooltip();
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

    // MARK: area di notifica

    /// <summary>Crea o toglie l'icona secondo le preferenze (anche quando cambiano).</summary>
    internal void UpdateTray()
    {
        if (Controller.Settings.ShowTrayIcon && _tray is null && !_quitting)
        {
            try
            {
                _tray = new TrayIcon(TrayEntries, OnTraySelected);
                UpdateTooltip();
            }
            catch (InvalidOperationException error)
            {
                AppLog.Error("Icona nell'area di notifica non creata", error);
            }
        }
        else if (!Controller.Settings.ShowTrayIcon && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
            ExitWhenIdle();
        }
    }

    /// <summary>Menu dell'icona, come <c>tray_entries</c> del client Linux.</summary>
    internal IReadOnlyList<MenuEntry> TrayEntries()
    {
        var entries = new List<MenuEntry>();
        if (_sessions.Count > 0)
        {
            entries.Add(MenuEntry.Info(Strings.Get("TraySessions")));
            entries.AddRange(_sessions.Select(s => s.TrayEntry()));
            entries.Add(MenuEntry.Separator());
        }
        entries.Add(new MenuEntry(Strings.Get("TrayVmList"), () => ShowVmList()));
        entries.Add(new MenuEntry(Strings.Get("TrayOpenFile"), () => _ = ChooseAndOpenAsync()));
        entries.Add(new MenuEntry(Strings.Get("TrayPreferences"), () => ShowPreferences()));
        entries.Add(MenuEntry.Separator());
        entries.Add(new MenuEntry(Strings.Get("TrayQuit"), () => _ = QuitAsync()));
        return entries;
    }

    /// <summary>Testo al passaggio del mouse: il nome, più le sessioni attive se ce ne sono.</summary>
    internal string TrayTooltip()
    {
        var active = _sessions.Count(s => s.IsActive);
        return active > 0 ? Strings.Format("TrayTooltipActive", AppPaths.AppName, active) : AppPaths.AppName;
    }

    private void UpdateTooltip()
    {
        if (_tray is not null) _tray.Tooltip = TrayTooltip();
    }

    /// <summary>Clic sull'icona: la sessione più recente, altrimenti la finestra principale.</summary>
    private void OnTraySelected()
    {
        if (_sessions.Count > 0) WindowChrome.BringToFront(_sessions[^1]);
        else Activate();
    }

    private void OnNotificationInvoked(int session, string? action)
    {
        if (_sessions.FirstOrDefault(s => s.Id == session) is { } window) window.RunAction(action);
        else Activate(); // sessione già chiusa (o notifica di un'esecuzione precedente)
    }

    // MARK: uscita

    /// <summary>«Esci»: con sessioni attive chiede conferma, poi chiude tutto.</summary>
    internal async Task QuitAsync()
    {
        var active = _sessions.Where(s => s.IsActive).ToList();
        if (active.Count > 0)
        {
            var window = active[^1];
            WindowChrome.BringToFront(window);
            if (!await Dialogs.ConfirmAsync(window.RootElement.XamlRoot, Strings.Get("QuitTitle"),
                    Strings.Format("QuitText", active.Count), Strings.Get("QuitAccept")))
            {
                return;
            }
        }
        Quit();
    }

    internal void Quit()
    {
        _quitting = true;
        foreach (var session in _sessions.ToList()) session.Close();
        _preferences?.Close();
        _welcome?.Close();
        _vmList?.Close();
        Shutdown();
    }

    private void Shutdown()
    {
        _quitting = true;
        _tray?.Dispose();
        _tray = null;
        Controller.Notifier.Shutdown();
        Exit();
    }

    private bool HasWindows => _sessions.Count > 0 || _welcome is not null || _vmList is not null || _preferences is not null;

    /// <summary>Senza finestre e senza icona non resta nulla con cui usare l'app: si esce.</summary>
    private void ExitWhenIdle()
    {
        // Dopo gli altri gestori di Closed (che azzerano i riferimenti alle finestre).
        DispatcherQueueSafe(() =>
        {
            if (!_quitting && !SelfTest.IsEnabled && _tray is null && !HasWindows) Shutdown();
        });
    }

    private static void DispatcherQueueSafe(Action action) =>
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() => action());

    private T Track<T>(T window, Action onClosed) where T : Window
    {
        window.Closed += (_, _) =>
        {
            onClosed();
            ExitWhenIdle();
        };
        return window;
    }
}

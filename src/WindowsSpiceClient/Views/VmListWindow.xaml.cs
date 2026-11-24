using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SpiceKit.Proxmox;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>
/// "Macchine virtuali": le VM di ogni server Proxmox configurato, con alimentazione e
/// connessione con un clic. Stessa logica della finestra del client Linux.
/// </summary>
public sealed partial class VmListWindow : Window
{
    private readonly App _app;
    private readonly ObservableCollection<ServerSectionModel> _sections = [];
    private readonly VmListLoader _loader;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };

    internal VmListWindow(App app)
    {
        _app = app;
        InitializeComponent();
        WindowChrome.Apply(this, Root, AppTitleBar, 680, 600, minWidth: 480, minHeight: 360);
        Sections.ItemsSource = _sections;

        _loader = new VmListLoader(app.Controller.Store, DispatcherQueue, OnResult, loading =>
        {
            IsLoading = loading;
            RefreshButton.IsEnabled = !loading;
        });
        _timer.Tick += (_, _) => Reload();
        app.Controller.Store.Changed += OnServersChanged;
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            _timer.Stop();
            app.Controller.Store.Changed -= OnServersChanged;
        };
        RebuildSections();
    }

    internal Grid RootElement => Root;

    /// <summary>Vero mentre un aggiornamento dell'elenco è in corso.</summary>
    internal bool IsLoading { get; private set; }

    /// <summary>Ricarica subito e poi ogni 10 secondi (come gli altri client).</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_timer.IsEnabled) return;
        Reload();
        _timer.Start();
    }

    internal void Reload() => _loader.Reload();

    private void OnServersChanged(object? sender, EventArgs e)
    {
        RebuildSections();
        Reload();
    }

    private void RebuildSections()
    {
        var servers = _app.Controller.Store.Servers;
        if (!servers.SequenceEqual(_sections.Select(s => s.Server)))
        {
            _sections.Clear();
            foreach (var server in servers) _sections.Add(new ServerSectionModel(server));
        }
        EmptyState.Visibility = servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListState.Visibility = servers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnResult(VmListResult result)
    {
        var section = _sections.FirstOrDefault(s => s.Server.Id == result.ServerId);
        if (section is null) return;
        if (result.Error is not null) section.SetError(result.Error);
        else if (result.VMs is not null) section.SetVms(result.VMs);
    }

    internal IReadOnlyList<ServerSectionModel> SectionModels => _sections;

    // MARK: Comandi

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private async void OnOpenFile(object sender, RoutedEventArgs e) => await _app.Controller.ChooseAndOpenAsync(this);

    private void OnPreferences(object sender, RoutedEventArgs e) => _app.ShowPreferences();

    private void OnConfigureServers(object sender, RoutedEventArgs e) => _app.ShowPreferences(PreferencesPage.Proxmox);

    private async void OnAbout(object sender, RoutedEventArgs e) => await Dialogs.AboutAsync(Root.XamlRoot);

    private async void OnQuit(object sender, RoutedEventArgs e) => await _app.QuitAsync();

    // MARK: Righe

    private static VmRowModel? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as VmRowModel;

    private void ShowError(string? text)
    {
        ErrorBar.Message = text ?? "";
        ErrorBar.IsOpen = text is not null;
    }

    private async void OnRowButton(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        await RunAsync(row, () => _app.Controller.ConnectVmAsync(row.Server, row.Vm));
    }

    private void OnPowerButton(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) PowerMenu(row).ShowAt((FrameworkElement)sender, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private void OnRowContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (RowOf(sender) is not { IsIdle: true } row) return;
        var options = new FlyoutShowOptions();
        if (args.TryGetPosition(sender, out var point)) options.Position = point;
        PowerMenu(row).ShowAt(sender, options);
        args.Handled = true;
    }

    private MenuFlyout PowerMenu(VmRowModel row)
    {
        var menu = new MenuFlyout();
        foreach (var action in PowerActions.For(row.Vm.Status))
        {
            if (action.IsDestructive() && menu.Items.Count > 0 && menu.Items[^1] is not MenuFlyoutSeparator)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            var item = new MenuFlyoutItem { Text = action.Title(), Icon = new FontIcon { Glyph = Glyph(action) } };
            item.Click += async (_, _) => await RunPowerAsync(row, action);
            menu.Items.Add(item);
        }
        return menu;
    }

    private static string Glyph(PowerAction action) => action switch
    {
        PowerAction.Start or PowerAction.Resume => "",
        PowerAction.Shutdown => "",
        PowerAction.Reboot => "",
        PowerAction.Reset => "",
        PowerAction.Stop => "",
        PowerAction.Suspend => "",
        _ => "",
    };

    private async Task RunPowerAsync(VmRowModel row, PowerAction action)
    {
        if (!await AppController.ConfirmPowerAsync(Root.XamlRoot, action, row.Vm.Name)) return;
        await RunAsync(row, () => AppController.PowerAsync(row.Server, row.Vm, action));
    }

    private async Task RunAsync(VmRowModel row, Func<Task> work)
    {
        row.Busy = true;
        ShowError(null);
        try
        {
            await work();
        }
        catch (Exception error) when (error is ProxmoxException or SpiceKit.VVFile.VVFileException or System.ComponentModel.Win32Exception)
        {
            ShowError($"{row.Vm.Name}: {AppController.ErrorText(error)}");
        }
        finally
        {
            row.Busy = false;
            Reload();
        }
    }
}

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SpiceKit.Proxmox;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

public enum PreferencesPage
{
    General,
    Proxmox,
}

/// <summary>Voce dell'elenco dei server.</summary>
public sealed record ServerItem(string Id, string Name, string Detail);

/// <summary>Preferenze: Generale e Proxmox. Stesse opzioni e testi dei client Linux e macOS.</summary>
public sealed partial class PreferencesWindow : Window
{
    private readonly App _app;
    private readonly ObservableCollection<ServerItem> _servers = [];
    private bool _loading;

    internal PreferencesWindow(App app)
    {
        _app = app;
        InitializeComponent();
        WindowChrome.Apply(this, Root, AppTitleBar, 860, 680, minWidth: 640, minHeight: 480);
        ServerList.ItemsSource = _servers;

        _loading = true;
        var settings = app.Controller.Settings;
        ReconnectSwitch.IsOn = settings.ReconnectAutomatically;
        FullScreenSwitch.IsOn = settings.OpenFullScreen;
        TraySwitch.IsOn = settings.ShowTrayIcon;
        NotificationsSwitch.IsOn = settings.Notifications;
        _loading = false;

        UpdateAssociation();
        RefreshServers();
        app.Controller.Store.Changed += OnServersChanged;
        Closed += (_, _) => app.Controller.Store.Changed -= OnServersChanged;
        Navigation.SelectedItem = GeneralItem;
    }

    internal Grid RootElement => Root;

    public void ShowPage(PreferencesPage page) =>
        Navigation.SelectedItem = page == PreferencesPage.Proxmox ? ProxmoxItem : GeneralItem;

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var proxmox = ReferenceEquals(args.SelectedItem, ProxmoxItem);
        GeneralPage.Visibility = proxmox ? Visibility.Collapsed : Visibility.Visible;
        ProxmoxPage.Visibility = proxmox ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: Generale

    private void OnSettingToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var settings = _app.Controller.Settings;
        settings.ReconnectAutomatically = ReconnectSwitch.IsOn;
        settings.OpenFullScreen = FullScreenSwitch.IsOn;
        settings.ShowTrayIcon = TraySwitch.IsOn;
        settings.Notifications = NotificationsSwitch.IsOn;
        try
        {
            settings.Save();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Salvataggio delle impostazioni non riuscito", error);
        }
        _app.UpdateTray();
    }

    private void UpdateAssociation()
    {
        var isDefault = FileAssociation.IsDefault();
        AssociationText.Text = isDefault
            ? Strings.Get("VVHandlerIsDefault")
            : Strings.Format("VVHandlerOther", FileAssociation.CurrentHandlerName() ?? Strings.Get("NoProgram"));
        AssociationButton.IsEnabled = !isDefault;
    }

    private async void OnMakeDefault(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociation.Register(WindowChrome.Handle(this));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            await Dialogs.MessageAsync(Root.XamlRoot, Strings.Get("AssociationFailedTitle"), error.Message);
        }
        UpdateAssociation();
    }

    // MARK: Proxmox

    private void OnServersChanged(object? sender, EventArgs e) => RefreshServers();

    private void RefreshServers()
    {
        var selected = (ServerList.SelectedItem as ServerItem)?.Id;
        _servers.Clear();
        foreach (var server in _app.Controller.Store.Servers)
        {
            _servers.Add(new ServerItem(server.Id, server.Name, $"{server.TokenId} · {server.Host}:{server.Port}"));
        }
        ServerList.SelectedItem = _servers.FirstOrDefault(s => s.Id == selected);
        NoServersText.Visibility = _servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private ProxmoxServer? Selected =>
        ServerList.SelectedItem is ServerItem item ? _app.Controller.Store.ServerById(item.Id) : null;

    private void UpdateButtons()
    {
        var selected = Selected is not null;
        EditServerButton.IsEnabled = selected;
        RemoveServerButton.IsEnabled = selected;
    }

    private void OnServerSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void OnServerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Selected is { } server) await EditAsync(server);
    }

    private async void OnAddServer(object sender, RoutedEventArgs e) => await EditAsync(null);

    private async void OnEditServer(object sender, RoutedEventArgs e)
    {
        if (Selected is { } server) await EditAsync(server);
    }

    private async void OnRemoveServer(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } server) return;
        if (!await Dialogs.ConfirmAsync(Root.XamlRoot, Strings.Get("RemoveServerTitle"),
                Strings.Format("RemoveServerText", server.Name), Strings.Get("Remove")))
        {
            return;
        }
        try
        {
            _app.Controller.Store.Remove(server);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            await Dialogs.MessageAsync(Root.XamlRoot, Strings.Get("RemoveServerTitle"), AppController.ErrorText(error));
        }
    }

    /// <summary>Apre l'editor; con <paramref name="host"/> precompila un nuovo server (es. dalla sessione).</summary>
    internal async Task EditAsync(ProxmoxServer? server, string? host = null)
    {
        var isNew = server is null;
        server ??= new ProxmoxServer { Host = host ?? "" };
        var dialog = new ServerEditorDialog(_app.Controller, server, isNew) { XamlRoot = Root.XamlRoot };
        await Dialogs.ShowAsync(dialog);
    }
}

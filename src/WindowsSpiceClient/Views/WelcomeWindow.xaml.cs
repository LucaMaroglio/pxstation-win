using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>Benvenuto: mostrato quando non ci sono server Proxmox né file da aprire.</summary>
public sealed partial class WelcomeWindow : Window
{
    private readonly App _app;

    internal WelcomeWindow(App app)
    {
        _app = app;
        InitializeComponent();
        AppNameText.Text = AppPaths.AppName;
        WindowChrome.Apply(this, Root, AppTitleBar, 520, 460, resizable: false);
        Root.Loaded += (_, _) => OpenButton.Focus(FocusState.Programmatic);
    }

    internal Microsoft.UI.Xaml.Controls.Grid RootElement => Root;

    private async void OnOpen(object sender, RoutedEventArgs e) => await _app.Controller.ChooseAndOpenAsync(this);

    private void OnVirtualMachines(object sender, RoutedEventArgs e)
    {
        _app.ShowVmList();
        Close();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Link;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        foreach (var item in await e.DataView.GetStorageItemsAsync())
        {
            if (item is StorageFile file && file.FileType.Equals(FileAssociation.Extension, StringComparison.OrdinalIgnoreCase))
            {
                await _app.Controller.OpenPathAsync(file.Path, this);
            }
        }
    }
}

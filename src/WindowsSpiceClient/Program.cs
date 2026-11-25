using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WindowsSpiceClient.Shell;

namespace WindowsSpiceClient;

/// <summary>
/// Punto d'ingresso al posto di quello generato da XAML (<c>DISABLE_XAML_GENERATED_MAIN</c>):
/// prima di creare l'app si controlla l'istanza unica.
/// </summary>
internal static partial class Program
{
    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Stesso AppUserModelID del collegamento dell'installer: finestre, icona aggiunta alla barra
        // delle applicazioni e jump list restano un'unica app.
        _ = SetCurrentProcessExplicitAppUserModelID(Services.AppPaths.AppId);
        if (Environment.GetCommandLineArgs().Contains(CommandLine.Unregister, StringComparer.OrdinalIgnoreCase))
        {
            // Disinstallazione (Inno Setup): via la registrazione delle notifiche e la jump list.
            Notifier.UnregisterAll();
            JumpList.Delete();
            return 0;
        }
        if (SingleInstance.RedirectIfAlreadyRunning()) return 0;
        // Prima di creare l'app: risorse x:Uid e Strings seguono la lingua scelta (vale al riavvio).
        Diagnostics.AppLog.Info($"lingua: {Services.Language.Apply(Services.Settings.Load())}");
        Application.Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            var app = new App();
            SingleInstance.Listen(app, dispatcher);
        });
        return 0;
    }

    [System.Runtime.InteropServices.LibraryImport("shell32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}

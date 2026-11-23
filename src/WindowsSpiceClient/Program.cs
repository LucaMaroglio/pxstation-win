using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WindowsSpiceClient.Shell;

namespace WindowsSpiceClient;

/// <summary>
/// Punto d'ingresso al posto di quello generato da XAML (<c>DISABLE_XAML_GENERATED_MAIN</c>):
/// prima di creare l'app si controlla l'istanza unica.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (SingleInstance.RedirectIfAlreadyRunning()) return 0;
        Application.Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            var app = new App();
            SingleInstance.Listen(app, dispatcher);
        });
        return 0;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;
using Windows.ApplicationModel.Activation;

namespace WindowsSpiceClient.Shell;

/// <summary>
/// Istanza unica (come i client Linux e macOS): un secondo avvio, per esempio un <c>.vv</c>
/// aperto dal browser, passa i suoi argomenti all'istanza già in esecuzione e termina.
/// </summary>
/// <remarks>
/// Usa <see cref="AppInstance"/> del Windows App SDK. L'autoverifica e le configurazioni isolate
/// (<c>WSC_DATA_DIR</c>) non partecipano: possono girare accanto all'app normale.
/// </remarks>
internal static partial class SingleInstance
{
    private const string Key = "main";

    /// <summary><see langword="true"/> se l'attivazione è stata passata a un'altra istanza: allora si esce.</summary>
    public static bool RedirectIfAlreadyRunning()
    {
        if (SelfTest.IsEnabled || AppPaths.IsIsolated) return false;
        var main = AppInstance.FindOrRegisterForKey(Key);
        if (main.IsCurrent) return false;

        var arguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        // L'istanza principale potrà portare in primo piano le sue finestre.
        try
        {
            AllowSetForegroundWindow((int)main.ProcessId);
        }
        catch (ArgumentException)
        {
        }
        // Il reindirizzamento richiede COM: si attende senza bloccare il thread STA.
        using var done = new ManualResetEvent(false);
        _ = Task.Run(() =>
        {
            try
            {
                main.RedirectActivationToAsync(arguments).AsTask().Wait();
            }
            catch (AggregateException error)
            {
                AppLog.Error("Reindirizzamento all'istanza principale non riuscito", error);
            }
            finally
            {
                done.Set();
            }
        });
        _ = CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
        return true;
    }

    /// <summary>Nell'istanza principale: le attivazioni successive aprono i loro file sul thread dell'interfaccia.</summary>
    public static void Listen(App app, DispatcherQueue dispatcher)
    {
        if (SelfTest.IsEnabled || AppPaths.IsIsolated) return;
        AppInstance.GetCurrent().Activated += (_, arguments) =>
        {
            var commandLine = arguments.Kind == ExtendedActivationKind.Launch && arguments.Data is ILaunchActivatedEventArgs launch
                ? launch.Arguments
                : string.Empty;
            var request = CommandLine.Parse(commandLine);
            AppLog.Info($"Attivazione da un'altra istanza: {request.Files.Count} file .vv, comando {request.Command ?? "nessuno"}");
            dispatcher.TryEnqueue(async () => await app.HandleAsync(request));
        };
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);
}

using Microsoft.UI.Xaml;
using SpiceKit.Proxmox;
using SpiceKit.VVFile;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Views;

namespace WindowsSpiceClient.Services;

/// <summary>
/// Logica condivisa dalle finestre: apertura dei file di connessione, connessione alle VM,
/// alimentazione. Porting di <c>Controller</c> (Linux); le finestre si limitano a disegnare.
/// </summary>
internal sealed class AppController(App app)
{
    public Settings Settings { get; } = Settings.Load();
    public ServerStore Store { get; } = new();
    public Shell.Notifier Notifier { get; } = new(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

    // MARK: File di connessione

    /// <summary>Chiede un file <c>.vv</c> e lo apre.</summary>
    public async Task ChooseAndOpenAsync(Window owner)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(owner.AppWindow.Id)
        {
            CommitButtonText = Strings.Get("OpenCommit"),
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(FileAssociation.Extension);
        var result = await picker.PickSingleFileAsync();
        if (result is not null) await OpenPathAsync(result.Path, owner);
    }

    /// <summary>Legge un <c>.vv</c> e apre subito la sessione (i ticket Proxmox scadono in ~30 s).</summary>
    public async Task<bool> OpenPathAsync(string path, Window? owner)
    {
        VVFile file;
        try
        {
            file = VVFileParser.ReadFile(path);
        }
        catch (Exception error) when (error is VVFileException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Apertura di {Path.GetFileName(path)} non riuscita", error);
            await app.ShowErrorAsync(owner, Strings.Get("OpenFailedTitle"), error.Message);
            return false;
        }

        if (file.Profile.DeleteThisFile)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AppLog.Info($"delete-this-file: impossibile eliminare il file ({error.Message})");
            }
        }

        AppLog.Info($"File di connessione aperto: {file.Profile} ({file.Warnings.Count} avvisi)");
        app.OpenSession(file.Profile, ProxmoxVMRef.From(file.Profile), vmName: null);
        app.CloseWelcome();
        return true;
    }

    // MARK: Macchine virtuali

    /// <summary>Etichetta e tipo del pulsante di una riga (le sessioni attive arrivano con M2).</summary>
    public static (string Label, string Kind) RowAction(ProxmoxVM vm) =>
        vm.IsRunning ? (Strings.Get("Connect"), "connect")
        : vm.IsPaused ? (Strings.Get("ResumeAndConnect"), "connect")
        : (Strings.Get("StartAndConnect"), "connect");

    /// <summary>Avvia la VM se serve, ottiene una connessione nuova e apre la sessione.</summary>
    public async Task ConnectVmAsync(ProxmoxServer server, ProxmoxVM vm)
    {
        var text = await Task.Run(async () =>
        {
            using var client = ServerStore.Client(server);
            if (!vm.IsRunning)
            {
                await client.PerformAsync(vm.IsPaused ? PowerAction.Resume : PowerAction.Start, vm.Node, vm.VmId).ConfigureAwait(false);
                await client.WaitUntilRunningAsync(vm.Node, vm.VmId).ConfigureAwait(false);
            }
            return await client.SpiceConnectionFileAsync(vm.Node, vm.VmId).ConfigureAwait(false);
        });
        var profile = VVFileParser.Parse(text).Profile;
        Store.LearnCa(server, profile);
        app.OpenSession(profile, new ProxmoxVMRef(server.Host, vm.Node, vm.VmId), vm.Name);
    }

    /// <summary>Chiede conferma prima delle azioni che staccano il sistema ospite.</summary>
    internal static Task<bool> ConfirmPowerAsync(XamlRoot root, PowerAction action, string vmName)
    {
        if (!action.IsDestructive()) return Task.FromResult(true);
        var reset = action == PowerAction.Reset;
        return Dialogs.ConfirmAsync(root,
            Strings.Format(reset ? "ConfirmResetTitle" : "ConfirmStopTitle", vmName),
            Strings.Get("ConfirmPowerText"),
            Strings.Get(reset ? "ConfirmResetAccept" : "ConfirmStopAccept"));
    }

    public static Task PowerAsync(ProxmoxServer server, ProxmoxVM vm, PowerAction action) => Task.Run(async () =>
    {
        using var client = ServerStore.Client(server);
        await client.PerformAsync(action, vm.Node, vm.VmId).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false); // Proxmox aggiorna lo stato con un attimo di ritardo
    });

    /// <summary>Prova le impostazioni di un server elencandone le VM (editor dei server).</summary>
    public static Task<int> TestConnectionAsync(ProxmoxServer candidate, string typedSecret) => Task.Run(async () =>
    {
        var secret = typedSecret.Length > 0 ? typedSecret : CredentialStore.Lookup(candidate.Id) ?? "";
        using var client = new ProxmoxClient(candidate, secret);
        return (await client.VirtualMachinesAsync().ConfigureAwait(false)).Count;
    });

    /// <summary>Il candidato con la CA del cluster già nota per quell'host, se non ne ha una.</summary>
    public ProxmoxServer WithKnownCa(ProxmoxServer candidate) =>
        candidate.ClusterCaPem is not null ? candidate
        : candidate with { ClusterCaPem = Store.ServerForHost(candidate.Host)?.ClusterCaPem ?? Store.KnownCa(candidate.Host) };

    /// <summary>Messaggio per l'utente di un errore inatteso o tipizzato.</summary>
    public static string ErrorText(Exception error) => error switch
    {
        ProxmoxException or VVFileException => error.Message,
        System.ComponentModel.Win32Exception => Strings.Format("CredentialError", error.Message),
        _ => error.Message,
    };
}

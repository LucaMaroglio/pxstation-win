using Microsoft.UI.Dispatching;
using SpiceKit.Proxmox;
using WindowsSpiceClient.Diagnostics;

namespace WindowsSpiceClient.Services;

/// <summary>Esito del caricamento delle VM di un server.</summary>
internal sealed record VmListResult(string ServerId, IReadOnlyList<ProxmoxVM>? VMs, string? Error);

/// <summary>
/// Carica le VM di tutti i server, un aggiornamento alla volta (una richiesta durante un
/// aggiornamento viene eseguita una volta dopo). I risultati arrivano sul thread
/// dell'interfaccia: prima l'elenco semplice, poi con gli stati di pausa. Porting di
/// <c>VMListLoader</c> (Linux).
/// </summary>
internal sealed class VmListLoader(ServerStore store, DispatcherQueue dispatcher, Action<VmListResult> onResult, Action<bool> onLoading)
{
    private bool _loading;
    private bool _again;

    public void Reload()
    {
        if (_loading)
        {
            _again = true;
            return;
        }
        var servers = store.Servers.ToList();
        if (servers.Count == 0) return;
        _loading = true;
        _again = false;
        onLoading(true);
        _ = RunAsync(servers);
    }

    private async Task RunAsync(List<ProxmoxServer> servers)
    {
        await Task.Run(async () =>
        {
            foreach (var server in servers)
            {
                try
                {
                    using var client = ServerStore.Client(server);
                    var vms = await client.VirtualMachinesAsync().ConfigureAwait(false);
                    Post(new VmListResult(server.Id, vms, null));
                    var detailed = await client.WithDetailedStatesAsync(vms, (vm, error) =>
                        AppLog.Info($"Stato di {vm.Key} non letto: {error.Message} {error.InnerException?.Message}")).ConfigureAwait(false);
                    AppLog.Info($"Elenco VM di {server.Name}: {string.Join(", ", detailed.Select(v => $"{v.VmId}={v.Status}"))}");
                    Post(new VmListResult(server.Id, detailed, null));
                }
                catch (Exception error) when (error is ProxmoxException or System.ComponentModel.Win32Exception)
                {
                    Post(new VmListResult(server.Id, null, AppController.ErrorText(error)));
                }
            }
        });
        _loading = false;
        onLoading(false);
        if (_again) Reload();
    }

    private void Post(VmListResult result) => dispatcher.TryEnqueue(() => onResult(result));
}

using SpiceKit.Proxmox;
using SpiceKit.VVFile;

namespace WindowsSpiceClient.Services;

/// <summary>
/// Server Proxmox aggiunti dall'utente (<c>servers.json</c>) e CA dei cluster imparate
/// (<c>cluster-cas.json</c>); i segreti stanno in <see cref="CredentialStore"/>. Porting di
/// <c>Store</c> (Linux). Usato solo dal thread dell'interfaccia.
/// </summary>
internal sealed class ServerStore
{
    private const string ServersFile = "servers.json";
    private const string CasFile = "cluster-cas.json";

    private readonly Dictionary<string, string> _learnedCas;

    public List<ProxmoxServer> Servers { get; private set; }

    /// <summary>Server aggiunti, modificati o rimossi.</summary>
    public event EventHandler? Changed;

    public ServerStore()
    {
        Servers = JsonFiles.Load(ServersFile, AppJsonContext.Default.ListProxmoxServer, () => [])
            .Where(s => !string.IsNullOrEmpty(s.Id) && !string.IsNullOrEmpty(s.Host)).ToList();
        _learnedCas = new Dictionary<string, string>(
            JsonFiles.Load(CasFile, AppJsonContext.Default.DictionaryStringString, () => []), StringComparer.OrdinalIgnoreCase);
    }

    public ProxmoxServer? ServerForHost(string host) => Servers.FirstOrDefault(s => s.HasHost(host));

    public ProxmoxServer? ServerById(string id) => Servers.FirstOrDefault(s => s.Id == id);

    public string? KnownCa(string host) => _learnedCas.GetValueOrDefault(ProxmoxServer.BareHost(host.Trim()));

    /// <summary>Aggiunge o sostituisce un server; <paramref name="secret"/>, se presente, va in Gestione credenziali.</summary>
    public void Save(ProxmoxServer server, string? secret)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.ClusterCaPem is null && KnownCa(server.Host) is { } ca) server = server with { ClusterCaPem = ca };
        if (!string.IsNullOrEmpty(secret)) CredentialStore.Save(server.Id, secret);
        var index = Servers.FindIndex(s => s.Id == server.Id);
        if (index >= 0) Servers[index] = server;
        else Servers.Add(server);
        Persist();
    }

    public void Remove(ProxmoxServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        CredentialStore.Delete(server.Id);
        Servers.RemoveAll(s => s.Id == server.Id);
        Persist();
    }

    /// <summary>
    /// Client API per il server. Può bloccare (Gestione credenziali): chiamarlo fuori dal
    /// thread dell'interfaccia.
    /// </summary>
    public static ProxmoxClient Client(ProxmoxServer server)
    {
        var secret = CredentialStore.Lookup(server.Id);
        if (string.IsNullOrEmpty(secret)) throw ProxmoxException.InvalidConfiguration("nessun segreto del token API salvato");
        return new ProxmoxClient(server, secret);
    }

    /// <summary>
    /// Ricorda la CA del cluster da un file di connessione ottenuto tramite una connessione API
    /// <b>già verificata</b> (mai da un file scaricato: potrebbe introdurre una CA per l'host dell'API).
    /// </summary>
    public void LearnCa(ProxmoxServer source, ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(profile);
        if (ProxmoxVMRef.From(profile) is not { } reference || profile.CaPem is not { } ca) return;
        if (!source.HasHost(reference.ApiHost)) return; // la CA vale solo per l'host interrogato
        var host = ProxmoxServer.BareHost(reference.ApiHost.Trim());
        if (_learnedCas.GetValueOrDefault(host) == ca) return;
        _learnedCas[host] = ca;
        JsonFiles.Save(CasFile, new Dictionary<string, string>(_learnedCas), AppJsonContext.Default.DictionaryStringString);
        if (ServerForHost(host) is { } server && server.ClusterCaPem != ca) Save(server with { ClusterCaPem = ca }, null);
    }

    private void Persist()
    {
        JsonFiles.Save(ServersFile, Servers, AppJsonContext.Default.ListProxmoxServer);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

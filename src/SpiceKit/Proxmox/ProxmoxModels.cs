using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using SpiceKit.VVFile;

namespace SpiceKit.Proxmox;

/// <summary>
/// Server Proxmox VE (nodo del cluster) raggiungibile tramite API REST. Il <i>segreto</i> del
/// token non fa parte di questo valore: sta in Gestione credenziali di Windows.
/// </summary>
public sealed partial record ProxmoxServer
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 8006;

    /// <summary><c>utente@realm!nometoken</c>.</summary>
    public string TokenId { get; init; } = "";

    /// <summary>SHA-256 (esadecimale) del certificato che l'utente ha scelto esplicitamente di considerare attendibile.</summary>
    public string? PinnedSha256 { get; init; }

    /// <summary>CA del cluster (<c>pve-root-ca</c>) ottenuta tramite l'API già verificata di questo host.</summary>
    public string? ClusterCaPem { get; init; }

    [GeneratedRegex("^[A-Za-z0-9._@!-]+$")]
    private static partial Regex TokenCharacters();

    [GeneratedRegex("^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$")]
    private static partial Regex HostName();

    /// <summary><c>utente@realm!nome</c>: solo lettere, cifre e <c>-_.@!</c> (finisce in un'intestazione HTTP).</summary>
    public static bool IsValidTokenId(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split('!');
        return parts.Length == 2 && parts[0].Contains('@', StringComparison.Ordinal) && parts[1].Length > 0
            && text.Length <= 256 && TokenCharacters().IsMatch(text);
    }

    /// <summary>I segreti dei token sono UUID.</summary>
    public static bool IsValidSecret(string text) =>
        text is { Length: 36 } && Guid.TryParseExact(text, "D", out _);

    /// <summary>Nome host o indirizzo IP (anche IPv6 tra parentesi quadre).</summary>
    public static bool IsValidHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var bare = BareHost(host.Trim());
        if (bare.Length == 0) return false;
        return IPAddress.TryParse(bare, out _) ? !bare.Contains('%', StringComparison.Ordinal) : HostName().IsMatch(bare);
    }

    public static string BareHost(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    /// <summary>Host pronto per un URI (IPv6 tra parentesi quadre).</summary>
    internal static string UriHost(string host)
    {
        var bare = BareHost(host);
        return IPAddress.TryParse(bare, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{bare}]"
            : bare;
    }

    /// <summary>Stesso host, ignorando maiuscole e parentesi quadre.</summary>
    public bool HasHost(string host) =>
        string.Equals(BareHost(Host.Trim()), BareHost((host ?? "").Trim()), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Macchina virtuale QEMU come elencata da <c>/cluster/resources</c>.</summary>
public sealed record ProxmoxVM(int VmId, string Name, string Node, string Status)
{
    /// <summary>Chiave univoca nel cluster: <c>nodo/vmid</c>.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Node}/{VmId}");
    public bool IsRunning => Status == "running";

    /// <summary>In pausa o sospesa (stato QMP).</summary>
    public bool IsPaused => Status is "paused" or "suspended";

    public string StatusLabel => Status switch
    {
        "running" => "in esecuzione",
        "stopped" => "spenta",
        "paused" => "in pausa",
        "suspended" => "sospesa",
        "prelaunch" => "in avvio",
        "unknown" => "sconosciuto",
        _ => Status,
    };
}

/// <summary><c>POST /nodes/{node}/qemu/{vmid}/status/{action}</c>.</summary>
public enum PowerAction
{
    Start,
    Shutdown,
    Reboot,
    Reset,
    Stop,
    Suspend,
    Resume,
}

public static class PowerActions
{
    public static string ApiName(this PowerAction action) => action.ToString().ToLowerInvariant();

    /// <summary>Azioni che staccano il sistema ospite senza chiederglielo (perdita di dati): chiedere conferma.</summary>
    public static bool IsDestructive(this PowerAction action) => action is PowerAction.Reset or PowerAction.Stop;

    public static string Title(this PowerAction action) => action switch
    {
        PowerAction.Start => "Avvia",
        PowerAction.Shutdown => "Arresta",
        PowerAction.Reboot => "Riavvia",
        PowerAction.Reset => "Reset…",
        PowerAction.Stop => "Spegni forzatamente…",
        PowerAction.Suspend => "Metti in pausa",
        PowerAction.Resume => "Riprendi",
        _ => action.ToString(),
    };

    /// <summary>Azioni disponibili per una VM nello stato indicato.</summary>
    public static IReadOnlyList<PowerAction> For(string status) => status switch
    {
        "running" => [PowerAction.Shutdown, PowerAction.Reboot, PowerAction.Suspend, PowerAction.Reset, PowerAction.Stop],
        "paused" or "suspended" => [PowerAction.Resume, PowerAction.Stop],
        _ => [PowerAction.Start],
    };
}

/// <summary>La VM a cui punta un file <c>.vv</c> di Proxmox.</summary>
/// <param name="ApiHost">Host dell'API del nodo (l'host del proxy nel <c>.vv</c>; l'API ascolta sulla 8006).</param>
/// <param name="Node">Nome del nodo.</param>
/// <param name="VmId">Identificativo della VM.</param>
public sealed record ProxmoxVMRef(string ApiHost, string Node, int VmId)
{
    /// <summary>
    /// Gli host dei <c>.vv</c> Proxmox sono <c>pvespiceproxy:&lt;ticket&gt;:&lt;vmid&gt;:&lt;nodo&gt;::&lt;…&gt;</c>
    /// e il proxy è il nodo stesso (<c>http://&lt;nodo&gt;:3128</c>).
    /// </summary>
    public static ProxmoxVMRef? From(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Proxy is not { } proxy) return null;
        var fields = profile.Host.Split(':');
        if (fields.Length < 4 || fields[0] != "pvespiceproxy" || fields[3].Length == 0) return null;
        if (!int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var vmid) || vmid <= 0) return null;
        return new ProxmoxVMRef(proxy.Host, fields[3], vmid);
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpiceKit.Proxmox;

/// <summary>
/// Client minimo dell'API REST di Proxmox VE (autenticazione con token API): elenco delle VM,
/// alimentazione e file di connessione SPICE. Porting di <c>ProxmoxClient</c> (macOS/Linux).
/// </summary>
/// <remarks>
/// TLS: il certificato deve essere valido per l'archivio di Windows <b>oppure</b> per la CA del
/// cluster già nota (con verifica del nome host), oppure corrispondere all'impronta SHA-256 che
/// l'utente ha fissato esplicitamente. Non esiste un modo per saltare la verifica.
/// </remarks>
public sealed partial class ProxmoxClient : IDisposable
{
    public const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly string _authorization;
    private readonly string? _pinned;
    private readonly X509Certificate2Collection _anchors = [];
    private volatile string? _lastRejected;

    public ProxmoxServer Server { get; }

    /// <summary>Pausa tra i tentativi di <c>spiceproxy</c> quando QEMU è occupato (modificabile nei test).</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Intervallo di controllo in <see cref="WaitUntilRunningAsync"/> (modificabile nei test).</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public ProxmoxClient(ProxmoxServer server, string secret)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(secret);
        if (!ProxmoxServer.IsValidHost(server.Host) || server.Port is < 1 or > 65535)
        {
            throw ProxmoxException.InvalidConfiguration("host");
        }
        if (!ProxmoxServer.IsValidTokenId(server.TokenId) || !ProxmoxServer.IsValidSecret(secret))
        {
            throw ProxmoxException.InvalidConfiguration("token");
        }
        Server = server;
        _authorization = $"PVEAPIToken={server.TokenId}={secret}";
        _pinned = server.PinnedSha256?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(server.ClusterCaPem))
        {
            try
            {
                _anchors.ImportFromPem(server.ClusterCaPem);
            }
            catch (CryptographicException)
            {
                // CA illeggibile: resta solo la fiducia di sistema (o l'impronta fissata).
                _anchors.Clear();
            }
        }

        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) => Validate(certificate, chain, errors),
            },
        };
        _http = new HttpClient(handler)
        {
            Timeout = Timeout,
            BaseAddress = new Uri(string.Create(CultureInfo.InvariantCulture,
                $"https://{ProxmoxServer.UriHost(server.Host.Trim())}:{server.Port}/api2/json/")),
        };
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var anchor in _anchors) anchor.Dispose();
    }

    // MARK: API

    /// <summary>VM QEMU (senza modelli) visibili al token, ordinate per nome.</summary>
    public async Task<IReadOnlyList<ProxmoxVM>> VirtualMachinesAsync(CancellationToken cancellationToken = default)
    {
        using var document = await RequestAsync(HttpMethod.Get, "cluster/resources?type=vm", null, cancellationToken).ConfigureAwait(false);
        var data = document.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array) throw ProxmoxException.InvalidResponse();
        var list = new List<ProxmoxVM>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || GetString(entry, "type") != "qemu") continue;
            if (entry.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.Number
                && template.TryGetInt32(out var isTemplate) && isTemplate != 0) continue;
            if (!entry.TryGetProperty("vmid", out var vmidElement) || vmidElement.ValueKind != JsonValueKind.Number
                || !vmidElement.TryGetInt32(out var vmid)) continue;
            if (GetString(entry, "node") is not { } node) continue;
            var name = GetString(entry, "name") is { Length: > 0 } n ? n : string.Create(CultureInfo.InvariantCulture, $"VM {vmid}");
            list.Add(new ProxmoxVM(vmid, name, node, GetString(entry, "status") is { Length: > 0 } s ? s : "unknown"));
        }
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        list.Sort((a, b) =>
        {
            var byName = compare.Compare(a.Name, b.Name, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
            return byName != 0 ? byName : a.VmId.CompareTo(b.VmId);
        });
        return list;
    }

    /// <summary>
    /// <paramref name="list"/> con le VM in pausa riconosciute: una richiesta di stato per VM in
    /// esecuzione, al massimo 8 alla volta; se lo stato non si legge resta quello elencato.
    /// </summary>
    public async Task<IReadOnlyList<ProxmoxVM>> WithDetailedStatesAsync(IReadOnlyList<ProxmoxVM> list, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(list);
        var result = list.ToArray();
        using var gate = new SemaphoreSlim(8);
        var tasks = Enumerable.Range(0, list.Count).Where(i => list[i].IsRunning).Select(async index =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var vm = list[index];
                result[index] = vm with { Status = await StatusAsync(vm.Node, vm.VmId, cancellationToken).ConfigureAwait(false) };
            }
            catch (ProxmoxException)
            {
                // resta lo stato elencato
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    /// <summary><c>running</c>, <c>paused</c>, <c>stopped</c>… (stato QMP quando disponibile: <c>status</c> da solo non dice mai <c>paused</c>).</summary>
    public async Task<string> StatusAsync(string node, int vmid, CancellationToken cancellationToken = default)
    {
        using var document = await RequestAsync(HttpMethod.Get, $"nodes/{Segment(node)}/qemu/{Id(vmid)}/status/current", null, cancellationToken).ConfigureAwait(false);
        var data = document.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Object || GetString(data, "status") is not { } status) throw ProxmoxException.InvalidResponse();
        return status == "running" && GetString(data, "qmpstatus") is { } qmp ? qmp : status;
    }

    public async Task PerformAsync(PowerAction action, string node, int vmid, CancellationToken cancellationToken = default)
    {
        using var _ = await RequestAsync(HttpMethod.Post, $"nodes/{Segment(node)}/qemu/{Id(vmid)}/status/{action.ApiName()}", [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Attende che la VM risulti in esecuzione (dopo Avvia o Riprendi).</summary>
    public async Task WaitUntilRunningAsync(string node, int vmid, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(1));
        while (DateTime.UtcNow < deadline)
        {
            if (await StatusAsync(node, vmid, cancellationToken).ConfigureAwait(false) == "running") return;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw ProxmoxException.StartTimeout();
    }

    internal static bool IsQmpTimeout(string message) =>
        message.Contains("qmp command", StringComparison.Ordinal) && message.Contains("timeout", StringComparison.Ordinal);

    /// <summary>
    /// Un file di connessione SPICE nuovo (ticket monouso) per la VM, come quello del pulsante
    /// "SPICE" dell'interfaccia web, in formato <c>.vv</c>. Serve VM.Console.
    /// </summary>
    public async Task<string> SpiceConnectionFileAsync(string node, int vmid, CancellationToken cancellationToken = default)
    {
        var path = $"nodes/{Segment(node)}/qemu/{Id(vmid)}/spiceproxy";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var document = await RequestAsync(HttpMethod.Post, path,
                    [new("proxy", ProxmoxServer.BareHost(Server.Host.Trim()))], cancellationToken).ConfigureAwait(false);
                var data = document.RootElement.GetProperty("data");
                if (data.ValueKind != JsonValueKind.Object) throw ProxmoxException.InvalidResponse();
                return ConnectionFileText(data);
            }
            catch (ProxmoxException error) when (error.Kind == ProxmoxErrorKind.Http && error.Status == 500 && IsQmpTimeout(error.Detail ?? ""))
            {
                // Il monitor di QEMU era occupato (backup, snapshot, molto I/O) e Proxmox ha
                // rinunciato a query-spice dopo qualche secondo: di solito al secondo tentativo va.
                if (attempt >= 3) throw ProxmoxException.QemuNotResponding(vmid);
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex VVKey();

    /// <summary>Trasforma l'oggetto JSON di <c>POST …/spiceproxy</c> in testo <c>.vv</c>.</summary>
    public static string ConnectionFileText(JsonElement data)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            if (!VVKey().IsMatch(property.Name)) continue;
            string? text = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                _ => null,
            };
            if (text is null) continue;
            // Proxmox restituisce la CA già con "\n" letterali (PVE::AccessControl::
            // remote_viewer_config: `$cacert =~ s/\n/\\n/g;`): vanno annullati prima, altrimenti
            // l'escape qui sotto raddoppia le barre e il PEM diventa illeggibile.
            if (property.Name == "ca") text = text.Replace("\\n", "\n", StringComparison.Ordinal);
            values[property.Name] = text;
        }
        var builder = new StringBuilder("[virt-viewer]\n");
        foreach (var (key, value) in values)
        {
            // Escape GKeyFile, così il risultato passa dal normale parser con le sue verifiche.
            var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal);
            builder.Append(key).Append('=').Append(escaped).Append('\n');
        }
        return builder.ToString();
    }

    // MARK: Trasporto

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex NodeName();

    /// <summary>I nomi dei nodi sono nomi host: si rifiuta tutto ciò che può cambiare il percorso.</summary>
    private static string Segment(string text) =>
        !string.IsNullOrEmpty(text) && NodeName().IsMatch(text) ? text : throw ProxmoxException.InvalidConfiguration("nome del nodo");

    private static string Id(int vmid) =>
        vmid > 0 ? vmid.ToString(CultureInfo.InvariantCulture) : throw ProxmoxException.InvalidConfiguration("VMID");

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private bool Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
        if (_pinned is not null && fingerprint == _pinned) return true;
        if (errors == SslPolicyErrors.None) return true;
        // Solo la catena non è valida (nome host corretto): si riprova con la CA del cluster,
        // *in aggiunta* alle radici di sistema (i certificati ACME continuano a funzionare).
        if (errors == SslPolicyErrors.RemoteCertificateChainErrors && _anchors.Count > 0 && ChainsToClusterCa(certificate, chain))
        {
            return true;
        }
        _lastRejected = fingerprint;
        return false;
    }

    private bool ChainsToClusterCa(X509Certificate certificate, X509Chain? presented)
    {
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_anchors);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // la CA di Proxmox non pubblica CRL
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // autenticazione server
        if (presented is not null)
        {
            foreach (var element in presented.ChainElements.Skip(1)) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        }
        return chain.Build(leaf);
    }

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, List<KeyValuePair<string, string>>? form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", _authorization);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (method == HttpMethod.Post) request.Content = new FormUrlEncodedContent(form ?? []);

        _lastRejected = null;
        byte[] body;
        HttpStatusCode status;
        string? reason;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw ProxmoxException.InvalidResponse();
            body = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            reason = response.ReasonPhrase;
        }
        catch (HttpRequestException error)
        {
            if (_lastRejected is { } fingerprint) throw ProxmoxException.UntrustedCertificate(fingerprint);
            throw ProxmoxException.Transport(Describe(error), error);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw ProxmoxException.Transport("tempo scaduto", error);
        }
        catch (IOException error)
        {
            throw ProxmoxException.Transport(error.Message, error);
        }

        JsonDocument? document = null;
        try
        {
            document = body.Length > 0 ? JsonDocument.Parse(body) : null;
        }
        catch (JsonException)
        {
            document = null;
        }

        var code = (int)status;
        if (code is >= 200 and < 300)
        {
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out _))
            {
                document?.Dispose();
                throw ProxmoxException.InvalidResponse();
            }
            return document;
        }
        var message = Message(document, reason);
        document?.Dispose();
        throw code switch
        {
            401 => ProxmoxException.Unauthorized(),
            403 => ProxmoxException.Forbidden(message),
            _ => ProxmoxException.Http(code, message),
        };
    }

    /// <summary>Legge il corpo e si ferma appena supera il limite: un server difettoso non fa crescere la memoria.</summary>
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw ProxmoxException.InvalidResponse();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Message(JsonDocument? document, string? reason)
    {
        if (document?.RootElement is { ValueKind: JsonValueKind.Object } root)
        {
            if (GetString(root, "message") is { } message) return Truncate(message).Trim();
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                return Truncate(string.Join("; ", errors.EnumerateObject().Select(e =>
                    $"{e.Name}: {(e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString() : e.Value.GetRawText())}")));
            }
        }
        return reason ?? "";
    }

    private static string Truncate(string text) => text.Length > 200 ? text[..200] : text;

    private static string Describe(HttpRequestException error)
    {
        if (error.HttpRequestError == HttpRequestError.NameResolutionError) return "host sconosciuto";
        for (Exception? inner = error; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }: return "connessione rifiutata";
                case SocketException { SocketErrorCode: SocketError.TimedOut }: return "tempo scaduto";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }: return "host sconosciuto";
                case AuthenticationException: return "errore TLS";
            }
        }
        return error.InnerException?.Message ?? error.Message;
    }
}

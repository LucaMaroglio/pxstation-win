using System.Text.Json;
using SpiceKit.Proxmox;
using SpiceKit.Tests.Support;
using SpiceKit.VVFile;

namespace SpiceKit.Tests.Proxmox;

public class ProxmoxTests
{
    private const string Secret = "5f0e6c6e-2a3b-4c5d-8e9f-0a1b2c3d4e5f";
    private const string VMList = """
        {"data":[
          {"type":"qemu","vmid":110,"name":"vm10","node":"pve1","status":"running"},
          {"type":"qemu","vmid":102,"name":"vm2","node":"pve1","status":"stopped"},
          {"type":"qemu","vmid":900,"name":"modello","node":"pve1","status":"stopped","template":1},
          {"type":"lxc","vmid":200,"name":"ct","node":"pve1","status":"running"},
          {"type":"qemu","vmid":105,"node":"pve2","status":"running"}
        ]}
        """;

    private static ProxmoxServer Server(TestHttpsServer https, string host = "localhost", string? pinned = null, string? ca = null) =>
        new() { Name = "prova", Host = host, Port = https.Port, TokenId = "root@pam!spice", PinnedSha256 = pinned, ClusterCaPem = ca };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void VMRefFromProxmoxConnectionFile()
    {
        var file = VVFileParser.Parse("[virt-viewer]\ntype=spice\nhost=pvespiceproxy:6512a3b4:101:pve-node1::0a2bc3d4e5f6\n" +
                                      "proxy=http://pve1.example.com:3128\ntls-port=61000\n");
        Assert.Equal(new ProxmoxVMRef("pve1.example.com", "pve-node1", 101), ProxmoxVMRef.From(file.Profile));
    }

    [Fact]
    public void PlainQemuFileHasNoVM()
    {
        var file = VVFileParser.Parse("[virt-viewer]\ntype=spice\nhost=10.0.0.5\nport=5900\n");
        Assert.Null(ProxmoxVMRef.From(file.Profile));
    }

    /// <summary>
    /// Proxmox restituisce <c>ca</c> con i ritorni a capo già convertiti in <c>\n</c>: il file
    /// risultante deve contenere comunque un PEM su più righe.
    /// </summary>
    [Fact]
    public void SpiceproxyCaIsNotDoubleEscaped()
    {
        using var pki = new TestPki();
        var pem = pki.CaPem.ReplaceLineEndings("\n") + "\n";
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "spice", ["host"] = "pvespiceproxy:abc:101:pve-node1::def", ["proxy"] = "http://pve1.example.com:3128",
            ["tls-port"] = 61000, ["ca"] = pem.Replace("\n", "\\n", StringComparison.Ordinal), ["delete-this-file"] = true,
            ["Bad Key"] = "x",
        });
        using var document = JsonDocument.Parse(json);
        var text = ProxmoxClient.ConnectionFileText(document.RootElement);
        var file = VVFileParser.Parse(text);
        Assert.Equal(pem, file.Profile.CaPem);
        Assert.Equal((ushort)61000, file.Profile.TlsPort);
        Assert.True(file.Profile.DeleteThisFile);
        Assert.DoesNotContain("Bad Key", text, StringComparison.Ordinal);
        var certificates = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
        certificates.ImportFromPem(file.Profile.CaPem);
        Assert.Single(certificates);
    }

    [Fact]
    public void TokenValidation()
    {
        Assert.True(ProxmoxServer.IsValidTokenId("root@pam!spice"));
        Assert.True(ProxmoxServer.IsValidTokenId("pawel@pve!mac-client"));
        Assert.False(ProxmoxServer.IsValidTokenId("root@pam"));
        Assert.False(ProxmoxServer.IsValidTokenId("root@pam!x\r\nX-Evil: 1"));
        Assert.False(ProxmoxServer.IsValidTokenId("root@pam!a=b"));
        Assert.True(ProxmoxServer.IsValidSecret(Secret));
        Assert.False(ProxmoxServer.IsValidSecret("not-a-uuid"));
        Assert.False(ProxmoxServer.IsValidSecret("{5f0e6c6e-2a3b-4c5d-8e9f-0a1b2c3d4e5f}"));
    }

    [Theory]
    [InlineData("pve1.example.com", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("[2001:db8::1]", true)]
    [InlineData("evil/path", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData("-bad.example", false)]
    public void HostValidation(string host, bool valid) => Assert.Equal(valid, ProxmoxServer.IsValidHost(host));

    [Fact]
    public void QmpTimeoutIsRecognised()
    {
        Assert.True(ProxmoxClient.IsQmpTimeout("VM 118 qmp command 'query-spice' failed - got timeout"));
        Assert.False(ProxmoxClient.IsQmpTimeout("VM 118 not running"));
    }

    [Fact]
    public void FingerprintFormatting() => Assert.Equal("AB:01:CD", ProxmoxException.FormatFingerprint("ab01cd"));

    [Fact]
    public void ClientRejectsUnsafeSettings()
    {
        var bad = new ProxmoxServer { Name = "x", Host = "evil/path", TokenId = "root@pam!t" };
        Assert.Equal(ProxmoxErrorKind.InvalidConfiguration, Assert.Throws<ProxmoxException>(() => new ProxmoxClient(bad, Secret)).Kind);
        var good = new ProxmoxServer { Name = "x", Host = "pve.example.com", TokenId = "root@pam!t" };
        Assert.Equal(ProxmoxErrorKind.InvalidConfiguration, Assert.Throws<ProxmoxException>(() => new ProxmoxClient(good, "nope")).Kind);
    }

    [Fact]
    public void PowerActionsForStates()
    {
        Assert.Equal([PowerAction.Start], PowerActions.For("stopped"));
        Assert.Equal([PowerAction.Resume, PowerAction.Stop], PowerActions.For("paused"));
        Assert.Contains(PowerAction.Shutdown, PowerActions.For("running"));
        Assert.True(PowerAction.Stop.IsDestructive());
        Assert.False(PowerAction.Shutdown.IsDestructive());
    }

    // MARK: TLS e richieste contro un server HTTPS locale

    [Fact]
    public async Task UntrustedCertificateReportsFingerprint()
    {
        using var pki = new TestPki();
        using var https = new TestHttpsServer(pki.Server, _ => (200, VMList));
        using var client = new ProxmoxClient(Server(https), Secret);
        var error = await Assert.ThrowsAsync<ProxmoxException>(() => client.VirtualMachinesAsync(Token));
        Assert.Equal(ProxmoxErrorKind.UntrustedCertificate, error.Kind);
        Assert.Equal(pki.ServerSha256, error.Sha256);
        Assert.Empty(https.Requests); // il token non è mai stato inviato
    }

    [Fact]
    public async Task PinnedCertificateIsAcceptedAndListIsFilteredAndSorted()
    {
        using var pki = new TestPki();
        using var https = new TestHttpsServer(pki.Server, _ => (200, VMList));
        using var client = new ProxmoxClient(Server(https, pinned: pki.ServerSha256.ToUpperInvariant()), Secret);
        var vms = await client.VirtualMachinesAsync(Token);
        Assert.Equal(["VM 105", "vm2", "vm10"], vms.Select(v => v.Name));
        var request = Assert.Single(https.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api2/json/cluster/resources?type=vm", request.Target);
        Assert.Equal($"PVEAPIToken=root@pam!spice={Secret}", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task ClusterCaIsTrustedWithMatchingHostName()
    {
        using var pki = new TestPki();
        using var https = new TestHttpsServer(pki.Server, _ => (200, VMList));
        using var client = new ProxmoxClient(Server(https, ca: pki.CaPem), Secret);
        Assert.Equal(3, (await client.VirtualMachinesAsync(Token)).Count);
    }

    [Fact]
    public async Task ClusterCaDoesNotExcuseWrongHostName()
    {
        using var pki = new TestPki("pve-altro.example.invalid");
        using var https = new TestHttpsServer(pki.Server, _ => (200, VMList));
        using var client = new ProxmoxClient(Server(https, ca: pki.CaPem), Secret);
        var error = await Assert.ThrowsAsync<ProxmoxException>(() => client.VirtualMachinesAsync(Token));
        Assert.Equal(ProxmoxErrorKind.UntrustedCertificate, error.Kind);
    }

    [Fact]
    public async Task ClusterCaOfAnotherClusterIsRejected()
    {
        using var pki = new TestPki();
        using var other = new TestPki();
        using var https = new TestHttpsServer(pki.Server, _ => (200, VMList));
        using var client = new ProxmoxClient(Server(https, ca: other.CaPem), Secret);
        var error = await Assert.ThrowsAsync<ProxmoxException>(() => client.VirtualMachinesAsync(Token));
        Assert.Equal(ProxmoxErrorKind.UntrustedCertificate, error.Kind);
    }

    [Theory]
    [InlineData(401, """{"data":null}""", ProxmoxErrorKind.Unauthorized)]
    [InlineData(403, """{"data":null,"message":"Permission check failed"}""", ProxmoxErrorKind.Forbidden)]
    [InlineData(500, """{"data":null,"message":"boom"}""", ProxmoxErrorKind.Http)]
    [InlineData(200, """{"nodata":1}""", ProxmoxErrorKind.InvalidResponse)]
    [InlineData(200, "not json", ProxmoxErrorKind.InvalidResponse)]
    public async Task HttpErrorsAreTyped(int status, string body, ProxmoxErrorKind kind)
    {
        using var pki = new TestPki();
        using var https = new TestHttpsServer(pki.Server, _ => (status, body));
        using var client = new ProxmoxClient(Server(https, ca: pki.CaPem), Secret);
        var error = await Assert.ThrowsAsync<ProxmoxException>(() => client.VirtualMachinesAsync(Token));
        Assert.Equal(kind, error.Kind);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var pki = new TestPki();
        var huge = "{\"data\":\"" + new string('x', ProxmoxClient.MaxResponseBytes) + "\"}";
        using var https = new TestHttpsServer(pki.Server, _ => (200, huge));
        using var client = new ProxmoxClient(Server(https, ca: pki.CaPem), Secret);
        var error = await Assert.ThrowsAsync<ProxmoxException>(() => client.VirtualMachinesAsync(Token));
        Assert.Equal(ProxmoxErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task SpiceproxyRetriesQmpTimeoutThenReturnsParsableFile()
    {
        using var pki = new TestPki();
        var calls = 0;
        using var https = new TestHttpsServer(pki.Server, request =>
        {
            if (!request.Target.EndsWith("/spiceproxy", StringComparison.Ordinal)) return (404, "{}");
            return Interlocked.Increment(ref calls) == 1
                ? (500, """{"data":null,"message":"VM 101 qmp command 'query-spice' failed - got timeout"}""")
                : (200, """{"data":{"type":"spice","host":"pvespiceproxy:abc:101:pve1::def","proxy":"http://localhost:3128","tls-port":61000,"password":"t"}}""");
        });
        using var client = new ProxmoxClient(Server(https, ca: pki.CaPem), Secret) { RetryDelay = TimeSpan.Zero };
        var text = await client.SpiceConnectionFileAsync("pve1", 101, Token);
        var profile = VVFileParser.Parse(text).Profile;
        Assert.Equal(new ProxmoxVMRef("localhost", "pve1", 101), ProxmoxVMRef.From(profile));
        Assert.Equal(2, calls);
        Assert.All(https.Requests, r => Assert.Equal("proxy=localhost", r.Body));
    }

    [Fact]
    public async Task SpiceproxyGivesUpAfterThreeQmpTimeouts()
    {
        using var pki = new TestPki();
        using var https = new TestHttpsServer(pki.Server, _ => (500, """{"data":null,"message":"qmp command 'query-spice' failed - got timeout"}"""));
        using var client = new ProxmoxClient(Server(https, ca: pki.CaPem), Secret) { RetryDelay = TimeSpan.Zero };
        var error = await Assert.ThrowsAsync<ProxmoxException>(() => client.SpiceConnectionFileAsync("pve1", 101, Token));
        Assert.Equal(ProxmoxErrorKind.QemuNotResponding, error.Kind);
        Assert.Equal(3, https.Requests.Count);
    }

    [Fact]
    public async Task UnsafeNodeNameIsRefusedBeforeAnyRequest()
    {
        using var pki = new TestPki();
        using var https = new TestHttpsServer(pki.Server, _ => (200, "{\"data\":null}"));
        using var client = new ProxmoxClient(Server(https, ca: pki.CaPem), Secret);
        var error = await Assert.ThrowsAsync<ProxmoxException>(() => client.PerformAsync(PowerAction.Stop, "../access", 101, Token));
        Assert.Equal(ProxmoxErrorKind.InvalidConfiguration, error.Kind);
        Assert.Empty(https.Requests);
    }

    [Fact]
    public async Task DetailedStatesRecognisePausedVMs()
    {
        using var pki = new TestPki();
        using var https = new TestHttpsServer(pki.Server, request => request.Target.Contains("/status/current", StringComparison.Ordinal)
            ? (200, """{"data":{"status":"running","qmpstatus":"paused"}}""")
            : (200, VMList));
        using var client = new ProxmoxClient(Server(https, ca: pki.CaPem), Secret);
        var vms = await client.WithDetailedStatesAsync(await client.VirtualMachinesAsync(Token), cancellationToken: Token);
        Assert.Equal(["paused", "stopped", "paused"], vms.Select(v => v.Status));
    }

    [Fact]
    public async Task ReadsAreRetriedOnceWhenTheConnectionDrops()
    {
        using var pki = new TestPki();
        var calls = 0;
        using var https = new TestHttpsServer(pki.Server, _ => Interlocked.Increment(ref calls) == 1 ? (0, "") : (200, VMList));
        using var client = new ProxmoxClient(Server(https, ca: pki.CaPem), Secret);
        Assert.Equal(3, (await client.VirtualMachinesAsync(Token)).Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ActionsAreNeverRetried()
    {
        using var pki = new TestPki();
        using var https = new TestHttpsServer(pki.Server, _ => (0, ""));
        using var client = new ProxmoxClient(Server(https, ca: pki.CaPem), Secret);
        var error = await Assert.ThrowsAsync<ProxmoxException>(() => client.PerformAsync(PowerAction.Stop, "pve1", 101, Token));
        Assert.Equal(ProxmoxErrorKind.Transport, error.Kind);
        Assert.Single(https.Requests);
    }
}

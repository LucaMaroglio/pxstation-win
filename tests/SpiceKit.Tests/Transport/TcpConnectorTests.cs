using SpiceKit.Tests.Support;
using SpiceKit.Transport;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Transport;

/// <summary>
/// <see cref="TcpConnector"/> su socket veri: TLS con la CA del file, <c>host-subject</c>,
/// nome host e proxy CONNECT (AGENTS.md §6: nessun modo di saltare la verifica).
/// </summary>
public sealed class TcpConnectorTests
{
    private const string ProxmoxSubject = "OU=PVE Cluster Node,O=Proxmox Virtual Environment,CN=localhost";
    private const string OpaqueHost = "pvespiceproxy:6527b3e1:101:pve1::abc123";

    private static async Task<ReceivedLink> LinkAsync(SpiceEndpoint endpoint, SpiceRoute route, TestSpiceTlsServer server)
    {
        var connector = new TcpConnector { ConnectTimeout = TimeSpan.FromSeconds(10) };
        await using var transport = await connector.ConnectAsync(endpoint, route, TestContext.Current.CancellationToken);
        var message = new LinkMessage(0, ChannelType.Main, 0, ChannelLinker.ClientCommonCaps, new CapabilitySet());
        await ChannelLinker.LinkAsync(transport, message, "ticket"u8.ToArray(), new FakeEncryptor(), TestContext.Current.CancellationToken);
        for (var i = 0; i < 50 && server.Links.Count == 0; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        return Assert.Single(server.Links);
    }

    private static Task<TransportException> FailAsync(SpiceEndpoint endpoint, ushort port) =>
        Assert.ThrowsAsync<TransportException>(() =>
            new TcpConnector { ConnectTimeout = TimeSpan.FromSeconds(10) }.ConnectAsync(endpoint, new SpiceRoute(port, true), TestContext.Current.CancellationToken));

    [Fact]
    public async Task Plain_tcp_links()
    {
        using var server = new TestSpiceTlsServer(null);
        var link = await LinkAsync(new SpiceEndpoint("localhost") { Port = server.Port }, new SpiceRoute(server.Port, false), server);
        Assert.Equal(ChannelType.Main, link.Type);
    }

    /// <summary>Come Proxmox: host opaco, proxy CONNECT, TLS con la sola CA del file e <c>host-subject</c>.</summary>
    [Fact]
    public async Task Proxmox_style_tls_through_proxy()
    {
        using var pki = new TestPki();
        using var server = new TestSpiceTlsServer(pki.Server);
        using var proxy = new TestConnectProxy(server.Port);
        var endpoint = new SpiceEndpoint(OpaqueHost)
        {
            TlsPort = 61000,
            Proxy = new SpiceKit.VVFile.ProxyEndpoint("localhost", proxy.Port),
            TrustAnchors = Pem.Certificates(pki.CaPem)!,
            HostSubject = HostSubject.Parse(ProxmoxSubject),
        };
        await LinkAsync(endpoint, new SpiceRoute(61000, true), server);
        Assert.Equal($"CONNECT {OpaqueHost}:61000 HTTP/1.1", Assert.Single(proxy.Requests));
    }

    [Fact]
    public async Task Ca_from_file_with_hostname_check()
    {
        using var pki = new TestPki();
        using var server = new TestSpiceTlsServer(pki.Server);
        var endpoint = new SpiceEndpoint("localhost") { TlsPort = server.Port, TrustAnchors = Pem.Certificates(pki.CaPem)! };
        await LinkAsync(endpoint, new SpiceRoute(server.Port, true), server);
    }

    [Fact]
    public async Task Certificate_from_another_ca_is_refused()
    {
        using var pki = new TestPki();
        using var other = new TestPki();
        using var server = new TestSpiceTlsServer(pki.Server);
        var endpoint = new SpiceEndpoint("localhost") { TlsPort = server.Port, TrustAnchors = Pem.Certificates(other.CaPem)!, HostSubject = HostSubject.Parse(ProxmoxSubject) };
        var error = await FailAsync(endpoint, server.Port);
        Assert.Equal(TransportErrorKind.TlsVerificationFailed, error.Kind);
        Assert.Contains("CA", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_host_subject_is_refused()
    {
        using var pki = new TestPki();
        using var server = new TestSpiceTlsServer(pki.Server);
        var endpoint = new SpiceEndpoint("localhost")
        {
            TlsPort = server.Port,
            TrustAnchors = Pem.Certificates(pki.CaPem)!,
            HostSubject = HostSubject.Parse("OU=PVE Cluster Node,O=Proxmox Virtual Environment,CN=pve2"),
        };
        var error = await FailAsync(endpoint, server.Port);
        Assert.Equal(TransportErrorKind.TlsVerificationFailed, error.Kind);
        Assert.Contains("host-subject", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_hostname_is_refused()
    {
        using var pki = new TestPki("pve-other.example.invalid");
        using var server = new TestSpiceTlsServer(pki.Server);
        var endpoint = new SpiceEndpoint("localhost") { TlsPort = server.Port, TrustAnchors = Pem.Certificates(pki.CaPem)! };
        var error = await FailAsync(endpoint, server.Port);
        Assert.Equal(TransportErrorKind.TlsVerificationFailed, error.Kind);
    }

    [Fact]
    public async Task Without_ca_the_system_trust_applies()
    {
        using var pki = new TestPki();
        using var server = new TestSpiceTlsServer(pki.Server);
        var error = await FailAsync(new SpiceEndpoint("localhost") { TlsPort = server.Port }, server.Port);
        Assert.Equal(TransportErrorKind.TlsVerificationFailed, error.Kind);
    }

    /// <summary>Un host opaco senza <c>host-subject</c> non si può verificare: rifiutato anche con la CA giusta.</summary>
    [Fact]
    public async Task Opaque_host_without_host_subject_is_refused()
    {
        using var pki = new TestPki();
        using var server = new TestSpiceTlsServer(pki.Server);
        using var proxy = new TestConnectProxy(server.Port);
        var endpoint = new SpiceEndpoint(OpaqueHost) { TlsPort = 61000, Proxy = new SpiceKit.VVFile.ProxyEndpoint("localhost", proxy.Port), TrustAnchors = Pem.Certificates(pki.CaPem)! };
        var error = await FailAsync(endpoint, 61000);
        Assert.Equal(TransportErrorKind.TlsVerificationFailed, error.Kind);
    }

    [Fact]
    public async Task Proxy_refusal_is_an_expired_connection_file()
    {
        using var proxy = new TestConnectProxy(1, status: 403);
        var endpoint = new SpiceEndpoint(OpaqueHost) { TlsPort = 61000, Proxy = new SpiceKit.VVFile.ProxyEndpoint("localhost", proxy.Port) };
        var error = await FailAsync(endpoint, 61000);
        Assert.Equal((TransportErrorKind.ProxyRefused, 403), (error.Kind, error.ProxyStatus));
        Assert.True(error.IsAuthenticationFailure);
    }

    [Fact]
    public async Task Refused_connection()
    {
        using var server = new TestSpiceTlsServer(null);
        var port = server.Port;
        server.Dispose();
        var error = await Assert.ThrowsAsync<TransportException>(() =>
            new TcpConnector().ConnectAsync(new SpiceEndpoint("localhost") { Port = port }, new SpiceRoute(port, false), TestContext.Current.CancellationToken));
        Assert.Equal(TransportErrorKind.ConnectionFailed, error.Kind);
    }
}

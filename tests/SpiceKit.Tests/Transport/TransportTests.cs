using System.Security.Cryptography;
using System.Text;
using SpiceKit.Tests.Support;
using SpiceKit.Transport;
using SpiceKit.VVFile;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Transport;

/// <summary>Porting di <c>TransportTests.swift</c>.</summary>
public sealed class TransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LinkMessage Message(ChannelType type = ChannelType.Main, uint connectionId = 0) =>
        new(connectionId, type, 0, ChannelLinker.ClientCommonCaps, new CapabilitySet());

    [Fact]
    public async Task Link_and_authenticate()
    {
        var (client, server) = MemoryTransport.Pair();
        var served = FakeSpiceServer.ServeLinkAsync(server);
        var linked = await ChannelLinker.LinkAsync(client, Message(ChannelType.Inputs, 42), "secret"u8.ToArray(), new FakeEncryptor(), Ct);
        var seen = await served;
        Assert.Equal(42u, seen.ConnectionId);
        Assert.Equal(ChannelType.Inputs, seen.Type);
        Assert.Equal("secret\0"u8.ToArray(), seen.Ticket[..7]); // terminato da NUL
        Assert.True(linked.ServerCommonCaps.Contains(CommonCapability.MiniHeader));
    }

    [Fact]
    public async Task Expired_ticket_is_an_authentication_failure()
    {
        var (client, server) = MemoryTransport.Pair();
        var served = FakeSpiceServer.ServeLinkAsync(server, authResult: LinkError.PermissionDenied);
        var error = await Assert.ThrowsAsync<TransportException>(() => ChannelLinker.LinkAsync(client, Message(), Array.Empty<byte>(), new FakeEncryptor(), Ct));
        await served;
        Assert.Equal(TransportErrorKind.AuthenticationFailed, error.Kind);
        Assert.Equal(LinkError.PermissionDenied, error.LinkError);
        Assert.True(error.IsAuthenticationFailure);
        Assert.Contains("file di connessione", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_rejected_and_missing_mini_header()
    {
        var (client, server) = MemoryTransport.Pair();
        var served = FakeSpiceServer.ServeLinkAsync(server, linkError: LinkError.NeedSecured);
        var rejected = await Assert.ThrowsAsync<TransportException>(() => ChannelLinker.LinkAsync(client, Message(), Array.Empty<byte>(), new FakeEncryptor(), Ct));
        await served;
        Assert.Equal((TransportErrorKind.LinkRejected, LinkError.NeedSecured), (rejected.Kind, rejected.LinkError));

        (client, server) = MemoryTransport.Pair();
        served = FakeSpiceServer.ServeLinkAsync(server, offerMiniHeader: false);
        var unsupported = await Assert.ThrowsAsync<TransportException>(() => ChannelLinker.LinkAsync(client, Message(), Array.Empty<byte>(), new FakeEncryptor(), Ct));
        await served;
        Assert.Equal(TransportErrorKind.UnsupportedServer, unsupported.Kind);
    }

    [Fact]
    public async Task Ticket_length_limit()
    {
        var (client, _) = MemoryTransport.Pair();
        var error = await Assert.ThrowsAsync<TransportException>(() =>
            ChannelLinker.LinkAsync(client, Message(), Enumerable.Repeat((byte)'A', 61).ToArray(), new FakeEncryptor(), Ct));
        Assert.Equal(TransportErrorKind.TicketTooLong, error.Kind);
    }

    [Fact]
    public async Task Bad_magic_and_oversized_reply_are_rejected()
    {
        foreach (var (magic, size) in new[] { (0xDEADBEEFu, 200u), (SpiceConstants.Magic, ChannelLinker.MaxReplySize + 1) })
        {
            var (client, server) = MemoryTransport.Pair();
            var reply = new ByteWriter();
            reply.WriteU32(magic);
            reply.WriteU32(2);
            reply.WriteU32(2);
            reply.WriteU32(size);
            await server.SendAsync(reply.ToArray(), Ct);
            var error = await Assert.ThrowsAsync<TransportException>(() => ChannelLinker.LinkAsync(client, Message(), Array.Empty<byte>(), new FakeEncryptor(), Ct));
            Assert.Equal(TransportErrorKind.ProtocolViolation, error.Kind);
        }
    }

    [Fact]
    public void Rsa_encryptor_produces_rsa1024_ciphertext()
    {
        var spki = File.ReadAllBytes(Fixtures.PathOf("tls", "ticket-key.spki.der"));
        Assert.Equal(SpiceConstants.TicketPublicKeyBytes, spki.Length);
        var ciphertext = new RsaTicketEncryptor().Encrypt("ticket\0"u8, spki);
        Assert.Equal(SpiceConstants.EncryptedTicketBytes, ciphertext.Length);

        // OAEP-SHA1 verificato a ritroso con una chiave generata qui.
        using var rsa = RSA.Create(1024);
        var encrypted = new RsaTicketEncryptor().Encrypt("ticket\0"u8, rsa.ExportSubjectPublicKeyInfo());
        Assert.Equal("ticket\0"u8.ToArray(), rsa.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA1));
    }

    [Fact]
    public void Certificate_subject_matches_host_subject()
    {
        var certificates = Pem.Certificates(File.ReadAllText(Fixtures.PathOf("tls", "test-server.pem")));
        Assert.NotNull(certificates);
        Assert.Single(certificates);
        var subject = HostSubject.SubjectOf(certificates[0]);
        Assert.NotNull(subject);
        Assert.Equal(["O", "OU", "CN"], subject.Select(a => a.Type));

        var expected = HostSubject.Parse("OU=PVE Cluster Node, O=Proxmox Virtual Environment,CN=pve-test.example.invalid");
        Assert.NotNull(expected);
        Assert.True(HostSubject.Matches(expected, subject));
        var wrong = HostSubject.Parse("OU=PVE Cluster Node,O=Proxmox Virtual Environment,CN=other");
        Assert.NotNull(wrong);
        Assert.False(HostSubject.Matches(wrong, subject));
    }

    [Fact]
    public void Pem_reads_several_certificates_and_refuses_unreadable_blocks()
    {
        var pem = File.ReadAllText(Fixtures.PathOf("tls", "test-server.pem"));
        Assert.Equal(2, Pem.Certificates(pem + pem)?.Count);
        Assert.Null(Pem.Certificates("garbage"));
        // Un blocco illeggibile accanto a uno valido: niente fiducia parziale.
        Assert.Null(Pem.Certificates(pem + "-----BEGIN CERTIFICATE-----\nAAEC\n-----END CERTIFICATE-----\n"));
    }

    [Fact]
    public void Host_subject_escaped_comma()
    {
        Assert.Equal([new NameComponent("CN", "a,b"), new NameComponent("O", "x")], HostSubject.Parse("CN=a\\,b,O=x"));
        Assert.Null(HostSubject.Parse("no-equals"));
        Assert.Null(HostSubject.Parse(""));
    }

    [Fact]
    public void Routes()
    {
        var both = new SpiceEndpoint("h") { Port = 5900, TlsPort = 5901 };
        Assert.Equal(new SpiceRoute(5901, true), both.Route(ChannelType.Display));
        var partial = new SpiceEndpoint("h") { Port = 5900, TlsPort = 5901, SecureKind = SecureChannelsKind.Some, SecureSet = new HashSet<ChannelType> { ChannelType.Main } };
        Assert.Equal(new SpiceRoute(5901, true), partial.Route(ChannelType.Main));
        Assert.Equal(new SpiceRoute(5900, false), partial.Route(ChannelType.Display));
        Assert.Equal(new SpiceRoute(5900, false), new SpiceEndpoint("h") { Port = 5900 }.Route(ChannelType.Main));
        Assert.Equal(new SpiceRoute(61000, true), new SpiceEndpoint("h") { TlsPort = 61000 }.Route(ChannelType.Cursor));
        Assert.Equal(TransportErrorKind.NoRoute, Assert.Throws<TransportException>(() => new SpiceEndpoint("h").Route(ChannelType.Main)).Kind);
    }

    /// <summary>Audit macOS, punto 1: <c>secure-channels</c> non viene mai declassato a TCP in chiaro.</summary>
    [Fact]
    public void Tls_requirement_is_never_downgraded()
    {
        var all = new SpiceEndpoint("h") { Port = 5900, SecureKind = SecureChannelsKind.All };
        foreach (var channel in new[] { ChannelType.Main, ChannelType.Display, ChannelType.Inputs, ChannelType.Usbredir, ChannelType.Smartcard })
        {
            Assert.Equal(TransportErrorKind.InsecureConfiguration, Assert.Throws<TransportException>(() => all.Route(channel)).Kind);
        }
        var some = new SpiceEndpoint("h") { Port = 5900, SecureKind = SecureChannelsKind.Some, SecureSet = new HashSet<ChannelType> { ChannelType.Main, ChannelType.Inputs } };
        Assert.Throws<TransportException>(() => some.Route(ChannelType.Main));
        Assert.Throws<TransportException>(() => some.Route(ChannelType.Inputs));
        Assert.Equal(new SpiceRoute(5900, false), some.Route(ChannelType.Display));
        // Il ripiego dopo NEED_SECURED va solo verso una porta TLS.
        Assert.Null(all.SecureRoute());
    }

    [Fact]
    public void Endpoint_from_profile_refuses_unusable_security_settings()
    {
        var profile = VVFileParser.Parse("[virt-viewer]\ntype=spice\nhost=h\ntls-port=61000\nsecure-channels=main;inputs\n").Profile;
        var endpoint = SpiceEndpoint.From(profile);
        Assert.True(endpoint.RequiresTls(ChannelType.Inputs));
        Assert.False(endpoint.RequiresTls(ChannelType.Display));

        profile.CaPem = "-----BEGIN CERTIFICATE-----\nAAEC\n-----END CERTIFICATE-----\n";
        Assert.Equal(TransportErrorKind.InsecureConfiguration, Assert.Throws<TransportException>(() => SpiceEndpoint.From(profile)).Kind);
        profile.CaPem = null;
        profile.HostSubject = "senza uguale";
        Assert.Equal(TransportErrorKind.InsecureConfiguration, Assert.Throws<TransportException>(() => SpiceEndpoint.From(profile)).Kind);
    }

    /// <summary>Audit macOS, punto 8: la destinazione del CONNECT non può iniettare intestazioni o richieste.</summary>
    [Fact]
    public void Http_connect_rejects_injection()
    {
        Assert.Null(HttpConnect.Request("host\r\nX-Evil: 1"));
        Assert.Null(HttpConnect.Request("host HTTP/1.1"));
        Assert.Null(HttpConnect.Request("a/b"));
        Assert.Null(HttpConnect.Request(""));
        Assert.Null(HttpConnect.Request("hòst:1"));
        Assert.NotNull(HttpConnect.Request("[fe80::1%25en0]:5900"));
    }

    [Fact]
    public void Http_connect()
    {
        var request = Encoding.ASCII.GetString(HttpConnect.Request("pvespiceproxy:abc:101:node::tok:61000")!);
        Assert.StartsWith("CONNECT pvespiceproxy:abc:101:node::tok:61000 HTTP/1.1\r\n", request, StringComparison.Ordinal);
        var response = "HTTP/1.1 200 OK\r\nX: y\r\n\r\nTLS"u8.ToArray();
        Assert.Equal(response.Length - 3, HttpConnect.HeaderEnd(response));
        Assert.Equal(200, HttpConnect.StatusCode(response));
        Assert.Equal(403, HttpConnect.StatusCode("HTTP/1.0 403 Forbidden\r\n\r\n"u8));
        Assert.Null(HttpConnect.StatusCode("SSH-2.0\r\n\r\n"u8));
        Assert.Equal(-1, HttpConnect.HeaderEnd("HTTP/1.1 200"u8));
    }
}

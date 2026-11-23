using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SpiceKit.Tests.Support;

/// <summary>Richiesta ricevuta dal server di prova.</summary>
public sealed record TestRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// Server HTTPS minimo in-process (una richiesta per connessione) per provare il client
/// Proxmox e le regole TLS senza un Proxmox vero. Un gestore che restituisce lo stato 0 fa
/// chiudere la connessione senza risposta (rete che cade).
/// </summary>
public sealed class TestHttpsServer : IDisposable
{
    // "localhost" si risolve prima in ::1: con un ascolto solo IPv4 ogni connessione
    // attenderebbe circa 2 secondi il rifiuto su IPv6.
    private readonly TcpListener _listener = new(IPAddress.IPv6Loopback, 0);
    private readonly X509Certificate2 _serverCertificate;
    private readonly Func<TestRequest, (int Status, string Body)> _handler;
    private readonly CancellationTokenSource _stop = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<TestRequest> Requests { get; } = [];

    public TestHttpsServer(X509Certificate2 serverCertificate, Func<TestRequest, (int Status, string Body)> handler)
    {
        _serverCertificate = serverCertificate;
        _handler = handler;
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            await using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(_serverCertificate);
            var request = await ReadRequestAsync(tls);
            lock (Requests) Requests.Add(request);
            var (status, body) = _handler(request);
            if (status == 0) return; // chiude la connessione senza rispondere
            var payload = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await tls.WriteAsync(Encoding.ASCII.GetBytes(head));
            await tls.WriteAsync(payload);
            await tls.FlushAsync();
        }
        catch (Exception)
        {
            // handshake rifiutato dal client: previsto in alcuni test
        }
    }

    private static async Task<TestRequest> ReadRequestAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (!(buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0) throw new IOException("connessione chiusa");
            buffer.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(l => l.Contains(':', StringComparison.Ordinal)))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var length = headers.TryGetValue("Content-Length", out var text) ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return new TestRequest(first[0], first[1], headers, Encoding.UTF8.GetString(body));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}

/// <summary>CA e certificato server di prova, generati al volo (nessun segreto nel repository).</summary>
public sealed class TestPki : IDisposable
{
    public X509Certificate2 Ca { get; }
    public X509Certificate2 Server { get; }
    public string CaPem => Ca.ExportCertificatePem();
    public string ServerSha256 => Convert.ToHexStringLower(SHA256.HashData(Server.RawData));

    public TestPki(string dnsName = "localhost")
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Proxmox Virtual Environment Test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("OU=PVE Cluster Node,O=Proxmox Virtual Environment,CN=" + dnsName, serverKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = RandomNumberGenerator.GetBytes(8);
        using var signed = request.Create(Ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), serial);
        using var withKey = signed.CopyWithPrivateKey(serverKey);
        // SChannel richiede una chiave non effimera: passaggio da PKCS#12.
        Server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    public void Dispose()
    {
        Ca.Dispose();
        Server.Dispose();
    }
}

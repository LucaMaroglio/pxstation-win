using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SpiceKit.Transport;

namespace SpiceKit.Tests.Support;

/// <summary>
/// Server SPICE finto su socket veri, in chiaro o TLS: ogni connessione fa l'handshake di link
/// (vedi <see cref="FakeSpiceServer"/>). Serve a provare <see cref="TcpConnector"/> end-to-end.
/// </summary>
public sealed class TestSpiceTlsServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.IPv6Loopback, 0);
    private readonly X509Certificate2? _certificate;
    private readonly CancellationTokenSource _stop = new();

    public TestSpiceTlsServer(X509Certificate2? certificate)
    {
        _certificate = certificate;
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public ushort Port => (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<ReceivedLink> Links { get; } = [];

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
        Stream stream = client.GetStream();
        try
        {
            if (_certificate is not null)
            {
                var tls = new SslStream(stream);
                await tls.AuthenticateAsServerAsync(_certificate);
                stream = tls;
            }
            await using var transport = new StreamTransport(stream, client);
            var link = await FakeSpiceServer.ServeLinkAsync(transport);
            lock (Links) Links.Add(link);
            // Resta aperto finché il client non chiude.
            await transport.ReceiveAsync(1);
        }
        catch (Exception)
        {
            // handshake rifiutato dal client o chiusura: previsto in alcuni test
            client.Dispose();
        }
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _listener.Stop();
    }
}

/// <summary>Proxy HTTP CONNECT come lo spiceproxy di Proxmox: inoltra verso una porta locale fissa.</summary>
public sealed class TestConnectProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.IPv6Loopback, 0);
    private readonly int _upstreamPort;
    private readonly int _status;

    public TestConnectProxy(int upstreamPort, int status = 200)
    {
        _upstreamPort = upstreamPort;
        _status = status;
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public ushort Port => (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<string> Requests { get; } = [];

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var header = new List<byte>();
        var one = new byte[1];
        while (!(header.Count >= 4 && Encoding.ASCII.GetString([.. header.TakeLast(4)]) == "\r\n\r\n"))
        {
            if (await stream.ReadAsync(one) == 0) return;
            header.Add(one[0]);
        }
        lock (Requests) Requests.Add(Encoding.ASCII.GetString([.. header]).Split("\r\n")[0]);
        if (_status != 200)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.0 {_status} Refused\r\n\r\n"));
            return;
        }
        using var upstream = new TcpClient(AddressFamily.InterNetworkV6);
        await upstream.ConnectAsync(IPAddress.IPv6Loopback, _upstreamPort);
        await stream.WriteAsync("HTTP/1.0 200 Connection established\r\n\r\n"u8.ToArray());
        var up = upstream.GetStream();
        await Task.WhenAny(stream.CopyToAsync(up), up.CopyToAsync(stream));
    }

    public void Dispose() => _listener.Stop();
}

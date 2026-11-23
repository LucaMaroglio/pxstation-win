using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SpiceKit.Transport;

/// <summary>
/// <see cref="IStreamConnector"/> con socket TCP, proxy HTTP CONNECT e <see cref="SslStream"/>.
/// </summary>
/// <remarks>
/// <para>Fiducia TLS: se il <c>.vv</c> contiene la CA, è l'<b>unica</b> radice; con
/// <c>host-subject</c> si verifica il soggetto al posto del nome host. Non c'è modo di
/// saltare la verifica.</para>
/// <para>Proxy: il TCP va al proxy, che con <c>CONNECT host:porta</c> apre il tunnel; il TLS
/// passa da un capo all'altro dentro il tunnel.</para>
/// </remarks>
public sealed class TcpConnector : IStreamConnector
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<ISpiceTransport> ConnectAsync(SpiceEndpoint endpoint, SpiceRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Stream? stream = null;
        try
        {
            ConfigureKeepAlive(socket);
            var (host, port) = endpoint.Proxy is { } proxy ? (proxy.Host, (int)proxy.Port) : (endpoint.Host, (int)route.Port);
            try
            {
                await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }
            catch (SocketException error)
            {
                throw TransportException.ConnectionFailed(error.SocketErrorCode == SocketError.ConnectionRefused
                    ? "connessione rifiutata" : error.Message, error);
            }
            stream = new NetworkStream(socket, ownsSocket: true);
            if (endpoint.Proxy is not null)
            {
                await OpenTunnelAsync(stream, $"{endpoint.Host}:{route.Port}", timeout.Token).ConfigureAwait(false);
            }
            if (route.UseTls)
            {
                stream = await AuthenticateAsync(stream, endpoint, timeout.Token).ConfigureAwait(false);
            }
            return new StreamTransport(stream, socket);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync(stream, socket).ConfigureAwait(false);
            throw TransportException.TimedOut();
        }
        catch
        {
            await DisposeAsync(stream, socket).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeAsync(Stream? stream, Socket socket)
    {
        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        socket.Dispose();
    }

    /// <summary>
    /// Collegamenti morti (sospensione, cambio di rete, NAT) scoperti in circa 30 secondi, così
    /// l'app può riconnettersi, invece delle ore previste dal sistema.
    /// </summary>
    private static void ConfigureKeepAlive(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
    }

    /// <summary>Scambio CONNECT; si legge un byte alla volta per non consumare i dati del TLS che seguono.</summary>
    internal static async Task OpenTunnelAsync(Stream stream, string target, CancellationToken cancellationToken)
    {
        var request = HttpConnect.Request(target)
            ?? throw TransportException.InsecureConfiguration("l'host non è una destinazione valida per il proxy");
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException error)
            {
                throw TransportException.ConnectionFailed(error.Message, error);
            }
            if (read == 0)
            {
                throw response.Count == 0 ? TransportException.Closed() : TransportException.ProxyProtocolError();
            }
            response.Add(one[0]);
            if (response.Count > HttpConnect.MaxResponseHeader) throw TransportException.ProxyProtocolError();
            if (response.Count >= 4 && response[^1] == '\n' && response[^2] == '\r' && response[^3] == '\n' && response[^4] == '\r')
            {
                break;
            }
        }
        var status = HttpConnect.StatusCode(response.ToArray()) ?? throw TransportException.ProxyProtocolError();
        if (status is < 200 or >= 300) throw TransportException.ProxyRefused(status);
    }

    private static async Task<Stream> AuthenticateAsync(Stream inner, SpiceEndpoint endpoint, CancellationToken cancellationToken)
    {
        var verifier = new TlsVerifier(endpoint);
        var tls = new SslStream(inner, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            // SNI e controllo del nome solo per nomi DNS o indirizzi IP: l'host Proxmox è opaco.
            TargetHost = TlsVerifier.IsCheckableHost(endpoint.Host) ? endpoint.Host : string.Empty,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = verifier.Validate,
        };
        try
        {
            await tls.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
            return tls;
        }
        catch (Exception error) when (error is AuthenticationException or IOException)
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            if (verifier.Failure is { } reason) throw TransportException.TlsVerificationFailed(reason, error);
            throw TransportException.ConnectionFailed("handshake TLS non riuscito: " + error.Message, error);
        }
    }
}

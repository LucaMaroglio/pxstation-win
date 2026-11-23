using SpiceKit.Transport;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>Connette e collega i canali di una sessione.</summary>
/// <param name="endpoint">Dove si trova il server.</param>
/// <param name="connector">Apre i flussi (TCP/TLS/proxy, o in memoria nei test).</param>
/// <param name="encryptor">Cifra il ticket.</param>
/// <param name="ticket">Copia i byte del ticket nel buffer dato e ne restituisce la lunghezza
/// (0 se già azzerato, -1 se non ci sta).</param>
public sealed class ChannelOpener(SpiceEndpoint endpoint, IStreamConnector connector, ITicketEncryptor encryptor,
    Func<byte[], int> ticket)
{
    /// <summary>Tempo massimo per connessione + link + autenticazione di un canale.</summary>
    public TimeSpan LinkTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public SpiceEndpoint Endpoint => endpoint;

    /// <summary>Apre <paramref name="type"/>/<paramref name="id"/> con <paramref name="connectionId"/> (0 per main, poi l'id di sessione).</summary>
    public async Task<ChannelConnection> OpenAsync(ChannelType type, byte id, uint connectionId, CapabilitySet channelCaps,
        CancellationToken cancellationToken = default)
    {
        var route = endpoint.Route(type);
        try
        {
            return await OpenAsync(type, id, connectionId, channelCaps, route, cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException error) when (error is { Kind: TransportErrorKind.LinkRejected, LinkError: LinkError.NeedSecured }
                                               && !route.UseTls)
        {
            var secure = endpoint.SecureRoute() ?? throw TransportException.LinkRejected(LinkError.NeedSecured);
            SpiceLog.Info("link", $"{type.Name()} richiede TLS: nuovo tentativo sulla porta TLS");
            return await OpenAsync(type, id, connectionId, channelCaps, secure, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ChannelConnection> OpenAsync(ChannelType type, byte id, uint connectionId, CapabilitySet channelCaps,
        SpiceRoute route, CancellationToken cancellationToken)
    {
        // Un server muto non deve bloccare la sessione: tutto l'handshake ha un limite di tempo.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LinkTimeout);
        ISpiceTransport? stream = null;
        var buffer = new byte[SpiceConstants.MaxPasswordLength + 1];
        try
        {
            stream = await connector.ConnectAsync(endpoint, route, timeout.Token).ConfigureAwait(false);
            var message = new LinkMessage(connectionId, type, id, ChannelLinker.ClientCommonCaps, channelCaps);
            var length = ticket(buffer);
            if (length < 0) throw TransportException.TicketTooLong();
            var linked = await ChannelLinker.LinkAsync(stream, message, buffer.AsMemory(0, length), encryptor, timeout.Token)
                .ConfigureAwait(false);
            SpiceLog.Debug("link", $"{type.Name()}:{id} collegato (tls={route.UseTls})");
            var connection = new ChannelConnection(type, id, stream, linked.ServerChannelCaps);
            stream = null;
            return connection;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TransportException.TimedOut();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

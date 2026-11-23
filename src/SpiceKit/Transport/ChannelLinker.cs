using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SpiceKit.Wire;

namespace SpiceKit.Transport;

/// <summary>Esito di link + autenticazione riusciti.</summary>
public sealed record LinkedChannel(CapabilitySet ServerCommonCaps, CapabilitySet ServerChannelCaps);

/// <summary>
/// Handshake di link SPICE e autenticazione con il ticket su un flusso già connesso.
/// </summary>
/// <remarks>
/// Sequenza (spice-protocol protocol.h):
/// <list type="number">
/// <item>client → <c>SpiceLinkHeader</c> + <c>SpiceLinkMess</c> + capacità;</item>
/// <item>server → <c>SpiceLinkHeader</c> + <c>SpiceLinkReply</c> (errore, chiave RSA, capacità);</item>
/// <item>con <c>PROTOCOL_AUTH_SELECTION</c>: client → uint32 <c>SPICE_COMMON_CAP_AUTH_SPICE</c>;</item>
/// <item>client → RSA-OAEP(ticket + NUL), 128 byte;</item>
/// <item>server → uint32 esito.</item>
/// </list>
/// </remarks>
public static class ChannelLinker
{
    /// <summary>Capacità comuni annunciate da ogni canale.</summary>
    public static CapabilitySet ClientCommonCaps => CapabilitySet.FromBits(
        (int)CommonCapability.ProtocolAuthSelection, (int)CommonCapability.AuthSpice, (int)CommonCapability.MiniHeader);

    /// <summary>Dimensione massima accettata per la risposta di link.</summary>
    public const uint MaxReplySize = 64 * 1024;

    public static async Task<LinkedChannel> LinkAsync(ISpiceTransport stream, LinkMessage message,
        ReadOnlyMemory<byte> ticket, ITicketEncryptor encryptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(encryptor);
        if (ticket.Length > SpiceConstants.MaxPasswordLength) throw TransportException.TicketTooLong();
        await stream.SendAsync(message.Encode(), cancellationToken).ConfigureAwait(false);

        var header = LinkHeader.Decode(await stream.ReceiveAsync(LinkHeader.Size, cancellationToken).ConfigureAwait(false));
        if (header.Magic != SpiceConstants.Magic) throw TransportException.ProtocolViolation("magic del link errato");
        if (header.Major != SpiceConstants.VersionMajor)
        {
            throw TransportException.UnsupportedServer(string.Create(CultureInfo.InvariantCulture,
                $"versione del protocollo {header.Major}.{header.Minor}"));
        }
        if (header.BodySize is < 4 or > MaxReplySize)
        {
            throw TransportException.ProtocolViolation(string.Create(CultureInfo.InvariantCulture,
                $"risposta di link di {header.BodySize} byte"));
        }
        var body = await stream.ReceiveAsync((int)header.BodySize, cancellationToken).ConfigureAwait(false);

        // Il codice di errore viene per primo; un link rifiutato può omettere il resto.
        var errorCode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (errorCode != (uint)LinkError.Ok) throw TransportException.LinkRejected((LinkError)errorCode);
        LinkReply reply;
        try
        {
            reply = LinkReply.Decode(body);
        }
        catch (WireException error)
        {
            throw TransportException.ProtocolViolation("risposta di link non valida", error);
        }

        if (!reply.CommonCaps.Contains(CommonCapability.MiniHeader))
        {
            throw TransportException.UnsupportedServer("il server non offre SPICE_COMMON_CAP_MINI_HEADER");
        }
        if (reply.CommonCaps.Contains(CommonCapability.ProtocolAuthSelection))
        {
            if (!reply.CommonCaps.Contains(CommonCapability.AuthSpice))
            {
                throw TransportException.UnsupportedServer("il server offre solo l'autenticazione SASL");
            }
            var mechanism = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(mechanism, (uint)CommonCapability.AuthSpice);
            await stream.SendAsync(mechanism, cancellationToken).ConfigureAwait(false);
        }

        var plaintext = new byte[ticket.Length + 1];
        byte[] ciphertext;
        try
        {
            ticket.Span.CopyTo(plaintext);
            ciphertext = encryptor.Encrypt(plaintext, reply.PublicKey);
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            throw TransportException.EncryptionFailed(error);
        }
        finally
        {
            // Azzerato in ogni caso, anche se la cifratura fallisce.
            CryptographicOperations.ZeroMemory(plaintext);
        }
        if (ciphertext.Length != SpiceConstants.EncryptedTicketBytes) throw TransportException.EncryptionFailed();
        await stream.SendAsync(ciphertext, cancellationToken).ConfigureAwait(false);

        var result = BinaryPrimitives.ReadUInt32LittleEndian(await stream.ReceiveAsync(4, cancellationToken).ConfigureAwait(false));
        if (result != (uint)LinkError.Ok) throw TransportException.AuthenticationFailed((LinkError)result);
        return new LinkedChannel(reply.CommonCaps, reply.ChannelCaps);
    }
}

/// <summary>Richiesta e risposta HTTP CONNECT per i tunnel <c>proxy=</c> dei file <c>.vv</c>.</summary>
public static class HttpConnect
{
    /// <summary>Limite all'intestazione della risposta del proxy.</summary>
    public const int MaxResponseHeader = 16 * 1024;

    /// <summary>
    /// La destinazione viene passata così com'è (Proxmox usa stringhe opache piene di due punti),
    /// ma solo se non può uscire dalla riga della richiesta: altrimenti <see langword="null"/>.
    /// </summary>
    public static byte[]? Request(string target) =>
        IsSafeTarget(target) ? Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n") : null;

    /// <summary>Solo lettere, cifre e <c>-._~:[]%</c>: niente spazi, controlli o delimitatori come <c>/ ? # @</c>.</summary>
    public static bool IsSafeTarget(string target) =>
        !string.IsNullOrEmpty(target) && target.Length <= 1024 && target.All(c => char.IsAsciiLetterOrDigit(c) || "-._~:[]%".Contains(c, StringComparison.Ordinal));

    /// <summary>Indice subito dopo la fine dell'intestazione, oppure -1 se incompleta.</summary>
    public static int HeaderEnd(ReadOnlySpan<byte> bytes)
    {
        var index = bytes.IndexOf("\r\n\r\n"u8);
        return index < 0 ? -1 : index + 4;
    }

    /// <summary>Codice di stato da <c>HTTP/1.x NNN motivo</c>, oppure <see langword="null"/>.</summary>
    public static int? StatusCode(ReadOnlySpan<byte> header)
    {
        var lineEnd = header.IndexOf((byte)'\r');
        if (lineEnd < 0) return null;
        var parts = Encoding.ASCII.GetString(header[..lineEnd]).Split(' ', 3);
        return parts.Length >= 2 && parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
               && parts[1].Length == 3 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            ? code : null;
    }
}

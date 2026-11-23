using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SpiceKit.Transport;

/// <summary>Un attributo di un nome distinto, es. <c>("CN", "pve1.example.com")</c>.</summary>
public readonly record struct NameComponent(string Type, string Value);

/// <summary>Gestione di <c>host-subject</c>: <c>OU=PVE Cluster Node,O=Proxmox Virtual Environment,CN=pve1</c>.</summary>
public static class HostSubject
{
    private static readonly Dictionary<string, string> ShortNames = new(StringComparer.Ordinal)
    {
        ["2.5.4.3"] = "CN", ["2.5.4.6"] = "C", ["2.5.4.7"] = "L", ["2.5.4.8"] = "ST",
        ["2.5.4.10"] = "O", ["2.5.4.11"] = "OU", ["2.5.4.5"] = "SERIALNUMBER",
        ["2.5.4.9"] = "STREET", ["0.9.2342.19200300.100.1.25"] = "DC",
        ["0.9.2342.19200300.100.1.1"] = "UID", ["1.2.840.113549.1.9.1"] = "EMAILADDRESS",
    };

    /// <summary>Divide sulle virgole non precedute da <c>\</c>; <see langword="null"/> se illeggibile.</summary>
    public static IReadOnlyList<NameComponent>? Parse(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var escaping = false;
        foreach (var c in text)
        {
            if (escaping)
            {
                current.Append(c);
                escaping = false;
            }
            else if (c == '\\') escaping = true;
            else if (c == ',')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        parts.Add(current.ToString());

        var attributes = new List<NameComponent>();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0) return null;
            var type = trimmed[..equals].Trim().ToUpperInvariant();
            if (type.Length == 0) return null;
            attributes.Add(new NameComponent(type, trimmed[(equals + 1)..].Trim()));
        }
        return attributes.Count == 0 ? null : attributes;
    }

    /// <summary>Soggetto di un certificato, nell'ordine in cui compare.</summary>
    public static IReadOnlyList<NameComponent>? SubjectOf(X509Certificate2 certificate)
    {
        try
        {
            var attributes = new List<NameComponent>();
            foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames(reversed: false))
            {
                if (rdn.HasMultipleElements) return null; // RDN multi-valore: non usati da Proxmox né da QEMU
                var oid = rdn.GetSingleElementType().Value ?? "";
                attributes.Add(new NameComponent(ShortNames.GetValueOrDefault(oid, oid), rdn.GetSingleElementValue() ?? ""));
            }
            return attributes;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Confronto senza ordine degli RDN (tipi senza distinzione di maiuscole, valori senza spazi ai lati).</summary>
    public static bool Matches(IReadOnlyList<NameComponent> expected, IReadOnlyList<NameComponent> actual)
    {
        static List<string> Normalize(IEnumerable<NameComponent> list) =>
            [.. list.Select(a => a.Type.ToUpperInvariant() + "=" + a.Value.Trim()).Order(StringComparer.Ordinal)];
        return Normalize(expected).SequenceEqual(Normalize(actual), StringComparer.Ordinal);
    }
}

/// <summary>PEM → certificati per la voce <c>ca</c> del <c>.vv</c> (uno o più).</summary>
public static class Pem
{
    /// <summary>
    /// Tutti i certificati del testo; <see langword="null"/> se un blocco <c>BEGIN CERTIFICATE</c>
    /// non si legge (una CA inutilizzabile non deve mai diventare "fiducia di sistema").
    /// </summary>
    public static X509Certificate2Collection? Certificates(string text)
    {
        var blocks = CountOccurrences(text, "-----BEGIN CERTIFICATE-----");
        var result = new X509Certificate2Collection();
        try
        {
            var remaining = text.AsSpan();
            while (PemEncoding.TryFind(remaining, out var fields))
            {
                if (remaining[fields.Label].SequenceEqual("CERTIFICATE"))
                {
                    var der = Convert.FromBase64String(remaining[fields.Base64Data].ToString());
                    result.Add(X509CertificateLoader.LoadCertificate(der));
                }
                remaining = remaining[fields.Location.End..];
            }
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            return null;
        }
        return result.Count == 0 || result.Count != blocks ? null : result;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}

/// <summary>
/// Cifra il ticket SPICE con la chiave RSA-1024 del server (OAEP, SHA-1), come richiesto da
/// <c>SpiceLinkEncryptedTicket</c> (protocol.h).
/// </summary>
public interface ITicketEncryptor
{
    /// <param name="plaintext">Byte del ticket seguiti dal terminatore NUL.</param>
    /// <param name="subjectPublicKeyInfo">I 162 byte DER della chiave da <c>SpiceLinkReply</c>.</param>
    /// <returns>128 byte di testo cifrato.</returns>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> subjectPublicKeyInfo);
}

/// <summary>Implementazione con <see cref="RSA"/> di sistema (CNG).</summary>
public sealed class RsaTicketEncryptor : ITicketEncryptor
{
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        return rsa.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA1);
    }
}

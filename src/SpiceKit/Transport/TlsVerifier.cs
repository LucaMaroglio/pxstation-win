using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SpiceKit.Transport;

/// <summary>
/// Verifica del certificato del server SPICE (AGENTS.md §6, porting di <c>TLSVerifier</c> macOS).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Con la CA del <c>.vv</c>: catena costruita solo verso quelle radici
/// (<see cref="X509ChainTrustMode.CustomRootTrust"/>), con politica "server TLS".</item>
/// <item>Senza CA: fiducia di sistema.</item>
/// <item>Con <c>host-subject</c>: il soggetto del certificato deve coincidere (gli host Proxmox
/// sono stringhe opache, non nomi DNS); senza, si verifica il nome host come spice-gtk.</item>
/// </list>
/// Nessun ripiego: una CA illeggibile o un soggetto diverso rifiutano la connessione.
/// </remarks>
public sealed class TlsVerifier(SpiceEndpoint endpoint)
{
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

    /// <summary>Motivo dell'ultimo rifiuto (per l'errore mostrato; può contenere nomi di host).</summary>
    public string? Failure { get; private set; }

    /// <summary>Il nome host si può verificare (nome DNS o indirizzo IP, non una stringa Proxmox).</summary>
    public static bool IsCheckableHost(string host) =>
        Uri.CheckHostName(host.Trim('[', ']')) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        Failure = Evaluate(certificate, chain, errors);
        return Failure is null;
    }

    private string? Evaluate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return "il server non ha inviato un certificato";
        }
        using var leaf = new X509Certificate2(certificate);

        if (endpoint.TrustAnchors.Count > 0)
        {
            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.AddRange(endpoint.TrustAnchors);
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.ApplicationPolicy.Add(ServerAuthentication);
            // Intermedi inviati dal server: utili per la catena, mai come radici.
            if (chain is not null)
            {
                foreach (var element in chain.ChainElements) custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
            if (!custom.Build(leaf))
            {
                var status = string.Join(", ", custom.ChainStatus.Select(s => s.Status.ToString()));
                return "il certificato non è firmato dalla CA del file di connessione (" + status + ")";
            }
        }
        else if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            return "il certificato non è attendibile per Windows";
        }

        if (endpoint.HostSubject is { } expected)
        {
            var actual = HostSubject.SubjectOf(leaf);
            if (actual is null) return "il soggetto del certificato non si legge";
            if (!HostSubject.Matches(expected, actual)) return "il soggetto del certificato non corrisponde a host-subject";
        }
        else
        {
            // Senza host-subject il nome host va verificato: un host opaco non si può verificare.
            if (!IsCheckableHost(endpoint.Host)) return "l'host non è un nome verificabile e manca host-subject";
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) return "il certificato è intestato a un altro host";
        }
        return null;
    }
}

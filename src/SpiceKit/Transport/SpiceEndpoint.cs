using System.Security.Cryptography.X509Certificates;
using SpiceKit.VVFile;
using SpiceKit.Wire;

namespace SpiceKit.Transport;

/// <summary>Come aprire un canale: porta e uso di TLS.</summary>
public readonly record struct SpiceRoute(ushort Port, bool UseTls);

/// <summary>Dove e come raggiungere un server SPICE (vista di trasporto di un profilo <c>.vv</c>).</summary>
public sealed class SpiceEndpoint
{
    public SpiceEndpoint(string host) => Host = host;

    /// <summary>Host del server, oppure destinazione opaca per il proxy (Proxmox) passata così com'è nel CONNECT.</summary>
    public string Host { get; init; }
    public ushort? Port { get; init; }
    public ushort? TlsPort { get; init; }
    public ProxyEndpoint? Proxy { get; init; }

    /// <summary>Le sole radici di fiducia (dalla voce <c>ca</c>); vuoto = fiducia di sistema.</summary>
    public X509Certificate2Collection TrustAnchors { get; init; } = [];

    /// <summary>Soggetto atteso del certificato del server (<c>host-subject</c>), già letto.</summary>
    public IReadOnlyList<NameComponent>? HostSubject { get; init; }

    /// <summary>Canali che devono usare TLS (<c>secure-channels</c>), come maschera di <see cref="ChannelType"/>.</summary>
    public SecureChannelsKind SecureKind { get; init; } = SecureChannelsKind.Unspecified;
    public IReadOnlySet<ChannelType> SecureSet { get; init; } = new HashSet<ChannelType>();

    public bool RequiresTls(ChannelType channel) => SecureKind switch
    {
        SecureChannelsKind.All => true,
        SecureChannelsKind.Some => SecureSet.Contains(channel),
        _ => false,
    };

    /// <summary>Sceglie TLS o in chiaro per un canale.</summary>
    /// <remarks>
    /// Solo porta TLS → TLS. Solo porta in chiaro → in chiaro, a meno che <c>secure-channels</c>
    /// richieda TLS per il canale: allora la connessione viene rifiutata, mai declassata.
    /// Entrambe → TLS, a meno che <c>secure-channels</c> sia indicato e non elenchi il canale
    /// (in chiaro, come fa virt-viewer).
    /// </remarks>
    public SpiceRoute Route(ChannelType channel)
    {
        switch (Port, TlsPort)
        {
            case (null, null):
                throw TransportException.NoRoute();
            case ({ } plain, null):
                if (RequiresTls(channel))
                {
                    throw TransportException.InsecureConfiguration($"il canale {channel.Name()} richiede TLS ma manca la porta TLS");
                }
                return new SpiceRoute(plain, false);
            case (null, { } tls):
                return new SpiceRoute(tls, true);
            case ({ } plain, { } tls):
                return SecureKind == SecureChannelsKind.Some && !SecureSet.Contains(channel)
                    ? new SpiceRoute(plain, false)
                    : new SpiceRoute(tls, true);
        }
    }

    /// <summary>Ripiego dopo <c>SPICE_LINK_ERR_NEED_SECURED</c> su una connessione in chiaro: solo verso una porta TLS.</summary>
    public SpiceRoute? SecureRoute() => TlsPort is { } tls ? new SpiceRoute(tls, true) : null;

    /// <summary>
    /// Traduce un profilo <c>.vv</c>. Lancia se la CA o <c>host-subject</c> non si possono usare:
    /// un'impostazione di sicurezza che non si può rispettare non deve diventare più debole.
    /// </summary>
    public static SpiceEndpoint From(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        X509Certificate2Collection anchors = [];
        if (profile.CaPem is { } pem)
        {
            anchors = Pem.Certificates(pem) ?? throw TransportException.InsecureConfiguration("il certificato della CA non si legge");
        }
        IReadOnlyList<NameComponent>? subject = null;
        if (profile.HostSubject is { } text)
        {
            subject = Transport.HostSubject.Parse(text)
                ?? throw TransportException.InsecureConfiguration("il soggetto atteso del certificato del server non si legge");
        }
        var secure = new HashSet<ChannelType>();
        foreach (var name in Enum.GetValues<ChannelName>())
        {
            if ((profile.SecureChannels.Channels & name.ToMask()) == 0) continue;
            secure.Add(name switch
            {
                ChannelName.Main => ChannelType.Main,
                ChannelName.Display => ChannelType.Display,
                ChannelName.Inputs => ChannelType.Inputs,
                ChannelName.Cursor => ChannelType.Cursor,
                ChannelName.Playback => ChannelType.Playback,
                ChannelName.Record => ChannelType.Record,
                ChannelName.Smartcard => ChannelType.Smartcard,
                ChannelName.Usbredir => ChannelType.Usbredir,
                ChannelName.Port => ChannelType.Port,
                _ => ChannelType.Webdav,
            });
        }
        return new SpiceEndpoint(profile.Host)
        {
            Port = profile.Port,
            TlsPort = profile.TlsPort,
            Proxy = profile.Proxy,
            TrustAnchors = anchors,
            HostSubject = subject,
            SecureKind = profile.SecureChannels.Kind,
            SecureSet = secure,
        };
    }
}

/// <summary>Apre flussi verso un endpoint; nei test lo si sostituisce con flussi in memoria.</summary>
public interface IStreamConnector
{
    Task<ISpiceTransport> ConnectAsync(SpiceEndpoint endpoint, SpiceRoute route, CancellationToken cancellationToken);
}

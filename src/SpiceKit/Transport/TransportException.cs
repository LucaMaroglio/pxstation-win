using System.Globalization;
using SpiceKit.Wire;

namespace SpiceKit.Transport;

public enum TransportErrorKind
{
    ConnectionFailed,
    Closed,
    TimedOut,
    TlsVerificationFailed,
    ProxyRefused,
    ProxyProtocolError,
    /// <summary>Il server ha rifiutato il link (<c>SpiceLinkErr</c>).</summary>
    LinkRejected,
    /// <summary>Il server ha rifiutato il ticket.</summary>
    AuthenticationFailed,
    UnsupportedServer,
    ProtocolViolation,
    TicketTooLong,
    EncryptionFailed,
    /// <summary>
    /// Il file chiede una sicurezza che la connessione non può dare (TLS richiesto senza porta
    /// TLS, CA o <c>host-subject</c> illeggibili).
    /// </summary>
    InsecureConfiguration,
    /// <summary>Nessuna porta configurata per questo canale.</summary>
    NoRoute,
}

/// <summary>Errori di connessione, link e autenticazione di un canale, con messaggi presentabili.</summary>
public sealed class TransportException : Exception
{
    private TransportException(TransportErrorKind kind, string message, string? detail = null,
        int proxyStatus = 0, LinkError linkError = LinkError.Ok, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Detail = detail;
        ProxyStatus = proxyStatus;
        LinkError = linkError;
    }

    public TransportErrorKind Kind { get; }

    /// <summary>Dettaglio tecnico (può contenere nomi di host: non mostrarlo nei log in chiaro).</summary>
    public string? Detail { get; }

    public int ProxyStatus { get; }

    public LinkError LinkError { get; }

    /// <summary>Il ticket è scaduto o non valido: serve un file di connessione nuovo.</summary>
    public bool IsAuthenticationFailure =>
        Kind == TransportErrorKind.AuthenticationFailed || (Kind == TransportErrorKind.ProxyRefused && ProxyStatus is 401 or 403);

    public static TransportException ConnectionFailed(string reason, Exception? inner = null) =>
        new(TransportErrorKind.ConnectionFailed, $"Impossibile collegarsi al server ({reason}).", reason, inner: inner);

    public static TransportException Closed() => new(TransportErrorKind.Closed, "Il server ha chiuso la connessione.");

    public static TransportException TimedOut() => new(TransportErrorKind.TimedOut, "Tempo scaduto durante la connessione.");

    public static TransportException TlsVerificationFailed(string reason, Exception? inner = null) =>
        new(TransportErrorKind.TlsVerificationFailed,
            "Il certificato del server non è stato verificato. La connessione è stata interrotta per sicurezza.", reason, inner: inner);

    public static TransportException ProxyRefused(int status) => new(TransportErrorKind.ProxyRefused,
        status is 401 or 403
            ? string.Create(CultureInfo.InvariantCulture,
                $"Il proxy ha rifiutato la connessione (HTTP {status}). Con Proxmox di solito il file di connessione è scaduto: premi di nuovo SPICE e apri subito il file nuovo.")
            : string.Create(CultureInfo.InvariantCulture, $"Il proxy ha rifiutato la connessione (HTTP {status})."),
        proxyStatus: status);

    public static TransportException ProxyProtocolError() =>
        new(TransportErrorKind.ProxyProtocolError, "Il proxy ha inviato una risposta non valida.");

    public static TransportException LinkRejected(LinkError error) => new(TransportErrorKind.LinkRejected,
        $"Il server ha rifiutato la connessione ({Describe(error)}).", linkError: error);

    public static TransportException AuthenticationFailed(LinkError error) => new(TransportErrorKind.AuthenticationFailed,
        error == LinkError.PermissionDenied
            ? "Il ticket è scaduto o non è valido. Scarica un file di connessione nuovo e riprova."
            : $"Autenticazione non riuscita ({Describe(error)}).",
        linkError: error);

    public static TransportException UnsupportedServer(string reason) =>
        new(TransportErrorKind.UnsupportedServer, $"Server non supportato: {reason}.", reason);

    public static TransportException ProtocolViolation(string reason, Exception? inner = null) =>
        new(TransportErrorKind.ProtocolViolation, $"Il server ha inviato dati inattesi ({reason}).", reason, inner: inner);

    public static TransportException TicketTooLong() =>
        new(TransportErrorKind.TicketTooLong, "Il ticket nel file di connessione è troppo lungo.");

    public static TransportException EncryptionFailed(Exception? inner = null) =>
        new(TransportErrorKind.EncryptionFailed, "Impossibile cifrare il ticket per il server.", inner: inner);

    public static TransportException InsecureConfiguration(string reason) => new(TransportErrorKind.InsecureConfiguration,
        $"Le impostazioni di sicurezza del file di connessione non si possono rispettare ({reason}). Connessione non avviata.", reason);

    public static TransportException NoRoute() =>
        new(TransportErrorKind.NoRoute, "Il file di connessione non indica una porta per questa connessione.");

    private static string Describe(LinkError error) => error switch
    {
        LinkError.InvalidMagic or LinkError.InvalidData => "dati non validi",
        LinkError.VersionMismatch => "versione del protocollo diversa",
        LinkError.NeedSecured => "serve una connessione cifrata",
        LinkError.NeedUnsecured => "serve una connessione non cifrata",
        LinkError.PermissionDenied => "permesso negato",
        LinkError.BadConnectionId => "sessione sconosciuta",
        LinkError.ChannelNotAvailable => "canale non disponibile",
        _ => "errore " + ((uint)error).ToString(CultureInfo.InvariantCulture),
    };
}

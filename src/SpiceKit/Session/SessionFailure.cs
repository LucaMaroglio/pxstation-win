using SpiceKit.Channels;
using SpiceKit.Transport;

namespace SpiceKit.Session;

/// <summary>Che cosa potrebbe ottenere una riconnessione.</summary>
public enum SessionRecovery
{
    /// <summary>La rete è caduta o è scaduto il tempo: vale la pena riconnettersi da soli.</summary>
    Automatic,

    /// <summary>
    /// La sessione l'ha chiusa il server o il proxy (VM spenta, altro client, ticket scaduto):
    /// riconnettersi solo se lo chiede l'utente.
    /// </summary>
    Manual,

    /// <summary>Con le stesse impostazioni fallirebbe di nuovo (sicurezza, configurazione, protocollo).</summary>
    None,
}

/// <summary>Motivo presentabile per cui una sessione è finita.</summary>
public sealed record SessionFailure(string Message, bool IsAuthenticationFailure = false, SessionRecovery Recovery = SessionRecovery.None)
{
    public static SessionFailure From(Exception error) => error switch
    {
        TransportException transport when transport.IsAuthenticationFailure =>
            new(transport.Message, true, SessionRecovery.Manual),
        TransportException { Kind: TransportErrorKind.TimedOut or TransportErrorKind.ConnectionFailed } transport =>
            new(transport.Message, false, SessionRecovery.Automatic),
        TransportException { Kind: TransportErrorKind.Closed or TransportErrorKind.ProxyRefused } transport =>
            new(transport.Message, false, SessionRecovery.Manual),
        TransportException transport => new(transport.Message),
        ChannelException { Kind: ChannelErrorKind.ServerDisconnected } =>
            new("Il server ha terminato la sessione.", false, SessionRecovery.Manual),
        ChannelException { Kind: ChannelErrorKind.MigrationUnsupported } channel =>
            new(channel.Message, false, SessionRecovery.Automatic),
        ChannelException channel => new(channel.Message, false, SessionRecovery.Manual),
        _ => new($"La sessione si è interrotta in modo inatteso ({error.GetType().Name}).", false, SessionRecovery.Manual),
    };
}

public enum SessionStateKind
{
    Idle,
    Connecting,
    Connected,
    Disconnected,
}

/// <summary>Stato della sessione; <see cref="Failure"/> è nullo per una chiusura voluta.</summary>
public sealed record SessionState(SessionStateKind Kind, SessionFailure? Failure = null)
{
    public static SessionState Idle { get; } = new(SessionStateKind.Idle);
    public static SessionState Connecting { get; } = new(SessionStateKind.Connecting);
    public static SessionState Connected { get; } = new(SessionStateKind.Connected);
    public static SessionState Disconnected(SessionFailure? failure) => new(SessionStateKind.Disconnected, failure);
}

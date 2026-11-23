namespace SpiceKit.Session;

/// <summary>
/// Quando e quante volte riconnettersi dopo una caduta (porting di <c>ReconnectPolicy.swift</c>,
/// come <c>ReconnectPolicy</c> del client Linux).
/// </summary>
/// <remarks>
/// Solo i problemi di rete fanno partire la riconnessione automatica: una sessione chiusa di
/// proposito dal server (ospite spento, altro client) resta chiusa, così due client non si
/// contendono la stessa VM. I tentativi si diradano e si fermano dopo <see cref="Delays"/>.
/// </remarks>
public sealed class ReconnectPolicy
{
    /// <summary>Attesa prima dei tentativi 1, 2, …; poi basta (circa due minuti in tutto).</summary>
    public IReadOnlyList<TimeSpan> Delays { get; init; } =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
    ];

    /// <summary>Una sessione rimasta su così a lungo ricomincia dal tentativo 1 quando cade.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxAttempts => Delays.Count;

    /// <summary>Attesa prima del tentativo (da 1), o <see langword="null"/> per arrendersi.</summary>
    public TimeSpan? Delay(int attempt) => attempt >= 1 && attempt <= Delays.Count ? Delays[attempt - 1] : null;

    /// <summary>Se riprovare dopo <paramref name="failure"/>.</summary>
    /// <param name="failure">Motivo della fine; <see langword="null"/> = chiusa dall'utente.</param>
    /// <param name="wasConnected">La sessione finita era collegata del tutto.</param>
    /// <param name="reconnecting">La sessione finita era essa stessa un tentativo di riconnessione.</param>
    public static bool ShouldReconnect(SessionFailure? failure, bool wasConnected, bool reconnecting) => failure switch
    {
        null => false,
        { Recovery: SessionRecovery.Automatic } => wasConnected || reconnecting,
        // Mentre la rete torna, un proxy può chiudere o rifiutare: si continua entro i tentativi,
        // ma mai dopo un ticket rifiutato.
        { Recovery: SessionRecovery.Manual } => reconnecting && !wasConnected && !failure.IsAuthenticationFailure,
        _ => false,
    };
}

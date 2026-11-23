namespace SpiceKit.Wire;

/// <summary>Tipo di errore incontrato decodificando dati ricevuti dalla rete.</summary>
public enum WireErrorKind
{
    /// <summary>Lettura oltre la fine del messaggio.</summary>
    Truncated,

    /// <summary>Un campo puntatore/offset punta fuori dal messaggio.</summary>
    BadOffset,

    /// <summary>Un valore è fuori dall'intervallo ammesso dal protocollo o dai nostri limiti.</summary>
    InvalidValue,

    /// <summary>Una dimensione supera un limite di sicurezza configurato.</summary>
    LimitExceeded,
}

/// <summary>
/// Errore nei dati del server. I dati del server sono ostili per definizione (AGENTS.md §6):
/// ogni lettura è controllata e un errore chiude il canale, non il programma.
/// </summary>
public sealed class WireException : Exception
{
    public WireException(WireErrorKind kind, string message) : base(message) => Kind = kind;

    public WireErrorKind Kind { get; }

    internal static WireException Truncated(int needed, int available) =>
        new(WireErrorKind.Truncated, $"Messaggio troncato: servono {needed} byte, disponibili {available}.");

    internal static WireException BadOffset(uint pointer) =>
        new(WireErrorKind.BadOffset, $"Offset fuori dal messaggio: {pointer}.");

    public static WireException InvalidValue(string what) => new(WireErrorKind.InvalidValue, what);

    public static WireException LimitExceeded(string what) => new(WireErrorKind.LimitExceeded, what);
}

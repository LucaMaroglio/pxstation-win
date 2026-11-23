namespace SpiceKit.VVFile;

/// <summary>Motivi per cui un file <c>.vv</c> non è utilizzabile.</summary>
public enum VVFileErrorKind
{
    NotUtf8,
    MissingVirtViewerSection,
    UnsupportedProtocol,
    MissingKey,
    InvalidValue,
    NoPort,
    TooLarge,
}

/// <summary>
/// Errore che rende inutilizzabile un file di connessione. <see cref="Exception.Message"/> è
/// in italiano e si può mostrare all'utente.
/// </summary>
public sealed class VVFileException : Exception
{
    public VVFileErrorKind Kind { get; }

    /// <summary>Chiave o protocollo coinvolti, se pertinenti.</summary>
    public string? Detail { get; }

    public VVFileException(VVFileErrorKind kind, string? detail = null)
        : base(Describe(kind, detail))
    {
        Kind = kind;
        Detail = detail;
    }

    public VVFileException() : this(VVFileErrorKind.InvalidValue) { }

    public VVFileException(string message) : base(message) { }

    public VVFileException(string message, Exception innerException) : base(message, innerException) { }

    private static string Describe(VVFileErrorKind kind, string? detail) => kind switch
    {
        VVFileErrorKind.NotUtf8 => "Il file di connessione non è testo UTF-8 valido.",
        VVFileErrorKind.MissingVirtViewerSection => "Il file di connessione non contiene una sezione [virt-viewer].",
        VVFileErrorKind.UnsupportedProtocol => $"Il file di connessione usa il protocollo “{detail}”. È supportato solo SPICE.",
        VVFileErrorKind.MissingKey => $"Nel file di connessione manca la voce obbligatoria “{detail}”.",
        VVFileErrorKind.InvalidValue => $"Il file di connessione ha un valore non valido per “{detail}”.",
        VVFileErrorKind.NoPort => "Il file di connessione non indica né una porta né una porta TLS.",
        VVFileErrorKind.TooLarge => "Il file di connessione è troppo grande.",
        _ => "Il file di connessione non è valido.",
    };
}

/// <summary>Problemi non bloccanti trovati durante la lettura.</summary>
public abstract record VVFileWarning
{
    private VVFileWarning() { }

    public sealed record MalformedLine(int Line) : VVFileWarning;
    public sealed record DuplicateKey(string Key, int Line) : VVFileWarning;
    public sealed record KeyOutsideSection(int Line) : VVFileWarning;
    public sealed record InvalidOptionalValue(string Key) : VVFileWarning;
    public sealed record InvalidEscape(string Key) : VVFileWarning;
    public sealed record UnknownKey(string Key) : VVFileWarning;
}

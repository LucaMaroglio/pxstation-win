namespace SpiceKit.Channels;

public enum SpiceLogLevel
{
    Debug,
    Info,
    Error,
}

/// <summary>
/// Registro di SpiceKit, senza dipendenze: l'app collega <see cref="Sink"/> al proprio log.
/// Mai ticket o CA nei messaggi; testo del server sempre passato da <c>WireText.Sanitize</c>.
/// </summary>
public static class SpiceLog
{
    public static Action<SpiceLogLevel, string>? Sink { get; set; }

    public static void Debug(string category, string message) => Sink?.Invoke(SpiceLogLevel.Debug, $"[{category}] {message}");

    public static void Info(string category, string message) => Sink?.Invoke(SpiceLogLevel.Info, $"[{category}] {message}");

    public static void Error(string category, string message) => Sink?.Invoke(SpiceLogLevel.Error, $"[{category}] {message}");
}

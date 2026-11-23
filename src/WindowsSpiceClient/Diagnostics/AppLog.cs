using System.Globalization;
using System.Text;

namespace WindowsSpiceClient.Diagnostics;

/// <summary>
/// Registro minimo su file in <c>%LOCALAPPDATA%\WindowsSpiceClient\logs</c>, usato da M0 per gli
/// errori non gestiti. Sarà sostituito da Microsoft.Extensions.Logging (AGENTS.md §2).
/// Non scrivere mai qui ticket, password o token (AGENTS.md §5).
/// </summary>
internal static class AppLog
{
    private static readonly Lock Gate = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsSpiceClient", "logs");

    public static string FilePath { get; } = Path.Combine(Directory, "app.log");

    /// <summary>Messaggi di debug solo con <c>WSC_LOG_DEBUG=1</c> (protocollo, messaggi ignorati).</summary>
    public static bool DebugEnabled { get; } = Environment.GetEnvironmentVariable("WSC_LOG_DEBUG") == "1";

    public static void Debug(string message)
    {
        if (DebugEnabled) Write("DEBUG", message);
    }

    public static void Info(string message) => Write("INFO ", message);

    /// <summary>Collega il registro di SpiceKit a questo file.</summary>
    public static void AttachSpiceKit() => SpiceKit.Channels.SpiceLog.Sink = (level, message) =>
    {
        switch (level)
        {
            case SpiceKit.Channels.SpiceLogLevel.Debug: Debug(message); break;
            case SpiceKit.Channels.SpiceLogLevel.Info: Info(message); break;
            default: Error(message); break;
        }
    };

    public static void Error(string message, Exception? error = null) =>
        Write("ERROR", error is null ? message : $"{message}\n{error}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Il registro non deve mai far fallire l'app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

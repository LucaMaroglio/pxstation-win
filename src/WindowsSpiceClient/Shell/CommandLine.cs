using System.Text.RegularExpressions;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Shell;

/// <summary>Che cosa chiede un avvio: file <c>.vv</c> da aprire o un comando della jump list.</summary>
internal sealed record LaunchRequest(IReadOnlyList<string> Files, string? Command);

/// <summary>
/// Argomenti della riga di comando, sia del primo avvio sia di quelli passati dall'istanza unica:
/// file <c>.vv</c> esistenti e i comandi delle attività della jump list.
/// </summary>
internal static partial class CommandLine
{
    public const string VmList = "--vm-list";
    public const string OpenFile = "--open";
    public const string Preferences = "--preferences";

    /// <summary>Usato dal programma di disinstallazione: toglie ciò che l'app ha registrato in Windows.</summary>
    public const string Unregister = "--unregister";

    private static readonly string[] Commands = [VmList, OpenFile, Preferences];

    public static LaunchRequest Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        var files = list.Where(a => a.EndsWith(FileAssociation.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a)).ToList();
        var command = list.FirstOrDefault(a => Commands.Contains(a, StringComparer.OrdinalIgnoreCase))?.ToLowerInvariant();
        return new LaunchRequest(files, command);
    }

    /// <summary>Riga di comando intera (con o senza virgolette), come la passa <c>AppInstance</c>.</summary>
    public static LaunchRequest Parse(string commandLine) =>
        Parse(Tokens().Matches(commandLine).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));

    [GeneratedRegex("\"([^\"]*)\"|(\\S+)")]
    private static partial Regex Tokens();
}

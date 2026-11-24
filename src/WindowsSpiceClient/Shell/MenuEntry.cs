namespace WindowsSpiceClient.Shell;

/// <summary>
/// Voce di menu indipendente dal disegno (menu dell'icona nell'area di notifica), come
/// <c>MenuEntry</c> del client Linux: comando, sottomenu, etichetta informativa o separatore.
/// </summary>
internal sealed record MenuEntry(string Text, Action? Invoke = null, IReadOnlyList<MenuEntry>? Children = null, bool Enabled = true)
{
    /// <summary>Etichetta non selezionabile (intestazioni, messaggi).</summary>
    public bool IsInfo => Invoke is null && Children is null && !IsSeparator;

    public bool IsSeparator { get; private init; }

    public static MenuEntry Info(string text) => new(text, Enabled: false);

    public static MenuEntry Separator() => new(string.Empty) { IsSeparator = true };

    /// <summary>Testi del menu in forma leggibile (log e autoverifica): «│» separa i livelli.</summary>
    public static IEnumerable<string> Describe(IEnumerable<MenuEntry> entries, string prefix = "")
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator) continue;
            yield return prefix + entry.Text + (entry.Enabled ? "" : " (disattivata)");
            if (entry.Children is { } children)
            {
                foreach (var line in Describe(children, prefix + entry.Text + " │ ")) yield return line;
            }
        }
    }
}

using System.Globalization;

namespace SpiceKit.VVFile;

[Flags]
public enum HotkeyModifiers : byte
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    /// <summary>Super / Windows / Meta.</summary>
    Super = 1 << 3,
}

/// <summary>
/// Combinazione di tasti di un file <c>.vv</c>, per esempio <c>Ctrl+Alt+R</c> o <c>Shift+F11</c>.
/// </summary>
/// <remarks>
/// virt-viewer accetta sia <c>Ctrl+Alt+R</c> sia la sintassi degli acceleratori GTK
/// (<c>&lt;Ctrl&gt;&lt;Alt&gt;R</c>). Il tasto è un nome normalizzato: lettere e cifre maiuscole
/// (<c>R</c>, <c>7</c>), <c>F1</c>…<c>F24</c> e nomi canonici (<c>Insert</c>, <c>Delete</c>,
/// <c>Home</c>, <c>End</c>, <c>PageUp</c>, <c>PageDown</c>, <c>Escape</c>, <c>Return</c>,
/// <c>Tab</c>, <c>Space</c>, <c>BackSpace</c>, <c>Left</c>, <c>Right</c>, <c>Up</c>,
/// <c>Down</c>, <c>Pause</c>, <c>Print</c>).
/// </remarks>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, string Key)
{
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Super)) parts.Add("Super");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    /// <summary>Legge <c>Ctrl+Alt+R</c>, <c>shift+f11</c>, <c>&lt;Control&gt;&lt;Alt&gt;Delete</c>.</summary>
    public static Hotkey? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<string>();
        var remaining = text.Trim();

        // Sintassi GTK: <Mod><Mod>Tasto
        while (remaining.StartsWith('<'))
        {
            var close = remaining.IndexOf('>', StringComparison.Ordinal);
            if (close < 0) break;
            tokens.Add(remaining[1..close]);
            remaining = remaining[(close + 1)..];
        }
        if (remaining.Length > 0) tokens.AddRange(remaining.Split('+'));
        for (var i = 0; i < tokens.Count; i++) tokens[i] = tokens[i].Trim();
        if (tokens.Count == 0 || tokens[^1].Length == 0) return null;

        var modifiers = HotkeyModifiers.None;
        foreach (var token in tokens.Take(tokens.Count - 1))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control" or "primary": modifiers |= HotkeyModifiers.Control; break;
                case "alt" or "mod1" or "option": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "super" or "meta" or "win" or "cmd" or "command" or "hyper": modifiers |= HotkeyModifiers.Super; break;
                default: return null;
            }
        }
        var key = NormalizeKey(tokens[^1]);
        return key is null ? null : new Hotkey(modifiers, key);
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["ins"] = "Insert", ["insert"] = "Insert",
        ["del"] = "Delete", ["delete"] = "Delete",
        ["home"] = "Home", ["end"] = "End",
        ["pgup"] = "PageUp", ["pageup"] = "PageUp", ["page_up"] = "PageUp", ["prior"] = "PageUp",
        ["pgdn"] = "PageDown", ["pagedown"] = "PageDown", ["page_down"] = "PageDown", ["next"] = "PageDown",
        ["esc"] = "Escape", ["escape"] = "Escape",
        ["enter"] = "Return", ["return"] = "Return",
        ["tab"] = "Tab", ["space"] = "Space",
        ["backspace"] = "BackSpace",
        ["left"] = "Left", ["right"] = "Right", ["up"] = "Up", ["down"] = "Down",
        ["pause"] = "Pause", ["break"] = "Pause",
        ["print"] = "Print", ["sysrq"] = "Print",
    };

    private static string? NormalizeKey(string token)
    {
        var lower = token.ToLowerInvariant();
        if (Aliases.TryGetValue(lower, out var alias)) return alias;
        if (lower.Length == 1 && char.IsLetterOrDigit(lower[0])) return lower.ToUpperInvariant();
        if (lower.Length > 1 && lower[0] == 'f'
            && int.TryParse(lower.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 24)
        {
            return string.Create(CultureInfo.InvariantCulture, $"F{number}");
        }
        return null;
    }
}

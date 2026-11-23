using SpiceKit.VVFile;

namespace SpiceKit.Input;

/// <summary>Azioni legate alle scorciatoie del file <c>.vv</c> (con i valori predefiniti di virt-viewer).</summary>
public enum HotkeyAction
{
    ToggleFullscreen,
    ReleaseCursor,
    SecureAttention,
}

/// <summary>Esito di un tasto premuto o rilasciato sull'host.</summary>
public abstract record KeyOutcome
{
    /// <summary>Da inviare all'ospite (scancode set 1, esteso = <c>0x100 | codice</c>).</summary>
    public sealed record Send(ushort Scancode, bool Down) : KeyOutcome;

    /// <summary>Scorciatoia dell'app: il tasto non va all'ospite.</summary>
    public sealed record Hotkey(HotkeyAction Action) : KeyOutcome;

    /// <summary>Tasto senza equivalente (o rilascio di un tasto mai inviato): si scarta.</summary>
    public sealed record Ignore : KeyOutcome;
}

/// <summary>
/// Traduce i tasti di Windows (codice virtuale, scancode e flag dell'hook a basso livello) in
/// scancode PC XT per SPICE, riconosce le scorciatoie e ricorda i tasti premuti nell'ospite.
/// </summary>
/// <remarks>
/// <para>Windows fornisce già lo scancode set 1: basta aggiungere <c>0x100</c> ai tasti estesi.
/// Eccezioni note: Bloc Num arriva come 0x45 esteso (è 0x45 normale), Maiusc destro a volte come
/// esteso (è 0x36), AltGr è preceduto da un Ctrl sinistro fittizio (scancode con il bit 0x200) che
/// l'ospite non deve ricevere, e Pausa non ha uno scancode semplice (sequenza E1): si scarta.</para>
/// <para>Non è thread-safe: va usato dal solo thread dell'interfaccia.</para>
/// </remarks>
public sealed class KeyboardState
{
    // Codici virtuali di Windows (WinUser.h).
    private const uint VkPause = 0x13;
    private const uint VkNumLock = 0x90;
    private const uint VkLShift = 0xA0;
    private const uint VkRShift = 0xA1;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;
    private const uint VkLMenu = 0xA4;
    private const uint VkRMenu = 0xA5;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;

    /// <summary>Scancode di Ctrl sinistro, Alt sinistro e Canc (esteso) per Ctrl+Alt+Canc.</summary>
    public static readonly IReadOnlyList<ushort> SecureAttentionSequence = [0x1D, 0x38, 0x153];

    private readonly List<(HotkeyModifiers Modifiers, uint VirtualKey, HotkeyAction Action)> _bindings = [];
    private readonly Func<uint, uint>? _scancodeOf;
    private readonly HashSet<ushort> _pressed = [];
    private readonly HashSet<uint> _heldModifiers = [];
    private readonly HashSet<uint> _swallowed = [];

    /// <param name="profile">Scorciatoie del <c>.vv</c>; quelle mancanti prendono i valori predefiniti.</param>
    /// <param name="scancodeOf">Scancode di un codice virtuale quando l'evento non lo porta
    /// (<c>MapVirtualKey(vk, MAPVK_VK_TO_VSC_EX)</c>: <c>0xE0xx</c> per i tasti estesi).</param>
    public KeyboardState(ConnectionProfile? profile, Func<uint, uint>? scancodeOf = null)
    {
        _scancodeOf = scancodeOf;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var hotkey = HotkeyOf(profile, action);
            if (VirtualKeyOf(hotkey.Key) is { } vk) _bindings.Add((hotkey.Modifiers, vk, action));
        }
    }

    /// <summary>Scancode premuti nell'ospite (da rilasciare quando la finestra perde il fuoco).</summary>
    public IReadOnlyCollection<ushort> Pressed => _pressed;

    /// <summary>Scorciatoia in uso per un'azione: quella del <c>.vv</c> o la predefinita.</summary>
    public static Hotkey HotkeyOf(ConnectionProfile? profile, HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleFullscreen => profile?.ToggleFullscreenHotkey ?? new Hotkey(HotkeyModifiers.Shift, "F11"),
        HotkeyAction.ReleaseCursor => profile?.ReleaseCursorHotkey ?? new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "R"),
        _ => profile?.SecureAttentionHotkey ?? new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "End"),
    };

    /// <summary>Un evento dell'hook: codice virtuale, scancode, tasto esteso, premuto o rilasciato.</summary>
    public KeyOutcome Process(uint virtualKey, uint scanCode, bool extended, bool down)
    {
        if (virtualKey == VkLControl && (scanCode & 0x200) != 0) return new KeyOutcome.Ignore(); // Ctrl fittizio di AltGr
        TrackModifier(virtualKey, down);

        // Il tasto di una scorciatoia non arriva all'ospite, neanche al rilascio.
        if (down && !IsModifier(virtualKey) && Match(virtualKey) is { } action)
        {
            _swallowed.Add(virtualKey);
            return new KeyOutcome.Hotkey(action);
        }
        if (!down && _swallowed.Remove(virtualKey)) return new KeyOutcome.Ignore();

        if (Scancode(virtualKey, scanCode, extended) is not { } code) return new KeyOutcome.Ignore();
        if (down)
        {
            _pressed.Add(code); // le ripetizioni automatiche arrivano come nuove pressioni
            return new KeyOutcome.Send(code, true);
        }
        return _pressed.Remove(code) ? new KeyOutcome.Send(code, false) : new KeyOutcome.Ignore();
    }

    /// <summary>Rilascia tutto: restituisce i tasti da sollevare nell'ospite (perdita del fuoco, rilascio del cursore).</summary>
    public IReadOnlyList<ushort> ReleaseAll()
    {
        ushort[] codes = [.. _pressed];
        _pressed.Clear();
        _heldModifiers.Clear();
        _swallowed.Clear();
        return codes;
    }

    /// <summary>Scancode set 1 di un tasto, o <see langword="null"/> se non ne ha uno semplice.</summary>
    public ushort? Scancode(uint virtualKey, uint scanCode, bool extended)
    {
        switch (virtualKey)
        {
            case VkPause: return null;
            case VkNumLock: return 0x45;
            case VkLShift: return 0x2A;
            case VkRShift: return 0x36;
        }
        scanCode &= 0xFF;
        if (scanCode == 0 && _scancodeOf is not null)
        {
            // Eventi sintetici senza scancode (tastiere su schermo, SendInput con solo il VK).
            var mapped = _scancodeOf(virtualKey);
            extended = (mapped & 0xFF00) is 0xE000 or 0xE100;
            scanCode = mapped & 0xFF;
        }
        if (scanCode is 0 or > 0x7F) return null;
        return (ushort)((extended ? 0x100u : 0u) | scanCode);
    }

    /// <summary>Codice virtuale di Windows per il nome di tasto normalizzato di <see cref="Hotkey"/>.</summary>
    public static uint? VirtualKeyOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return char.ToUpperInvariant(key[0]);
        if (key.Length > 1 && key[0] == 'F' && int.TryParse(key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n is >= 1 and <= 24)
        {
            return (uint)(0x70 + n - 1);
        }
        return key switch
        {
            "Insert" => 0x2D, "Delete" => 0x2E, "Home" => 0x24, "End" => 0x23,
            "PageUp" => 0x21, "PageDown" => 0x22, "Escape" => 0x1B, "Return" => 0x0D,
            "Tab" => 0x09, "Space" => 0x20, "BackSpace" => 0x08,
            "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28,
            "Pause" => VkPause, "Print" => 0x2C,
            _ => null,
        };
    }

    private HotkeyAction? Match(uint virtualKey)
    {
        var held = HeldModifiers();
        foreach (var (modifiers, vk, action) in _bindings)
        {
            if (vk == virtualKey && modifiers == held) return action;
        }
        return null;
    }

    private HotkeyModifiers HeldModifiers()
    {
        var held = HotkeyModifiers.None;
        foreach (var vk in _heldModifiers)
        {
            held |= vk switch
            {
                VkLControl or VkRControl => HotkeyModifiers.Control,
                VkLMenu or VkRMenu => HotkeyModifiers.Alt,
                VkLShift or VkRShift => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.Super,
            };
        }
        return held;
    }

    private void TrackModifier(uint virtualKey, bool down)
    {
        if (!IsModifier(virtualKey)) return;
        if (down) _heldModifiers.Add(virtualKey);
        else _heldModifiers.Remove(virtualKey);
    }

    private static bool IsModifier(uint vk) => vk is VkLShift or VkRShift or VkLControl or VkRControl or VkLMenu or VkRMenu or VkLWin or VkRWin;
}

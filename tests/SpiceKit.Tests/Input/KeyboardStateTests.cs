using SpiceKit.Input;
using SpiceKit.VVFile;

namespace SpiceKit.Tests.Input;

public class KeyboardStateTests
{
    // Codici virtuali e scancode come li riporta l'hook WH_KEYBOARD_LL.
    private const uint VkA = 0x41, VkR = 0x52, VkEnd = 0x23, VkF11 = 0x7A, VkUp = 0x26;
    private const uint VkLControl = 0xA2, VkLMenu = 0xA4, VkRMenu = 0xA5, VkLShift = 0xA0, VkRShift = 0xA1, VkNumLock = 0x90;

    [Fact]
    public void Plain_and_extended_keys_become_set1_scancodes()
    {
        var keyboard = new KeyboardState(null);
        Assert.Equal(new KeyOutcome.Send(0x1E, true), keyboard.Process(VkA, 0x1E, false, true));
        Assert.Equal(new KeyOutcome.Send(0x1E, false), keyboard.Process(VkA, 0x1E, false, false));
        Assert.Equal(new KeyOutcome.Send(0x148, true), keyboard.Process(VkUp, 0x48, true, true));
        // Rilascio di un tasto mai premuto nell'ospite: niente.
        Assert.IsType<KeyOutcome.Ignore>(keyboard.Process(VkA, 0x1E, false, false));
    }

    [Fact]
    public void Windows_quirks_are_corrected()
    {
        var keyboard = new KeyboardState(null);
        Assert.Equal((ushort)0x45, keyboard.Scancode(VkNumLock, 0x45, extended: true));
        Assert.Equal((ushort)0x36, keyboard.Scancode(VkRShift, 0x36, extended: true));
        Assert.Null(keyboard.Scancode(0x13, 0x45, extended: false)); // Pausa
        // AltGr: il Ctrl sinistro fittizio (0x21D) non va all'ospite, Alt destro sì.
        Assert.IsType<KeyOutcome.Ignore>(keyboard.Process(VkLControl, 0x21D, false, true));
        Assert.Equal(new KeyOutcome.Send(0x138, true), keyboard.Process(VkRMenu, 0x38, true, true));
    }

    [Fact]
    public void Synthetic_keys_without_scancode_use_the_mapping()
    {
        var keyboard = new KeyboardState(null, vk => vk == 0x2E ? 0xE053u : 0);
        Assert.Equal((ushort)0x153, keyboard.Scancode(0x2E, 0, extended: false));
        Assert.Null(keyboard.Scancode(0xFF, 0, extended: false));
    }

    [Fact]
    public void Default_hotkeys_are_swallowed_including_their_release()
    {
        var keyboard = new KeyboardState(null);
        Assert.Equal(new KeyOutcome.Send(0x1D, true), keyboard.Process(VkLControl, 0x1D, false, true));
        Assert.Equal(new KeyOutcome.Send(0x38, true), keyboard.Process(VkLMenu, 0x38, false, true));
        Assert.Equal(new KeyOutcome.Hotkey(HotkeyAction.ReleaseCursor), keyboard.Process(VkR, 0x13, false, true));
        Assert.IsType<KeyOutcome.Ignore>(keyboard.Process(VkR, 0x13, false, false));
        Assert.Equal(new KeyOutcome.Hotkey(HotkeyAction.SecureAttention), keyboard.Process(VkEnd, 0x4F, true, true));
        // I modificatori premuti si rilasciano tutti quando si lascia la tastiera.
        Assert.Equal([(ushort)0x1D, (ushort)0x38], keyboard.ReleaseAll().Order());
        Assert.Empty(keyboard.Pressed);
    }

    [Fact]
    public void Modifiers_must_match_exactly()
    {
        var keyboard = new KeyboardState(null);
        keyboard.Process(VkLControl, 0x1D, false, true);
        keyboard.Process(VkLShift, 0x2A, false, true);
        // Ctrl+Maiusc+F11 non è Maiusc+F11.
        Assert.Equal(new KeyOutcome.Send(0x57, true), keyboard.Process(VkF11, 0x57, false, true));
        keyboard.Process(VkLControl, 0x1D, false, false);
        Assert.Equal(new KeyOutcome.Hotkey(HotkeyAction.ToggleFullscreen), keyboard.Process(VkF11, 0x57, false, true));
    }

    [Fact]
    public void Hotkeys_from_the_vv_file_replace_the_defaults()
    {
        var profile = new ConnectionProfile("host") { ReleaseCursorHotkey = Hotkey.Parse("Ctrl+Alt+F12") };
        var keyboard = new KeyboardState(profile);
        keyboard.Process(VkLControl, 0x1D, false, true);
        keyboard.Process(VkLMenu, 0x38, false, true);
        Assert.Equal(new KeyOutcome.Send(0x13, true), keyboard.Process(VkR, 0x13, false, true));
        Assert.Equal(new KeyOutcome.Hotkey(HotkeyAction.ReleaseCursor), keyboard.Process(0x7B, 0x58, false, true));
    }

    [Theory]
    [InlineData("R", 0x52u)]
    [InlineData("7", 0x37u)]
    [InlineData("F1", 0x70u)]
    [InlineData("F24", 0x87u)]
    [InlineData("Insert", 0x2Du)]
    [InlineData("End", 0x23u)]
    public void Hotkey_names_map_to_virtual_keys(string name, uint expected) =>
        Assert.Equal(expected, KeyboardState.VirtualKeyOf(name));
}

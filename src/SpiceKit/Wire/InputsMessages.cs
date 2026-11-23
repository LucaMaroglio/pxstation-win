// Canale inputs (spice.proto, channel InputsChannel).

namespace SpiceKit.Wire;

/// <summary><c>SPICE_MSG_INPUTS_INIT</c> e <c>SPICE_MSG_INPUTS_KEY_MODIFIERS</c>: blocchi attivi nell'ospite.</summary>
public static class InputsDecoders
{
    public static KeyboardLocks Locks(ReadOnlySpan<byte> bytes) => (KeyboardLocks)new ByteReader(bytes).ReadU16();
}

/// <summary>Messaggi client → server del canale inputs.</summary>
public static class InputsClientEncoder
{
    /// <summary>
    /// Codifica uno scancode PC XT (set 1) come <c>spice_make_scancode</c> di spice-gtk.
    /// </summary>
    /// <remarks>
    /// I tasti estesi (prefisso <c>0xE0</c>) si passano come <c>0x100 | codice</c>. Sul filo è un
    /// uint32 che contiene la sequenza di byte in little-endian: <c>codice</c> per i tasti normali,
    /// <c>0xE0, codice</c> per quelli estesi. Il rilascio imposta il bit <c>0x80</c> del codice.
    /// Fonte del comportamento: spice-gtk 0.42 src/spice-util.c.
    /// </remarks>
    public static uint WireScancode(ushort scancode, bool release)
    {
        var code = scancode & 0x17Fu;
        if (release) code |= 0x80;
        if (code < 0x100) return code;
        // Byte 0 = 0xE0, byte 1 = codice.
        return 0xE0u | ((code & 0xFF) << 8);
    }

    public static SpiceMessage KeyDown(ushort scancode) => U32(InputsClientMessage.KeyDown, WireScancode(scancode, release: false));

    public static SpiceMessage KeyUp(ushort scancode) => U32(InputsClientMessage.KeyUp, WireScancode(scancode, release: true));

    public static SpiceMessage KeyModifiers(KeyboardLocks locks)
    {
        var writer = new ByteWriter(2);
        writer.WriteU16((ushort)locks);
        return new SpiceMessage((ushort)InputsClientMessage.KeyModifiers, writer.ToArray());
    }

    /// <summary>Modalità server: movimento relativo.</summary>
    public static SpiceMessage MouseMotion(int dx, int dy, MouseButtons buttons)
    {
        var writer = new ByteWriter(10);
        writer.WriteI32(dx);
        writer.WriteI32(dy);
        writer.WriteU16((ushort)buttons);
        return new SpiceMessage((ushort)InputsClientMessage.MouseMotion, writer.ToArray());
    }

    /// <summary>Modalità client: posizione assoluta in pixel dell'ospite.</summary>
    public static SpiceMessage MousePosition(uint x, uint y, MouseButtons buttons, byte displayId)
    {
        var writer = new ByteWriter(11);
        writer.WriteU32(x);
        writer.WriteU32(y);
        writer.WriteU16((ushort)buttons);
        writer.WriteU8(displayId);
        return new SpiceMessage((ushort)InputsClientMessage.MousePosition, writer.ToArray());
    }

    public static SpiceMessage MousePress(MouseButton button, MouseButtons buttons) => Button(InputsClientMessage.MousePress, button, buttons);

    public static SpiceMessage MouseRelease(MouseButton button, MouseButtons buttons) => Button(InputsClientMessage.MouseRelease, button, buttons);

    private static SpiceMessage Button(InputsClientMessage type, MouseButton button, MouseButtons buttons)
    {
        var writer = new ByteWriter(3);
        writer.WriteU8((byte)button);
        writer.WriteU16((ushort)buttons);
        return new SpiceMessage((ushort)type, writer.ToArray());
    }

    private static SpiceMessage U32(InputsClientMessage type, uint value)
    {
        var writer = new ByteWriter(4);
        writer.WriteU32(value);
        return new SpiceMessage((ushort)type, writer.ToArray());
    }
}

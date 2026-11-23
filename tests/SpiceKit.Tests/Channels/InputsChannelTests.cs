using System.Buffers.Binary;
using SpiceKit.Channels;
using SpiceKit.Tests.Support;
using SpiceKit.Transport;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Channels;

public class InputsChannelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData((ushort)0x1E, false, 0x1Eu)] // A premuto
    [InlineData((ushort)0x1E, true, 0x9Eu)] // A rilasciato
    [InlineData((ushort)0x148, false, 0x48E0u)] // freccia su: E0 48
    [InlineData((ushort)0x148, true, 0xC8E0u)] // freccia su rilasciata: E0 C8
    [InlineData((ushort)0x15B, false, 0x5BE0u)] // Windows sinistro
    [InlineData((ushort)0x153, true, 0xD3E0u)] // Canc (esteso) rilasciato
    public void Scancodes_are_encoded_like_spice_gtk(ushort code, bool release, uint expected) =>
        Assert.Equal(expected, InputsClientEncoder.WireScancode(code, release));

    [Fact]
    public void Mouse_messages_have_the_wire_layout()
    {
        var position = InputsClientEncoder.MousePosition(640, 480, MouseButtons.Left | MouseButtons.Right, 0);
        Assert.Equal((ushort)InputsClientMessage.MousePosition, position.Type);
        Assert.Equal([0x80, 2, 0, 0, 0xE0, 1, 0, 0, 5, 0, 0], position.Payload);

        var motion = InputsClientEncoder.MouseMotion(-3, 7, MouseButtons.None);
        Assert.Equal([0xFD, 0xFF, 0xFF, 0xFF, 7, 0, 0, 0, 0, 0], motion.Payload);

        var press = InputsClientEncoder.MousePress(MouseButton.Up, MouseButtons.Up);
        Assert.Equal((ushort)InputsClientMessage.MousePress, press.Type);
        Assert.Equal([(byte)MouseButton.Up, 8, 0], press.Payload);
    }

    private static InputsChannel NewInputs()
    {
        var (client, _) = MemoryTransport.Pair();
        return new InputsChannel(new ChannelConnection(ChannelType.Inputs, 0, client, new CapabilitySet()));
    }

    private static List<SpiceMessage> Drain(InputsChannel inputs)
    {
        var messages = new List<SpiceMessage>();
        while (inputs.TakeQueued() is { } message) messages.Add(message);
        return messages;
    }

    private static SpiceMessage Ack() => new((ushort)InputsServerMessage.MouseMotionAck, []);

    [Fact]
    public void Motion_is_throttled_until_the_server_acknowledges()
    {
        var inputs = NewInputs();
        // Fino a 2 × ACK_BUNCH movimenti partono subito, poi si accumulano.
        for (var i = 0; i < 20; i++) inputs.Post(new InputEvent.Motion(1, 2, MouseButtons.None));
        var sent = Drain(inputs);
        Assert.Equal(2 * SpiceConstants.InputMotionAckBunch, sent.Count);
        Assert.Equal(2 * SpiceConstants.InputMotionAckBunch, inputs.UnacknowledgedMotions);

        // La conferma libera un gruppo: parte un solo messaggio con la somma degli altri 12.
        inputs.Handle(Ack());
        var flushed = Assert.Single(Drain(inputs));
        Assert.Equal((ushort)InputsClientMessage.MouseMotion, flushed.Type);
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(flushed.Payload));
        Assert.Equal(24, BinaryPrimitives.ReadInt32LittleEndian(flushed.Payload.AsSpan(4)));
    }

    [Fact]
    public void Positions_keep_only_the_latest_and_clicks_flush_them_first()
    {
        var inputs = NewInputs();
        for (uint i = 0; i < 2 * SpiceConstants.InputMotionAckBunch; i++) inputs.Post(new InputEvent.Position(i, i, MouseButtons.None, 0));
        Drain(inputs);
        inputs.Post(new InputEvent.Position(100, 100, MouseButtons.None, 0));
        inputs.Post(new InputEvent.Position(200, 150, MouseButtons.None, 0));
        Assert.Empty(Drain(inputs));

        // Il clic non aspetta la conferma e porta con sé l'ultima posizione.
        inputs.Post(new InputEvent.Press(MouseButton.Left, MouseButtons.Left));
        var sent = Drain(inputs);
        Assert.Equal([(ushort)InputsClientMessage.MousePosition, (ushort)InputsClientMessage.MousePress], sent.Select(m => m.Type));
        Assert.Equal(200u, BinaryPrimitives.ReadUInt32LittleEndian(sent[0].Payload));
    }

    [Fact]
    public void Lock_state_is_sent_only_when_it_differs_from_the_guest()
    {
        var inputs = NewInputs();
        inputs.Handle(new SpiceMessage((ushort)InputsServerMessage.Init, [(byte)KeyboardLocks.NumLock, 0]));
        Assert.Equal(KeyboardLocks.NumLock, inputs.GuestLocks);
        inputs.Post(new InputEvent.Locks(KeyboardLocks.NumLock));
        Assert.Empty(Drain(inputs));
        inputs.Post(new InputEvent.Locks(KeyboardLocks.NumLock | KeyboardLocks.CapsLock));
        var sent = Assert.Single(Drain(inputs));
        Assert.Equal((ushort)InputsClientMessage.KeyModifiers, sent.Type);
        Assert.Equal([6, 0], sent.Payload);
    }

    [Fact]
    public void Truncated_server_messages_close_the_channel()
    {
        var inputs = NewInputs();
        var error = Assert.Throws<ChannelException>(() => inputs.Handle(new SpiceMessage((ushort)InputsServerMessage.KeyModifiers, [1])));
        Assert.Equal(ChannelErrorKind.Malformed, error.Kind);
    }

    [Fact]
    public async Task Run_sends_queued_input_in_order()
    {
        var (client, server) = MemoryTransport.Pair();
        var inputs = new InputsChannel(new ChannelConnection(ChannelType.Inputs, 0, client, new CapabilitySet()));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = inputs.RunAsync(stop.Token);

        inputs.Post(new InputEvent.KeyDown(0x1D));
        inputs.Post(new InputEvent.KeyDown(0x138));
        inputs.Post(new InputEvent.KeyDown(0x153));
        inputs.Post(new InputEvent.KeyUp(0x153));

        var codes = new List<(ushort, uint)>();
        for (var i = 0; i < 4; i++)
        {
            var header = MiniHeader.Decode(await server.ReceiveAsync(MiniHeader.WireSize, Ct));
            var payload = await server.ReceiveAsync((int)header.Size, Ct);
            codes.Add((header.Type, BinaryPrimitives.ReadUInt32LittleEndian(payload)));
        }
        Assert.Equal([
            ((ushort)InputsClientMessage.KeyDown, 0x1Du),
            ((ushort)InputsClientMessage.KeyDown, 0x38E0u),
            ((ushort)InputsClientMessage.KeyDown, 0x53E0u),
            ((ushort)InputsClientMessage.KeyUp, 0xD3E0u),
        ], codes);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}

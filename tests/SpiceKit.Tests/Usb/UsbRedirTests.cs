using System.Buffers.Binary;
using System.Threading.Channels;
using SpiceKit.Channels;
using SpiceKit.Tests.Support;
using SpiceKit.Transport;
using SpiceKit.UsbRedirWire;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Usb;

/// <summary>Dispositivo con un endpoint bulk IN che registra le richieste ricevute.</summary>
internal sealed class RecordingUsbDevice : IUsbRedirDevice
{
    public List<int> BulkLengths { get; } = [];

    public bool Closed { get; private set; }

    public UsbDeviceDescription Description { get; init; } = new(UsbSpeed.High, 0xFF, 0, 0, 0x1209, 0x0001, 0x0100,
        [new UsbInterfaceInfo(0, 0xFF, 0, 0)], [new UsbEndpointInfo(0x81, UsbTransferType.Bulk, 0, 0, 512)]);

    public string DisplayName => "Registratore";

    public Task<UsbDeviceDescription> DescribeAsync() => Task.FromResult(Description);

    public Task<UsbTransferResult> ControlAsync(UsbControlSetup setup, byte[] data) => Task.FromResult(UsbTransferResult.Failed(UsbRedirStatus.Stall));

    public Task<UsbTransferResult> BulkAsync(byte endpoint, byte[] data, int length)
    {
        lock (BulkLengths) BulkLengths.Add(length);
        return Task.FromResult(UsbTransferResult.Ok(Enumerable.Repeat((byte)0xAB, Math.Min(length, 4)).ToArray()));
    }

    public Task<UsbTransferResult> InterruptAsync(byte endpoint, byte[] data, int length) => Task.FromResult(UsbTransferResult.Failed(UsbRedirStatus.Stall));

    public Task<UsbRedirStatus> SetConfigurationAsync(byte value) => Task.FromResult(UsbRedirStatus.Success);

    public Task<byte> GetConfigurationAsync() => Task.FromResult((byte)1);

    public Task<UsbRedirStatus> SetAltSettingAsync(byte interfaceNumber, byte alt) => Task.FromResult(UsbRedirStatus.Success);

    public Task<byte?> GetAltSettingAsync(byte interfaceNumber) => Task.FromResult<byte?>(0);

    public Task<bool> ResetAsync() => Task.FromResult(true);

    public Task AbortAsync(byte endpoint) => Task.CompletedTask;

    public Task CloseAsync()
    {
        Closed = true;
        return Task.CompletedTask;
    }
}

/// <summary>Il lato ospite (usb-redir di QEMU) di uno slot usbredir.</summary>
internal sealed class GuestSide(MemoryTransport stream, UsbRedirCapabilities caps)
{
    private readonly List<byte> _pending = [];
    private bool _sawHostHello;

    public MemoryTransport Stream => stream;

    private static byte[] Frame(UsbRedirPacketType type, ulong id, byte[] body, bool shortId)
    {
        var w = new ByteWriter();
        w.WriteU32((uint)type);
        w.WriteU32((uint)body.Length);
        if (shortId) w.WriteU32((uint)id);
        else w.WriteU64(id);
        w.WriteBytes(body);
        return w.ToArray();
    }

    public Task SendAsync(byte[] usbredir) =>
        stream.SendAsync(new SpiceMessage((ushort)SpiceVmcMessage.Data, usbredir).Encode(), TestContext.Current.CancellationToken);

    public Task SendHelloAsync()
    {
        var body = new byte[64 + (4 * caps.Words.Count)];
        "test guest"u8.CopyTo(body);
        for (var i = 0; i < caps.Words.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(64 + (4 * i)), caps.Words[i]);
        return SendAsync(Frame(UsbRedirPacketType.Hello, 0, body, shortId: true));
    }

    /// <summary>Pacchetto bulk con lunghezza a 32 bit (serve <c>bulk_length_32</c>).</summary>
    public Task SendBulkAsync(ulong id, byte endpoint, uint length)
    {
        var w = new ByteWriter();
        w.WriteU8(endpoint);
        w.WriteU8(0);
        w.WriteU16((ushort)length);
        w.WriteU32(0);
        w.WriteU16((ushort)(length >> 16));
        return SendAsync(Frame(UsbRedirPacketType.BulkPacket, id, w.ToArray(), shortId: false));
    }

    public Task SendAsync(UsbRedirPacketType type, ulong id, byte[] body) => SendAsync(Frame(type, id, body, shortId: false));

    /// <summary>Prossimo pacchetto del client: (tipo, id, corpo).</summary>
    public async Task<(uint Type, ulong Id, byte[] Body)> NextPacketAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        while (true)
        {
            var headerSize = _sawHostHello ? 16 : 12;
            if (_pending.Count >= headerSize)
            {
                var bytes = _pending.ToArray();
                var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
                var id = _sawHostHello ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
                if (bytes.Length >= headerSize + length)
                {
                    _pending.RemoveRange(0, headerSize + length);
                    if (type == (uint)UsbRedirPacketType.Hello) _sawHostHello = true;
                    return (type, id, bytes[headerSize..(headerSize + length)]);
                }
            }
            var header = MiniHeader.Decode(await stream.ReceiveAsync(MiniHeader.WireSize, ct));
            _pending.AddRange(await stream.ReceiveAsync((int)header.Size, ct));
        }
    }
}

public class UsbRedirTests
{
    private static async Task<(UsbRedirChannel Channel, GuestSide Guest, RecordingUsbDevice Device, Task Loop, CancellationTokenSource Stop)> MakeSlotAsync(
        UsbRedirCapabilities caps)
    {
        var (client, server) = MemoryTransport.Pair();
        var channel = new UsbRedirChannel(0, new ChannelConnection(ChannelType.Usbredir, 0, client, new CapabilitySet()),
            Channel.CreateUnbounded<UsbSlotEvent>().Writer);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var loop = channel.RunAsync(stop.Token);
        var guest = new GuestSide(server, caps);
        Assert.Equal((uint)UsbRedirPacketType.Hello, (await guest.NextPacketAsync()).Type);
        await guest.SendHelloAsync();
        var device = new RecordingUsbDevice();
        await channel.AttachAsync(device);
        // ep_info, interface_info, device_connect
        Assert.Equal((uint)UsbRedirPacketType.EpInfo, (await guest.NextPacketAsync()).Type);
        Assert.Equal((uint)UsbRedirPacketType.InterfaceInfo, (await guest.NextPacketAsync()).Type);
        var connect = await guest.NextPacketAsync();
        Assert.Equal((uint)UsbRedirPacketType.DeviceConnect, connect.Type);
        return (channel, guest, device, loop, stop);
    }

    [Fact]
    public void Codec_negotiates_ids_and_bulk_length()
    {
        var codec = new UsbRedirCodec();
        var hello = codec.EncodeHello();
        Assert.Equal(12 + 64 + 4, hello.Length); // id a 32 bit prima dello scambio
        Assert.False(codec.Negotiated(UsbRedirCapability.Ids64));

        var guestHello = new byte[12 + 68];
        BinaryPrimitives.WriteUInt32LittleEndian(guestHello.AsSpan(4), 68);
        BinaryPrimitives.WriteUInt32LittleEndian(guestHello.AsSpan(12 + 64),
            UsbRedirCapabilities.Of(UsbRedirCapability.Ids64, UsbRedirCapability.BulkLength32, UsbRedirCapability.BulkStreams).Words[0]);
        var packets = codec.Feed(guestHello.AsSpan(0, 30)); // a pezzi
        Assert.Empty(packets);
        Assert.IsType<UsbRedirIncoming.Hello>(Assert.Single(codec.Feed(guestHello.AsSpan(30))));
        Assert.True(codec.Negotiated(UsbRedirCapability.Ids64));
        Assert.True(codec.Negotiated(UsbRedirCapability.BulkLength32));
        Assert.False(codec.Negotiated(UsbRedirCapability.BulkStreams)); // non lo annunciamo

        var reply = codec.EncodeBulkReply(0x1_0000_0001, 0x81, 0, UsbRedirStatus.Success, 70_000, new byte[70_000]);
        Assert.Equal(0x1_0000_0001ul, BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(8)));
        Assert.Equal((ushort)(70_000 & 0xFFFF), BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(16 + 2)));
        Assert.Equal((ushort)(70_000 >> 16), BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(16 + 8)));
        Assert.Equal(16 + 10 + 70_000, reply.Length);
    }

    [Fact]
    public void Hostile_packets_are_rejected()
    {
        var codec = new UsbRedirCodec();
        var huge = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(0), (uint)UsbRedirPacketType.ControlPacket);
        BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(4), UsbRedirCodec.MaxPacketLength + 1);
        Assert.Throws<UsbRedirException>(() => codec.Feed(huge));
        var shortControl = new byte[12 + 3];
        BinaryPrimitives.WriteUInt32LittleEndian(shortControl.AsSpan(0), (uint)UsbRedirPacketType.ControlPacket);
        BinaryPrimitives.WriteUInt32LittleEndian(shortControl.AsSpan(4), 3);
        Assert.Throws<UsbRedirException>(() => new UsbRedirCodec().Feed(shortControl));
    }

    [Fact]
    public async Task Oversized_bulk_in_is_refused_before_reaching_the_device()
    {
        var (channel, guest, device, loop, stop) = await MakeSlotAsync(UsbRedirCapabilities.Of(UsbRedirCapability.Ids64, UsbRedirCapability.BulkLength32));
        // Un pacchetto minuscolo non deve far allocare ~4 GiB al client.
        await guest.SendBulkAsync(7, 0x81, 0xFFFF_FFFF);
        var reply = await guest.NextPacketAsync();
        Assert.Equal(((uint)UsbRedirPacketType.BulkPacket, 7ul), (reply.Type, reply.Id));
        Assert.Equal((byte)UsbRedirStatus.Inval, reply.Body[1]);
        // Endpoint che il dispositivo non ha: rifiutato.
        await guest.SendBulkAsync(8, 0x82, 64);
        Assert.Equal((byte)UsbRedirStatus.Inval, (await guest.NextPacketAsync()).Body[1]);
        // Una richiesta normale funziona.
        await guest.SendBulkAsync(9, 0x81, 512);
        var ok = await guest.NextPacketAsync();
        Assert.Equal(9ul, ok.Id);
        Assert.Equal((byte)UsbRedirStatus.Success, ok.Body[1]);
        Assert.Equal([512], device.BulkLengths);
        Assert.Equal(0, channel.OutstandingBytes);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        Assert.True(device.Closed);
    }

    [Fact]
    public async Task Channel_loss_releases_the_device()
    {
        var (channel, guest, device, loop, stop) = await MakeSlotAsync(UsbRedirCapabilities.Of(UsbRedirCapability.Ids64));
        await guest.Stream.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => loop);
        Assert.True(device.Closed);
        Assert.False(channel.IsFree); // uno slot chiuso non accetta più dispositivi
        var error = await Assert.ThrowsAsync<UsbAttachException>(() => channel.AttachAsync(new RecordingUsbDevice()));
        Assert.Equal(UsbAttachErrorKind.SlotBusy, error.Kind);
        stop.Dispose();
    }

    [Fact]
    public async Task Detach_finishes_when_the_ack_is_withheld()
    {
        var (channel, _, device, loop, stop) = await MakeSlotAsync(UsbRedirCapabilities.Of(UsbRedirCapability.Ids64, UsbRedirCapability.DeviceDisconnectAck));
        var started = DateTime.UtcNow;
        await channel.DetachAsync();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.True(channel.IsFree);
        Assert.True(device.Closed);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task Keyboards_need_confirmation_and_the_guest_filter_is_honoured()
    {
        var (client, server) = MemoryTransport.Pair();
        var channel = new UsbRedirChannel(0, new ChannelConnection(ChannelType.Usbredir, 0, client, new CapabilitySet()),
            Channel.CreateUnbounded<UsbSlotEvent>().Writer);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var loop = channel.RunAsync(stop.Token);
        var guest = new GuestSide(server, UsbRedirCapabilities.Of(UsbRedirCapability.Ids64, UsbRedirCapability.Filter));
        await guest.NextPacketAsync();
        await guest.SendHelloAsync();
        var keyboard = new RecordingUsbDevice
        {
            Description = new RecordingUsbDevice().Description with { Interfaces = [new UsbInterfaceInfo(0, 3, 1, 1)] },
        };
        var error = await Assert.ThrowsAsync<UsbAttachException>(() => channel.AttachAsync(keyboard));
        Assert.Equal(UsbAttachErrorKind.KeyboardOrMouse, error.Kind);
        // L'ospite accetta solo la classe 8: un dispositivo 0xFF viene rifiutato.
        await guest.SendAsync(UsbRedirPacketType.FilterFilter, 0, "0x08,-1,-1,-1,1\0"u8.ToArray());
        await Task.Delay(100, TestContext.Current.CancellationToken);
        error = await Assert.ThrowsAsync<UsbAttachException>(() => channel.AttachAsync(new RecordingUsbDevice()));
        Assert.Equal(UsbAttachErrorKind.RejectedByGuestFilter, error.Kind);
        Assert.True(channel.IsFree);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public void Lz4_spicevmc_data_is_decompressed_with_a_size_limit()
    {
        var huge = new byte[5];
        huge[0] = (byte)DataCompressionType.Lz4;
        BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(1), UsbRedirChannel.MaxUncompressedSize + 1u);
        Assert.Throws<WireException>(() => UsbRedirChannel.Decompress(huge));
        Assert.Equal([1, 2, 3], UsbRedirChannel.Decompress([(byte)DataCompressionType.None, 1, 2, 3]));
    }
}

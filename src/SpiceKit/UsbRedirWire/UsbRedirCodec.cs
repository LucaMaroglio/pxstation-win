using System.Buffers.Binary;
using System.Text;

namespace SpiceKit.UsbRedirWire;

/// <summary>Il flusso usbredir non è valido.</summary>
public sealed class UsbRedirException(string message) : Exception(message);

/// <summary>Intestazione dei pacchetti dati bulk, interrupt e iso.</summary>
public readonly record struct UsbDataHeader(byte Endpoint, UsbRedirStatus Status, uint Length, uint StreamId = 0);

/// <summary>Pacchetto ricevuto dallo usb-guest.</summary>
public abstract record UsbRedirIncoming
{
    public sealed record Hello(string Version, UsbRedirCapabilities Capabilities) : UsbRedirIncoming;

    public sealed record Reset : UsbRedirIncoming;

    public sealed record SetConfiguration(ulong Id, byte Configuration) : UsbRedirIncoming;

    public sealed record GetConfiguration(ulong Id) : UsbRedirIncoming;

    public sealed record SetAltSetting(ulong Id, byte Interface, byte Alt) : UsbRedirIncoming;

    public sealed record GetAltSetting(ulong Id, byte Interface) : UsbRedirIncoming;

    public sealed record StartIso(ulong Id, byte Endpoint) : UsbRedirIncoming;

    public sealed record StopIso(ulong Id, byte Endpoint) : UsbRedirIncoming;

    public sealed record StartInterruptReceiving(ulong Id, byte Endpoint) : UsbRedirIncoming;

    public sealed record StopInterruptReceiving(ulong Id, byte Endpoint) : UsbRedirIncoming;

    public sealed record AllocBulkStreams(ulong Id, uint Endpoints, uint Streams) : UsbRedirIncoming;

    public sealed record FreeBulkStreams(ulong Id, uint Endpoints) : UsbRedirIncoming;

    public sealed record CancelDataPacket(ulong Id) : UsbRedirIncoming;

    public sealed record FilterReject : UsbRedirIncoming;

    public sealed record FilterFilter(string Rules) : UsbRedirIncoming;

    public sealed record DeviceDisconnectAck : UsbRedirIncoming;

    public sealed record StartBulkReceiving(ulong Id, byte Endpoint, uint StreamId) : UsbRedirIncoming;

    public sealed record StopBulkReceiving(ulong Id, byte Endpoint, uint StreamId) : UsbRedirIncoming;

    public sealed record Control(ulong Id, byte Endpoint, UsbControlSetup Setup, byte[] Data) : UsbRedirIncoming;

    public sealed record Bulk(ulong Id, UsbDataHeader Header, byte[] Data) : UsbRedirIncoming;

    public sealed record Interrupt(ulong Id, UsbDataHeader Header, byte[] Data) : UsbRedirIncoming;

    public sealed record Iso(ulong Id, UsbDataHeader Header, byte[] Data) : UsbRedirIncoming;

    public sealed record Unknown(uint Type, ulong Id) : UsbRedirIncoming;
}

/// <summary>
/// Analizzatore incrementale e codificatore usbredir dal lato usb-host (porting di
/// <c>USBRedirCodec.swift</c>). La larghezza degli id (32/64 bit) e i campi facoltativi seguono
/// le capacità negoziate. Non thread-safe: lo usa il solo ciclo del canale.
/// </summary>
public sealed class UsbRedirCodec(UsbRedirCapabilities? capabilities = null)
{
    /// <summary>Pacchetti più grandi si rifiutano (limite contro input ostili).</summary>
    public const uint MaxPacketLength = 64 * 1024 * 1024;

    /// <summary>Versione nel nostro hello (testo libero, solo per i log).</summary>
    public const string VersionString = "PxStation Windows";

    private byte[] _buffer = [];
    private int _length;

    public UsbRedirCapabilities OurCapabilities { get; } = capabilities ?? UsbRedirCapabilities.Client;

    public UsbRedirCapabilities? PeerCapabilities { get; private set; }

    /// <summary><see langword="true"/> dopo lo scambio degli hello, se entrambi annunciano <paramref name="cap"/>.</summary>
    public bool Negotiated(UsbRedirCapability cap) => OurCapabilities.Contains(cap) && PeerCapabilities?.Contains(cap) == true;

    private int IdSize => Negotiated(UsbRedirCapability.Ids64) ? 8 : 4;

    // MARK: decodifica

    public List<UsbRedirIncoming> Feed(ReadOnlySpan<byte> bytes)
    {
        if (_length + bytes.Length > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + bytes.Length));
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        var packets = new List<UsbRedirIncoming>();
        var offset = 0;
        while (true)
        {
            // Prima dell'hello dell'ospite gli id sono a 32 bit (l'hello lo è sempre).
            var headerSize = 8 + IdSize;
            var available = _buffer.AsSpan(offset, _length - offset);
            if (available.Length < headerSize) break;
            var type = BinaryPrimitives.ReadUInt32LittleEndian(available);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(available[4..]);
            var id = IdSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(available[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(available[8..]);
            if (length > MaxPacketLength) throw new UsbRedirException($"pacchetto usbredir di {length} byte");
            if (available.Length - headerSize < length) break;
            var body = available.Slice(headerSize, (int)length).ToArray();
            offset += headerSize + (int)length;
            packets.Add(Decode(type, id, body));
        }
        if (offset > 0)
        {
            _buffer.AsSpan(offset, _length - offset).CopyTo(_buffer);
            _length -= offset;
            if (_length == 0 && _buffer.Length > 1 << 20) _buffer = [];
        }
        return packets;
    }

    private UsbRedirIncoming Decode(uint rawType, ulong id, byte[] body)
    {
        void Need(int count)
        {
            if (body.Length < count) throw new UsbRedirException($"pacchetto {rawType} troppo corto ({body.Length} < {count})");
        }
        ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at));

        switch ((UsbRedirPacketType)rawType)
        {
            case UsbRedirPacketType.Hello:
                Need(64);
                var end = Array.IndexOf(body, (byte)0, 0, 64);
                var version = Encoding.UTF8.GetString(body, 0, end < 0 ? 64 : end);
                var words = new List<uint>();
                for (var at = 64; at + 4 <= body.Length && words.Count < 8; at += 4) words.Add(U32(at));
                var caps = new UsbRedirCapabilities(words.Count == 0 ? [0u] : words);
                PeerCapabilities = caps;
                return new UsbRedirIncoming.Hello(version, caps);
            case UsbRedirPacketType.Reset:
                return new UsbRedirIncoming.Reset();
            case UsbRedirPacketType.SetConfiguration:
                Need(1);
                return new UsbRedirIncoming.SetConfiguration(id, body[0]);
            case UsbRedirPacketType.GetConfiguration:
                return new UsbRedirIncoming.GetConfiguration(id);
            case UsbRedirPacketType.SetAltSetting:
                Need(2);
                return new UsbRedirIncoming.SetAltSetting(id, body[0], body[1]);
            case UsbRedirPacketType.GetAltSetting:
                Need(1);
                return new UsbRedirIncoming.GetAltSetting(id, body[0]);
            case UsbRedirPacketType.StartIsoStream:
                Need(3);
                return new UsbRedirIncoming.StartIso(id, body[0]);
            case UsbRedirPacketType.StopIsoStream:
                Need(1);
                return new UsbRedirIncoming.StopIso(id, body[0]);
            case UsbRedirPacketType.StartInterruptReceiving:
                Need(1);
                return new UsbRedirIncoming.StartInterruptReceiving(id, body[0]);
            case UsbRedirPacketType.StopInterruptReceiving:
                Need(1);
                return new UsbRedirIncoming.StopInterruptReceiving(id, body[0]);
            case UsbRedirPacketType.AllocBulkStreams:
                Need(8);
                return new UsbRedirIncoming.AllocBulkStreams(id, U32(0), U32(4));
            case UsbRedirPacketType.FreeBulkStreams:
                Need(4);
                return new UsbRedirIncoming.FreeBulkStreams(id, U32(0));
            case UsbRedirPacketType.CancelDataPacket:
                return new UsbRedirIncoming.CancelDataPacket(id);
            case UsbRedirPacketType.FilterReject:
                return new UsbRedirIncoming.FilterReject();
            case UsbRedirPacketType.FilterFilter:
                var zero = Array.IndexOf(body, (byte)0);
                return new UsbRedirIncoming.FilterFilter(Encoding.UTF8.GetString(body, 0, zero < 0 ? body.Length : zero));
            case UsbRedirPacketType.DeviceDisconnectAck:
                return new UsbRedirIncoming.DeviceDisconnectAck();
            case UsbRedirPacketType.StartBulkReceiving:
                Need(10);
                return new UsbRedirIncoming.StartBulkReceiving(id, body[8], U32(0));
            case UsbRedirPacketType.StopBulkReceiving:
                Need(5);
                return new UsbRedirIncoming.StopBulkReceiving(id, body[4], U32(0));
            case UsbRedirPacketType.ControlPacket:
                Need(10);
                var setup = new UsbControlSetup(body[2], body[1], U16(4), U16(6), U16(8));
                return new UsbRedirIncoming.Control(id, body[0], setup, body[10..]);
            case UsbRedirPacketType.BulkPacket:
                var longLength = Negotiated(UsbRedirCapability.BulkLength32);
                var size = longLength ? 10 : 8;
                Need(size);
                var length = (uint)U16(2) | (longLength ? (uint)U16(8) << 16 : 0);
                return new UsbRedirIncoming.Bulk(id, new UsbDataHeader(body[0], Status(body[1]), length, U32(4)), body[size..]);
            case UsbRedirPacketType.InterruptPacket or UsbRedirPacketType.IsoPacket:
                Need(4);
                var header = new UsbDataHeader(body[0], Status(body[1]), U16(2));
                return rawType == (uint)UsbRedirPacketType.IsoPacket
                    ? new UsbRedirIncoming.Iso(id, header, body[4..])
                    : new UsbRedirIncoming.Interrupt(id, header, body[4..]);
            default:
                // Tipi dallo usb-host allo usb-guest non dovrebbero arrivare qui.
                return new UsbRedirIncoming.Unknown(rawType, id);
        }
    }

    private static UsbRedirStatus Status(byte value) => value <= (byte)UsbRedirStatus.Babble ? (UsbRedirStatus)value : UsbRedirStatus.IoError;

    // MARK: codifica (usb-host → usb-guest)

    private byte[] Packet(UsbRedirPacketType type, ulong id, ReadOnlySpan<byte> body, bool forceShortId = false)
    {
        var longId = IdSize == 8 && !forceShortId;
        var packet = new byte[8 + (longId ? 8 : 4) + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)body.Length);
        if (longId) BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), id);
        else BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), unchecked((uint)id));
        body.CopyTo(packet.AsSpan(longId ? 16 : 12));
        return packet;
    }

    /// <summary>Il nostro hello (id sempre a 32 bit: si manda prima di conoscere quello dell'ospite).</summary>
    public byte[] EncodeHello()
    {
        var body = new byte[64 + (4 * OurCapabilities.Words.Count)];
        Encoding.UTF8.GetBytes(VersionString).AsSpan(0, Math.Min(63, VersionString.Length)).CopyTo(body);
        for (var i = 0; i < OurCapabilities.Words.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(64 + (4 * i)), OurCapabilities.Words[i]);
        return Packet(UsbRedirPacketType.Hello, 0, body, forceShortId: true);
    }

    public byte[] EncodeDeviceConnect(UsbDeviceDescription device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var withVersion = Negotiated(UsbRedirCapability.ConnectDeviceVersion);
        var body = new byte[withVersion ? 10 : 8];
        body[0] = (byte)device.Speed;
        body[1] = device.DeviceClass;
        body[2] = device.DeviceSubclass;
        body[3] = device.DeviceProtocol;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), device.VendorId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), device.ProductId);
        if (withVersion) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), device.DeviceVersionBcd);
        return Packet(UsbRedirPacketType.DeviceConnect, 0, body);
    }

    public byte[] EncodeDeviceDisconnect() => Packet(UsbRedirPacketType.DeviceDisconnect, 0, []);

    public byte[] EncodeInterfaceInfo(IReadOnlyList<UsbInterfaceInfo> interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        var list = interfaces.Take(32).ToArray();
        var body = new byte[4 + (4 * 32)];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)list.Length);
        for (var i = 0; i < list.Length; i++)
        {
            body[4 + i] = list[i].Number;
            body[4 + 32 + i] = list[i].InterfaceClass;
            body[4 + 64 + i] = list[i].InterfaceSubclass;
            body[4 + 96 + i] = list[i].InterfaceProtocol;
        }
        return Packet(UsbRedirPacketType.InterfaceInfo, 0, body);
    }

    public byte[] EncodeEndpointInfo(IReadOnlyList<UsbEndpointInfo> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var types = Enumerable.Repeat((byte)UsbTransferType.Invalid, 32).ToArray();
        var intervals = new byte[32];
        var interfaces = new byte[32];
        var maxPacket = new ushort[32];
        // Endpoint 0 (controllo) nelle due direzioni.
        foreach (var index in new[] { 0, 16 })
        {
            types[index] = (byte)UsbTransferType.Control;
            maxPacket[index] = 64;
        }
        foreach (var endpoint in endpoints.Where(e => (e.Address & 0x0F) != 0))
        {
            types[endpoint.RedirIndex] = (byte)endpoint.Type;
            intervals[endpoint.RedirIndex] = endpoint.Interval;
            interfaces[endpoint.RedirIndex] = endpoint.Interface;
            maxPacket[endpoint.RedirIndex] = endpoint.MaxPacketSize;
        }
        var withMax = Negotiated(UsbRedirCapability.EpInfoMaxPacketSize);
        var withStreams = Negotiated(UsbRedirCapability.BulkStreams);
        var body = new byte[96 + (withMax ? 64 : 0) + (withStreams ? 128 : 0)];
        types.CopyTo(body, 0);
        intervals.CopyTo(body, 32);
        interfaces.CopyTo(body, 64);
        if (withMax)
        {
            for (var i = 0; i < 32; i++) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(96 + (2 * i)), maxPacket[i]);
        }
        return Packet(UsbRedirPacketType.EpInfo, 0, body);
    }

    public byte[] EncodeConfigurationStatus(ulong id, UsbRedirStatus status, byte configuration) =>
        Packet(UsbRedirPacketType.ConfigurationStatus, id, [(byte)status, configuration]);

    public byte[] EncodeAltSettingStatus(ulong id, UsbRedirStatus status, byte @interface, byte alt) =>
        Packet(UsbRedirPacketType.AltSettingStatus, id, [(byte)status, @interface, alt]);

    public byte[] EncodeIsoStreamStatus(ulong id, UsbRedirStatus status, byte endpoint) =>
        Packet(UsbRedirPacketType.IsoStreamStatus, id, [(byte)status, endpoint]);

    public byte[] EncodeInterruptReceivingStatus(ulong id, UsbRedirStatus status, byte endpoint) =>
        Packet(UsbRedirPacketType.InterruptReceivingStatus, id, [(byte)status, endpoint]);

    public byte[] EncodeBulkStreamsStatus(ulong id, UsbRedirStatus status, uint endpoints, uint streams)
    {
        var body = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(body, endpoints);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), streams);
        body[8] = (byte)status;
        return Packet(UsbRedirPacketType.BulkStreamsStatus, id, body);
    }

    public byte[] EncodeBulkReceivingStatus(ulong id, UsbRedirStatus status, byte endpoint, uint streamId)
    {
        var body = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(body, streamId);
        body[4] = endpoint;
        body[5] = (byte)status;
        return Packet(UsbRedirPacketType.BulkReceivingStatus, id, body);
    }

    /// <summary>Risposta a un pacchetto di controllo: i dati solo per i trasferimenti IN.</summary>
    public byte[] EncodeControlReply(ulong id, byte endpoint, UsbControlSetup setup, UsbRedirStatus status, int actualLength, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var payload = setup.IsDeviceToHost ? data.AsSpan(0, Math.Min(Math.Max(actualLength, 0), data.Length)) : [];
        var body = new byte[10 + payload.Length];
        body[0] = endpoint;
        body[1] = setup.Request;
        body[2] = setup.RequestType;
        body[3] = (byte)status;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), setup.Value);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), setup.Index);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), (ushort)Math.Clamp(actualLength, 0, ushort.MaxValue));
        payload.CopyTo(body.AsSpan(10));
        return Packet(UsbRedirPacketType.ControlPacket, id, body);
    }

    /// <summary>Risposta a un pacchetto bulk: i dati solo per gli endpoint IN.</summary>
    public byte[] EncodeBulkReply(ulong id, byte endpoint, uint streamId, UsbRedirStatus status, int actualLength, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var longLength = Negotiated(UsbRedirCapability.BulkLength32);
        var length = (uint)Math.Max(actualLength, 0);
        var payload = (endpoint & 0x80) != 0 ? data.AsSpan(0, Math.Min((int)length, data.Length)) : [];
        var size = longLength ? 10 : 8;
        var body = new byte[size + payload.Length];
        body[0] = endpoint;
        body[1] = (byte)status;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), unchecked((ushort)length));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), streamId);
        if (longLength) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), (ushort)(length >> 16));
        payload.CopyTo(body.AsSpan(size));
        return Packet(UsbRedirPacketType.BulkPacket, id, body);
    }

    /// <summary>Pacchetto interrupt: risposta a un OUT o dati IN non richiesti.</summary>
    public byte[] EncodeInterrupt(ulong id, byte endpoint, UsbRedirStatus status, int actualLength, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var payload = (endpoint & 0x80) != 0 ? data.AsSpan(0, Math.Min(Math.Max(actualLength, 0), data.Length)) : [];
        var body = new byte[4 + payload.Length];
        body[0] = endpoint;
        body[1] = (byte)status;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)Math.Clamp(actualLength, 0, ushort.MaxValue));
        payload.CopyTo(body.AsSpan(4));
        return Packet(UsbRedirPacketType.InterruptPacket, id, body);
    }
}

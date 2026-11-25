using System.Buffers.Binary;
using System.Text;
using SpiceKit.UsbRedirWire;

namespace SpiceKit.UsbEmulation;

/// <summary>
/// Lettore CD/DVD USB 2.0 virtuale (Mass Storage, Bulk-Only Transport, SCSI MMC) basato su un file
/// immagine, mostrato all'ospite attraverso uno slot usbredir (porting di <c>EmulatedCDROM</c> macOS).
/// </summary>
/// <remarks>
/// L'immagine si legge a richiesta, mai tutta in memoria. Thread-safe: i trasferimenti dei due
/// endpoint bulk passano da un lock (il protocollo Bulk-Only è comunque sequenziale).
/// </remarks>
public sealed class EmulatedCdrom : IUsbRedirDevice
{
    /// <summary>VID open source di pid.codes.</summary>
    public const ushort VendorId = 0x1209;

    /// <summary>PID di prova di pid.codes. TODO(verify): registrarne uno vero prima del rilascio.</summary>
    public const ushort ProductId = 0x0010;

    internal const byte BulkIn = 0x81;
    internal const byte BulkOut = 0x02;
    private const ushort MaxPacket = 512;

    private readonly IBlockSource _source;
    private readonly Action? _onEject;
    private readonly Lock _lock = new();
    private readonly ScsiCdrom _scsi;
    private BulkOnlyTransport _bot = new();
    private byte _configured;

    /// <param name="source">Immagine; si chiude con il dispositivo se è <see cref="IDisposable"/>.</param>
    /// <param name="name">Nome mostrato (di solito il nome del file).</param>
    /// <param name="onEject">L'ospite ha espulso il disco (START STOP UNIT con LoEj).</param>
    public EmulatedCdrom(IBlockSource source, string name, Action? onEject = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        DisplayName = name;
        _onEject = onEject;
        _scsi = new ScsiCdrom(source.Size / ScsiCdrom.BlockSize);
    }

    public string DisplayName { get; }

    public static UsbDeviceDescription Description { get; } = new(UsbSpeed.High, 0, 0, 0, VendorId, ProductId, 0x0100,
        [new UsbInterfaceInfo(0, 0x08, 0x06, 0x50)],
        [new UsbEndpointInfo(BulkIn, UsbTransferType.Bulk, 0, 0, MaxPacket), new UsbEndpointInfo(BulkOut, UsbTransferType.Bulk, 0, 0, MaxPacket)]);

    public Task<UsbDeviceDescription> DescribeAsync() => Task.FromResult(Description);

    public Task<UsbTransferResult> ControlAsync(UsbControlSetup setup, byte[] data)
    {
        lock (_lock) return Task.FromResult(Control(setup));
    }

    private UsbTransferResult Control(UsbControlSetup setup)
    {
        var kind = (setup.RequestType >> 5) & 0x03;
        return (kind, setup.Request) switch
        {
            (0, 0x06) => UsbDescriptors.Find(CdromDescriptor(setup.Value), setup.Length), // GET_DESCRIPTOR
            (0, 0x00) => UsbTransferResult.Ok(new byte[Math.Min(2, (int)setup.Length)]), // GET_STATUS
            (0, 0x01) or (0, 0x03) => UsbTransferResult.Ok(), // CLEAR_FEATURE / SET_FEATURE (ENDPOINT_HALT)
            (0, 0x08) => UsbTransferResult.Ok([_configured]), // GET_CONFIGURATION
            (0, 0x09) => SetConfigurationValue((byte)setup.Value),
            (0, 0x0A) => UsbTransferResult.Ok([0]), // GET_INTERFACE
            (0, 0x0B) => setup.Value == 0 ? UsbTransferResult.Ok() : UsbTransferResult.Failed(UsbRedirStatus.Stall), // SET_INTERFACE
            (1, 0xFE) => UsbTransferResult.Ok([0]), // Get Max LUN
            (1, 0xFF) => ResetTransport(), // Bulk-Only Mass Storage Reset
            _ => UsbTransferResult.Failed(UsbRedirStatus.Stall),
        };
    }

    private UsbTransferResult SetConfigurationValue(byte value)
    {
        _configured = value;
        return UsbTransferResult.Ok();
    }

    private UsbTransferResult ResetTransport()
    {
        _bot = new BulkOnlyTransport();
        return UsbTransferResult.Ok();
    }

    public Task<UsbTransferResult> BulkAsync(byte endpoint, byte[] data, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        UsbTransferResult result;
        var ejected = false;
        lock (_lock)
        {
            result = endpoint switch
            {
                BulkOut => _bot.HandleOut(data, _scsi, out ejected),
                BulkIn => _bot.HandleIn(length, _scsi, _source),
                _ => UsbTransferResult.Failed(UsbRedirStatus.Inval),
            };
        }
        if (ejected) _onEject?.Invoke();
        return Task.FromResult(result);
    }

    public Task<UsbTransferResult> InterruptAsync(byte endpoint, byte[] data, int length) =>
        Task.FromResult(UsbTransferResult.Failed(UsbRedirStatus.Inval));

    public Task<UsbRedirStatus> SetConfigurationAsync(byte value)
    {
        lock (_lock)
        {
            if (value > 1) return Task.FromResult(UsbRedirStatus.Stall);
            _configured = value;
            return Task.FromResult(UsbRedirStatus.Success);
        }
    }

    public Task<byte> GetConfigurationAsync()
    {
        lock (_lock) return Task.FromResult(_configured);
    }

    public Task<UsbRedirStatus> SetAltSettingAsync(byte interfaceNumber, byte alt) =>
        Task.FromResult(interfaceNumber == 0 && alt == 0 ? UsbRedirStatus.Success : UsbRedirStatus.Stall);

    public Task<byte?> GetAltSettingAsync(byte interfaceNumber) => Task.FromResult(interfaceNumber == 0 ? (byte?)0 : null);

    public Task<bool> ResetAsync()
    {
        lock (_lock) _bot = new BulkOnlyTransport();
        return Task.FromResult(true);
    }

    public Task AbortAsync(byte endpoint) => Task.CompletedTask;

    public Task CloseAsync()
    {
        (_source as IDisposable)?.Dispose();
        return Task.CompletedTask;
    }

    private static byte[]? CdromDescriptor(ushort value) => (value >> 8) switch
    {
        0x01 => UsbDescriptors.Device(VendorId, ProductId, usb20: true),
        0x02 => Configuration(MaxPacket, 0x02),
        0x07 => Configuration(64, 0x07), // other-speed (full speed: pacchetti bulk da 64 byte)
        0x06 => [10, 0x06, 0x00, 0x02, 0, 0, 0, 64, 1, 0], // device qualifier
        0x03 => (value & 0xFF) switch
        {
            0 => [4, 0x03, 0x09, 0x04], // inglese (USA)
            1 => UsbDescriptors.String("PxStation Windows"),
            2 => UsbDescriptors.String("Virtual CD-ROM"),
            3 => UsbDescriptors.String("5350494345303031"), // ≥ 12 cifre esadecimali, come vuole MSC
            _ => null,
        },
        _ => null,
    };

    private static byte[] Configuration(ushort maxPacket, byte type) =>
    [
        9, type, 32, 0, 1, 1, 0, 0x80, 50, // 32 byte in tutto, 1 interfaccia, alimentato dal bus, 100 mA
        9, 0x04, 0, 0, 2, 0x08, 0x06, 0x50, 0, // interfaccia: MSC, SCSI trasparente, BOT
        7, 0x05, BulkIn, 0x02, (byte)maxPacket, (byte)(maxPacket >> 8), 0,
        7, 0x05, BulkOut, 0x02, (byte)maxPacket, (byte)(maxPacket >> 8), 0,
    ];
}

/// <summary>Descrittori USB comuni ai dispositivi emulati.</summary>
internal static class UsbDescriptors
{
    public static byte[] Device(ushort vendor, ushort product, bool usb20) =>
    [
        18, 0x01, (byte)(usb20 ? 0x00 : 0x10), (byte)(usb20 ? 0x02 : 0x01), // bcdUSB 2.00 / 1.10
        0x00, 0x00, 0x00, 64, // classe per interfaccia, bMaxPacketSize0
        (byte)vendor, (byte)(vendor >> 8), (byte)product, (byte)(product >> 8),
        0x00, 0x01, // bcdDevice 1.00
        1, 2, 3, 1, // iManufacturer, iProduct, iSerialNumber, bNumConfigurations
    ];

    public static byte[] String(string text)
    {
        var units = Encoding.Unicode.GetBytes(text);
        return [(byte)(2 + units.Length), 0x03, .. units];
    }

    public static UsbTransferResult Find(byte[]? descriptor, ushort length) =>
        descriptor is null ? UsbTransferResult.Failed(UsbRedirStatus.Stall) : UsbTransferResult.Ok(descriptor[..Math.Min(descriptor.Length, length)]);
}

/// <summary>Macchina a stati del Bulk-Only Transport della classe Mass Storage (CBW → dati → CSW).</summary>
internal sealed class BulkOnlyTransport
{
    private const uint CbwSignature = 0x4342_5355; // "USBC"
    private const uint CswSignature = 0x5342_5355; // "USBS"

    private enum Phase
    {
        Idle,
        DataIn,
        DataOut,
        Status,

        /// <summary>CBW non valido: si stalla fino a un reset Bulk-Only.</summary>
        Halted,
    }

    private Phase _phase = Phase.Idle;
    private uint _tag;
    private int _expected;
    private int _done;
    private byte _status;
    private int _residue;
    private ScsiResponse? _response;

    public UsbTransferResult HandleOut(byte[] data, ScsiCdrom scsi, out bool ejected)
    {
        ejected = false;
        switch (_phase)
        {
            case Phase.Idle:
                if (data.Length != 31 || BinaryPrimitives.ReadUInt32LittleEndian(data) != CbwSignature) return Halt();
                var cdbLength = data[14] & 0x1F;
                if (cdbLength is < 1 or > 16) return Halt();
                _tag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                _expected = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)), (uint)int.MaxValue);
                var directionIn = (data[12] & 0x80) != 0;
                var response = scsi.Execute(data.AsSpan(15, cdbLength));
                ejected = response.Ejected;
                _done = 0;
                if (_expected == 0)
                {
                    ToStatus(0, response.Status);
                }
                else if (directionIn)
                {
                    _response = response;
                    _phase = Phase.DataIn;
                }
                else
                {
                    // Sola lettura: i dati OUT si accettano e si scartano, poi si riporta l'esito.
                    _status = response.Status == ScsiCdrom.Good ? scsi.Fail(Sense.WriteProtected).Status : response.Status;
                    _phase = Phase.DataOut;
                }
                return UsbTransferResult.Written(data.Length);
            case Phase.DataOut:
                _done += data.Length;
                if (_done >= _expected) ToStatus(Math.Max(0, _expected - _done), _status);
                return UsbTransferResult.Written(data.Length);
            default:
                return UsbTransferResult.Failed(UsbRedirStatus.Stall);
        }
    }

    public UsbTransferResult HandleIn(int maxLength, ScsiCdrom scsi, IBlockSource source)
    {
        switch (_phase)
        {
            case Phase.DataIn when _response is { } response:
                var available = Math.Min(_expected, response.Length) - _done;
                var chunk = Math.Max(0, Math.Min(Math.Min(maxLength, available), UsbTransferLimits.MaxTransferLength));
                byte[] bytes;
                try
                {
                    bytes = response.Read(_done, chunk, source);
                }
                catch (IOException)
                {
                    // Lettura dell'immagine non riuscita: la fase dati finisce prima e si riporta MEDIUM ERROR.
                    scsi.Fail(Sense.UnrecoveredReadError);
                    ToStatus(_expected - _done, ScsiCdrom.CheckCondition);
                    return UsbTransferResult.Ok();
                }
                _done += bytes.Length;
                if (_done >= Math.Min(_expected, response.Length) || bytes.Length < maxLength) ToStatus(_expected - _done, response.Status);
                return UsbTransferResult.Ok(bytes);
            case Phase.Status:
                _phase = Phase.Idle;
                var csw = new byte[13];
                BinaryPrimitives.WriteUInt32LittleEndian(csw, CswSignature);
                BinaryPrimitives.WriteUInt32LittleEndian(csw.AsSpan(4), _tag);
                BinaryPrimitives.WriteUInt32LittleEndian(csw.AsSpan(8), (uint)Math.Max(0, _residue));
                csw[12] = _status;
                return UsbTransferResult.Ok(csw[..Math.Min(13, Math.Max(0, maxLength))]);
            default:
                return UsbTransferResult.Failed(UsbRedirStatus.Stall);
        }
    }

    private void ToStatus(int residue, byte status)
    {
        _phase = Phase.Status;
        _residue = residue;
        _status = status;
        _response = null;
    }

    private UsbTransferResult Halt()
    {
        _phase = Phase.Halted;
        return UsbTransferResult.Failed(UsbRedirStatus.Stall);
    }
}

// Protocollo usbredir 0.7: costanti e strutture (usbredir/docs/usb-redirection-protocol.md).
// Interi little-endian, strutture impacchettate. Questo client è lo usb-host; il dispositivo
// usb-redir di QEMU è lo usb-guest (porting di USBRedirProtocol.swift).

namespace SpiceKit.UsbRedirWire;

/// <summary>Tipi di pacchetto <c>usb_redir_*</c>.</summary>
public enum UsbRedirPacketType : uint
{
    Hello = 0,
    DeviceConnect,
    DeviceDisconnect,
    Reset,
    InterfaceInfo,
    EpInfo,
    SetConfiguration,
    GetConfiguration,
    ConfigurationStatus,
    SetAltSetting,
    GetAltSetting,
    AltSettingStatus,
    StartIsoStream,
    StopIsoStream,
    IsoStreamStatus,
    StartInterruptReceiving,
    StopInterruptReceiving,
    InterruptReceivingStatus,
    AllocBulkStreams,
    FreeBulkStreams,
    BulkStreamsStatus,
    CancelDataPacket,
    FilterReject,
    FilterFilter,
    DeviceDisconnectAck,
    StartBulkReceiving,
    StopBulkReceiving,
    BulkReceivingStatus,
    ControlPacket = 100,
    BulkPacket,
    IsoPacket,
    InterruptPacket,
    BufferedBulkPacket,
}

/// <summary><c>usb_redir_success</c> … <c>usb_redir_babble</c>.</summary>
public enum UsbRedirStatus : byte
{
    Success = 0,
    Cancelled,
    Inval,
    IoError,
    Stall,
    Timeout,
    Babble,
}

/// <summary><c>usb_redir_cap_*</c>: indici di bit.</summary>
public enum UsbRedirCapability
{
    BulkStreams = 0,
    ConnectDeviceVersion,
    Filter,
    DeviceDisconnectAck,
    EpInfoMaxPacketSize,
    Ids64,
    BulkLength32,
    BulkReceiving,
}

/// <summary><c>usb_redir_speed_*</c>.</summary>
public enum UsbSpeed : byte
{
    Low = 0,
    Full,
    High,
    Super,
    Unknown = 255,
}

/// <summary><c>usb_redir_type_*</c> (come i tipi di trasferimento degli endpoint USB).</summary>
public enum UsbTransferType : byte
{
    Control = 0,
    Iso,
    Bulk,
    Interrupt,
    Invalid = 255,
}

/// <summary>Limiti dei trasferimenti chiesti dall'ospite (le lunghezze dichiarate le decide lui).</summary>
public static class UsbTransferLimits
{
    /// <summary>Trasferimento singolo più grande (bulk, interrupt, controllo).</summary>
    public const int MaxTransferLength = 4 * 1024 * 1024;

    /// <summary>Byte prenotati al massimo da tutti i trasferimenti in coda e in corso di uno slot.</summary>
    public const int MaxOutstandingBytes = 32 * 1024 * 1024;

    /// <summary>Trasferimenti in coda e in corso al massimo per slot.</summary>
    public const int MaxOutstandingPackets = 1024;
}

/// <summary>Capacità usbredir come maschera di bit su parole da 32 bit.</summary>
public sealed record UsbRedirCapabilities(IReadOnlyList<uint> Words)
{
    /// <summary>Capacità annunciate dal client (usb-host): niente bulk stream, niente bulk receiving, niente iso.</summary>
    public static UsbRedirCapabilities Client { get; } = Of(UsbRedirCapability.ConnectDeviceVersion, UsbRedirCapability.Filter,
        UsbRedirCapability.DeviceDisconnectAck, UsbRedirCapability.EpInfoMaxPacketSize, UsbRedirCapability.Ids64,
        UsbRedirCapability.BulkLength32);

    public static UsbRedirCapabilities Of(params UsbRedirCapability[] caps)
    {
        ArgumentNullException.ThrowIfNull(caps);
        var word = 0u;
        foreach (var cap in caps) word |= 1u << (int)cap;
        return new UsbRedirCapabilities([word]);
    }

    public bool Contains(UsbRedirCapability cap)
    {
        var index = (int)cap / 32;
        return index < Words.Count && (Words[index] & (1u << ((int)cap % 32))) != 0;
    }
}

/// <summary>Pacchetto di setup di un trasferimento di controllo.</summary>
public readonly record struct UsbControlSetup(byte RequestType, byte Request, ushort Value, ushort Index, ushort Length)
{
    public bool IsDeviceToHost => (RequestType & 0x80) != 0;
}

/// <summary>Un'interfaccia della configurazione attiva.</summary>
public readonly record struct UsbInterfaceInfo(byte Number, byte InterfaceClass, byte InterfaceSubclass, byte InterfaceProtocol);

/// <summary>Un endpoint della configurazione attiva; <c>Address</c> ha il bit di direzione (0x80 = IN).</summary>
public readonly record struct UsbEndpointInfo(byte Address, UsbTransferType Type, byte Interval, byte Interface, ushort MaxPacketSize)
{
    public bool IsIn => (Address & 0x80) != 0;

    /// <summary>Indice negli array di <c>usb_redir_ep_info</c>: 0–15 OUT, 16–31 IN.</summary>
    public int RedirIndex => (Address & 0x0F) + (IsIn ? 16 : 0);
}

/// <summary>Ciò che descrivono <c>device_connect</c>, <c>interface_info</c> ed <c>ep_info</c>.</summary>
public sealed record UsbDeviceDescription(UsbSpeed Speed, byte DeviceClass, byte DeviceSubclass, byte DeviceProtocol,
    ushort VendorId, ushort ProductId, ushort DeviceVersionBcd, IReadOnlyList<UsbInterfaceInfo> Interfaces,
    IReadOnlyList<UsbEndpointInfo> Endpoints)
{
    /// <summary>Vista per il filtro: classe del dispositivo più quelle di ogni interfaccia.</summary>
    public UsbFilter.Device FilterDevice =>
        new(new HashSet<byte>(Interfaces.Select(i => i.InterfaceClass).Prepend(DeviceClass)), VendorId, ProductId, DeviceVersionBcd);

    /// <summary>Tastiere e mouse HID di avvio non si reindirizzano mai senza conferma (toglierebbero l'input al PC).</summary>
    public bool IsBootKeyboardOrMouse =>
        Interfaces.Any(i => i.InterfaceClass == 3 && i.InterfaceSubclass == 1 && i.InterfaceProtocol is 1 or 2);
}

/// <summary>Esito di un trasferimento: <c>Data</c> sono i dati letti (IN), <c>ActualLength</c> i byte trasferiti.</summary>
public sealed record UsbTransferResult(UsbRedirStatus Status, byte[] Data, int ActualLength)
{
    public static UsbTransferResult Ok(byte[]? data = null) => new(UsbRedirStatus.Success, data ?? [], data?.Length ?? 0);

    public static UsbTransferResult Written(int length) => new(UsbRedirStatus.Success, [], length);

    public static UsbTransferResult Failed(UsbRedirStatus status) => new(status, [], 0);
}

/// <summary>
/// Dispositivo USB che si può mostrare all'ospite attraverso uno slot usbredir: reale (UsbDk) o
/// emulato (CD-ROM, lettore di smartcard). I trasferimenti sullo stesso endpoint arrivano uno
/// alla volta e in ordine; endpoint diversi possono procedere insieme.
/// </summary>
public interface IUsbRedirDevice
{
    string DisplayName { get; }

    /// <summary>Descrizione del dispositivo e della configurazione attiva (riletta dopo i cambi di configurazione).</summary>
    Task<UsbDeviceDescription> DescribeAsync();

    /// <summary>Trasferimento di controllo sull'endpoint 0 (<paramref name="data"/> è il carico OUT).</summary>
    Task<UsbTransferResult> ControlAsync(UsbControlSetup setup, byte[] data);

    /// <summary>Bulk: OUT invia <paramref name="data"/>; IN legge fino a <paramref name="length"/> byte.</summary>
    Task<UsbTransferResult> BulkAsync(byte endpoint, byte[] data, int length);

    /// <summary>Interrupt: OUT invia <paramref name="data"/>; IN legge fino a <paramref name="length"/> byte (interrogazione).</summary>
    Task<UsbTransferResult> InterruptAsync(byte endpoint, byte[] data, int length);

    Task<UsbRedirStatus> SetConfigurationAsync(byte value);

    Task<byte> GetConfigurationAsync();

    Task<UsbRedirStatus> SetAltSettingAsync(byte interfaceNumber, byte alt);

    Task<byte?> GetAltSettingAsync(byte interfaceNumber);

    /// <summary>Reset della porta; <see langword="false"/> se dopo il dispositivo non c'è più.</summary>
    Task<bool> ResetAsync();

    /// <summary>Interrompe i trasferimenti in corso su un endpoint (finiscono con <see cref="UsbRedirStatus.Cancelled"/>).</summary>
    Task AbortAsync(byte endpoint);

    /// <summary>Rilascia il dispositivo (una volta sola, allo sgancio).</summary>
    Task CloseAsync();
}

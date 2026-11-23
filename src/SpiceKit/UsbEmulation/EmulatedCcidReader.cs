using SpiceKit.Channels;
using SpiceKit.UsbRedirWire;

namespace SpiceKit.UsbEmulation;

/// <summary>
/// Lettore di smartcard USB virtuale (classe CCID 0x0B, rev. 1.10) mostrato attraverso uno slot
/// usbredir (porting di <c>EmulatedCCIDReader</c> macOS, scritto dalla specifica USB CCID 1.1).
/// </summary>
/// <remarks>
/// <para>Funziona con qualunque QEMU (anche quello di Proxmox, senza <c>ccid-card-passthru</c>) e usa il
/// driver CCID di classe dell'ospite. Scambio a livello di APDU brevi: l'ospite manda APDU interi in
/// <c>PC_to_RDR_XfrBlock</c> e riceve APDU interi.</para>
/// <para>Limiti: messaggi ≤ 271 byte, trasferimenti OUT ≤ 4 messaggi, e oltre 4 risposte non lette i
/// comandi nuovi si rifiutano con uno stallo (un ospite che non legge non fa crescere la memoria).</para>
/// </remarks>
public sealed class EmulatedCcidReader : IUsbRedirDevice
{
    public const ushort VendorId = 0x1209;

    /// <summary>PID di prova di pid.codes. TODO(verify): registrarne uno vero prima del rilascio.</summary>
    public const ushort ProductId = 0x0011;

    internal const byte BulkIn = 0x81;
    internal const byte BulkOut = 0x02;
    internal const byte InterruptIn = 0x83;

    /// <summary>dwMaxCCIDMessageLength: intestazione di 10 byte + APDU breve di 261 byte.</summary>
    internal const int MaxMessageLength = 271;

    internal const int MaxUnreadBytes = 4 * MaxMessageLength;
    internal const int MaxOutTransfer = 4 * MaxMessageLength;

    private readonly ISmartcard _card;
    private readonly Lock _lock = new();
    private readonly List<byte> _out = [];
    private readonly List<byte> _in = [];
    private byte _configured;
    private byte[]? _atr;
    private bool _powered;
    private bool _cardFailed;
    private byte[]? _pendingNotification = [0x50, 0x03]; // carta presente + cambiata
    private TaskCompletionSource<bool>? _inWaiter;
    private TaskCompletionSource<bool>? _interruptWaiter;
    private bool _closed;

    /// <summary>Un ciclo sta già eseguendo i comandi: i nuovi dati li prende lui.</summary>
    private bool _processing;

    public EmulatedCcidReader(ISmartcard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        _card = card;
        DisplayName = card.DisplayName;
    }

    public string DisplayName { get; }

    public static UsbDeviceDescription Description { get; } = new(UsbSpeed.Full, 0, 0, 0, VendorId, ProductId, 0x0100,
        [new UsbInterfaceInfo(0, 0x0B, 0, 0)],
        [
            new UsbEndpointInfo(BulkOut, UsbTransferType.Bulk, 0, 0, 64),
            new UsbEndpointInfo(BulkIn, UsbTransferType.Bulk, 0, 0, 64),
            new UsbEndpointInfo(InterruptIn, UsbTransferType.Interrupt, 16, 0, 8),
        ]);

    public Task<UsbDeviceDescription> DescribeAsync() => Task.FromResult(Description);

    public Task<UsbTransferResult> ControlAsync(UsbControlSetup setup, byte[] data)
    {
        var kind = (setup.RequestType >> 5) & 0x03;
        lock (_lock)
        {
            return Task.FromResult((kind, setup.Request) switch
            {
                (0, 0x06) => UsbDescriptors.Find(Descriptor(setup.Value), setup.Length),
                (0, 0x00) => UsbTransferResult.Ok(new byte[Math.Min(2, (int)setup.Length)]),
                (0, 0x01) or (0, 0x03) => UsbTransferResult.Ok(),
                (0, 0x08) => UsbTransferResult.Ok([_configured]),
                (0, 0x09) => Configure((byte)setup.Value),
                (0, 0x0A) => UsbTransferResult.Ok([0]),
                (0, 0x0B) => setup.Value == 0 ? UsbTransferResult.Ok() : UsbTransferResult.Failed(UsbRedirStatus.Stall),
                (1, 0x01) => UsbTransferResult.Ok(), // ABORT
                (1, 0x02) or (1, 0x03) => UsbTransferResult.Ok([]), // GET_CLOCK_FREQUENCIES / GET_DATA_RATES: nessuna
                _ => UsbTransferResult.Failed(UsbRedirStatus.Stall),
            });
        }
    }

    private UsbTransferResult Configure(byte value)
    {
        _configured = value;
        return UsbTransferResult.Ok();
    }

    public async Task<UsbTransferResult> BulkAsync(byte endpoint, byte[] data, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        switch (endpoint)
        {
            case BulkOut:
                lock (_lock)
                {
                    // Si rifiuta (stallo) invece di accodare: l'ospite deve prima leggere le risposte.
                    if (data.Length > MaxOutTransfer || _in.Count > MaxUnreadBytes) return UsbTransferResult.Failed(UsbRedirStatus.Stall);
                    _out.AddRange(data);
                }
                await ProcessOutAsync().ConfigureAwait(false);
                return UsbTransferResult.Written(data.Length);
            case BulkIn:
                while (true)
                {
                    TaskCompletionSource<bool> waiter;
                    lock (_lock)
                    {
                        if (_in.Count > 0)
                        {
                            var count = Math.Min(Math.Max(length, 0), _in.Count);
                            var chunk = _in.GetRange(0, count).ToArray();
                            _in.RemoveRange(0, count);
                            return UsbTransferResult.Ok(chunk);
                        }
                        if (_closed) return UsbTransferResult.Failed(UsbRedirStatus.Cancelled);
                        // Un'attesa più nuova sullo stesso endpoint chiude la precedente.
                        _inWaiter?.TrySetResult(false);
                        waiter = _inWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    if (!await waiter.Task.ConfigureAwait(false)) return UsbTransferResult.Failed(UsbRedirStatus.Cancelled);
                }
            default:
                return UsbTransferResult.Failed(UsbRedirStatus.Inval);
        }
    }

    public async Task<UsbTransferResult> InterruptAsync(byte endpoint, byte[] data, int length)
    {
        if (endpoint != InterruptIn) return UsbTransferResult.Failed(UsbRedirStatus.Inval);
        while (true)
        {
            TaskCompletionSource<bool> waiter;
            lock (_lock)
            {
                if (_pendingNotification is { } message)
                {
                    _pendingNotification = null;
                    return UsbTransferResult.Ok(message[..Math.Min(message.Length, Math.Max(length, 0))]);
                }
                if (_closed) return UsbTransferResult.Failed(UsbRedirStatus.Cancelled);
                _interruptWaiter?.TrySetResult(false);
                waiter = _interruptWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (!await waiter.Task.ConfigureAwait(false)) return UsbTransferResult.Failed(UsbRedirStatus.Cancelled);
        }
    }

    public Task<UsbRedirStatus> SetConfigurationAsync(byte value)
    {
        lock (_lock)
        {
            if (value > 1) return Task.FromResult(UsbRedirStatus.Stall);
            _configured = value;
        }
        return Task.FromResult(UsbRedirStatus.Success);
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
        lock (_lock)
        {
            _out.Clear();
            _in.Clear();
            _powered = false;
        }
        return Task.FromResult(true);
    }

    public Task AbortAsync(byte endpoint)
    {
        lock (_lock)
        {
            if (endpoint == BulkIn)
            {
                _inWaiter?.TrySetResult(false);
                _inWaiter = null;
            }
            else if (endpoint == InterruptIn)
            {
                _interruptWaiter?.TrySetResult(false);
                _interruptWaiter = null;
            }
        }
        return Task.CompletedTask;
    }

    public async Task CloseAsync()
    {
        bool connected;
        lock (_lock)
        {
            _closed = true;
            _inWaiter?.TrySetResult(false);
            _interruptWaiter?.TrySetResult(false);
            _inWaiter = _interruptWaiter = null;
            connected = _atr is not null;
            _atr = null;
        }
        if (connected) await _card.DisconnectAsync().ConfigureAwait(false);
    }

    // MARK: comandi CCID

    private byte IccStatus => _cardFailed ? (byte)2 : _powered ? (byte)0 : (byte)1;

    /// <summary>Esegue i messaggi completi arrivati; uno alla volta, nell'ordine (la carta non va mai intercalata).</summary>
    private async Task ProcessOutAsync()
    {
        lock (_lock)
        {
            if (_processing) return;
            _processing = true;
        }
        try
        {
            while (true)
            {
                byte[] message;
                lock (_lock)
                {
                    if (_out.Count < 10)
                    {
                        _processing = false; // nello stesso lock del controllo: nessun dato resta indietro
                        return;
                    }
                    if (_in.Count > MaxUnreadBytes)
                    {
                        _out.Clear(); // più comandi in coda di quante risposte si tengono
                        _processing = false;
                        return;
                    }
                    var length = _out[1] | (_out[2] << 8) | (_out[3] << 16) | (_out[4] << 24);
                    if (length < 0 || length > MaxMessageLength - 10)
                    {
                        // Messaggio non valido o troppo grande: si risponde a questa intestazione e si scarta il flusso.
                        var header = _out.GetRange(0, 10).ToArray();
                        _out.Clear();
                        Respond(CcidResponse.SlotStatus(header, IccStatus, failed: true, error: 1)); // dwLength errato
                        _processing = false;
                        return;
                    }
                    if (_out.Count < 10 + length)
                    {
                        _processing = false;
                        return;
                    }
                    message = _out.GetRange(0, 10 + length).ToArray();
                    _out.RemoveRange(0, 10 + length);
                }
                var response = await ExecuteAsync(message).ConfigureAwait(false);
                lock (_lock) Respond(response);
            }
        }
        catch
        {
            lock (_lock) _processing = false;
            throw;
        }
    }

    private async Task<byte[]> ExecuteAsync(byte[] message)
    {
        var header = message[..10];
        var payload = message[10..];
        byte status;
        lock (_lock) status = IccStatus;
        if (header[5] != 0) return CcidResponse.SlotStatus(header, status, failed: true, error: 5); // slot errato
        switch (header[0])
        {
            case 0x62: // PC_to_RDR_IccPowerOn
                try
                {
                    byte[]? atr;
                    lock (_lock) atr = _atr;
                    atr ??= await _card.ConnectAsync().ConfigureAwait(false);
                    lock (_lock)
                    {
                        _atr = atr;
                        _powered = true;
                        _cardFailed = false;
                    }
                    return CcidResponse.DataBlock(header, 0, atr);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    lock (_lock) _cardFailed = true;
                    return CcidResponse.DataBlock(header, 2, [], failed: true, error: 0xFE); // ICC_MUTE
                }
            case 0x63: // PC_to_RDR_IccPowerOff
                lock (_lock)
                {
                    _powered = false;
                    return CcidResponse.SlotStatus(header, IccStatus);
                }
            case 0x65 or 0x6E or 0x71 or 0x72: // GetSlotStatus, IccClock, Mechanical, Abort
                return CcidResponse.SlotStatus(header, status);
            case 0x6F: // PC_to_RDR_XfrBlock
                bool powered;
                lock (_lock) powered = _powered;
                if (!powered || payload.Length == 0) return CcidResponse.DataBlock(header, status, [], failed: true, error: 0xFE);
                try
                {
                    var response = await _card.TransmitAsync(payload).ConfigureAwait(false);
                    return response.Length > MaxMessageLength - 10
                        ? CcidResponse.DataBlock(header, status, [], failed: true, error: 0xFB) // HW_ERROR
                        : CcidResponse.DataBlock(header, 0, response);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return CcidResponse.DataBlock(header, status, [], failed: true, error: 0xFE);
                }
            case 0x6C or 0x61 or 0x6D: // GetParameters, SetParameters, ResetParameters
                byte[] currentAtr;
                lock (_lock) currentAtr = _atr ?? [];
                return CcidResponse.Parameters(header, status, currentAtr);
            case 0x73: // SetDataRateAndClockFrequency: i valori predefiniti
                return CcidResponse.DataRateAndClock(header, status);
            default: // Escape, T0APDU, Secure…: CMD_NOT_SUPPORTED
                return CcidResponse.SlotStatus(header, status, failed: true, error: 0);
        }
    }

    /// <summary>Accoda una risposta e sveglia la lettura bulk in attesa (con il lock preso).</summary>
    private void Respond(byte[] bytes)
    {
        _in.AddRange(bytes);
        _inWaiter?.TrySetResult(true);
        _inWaiter = null;
    }

    // MARK: descrittori

    /// <summary>Descrittore di classe Smart Card (CCID 1.1 §5.1), 54 byte.</summary>
    private static readonly byte[] CcidClass =
    [
        54, 0x21, 0x10, 0x01, // bLength, tipo, bcdCCID 1.10
        0x00, // bMaxSlotIndex
        0x07, // bVoltageSupport: 5 V, 3 V, 1,8 V
        .. Le32(0x0000_0003), // dwProtocols: T=0, T=1
        .. Le32(3580), .. Le32(3580), // dwDefaultClock, dwMaximumClock (kHz)
        0x00,
        .. Le32(9600), .. Le32(115_200), // dwDataRate, dwMaxDataRate
        0x00,
        .. Le32(254), // dwMaxIFSD
        .. Le32(0), .. Le32(0), // dwSynchProtocols, dwMechanical
        // dwFeatures: parametri automatici dall'ATR, attivazione, tensione, clock, baud, PPS, IFSD; APDU brevi.
        .. Le32(0x0002_04BE),
        .. Le32(MaxMessageLength),
        0xFF, 0xFF, // bClassGetResponse / bClassEnvelope: eco della classe dell'APDU
        0x00, 0x00, // wLcdLayout: nessuno
        0x00, // bPINSupport: nessuno
        0x01, // bMaxCCIDBusySlots
    ];

    private static byte[] Configuration()
    {
        byte[] body =
        [
            9, 0x04, 0, 0, 3, 0x0B, 0x00, 0x00, 0, // interfaccia CCID, 3 endpoint
            .. CcidClass,
            7, 0x05, BulkOut, 0x02, 64, 0, 0,
            7, 0x05, BulkIn, 0x02, 64, 0, 0,
            7, 0x05, InterruptIn, 0x03, 8, 0, 16,
        ];
        var total = 9 + body.Length;
        return [9, 0x02, (byte)total, (byte)(total >> 8), 1, 1, 0, 0x80, 50, .. body];
    }

    private static byte[]? Descriptor(ushort value) => (value >> 8) switch
    {
        0x01 => UsbDescriptors.Device(VendorId, ProductId, usb20: false),
        0x02 => Configuration(),
        0x03 => (value & 0xFF) switch
        {
            0 => [4, 0x03, 0x09, 0x04],
            1 => UsbDescriptors.String("Windows Spice Client"),
            2 => UsbDescriptors.String("Virtual Smart Card Reader"),
            3 => UsbDescriptors.String("5350494345434344"),
            _ => null,
        },
        _ => null, // solo full speed: niente device qualifier
    };

    internal static byte[] Le32(uint value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];
}

/// <summary>Messaggi RDR_to_PC del lettore CCID.</summary>
internal static class CcidResponse
{
    private static byte[] Message(byte type, byte[] header, byte status, byte error, byte specific, byte[] data) =>
    [
        type, .. EmulatedCcidReader.Le32((uint)data.Length),
        header.Length > 5 ? header[5] : (byte)0, header.Length > 6 ? header[6] : (byte)0, status, error, specific, .. data,
    ];

    private static byte Status(byte icc, bool failed) => (byte)((failed ? 0x40 : 0) | (icc & 0x03));

    /// <summary>RDR_to_PC_DataBlock (0x80).</summary>
    public static byte[] DataBlock(byte[] header, byte iccStatus, byte[] data, bool failed = false, byte error = 0) =>
        Message(0x80, header, Status(iccStatus, failed), failed ? error : (byte)0, 0, data);

    /// <summary>RDR_to_PC_SlotStatus (0x81).</summary>
    public static byte[] SlotStatus(byte[] header, byte iccStatus, bool failed = false, byte error = 0) =>
        Message(0x81, header, Status(iccStatus, failed), failed ? error : (byte)0, 0, []);

    /// <summary>RDR_to_PC_Parameters (0x82) per il protocollo annunciato dall'ATR.</summary>
    public static byte[] Parameters(byte[] header, byte iccStatus, byte[] atr) =>
        AtrProtocols(atr).Contains(1)
            // T=1: Fi/Di 0x11, LRC, guard time 0, BWI 4 / CWI 13, clock stop no, IFSC 254, NAD 0.
            ? Message(0x82, header, Status(iccStatus, false), 0, 1, [0x11, 0x10, 0x00, 0x4D, 0x00, 0xFE, 0x00])
            // T=0: Fi/Di 0x11, convenzione diretta, guard time 0, WI 10, clock stop no.
            : Message(0x82, header, Status(iccStatus, false), 0, 0, [0x11, 0x00, 0x00, 0x0A, 0x00]);

    /// <summary>RDR_to_PC_DataRateAndClockFrequency (0x84): 3,58 MHz, 9600 bps.</summary>
    public static byte[] DataRateAndClock(byte[] header, byte iccStatus) =>
        Message(0x84, header, Status(iccStatus, false), 0, 0, [.. EmulatedCcidReader.Le32(3580), .. EmulatedCcidReader.Le32(9600)]);

    /// <summary>Protocolli annunciati nei byte TDi dell'ATR (T=0 se nessuno).</summary>
    public static HashSet<int> AtrProtocols(byte[] atr)
    {
        var result = new HashSet<int>();
        if (atr.Length >= 2)
        {
            var index = 1;
            var y = atr[1] >> 4;
            while (index < atr.Length)
            {
                // Si saltano TAi, TBi, TCi; si legge TDi se c'è.
                foreach (var bit in new[] { 0x1, 0x2, 0x4 })
                {
                    if ((y & bit) != 0) index++;
                }
                if ((y & 0x8) == 0) break;
                index++;
                if (index >= atr.Length) break;
                result.Add(atr[index] & 0x0F);
                y = atr[index] >> 4;
            }
        }
        if (result.Count == 0) result.Add(0);
        return result;
    }
}

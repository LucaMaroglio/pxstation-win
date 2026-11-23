using System.Threading.Channels;
using SpiceKit.Codecs;
using SpiceKit.UsbRedirWire;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>Perché un dispositivo ha lasciato lo slot.</summary>
public enum UsbDetachReason
{
    UserRequest,
    GuestRejected,
    DeviceGone,
    ChannelClosed,
}

/// <summary>Eventi degli slot usbredir.</summary>
public abstract record UsbSlotEvent(int Slot)
{
    public sealed record Attached(int Slot, string Name) : UsbSlotEvent(Slot);

    public sealed record Detached(int Slot, UsbDetachReason Reason) : UsbSlotEvent(Slot);
}

public enum UsbAttachErrorKind
{
    SlotBusy,
    GuestNotReady,
    RejectedByGuestFilter,
    KeyboardOrMouse,

    /// <summary>Lo vieta il <c>usb-filter</c> del file di connessione (o <c>enable-usbredir=0</c>).</summary>
    BlockedByConnectionFile,
}

/// <summary>Un dispositivo non si è potuto collegare all'ospite.</summary>
public sealed class UsbAttachException(UsbAttachErrorKind kind) : Exception(kind switch
{
    UsbAttachErrorKind.SlotBusy => "Nessuno slot USB libero nella macchina virtuale.",
    UsbAttachErrorKind.GuestNotReady => "La macchina virtuale non ha risposto sullo slot USB.",
    UsbAttachErrorKind.RejectedByGuestFilter => "La macchina virtuale non accetta questo dispositivo.",
    UsbAttachErrorKind.KeyboardOrMouse => "Tastiere e mouse non si collegano alla macchina virtuale senza conferma.",
    _ => "Il file di connessione non permette di collegare questo dispositivo.",
})
{
    public UsbAttachErrorKind Kind { get; } = kind;
}

/// <summary>
/// Uno slot usbredir (porting di <c>USBRedirChannel.swift</c>): un canale spicevmc che porta il
/// protocollo usbredir, con questo client nel ruolo di usb-host (AGENTS.md macOS §7a). I
/// dispositivi si collegano e si scollegano sul canale aperto.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>I trasferimenti di uno stesso endpoint si eseguono in ordine, endpoint diversi insieme.</item>
/// <item>Limiti contro un ospite ostile: ≤ 4 MiB per trasferimento, ≤ 32 MiB e ≤ 1024 trasferimenti
///   in sospeso per slot, solo sugli endpoint che il dispositivo ha, lunghezze IN controllate prima di allocare.</item>
/// <item>Tastiere e mouse di avvio richiedono una conferma esplicita; l'uscita del canale rilascia subito il dispositivo.</item>
/// <item>Niente trasferimenti isocroni, bulk stream o bulk receiving (rifiutati con <c>inval</c>).</item>
/// </list>
/// </remarks>
public sealed class UsbRedirChannel
{
    /// <summary>Limite dei dati spicevmc compressi.</summary>
    internal const int MaxUncompressedSize = 64 * 1024 * 1024;

    private readonly ChannelConnection _connection;
    private readonly ChannelWriter<UsbSlotEvent> _events;
    private readonly UsbRedirCodec _codec = new();
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<bool> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<byte, Channel<Func<Task>>> _queues = [];
    private readonly Dictionary<byte, CancellationTokenSource> _pollers = [];
    private readonly Dictionary<byte, ulong> _interruptIds = [];
    private readonly HashSet<ulong> _cancelled = [];
    private readonly Dictionary<ulong, (byte Endpoint, int Bytes)> _inFlight = [];
    private Dictionary<byte, UsbTransferType> _endpointTypes = [];
    private UsbFilter? _guestFilter;
    private IUsbRedirDevice? _device;
    private string _deviceName = "";
    private bool _attaching;
    private bool _closed;
    private int _outstandingBytes;
    private TaskCompletionSource? _disconnectAck;

    public UsbRedirChannel(int slot, ChannelConnection connection, ChannelWriter<UsbSlotEvent> events)
    {
        Slot = slot;
        _connection = connection;
        _events = events;
    }

    public static CapabilitySet Capabilities { get; } = CapabilitySet.FromBits((int)SpiceVmcCapability.DataCompressLz4);

    public int Slot { get; }

    public bool IsFree
    {
        get
        {
            lock (_gate) return _device is null && !_attaching && !_closed;
        }
    }

    public string? AttachedName
    {
        get
        {
            lock (_gate) return _device is null ? null : _deviceName;
        }
    }

    /// <summary>Byte prenotati dai trasferimenti in sospeso (test).</summary>
    internal int OutstandingBytes
    {
        get
        {
            lock (_gate) return _outstandingBytes;
        }
    }

    // MARK: ciclo del canale

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SendRawAsync(_codec.EncodeHello()).ConfigureAwait(false);
            while (true)
            {
                var message = await _connection.NextMessageAsync(cancellationToken).ConfigureAwait(false);
                byte[] bytes;
                switch ((SpiceVmcMessage)message.Type)
                {
                    case SpiceVmcMessage.Data:
                        bytes = message.Payload;
                        break;
                    case SpiceVmcMessage.CompressedData:
                        bytes = Decompress(message.Payload);
                        break;
                    default:
                        continue;
                }
                List<UsbRedirIncoming> packets;
                try
                {
                    packets = _codec.Feed(bytes);
                }
                catch (UsbRedirException error)
                {
                    throw ChannelException.Malformed($"usbredir slot {Slot}: {error.Message}");
                }
                foreach (var packet in packets) await HandleAsync(packet).ConfigureAwait(false);
            }
        }
        finally
        {
            // Qualunque uscita (fine, errore, dati non validi) rilascia subito il dispositivo.
            lock (_gate) _closed = true;
            _hello.TrySetResult(false);
            await DetachLocallyAsync(UsbDetachReason.ChannelClosed).ConfigureAwait(false);
        }
    }

    internal static byte[] Decompress(byte[] payload)
    {
        var r = new ByteReader(payload);
        var type = (DataCompressionType)r.ReadU8();
        if (type != DataCompressionType.Lz4) return r.ReadRemaining().ToArray();
        var size = r.ReadU32();
        if (size > MaxUncompressedSize) throw WireException.LimitExceeded($"spicevmc {size}");
        try
        {
            return Lz4Decoder.Decompress(r.ReadRemaining(), (int)size);
        }
        catch (CodecException error)
        {
            throw ChannelException.Malformed("spicevmc LZ4: " + error.Message);
        }
    }

    private Task SendRawAsync(byte[] bytes) => _connection.SendAsync(new SpiceMessage((ushort)SpiceVmcMessage.Data, bytes));

    private async Task SendAsync(byte[] bytes)
    {
        try
        {
            await SendRawAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception error) when (error is Transport.TransportException or ObjectDisposedException or OperationCanceledException)
        {
            SpiceLog.Debug("usbredir", $"slot {Slot}: invio non riuscito ({error.Message})");
        }
    }

    // MARK: collegamento e scollegamento

    /// <summary>
    /// Mostra <paramref name="device"/> all'ospite su questo slot. Tastiere e mouse di avvio (anche
    /// le chiavi di sicurezza come YubiKey, che "digitano" codici) richiedono <paramref name="allowInputDevice"/>.
    /// </summary>
    /// <exception cref="UsbAttachException">Slot occupato, ospite che non risponde o dispositivo rifiutato.</exception>
    public async Task AttachAsync(IUsbRedirDevice device, bool allowInputDevice = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            // Prenotazione prima di qualunque attesa: un secondo collegamento trova lo slot occupato.
            if (_device is not null || _attaching || _closed) throw new UsbAttachException(UsbAttachErrorKind.SlotBusy);
            _attaching = true;
        }
        try
        {
            // L'ospite (usb-redir di QEMU) saluta all'apertura dello slot: non si aspetta per sempre.
            var ready = await _hello.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!ready) throw new UsbAttachException(UsbAttachErrorKind.GuestNotReady);
            var description = await device.DescribeAsync().ConfigureAwait(false);
            if (description.IsBootKeyboardOrMouse && !allowInputDevice) throw new UsbAttachException(UsbAttachErrorKind.KeyboardOrMouse);
            byte[] endpointInfo, interfaceInfo, connect;
            lock (_gate)
            {
                if (_guestFilter is { } filter && !filter.Allows(description.FilterDevice))
                {
                    throw new UsbAttachException(UsbAttachErrorKind.RejectedByGuestFilter);
                }
                if (_closed) throw new UsbAttachException(UsbAttachErrorKind.GuestNotReady);
                _device = device;
                _deviceName = device.DisplayName;
                RememberEndpoints(description);
                _cancelled.Clear();
                // Ordine richiesto dal protocollo: ep_info, interface_info, poi device_connect.
                endpointInfo = _codec.EncodeEndpointInfo(description.Endpoints);
                interfaceInfo = _codec.EncodeInterfaceInfo(description.Interfaces);
                connect = _codec.EncodeDeviceConnect(description);
            }
            await SendAsync(endpointInfo).ConfigureAwait(false);
            await SendAsync(interfaceInfo).ConfigureAwait(false);
            await SendAsync(connect).ConfigureAwait(false);
            _events.TryWrite(new UsbSlotEvent.Attached(Slot, device.DisplayName));
            SpiceLog.Info("usbredir", $"slot {Slot}: dispositivo collegato (classe {description.DeviceClass})");
        }
        catch (TimeoutException)
        {
            throw new UsbAttachException(UsbAttachErrorKind.GuestNotReady);
        }
        finally
        {
            lock (_gate) _attaching = false;
        }
    }

    /// <summary>Toglie il dispositivo all'ospite e lo rilascia.</summary>
    public async Task DetachAsync()
    {
        IUsbRedirDevice? detaching;
        TaskCompletionSource? ack = null;
        byte[] disconnect;
        lock (_gate)
        {
            detaching = _device;
            if (detaching is null) return;
            disconnect = _codec.EncodeDeviceDisconnect();
            if (_codec.Negotiated(UsbRedirCapability.DeviceDisconnectAck))
            {
                ack = _disconnectAck ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        await SendAsync(disconnect).ConfigureAwait(false);
        if (ack is not null)
        {
            // Breve attesa che l'ospite smetta di usare il dispositivo.
            await Task.WhenAny(ack.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            lock (_gate)
            {
                // Nel frattempo l'ospite può averlo rifiutato e un altro dispositivo essere stato collegato.
                if (!ReferenceEquals(_device, detaching)) return;
            }
        }
        await DetachLocallyAsync(UsbDetachReason.UserRequest).ConfigureAwait(false);
    }

    private async Task DetachLocallyAsync(UsbDetachReason reason)
    {
        IUsbRedirDevice? current;
        byte[] pollerEndpoints;
        lock (_gate)
        {
            current = _device;
            if (current is null) return;
            _device = null;
            _disconnectAck?.TrySetResult();
            _disconnectAck = null;
            _endpointTypes = [];
            _outstandingBytes = 0;
            pollerEndpoints = [.. _pollers.Keys];
            foreach (var poller in _pollers.Values) poller.Cancel();
            _pollers.Clear();
            foreach (var queue in _queues.Values) queue.Writer.TryComplete();
            _queues.Clear();
            _inFlight.Clear();
        }
        foreach (var endpoint in pollerEndpoints) await current.AbortAsync(endpoint).ConfigureAwait(false);
        await current.CloseAsync().ConfigureAwait(false);
        _events.TryWrite(new UsbSlotEvent.Detached(Slot, reason));
        SpiceLog.Info("usbredir", $"slot {Slot}: dispositivo scollegato ({reason})");
    }

    // MARK: pacchetti

    private IUsbRedirDevice? Device
    {
        get
        {
            lock (_gate) return _device;
        }
    }

    private byte[] Encode(Func<UsbRedirCodec, byte[]> build)
    {
        lock (_gate) return build(_codec);
    }

    internal async Task HandleAsync(UsbRedirIncoming packet)
    {
        switch (packet)
        {
            case UsbRedirIncoming.Hello hello:
                SpiceLog.Debug("usbredir", $"slot {Slot}: hello dell'ospite ({WireText.Sanitize(hello.Version)})");
                _hello.TrySetResult(true);
                break;
            case UsbRedirIncoming.FilterFilter filter:
                try
                {
                    var parsed = UsbFilter.Parse(filter.Rules);
                    lock (_gate) _guestFilter = parsed;
                }
                catch (FormatException)
                {
                    // Filtro illeggibile dell'ospite: resta quello precedente.
                }
                break;
            case UsbRedirIncoming.FilterReject:
                await SendAsync(Encode(c => c.EncodeDeviceDisconnect())).ConfigureAwait(false);
                await DetachLocallyAsync(UsbDetachReason.GuestRejected).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.DeviceDisconnectAck:
                lock (_gate)
                {
                    _disconnectAck?.TrySetResult();
                    _disconnectAck = null;
                }
                break;
            case UsbRedirIncoming.Reset:
                if (Device is { } resetting && !await resetting.ResetAsync().ConfigureAwait(false))
                {
                    await SendAsync(Encode(c => c.EncodeDeviceDisconnect())).ConfigureAwait(false);
                    await DetachLocallyAsync(UsbDetachReason.DeviceGone).ConfigureAwait(false);
                }
                break;
            case UsbRedirIncoming.SetConfiguration set:
                if (Device is not { } configuring)
                {
                    await SendAsync(Encode(c => c.EncodeConfigurationStatus(set.Id, UsbRedirStatus.IoError, 0))).ConfigureAwait(false);
                    break;
                }
                var configStatus = await configuring.SetConfigurationAsync(set.Configuration).ConfigureAwait(false);
                if (configStatus == UsbRedirStatus.Success) await SendDescriptionUpdateAsync(configuring).ConfigureAwait(false);
                var configuration = await configuring.GetConfigurationAsync().ConfigureAwait(false);
                await SendAsync(Encode(c => c.EncodeConfigurationStatus(set.Id, configStatus, configuration))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.GetConfiguration get:
                var current = Device is { } device ? await device.GetConfigurationAsync().ConfigureAwait(false) : (byte?)null;
                await SendAsync(Encode(c => c.EncodeConfigurationStatus(get.Id, current is null ? UsbRedirStatus.IoError : UsbRedirStatus.Success,
                    current ?? 0))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.SetAltSetting set:
                if (Device is not { } altering)
                {
                    await SendAsync(Encode(c => c.EncodeAltSettingStatus(set.Id, UsbRedirStatus.IoError, set.Interface, 0))).ConfigureAwait(false);
                    break;
                }
                var altStatus = await altering.SetAltSettingAsync(set.Interface, set.Alt).ConfigureAwait(false);
                if (altStatus == UsbRedirStatus.Success) await SendDescriptionUpdateAsync(altering).ConfigureAwait(false);
                var alt = await altering.GetAltSettingAsync(set.Interface).ConfigureAwait(false) ?? 0;
                await SendAsync(Encode(c => c.EncodeAltSettingStatus(set.Id, altStatus, set.Interface, alt))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.GetAltSetting get:
                var currentAlt = Device is { } queried ? await queried.GetAltSettingAsync(get.Interface).ConfigureAwait(false) : null;
                await SendAsync(Encode(c => c.EncodeAltSettingStatus(get.Id, currentAlt is null ? UsbRedirStatus.Inval : UsbRedirStatus.Success,
                    get.Interface, currentAlt ?? 0))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.StartIso iso:
                // Trasferimenti isocroni non supportati (webcam e audio USB non funzionano ancora).
                await SendAsync(Encode(c => c.EncodeIsoStreamStatus(iso.Id, UsbRedirStatus.Inval, iso.Endpoint))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.StopIso iso:
                await SendAsync(Encode(c => c.EncodeIsoStreamStatus(iso.Id, UsbRedirStatus.Inval, iso.Endpoint))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.StartInterruptReceiving start:
                await StartInterruptReceivingAsync(start.Id, start.Endpoint).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.StopInterruptReceiving stop:
                CancellationTokenSource? poller;
                lock (_gate) _pollers.Remove(stop.Endpoint, out poller);
                poller?.Cancel();
                // Annullare il compito non basta per una richiesta già sospesa sul dispositivo: la si interrompe.
                if (Device is { } stopping) await stopping.AbortAsync(stop.Endpoint).ConfigureAwait(false);
                await SendAsync(Encode(c => c.EncodeInterruptReceivingStatus(stop.Id, UsbRedirStatus.Success, stop.Endpoint))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.AllocBulkStreams alloc:
                await SendAsync(Encode(c => c.EncodeBulkStreamsStatus(alloc.Id, UsbRedirStatus.Inval, alloc.Endpoints, 0))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.FreeBulkStreams free:
                await SendAsync(Encode(c => c.EncodeBulkStreamsStatus(free.Id, UsbRedirStatus.Inval, free.Endpoints, 0))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.StartBulkReceiving start:
                await SendAsync(Encode(c => c.EncodeBulkReceivingStatus(start.Id, UsbRedirStatus.Inval, start.Endpoint, start.StreamId))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.StopBulkReceiving stop:
                await SendAsync(Encode(c => c.EncodeBulkReceivingStatus(stop.Id, UsbRedirStatus.Inval, stop.Endpoint, stop.StreamId))).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.CancelDataPacket cancel:
                // Solo id davvero in corso: l'ospite non può far crescere l'insieme a piacere.
                byte? cancelEndpoint = null;
                lock (_gate)
                {
                    if (_inFlight.TryGetValue(cancel.Id, out var entry))
                    {
                        _cancelled.Add(cancel.Id);
                        cancelEndpoint = entry.Endpoint;
                    }
                }
                if (cancelEndpoint is { } ep && Device is { } aborting) await aborting.AbortAsync(ep).ConfigureAwait(false);
                break;
            case UsbRedirIncoming.Control control:
                HandleControl(control);
                break;
            case UsbRedirIncoming.Bulk bulk:
                HandleBulk(bulk);
                break;
            case UsbRedirIncoming.Interrupt interrupt:
                HandleInterruptOut(interrupt);
                break;
            case UsbRedirIncoming.Unknown unknown:
                SpiceLog.Debug("usbredir", $"slot {Slot}: pacchetto usbredir {unknown.Type} ignorato");
                break;
        }
    }

    private void HandleControl(UsbRedirIncoming.Control control)
    {
        var bytes = control.Setup.Length + control.Data.Length;
        if (!TryReserve(bytes, control.Id, 0, requireType: null, out var device))
        {
            _ = SendAsync(Encode(c => c.EncodeControlReply(control.Id, control.Endpoint, control.Setup, UsbRedirStatus.Inval, 0, [])));
            return;
        }
        Enqueue(0, async () =>
        {
            var result = await device.ControlAsync(control.Setup, control.Data).ConfigureAwait(false);
            await CompleteAsync(control.Id, device, (codec, status) => codec.EncodeControlReply(control.Id, control.Endpoint, control.Setup,
                status ?? result.Status, status is null ? result.ActualLength : 0, result.Data)).ConfigureAwait(false);
        });
    }

    private void HandleBulk(UsbRedirIncoming.Bulk bulk)
    {
        var header = bulk.Header;
        var isIn = (header.Endpoint & 0x80) != 0;
        // La lunghezza IN dichiarata la decide l'ospite: si controlla prima di allocare qualunque cosa.
        var bytes = isIn ? (long)header.Length : bulk.Data.Length;
        if (bytes > UsbTransferLimits.MaxTransferLength
            || !TryReserve((int)bytes, bulk.Id, header.Endpoint, UsbTransferType.Bulk, out var device))
        {
            _ = SendAsync(Encode(c => c.EncodeBulkReply(bulk.Id, header.Endpoint, header.StreamId, UsbRedirStatus.Inval, 0, [])));
            return;
        }
        Enqueue(header.Endpoint, async () =>
        {
            var result = await device.BulkAsync(header.Endpoint, isIn ? [] : bulk.Data, isIn ? (int)header.Length : bulk.Data.Length)
                .ConfigureAwait(false);
            await CompleteAsync(bulk.Id, device, (codec, status) => codec.EncodeBulkReply(bulk.Id, header.Endpoint, header.StreamId,
                status ?? result.Status, status is null ? result.ActualLength : 0, result.Data)).ConfigureAwait(false);
        });
    }

    private void HandleInterruptOut(UsbRedirIncoming.Interrupt interrupt)
    {
        var header = interrupt.Header;
        // Come pacchetto arriva solo l'interrupt OUT; l'IN usa start_interrupt_receiving.
        if ((header.Endpoint & 0x80) != 0
            || !TryReserve(interrupt.Data.Length, interrupt.Id, header.Endpoint, UsbTransferType.Interrupt, out var device))
        {
            _ = SendAsync(Encode(c => c.EncodeInterrupt(interrupt.Id, header.Endpoint, UsbRedirStatus.Inval, 0, [])));
            return;
        }
        Enqueue(header.Endpoint, async () =>
        {
            var result = await device.InterruptAsync(header.Endpoint, interrupt.Data, interrupt.Data.Length).ConfigureAwait(false);
            await CompleteAsync(interrupt.Id, device, (codec, status) => codec.EncodeInterrupt(interrupt.Id, header.Endpoint,
                status ?? result.Status, status is null ? result.ActualLength : 0, [])).ConfigureAwait(false);
        });
    }

    /// <summary>Prenota il budget di un trasferimento; <see langword="false"/> se troppo grande, slot saturo o endpoint inesistente.</summary>
    private bool TryReserve(int bytes, ulong id, byte endpoint, UsbTransferType? requireType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IUsbRedirDevice? device)
    {
        lock (_gate)
        {
            device = _device;
            if (device is null) return false;
            if (requireType is { } type && (!_endpointTypes.TryGetValue(endpoint, out var actual) || actual != type)) return false;
            // Un id riusato mentre è in corso renderebbe orfana la prima prenotazione.
            if (_inFlight.ContainsKey(id) || bytes < 0 || bytes > UsbTransferLimits.MaxTransferLength
                || _inFlight.Count >= UsbTransferLimits.MaxOutstandingPackets
                || _outstandingBytes + bytes > UsbTransferLimits.MaxOutstandingBytes)
            {
                SpiceLog.Info("usbredir", $"slot {Slot}: trasferimento di {bytes} byte rifiutato (limiti)");
                return false;
            }
            _outstandingBytes += bytes;
            _inFlight[id] = (endpoint, bytes);
            return true;
        }
    }

    /// <summary>Esegue in ordine i trasferimenti di un endpoint; endpoint diversi procedono insieme.</summary>
    private void Enqueue(byte endpoint, Func<Task> work)
    {
        Channel<Func<Task>> queue;
        lock (_gate)
        {
            if (!_queues.TryGetValue(endpoint, out var existing))
            {
                existing = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
                _queues[endpoint] = existing;
                var reader = existing.Reader;
                _ = Task.Run(async () =>
                {
                    await foreach (var job in reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        try
                        {
                            await job().ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        {
                            SpiceLog.Info("usbredir", $"slot {Slot}: trasferimento non riuscito ({error.Message})");
                        }
                    }
                });
            }
            queue = existing;
        }
        queue.Writer.TryWrite(work);
    }

    /// <summary>Risposta a un pacchetto dati finito (come annullato se l'ospite lo ha annullato).</summary>
    private async Task CompleteAsync(ulong id, IUsbRedirDevice device, Func<UsbRedirCodec, UsbRedirStatus?, byte[]> build)
    {
        byte[] reply;
        lock (_gate)
        {
            if (_inFlight.Remove(id, out var entry)) _outstandingBytes = Math.Max(0, _outstandingBytes - entry.Bytes);
            // Dispositivo scollegato o sostituito nel frattempo: nessuna risposta.
            if (!ReferenceEquals(_device, device)) return;
            reply = build(_codec, _cancelled.Remove(id) ? UsbRedirStatus.Cancelled : null);
        }
        await SendAsync(reply).ConfigureAwait(false);
    }

    private async Task SendDescriptionUpdateAsync(IUsbRedirDevice device)
    {
        var description = await device.DescribeAsync().ConfigureAwait(false);
        byte[] endpointInfo, interfaceInfo;
        lock (_gate)
        {
            RememberEndpoints(description);
            endpointInfo = _codec.EncodeEndpointInfo(description.Endpoints);
            interfaceInfo = _codec.EncodeInterfaceInfo(description.Interfaces);
        }
        await SendAsync(endpointInfo).ConfigureAwait(false);
        await SendAsync(interfaceInfo).ConfigureAwait(false);
    }

    private void RememberEndpoints(UsbDeviceDescription description)
    {
        var types = new Dictionary<byte, UsbTransferType>();
        foreach (var endpoint in description.Endpoints) types.TryAdd(endpoint.Address, endpoint.Type);
        _endpointTypes = types;
    }

    // MARK: ricezione interrupt (IN)

    private async Task StartInterruptReceivingAsync(ulong id, byte endpoint)
    {
        IUsbRedirDevice? device;
        bool valid, running;
        lock (_gate)
        {
            device = _device;
            valid = device is not null && (endpoint & 0x80) != 0
                && _endpointTypes.TryGetValue(endpoint, out var type) && type == UsbTransferType.Interrupt;
            // Idempotente: un secondo avvio non deve accumulare interrogazioni (ognuna lascerebbe una richiesta sospesa).
            running = _pollers.ContainsKey(endpoint);
        }
        if (!valid || device is null)
        {
            await SendAsync(Encode(c => c.EncodeInterruptReceivingStatus(id, UsbRedirStatus.Inval, endpoint))).ConfigureAwait(false);
            return;
        }
        if (!running)
        {
            var description = await device.DescribeAsync().ConfigureAwait(false);
            var maxPacket = description.Endpoints.FirstOrDefault(e => e.Address == endpoint).MaxPacketSize is var size and > 0 ? size : (ushort)64;
            CancellationTokenSource? poller = null;
            lock (_gate)
            {
                if (!_pollers.ContainsKey(endpoint) && ReferenceEquals(_device, device))
                {
                    poller = new CancellationTokenSource();
                    _pollers[endpoint] = poller;
                    _interruptIds[endpoint] = 0;
                }
            }
            if (poller is not null) _ = Task.Run(() => PollInterruptsAsync(device, endpoint, maxPacket, poller));
        }
        await SendAsync(Encode(c => c.EncodeInterruptReceivingStatus(id, UsbRedirStatus.Success, endpoint))).ConfigureAwait(false);
    }

    private async Task PollInterruptsAsync(IUsbRedirDevice device, byte endpoint, int maxPacket, CancellationTokenSource poller)
    {
        try
        {
            while (!poller.IsCancellationRequested)
            {
                var result = await device.InterruptAsync(endpoint, [], maxPacket).ConfigureAwait(false);
                if (poller.IsCancellationRequested || result.Status == UsbRedirStatus.Cancelled) return;
                byte[] message;
                lock (_gate)
                {
                    if (!ReferenceEquals(_device, device)) return;
                    var packetId = _interruptIds.GetValueOrDefault(endpoint);
                    _interruptIds[endpoint] = packetId + 1;
                    // Un flusso fermato si segnala sempre come stallo.
                    message = result.Status == UsbRedirStatus.Success
                        ? _codec.EncodeInterrupt(packetId, endpoint, UsbRedirStatus.Success, result.Data.Length, result.Data)
                        : _codec.EncodeInterruptReceivingStatus(0, UsbRedirStatus.Stall, endpoint);
                }
                await SendAsync(message).ConfigureAwait(false);
                if (result.Status != UsbRedirStatus.Success) return;
            }
        }
        finally
        {
            // Un'interrogazione finita da sola libera l'endpoint per un nuovo avvio.
            lock (_gate)
            {
                if (_pollers.TryGetValue(endpoint, out var registered) && ReferenceEquals(registered, poller)) _pollers.Remove(endpoint);
            }
        }
    }
}

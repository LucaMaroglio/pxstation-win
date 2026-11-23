using System.Threading.Channels;
using SpiceKit.Channels;
using SpiceKit.UsbRedirWire;
using SpiceKit.VVFile;

namespace SpiceKit.Session;

/// <summary>Dispositivi della sessione (M8): slot USB (CD-ROM virtuale, lettore di smartcard, USB reale) e smartcard.</summary>
public sealed partial class SpiceSession
{
    /// <summary>Slot usbredir collegati al massimo (l'elenco dei canali lo decide il server: limite contro server ostili).</summary>
    internal const int MaxUsbSlots = 16;

    /// <summary>Classe USB dei lettori di smartcard.</summary>
    private const byte SmartcardClass = 0x0B;

    private readonly ConnectionProfile _profile;
    private readonly List<UsbRedirChannel> _usbSlots = [];
    private readonly Channel<UsbSlotEvent> _usb = Channel.CreateBounded<UsbSlotEvent>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<SmartcardEvent> _smartcard = Channel.CreateBounded<SmartcardEvent>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private SmartcardChannel? _smartcardChannel;

    /// <summary>Il file di connessione permette la condivisione USB (e quindi anche le ISO): <c>enable-usbredir</c> non è 0.</summary>
    public bool UsbAllowed => _profile.EnableUsbRedirection != false;

    /// <summary>Il file di connessione permette le smartcard: <c>enable-smartcard</c> non è 0.</summary>
    public bool SmartcardAllowed => _profile.EnableSmartcard != false;

    /// <summary>Cambi degli slot USB; si completa alla fine della sessione.</summary>
    public ChannelReader<UsbSlotEvent> UsbEvents => _usb.Reader;

    /// <summary>Carta inserita o tolta dal lettore del canale smartcard; si completa alla fine della sessione.</summary>
    public ChannelReader<SmartcardEvent> SmartcardEvents => _smartcard.Reader;

    /// <summary>Il server ha un canale smartcard (QEMU con <c>ccid-card-passthru</c>).</summary>
    public bool HasSmartcardChannel => Volatile.Read(ref _smartcardChannel) is not null;

    /// <summary>Slot USB dell'ospite e nome del dispositivo collegato (<see langword="null"/> = libero), in ordine.</summary>
    public IReadOnlyList<(int Slot, string? Device)> UsbSlotStates()
    {
        lock (_lock) return [.. _usbSlots.OrderBy(s => s.Slot).Select(s => (s.Slot, s.AttachedName))];
    }

    /// <summary>
    /// Collega un dispositivo al primo slot libero; restituisce il numero dello slot. Le regole del file
    /// di connessione valgono qui per ogni dispositivo, reale o emulato (ISO comprese): nessuna parte
    /// dell'interfaccia può aggirarle.
    /// </summary>
    /// <exception cref="UsbAttachException">Vietato dal file di connessione, nessuno slot libero o rifiutato dall'ospite.</exception>
    public async Task<int> AttachUsbAsync(IUsbRedirDevice device, bool allowInputDevice = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!UsbAllowed) throw new UsbAttachException(UsbAttachErrorKind.BlockedByConnectionFile);
        var description = await device.DescribeAsync().ConfigureAwait(false);
        if (_profile.UsbFilter is { } filter && !filter.Allows(description.FilterDevice))
        {
            throw new UsbAttachException(UsbAttachErrorKind.BlockedByConnectionFile);
        }
        // I lettori di smartcard (anche quello emulato) seguono enable-smartcard.
        if (!SmartcardAllowed && description.FilterDevice.Classes.Contains(SmartcardClass))
        {
            throw new UsbAttachException(UsbAttachErrorKind.BlockedByConnectionFile);
        }
        UsbRedirChannel[] slots;
        lock (_lock) slots = [.. _usbSlots.OrderBy(s => s.Slot)];
        foreach (var slot in slots.Where(s => s.IsFree))
        {
            try
            {
                await slot.AttachAsync(device, allowInputDevice).ConfigureAwait(false);
                return slot.Slot;
            }
            catch (UsbAttachException error) when (error.Kind == UsbAttachErrorKind.SlotBusy)
            {
                // Occupato nel frattempo: si prova il successivo.
            }
        }
        throw new UsbAttachException(UsbAttachErrorKind.SlotBusy);
    }

    /// <summary>Scollega il dispositivo di uno slot.</summary>
    public Task DetachUsbAsync(int slot)
    {
        UsbRedirChannel? channel;
        lock (_lock) channel = _usbSlots.FirstOrDefault(s => s.Slot == slot);
        return channel?.DetachAsync() ?? Task.CompletedTask;
    }

    /// <summary>Condivide una smartcard del PC con il lettore virtuale dell'ospite (canale smartcard).</summary>
    /// <exception cref="SmartcardException">Nessun canale smartcard, lettore non accettato o carta già condivisa.</exception>
    public Task InsertSmartcardAsync(ISmartcard card)
    {
        if (!SmartcardAllowed || Volatile.Read(ref _smartcardChannel) is not { } channel)
        {
            throw new SmartcardException(SmartcardErrorKind.ReaderNotAccepted);
        }
        return channel.InsertAsync(card);
    }

    /// <summary>Toglie la smartcard all'ospite.</summary>
    public Task RemoveSmartcardAsync() => Volatile.Read(ref _smartcardChannel)?.RemoveAsync() ?? Task.CompletedTask;
}

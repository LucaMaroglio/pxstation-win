using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpiceKit.Channels;
using SpiceKit.Session;
using SpiceKit.UsbEmulation;
using SpiceKit.Windows.Smartcard;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>
/// Dispositivi nella finestra di sessione (M8): un'immagine ISO come CD-ROM USB e una smartcard del
/// PC, attraverso il canale smartcard se la VM lo ha, altrimenti con il lettore CCID virtuale su uno
/// slot USB (Proxmox). Le regole del file di connessione le applica la sessione per ogni dispositivo.
/// Dopo una riconnessione ISO e smartcard si ricollegano da sole.
/// </summary>
public sealed partial class SessionWindow
{
    private (string Path, string Name, int Slot)? _iso;
    private (string Reader, int? Slot)? _sharedCard;
    private string? _restoreIso;
    private string? _restoreCard;

    /// <summary>Slot USB e canale smartcard dell'ospite (per l'autoverifica).</summary>
    internal (int UsbSlots, bool SmartcardChannel) DeviceSupport =>
        _session is { } session ? (session.UsbSlotStates().Count, session.HasSmartcardChannel) : (0, false);

    private async Task RunDevicesAsync(SpiceSession session)
    {
        var usb = RunUsbEventsAsync(session);
        await foreach (var value in session.SmartcardEvents.ReadAllAsync())
        {
            if (!ReferenceEquals(session, _session)) break;
            if (value is SmartcardEvent.Removed && _sharedCard is { Slot: null }) _sharedCard = null;
        }
        await usb;
    }

    private async Task RunUsbEventsAsync(SpiceSession session)
    {
        await foreach (var value in session.UsbEvents.ReadAllAsync())
        {
            if (!ReferenceEquals(session, _session)) return;
            // Canale chiuso (sessione finita): ISO e smartcard restano da ricollegare dopo una riconnessione.
            if (value is not UsbSlotEvent.Detached detached || detached.Reason == UsbDetachReason.ChannelClosed) continue;
            if (_iso is { } iso && iso.Slot == detached.Slot)
            {
                _iso = null;
                if (detached.Reason is UsbDetachReason.GuestRejected or UsbDetachReason.DeviceGone)
                {
                    ShowNotice(Strings.Get("DevicesTitle"), InfoBarSeverity.Informational, Strings.Format("IsoEjected", iso.Name));
                }
            }
            if (_sharedCard is { Slot: { } slot } && slot == detached.Slot) _sharedCard = null;
        }
    }

    /// <summary>Dopo una riconnessione rimette ISO e smartcard che l'utente aveva condiviso.</summary>
    private async Task RestoreDevicesAsync()
    {
        if (_restoreIso is { } iso)
        {
            _restoreIso = null;
            await MountIsoAsync(iso);
        }
        if (_restoreCard is { } reader)
        {
            _restoreCard = null;
            await ShareSmartcardAsync(new WindowsSmartcard(reader));
        }
    }

    /// <summary>Prima di una riconnessione: che cosa ricollegare (la sessione vecchia rilascia tutto da sola).</summary>
    private void RememberDevicesForReconnect()
    {
        _restoreIso ??= _iso?.Path;
        _restoreCard ??= _sharedCard?.Reader;
        _iso = null;
        _sharedCard = null;
    }

    // MARK: menu

    private void OnSessionMenuOpening(object? sender, object e)
    {
        var live = _session is { State.Kind: SessionStateKind.Connected } connected ? connected : null;
        var freeSlot = live?.UsbSlotStates().Any(s => s.Device is null) == true;
        MountIsoItem.Visibility = _iso is null ? Visibility.Visible : Visibility.Collapsed;
        MountIsoItem.IsEnabled = live is { UsbAllowed: true } && freeSlot;
        EjectIsoItem.Visibility = _iso is null ? Visibility.Collapsed : Visibility.Visible;
        if (_iso is { } iso) EjectIsoItem.Text = Strings.Format("SessionEjectIso", iso.Name);

        SmartcardMenu.Items.Clear();
        SmartcardMenu.IsEnabled = _sharedCard is not null
                                  || (live is { SmartcardAllowed: true } && (live.HasSmartcardChannel || (live.UsbAllowed && freeSlot)));
        if (_sharedCard is { } card)
        {
            var remove = new MenuFlyoutItem { Text = Strings.Format("SmartcardRemove", card.Reader) };
            remove.Click += async (_, _) => await RemoveSmartcardAsync();
            SmartcardMenu.Items.Add(remove);
            return;
        }
        var readers = WindowsSmartcard.Readers();
        if (readers.Count == 0)
        {
            SmartcardMenu.Items.Add(new MenuFlyoutItem { Text = Strings.Get("SmartcardNoReaders"), IsEnabled = false });
            return;
        }
        foreach (var reader in readers)
        {
            var item = new MenuFlyoutItem
            {
                Text = reader.HasCard ? reader.Name : Strings.Format("SmartcardNoCard", reader.Name),
                IsEnabled = reader.HasCard,
            };
            var name = reader.Name;
            item.Click += async (_, _) => await ShareSmartcardAsync(new WindowsSmartcard(name));
            SmartcardMenu.Items.Add(item);
        }
    }

    private async void OnMountIso(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(".iso");
        picker.FileTypeFilter.Add(".img");
        if (await picker.PickSingleFileAsync() is { } file) await MountIsoAsync(file.Path);
    }

    private async void OnEjectIso(object sender, RoutedEventArgs e) => await EjectIsoAsync();

    // MARK: ISO

    /// <summary>Monta un'immagine come CD-ROM USB nell'ospite (anche per l'autoverifica).</summary>
    internal async Task<bool> MountIsoAsync(string path)
    {
        var name = Path.GetFileName(path);
        if (_session is not { } session || _iso is not null) return false;
        FileBlockSource source;
        try
        {
            source = new FileBlockSource(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Strings.Get("DevicesTitle"), InfoBarSeverity.Error, Strings.Format("IsoFailed", name, error.Message));
            return false;
        }
        var drive = new EmulatedCdrom(source, name, onEject: () => DispatcherQueue.TryEnqueue(() => _ = EjectIsoAsync()));
        try
        {
            var slot = await session.AttachUsbAsync(drive);
            _iso = (path, name, slot);
            AppLog.Info($"ISO montata nello slot USB {slot}");
            ShowNotice(Strings.Get("DevicesTitle"), InfoBarSeverity.Success, Strings.Format("IsoMounted", name));
            return true;
        }
        catch (UsbAttachException error)
        {
            await drive.CloseAsync();
            ShowNotice(Strings.Get("DevicesTitle"), InfoBarSeverity.Error, Strings.Format("IsoFailed", name, error.Message));
            return false;
        }
    }

    private async Task EjectIsoAsync()
    {
        if (_iso is not { } iso || _session is not { } session) return;
        _iso = null;
        await session.DetachUsbAsync(iso.Slot);
    }

    // MARK: smartcard

    /// <summary>Condivide una smartcard: canale smartcard se c'è, altrimenti lettore CCID virtuale su uno slot USB.</summary>
    internal async Task<bool> ShareSmartcardAsync(ISmartcard card)
    {
        if (_session is not { } session || _sharedCard is not null) return false;
        try
        {
            if (session.HasSmartcardChannel)
            {
                await session.InsertSmartcardAsync(card);
                _sharedCard = (card.DisplayName, null);
            }
            else
            {
                var slot = await session.AttachUsbAsync(new EmulatedCcidReader(card));
                _sharedCard = (card.DisplayName, slot);
            }
            AppLog.Info($"Smartcard condivisa ({(_sharedCard.Value.Slot is null ? "canale smartcard" : "lettore CCID virtuale")})");
            ShowNotice(Strings.Get("DevicesTitle"), InfoBarSeverity.Success, Strings.Format("SmartcardShared", card.DisplayName));
            return true;
        }
        catch (Exception error) when (error is SmartcardException or UsbAttachException or IOException)
        {
            await card.DisconnectAsync();
            ShowNotice(Strings.Get("DevicesTitle"), InfoBarSeverity.Error, Strings.Format("SmartcardFailed", SmartcardErrors.Describe(error)));
            return false;
        }
    }

    private async Task RemoveSmartcardAsync()
    {
        if (_sharedCard is not { } card || _session is not { } session) return;
        _sharedCard = null;
        if (card.Slot is { } slot) await session.DetachUsbAsync(slot);
        else await session.RemoveSmartcardAsync();
        ShowNotice(Strings.Get("DevicesTitle"), InfoBarSeverity.Informational, Strings.Get("SmartcardRemoved"));
    }
}

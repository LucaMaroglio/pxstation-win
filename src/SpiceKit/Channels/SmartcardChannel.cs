using System.Threading.Channels;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>
/// Smartcard del PC condivisa con l'ospite (passthrough: gli APDU dell'ospite arrivano alla carta
/// vera senza modifiche). La implementa SpiceKit.Windows con WinSCard.
/// </summary>
public interface ISmartcard
{
    string DisplayName { get; }

    /// <summary>Apre una sessione esclusiva e restituisce l'ATR della carta.</summary>
    Task<byte[]> ConnectAsync();

    /// <summary>Invia un APDU di comando e restituisce l'APDU di risposta (dati + SW1 SW2).</summary>
    Task<byte[]> TransmitAsync(byte[] apdu);

    /// <summary>Chiude la sessione: la carta torna a Windows.</summary>
    Task DisconnectAsync();
}

public enum SmartcardErrorKind
{
    /// <summary>Il server non ha accettato il lettore virtuale (nessun dispositivo smartcard nella VM?).</summary>
    ReaderNotAccepted,
    CardAlreadyShared,
    NoAtr,
}

public sealed class SmartcardException(SmartcardErrorKind kind) : Exception(kind switch
{
    SmartcardErrorKind.ReaderNotAccepted => "La macchina virtuale non ha accettato il lettore di smartcard.",
    SmartcardErrorKind.CardAlreadyShared => "Una smartcard è già condivisa con la macchina virtuale.",
    _ => "La carta non ha risposto all'accensione.",
})
{
    public SmartcardErrorKind Kind { get; } = kind;
}

public abstract record SmartcardEvent
{
    public sealed record Inserted(string Name) : SmartcardEvent;

    public sealed record Removed : SmartcardEvent;
}

/// <summary>
/// Canale smartcard (porting di <c>SmartcardChannel.swift</c>): un lettore virtuale nell'ospite
/// (<c>ccid-card-passthru</c> di QEMU) con dentro una carta del PC alla volta.
/// </summary>
/// <remarks>
/// Flusso: READER_ADD con id <see cref="VscMessage.UndefinedReaderId"/> → il server risponde
/// ERROR(success) con l'id assegnato → ATR → gli APDU dell'ospite uno per uno → CARD_REMOVE.
/// FLUSH si risponde con FLUSH_COMPLETE. L'uscita del canale restituisce subito la carta.
/// </remarks>
public sealed class SmartcardChannel(ChannelConnection connection, ChannelWriter<SmartcardEvent> events)
{
    internal const string ReaderName = "PxStation Windows Smartcard Reader";

    private readonly Lock _gate = new();
    private uint? _readerId;
    private TaskCompletionSource<uint?>? _readerWaiter;
    private ISmartcard? _card;
    private bool _inserting;
    private bool _closed;

    public static CapabilitySet Capabilities { get; } = new();

    public string? SharedCardName
    {
        get
        {
            lock (_gate) return _card?.DisplayName;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                var message = await connection.NextMessageAsync(cancellationToken).ConfigureAwait(false);
                if (message.Type != VscMessage.ChannelMessageType) continue;
                VscMessage vsc;
                try
                {
                    vsc = VscMessage.Decode(message.Payload);
                }
                catch (WireException error)
                {
                    SpiceLog.Info("smartcard", $"messaggio non valido: {error.Message}");
                    continue;
                }
                await HandleAsync(vsc).ConfigureAwait(false);
            }
        }
        finally
        {
            TaskCompletionSource<uint?>? waiter;
            lock (_gate)
            {
                _closed = true;
                waiter = _readerWaiter;
                _readerWaiter = null;
            }
            waiter?.TrySetResult(null);
            // Canale finito: la carta torna subito a Windows.
            await ReleaseCardAsync().ConfigureAwait(false);
        }
    }

    internal async Task HandleAsync(VscMessage message)
    {
        switch (message.Type)
        {
            case VscMessageType.Error:
                var code = message.ErrorCode ?? (uint)VscErrorCode.GeneralError;
                TaskCompletionSource<uint?>? waiter;
                lock (_gate)
                {
                    waiter = _readerWaiter;
                    _readerWaiter = null;
                }
                if (waiter is not null) waiter.TrySetResult(code == (uint)VscErrorCode.Success ? message.ReaderId : null);
                else if (code != (uint)VscErrorCode.Success) SpiceLog.Info("smartcard", $"errore {code} dell'ospite per il lettore {message.ReaderId}");
                break;
            case VscMessageType.Apdu:
                await TransmitAsync(message).ConfigureAwait(false);
                break;
            case VscMessageType.Flush:
                await SendAsync(new VscMessage(VscMessageType.FlushComplete, message.ReaderId)).ConfigureAwait(false);
                break;
        }
    }

    private async Task TransmitAsync(VscMessage message)
    {
        ISmartcard? card;
        lock (_gate) card = message.ReaderId == _readerId ? _card : null;
        if (card is null)
        {
            await SendAsync(VscMessage.Failure(VscErrorCode.GeneralError, message.ReaderId)).ConfigureAwait(false);
            return;
        }
        try
        {
            var response = await card.TransmitAsync(message.Data).ConfigureAwait(false);
            await SendAsync(new VscMessage(VscMessageType.Apdu, message.ReaderId, response)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SpiceLog.Info("smartcard", $"APDU non riuscito: {error.Message}");
            await SendAsync(VscMessage.Failure(VscErrorCode.GeneralError, message.ReaderId)).ConfigureAwait(false);
        }
    }

    /// <summary>Inserisce <paramref name="card"/> nel lettore virtuale dell'ospite.</summary>
    /// <exception cref="SmartcardException">Lettore non accettato, carta già condivisa o senza ATR.</exception>
    public async Task InsertAsync(ISmartcard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        lock (_gate)
        {
            if (_closed) throw new SmartcardException(SmartcardErrorKind.ReaderNotAccepted);
            if (_card is not null || _inserting) throw new SmartcardException(SmartcardErrorKind.CardAlreadyShared);
            _inserting = true;
        }
        try
        {
            var reader = await EnsureReaderAsync().ConfigureAwait(false);
            var atr = await card.ConnectAsync().ConfigureAwait(false);
            bool closed;
            lock (_gate) closed = _closed;
            if (atr.Length == 0 || closed)
            {
                // ATR vuoto, o canale chiuso mentre la carta si apriva: nessuna sessione esclusiva lasciata aperta.
                await card.DisconnectAsync().ConfigureAwait(false);
                throw new SmartcardException(closed ? SmartcardErrorKind.ReaderNotAccepted : SmartcardErrorKind.NoAtr);
            }
            lock (_gate) _card = card;
            await SendAsync(new VscMessage(VscMessageType.Atr, reader, atr)).ConfigureAwait(false);
            events.TryWrite(new SmartcardEvent.Inserted(card.DisplayName));
            SpiceLog.Info("smartcard", $"carta inserita nel lettore {reader}");
        }
        finally
        {
            lock (_gate) _inserting = false;
        }
    }

    /// <summary>Toglie la carta all'ospite e la restituisce a Windows.</summary>
    public async Task RemoveAsync()
    {
        uint? reader;
        lock (_gate)
        {
            if (_card is null) return;
            reader = _readerId;
        }
        if (reader is { } id) await SendAsync(new VscMessage(VscMessageType.CardRemove, id)).ConfigureAwait(false);
        await ReleaseCardAsync().ConfigureAwait(false);
    }

    private async Task ReleaseCardAsync()
    {
        ISmartcard? card;
        lock (_gate)
        {
            card = _card;
            _card = null;
        }
        if (card is null) return;
        await card.DisconnectAsync().ConfigureAwait(false);
        events.TryWrite(new SmartcardEvent.Removed());
    }

    private async Task<uint> EnsureReaderAsync()
    {
        TaskCompletionSource<uint?> waiter;
        lock (_gate)
        {
            if (_readerId is { } existing) return existing;
            if (_readerWaiter is not null) throw new SmartcardException(SmartcardErrorKind.CardAlreadyShared);
            // L'attesa si registra prima dell'invio, così la conferma non può arrivare prima.
            waiter = _readerWaiter = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        await SendAsync(new VscMessage(VscMessageType.ReaderAdd, VscMessage.UndefinedReaderId,
            System.Text.Encoding.UTF8.GetBytes(ReaderName))).ConfigureAwait(false);
        uint? assigned;
        try
        {
            assigned = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            assigned = null;
        }
        lock (_gate)
        {
            if (ReferenceEquals(_readerWaiter, waiter)) _readerWaiter = null;
            if (assigned is { } id) _readerId = id;
        }
        return assigned ?? throw new SmartcardException(SmartcardErrorKind.ReaderNotAccepted);
    }

    private async Task SendAsync(VscMessage message)
    {
        try
        {
            await connection.SendAsync(message.Encode()).ConfigureAwait(false);
        }
        catch (Exception error) when (error is Transport.TransportException or ObjectDisposedException or OperationCanceledException)
        {
            SpiceLog.Debug("smartcard", $"invio non riuscito: {error.Message}");
        }
    }
}

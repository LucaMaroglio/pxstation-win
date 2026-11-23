using System.Threading.Channels;
using SpiceKit.Agent;
using SpiceKit.Channels;
using SpiceKit.Wire;

namespace SpiceKit.Session;

/// <summary>Perché un file non è arrivato nell'ospite.</summary>
public sealed class FileTransferException(string message, FileTransferResult? result = null) : Exception(message)
{
    public FileTransferResult? Result { get; } = result;

    public static FileTransferException From(FileTransferResult result) => new(result switch
    {
        FileTransferResult.NotEnoughSpace => "Nella macchina virtuale non c'è abbastanza spazio libero.",
        FileTransferResult.SessionLocked => "La sessione della macchina virtuale è bloccata.",
        FileTransferResult.Cancelled => "Il trasferimento è stato annullato.",
        FileTransferResult.Disabled => "Il trasferimento di file è disattivato nella macchina virtuale.",
        FileTransferResult.AgentNotConnected => "L'agente SPICE (spice-vdagent) non è attivo nella macchina virtuale.",
        _ => "La macchina virtuale non è riuscita a salvare il file.",
    }, result);
}

/// <summary>Funzioni dell'agente dell'ospite (M6): risoluzione, appunti, trasferimento di file.</summary>
public sealed partial class SpiceSession
{
    /// <summary>Byte per l'agente in coda oltre i quali un trasferimento di file aspetta (1 MiB).</summary>
    private const int FileTransferWindow = 1024 * 1024;

    private readonly Channel<ClipboardMessage> _clipboard = Channel.CreateBounded<ClipboardMessage>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Dictionary<uint, TaskCompletionSource<FileTransferResult>> _fileWaiters = [];
    private readonly Dictionary<uint, FileTransferResult> _earlyFileStatus = [];
    private CapabilitySet _guestAgentCaps = new();
    private uint _nextFileTransferId = 1;

    /// <summary>Messaggi degli appunti dell'ospite (solo la selezione CLIPBOARD; testo già con gli a capo di Windows).</summary>
    public ChannelReader<ClipboardMessage> ClipboardEvents => _clipboard.Reader;

    /// <summary>L'agente dell'ospite è collegato.</summary>
    public bool IsAgentConnected => Volatile.Read(ref _main)?.IsAgentConnected == true;

    /// <summary>L'agente sa condividere gli appunti su richiesta.</summary>
    public bool ClipboardSupported => GuestCaps.Contains((int)AgentCapability.ClipboardByDemand);

    private CapabilitySet GuestCaps
    {
        get
        {
            lock (_lock) return _guestAgentCaps;
        }
    }

    private bool SelectionHeader => GuestCaps.Contains((int)AgentCapability.ClipboardSelection);

    /// <summary>Chiede all'agente di portare lo schermo dell'ospite a questa misura (pixel). Serve spice-vdagent.</summary>
    public bool RequestGuestResolution(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > DisplayChannel.MaxSurfaceDimension || height > DisplayChannel.MaxSurfaceDimension) return false;
        return SendAgent(AgentMessage.MonitorsConfig([((uint)width, (uint)height, 0, 0)]));
    }

    /// <summary>Gli appunti di Windows ora contengono questi tipi (i dati seguono su richiesta).</summary>
    public void OfferClipboard(IReadOnlyList<ClipboardDataType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (ClipboardSupported && types.Count > 0) SendAgent(ClipboardCodec.Grab(types, SelectionHeader));
    }

    /// <summary>Risponde a una richiesta dell'ospite. Il testo si dà nella forma di Windows e si converte per l'ospite.</summary>
    public void ProvideClipboard(ClipboardDataType type, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!ClipboardSupported) return;
        var payload = type == ClipboardDataType.Utf8Text
            ? ClipboardCodec.TextToGuest(System.Text.Encoding.UTF8.GetString(data), GuestCaps.Contains((int)AgentCapability.GuestLineEndCrlf))
            : data;
        SendAgent(ClipboardCodec.Data(type, payload, SelectionHeader));
    }

    /// <summary>Chiede all'ospite i dati degli appunti che ha offerto.</summary>
    public void RequestClipboard(ClipboardDataType type)
    {
        if (ClipboardSupported) SendAgent(ClipboardCodec.Request(type, SelectionHeader));
    }

    /// <summary>Ritira un'offerta (gli appunti di Windows non hanno più dati condivisibili).</summary>
    public void ReleaseClipboard()
    {
        if (ClipboardSupported) SendAgent(ClipboardCodec.Release(SelectionHeader));
    }

    /// <summary>
    /// Copia un file nell'ospite (finisce nella cartella dei download dell'agente). <paramref name="progress"/>
    /// riceve i byte inviati; la cancellazione annulla il trasferimento.
    /// </summary>
    /// <exception cref="FileTransferException">Agente assente, trasferimento rifiutato o file illeggibile.</exception>
    public async Task SendFileAsync(string path, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Volatile.Read(ref _main) is not { IsAgentConnected: true } main) throw FileTransferException.From(FileTransferResult.AgentNotConnected);
        if (GuestCaps.Contains((int)AgentCapability.FileXferDisabled)) throw FileTransferException.From(FileTransferResult.Disabled);
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferCodec.ChunkSize, useAsync: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new FileTransferException($"Impossibile leggere il file ({error.Message}).");
        }
        await using var _ = file.ConfigureAwait(false);
        uint id;
        lock (_lock) id = _nextFileTransferId++;
        var size = file.Length;
        if (!main.SendToAgent(FileTransferCodec.Start(id, Path.GetFileName(path), (ulong)size)))
        {
            throw FileTransferException.From(FileTransferResult.AgentNotConnected);
        }
        var first = await WaitForFileStatusAsync(id, cancellationToken).ConfigureAwait(false);
        if (first != FileTransferResult.CanSendData) throw FileTransferException.From(first);

        var buffer = new byte[FileTransferCodec.ChunkSize];
        long sent = 0;
        try
        {
            while (sent < size)
            {
                // Dosato dal controllo di flusso dell'agente: al massimo ~1 MiB in coda.
                while (main.PendingAgentBytes > FileTransferWindow)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
                var read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new FileTransferException("Il file si è accorciato durante la copia.");
                if (!main.SendToAgent(FileTransferCodec.Data(id, buffer.AsSpan(0, read))))
                {
                    throw FileTransferException.From(FileTransferResult.AgentNotConnected);
                }
                sent += read;
                progress?.Report(sent);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or FileTransferException or IOException)
        {
            main.SendToAgent(FileTransferCodec.Status(id, FileTransferResult.Cancelled));
            ForgetFileTransfer(id);
            if (error is IOException io) throw new FileTransferException($"Impossibile leggere il file ({io.Message}).");
            throw;
        }
        var result = await WaitForFileStatusAsync(id, cancellationToken).ConfigureAwait(false);
        if (result != FileTransferResult.Success) throw FileTransferException.From(result);
    }

    private bool SendAgent(AgentMessage message) => Volatile.Read(ref _main)?.SendToAgent(message) == true;

    private async Task<FileTransferResult> WaitForFileStatusAsync(uint id, CancellationToken cancellationToken)
    {
        TaskCompletionSource<FileTransferResult> waiter;
        lock (_lock)
        {
            if (_earlyFileStatus.Remove(id, out var early)) return early;
            waiter = new TaskCompletionSource<FileTransferResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _fileWaiters[id] = waiter;
        }
        using (cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken)))
        {
            return await waiter.Task.ConfigureAwait(false);
        }
    }

    private void ForgetFileTransfer(uint id)
    {
        lock (_lock)
        {
            if (_fileWaiters.Remove(id, out var waiter)) waiter.TrySetResult(FileTransferResult.Cancelled);
            _earlyFileStatus.Remove(id);
        }
    }

    private void SetGuestAgentCapabilities(CapabilitySet caps)
    {
        lock (_lock) _guestAgentCaps = caps;
        Emit(new SessionEvent.ClipboardAvailable(ClipboardSupported));
    }

    /// <summary>Agente scollegato o sessione finita: capacità azzerate, trasferimenti in attesa conclusi.</summary>
    private void AgentGone()
    {
        TaskCompletionSource<FileTransferResult>[] waiters;
        lock (_lock)
        {
            _guestAgentCaps = new CapabilitySet();
            waiters = [.. _fileWaiters.Values];
            _fileWaiters.Clear();
            _earlyFileStatus.Clear();
        }
        foreach (var waiter in waiters) waiter.TrySetResult(FileTransferResult.AgentNotConnected);
    }

    private void HandleAgentMessage(AgentMessage message)
    {
        if (FileTransferCodec.DecodeStatus(message) is { } status)
        {
            lock (_lock)
            {
                if (_fileWaiters.Remove(status.Id, out var waiter)) waiter.TrySetResult(status.Result);
                // Stato arrivato prima dell'attesa (corsa con l'invio): si tiene, ma solo per id già usati.
                else if (status.Id < _nextFileTransferId && _earlyFileStatus.Count < 64) _earlyFileStatus[status.Id] = status.Result;
            }
            return;
        }
        ClipboardMessage? decoded;
        try
        {
            decoded = ClipboardCodec.Decode(message, SelectionHeader);
        }
        catch (WireException error)
        {
            SpiceLog.Info("session", $"messaggio degli appunti scartato: {error.Message}");
            return;
        }
        if (decoded is null || decoded.Selection != ClipboardSelection.Clipboard) return;
        if (decoded is ClipboardMessage.Data { Type: ClipboardDataType.Utf8Text } text)
        {
            decoded = text with { Bytes = System.Text.Encoding.UTF8.GetBytes(ClipboardCodec.TextFromGuest(text.Bytes)) };
        }
        _clipboard.Writer.TryWrite(decoded);
    }
}

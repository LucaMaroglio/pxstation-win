using SpiceKit.Agent;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>
/// Coda dei messaggi verso l'agente, con il controllo di flusso a gettoni del server: ogni
/// AGENT_DATA consuma un gettone; il server ne restituisce con <c>AGENT_TOKEN</c>.
/// </summary>
/// <remarks>
/// Limitata (un trasferimento degli appunti al massimo + 1 MiB): se l'agente non consuma, i
/// messaggi nuovi si scartano invece di accumularsi senza fine. Un solo invio alla volta (ordine
/// garantito); <see cref="Enqueue"/> e <see cref="AddTokens"/> si possono chiamare da qualunque thread.
/// </remarks>
public sealed class AgentOutbox
{
    /// <summary>Byte in coda al massimo.</summary>
    public const int MaxBytes = ClipboardCodec.MaxDataSize + (1024 * 1024);

    private readonly Lock _lock = new();
    private readonly Queue<byte[]> _chunks = new();
    /// <summary>Segnale "c'è qualcosa da fare" (al massimo uno in sospeso).</summary>
    private readonly System.Threading.Channels.Channel<bool> _signal = System.Threading.Channels.Channel.CreateBounded<bool>(
        new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });
    private uint _tokens;
    private int _bytes;

    /// <summary>Byte in coda e non ancora inviati (per dosare i trasferimenti grandi).</summary>
    public int PendingBytes
    {
        get
        {
            lock (_lock) return _bytes;
        }
    }

    /// <summary>Accoda un messaggio; <see langword="false"/> se la coda è piena (messaggio scartato).</summary>
    public bool Enqueue(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var chunks = message.EncodeChunks();
        var size = chunks.Sum(c => c.Length);
        lock (_lock)
        {
            if (_bytes + size > MaxBytes) return false;
            foreach (var chunk in chunks) _chunks.Enqueue(chunk);
            _bytes += size;
        }
        _signal.Writer.TryWrite(true);
        return true;
    }

    public void AddTokens(uint tokens)
    {
        lock (_lock) _tokens = unchecked(_tokens + tokens);
        _signal.Writer.TryWrite(true);
    }

    /// <summary>Gettoni di invio, all'avvio dell'agente (<c>INIT</c>, <c>AGENT_CONNECTED_TOKENS</c>).</summary>
    public void SetTokens(uint tokens)
    {
        lock (_lock) _tokens = tokens;
        _signal.Writer.TryWrite(true);
    }

    /// <summary>L'agente si è scollegato: la coda si svuota.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _chunks.Clear();
            _bytes = 0;
        }
    }

    /// <summary>Pezzo da inviare ora, se ci sono dati e gettoni (test).</summary>
    internal byte[]? TryTake()
    {
        lock (_lock)
        {
            if (_tokens == 0 || _chunks.Count == 0) return null;
            _tokens--;
            var chunk = _chunks.Dequeue();
            _bytes -= chunk.Length;
            return chunk;
        }
    }

    /// <summary>Invia i pezzi quando ci sono gettoni, fino alla cancellazione.</summary>
    public async Task PumpAsync(Func<byte[], CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        while (true)
        {
            await _signal.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            while (TryTake() is { } chunk) await send(chunk, cancellationToken).ConfigureAwait(false);
        }
    }
}

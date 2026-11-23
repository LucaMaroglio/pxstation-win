using System.Threading.Channels;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>Eventi del canale playback (ospite → altoparlanti).</summary>
public abstract record PlaybackEvent
{
    public sealed record Started(int Channels, int Frequency) : PlaybackEvent;

    public sealed record Stopped : PlaybackEvent;

    /// <summary>Un pacchetto: PCM S16LE interlacciato oppure un fotogramma Opus.</summary>
    public sealed record Data(AudioDataMode Mode, uint Time, ReadOnlyMemory<byte> Bytes) : PlaybackEvent;

    /// <summary>Volume del mixer dell'ospite, 0…1.</summary>
    public sealed record Volume(float Level) : PlaybackEvent;

    public sealed record Mute(bool Muted) : PlaybackEvent;

    public sealed record Latency(uint Milliseconds) : PlaybackEvent;
}

/// <summary>
/// Canale playback (porting di <c>PlaybackChannel</c> macOS): inoltra i pacchetti audio; la
/// decodifica e l'uscita stanno nella piattaforma (SpiceKit.Windows).
/// </summary>
/// <remarks>
/// Pacchetti oltre 64 KiB si scartano (10 ms di PCM a 8 canali e 192 kHz sono circa 31 KB). La
/// coda verso chi riproduce tiene al massimo ~2 secondi: se rimane indietro si perde l'audio più
/// vecchio, mai memoria senza limiti.
/// </remarks>
public sealed class PlaybackChannel(ChannelConnection connection, ChannelWriter<PlaybackEvent> events)
{
    internal const int MaxPacket = 64 * 1024;

    /// <summary>Capacità annunciate: volume, latenza e (se la piattaforma lo decodifica) Opus.</summary>
    public static CapabilitySet Capabilities(bool opus) => opus
        ? CapabilitySet.FromBits((int)PlaybackCapability.Volume, (int)PlaybackCapability.Latency, (int)PlaybackCapability.Opus)
        : CapabilitySet.FromBits((int)PlaybackCapability.Volume, (int)PlaybackCapability.Latency);

    private AudioDataMode _mode = AudioDataMode.Raw;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                Handle(await connection.NextMessageAsync(cancellationToken).ConfigureAwait(false));
            }
        }
        finally
        {
            events.TryWrite(new PlaybackEvent.Stopped());
        }
    }

    internal void Handle(SpiceMessage message)
    {
        if (message.Payload.Length > MaxPacket)
        {
            SpiceLog.Info("playback", $"pacchetto audio di {message.Payload.Length} byte scartato");
            return;
        }
        try
        {
            switch ((PlaybackServerMessage)message.Type)
            {
                case PlaybackServerMessage.Mode:
                    var mode = AudioPacket.DecodeMode(message.Payload);
                    _mode = mode.Mode ?? AudioDataMode.Invalid;
                    if (!mode.Data.IsEmpty) events.TryWrite(new PlaybackEvent.Data(_mode, mode.Time, mode.Data));
                    break;
                case PlaybackServerMessage.Data:
                    var packet = AudioPacket.DecodeData(message.Payload);
                    events.TryWrite(new PlaybackEvent.Data(_mode, packet.Time, packet.Data));
                    break;
                case PlaybackServerMessage.Start:
                    var start = AudioStart.Decode(message.Payload, hasTime: true);
                    if (start.Format != AudioStart.FormatS16)
                    {
                        SpiceLog.Info("playback", $"formato audio {start.Format} non supportato");
                        break;
                    }
                    events.TryWrite(new PlaybackEvent.Started((int)start.Channels, (int)start.Frequency));
                    break;
                case PlaybackServerMessage.Stop:
                    events.TryWrite(new PlaybackEvent.Stopped());
                    break;
                case PlaybackServerMessage.Volume:
                    events.TryWrite(new PlaybackEvent.Volume(AudioDecoders.Volume(message.Payload)));
                    break;
                case PlaybackServerMessage.Mute:
                    events.TryWrite(new PlaybackEvent.Mute(AudioDecoders.Mute(message.Payload)));
                    break;
                case PlaybackServerMessage.Latency:
                    events.TryWrite(new PlaybackEvent.Latency(new ByteReader(message.Payload).ReadU32()));
                    break;
            }
        }
        catch (WireException error)
        {
            SpiceLog.Info("playback", $"messaggio {message.Type} non valido: {error.Message}");
        }
    }

    /// <summary>Coda degli eventi per chi riproduce: ~2 s di pacchetti da 10 ms, poi si perde il più vecchio.</summary>
    public static Channel<PlaybackEvent> CreateEventQueue() => Channel.CreateBounded<PlaybackEvent>(
        new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
}

/// <summary>Eventi del canale record (microfono → ospite).</summary>
public abstract record RecordEvent
{
    /// <summary>L'ospite registra: vuole PCM S16 con questi canali e questa frequenza.</summary>
    public sealed record Started(int Channels, int Frequency) : RecordEvent;

    public sealed record Stopped : RecordEvent;

    public sealed record Volume(float Level) : RecordEvent;

    public sealed record Mute(bool Muted) : RecordEvent;
}

/// <summary>
/// Canale record (porting di <c>RecordChannel</c> macOS): l'ospite chiede l'audio con START, il
/// client risponde con MODE, START_MARK e poi pacchetti DATA fino a STOP.
/// </summary>
/// <remarks>
/// <see cref="Send"/> si chiama dal thread di cattura e non blocca: i messaggi passano da una coda
/// limitata (~0,5 s) svuotata da <see cref="RunAsync"/>. Solo PCM: Opus in ingresso richiede un
/// server compilato con Opus e non serve per la voce sulla rete locale.
/// </remarks>
public sealed class RecordChannel(ChannelConnection connection, ChannelWriter<RecordEvent> events)
{
    public static CapabilitySet Capabilities { get; } = CapabilitySet.FromBits((int)RecordCapability.Volume);

    private readonly Channel<SpiceMessage> _outbox = Channel.CreateBounded<SpiceMessage>(
        // Coda piena: si perde l'audio nuovo, mai MODE o START_MARK già accodati.
        new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Lock _lock = new();
    private bool _active;
    private bool _markSent;
    private bool _modeSent;

    /// <summary>L'ospite sta registrando.</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock) return _active;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = PumpAsync(stop.Token);
        try
        {
            while (true)
            {
                Handle(await connection.NextMessageAsync(cancellationToken).ConfigureAwait(false));
            }
        }
        finally
        {
            lock (_lock) _active = false;
            events.TryWrite(new RecordEvent.Stopped());
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or Transport.TransportException)
            {
            }
        }
    }

    internal void Handle(SpiceMessage message)
    {
        try
        {
            switch ((RecordServerMessage)message.Type)
            {
                case RecordServerMessage.Start:
                    var start = AudioStart.Decode(message.Payload, hasTime: false);
                    if (start.Format != AudioStart.FormatS16) break;
                    lock (_lock)
                    {
                        _active = true;
                        _markSent = false;
                        _modeSent = false;
                    }
                    events.TryWrite(new RecordEvent.Started((int)start.Channels, (int)start.Frequency));
                    break;
                case RecordServerMessage.Stop:
                    lock (_lock) _active = false;
                    events.TryWrite(new RecordEvent.Stopped());
                    break;
                case RecordServerMessage.Volume:
                    events.TryWrite(new RecordEvent.Volume(AudioDecoders.Volume(message.Payload)));
                    break;
                case RecordServerMessage.Mute:
                    events.TryWrite(new RecordEvent.Mute(AudioDecoders.Mute(message.Payload)));
                    break;
            }
        }
        catch (WireException error)
        {
            SpiceLog.Info("record", $"messaggio {message.Type} non valido: {error.Message}");
        }
    }

    /// <summary>Audio catturato (PCM S16LE interlacciato); ignorato se l'ospite non registra.</summary>
    public void Send(ReadOnlySpan<byte> pcm, uint time)
    {
        if (pcm.IsEmpty || pcm.Length > PlaybackChannel.MaxPacket) return;
        lock (_lock)
        {
            if (!_active) return;
            if (!_modeSent)
            {
                _outbox.Writer.TryWrite(RecordClientEncoder.Mode(AudioDataMode.Raw, time));
                _modeSent = true;
            }
            if (!_markSent)
            {
                _outbox.Writer.TryWrite(RecordClientEncoder.StartMark(time));
                _markSent = true;
            }
            _outbox.Writer.TryWrite(RecordClientEncoder.Data(pcm, time));
        }
    }

    /// <summary>Messaggi in coda e non ancora inviati (test).</summary>
    internal SpiceMessage? TakeQueued() => _outbox.Reader.TryRead(out var message) ? message : null;

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }
}

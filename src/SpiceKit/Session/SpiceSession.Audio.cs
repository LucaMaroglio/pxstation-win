using System.Threading.Channels;
using SpiceKit.Channels;

namespace SpiceKit.Session;

/// <summary>Che cosa sa fare la piattaforma con l'audio; decide quali canali collegare e cosa annunciare.</summary>
/// <param name="Playback">Collegare il canale playback (altoparlanti).</param>
/// <param name="OpusPlayback">Annunciare Opus in riproduzione (altrimenti PCM).</param>
/// <param name="Record">Collegare il canale record (il microfono si accende solo su richiesta dell'utente).</param>
public readonly record struct AudioSupport(bool Playback, bool OpusPlayback, bool Record);

/// <summary>Audio della sessione (M7): riproduzione e microfono.</summary>
public sealed partial class SpiceSession
{
    private readonly Channel<PlaybackEvent> _playback = PlaybackChannel.CreateEventQueue();
    private readonly Channel<RecordEvent> _record = Channel.CreateBounded<RecordEvent>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private RecordChannel? _recordChannel;

    /// <summary>Capacità audio della piattaforma; da impostare prima di <see cref="ConnectAsync"/>.</summary>
    public AudioSupport Audio { get; init; }

    /// <summary>Audio dell'ospite da riprodurre; si completa alla fine della sessione.</summary>
    public ChannelReader<PlaybackEvent> PlaybackEvents => _playback.Reader;

    /// <summary>Richieste di registrazione dell'ospite; si completa alla fine della sessione.</summary>
    public ChannelReader<RecordEvent> RecordEvents => _record.Reader;

    /// <summary>L'ospite sta registrando (vuole l'audio del microfono).</summary>
    public bool GuestIsRecording => Volatile.Read(ref _recordChannel)?.IsActive == true;

    /// <summary>
    /// Audio del microfono (PCM S16LE interlacciato nel formato chiesto dall'ospite). Non blocca;
    /// ignorato se l'ospite non registra.
    /// </summary>
    public void SendMicrophone(ReadOnlySpan<byte> pcm) =>
        Volatile.Read(ref _recordChannel)?.Send(pcm, _clock.Now ?? 0);
}

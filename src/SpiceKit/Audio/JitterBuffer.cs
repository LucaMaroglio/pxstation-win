namespace SpiceKit.Audio;

/// <summary>
/// Buffer anti-jitter dell'audio riprodotto (porting dell'anello di <c>AudioPlayer.swift</c>).
/// </summary>
/// <remarks>
/// Campioni S16 interlacciati. La riproduzione parte quando ci sono ~60 ms, sull'esaurimento
/// produce silenzio e si riempie di nuovo prima di ripartire, e oltre ~250 ms scarta l'audio più
/// vecchio: la latenza resta bassa anche dopo un rallentamento della rete. Thread-safe: si scrive
/// dal ciclo dei pacchetti e si legge dal thread di uscita audio.
/// </remarks>
public sealed class JitterBuffer
{
    private readonly Lock _lock = new();
    private readonly short[] _samples;
    private readonly int _startThreshold;
    private readonly int _maxBuffered;
    private int _read;
    private int _count;
    private bool _playing;

    public JitterBuffer(int channels, int sampleRate, int startMilliseconds = 60, int maxMilliseconds = 250)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        Channels = channels;
        SampleRate = sampleRate;
        _samples = new short[sampleRate * channels]; // 1 s
        _startThreshold = Align(sampleRate * channels * startMilliseconds / 1000);
        _maxBuffered = Align(sampleRate * channels * maxMilliseconds / 1000);
    }

    public int Channels { get; }

    public int SampleRate { get; }

    /// <summary>Volte in cui l'audio si è esaurito durante la riproduzione (diagnostica).</summary>
    public int Underruns { get; private set; }

    /// <summary>Campioni scartati per contenere la latenza (diagnostica).</summary>
    public long Dropped { get; private set; }

    /// <summary>Millisecondi di audio in attesa.</summary>
    public int BufferedMilliseconds
    {
        get
        {
            lock (_lock) return (int)((long)_count * 1000 / (SampleRate * Channels));
        }
    }

    /// <summary>Accoda campioni interlacciati.</summary>
    public void Write(ReadOnlySpan<short> pcm)
    {
        lock (_lock)
        {
            var size = _samples.Length;
            foreach (var sample in pcm)
            {
                _samples[(_read + _count) % size] = sample;
                if (_count < size) _count++;
                else _read = (_read + 1) % size;
            }
            if (_count > _maxBuffered)
            {
                // Troppa latenza accumulata (per esempio dopo uno stallo della rete): via il più vecchio.
                var drop = Align(_count - _startThreshold);
                _read = (_read + drop) % size;
                _count -= drop;
                Dropped += drop;
            }
        }
    }

    /// <summary>Riempie <paramref name="output"/> (campioni interlacciati); silenzio dove manca audio.</summary>
    /// <returns>Campioni reali copiati (il resto è silenzio).</returns>
    public int Read(Span<short> output)
    {
        lock (_lock)
        {
            if (!_playing && _count >= _startThreshold) _playing = true;
            var playable = _playing ? Math.Min(Align(output.Length), _count) : 0;
            for (var i = 0; i < playable; i++) output[i] = _samples[(_read + i) % _samples.Length];
            output[playable..].Clear();
            _read = (_read + playable) % _samples.Length;
            _count -= playable;
            if (_playing && playable < output.Length)
            {
                _playing = false; // esaurito: si riempie di nuovo prima di ripartire
                Underruns++;
            }
            return playable;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _read = 0;
            _count = 0;
            _playing = false;
        }
    }

    private int Align(int samples) => samples - (samples % Channels);
}

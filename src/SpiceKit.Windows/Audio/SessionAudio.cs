using System.Runtime.InteropServices;
using SpiceKit.Audio;
using SpiceKit.Channels;
using SpiceKit.Session;
using SpiceKit.Wire;

namespace SpiceKit.Windows.Audio;

/// <summary>
/// Audio di una sessione: riproduce l'audio dell'ospite (PCM o Opus) e, solo se l'utente lo
/// accende, manda il microfono quando l'ospite registra.
/// </summary>
/// <remarks>
/// Il microfono è spento per impostazione predefinita in ogni sessione (come macOS): si apre solo
/// con <see cref="MicrophoneEnabled"/> acceso e l'ospite in registrazione, e si chiude appena una
/// delle due cose smette. I cicli girano fuori dal thread dell'interfaccia; <see cref="Changed"/>
/// avvisa (da un altro thread) quando cambia lo stato da mostrare.
/// </remarks>
public sealed class SessionAudio : IAsyncDisposable
{
    /// <summary>Che cosa annunciare al server: riproduzione con Opus (Concentus) e registrazione PCM.</summary>
    public static AudioSupport Support { get; } = new(Playback: true, OpusPlayback: true, Record: true);

    private readonly SpiceSession _session;
    private readonly Lock _lock = new();
    private readonly Task _playback;
    private readonly Task _record;
    private JitterBuffer? _jitter;
    private WasapiRenderer? _renderer;
    private OpusPacketDecoder? _opus;
    private WasapiCapture? _capture;
    private (int Channels, int Frequency)? _recordFormat;
    private bool _microphoneEnabled;
    private float _volume = 1;
    private bool _muted;
    private long _packets;
    private long _samples;
    private long _microphonePackets;
    private AudioDataMode _lastMode;
    private long _crossings;
    private long _frames;
    private bool _lastPositive;
    private int _frequency;

    public SessionAudio(SpiceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _playback = Task.Run(RunPlaybackAsync);
        _record = Task.Run(RunRecordAsync);
    }

    /// <summary>Lo stato da mostrare è cambiato (ospite che registra, errore del microfono…). Da un altro thread.</summary>
    public event Action? Changed;

    /// <summary>L'utente permette l'uso del microfono in questa sessione.</summary>
    public bool MicrophoneEnabled
    {
        get
        {
            lock (_lock) return _microphoneEnabled;
        }
        set
        {
            lock (_lock)
            {
                if (_microphoneEnabled == value) return;
                _microphoneEnabled = value;
                UpdateCapture();
            }
            Changed?.Invoke();
        }
    }

    /// <summary>L'ospite sta registrando (anche con il microfono spento).</summary>
    public bool GuestIsRecording
    {
        get
        {
            lock (_lock) return _recordFormat is not null;
        }
    }

    /// <summary>Il microfono è aperto e manda audio all'ospite.</summary>
    public bool MicrophoneActive
    {
        get
        {
            lock (_lock) return _capture?.IsCapturing == true;
        }
    }

    /// <summary>Windows ha negato l'accesso al microfono (Impostazioni › Privacy e sicurezza › Microfono).</summary>
    public bool MicrophoneDenied
    {
        get
        {
            lock (_lock) return _capture?.AccessDeniedByWindows == true;
        }
    }

    /// <summary>Diagnostica: pacchetti audio ricevuti, campioni decodificati, uscita, pacchetti del microfono inviati.</summary>
    public (long Packets, long Samples, string Output, long MicrophonePackets) Statistics
    {
        get
        {
            lock (_lock)
            {
                var device = _renderer is null ? "nessun flusso"
                    : _renderer.IsPlaying ? "dispositivo aperto"
                    : $"nessun dispositivo ({_renderer.DeviceError ?? "in apertura"})";
                // Frequenza dominante stimata: due passaggi per lo zero a periodo.
                var hertz = _frames > 0 && _frequency > 0 ? _crossings * _frequency / 2 / _frames : 0;
                var output = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{(_lastMode == AudioDataMode.Opus ? "Opus" : "PCM")} {_frequency} Hz, tono ~{hertz} Hz, {device}");
                return (_packets, _samples, output, _microphonePackets);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // I cicli finiscono quando la sessione completa le code degli eventi.
        await Task.WhenAny(Task.WhenAll(_playback, _record), Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        lock (_lock)
        {
            _microphoneEnabled = false;
            StopPlayback();
            UpdateCapture();
        }
    }

    private async Task RunPlaybackAsync()
    {
        await foreach (var value in _session.PlaybackEvents.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_lock)
            {
                switch (value)
                {
                    case PlaybackEvent.Started started:
                        StopPlayback();
                        _jitter = new JitterBuffer(started.Channels, started.Frequency);
                        _frequency = started.Frequency;
                        _renderer = new WasapiRenderer(_jitter);
                        ApplyGain();
                        break;
                    case PlaybackEvent.Data data when _jitter is not null:
                        _packets++;
                        Play(data);
                        break;
                    case PlaybackEvent.Volume volume:
                        _volume = volume.Level;
                        ApplyGain();
                        break;
                    case PlaybackEvent.Mute mute:
                        _muted = mute.Muted;
                        ApplyGain();
                        break;
                    case PlaybackEvent.Stopped:
                        StopPlayback();
                        break;
                }
            }
        }
        lock (_lock) StopPlayback();
    }

    private void Play(PlaybackEvent.Data data)
    {
        if (_jitter is not { } jitter) return;
        ReadOnlySpan<short> pcm;
        if (data.Mode == AudioDataMode.Opus)
        {
            if (jitter.Channels > 2) return;
            _opus ??= new OpusPacketDecoder(jitter.Channels);
            pcm = _opus.Decode(data.Bytes.Span);
        }
        else if (data.Mode == AudioDataMode.Raw)
        {
            var bytes = data.Bytes.Span;
            pcm = MemoryMarshal.Cast<byte, short>(bytes[..(bytes.Length & ~1)]);
        }
        else
        {
            return;
        }
        _samples += pcm.Length;
        _lastMode = data.Mode;
        CountCrossings(pcm, jitter.Channels);
        jitter.Write(pcm);
    }

    /// <summary>Passaggi per lo zero del primo canale (stima della frequenza per la diagnostica).</summary>
    private void CountCrossings(ReadOnlySpan<short> pcm, int channels)
    {
        for (var i = 0; i < pcm.Length; i += channels)
        {
            var positive = pcm[i] > 0;
            if (positive != _lastPositive && pcm[i] != 0) _crossings++;
            if (pcm[i] != 0) _lastPositive = positive;
            _frames++;
        }
    }

    private void ApplyGain()
    {
        if (_renderer is not null) _renderer.Gain = _muted ? 0 : _volume;
    }

    private void StopPlayback()
    {
        _renderer?.Dispose();
        _renderer = null;
        _jitter = null;
        _opus = null;
    }

    private async Task RunRecordAsync()
    {
        await foreach (var value in _session.RecordEvents.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_lock)
            {
                switch (value)
                {
                    case RecordEvent.Started started:
                        _recordFormat = (started.Channels, started.Frequency);
                        _capture?.Dispose();
                        _capture = null; // formato nuovo: si riapre
                        UpdateCapture();
                        break;
                    case RecordEvent.Stopped:
                        _recordFormat = null;
                        UpdateCapture();
                        break;
                }
            }
            Changed?.Invoke();
        }
        lock (_lock)
        {
            _recordFormat = null;
            UpdateCapture();
        }
    }

    /// <summary>Apre il microfono solo con il permesso dell'utente e l'ospite in registrazione (con il lock preso).</summary>
    private void UpdateCapture()
    {
        var wanted = _microphoneEnabled && _recordFormat is not null;
        if (wanted && _capture is null && _recordFormat is { } format)
        {
            _capture = new WasapiCapture(format.Channels, format.Frequency, packet =>
            {
                Interlocked.Increment(ref _microphonePackets);
                _session.SendMicrophone(packet.Span);
            });
        }
        else if (!wanted && _capture is not null)
        {
            _capture.Dispose();
            _capture = null;
        }
    }
}

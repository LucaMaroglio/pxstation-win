using Concentus;

namespace SpiceKit.Windows.Audio;

/// <summary>
/// Decodifica i pacchetti Opus del canale playback (uno per messaggio, 48 kHz) con Concentus,
/// implementazione C# di Opus (licenza BSD), senza librerie native.
/// </summary>
public sealed class OpusPacketDecoder
{
    /// <summary>Opus lavora sempre a 48 kHz in SPICE.</summary>
    public const int SampleRate = 48_000;

    /// <summary>120 ms a 48 kHz: il pacchetto Opus più lungo possibile.</summary>
    private const int MaxFrames = 5760;

    /// <summary>Pacchetto più grande accettato (i fotogrammi Opus non superano 1275 byte; margine per pacchetti multipli).</summary>
    private const int MaxPacket = 4000;

    private readonly IOpusDecoder _decoder;
    private readonly short[] _output;

    static OpusPacketDecoder() => OpusCodecFactory.AttemptToUseNativeLibrary = false;

    public OpusPacketDecoder(int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 2);
        Channels = channels;
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, channels);
        _output = new short[MaxFrames * channels];
    }

    public int Channels { get; }

    /// <summary>Campioni interlacciati del pacchetto (validi fino alla chiamata successiva), vuoto se non decodificabile.</summary>
    public ReadOnlySpan<short> Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty || packet.Length > MaxPacket) return [];
        try
        {
            var frames = _decoder.Decode(packet, _output, MaxFrames, false);
            return frames > 0 ? _output.AsSpan(0, frames * Channels) : [];
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IndexOutOfRangeException or OpusException)
        {
            _decoder.ResetState();
            return [];
        }
    }
}

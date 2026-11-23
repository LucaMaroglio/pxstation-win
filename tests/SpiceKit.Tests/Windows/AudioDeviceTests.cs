using Concentus;
using SpiceKit.Audio;
using SpiceKit.Windows.Audio;

namespace SpiceKit.Tests.Windows;

/// <summary>Opus (Concentus) e WASAPI.</summary>
public class AudioDeviceTests
{
    private static short[] Sine(int frames, int channels, double frequency)
    {
        var pcm = new short[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var value = (short)(12000 * Math.Sin(2 * Math.PI * frequency * i / 48000));
            for (var c = 0; c < channels; c++) pcm[(i * channels) + c] = value;
        }
        return pcm;
    }

    private static double Rms(ReadOnlySpan<short> pcm)
    {
        double sum = 0;
        foreach (var s in pcm) sum += (double)s * s;
        return Math.Sqrt(sum / Math.Max(1, pcm.Length));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Opus_packets_decode_to_the_same_tone(int channels)
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        var encoder = OpusCodecFactory.CreateEncoder(48000, channels, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
        var decoder = new OpusPacketDecoder(channels);
        var packet = new byte[1500];
        double rms = 0;
        // Alcuni pacchetti: il primo risente dell'avvio del codec.
        for (var n = 0; n < 5; n++)
        {
            var length = encoder.Encode(Sine(480, channels, 440), 480, packet, packet.Length); // 10 ms, come spice-server
            var pcm = decoder.Decode(packet.AsSpan(0, length));
            Assert.Equal(480 * channels, pcm.Length);
            rms = Rms(pcm);
        }
        Assert.InRange(rms, 6000, 11000); // seno di ampiezza 12000: RMS ≈ 8485
    }

    [Fact]
    public void Invalid_opus_packets_yield_nothing()
    {
        var decoder = new OpusPacketDecoder(2);
        Assert.True(decoder.Decode([]).IsEmpty);
        Assert.True(decoder.Decode(new byte[5000]).IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusPacketDecoder(3));
    }

    [Fact]
    public async Task Wasapi_renderer_drains_the_jitter_buffer()
    {
        var buffer = new JitterBuffer(2, 48000);
        using var renderer = new WasapiRenderer(buffer);
        buffer.Write(Sine(48000 / 5, 2, 440)); // 200 ms (ne restano al massimo 250)
        for (var i = 0; i < 40 && !renderer.IsPlaying && renderer.DeviceError is null; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        if (!renderer.IsPlaying) Assert.Skip($"nessun dispositivo audio di uscita ({renderer.DeviceError})");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.True(buffer.BufferedMilliseconds < 100, $"audio ancora in coda: {buffer.BufferedMilliseconds} ms");
    }
}

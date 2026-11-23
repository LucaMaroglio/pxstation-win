using System.Buffers.Binary;
using System.Threading.Channels;
using SpiceKit.Audio;
using SpiceKit.Channels;
using SpiceKit.Session;
using SpiceKit.Tests.Support;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Audio;

public class AudioTests
{
    private static SpiceMessage Message<T>(T type, params byte[] payload) where T : Enum => new(Convert.ToUInt16(type, System.Globalization.CultureInfo.InvariantCulture), payload);

    private static byte[] Start(uint channels, ushort format, uint frequency, bool time)
    {
        var w = new ByteWriter();
        w.WriteU32(channels);
        w.WriteU16(format);
        w.WriteU32(frequency);
        if (time) w.WriteU32(1000);
        return w.ToArray();
    }

    private static (PlaybackChannel Channel, Channel<PlaybackEvent> Events) NewPlayback()
    {
        var (client, _) = MemoryTransport.Pair();
        var events = Channel.CreateUnbounded<PlaybackEvent>();
        return (new PlaybackChannel(new ChannelConnection(ChannelType.Playback, 0, client, new CapabilitySet()), events.Writer), events);
    }

    private static List<T> Drain<T>(Channel<T> channel)
    {
        var list = new List<T>();
        while (channel.Reader.TryRead(out var value)) list.Add(value);
        return list;
    }

    [Fact]
    public void Playback_start_mode_data_volume_and_mute()
    {
        var (playback, events) = NewPlayback();
        playback.Handle(Message(PlaybackServerMessage.Start, Start(2, AudioStart.FormatS16, 48000, time: true)));
        playback.Handle(Message(PlaybackServerMessage.Mode, [10, 0, 0, 0, (byte)AudioDataMode.Opus, 0]));
        playback.Handle(Message(PlaybackServerMessage.Data, [20, 0, 0, 0, 0xAA, 0xBB]));
        playback.Handle(Message(PlaybackServerMessage.Volume, [2, 0xFF, 0xFF, 0, 0]));
        playback.Handle(Message(PlaybackServerMessage.Mute, [1]));
        playback.Handle(Message(PlaybackServerMessage.Stop));
        var list = Drain(events);
        Assert.Equal(new PlaybackEvent.Started(2, 48000), list[0]);
        var data = Assert.IsType<PlaybackEvent.Data>(list[1]);
        Assert.Equal((AudioDataMode.Opus, 20u), (data.Mode, data.Time));
        Assert.Equal([0xAA, 0xBB], data.Bytes.ToArray());
        Assert.Equal(0.5f, Assert.IsType<PlaybackEvent.Volume>(list[2]).Level, 3);
        Assert.Equal(new PlaybackEvent.Mute(true), list[3]);
        Assert.IsType<PlaybackEvent.Stopped>(list[4]);
    }

    [Fact]
    public void Hostile_audio_is_rejected()
    {
        var (playback, events) = NewPlayback();
        playback.Handle(Message(PlaybackServerMessage.Start, Start(9, AudioStart.FormatS16, 48000, time: true)));
        playback.Handle(Message(PlaybackServerMessage.Start, Start(2, AudioStart.FormatS16, 400_000, time: true)));
        playback.Handle(Message(PlaybackServerMessage.Start, Start(2, 7, 48000, time: true)));
        playback.Handle(Message(PlaybackServerMessage.Data, new byte[PlaybackChannel.MaxPacket + 1]));
        playback.Handle(Message(PlaybackServerMessage.Data, [1, 2]));
        Assert.Empty(Drain(events));
    }

    [Fact]
    public void Record_sends_mode_and_mark_once_then_data_only_while_recording()
    {
        var (client, _) = MemoryTransport.Pair();
        var events = Channel.CreateUnbounded<RecordEvent>();
        var record = new RecordChannel(new ChannelConnection(ChannelType.Record, 0, client, new CapabilitySet()), events.Writer);
        record.Send([1, 2, 3, 4], 5);
        Assert.Null(record.TakeQueued()); // l'ospite non registra

        record.Handle(Message(RecordServerMessage.Start, Start(2, AudioStart.FormatS16, 44100, time: false)));
        Assert.Equal(new RecordEvent.Started(2, 44100), Assert.Single(Drain(events)));
        Assert.True(record.IsActive);
        record.Send([1, 2, 3, 4], 7);
        record.Send([5, 6, 7, 8], 17);
        var sent = new List<SpiceMessage>();
        while (record.TakeQueued() is { } message) sent.Add(message);
        Assert.Equal([(ushort)RecordClientMessage.Mode, (ushort)RecordClientMessage.StartMark,
            (ushort)RecordClientMessage.Data, (ushort)RecordClientMessage.Data], sent.Select(m => m.Type));
        Assert.Equal((ushort)AudioDataMode.Raw, BinaryPrimitives.ReadUInt16LittleEndian(sent[0].Payload.AsSpan(4)));
        Assert.Equal([17, 0, 0, 0, 5, 6, 7, 8], sent[3].Payload);

        record.Handle(Message(RecordServerMessage.Stop));
        record.Send([1, 2], 30);
        Assert.Null(record.TakeQueued());
    }

    [Fact]
    public void The_record_queue_is_bounded_and_keeps_mode_and_mark()
    {
        var (client, _) = MemoryTransport.Pair();
        var record = new RecordChannel(new ChannelConnection(ChannelType.Record, 0, client, new CapabilitySet()),
            Channel.CreateUnbounded<RecordEvent>().Writer);
        record.Handle(Message(RecordServerMessage.Start, Start(1, AudioStart.FormatS16, 8000, time: false)));
        for (var i = 0; i < 500; i++) record.Send([1, 2], (uint)i);
        var sent = new List<SpiceMessage>();
        while (record.TakeQueued() is { } message) sent.Add(message);
        Assert.Equal(50, sent.Count);
        Assert.Equal((ushort)RecordClientMessage.Mode, sent[0].Type);
        Assert.Equal((ushort)RecordClientMessage.StartMark, sent[1].Type);
    }

    [Fact]
    public void Jitter_buffer_waits_plays_and_rebuffers()
    {
        // 1 kHz mono: soglia 60 campioni, massimo 250.
        var buffer = new JitterBuffer(1, 1000);
        var output = new short[20];
        buffer.Write(Enumerable.Range(1, 40).Select(i => (short)i).ToArray());
        Assert.Equal(0, buffer.Read(output)); // meno di 60 ms: silenzio
        Assert.All(output, s => Assert.Equal(0, s));
        buffer.Write(Enumerable.Range(41, 40).Select(i => (short)i).ToArray());
        Assert.Equal(20, buffer.Read(output));
        Assert.Equal(1, output[0]);
        Assert.Equal(20, output[19]);
        // Si svuota: il resto è silenzio e si conta un esaurimento.
        var big = new short[100];
        Assert.Equal(60, buffer.Read(big));
        Assert.Equal(80, big[59]);
        Assert.Equal(0, big[60]);
        Assert.Equal(1, buffer.Underruns);
    }

    [Fact]
    public void Jitter_buffer_drops_old_audio_beyond_the_limit()
    {
        var buffer = new JitterBuffer(2, 1000); // stereo: soglia 120, massimo 500 campioni
        buffer.Write(Enumerable.Range(0, 600).Select(i => (short)i).ToArray());
        Assert.Equal(120 * 1000 / 2000, buffer.BufferedMilliseconds);
        Assert.Equal(480, buffer.Dropped);
        var output = new short[2];
        buffer.Read(output);
        Assert.Equal([480, 481], output); // i più recenti, canali ancora allineati
    }

    [Fact]
    public void Audio_channels_are_linked_only_when_supported()
    {
        IReadOnlyList<ChannelEntry> offered =
        [
            new((byte)ChannelType.Display, 0), new((byte)ChannelType.Playback, 0), new((byte)ChannelType.Record, 0),
        ];
        Assert.Equal([ChannelType.Display], SpiceSession.Wanted(offered).Select(e => e.Type));
        Assert.Equal([ChannelType.Display, ChannelType.Playback],
            SpiceSession.Wanted(offered, new AudioSupport(true, true, false)).Select(e => e.Type));
        Assert.Equal([ChannelType.Display, ChannelType.Playback, ChannelType.Record],
            SpiceSession.Wanted(offered, new AudioSupport(true, false, true)).Select(e => e.Type));
        Assert.True(PlaybackChannel.Capabilities(opus: true).Contains((int)PlaybackCapability.Opus));
        Assert.False(PlaybackChannel.Capabilities(opus: false).Contains((int)PlaybackCapability.Opus));
    }
}

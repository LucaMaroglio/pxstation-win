using System.Buffers.Binary;
using System.Threading.Channels;
using SpiceKit.Channels;
using SpiceKit.Codecs;
using SpiceKit.Tests.Support;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Channels;

/// <summary>Flussi video del canale display con decoder finti (quelli veri sono in SpiceKit.Windows).</summary>
public class DisplayStreamTests
{
    /// <summary>"JPEG" finto: 1° byte = larghezza, 2° = altezza, 3° = rosso; il resto non conta. Byte 0xFF = dati non validi.</summary>
    private sealed class FakeJpeg : IJpegDecoder
    {
        public PixelBuffer Decode(ReadOnlySpan<byte> jpeg)
        {
            if (jpeg.Length < 3 || jpeg[0] == 0xFF) throw CodecException.Corrupt("finto");
            var pixels = new uint[jpeg[0] * jpeg[1]];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = 0xFF00_0000u | ((uint)jpeg[2] << 16) | (uint)(i / jpeg[0]);
            return new PixelBuffer(jpeg[0], jpeg[1], pixels, hasAlpha: false);
        }
    }

    private sealed class FakeVideo : IVideoDecoder
    {
        public bool Disposed { get; private set; }

        public PixelBuffer? Decode(ReadOnlySpan<byte> accessUnit) => accessUnit.Length == 0 ? null : new FakeJpeg().Decode(accessUnit);

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeDecoders(params VideoCodecType[] codecs) : IMediaDecoders
    {
        public List<FakeVideo> Created { get; } = [];

        public IJpegDecoder? Jpeg { get; } = new FakeJpeg();

        public IReadOnlyList<VideoCodecType> VideoCodecs { get; } = codecs;

        public IVideoDecoder? CreateVideoDecoder(VideoCodecType codec)
        {
            var decoder = new FakeVideo();
            Created.Add(decoder);
            return decoder;
        }
    }

    private static (DisplayChannel Display, FakeDecoders Decoders) NewDisplay(MultimediaClock? clock = null, params VideoCodecType[] codecs)
    {
        var (client, _) = MemoryTransport.Pair();
        var decoders = new FakeDecoders(codecs.Length == 0 ? [VideoCodecType.H264, VideoCodecType.Mjpeg] : codecs);
        var display = new DisplayChannel(new ChannelConnection(ChannelType.Display, 0, client, new CapabilitySet()),
            Channel.CreateUnbounded<DisplayEvent>().Writer, null, decoders, clock);
        var w = new ByteWriter();
        foreach (var value in new uint[] { 0, 8, 4, (uint)SurfaceFormat.Xrgb32, 1 }) w.WriteU32(value);
        display.Handle(new SpiceMessage((ushort)DisplayServerMessage.SurfaceCreate, w.ToArray()));
        return (display, decoders);
    }

    private static void WriteRect(ByteWriter w, SpiceRect rect)
    {
        w.WriteI32(rect.Top);
        w.WriteI32(rect.Left);
        w.WriteI32(rect.Bottom);
        w.WriteI32(rect.Right);
    }

    private static SpiceMessage Create(uint id, VideoCodecType codec, SpiceRect destination, bool topDown = true)
    {
        var w = new ByteWriter();
        w.WriteU32(0); // superficie
        w.WriteU32(id);
        w.WriteU8(topDown ? (byte)1 : (byte)0);
        w.WriteU8((byte)codec);
        w.WriteU64(0);
        foreach (var value in new[] { destination.Width, destination.Height, destination.Width, destination.Height }) w.WriteU32((uint)value);
        WriteRect(w, destination);
        w.WriteU8(0); // nessun ritaglio
        return new SpiceMessage((ushort)DisplayServerMessage.StreamCreate, w.ToArray());
    }

    private static SpiceMessage Data(uint id, uint time, byte[] frame)
    {
        var w = new ByteWriter();
        w.WriteU32(id);
        w.WriteU32(time);
        w.WriteU32((uint)frame.Length);
        w.WriteBytes(frame);
        return new SpiceMessage((ushort)DisplayServerMessage.StreamData, w.ToArray());
    }

    private static SpiceMessage ActivateReport(uint id, uint unique, uint window)
    {
        var w = new ByteWriter();
        foreach (var value in new[] { id, unique, window, 1000u }) w.WriteU32(value);
        return new SpiceMessage((ushort)DisplayServerMessage.StreamActivateReport, w.ToArray());
    }

    [Fact]
    public void Codecs_are_announced_only_when_decodable()
    {
        var none = DisplayChannel.CapabilitiesFor(null);
        Assert.False(none.Contains((int)DisplayCapability.CodecMjpeg));
        Assert.False(none.Contains((int)DisplayCapability.StreamReport));

        var some = DisplayChannel.CapabilitiesFor(new FakeDecoders(VideoCodecType.H264, VideoCodecType.Mjpeg));
        Assert.True(some.Contains((int)DisplayCapability.CodecH264));
        Assert.True(some.Contains((int)DisplayCapability.CodecMjpeg));
        Assert.True(some.Contains((int)DisplayCapability.StreamReport));
        Assert.True(some.Contains((int)DisplayCapability.PrefVideoCodecType));
        Assert.False(some.Contains((int)DisplayCapability.CodecH265));

        var preferred = DisplayClientEncoder.PreferredVideoCodecs([VideoCodecType.H264, VideoCodecType.Mjpeg]);
        Assert.Equal([2, (byte)VideoCodecType.H264, (byte)VideoCodecType.Mjpeg], preferred.Payload);
    }

    [Fact]
    public void Mjpeg_frames_are_drawn_into_the_destination()
    {
        var (display, _) = NewDisplay();
        display.Handle(Create(1, VideoCodecType.Mjpeg, SpiceRect.FromSize(2, 1, 4, 2)));
        display.Handle(Data(1, 0, [4, 2, 0xAA]));
        var screen = display.PrimarySurface()!;
        Assert.Equal(0xFFAA_0000u, screen[2, 1]);
        Assert.Equal(0xFFAA_0001u, screen[5, 2]);
        Assert.Equal(0xFF00_0000u, screen[1, 1]);
        Assert.Equal(0xFF00_0000u, screen[6, 1]);
        Assert.Equal((1, 0), display.VideoFrames);
    }

    [Fact]
    public void Bottom_up_streams_are_flipped_and_scaled_to_the_destination()
    {
        var (display, _) = NewDisplay();
        display.Handle(Create(1, VideoCodecType.H264, SpiceRect.FromSize(0, 0, 8, 4), topDown: false));
        // Fotogramma 4×2 (riga 0 = blu 0, riga 1 = blu 1) capovolto e ingrandito a 8×4.
        display.Handle(Data(1, 0, [4, 2, 0x10]));
        var screen = display.PrimarySurface()!;
        Assert.Equal(1u, screen[0, 0] & 0xFF);
        Assert.Equal(0u, screen[7, 3] & 0xFF);
        Assert.Equal(0x10u, (screen[3, 1] >> 16) & 0xFF);
    }

    [Fact]
    public void Undecodable_frames_are_dropped_not_fatal()
    {
        var (display, _) = NewDisplay();
        display.Handle(Create(1, VideoCodecType.Mjpeg, SpiceRect.FromSize(0, 0, 4, 2)));
        display.Handle(Data(1, 0, [0xFF, 0, 0]));
        display.Handle(Data(1, 0, [1]));
        Assert.Equal((0, 2), display.VideoFrames);
        Assert.Equal(0, display.DecodeErrorCount);
    }

    [Fact]
    public void Unsupported_codecs_tell_the_server_to_fall_back()
    {
        var (display, decoders) = NewDisplay(null, VideoCodecType.Mjpeg);
        display.Handle(Create(3, VideoCodecType.Vp8, SpiceRect.FromSize(0, 0, 4, 2)));
        Assert.Empty(decoders.Created);
        display.Handle(ActivateReport(3, 77, 10));
        var report = Assert.Single(display.PendingReports);
        Assert.Equal((ushort)DisplayClientMessage.StreamReport, report.Type);
        Assert.Equal(77u, BinaryPrimitives.ReadUInt32LittleEndian(report.Payload.AsSpan(4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(report.Payload.AsSpan(16)));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(report.Payload.AsSpan(20)));
    }

    [Fact]
    public void Reports_count_frames_and_drops_in_windows()
    {
        var clock = new MultimediaClock();
        clock.Set(1000);
        var (display, _) = NewDisplay(clock);
        display.Handle(Create(1, VideoCodecType.Mjpeg, SpiceRect.FromSize(0, 0, 4, 2)));
        display.Handle(ActivateReport(1, 5, 3));
        display.Handle(Data(1, 1100, [4, 2, 1]));
        display.Handle(Data(1, 1133, [0xFF, 0, 0]));
        Assert.Empty(display.PendingReports);
        display.Handle(Data(1, 1166, [4, 2, 1]));
        var report = Assert.Single(display.PendingReports).Payload;
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(offset));
        Assert.Equal(new uint[] { 1, 5, 1100, 1166, 3, 1 }, new[] { U32(0), U32(4), U32(8), U32(12), U32(16), U32(20) });
        Assert.True(BinaryPrimitives.ReadInt32LittleEndian(report.AsSpan(24)) > 0, "fotogrammi in anticipo sull'orologio");
        Assert.Equal(uint.MaxValue, U32(28));
    }

    [Fact]
    public void The_stream_count_is_limited_and_decoders_are_released()
    {
        var (display, decoders) = NewDisplay();
        for (uint id = 0; id < DisplayChannel.MaxStreams; id++) display.Handle(Create(id, VideoCodecType.H264, SpiceRect.FromSize(0, 0, 2, 2)));
        // Ricreare un flusso esistente non conta come nuovo.
        display.Handle(Create(0, VideoCodecType.H264, SpiceRect.FromSize(0, 0, 2, 2)));
        Assert.True(decoders.Created[0].Disposed);
        Assert.Throws<ChannelException>(() => display.Handle(Create(1000, VideoCodecType.H264, SpiceRect.FromSize(0, 0, 2, 2))));

        display.Handle(new SpiceMessage((ushort)DisplayServerMessage.StreamDestroyAll, []));
        Assert.Equal(0, display.StreamCount);
        Assert.All(decoders.Created, d => Assert.True(d.Disposed));
    }

    [Fact]
    public void Clip_and_sized_data_move_the_stream()
    {
        var (display, _) = NewDisplay();
        display.Handle(Create(1, VideoCodecType.Mjpeg, SpiceRect.FromSize(0, 0, 4, 2)));
        var w = new ByteWriter();
        w.WriteU32(1);
        w.WriteU32(0);
        w.WriteU32(4);
        w.WriteU32(2);
        WriteRect(w, SpiceRect.FromSize(4, 2, 4, 2));
        w.WriteU32(3);
        w.WriteBytes([4, 2, 0x55]);
        display.Handle(new SpiceMessage((ushort)DisplayServerMessage.StreamDataSized, w.ToArray()));
        var screen = display.PrimarySurface()!;
        Assert.Equal(0xFF00_0000u, screen[0, 0]);
        Assert.Equal(0x55u, (screen[4, 2] >> 16) & 0xFF);
    }

    [Fact]
    public void Jpeg_images_use_the_platform_decoder_and_must_match_the_descriptor()
    {
        var resolver = new ImageResolver(1 << 20, 1 << 20, new FakeJpeg());
        var w = new ByteWriter();
        w.WriteU64(9);
        w.WriteU8((byte)ImageType.Jpeg);
        w.WriteU8(0);
        w.WriteU32(2);
        w.WriteU32(1);
        w.WriteU32(3);
        w.WriteBytes([2, 1, 0x80]);
        var message = (ReadOnlyMemory<byte>)w.ToArray();
        var reader = new ByteReader(message.Span);
        var image = SpiceImage.Decode(message, ref reader);
        Assert.Equal(0xFF80_0000u, resolver.Resolve(image, _ => null)[0, 0]);

        var noJpeg = new ImageResolver(1 << 20, 1 << 20);
        Assert.Throws<ImageResolveException>(() => noJpeg.Resolve(image, _ => null));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x68, 3, 0, 0, 0, 1, 0x65, 4, 5, 0 }, 3, 5)]
    [InlineData(new byte[] { 0, 0, 1, 0x41, 9 }, 1, 1)]
    [InlineData(new byte[] { 1, 2, 3 }, 0, 0)]
    public void Annex_b_splits_nal_units(byte[] data, int count, int keyType)
    {
        var units = AnnexB.NalUnits(data);
        Assert.Equal(count, units.Count);
        if (count == 3)
        {
            Assert.Equal(new byte[] { 0x67, 1, 2 }, data[units[0]]);
            Assert.Equal(new byte[] { 0x68, 3 }, data[units[1]]);
            Assert.Equal(new byte[] { 0x65, 4, 5 }, data[units[2]]);
            Assert.True(AnnexB.IsParameterSet(AnnexB.NalType(data[units[0]], hevc: false), hevc: false));
            Assert.True(AnnexB.HasKeyFrame(data, hevc: false));
            Assert.Equal(keyType, AnnexB.NalType(data[units[2]], hevc: false));
        }
        else if (count == 1)
        {
            Assert.False(AnnexB.HasKeyFrame(data, hevc: false));
        }
    }
}

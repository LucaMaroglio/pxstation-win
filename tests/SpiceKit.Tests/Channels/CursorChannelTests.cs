using System.Threading.Channels;
using SpiceKit.Channels;
using SpiceKit.Codecs;
using SpiceKit.Tests.Support;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Channels;

public class CursorChannelTests
{
    private static (CursorChannel Cursor, Channel<CursorEvent> Events) NewCursor()
    {
        var (client, _) = MemoryTransport.Pair();
        var events = Channel.CreateUnbounded<CursorEvent>();
        return (new CursorChannel(new ChannelConnection(ChannelType.Cursor, 0, client, new CapabilitySet()), events.Writer), events);
    }

    private static List<CursorEvent> Drain(Channel<CursorEvent> events)
    {
        var list = new List<CursorEvent>();
        while (events.Reader.TryRead(out var value)) list.Add(value);
        return list;
    }

    private static void WriteHeader(ByteWriter w, CursorOptions options, ulong unique, CursorType type, ushort width, ushort height,
        ushort hotX = 0, ushort hotY = 0)
    {
        w.WriteU16((ushort)options);
        if (options.HasFlag(CursorOptions.NoShape)) return;
        w.WriteU64(unique);
        w.WriteU8((byte)type);
        w.WriteU16(width);
        w.WriteU16(height);
        w.WriteU16(hotX);
        w.WriteU16(hotY);
    }

    /// <summary>CURSOR_SET con un cursore alpha 2×1: rosso opaco e blu al 50%.</summary>
    private static SpiceMessage SetAlpha(ulong unique, CursorOptions options, short x = 10, short y = 20)
    {
        var w = new ByteWriter();
        w.WriteI16(x);
        w.WriteI16(y);
        w.WriteU8(1);
        WriteHeader(w, options, unique, CursorType.Alpha, 2, 1, hotX: 1);
        if (!options.HasFlag(CursorOptions.FromCache))
        {
            w.WriteBytes([0, 0, 0xFF, 0xFF]);
            w.WriteBytes([0xFF, 0, 0, 0x80]);
        }
        return new SpiceMessage((ushort)CursorServerMessage.Set, w.ToArray());
    }

    [Fact]
    public void Alpha_cursors_are_premultiplied_and_cached()
    {
        var (cursor, events) = NewCursor();
        cursor.Handle(SetAlpha(7, CursorOptions.CacheMe));
        var list = Drain(events);
        var shape = Assert.IsType<CursorEvent.Shape>(list[0]);
        Assert.NotNull(shape.Image);
        Assert.Equal([0xFFFF_0000u, 0x8000_0080u], shape.Image.Image.Pixels);
        Assert.Equal((1, 0), (shape.Image.HotSpotX, shape.Image.HotSpotY));
        Assert.Equal(new CursorEvent.Move(10, 20), list[1]);
        Assert.Equal(new CursorEvent.Visible(true), list[2]);

        cursor.Handle(SetAlpha(7, CursorOptions.FromCache));
        var cached = Assert.IsType<CursorEvent.Shape>(Drain(events)[0]);
        Assert.Same(shape.Image, cached.Image);

        cursor.Handle(new SpiceMessage((ushort)CursorServerMessage.InvalAll, []));
        cursor.Handle(SetAlpha(7, CursorOptions.FromCache));
        Assert.Null(Assert.IsType<CursorEvent.Shape>(Drain(events)[0]).Image);
    }

    [Fact]
    public void Mono_cursors_use_and_xor_masks()
    {
        // 2×2: AND = [10 00 | 00 00], XOR = [00 00 | 40 00] → trasparente, nero / nero, bianco.
        var header = new CursorHeader(1, (byte)CursorType.Mono, 2, 2, 0, 0);
        var image = CursorDecoder.Decode(header, [0x80, 0x00, 0x00, 0x40]);
        Assert.NotNull(image);
        Assert.Equal([0u, 0xFF00_0000, 0xFF00_0000, 0xFFFF_FFFF], image.Image.Pixels);
    }

    [Fact]
    public void Hostile_shapes_are_rejected()
    {
        Assert.Null(CursorDecoder.Decode(new CursorHeader(1, (byte)CursorType.Alpha, 513, 1, 0, 0), new byte[513 * 4]));
        Assert.Null(CursorDecoder.Decode(new CursorHeader(1, (byte)CursorType.Alpha, 4, 4, 0, 0), new byte[63]));
        Assert.Null(CursorDecoder.Decode(new CursorHeader(1, (byte)CursorType.Color8, 4, 4, 0, 0), new byte[1024]));
        // Punto attivo fuori dall'immagine: riportato dentro.
        var image = CursorDecoder.Decode(new CursorHeader(1, (byte)CursorType.Alpha, 1, 1, 900, 900), new byte[4]);
        Assert.Equal((0, 0), (image!.HotSpotX, image.HotSpotY));
    }

    [Fact]
    public void The_cache_is_bounded()
    {
        var (cursor, events) = NewCursor();
        for (ulong id = 0; id < CursorChannel.MaxCacheEntries + 10; id++) cursor.Handle(SetAlpha(id, CursorOptions.CacheMe));
        Drain(events);
        Assert.True(cursor.CachedShapes <= CursorChannel.MaxCacheEntries);
    }

    [Fact]
    public void Moves_hides_and_malformed_messages()
    {
        var (cursor, events) = NewCursor();
        cursor.Handle(new SpiceMessage((ushort)CursorServerMessage.Move, [0xFE, 0xFF, 5, 0]));
        cursor.Handle(new SpiceMessage((ushort)CursorServerMessage.Hide, []));
        cursor.Handle(new SpiceMessage((ushort)CursorServerMessage.Set, [1, 2, 3]));
        Assert.Equal([new CursorEvent.Move(-2, 5), new CursorEvent.Visible(false)], Drain(events));
        Assert.Equal(1, cursor.DecodeErrorCount);
    }

    [Fact]
    public void Hidden_shape_flag_yields_no_image()
    {
        var (cursor, events) = NewCursor();
        var w = new ByteWriter();
        w.WriteI16(0);
        w.WriteI16(0);
        w.WriteU8(0);
        WriteHeader(w, CursorOptions.NoShape, 0, CursorType.Alpha, 0, 0);
        cursor.Handle(new SpiceMessage((ushort)CursorServerMessage.Set, w.ToArray()));
        Assert.Null(Assert.IsType<CursorEvent.Shape>(Drain(events)[0]).Image);
    }
}

using System.Buffers.Binary;
using System.Threading.Channels;
using SpiceKit.Channels;
using SpiceKit.Codecs;
using SpiceKit.Tests.Support;
using SpiceKit.Transport;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Channels;

/// <summary>
/// Riproduce flussi del canale display catturati da uno spice-server reale (Fixtures/display),
/// come <c>DisplayReplayTests.swift</c>: LZ è il riferimento verificato; LZ4 e GLZ (con il
/// dizionario fra immagini) devono dare gli stessi pixel.
/// </summary>
public class DisplayChannelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Messaggi di una cattura (sequenza di mini-header e corpi).</summary>
    private static List<SpiceMessage> Capture(string name)
    {
        var bytes = File.ReadAllBytes(Fixtures.PathOf("display", name));
        var messages = new List<SpiceMessage>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var header = MiniHeader.Decode(bytes.AsSpan(offset));
            offset += MiniHeader.WireSize;
            messages.Add(new SpiceMessage(header.Type, bytes.AsSpan(offset, (int)header.Size).ToArray()));
            offset += (int)header.Size;
        }
        return messages;
    }

    private static (DisplayChannel Display, Channel<DisplayEvent> Events) NewDisplay(long budget = DisplayChannel.MaxSurfacePixels)
    {
        var (client, _) = MemoryTransport.Pair();
        var connection = new ChannelConnection(ChannelType.Display, 0, client, new CapabilitySet());
        var events = Channel.CreateUnbounded<DisplayEvent>();
        return (new DisplayChannel(connection, events.Writer) { SurfacePixelBudget = budget }, events);
    }

    private static (PixelBuffer Screen, int Errors) Replay(string name)
    {
        var (display, _) = NewDisplay();
        foreach (var message in Capture(name)) display.Handle(message);
        return (display.PrimarySurface() ?? throw new InvalidOperationException("nessuna superficie primaria"), display.DecodeErrorCount);
    }

    [Theory]
    [InlineData("lz4.bin")]
    [InlineData("glz.bin")]
    [InlineData("auto-glz.bin")]
    public void Captures_match_the_lz_baseline(string name)
    {
        var (baseline, baselineErrors) = Replay("lz.bin");
        Assert.Equal(0, baselineErrors);
        Assert.True(baseline.Width > 0 && baseline.Height > 0);
        Assert.True(baseline.Pixels.Distinct().Count() > 16, "la cattura deve avere contenuto vero");

        var (screen, errors) = Replay(name);
        Assert.Equal(0, errors);
        Assert.Equal(baseline.Width, screen.Width);
        Assert.Equal(baseline.Height, screen.Height);
        var differing = screen.Pixels.Zip(baseline.Pixels).Count(p => (p.First & 0x00FF_FFFF) != (p.Second & 0x00FF_FFFF));
        Assert.Equal(0, differing);
    }

    [Fact]
    public async Task Run_sends_init_and_preferences_then_publishes_updates()
    {
        var (client, server) = MemoryTransport.Pair();
        var caps = CapabilitySet.FromBits((int)DisplayCapability.PrefCompression);
        var connection = new ChannelConnection(ChannelType.Display, 0, client, caps);
        var events = Channel.CreateUnbounded<DisplayEvent>();
        var credit = new DisplayUpdateCredit(limit: 1);
        var display = new DisplayChannel(connection, events.Writer, credit);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = display.RunAsync(stop.Token);

        var init = await ReceiveAsync(server);
        Assert.Equal((ushort)DisplayClientMessage.Init, init.Type);
        Assert.Equal(14, init.Payload.Length);
        Assert.Equal(DisplayChannel.GlzWindowSize, BinaryPrimitives.ReadInt32LittleEndian(init.Payload.AsSpan(10)));
        var preferred = await ReceiveAsync(server);
        Assert.Equal((ushort)DisplayClientMessage.PreferredCompression, preferred.Type);
        Assert.Equal([(byte)ImageCompression.AutoGlz], preferred.Payload);

        await server.SendAsync(SurfaceCreateMessage(0, 4, 2, primary: true).Encode(), Ct);
        await server.SendAsync(FillMessage(0, SpiceRect.FromSize(1, 0, 2, 1), 0x00FF_0000).Encode(), Ct);

        var created = Assert.IsType<DisplayEvent.PrimarySurfaceCreated>(await events.Reader.ReadAsync(Ct));
        Assert.Equal((4, 2), (created.Width, created.Height));
        var first = Assert.IsType<DisplayEvent.Update>(await events.Reader.ReadAsync(Ct));
        Assert.Equal(SpiceRect.FromSize(0, 0, 4, 2), first.Surface.Rect);

        // Con il credito esaurito il riempimento resta in attesa; dopo Consumed arriva con i suoi pixel.
        credit.Consumed();
        var second = Assert.IsType<DisplayEvent.Update>(await events.Reader.ReadAsync(Ct));
        Assert.Equal(SpiceRect.FromSize(1, 0, 2, 1), second.Surface.Rect);
        Assert.Equal([0xFFFF_0000u, 0xFFFF_0000], second.Surface.Pixels);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public void Oversized_surfaces_close_the_channel()
    {
        var (display, _) = NewDisplay();
        var error = Assert.Throws<ChannelException>(() => display.Handle(SurfaceCreateMessage(0, 16_385, 10, primary: true)));
        Assert.Equal(ChannelErrorKind.Malformed, error.Kind);
    }

    [Fact]
    public void The_aggregate_surface_budget_is_enforced()
    {
        // Budget ridotto a 100 pixel per non allocare i 512 MiB reali.
        var (display, _) = NewDisplay(budget: 100);
        display.Handle(SurfaceCreateMessage(1, 10, 10, primary: false));
        Assert.Throws<ChannelException>(() => display.Handle(SurfaceCreateMessage(2, 1, 1, primary: false)));
        // Sostituire la stessa superficie non conta due volte; distruggerla libera il budget.
        display.Handle(SurfaceCreateMessage(1, 10, 10, primary: false));
        display.Handle(new SpiceMessage((ushort)DisplayServerMessage.SurfaceDestroy, [1, 0, 0, 0]));
        display.Handle(SurfaceCreateMessage(2, 10, 10, primary: false));
    }

    [Fact]
    public void The_surface_count_is_limited()
    {
        var (display, _) = NewDisplay();
        for (uint id = 0; id < DisplayChannel.MaxSurfaces; id++) display.Handle(SurfaceCreateMessage(id, 1, 1, primary: false));
        Assert.Throws<ChannelException>(() => display.Handle(SurfaceCreateMessage(5000, 1, 1, primary: false)));
    }

    [Fact]
    public void Malformed_draws_are_counted_not_fatal()
    {
        var (display, _) = NewDisplay();
        display.Handle(SurfaceCreateMessage(0, 4, 4, primary: true));
        display.Handle(new SpiceMessage((ushort)DisplayServerMessage.DrawCopy, [1, 2, 3]));
        // DRAW_COPY con un'immagine "dalla cache" mai vista.
        display.Handle(CopyFromCacheMessage(0, 42));
        Assert.Equal(2, display.DecodeErrorCount);
        Assert.NotNull(display.PrimarySurface());
    }

    [Fact]
    public void Draw_copy_decodes_an_inline_bitmap()
    {
        var (display, _) = NewDisplay();
        display.Handle(SurfaceCreateMessage(0, 4, 4, primary: true));
        display.Handle(CopyBitmapMessage(0, SpiceRect.FromSize(1, 1, 2, 1), [0x10, 0x20, 0x30, 0, 0x40, 0x50, 0x60, 0]));
        var screen = display.PrimarySurface()!;
        Assert.Equal(0, display.DecodeErrorCount);
        Assert.Equal(0xFF30_2010u, screen[1, 1]);
        Assert.Equal(0xFF60_5040u, screen[2, 1]);
        Assert.Equal(0xFF00_0000u, screen[0, 0]);
    }

    private static async Task<SpiceMessage> ReceiveAsync(MemoryTransport server)
    {
        var header = MiniHeader.Decode(await server.ReceiveAsync(MiniHeader.WireSize, Ct));
        return new SpiceMessage(header.Type, await server.ReceiveAsync((int)header.Size, Ct));
    }

    private static SpiceMessage SurfaceCreateMessage(uint id, uint width, uint height, bool primary)
    {
        var w = new ByteWriter();
        w.WriteU32(id);
        w.WriteU32(width);
        w.WriteU32(height);
        w.WriteU32((uint)SurfaceFormat.Xrgb32);
        w.WriteU32(primary ? 1u : 0);
        return new SpiceMessage((ushort)DisplayServerMessage.SurfaceCreate, w.ToArray());
    }

    private static void WriteBase(ByteWriter w, uint surface, SpiceRect box)
    {
        w.WriteU32(surface);
        w.WriteI32(box.Top);
        w.WriteI32(box.Left);
        w.WriteI32(box.Bottom);
        w.WriteI32(box.Right);
        w.WriteU8(0); // nessun ritaglio
    }

    private static SpiceMessage FillMessage(uint surface, SpiceRect box, uint color)
    {
        var w = new ByteWriter();
        WriteBase(w, surface, box);
        w.WriteU8(1); // pennello pieno
        w.WriteU32(color);
        w.WriteU16((ushort)Ropd.OpPut);
        w.WriteU8(0); // maschera: flag, posizione, puntatore nullo
        w.WriteI32(0);
        w.WriteI32(0);
        w.WriteU32(0);
        return new SpiceMessage((ushort)DisplayServerMessage.DrawFill, w.ToArray());
    }

    /// <summary>DRAW_COPY con l'immagine subito dopo il corpo (offset fisso).</summary>
    private static SpiceMessage CopyMessage(uint surface, SpiceRect box, SpiceRect area, Action<ByteWriter> image)
    {
        var w = new ByteWriter();
        WriteBase(w, surface, box);
        const int bodySize = 4 + 16 + 1 + 4 + 16 + 2 + 1 + 1 + 8 + 4;
        w.WriteU32(bodySize); // puntatore all'immagine
        w.WriteI32(area.Top);
        w.WriteI32(area.Left);
        w.WriteI32(area.Bottom);
        w.WriteI32(area.Right);
        w.WriteU16((ushort)Ropd.OpPut);
        w.WriteU8((byte)ScaleMode.Nearest);
        w.WriteU8(0);
        w.WriteI32(0);
        w.WriteI32(0);
        w.WriteU32(0);
        Assert.Equal(bodySize, w.Count);
        image(w);
        return new SpiceMessage((ushort)DisplayServerMessage.DrawCopy, w.ToArray());
    }

    private static SpiceMessage CopyFromCacheMessage(uint surface, ulong id) =>
        CopyMessage(surface, SpiceRect.FromSize(0, 0, 1, 1), SpiceRect.FromSize(0, 0, 1, 1), w =>
        {
            w.WriteU64(id);
            w.WriteU8((byte)ImageType.FromCache);
            w.WriteU8(0);
            w.WriteU32(1);
            w.WriteU32(1);
        });

    private static SpiceMessage CopyBitmapMessage(uint surface, SpiceRect box, byte[] bgrx) =>
        CopyMessage(surface, box, SpiceRect.FromSize(0, 0, box.Width, box.Height), w =>
        {
            w.WriteU64(7);
            w.WriteU8((byte)ImageType.Bitmap);
            w.WriteU8(0);
            w.WriteU32((uint)box.Width);
            w.WriteU32((uint)box.Height);
            w.WriteU8((byte)BitmapFormat.ThirtyTwoBit);
            w.WriteU8((byte)BitmapOptions.TopDown);
            w.WriteU32((uint)box.Width);
            w.WriteU32((uint)box.Height);
            w.WriteU32((uint)box.Width * 4);
            w.WriteU32(0); // nessuna palette
            w.WriteBytes(bgrx);
        });
}

using SpiceKit.Codecs;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Codecs;

/// <summary>Porting di <c>CanvasTests.swift</c>.</summary>
public class CanvasTests
{
    private static PixelBuffer Surface(int width = 8, int height = 6) => new(width, height, 0xFF00_0000);

    private static SpiceClip Clip(params SpiceRect[] rects) => new(rects);

    [Fact]
    public void Fill_clips_to_surface_and_clip_rects()
    {
        var s = Surface();
        var dirty = s.Fill(SpiceRect.FromSize(-2, -2, 6, 6),
            Clip(SpiceRect.FromSize(1, 1, 2, 2), SpiceRect.FromSize(0, 3, 1, 1)), new FillSource.Color(0x00FF_0000));
        Assert.Equal(SpiceRect.FromSize(0, 1, 3, 3), dirty);
        Assert.Equal(0xFFFF_0000, s[1, 1]);
        Assert.Equal(0xFFFF_0000, s[2, 2]);
        Assert.Equal(0xFFFF_0000, s[0, 3]);
        Assert.Equal(0xFF00_0000, s[0, 0]);
        Assert.Equal(0xFF00_0000, s[3, 3]);
    }

    [Fact]
    public void Rop_xor_and_invert()
    {
        var s = Surface(2, 1);
        s.Fill(s.Bounds, SpiceClip.None, new FillSource.Color(0x0000_00FF));
        s.Fill(s.Bounds, SpiceClip.None, new FillSource.Color(0x0000_000F), Ropd.OpXor);
        Assert.Equal(0xFF00_00F0, s[0, 0]);
        s.ApplyUnary(s.Bounds, SpiceClip.None, Ropd.OpInvers);
        Assert.Equal(0xFFFF_FF0F, s[1, 0]);
        s.ApplyUnary(s.Bounds, SpiceClip.None, Ropd.OpBlackness);
        Assert.Equal(0xFF00_0000, s[1, 0]);
    }

    [Fact]
    public void Copy_with_scaling()
    {
        var source = new PixelBuffer(2, 1);
        source[0, 0] = 0xFF11_1111;
        source[1, 0] = 0xFF22_2222;
        var s = Surface(4, 2);
        s.Copy(source, source.Bounds, SpiceRect.FromSize(0, 0, 4, 2), SpiceClip.None);
        Assert.Equal(0xFF11_1111, s[0, 0]);
        Assert.Equal(0xFF11_1111, s[1, 1]);
        Assert.Equal(0xFF22_2222, s[2, 0]);
        Assert.Equal(0xFF22_2222, s[3, 1]);
    }

    [Fact]
    public void Copy_rejects_source_areas_outside_the_image()
    {
        var source = new PixelBuffer(2, 2);
        var s = Surface(4, 4);
        Assert.True(s.Copy(source, SpiceRect.FromSize(1, 1, 5, 5), s.Bounds, SpiceClip.None).IsEmpty);
        Assert.True(s.Copy(source, new SpiceRect(int.MinValue, 0, 1, 1), s.Bounds, SpiceClip.None).IsEmpty);
    }

    [Fact]
    public void Copy_bits_handles_overlap_like_scrolling()
    {
        var s = Surface(1, 5);
        for (var y = 0; y < 5; y++) s[0, y] = 0xFF00_0000 | (uint)y;
        // Scorrimento in su di uno: righe 1…4 → 0…3.
        s.CopyBits(new SpicePoint(0, 1), SpiceRect.FromSize(0, 0, 1, 4), SpiceClip.None);
        Assert.Equal([1u, 2, 3, 4], Enumerable.Range(0, 4).Select(y => s[0, y] & 0xFF));
        // Scorrimento in giù di due.
        s.CopyBits(new SpicePoint(0, 0), SpiceRect.FromSize(0, 2, 1, 3), SpiceClip.None);
        Assert.Equal([1u, 2, 1, 2, 3], Enumerable.Range(0, 5).Select(y => s[0, y] & 0xFF));
    }

    [Fact]
    public void Copy_bits_with_hostile_coordinates_is_harmless()
    {
        var s = Surface(4, 4);
        Assert.True(s.CopyBits(new SpicePoint(int.MaxValue, int.MinValue), s.Bounds, SpiceClip.None).IsEmpty);
    }

    [Fact]
    public void Transparent_skips_the_color_key()
    {
        var source = new PixelBuffer(2, 1);
        source[0, 0] = 0xFF00_FF00;
        source[1, 0] = 0xFF12_3456;
        var s = Surface(2, 1);
        s.TransparentCopy(source, source.Bounds, s.Bounds, SpiceClip.None, 0x00FF00);
        Assert.Equal(0xFF00_0000, s[0, 0]);
        Assert.Equal(0xFF12_3456, s[1, 0]);
    }

    [Fact]
    public void Alpha_blend_half()
    {
        var source = new PixelBuffer(1, 1, 0xFFFF_FFFF, hasAlpha: true);
        var s = Surface(1, 1);
        s.AlphaBlend(source, source.Bounds, s.Bounds, SpiceClip.None, 128, sourceHasAlpha: true);
        var red = (s[0, 0] >> 16) & 0xFF;
        Assert.InRange(red, 127u, 129u);
    }

    [Fact]
    public void Mask_limits_fill()
    {
        var s = Surface(2, 1);
        var mask = new DrawMask(2, 1, [true, false], false, new SpicePoint(0, 0));
        s.Fill(s.Bounds, SpiceClip.None, new FillSource.Color(0xFFFFFF), mask: mask);
        Assert.Equal(0xFFFF_FFFF, s[0, 0]);
        Assert.Equal(0xFF00_0000, s[1, 0]);
    }

    [Fact]
    public void Bitmap_formats()
    {
        var palette = new Palette(1, [0x000000, 0xFF0000, 0x00FF00, 0x0000FF]);
        // 2x2 a 8 bit dal basso: la prima riga memorizzata è quella in basso.
        var bitmap = new BitmapData(BitmapFormat.EightBit, BitmapOptions.None, 2, 2, 4, new PaletteReference.Inline(palette),
            new byte[] { 1, 2, 0, 0, 3, 0, 0, 0 });
        var image = BitmapDecoder.Decode(bitmap, palette);
        Assert.Equal(0xFF00_00FF, image[0, 0]); // in alto a sinistra = seconda riga memorizzata, indice 3
        Assert.Equal(0xFFFF_0000, image[0, 1]);
        Assert.Equal(0xFF00_FF00, image[1, 1]);

        var rgb16 = new BitmapData(BitmapFormat.SixteenBit, BitmapOptions.TopDown, 1, 1, 2, PaletteReference.Absent,
            new byte[] { 0x00, 0x7C }); // rosso puro in x555
        Assert.Equal(0xFFFF_0000, BitmapDecoder.Decode(rgb16, null)[0, 0]);

        var mono = new BitmapData(BitmapFormat.OneBitBE, BitmapOptions.TopDown, 3, 1, 1, new PaletteReference.Inline(palette),
            new byte[] { 0b1010_0000 });
        Assert.Equal([0xFFFF_0000, 0xFF00_0000, 0xFFFF_0000], BitmapDecoder.Decode(mono, palette).Pixels);
    }

    [Fact]
    public void Bitmap_rejects_short_data()
    {
        var bitmap = new BitmapData(BitmapFormat.ThirtyTwoBit, BitmapOptions.None, 4, 4, 16, PaletteReference.Absent, new byte[3]);
        Assert.Equal(CodecErrorKind.Truncated, Assert.Throws<CodecException>(() => BitmapDecoder.Decode(bitmap, null)).Kind);
    }

    [Fact]
    public void Lz4_overlapping_match()
    {
        // "a" letterale, poi rimando con offset 1 e lunghezza 9 → dieci "a" e il letterale finale "b".
        byte[] block = [0x15, 0x61, 0x01, 0x00, 0x10, 0x62];
        Assert.Equal("aaaaaaaaaab"u8.ToArray(), Lz4Decoder.Decompress(block, 11));
        for (var length = 0; length < 40; length++)
        {
            try
            {
                Lz4Decoder.Decompress(Enumerable.Repeat((byte)0xFF, length).ToArray(), 100);
            }
            catch (CodecException)
            {
            }
        }
    }
}

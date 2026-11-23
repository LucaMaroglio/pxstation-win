using SpiceKit.Codecs;
using SpiceKit.Tests.Support;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Codecs;

/// <summary>
/// Fixture "golden" (porting di <c>GoldenCodecTests.swift</c>): immagini sintetiche codificate
/// con il codificatore SPICE di riferimento, confrontate con l'hash FNV-1a dei pixel attesi.
/// </summary>
public class GoldenCodecTests
{
    public static TheoryData<string> Quic => [.. Fixtures.Codecs.Where(f => f.Codec == "quic").Select(f => f.Name)];

    public static TheoryData<string> Lz => [.. Fixtures.Codecs.Where(f => f.Codec == "lz").Select(f => f.Name)];

    public static TheoryData<string> All => [.. Fixtures.Codecs.Where(f => f.Codec is "quic" or "lz").Select(f => f.Name)];

    private static CodecFixture Find(string name) => Fixtures.Codecs.Single(f => f.Name == name);

    private static Palette? PaletteOf(CodecFixture fixture) => fixture.Palette is { } entries ? new Palette(1, entries) : null;

    [Theory]
    [MemberData(nameof(Quic))]
    public void Quic_matches_the_reference(string name)
    {
        var fixture = Find(name);
        var image = QuicDecoder.Decode(fixture.Payload());
        Assert.Equal(fixture.Width, image.Width);
        Assert.Equal(fixture.Height, image.Height);
        Assert.Equal(fixture.Fnv, Fixtures.Fnv1a(image.Pixels));
    }

    [Theory]
    [MemberData(nameof(Lz))]
    public void Lz_matches_the_reference(string name)
    {
        var fixture = Find(name);
        var image = LzDecoder.Decode(fixture.Payload(), PaletteOf(fixture));
        Assert.Equal(fixture.Width, image.Width);
        Assert.Equal(fixture.Height, image.Height);
        Assert.Equal(fixture.Fnv, Fixtures.Fnv1a(image.Pixels));
    }

    /// <summary>Flussi troncati o con bit alterati falliscono in modo pulito: mai crash né blocchi.</summary>
    [Theory]
    [MemberData(nameof(All))]
    public void Corrupted_streams_fail_cleanly(string name)
    {
        var fixture = Find(name);
        var original = fixture.Payload();
        var palette = PaletteOf(fixture);
        var random = new Random(name.GetHashCode(StringComparison.Ordinal));
        for (var trial = 0; trial < 40; trial++)
        {
            var bytes = (byte[])original.Clone();
            if (trial % 2 == 0)
            {
                bytes = bytes[..random.Next(0, Math.Max(1, bytes.Length))];
            }
            else
            {
                for (var k = 0; k < 4; k++) bytes[random.Next(bytes.Length)] ^= (byte)random.Next(1, 256);
            }
            try
            {
                if (fixture.Codec == "quic") QuicDecoder.Decode(bytes);
                else LzDecoder.Decode(bytes, palette);
            }
            catch (CodecException)
            {
            }
        }
    }

    [Fact]
    public void Quic_rejects_a_bad_magic()
    {
        var error = Assert.Throws<CodecException>(() => QuicDecoder.Decode(new byte[8]));
        Assert.Equal(CodecErrorKind.BadMagic, error.Kind);
    }

    [Fact]
    public void Lz_requires_a_palette_for_plt()
    {
        var error = Assert.Throws<CodecException>(() => LzDecoder.Decode(Find("lz-plt8-desktop").Payload()));
        Assert.Equal(CodecErrorKind.MissingPalette, error.Kind);
    }

    [Fact]
    public void Lz4_block_matches_the_reference()
    {
        var expected = File.ReadAllBytes(Fixtures.PathOf("codecs", "lz4-block.expected"));
        var output = Lz4Decoder.Decompress(File.ReadAllBytes(Fixtures.PathOf("codecs", "lz4-block.bin")), expected.Length);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Lz4_rejects_offsets_before_the_start()
    {
        // Token: 1 letterale, poi rimando con offset 5 quando c'è un solo byte.
        byte[] block = [0x10, 0x41, 0x05, 0x00];
        Assert.Throws<CodecException>(() => Lz4Decoder.Decompress(block, 10));
    }

    [Fact]
    public void Limits_reject_oversized_images()
    {
        Assert.Throws<CodecException>(() => CodecLimits.Check(16_385, 1));
        Assert.Throws<CodecException>(() => CodecLimits.Check(0, 10));
        Assert.Throws<CodecException>(() => CodecLimits.Check(16_384, 16_384)); // 256 Mi pixel > 64 Mi
    }
}

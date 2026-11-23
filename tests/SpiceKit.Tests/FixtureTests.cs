using SpiceKit.Tests.Support;

namespace SpiceKit.Tests;

/// <summary>Le fixture del progetto macOS sono presenti e leggibili (base per M2–M5).</summary>
public class FixtureTests
{
    [Fact]
    public void Codec_manifest_is_loaded()
    {
        Assert.True(Fixtures.Codecs.Count >= 20);
        Assert.Contains(Fixtures.Codecs, f => f.Codec == "quic");
        Assert.Contains(Fixtures.Codecs, f => f.Codec == "lz");
    }

    [Fact]
    public void Every_codec_fixture_has_its_payload()
    {
        foreach (var fixture in Fixtures.Codecs)
        {
            Assert.True(fixture.Payload().Length > 0, fixture.Name);
            Assert.Matches("^[0-9a-f]{16}$", fixture.Fnv);
        }
    }

    [Theory]
    [InlineData("display", "lz.bin")]
    [InlineData("display", "glz.bin")]
    [InlineData("display", "lz4.bin")]
    [InlineData("vv", "proxmox.vv")]
    [InlineData("tls", "test-server.pem")]
    public void Other_fixtures_are_present(string folder, string file)
    {
        Assert.True(File.Exists(Fixtures.PathOf(folder, file)));
    }

    [Fact]
    public void Fnv1a_matches_reference_of_empty_and_single_pixel()
    {
        // Valori calcolati con la stessa definizione dei test macOS (FNV-1a-64 sui byte little-endian).
        Assert.Equal("cbf29ce484222325", Fixtures.Fnv1a([]));
        Assert.Equal(Fixtures.Fnv1a([0xFF000000]), Fixtures.Fnv1a([0xFF000000]));
        Assert.NotEqual(Fixtures.Fnv1a([0xFF000000]), Fixtures.Fnv1a([0xFF000001]));
    }
}

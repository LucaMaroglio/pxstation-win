using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpiceKit.Tests.Support;

/// <summary>Una voce di <c>Fixtures/codecs/manifest.json</c> (stesso formato del progetto macOS).</summary>
/// <remarks>
/// <c>Fnv</c> è FNV-1a-64 sui pixel BGRA attesi (<c>0xAARRGGBB</c>, byte little-endian,
/// dall'alto in basso). Vedi <c>Fixtures/README.md</c>.
/// </remarks>
public sealed record CodecFixture(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("codec")] string Codec,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("fnv")] string Fnv,
    [property: JsonPropertyName("palette")] uint[]? Palette)
{
    public override string ToString() => Name;

    public byte[] Payload() => File.ReadAllBytes(Fixtures.PathOf("codecs", $"{Name}.bin"));
}

public static class Fixtures
{
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string PathOf(params string[] parts) => Path.Combine([Directory, .. parts]);

    private static readonly Lazy<IReadOnlyList<CodecFixture>> _codecs = new(() =>
        JsonSerializer.Deserialize<List<CodecFixture>>(File.ReadAllText(PathOf("codecs", "manifest.json")))
        ?? []);

    public static IReadOnlyList<CodecFixture> Codecs => _codecs.Value;

    /// <summary>FNV-1a-64 dei pixel, come <c>fnv1a</c> nei test macOS.</summary>
    public static string Fnv1a(ReadOnlySpan<uint> pixels)
    {
        var hash = 0xCBF2_9CE4_8422_2325UL;
        foreach (var pixel in pixels)
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                hash ^= (pixel >> shift) & 0xFF;
                hash = unchecked(hash * 0x0000_0100_0000_01B3UL);
            }
        }

        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}

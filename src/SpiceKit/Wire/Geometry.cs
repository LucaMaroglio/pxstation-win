using System.Globalization;

namespace SpiceKit.Wire;

/// <summary><c>SpicePoint</c> (spice.proto <c>Point</c>): due int32.</summary>
public readonly record struct SpicePoint(int X, int Y)
{
    internal static SpicePoint Decode(ref ByteReader reader) => new(reader.ReadI32(), reader.ReadI32());
}

/// <summary>
/// <c>SpiceRect</c> (spice.proto <c>Rect</c>): sul filo l'ordine è top, left, bottom, right;
/// <c>Right</c> e <c>Bottom</c> sono esclusi.
/// </summary>
public readonly record struct SpiceRect(int Left, int Top, int Right, int Bottom)
{
    public static SpiceRect Empty => default;

    /// <summary>Rettangolo da origine e dimensioni, con saturazione a int32.</summary>
    public static SpiceRect FromSize(long x, long y, long width, long height) =>
        new(Clamp(x), Clamp(y), Clamp(x + width), Clamp(y + height));

    public int Width => (int)Math.Max(0, (long)Right - Left);

    public int Height => (int)Math.Max(0, (long)Bottom - Top);

    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public SpiceRect Intersect(SpiceRect other)
    {
        var result = new SpiceRect(Math.Max(Left, other.Left), Math.Max(Top, other.Top),
            Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        return result.IsEmpty ? Empty : result;
    }

    /// <summary>Il più piccolo rettangolo che contiene entrambi (quello vuoto è l'elemento neutro).</summary>
    public SpiceRect Union(SpiceRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new SpiceRect(Math.Min(Left, other.Left), Math.Min(Top, other.Top),
            Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    /// <summary>Spostamento con saturazione (sicuro con coordinate ostili).</summary>
    public SpiceRect Offset(long dx, long dy) =>
        new(Clamp(Left + dx), Clamp(Top + dy), Clamp(Right + dx), Clamp(Bottom + dy));

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({Left},{Top} {Width}x{Height})");

    internal static SpiceRect Decode(ref ByteReader reader)
    {
        var top = reader.ReadI32();
        var left = reader.ReadI32();
        var bottom = reader.ReadI32();
        var right = reader.ReadI32();
        return new SpiceRect(left, top, right, bottom);
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}

/// <summary><c>SpiceClip</c>: nessuno o un elenco di rettangoli.</summary>
public sealed record SpiceClip(IReadOnlyList<SpiceRect>? Rects)
{
    /// <summary>Un server ostile potrebbe dichiarare miliardi di rettangoli.</summary>
    internal const uint MaxRects = 65_536;

    public static SpiceClip None { get; } = new((IReadOnlyList<SpiceRect>?)null);

    internal static SpiceClip Decode(ref ByteReader reader)
    {
        var type = reader.ReadU8();
        switch (type)
        {
            case 0:
                return None;
            case 1:
                var count = reader.ReadU32();
                if (count > MaxRects || (long)count * 16 > reader.Remaining)
                {
                    throw WireException.LimitExceeded($"rettangoli di ritaglio: {count}");
                }
                var rects = new SpiceRect[count];
                for (var i = 0; i < rects.Length; i++) rects[i] = SpiceRect.Decode(ref reader);
                return new SpiceClip(rects);
            default:
                throw WireException.InvalidValue($"tipo di ritaglio {type}");
        }
    }
}

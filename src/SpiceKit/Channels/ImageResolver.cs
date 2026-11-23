using System.IO.Compression;
using SpiceKit.Codecs;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>Un'immagine non si può risolvere (cache, superficie o formato).</summary>
public sealed class ImageResolveException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Decodifica le <c>SpiceImage</c> e mantiene le cache di pixmap e palette che il server gestisce
/// a distanza (<c>CACHE_ME</c>, <c>FROM_CACHE</c>, <c>INVAL_*</c>). Porting di
/// <c>SpiceChannels/ImageResolver.swift</c>. Non thread-safe: un'istanza per canale display.
/// </summary>
public sealed class ImageResolver(long pixmapCacheBudget, int glzWindowPixels)
{
    internal const int MaxCachedPalettes = 4096;

    /// <summary>1 Mi colori = 4 MiB fra tutte le palette in cache.</summary>
    internal const int MaxPaletteEntries = 1 << 20;

    private readonly Dictionary<ulong, PixelBuffer> _pixmaps = [];
    private readonly Dictionary<ulong, Palette> _palettes = [];
    private readonly GlzDecoder _glz = new(glzWindowPixels);
    private long _cachedPixels;
    private int _paletteEntries;

    /// <summary>Budget in pixel annunciato al server in <c>SPICE_MSGC_DISPLAY_INIT</c>.</summary>
    public long PixmapCacheBudget { get; } = pixmapCacheBudget;

    public int CachedPixmapCount => _pixmaps.Count;

    public int CachedPaletteCount => _palettes.Count;

    /// <summary>Pixel di <paramref name="image"/>; <paramref name="surface"/> fornisce le altre superfici per le immagini <c>SURFACE</c>.</summary>
    public PixelBuffer Resolve(SpiceImage image, Func<uint, PixelBuffer?> surface)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(surface);
        var descriptor = image.Descriptor;
        PixelBuffer pixels;
        try
        {
            switch (image.Payload)
            {
                case ImagePayload.FromCache:
                    return _pixmaps.TryGetValue(descriptor.Id, out var cached)
                        ? cached
                        : throw new ImageResolveException($"immagine {descriptor.Id} non in cache");
                case ImagePayload.Surface source:
                    return surface(source.Id) ?? throw new ImageResolveException($"superficie {source.Id} sconosciuta");
                case ImagePayload.Bitmap bitmap:
                    var bitmapPalette = ResolvePalette(bitmap.Data.Palette, bitmap.Data.Flags, bitmap.Data.Format.IsPaletted());
                    pixels = BitmapDecoder.Decode(bitmap.Data, bitmapPalette);
                    break;
                case ImagePayload.Quic quic:
                    pixels = QuicDecoder.Decode(quic.Data.Span);
                    break;
                case ImagePayload.LzRgb lz:
                    pixels = LzDecoder.Decode(lz.Data.Span);
                    break;
                case ImagePayload.LzPlt lzPlt:
                    pixels = LzDecoder.Decode(lzPlt.Data.Span, ResolvePalette(lzPlt.Palette, lzPlt.Flags, required: true));
                    break;
                case ImagePayload.GlzRgb glz:
                    pixels = _glz.Decode(glz.Data.Span);
                    break;
                case ImagePayload.ZlibGlzRgb zlib:
                    pixels = _glz.Decode(Inflate(zlib.Data, zlib.GlzDataSize));
                    break;
                case ImagePayload.Lz4 lz4:
                    pixels = Lz4ImageDecoder.Decode(lz4.Data.Span, (int)descriptor.Width, (int)descriptor.Height);
                    break;
                default:
                    // JPEG e JPEG con alfa arrivano con M5 (WIC nell'app).
                    throw new ImageResolveException($"formato {descriptor.Type} non ancora supportato");
            }
        }
        catch (CodecException error)
        {
            throw new ImageResolveException($"immagine {descriptor.Type}: {error.Message}", error);
        }

        if ((descriptor.Flags & (ImageOptions.CacheMe | ImageOptions.CacheReplaceMe)) != 0) Store(pixels, descriptor.Id);
        return pixels;
    }

    /// <summary>zlib (RFC 1950) per <c>ZLIB_GLZ_RGB</c>; la dimensione decompressa è nota.</summary>
    private static byte[] Inflate(ReadOnlyMemory<byte> data, uint expectedSize)
    {
        if (expectedSize is 0 or > Lz4Decoder.MaxOutput) throw CodecException.Corrupt("zlib dimensione");
        var output = new byte[expectedSize];
        using var input = new ZLibStream(new MemoryStream(data.ToArray(), writable: false), CompressionMode.Decompress);
        try
        {
            input.ReadExactly(output);
        }
        catch (Exception error) when (error is EndOfStreamException or InvalidDataException)
        {
            throw CodecException.Corrupt("zlib: " + error.Message);
        }
        return output;
    }

    private Palette? ResolvePalette(PaletteReference reference, BitmapOptions flags, bool required)
    {
        switch (reference)
        {
            case PaletteReference.Inline inline:
                var palette = inline.Palette;
                if (flags.HasFlag(BitmapOptions.PaletteCacheMe))
                {
                    // Limitata per numero e per colori totali (una palette può averne 65535): un
                    // server che conia id senza fine causa solo mancanze in cache.
                    var replaced = _palettes.TryGetValue(palette.Unique, out var old) ? old.Entries.Length : 0;
                    if ((_palettes.Count >= MaxCachedPalettes && replaced == 0)
                        || _paletteEntries - replaced + palette.Entries.Length > MaxPaletteEntries)
                    {
                        _palettes.Clear();
                        _paletteEntries = 0;
                        replaced = 0;
                    }
                    _paletteEntries += palette.Entries.Length - replaced;
                    _palettes[palette.Unique] = palette;
                }
                return palette;
            case PaletteReference.Cached cached:
                if (_palettes.TryGetValue(cached.Id, out var found)) return found;
                if (required) throw new ImageResolveException($"palette {cached.Id} non in cache");
                return null;
            default:
                return null;
        }
    }

    private void Store(PixelBuffer image, ulong id)
    {
        if (_pixmaps.TryGetValue(id, out var old)) _cachedPixels -= (long)old.Width * old.Height;
        _pixmaps[id] = image;
        _cachedPixels += (long)image.Width * image.Height;
        // Il server segue la nostra cache e invalida prima di superare il budget: questa è solo
        // una protezione da un server che non lo fa.
        if (_cachedPixels > PixmapCacheBudget * 2)
        {
            _pixmaps.Clear();
            _cachedPixels = 0;
        }
    }

    public void InvalidatePixmap(ulong id)
    {
        if (_pixmaps.Remove(id, out var old)) _cachedPixels -= (long)old.Width * old.Height;
    }

    public void InvalidateAllPixmaps()
    {
        _pixmaps.Clear();
        _cachedPixels = 0;
    }

    public void InvalidatePalette(ulong id)
    {
        if (_palettes.Remove(id, out var old)) _paletteEntries -= old.Entries.Length;
    }

    public void InvalidateAllPalettes()
    {
        _palettes.Clear();
        _paletteEntries = 0;
    }

    /// <summary>Maschera a 1 bit di una QMask (solo maschere non compresse).</summary>
    public static DrawMask? Mask(QMask qmask)
    {
        ArgumentNullException.ThrowIfNull(qmask);
        if (qmask.Bitmap?.Payload is not ImagePayload.Bitmap bitmap) return null;
        try
        {
            var bits = BitmapDecoder.DecodeMask(bitmap.Data);
            return new DrawMask(bits.Width, bits.Height, bits.Bits, qmask.Inverted, qmask.Position);
        }
        catch (CodecException)
        {
            return null;
        }
    }
}

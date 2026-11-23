namespace SpiceKit.Codecs;

/// <summary>
/// <c>SPICE_IMAGE_TYPE_GLZ_RGB</c> (porting di <c>SpiceCodecs/GLZDecoder.swift</c>): compressione
/// in stile LZ i cui rimandi possono puntare a immagini precedenti di una finestra condivisa (il
/// "dizionario globale"), identificate dall'id dell'immagine.
/// </summary>
/// <remarks>
/// Scritto da una descrizione del formato e verificato con immagini catturate da uno
/// spice-server reale (Fixtures/display), non derivato dal suo codice. Intestazione big-endian
/// di 33 byte (magic 0x20205A4C, versione, byte di tipo con il flag top-down nel bit 4,
/// larghezza, altezza, stride, id u64, distanza della testa della finestra u32), poi byte di
/// controllo: &lt; 32 introduce C+1 letterali; altrimenti un rimando con lunghezza, offset e
/// distanza in immagini nel byte di controllo e in pochi byte successivi. Le immagini RGBA
/// hanno un secondo passaggio, solo alfa, con lo stesso schema. Non thread-safe: un'istanza
/// per canale display.
/// </remarks>
public sealed class GlzDecoder(int maxWindowPixels = 64 * 1024 * 1024)
{
    private const uint Magic = 0x2020_5A4C;
    private const int HeaderSize = 33;

    /// <summary>Immagini decodificate tenute per i rimandi fra immagini (pixel a 32 bit).</summary>
    private readonly Dictionary<ulong, uint[]> _window = [];
    private long _windowPixels;

    /// <summary>
    /// Limite della finestra: il dizionario del server è dimensionato da ciò che il client annuncia
    /// in DISPLAY_INIT; questo protegge solo da un server che non lo rispetta.
    /// </summary>
    public int MaxWindowPixels { get; } = maxWindowPixels;

    public int StoredImageCount => _window.Count;

    public void Reset()
    {
        _window.Clear();
        _windowPixels = 0;
    }

    private enum Unit
    {
        /// <summary>3 byte B G R.</summary>
        Rgb24,

        /// <summary>2 byte big-endian x1r5g5b5.</summary>
        Rgb16,

        /// <summary>1 byte, scritto solo nel byte alto.</summary>
        Alpha,
    }

    public PixelBuffer Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw CodecException.Truncated();
        var reader = new Reader(data);
        if (reader.BE32() != Magic) throw CodecException.BadMagic();
        var version = reader.BE32();
        if (version != 0x0001_0001) throw CodecException.Version(version);
        var typeByte = reader.Byte();
        var topDown = (typeByte & 0x10) != 0;
        var type = typeByte & 0x0F;
        if (type is < 6 or > 9) throw CodecException.Unsupported($"glz tipo {type}");
        var width = (long)reader.BE32();
        var height = (long)reader.BE32();
        _ = reader.BE32(); // stride: le righe si decodificano come width pixel
        var id = ((ulong)reader.BE32() << 32) | reader.BE32();
        var headDistance = (ulong)reader.BE32();
        CodecLimits.Check(width, height);

        var pixels = new uint[width * height];
        var rgb16 = type == 6;
        DecodePass(ref reader, pixels, id, rgb16 ? Unit.Rgb16 : Unit.Rgb24, rgb16 ? 1 : 0);
        var rgba = type == 9;
        if (rgba) DecodePass(ref reader, pixels, id, Unit.Alpha, 2);

        // Nella finestra va la versione senza alfa forzato: i rimandi copiano i valori così come sono.
        Remember((uint[])pixels.Clone(), id, headDistance);
        if (!rgba)
        {
            for (var i = 0; i < pixels.Length; i++) pixels[i] |= 0xFF00_0000;
        }
        var output = new PixelBuffer((int)width, (int)height, pixels, rgba);
        if (!topDown) output.FlipVertically();
        return output;
    }

    private void DecodePass(ref Reader reader, uint[] pixels, ulong id, Unit unit, int lengthBias)
    {
        var total = pixels.Length;
        var alpha = unit == Unit.Alpha;
        var op = 0;
        while (op < total)
        {
            int control = reader.Byte();
            if (control < 32)
            {
                var run = control + 1;
                if (op + run > total) throw CodecException.Corrupt("glz troppi letterali");
                for (var k = 0; k < run; k++, op++)
                {
                    switch (unit)
                    {
                        case Unit.Rgb24:
                            uint b = reader.Byte(), g = reader.Byte(), r = reader.Byte();
                            pixels[op] = b | (g << 8) | (r << 16);
                            break;
                        case Unit.Rgb16:
                            uint hi = reader.Byte(), lo = reader.Byte();
                            pixels[op] = Lz4ImageDecoder.Expand555((hi << 8) | lo);
                            break;
                        default:
                            pixels[op] = (pixels[op] & 0x00FF_FFFF) | ((uint)reader.Byte() << 24);
                            break;
                    }
                }
                continue;
            }

            // Rimando.
            var length = control >> 5;
            var longForm = ((control >> 4) & 1) == 1;
            var offset = control & 0x0F;
            if (length == 7)
            {
                while (true)
                {
                    int extra = reader.Byte();
                    length += extra;
                    if (length > total) throw CodecException.Corrupt("glz lunghezza");
                    if (extra != 255) break;
                }
            }
            offset += reader.Byte() << 4;
            int x = reader.Byte();
            var distanceBytes = x >> 6;
            long distance = 0;
            if (!longForm)
            {
                distance = x & 0x3F;
                for (var i = 0; i < distanceBytes; i++) distance += (long)reader.Byte() << (6 + (8 * i));
            }
            else
            {
                offset += (x & 0x1F) << 12;
                for (var i = 0; i < distanceBytes; i++) distance |= (long)reader.Byte() << (8 * i);
                if (((x >> 5) & 1) == 1) offset += reader.Byte() << 17;
            }
            length += lengthBias;
            if (length <= 0 || op + length > total) throw CodecException.Corrupt("glz rimando oltre la fine");

            if (distance == 0)
            {
                offset++;
                if (offset > op) throw CodecException.Corrupt("glz offset");
                var source = op - offset;
                for (var k = 0; k < length; k++)
                {
                    var value = pixels[source + k]; // può sovrapporsi: copia in avanti
                    pixels[op + k] = alpha ? (pixels[op + k] & 0x00FF_FFFF) | (value & 0xFF00_0000) : value;
                }
            }
            else
            {
                if ((ulong)distance > id || !_window.TryGetValue(id - (ulong)distance, out var reference))
                {
                    throw CodecException.Corrupt("glz rimando a un'immagine sconosciuta");
                }
                if ((long)offset + length > reference.Length) throw CodecException.Corrupt("glz rimando oltre l'immagine");
                for (var k = 0; k < length; k++)
                {
                    var value = reference[offset + k];
                    pixels[op + k] = alpha ? (pixels[op + k] & 0x00FF_FFFF) | (value & 0xFF00_0000) : value;
                }
            }
            op += length;
        }
    }

    /// <summary>Conserva l'immagine e scarta quelle uscite dalla finestra del codificatore.</summary>
    private void Remember(uint[] pixels, ulong id, ulong headDistance)
    {
        if (_window.TryGetValue(id, out var old)) _windowPixels -= old.Length;
        _window[id] = pixels;
        _windowPixels += pixels.Length;
        if (headDistance <= id)
        {
            var oldest = id - headDistance;
            foreach (var key in _window.Keys.Where(k => k < oldest).ToList())
            {
                _windowPixels -= _window[key].Length;
                _window.Remove(key);
            }
        }
        // Protezione da un server che non rispetta la finestra: si dimenticano le immagini più vecchie.
        while (_windowPixels > MaxWindowPixels && _window.Count > 1)
        {
            var oldestKey = _window.Keys.Where(k => k != id).Min();
            _windowPixels -= _window[oldestKey].Length;
            _window.Remove(oldestKey);
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _index;

        public byte Byte()
        {
            if ((uint)_index >= (uint)_data.Length) throw CodecException.Truncated();
            return _data[_index++];
        }

        public uint BE32() => ((uint)Byte() << 24) | ((uint)Byte() << 16) | ((uint)Byte() << 8) | Byte();
    }
}

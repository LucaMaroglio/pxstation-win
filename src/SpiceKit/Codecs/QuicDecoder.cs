using System.Buffers.Binary;
using System.Numerics;

namespace SpiceKit.Codecs;

/// <summary>
/// Decoder di <c>SPICE_IMAGE_TYPE_QUIC</c> (porting di <c>SpiceCodecs/QUICDecoder.swift</c>; vedi
/// <c>QuicModel.cs</c>).
/// </summary>
/// <remarks>
/// Flusso: parole little-endian a 32 bit lette come flusso di bit dal bit più alto. Parole di
/// intestazione: magic "QUIC" (0x43495551), versione 0, tipo, larghezza, altezza. Righe dall'alto
/// in basso.
/// </remarks>
public static class QuicDecoder
{
    /// <summary><c>QuicImageType</c>.</summary>
    public enum ImageKind : uint
    {
        Gray = 1,
        Rgb16,
        Rgb24,
        Rgb32,
        Rgba,
    }

    internal const uint Magic = 0x4349_5551;

    public static PixelBuffer Decode(ReadOnlySpan<byte> data)
    {
        var wordCount = data.Length / 4;
        if (wordCount == 0) throw CodecException.Truncated();
        var words = new uint[wordCount];
        for (var i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);

        var bits = new BitReader(words);
        if (bits.Word != Magic) throw CodecException.BadMagic();
        bits.Eat32();
        var version = bits.Word;
        if (version != 0) throw CodecException.Version(version);
        bits.Eat32();
        var rawType = bits.Word;
        bits.Eat32();
        var width = (long)bits.Word;
        bits.Eat32();
        var height = (long)bits.Word;
        bits.Eat32();
        if (rawType is 0 or > (uint)ImageKind.Rgba) throw CodecException.Unsupported($"quic tipo {rawType}");
        CodecLimits.Check(width, height);
        return new Decoder((ImageKind)rawType, (int)width, bits).Run((int)height);
    }

    /// <summary>Lettore di bit: la parola di codice successiva comincia dal bit alto di <see cref="Word"/>.</summary>
    internal struct BitReader
    {
        private readonly uint[] _words;
        private int _nextIndex = 1;
        private uint _nextWord;
        private int _availableBits;

        /// <summary>Parole chieste oltre la fine (il codificatore chiude con zeri).</summary>
        private int _overrun;

        public BitReader(uint[] words)
        {
            _words = words;
            Word = words[0];
            _nextWord = words[0];
        }

        public uint Word { get; private set; }

        /// <summary>Consuma <paramref name="count"/> bit (da 1 a 31).</summary>
        public void Eat(int count)
        {
            if (count is <= 0 or >= 32) throw CodecException.Corrupt($"quic lunghezza della parola di codice {count}");
            var word = Word << count;
            var delta = _availableBits - count;
            if (delta >= 0)
            {
                _availableBits = delta;
                Word = word | (_nextWord >> _availableBits);
                return;
            }
            var missing = -delta;
            word |= _nextWord << missing;
            if (_nextIndex < _words.Length)
            {
                _nextWord = _words[_nextIndex++];
            }
            else
            {
                if (++_overrun > 2) throw CodecException.Truncated();
                _nextWord = 0;
            }
            _availableBits = 32 - missing;
            Word = word | (_availableBits >= 32 ? 0 : _nextWord >> _availableBits);
        }

        public void Eat32()
        {
            Eat(16);
            Eat(16);
        }
    }

    /// <summary>Stato della decodifica in buffer piatti (ciclo critico: niente array annidati).</summary>
    private sealed class Decoder
    {
        private readonly ImageKind _kind;
        private readonly int _width;
        private readonly int _channelCount;
        private readonly int _colorChannels;
        private readonly int _bpc;
        private readonly uint _valueMask;
        private readonly QuicFamily _family;
        private readonly uint[] _escapeSuffixMask = new uint[QuicTables.MaxCodes];
        private readonly int[] _bucketForContext = new int[256];
        private readonly int _bucketCount;
        private readonly uint[] _counters;
        private readonly byte[] _bestCode;

        /// <summary><c>_residuals[c * (width + 1) + k]</c>; k = 0 è la posizione "-1".</summary>
        private readonly byte[] _residuals;
        private byte[] _current;
        private byte[] _previous;
        private BitReader _bits;
        private QuicCommonState _colorState = new();
        private QuicCommonState _alphaState = new();

        public Decoder(ImageKind kind, int width, BitReader bits)
        {
            _kind = kind;
            _width = width;
            _bits = bits;
            _colorChannels = kind == ImageKind.Gray ? 1 : 3;
            _channelCount = kind == ImageKind.Rgba ? 4 : _colorChannels;
            _bpc = kind == ImageKind.Rgb16 ? 5 : 8;
            _valueMask = QuicTables.LowBitsMask(_bpc);
            _family = _bpc == 5 ? QuicFamily.FiveBit : QuicFamily.EightBit;
            for (var l = 0; l < QuicTables.MaxCodes; l++) _escapeSuffixMask[l] = QuicTables.LowBitsMask(_family.EscapeSuffixLength[l]);

            var (map, count) = QuicFamily.Buckets(_bpc);
            _bucketCount = count;
            for (var i = 0; i < 256; i++) _bucketForContext[i] = map[i & (int)_valueMask];
            _counters = new uint[_channelCount * count * QuicTables.MaxCodes];
            _bestCode = new byte[_channelCount * count];
            Array.Fill(_bestCode, (byte)(_bpc - 1));
            _residuals = new byte[_channelCount * (width + 1)];
            _current = new byte[_channelCount * width];
            _previous = new byte[_channelCount * width];
        }

        public PixelBuffer Run(int height)
        {
            var output = new PixelBuffer(_width, height, hasAlpha: _kind == ImageKind.Rgba);
            for (var row = 0; row < height; row++)
            {
                // residual[-1] vale 0 sulla prima riga, poi riporta residual[0] della riga sopra.
                for (var c = 0; c < _channelCount; c++)
                {
                    var b = c * (_width + 1);
                    _residuals[b] = row == 0 ? (byte)0 : _residuals[b + 1];
                }
                DecodeRow(row == 0, 0, _colorChannels, ref _colorState);
                if (_kind == ImageKind.Rgba) DecodeRow(row == 0, 3, 4, ref _alphaState);
                Store(row, output.Pixels);
                (_current, _previous) = (_previous, _current);
            }
            return output;
        }

        private void Store(int row, uint[] output)
        {
            var w = _width;
            var o = output.AsSpan(row * w, w);
            var r = _current.AsSpan(0, w);
            switch (_kind)
            {
                case ImageKind.Rgb24 or ImageKind.Rgb32:
                {
                    var g = _current.AsSpan(w, w);
                    var b = _current.AsSpan(2 * w, w);
                    for (var x = 0; x < w; x++) o[x] = PixelBuffer.Opaque(r[x], g[x], b[x]);
                    break;
                }
                case ImageKind.Rgba:
                {
                    var g = _current.AsSpan(w, w);
                    var b = _current.AsSpan(2 * w, w);
                    var a = _current.AsSpan(3 * w, w);
                    for (var x = 0; x < w; x++) o[x] = ((uint)a[x] << 24) | ((uint)r[x] << 16) | ((uint)g[x] << 8) | b[x];
                    break;
                }
                case ImageKind.Rgb16:
                {
                    var g = _current.AsSpan(w, w);
                    var b = _current.AsSpan(2 * w, w);
                    for (var x = 0; x < w; x++)
                    {
                        o[x] = PixelBuffer.Opaque(PixelBuffer.Expand5(r[x]), PixelBuffer.Expand5(g[x]), PixelBuffer.Expand5(b[x]));
                    }
                    break;
                }
                default:
                    for (var x = 0; x < w; x++) o[x] = PixelBuffer.Opaque(r[x], r[x], r[x]);
                    break;
            }
        }

        private byte DecodeResidual(int channel, byte context)
        {
            var bucket = _bucketForContext[context];
            int l = _bestCode[(channel * _bucketCount) + bucket];
            var window = _bits.Word;
            uint value;
            int length;
            if (window > _family.EscapePrefixMask[l])
            {
                var zeros = BitOperations.LeadingZeroCount(window);
                length = zeros + 1 + l;
                if (length >= 32) throw CodecException.Corrupt("quic parola di codice troppo lunga");
                value = (uint)(zeros << l) | ((window >> (32 - length)) & QuicTables.LowBitsMask(l));
            }
            else
            {
                length = _family.EscapeCodewordLength[l];
                if (length is <= 0 or >= 32) throw CodecException.Corrupt("quic lunghezza di escape");
                value = _family.GolombCodewords[l] + ((window >> (32 - length)) & _escapeSuffixMask[l]);
            }
            _bits.Eat(length);
            return (byte)value;
        }

        /// <summary>Adatta le statistiche con il residuo appena codificato in <paramref name="index"/> (da 1).</summary>
        private void UpdateModel(int first, int last, int index, uint trigger)
        {
            var lengths = _family.CodeLength;
            for (var c = first; c < last; c++)
            {
                var row = c * (_width + 1);
                var bucket = _bucketForContext[_residuals[row + index - 1]];
                var value = _residuals[row + index];
                var counts = _counters.AsSpan(((c * _bucketCount) + bucket) * QuicTables.MaxCodes, QuicTables.MaxCodes);
                var codes = lengths.AsSpan(value * QuicTables.MaxCodes, QuicTables.MaxCodes);
                var best = _bpc - 1;
                counts[best] += codes[best];
                var bestLength = counts[best];
                for (var l = _bpc - 2; l >= 0; l--)
                {
                    counts[l] += codes[l];
                    if (counts[l] < bestLength)
                    {
                        best = l;
                        bestLength = counts[l];
                    }
                }
                _bestCode[(c * _bucketCount) + bucket] = (byte)best;
                if (bestLength > trigger)
                {
                    for (var k = 0; k < _bpc; k++) counts[k] >>= 1;
                }
            }
        }

        /// <summary>Divide una riga in segmenti dove l'indice della maschera di attesa avanza.</summary>
        private void DecodeRow(bool firstRow, int first, int last, ref QuicCommonState state)
        {
            var position = 0;
            var remaining = _width;
            while (state.WaitMaskIndex < QuicTables.WaitMaskMax && state.WaitMaskLeft <= remaining)
            {
                if (state.WaitMaskLeft > 0)
                {
                    Segment(firstRow, first, last, ref state, position, position + state.WaitMaskLeft);
                    position += state.WaitMaskLeft;
                    remaining -= state.WaitMaskLeft;
                }
                state.WaitMaskIndex++;
                state.UpdateTrigger();
                state.WaitMaskLeft = QuicTables.WaitMaskStep;
            }
            if (remaining > 0)
            {
                Segment(firstRow, first, last, ref state, position, position + remaining);
                if (state.WaitMaskIndex < QuicTables.WaitMaskMax) state.WaitMaskLeft -= remaining;
            }
        }

        /// <summary>Pixel <paramref name="i"/>: la prima riga predice da sinistra, le altre da sinistra e sopra.</summary>
        private void DecodePixel(int i, bool firstRow, int first, int last)
        {
            var w = _width;
            var unfold = _family.Unfold;
            for (var c = first; c < last; c++)
            {
                var res = c * (w + 1);
                var cur = c * w;
                var residual = DecodeResidual(c, _residuals[res + i]);
                _residuals[res + i + 1] = residual;
                uint prediction;
                if (firstRow) prediction = i == 0 ? 0u : _current[cur + i - 1];
                else if (i == 0) prediction = _previous[cur];
                else prediction = ((uint)_current[cur + i - 1] + _previous[cur + i]) >> 1;
                _current[cur + i] = (byte)((unfold[residual] + prediction) & _valueMask);
            }
        }

        private bool SamePixel(byte[] a, int ia, byte[] b, int ib, int first, int last)
        {
            for (var c = first; c < last; c++)
            {
                if (a[(c * _width) + ia] != b[(c * _width) + ib]) return false;
            }
            return true;
        }

        /// <summary>Previsione di sequenza: la riga sopra si ripete in <paramref name="i"/> e la riga corrente si è ripetuta due volte.</summary>
        private bool ShouldStartRun(int i, int runIndex, int first, int last) =>
            SamePixel(_previous, i - 1, _previous, i, first, last)
            && runIndex != i && i > 2
            && SamePixel(_current, i - 1, _current, i - 2, first, last);

        private void Segment(bool firstRow, int first, int last, ref QuicCommonState state, int start, int end)
        {
            var waitMask = QuicTables.LowBitsMask(state.WaitMaskIndex);
            var i = start;
            int stop;
            var runIndex = 0;
            if (i == 0)
            {
                DecodePixel(0, firstRow, first, last);
                if (state.WaitCount > 0)
                {
                    state.WaitCount--;
                }
                else
                {
                    state.WaitCount = (int)(state.NextRandom() & waitMask);
                    UpdateModel(first, last, 1, state.Trigger);
                }
                i++;
            }
            stop = i + state.WaitCount;
            var allowRuns = !firstRow; // la prima riga non ha una riga sopra da cui prevedere

            while (true)
            {
                var runRequested = false;
                while (!runRequested && stop < end)
                {
                    while (i <= stop)
                    {
                        if (allowRuns && ShouldStartRun(i, runIndex, first, last))
                        {
                            runRequested = true;
                            break;
                        }
                        DecodePixel(i, firstRow, first, last);
                        i++;
                    }
                    if (runRequested) break;
                    UpdateModel(first, last, stop + 1, state.Trigger);
                    stop = i + (int)(state.NextRandom() & waitMask);
                }
                if (!runRequested)
                {
                    while (i < end)
                    {
                        if (allowRuns && ShouldStartRun(i, runIndex, first, last))
                        {
                            runRequested = true;
                            break;
                        }
                        DecodePixel(i, firstRow, first, last);
                        i++;
                    }
                }
                if (!runRequested)
                {
                    state.WaitCount = stop - end;
                    return;
                }

                // Sequenza: ripete il pixel a sinistra. La storia dei residui volutamente non cambia.
                state.WaitCount = stop - i;
                runIndex = i;
                var length = DecodeRunLength(ref state);
                if (length < 0 || length > end - i) throw CodecException.Corrupt("quic lunghezza della sequenza");
                var runEnd = i + length;
                for (; i < runEnd; i++)
                {
                    for (var c = first; c < last; c++) _current[(c * _width) + i] = _current[(c * _width) + i - 1];
                }
                if (i == end) return;
                stop = i + state.WaitCount;
            }
        }

        /// <summary>Lunghezza di sequenza adattiva MELCODE.</summary>
        private int DecodeRunLength(ref QuicCommonState state)
        {
            var orders = QuicTables.MelcodeOrders;
            var length = 0;
            while (true)
            {
                var topByte = (byte)~(_bits.Word >> 24);
                var ones = BitOperations.LeadingZeroCount((uint)topByte) - 24; // uni iniziali della finestra (8 se tutti)
                for (var k = 0; k < ones; k++)
                {
                    length += state.MelcOrder;
                    if (state.MelcState < orders.Length - 1)
                    {
                        state.MelcState++;
                        state.MelcLength = orders[state.MelcState];
                        state.MelcOrder = 1 << state.MelcLength;
                    }
                }
                if (ones != 8)
                {
                    _bits.Eat(ones + 1); // gli uni più lo zero finale
                    break;
                }
                _bits.Eat(8);
            }
            if (state.MelcLength > 0)
            {
                length += (int)(_bits.Word >> (32 - state.MelcLength));
                _bits.Eat(state.MelcLength);
            }
            if (state.MelcState > 0)
            {
                state.MelcState--;
                state.MelcLength = orders[state.MelcState];
                state.MelcOrder = 1 << state.MelcLength;
            }
            return length;
        }
    }
}

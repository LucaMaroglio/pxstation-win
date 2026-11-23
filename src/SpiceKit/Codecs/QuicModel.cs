// Tabelle e modello adattivo del codec di immagini SPICE "QUIC" (porting di
// SpiceCodecs/QUICModel.swift).
//
// QUIC è un codificatore predittivo simile a LOCO-I/JPEG-LS: ogni valore di canale è predetto dai
// vicini a sinistra e sopra, il residuo è mappato in un valore senza segno e codificato con un
// codice di Golomb–Rice adattivo scelto per gruppo di contesti; le ripetizioni lunghe sulle righe
// che ripetono quella precedente sono codificate con un codificatore di sequenze MELCODE.
//
// Le costanti sono parametri del formato (devono coincidere bit per bit con il codificatore del
// server). Scritto dalla descrizione del formato e verificato con flussi del codificatore di
// riferimento (Fixtures/codecs); nessun codice di riferimento incluso.

namespace SpiceKit.Codecs;

internal static class QuicTables
{
    /// <summary>Parametri di Golomb tenuti per gruppo.</summary>
    public const int MaxCodes = 8;

    /// <summary>Lunghezza massima di una parola di codice (DEFmaxclen).</summary>
    public const int CodewordLimit = 26;

    /// <summary>Indice massimo della maschera di attesa (DEFwmimax).</summary>
    public const int WaitMaskMax = 6;

    /// <summary>Simboli codificati prima che l'indice della maschera di attesa avanzi (DEFwminext).</summary>
    public const int WaitMaskStep = 2048;

    /// <summary><c>(1 &lt;&lt; n) - 1</c>, valido per n da 0 a 32.</summary>
    public static uint LowBitsMask(int n) => n >= 32 ? uint.MaxValue : (1u << n) - 1;

    /// <summary>
    /// Tabella pseudo-casuale che pianifica gli aggiornamenti del modello. Codificatore e
    /// decodificatore la consumano allo stesso modo: i valori fanno parte del formato.
    /// </summary>
    public static ReadOnlySpan<uint> Tabrand =>
    [
        0x02c57542, 0x35427717, 0x2f5a2153, 0x9244f155, 0x7bd26d07, 0x354c6052, 0x57329b28, 0x2993868e,
        0x6cd8808c, 0x147b46e0, 0x99db66af, 0xe32b4cac, 0x1b671264, 0x9d433486, 0x62a4c192, 0x06089a4b,
        0x9e3dce44, 0xdaabee13, 0x222425ea, 0xa46f331d, 0xcd589250, 0x8bb81d7f, 0xc8b736b9, 0x35948d33,
        0xd7ac7fd0, 0x5fbe2803, 0x2cfbc105, 0x013dbc4e, 0x7a37820f, 0x39f88e9e, 0xedd58794, 0xc5076689,
        0xfcada5a4, 0x64c2f46d, 0xb3ba3243, 0x8974b4f9, 0x5a05aebd, 0x20afcd00, 0x39e2b008, 0x88a18a45,
        0x600bde29, 0xf3971ace, 0xf37b0a6b, 0x7041495b, 0x70b707ab, 0x06beffbb, 0x4206051f, 0xe13c4ee3,
        0xc1a78327, 0x91aa067c, 0x8295f72a, 0x732917a6, 0x1d871b4d, 0x4048f136, 0xf1840e7e, 0x6a6048c1,
        0x696cb71a, 0x7ff501c3, 0x0fc6310b, 0x57e0f83d, 0x8cc26e74, 0x11a525a2, 0x946934c7, 0x7cd888f0,
        0x8f9d8604, 0x4f86e73b, 0x04520316, 0xdeeea20c, 0xf1def496, 0x67687288, 0xf540c5b2, 0x22401484,
        0x3478658a, 0xc2385746, 0x01979c2c, 0x5dad73c8, 0x0321f58b, 0xf0fedbee, 0x92826ddf, 0x284bec73,
        0x5b1a1975, 0x03df1e11, 0x20963e01, 0xa17cf12b, 0x740d776e, 0xa7a6bf3c, 0x01b5cce4, 0x1118aa76,
        0xfc6fac0a, 0xce927e9b, 0x00bf2567, 0x806f216c, 0xbca69056, 0x795bd3e9, 0xc9dc4557, 0x8929b6c2,
        0x789d52ec, 0x3f3fbf40, 0xb9197368, 0xa38c15b5, 0xc3b44fa8, 0xca8333b0, 0xb7e8d590, 0xbe807feb,
        0xbf5f8360, 0xd99e2f5c, 0x372928e1, 0x7c757c4c, 0x0db5b154, 0xc01ede02, 0x1fc86e78, 0x1f3985be,
        0xb4805c77, 0x00c880fa, 0x974c1b12, 0x35ab0214, 0xb2dc840d, 0x5b00ae37, 0xd313b026, 0xb260969d,
        0x7f4c8879, 0x1734c4d3, 0x49068631, 0xb9f6a021, 0x6b863e6f, 0xcee5debf, 0x29f8c9fb, 0x53dd6880,
        0x72b61223, 0x1f67a9fd, 0x0a0f6993, 0x13e59119, 0x11cca12e, 0xfe6b6766, 0x16b6effc, 0x97918fc4,
        0xc2b8a563, 0x94f2f741, 0x0bfa8c9a, 0xd1537ae8, 0xc1da349c, 0x873c60ca, 0x95005b85, 0x9b5c080e,
        0xbc8abbd9, 0xe1eab1d2, 0x6dac9070, 0x4ea9ebf1, 0xe0cf30d4, 0x1ef5bd7b, 0xd161043e, 0x5d2fa2e2,
        0xff5d3cae, 0x86ed9f87, 0x2aa1daa1, 0xbd731a34, 0x9e8f4b22, 0xb1c2c67a, 0xc21758c9, 0xa182215d,
        0xccb01948, 0x8d168df7, 0x04238cfe, 0x368c3dbc, 0x0aeadca5, 0xbad21c24, 0x0a71fee5, 0x9fc5d872,
        0x54c152c6, 0xfc329483, 0x6783384a, 0xeddb3e1c, 0x65f90e30, 0x884ad098, 0xce81675a, 0x4b372f7d,
        0x68bf9a39, 0x43445f1e, 0x40f8d8cb, 0x90d5acb6, 0x4cd07282, 0x349eeb06, 0x0c9d5332, 0x520b24ef,
        0x80020447, 0x67976491, 0x2f931ca3, 0xfe9b0535, 0xfcd30220, 0x61a9e6cc, 0xa487d8d7, 0x3f7c5dd1,
        0x7d0127c5, 0x48f51d15, 0x60dea871, 0xc9a91cb7, 0x58b53bb3, 0x9d5e0b2d, 0x624a78b4, 0x30dbee1b,
        0x9bdf22e7, 0x1df5c299, 0x2d5643a7, 0xf4dd35ff, 0x03ca8fd6, 0x53b47ed8, 0x6f2c19aa, 0xfeb0c1f4,
        0x49e54438, 0x2f2577e6, 0xbf876969, 0x72440ea9, 0xfa0bafb8, 0x74f5b3a0, 0x7dd357cd, 0x89ce1358,
        0x6ef2cdda, 0x1e7767f3, 0xa6be9fdb, 0x4f5f88f8, 0xba994a3a, 0x08ca6b65, 0xe0893818, 0x9e00a16a,
        0xf42bfc8f, 0x9972eedc, 0x749c8b51, 0x32c05f5e, 0xd706805f, 0x6bfbb7cf, 0xd9210a10, 0x31a1db97,
        0x923a9559, 0x37a7a1f6, 0x059f8861, 0xca493e62, 0x65157e81, 0x8f6467dd, 0xab85ff9f, 0x9331aff2,
        0x8616b9f5, 0xedbd5695, 0xee7e29b1, 0x313ac44f, 0xb903112f, 0x432ef649, 0xdc0a36c0, 0x61cf2bba,
        0x81474925, 0xa8b6c7ad, 0xee5931de, 0xb2f8158d, 0x59fb7409, 0x2e3dfaed, 0x9af25a3f, 0xe1fed4d5,
    ];

    /// <summary>Soglie di dimezzamento dei contatori per indice della maschera di attesa (evoluzione 3).</summary>
    public static ReadOnlySpan<uint> WaitMaskTriggers => [110, 550, 900, 800, 550, 400, 350, 250, 140, 160, 140];

    /// <summary>Tabella di stato di MELCODE: esponente della sequenza per stato.</summary>
    public static ReadOnlySpan<byte> MelcodeOrders =>
    [
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
        4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    ];
}

/// <summary>Famiglia di codici di Golomb per una profondità (8 o 5 bit per canale).</summary>
internal sealed class QuicFamily
{
    public static readonly QuicFamily EightBit = new(8);
    public static readonly QuicFamily FiveBit = new(5);

    private QuicFamily(int bpc)
    {
        BitsPerChannel = bpc;
        var valueMask = (int)QuicTables.LowBitsMask(bpc);
        for (var l = 0; l < bpc; l++)
        {
            var prefixLength = Math.Min(QuicTables.CodewordLimit - bpc, (int)QuicTables.LowBitsMask(bpc - l));
            var escapeCount = valueMask + 1 - (prefixLength << l);
            GolombCodewords[l] = (uint)(prefixLength << l);
            EscapeSuffixLength[l] = CeilLog2(escapeCount);
            EscapeCodewordLength[l] = prefixLength + EscapeSuffixLength[l];
            EscapePrefixMask[l] = QuicTables.LowBitsMask(32 - prefixLength);
            for (var value = 0; value < 256; value++)
            {
                CodeLength[(value * QuicTables.MaxCodes) + l] = (uint)(value < GolombCodewords[l]
                    ? (value >> l) + l + 1
                    : EscapeCodewordLength[l]);
            }
        }
        for (var s = 0; s <= valueMask; s++)
        {
            Unfold[s] = (byte)((s & 1) != 0 ? valueMask - (s >> 1) : s >> 1);
        }
    }

    public int BitsPerChannel { get; }

    /// <summary>Indicizzati dal parametro di codice <c>l</c>.</summary>
    public uint[] GolombCodewords { get; } = new uint[QuicTables.MaxCodes];

    public int[] EscapeCodewordLength { get; } = new int[QuicTables.MaxCodes];

    public uint[] EscapePrefixMask { get; } = new uint[QuicTables.MaxCodes];

    public int[] EscapeSuffixLength { get; } = new int[QuicTables.MaxCodes];

    /// <summary><c>CodeLength[value * MaxCodes + l]</c>: bit necessari per <c>value</c> con il parametro <c>l</c>.</summary>
    public uint[] CodeLength { get; } = new uint[256 * QuicTables.MaxCodes];

    /// <summary>Riporta il residuo ripiegato a residuo con segno (modulo 2^bpc).</summary>
    public byte[] Unfold { get; } = new byte[256];

    internal static int CeilLog2(int value)
    {
        if (value <= 1) return 0;
        var result = 1;
        var v = value - 1;
        while ((v >>= 1) != 0) result++;
        return result;
    }

    /// <summary>
    /// Mappa contesto → gruppo. Il contesto è il residuo precedente (0..2^bpc); i gruppi crescono
    /// 1, 2, 4, 8… e l'ultimo assorbe il resto.
    /// </summary>
    public static (int[] Map, int Count) Buckets(int bpc)
    {
        var levels = 1 << bpc;
        var map = new int[levels];
        int bucket = 0, end = 0, size = 1, repeatCounter = 2; // repfirst + 1
        do
        {
            var start = bucket == 0 ? 0 : end + 1;
            if (--repeatCounter == 0)
            {
                repeatCounter = 1;
                size *= 2;
            }
            end = start + size - 1;
            if (end + size >= levels) end = levels - 1;
            for (var context = start; context <= end; context++) map[context] = bucket;
            bucket++;
        }
        while (end < levels - 1);
        return (map, bucket);
    }
}

/// <summary>Pianificazione e stato delle sequenze condivisi dai canali codificati insieme.</summary>
internal struct QuicCommonState()
{
    public int WaitCount;
    public uint TabrandSeed = 0xFF;
    public int WaitMaskIndex;
    public int WaitMaskLeft = QuicTables.WaitMaskStep;
    public uint Trigger = QuicTables.WaitMaskTriggers[0];
    public int MelcState;
    public int MelcLength;
    public int MelcOrder = 1;

    public uint NextRandom()
    {
        TabrandSeed++;
        return QuicTables.Tabrand[(int)(TabrandSeed & 0xFF)];
    }

    public void UpdateTrigger() => Trigger = QuicTables.WaitMaskTriggers[Math.Min(WaitMaskIndex, 10)];
}

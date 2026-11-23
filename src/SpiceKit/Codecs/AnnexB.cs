namespace SpiceKit.Codecs;

/// <summary>
/// Flussi H.264 / H.265 in forma Annex B, come li producono i codificatori GStreamer di
/// spice-server (<c>stream-format=byte-stream</c>): unità NAL separate da 00 00 01 / 00 00 00 01
/// (porting di <c>AnnexB.swift</c>).
/// </summary>
public static class AnnexB
{
    /// <summary>Intervalli delle unità NAL (senza codici di inizio né zeri finali), in ordine.</summary>
    public static List<Range> NalUnits(ReadOnlySpan<byte> data)
    {
        var units = new List<Range>();
        var start = -1;
        var i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0) Add(units, data, start, i);
                i += 3;
                start = i;
            }
            else
            {
                i++;
            }
        }
        if (start >= 0 && start < data.Length) Add(units, data, start, data.Length);
        return units;
    }

    private static void Add(List<Range> units, ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0) end--;
        if (end > start) units.Add(start..end);
    }

    /// <summary>Tipo dell'unità NAL (H.264: 5 bit bassi del primo byte; H.265: bit 1–6).</summary>
    public static int NalType(ReadOnlySpan<byte> unit, bool hevc) =>
        unit.IsEmpty ? 0 : hevc ? (unit[0] >> 1) & 0x3F : unit[0] & 0x1F;

    /// <summary>Insiemi di parametri: SPS/PPS di H.264, VPS/SPS/PPS di H.265.</summary>
    public static bool IsParameterSet(int type, bool hevc) => hevc ? type is >= 32 and <= 34 : type is 7 or 8;

    /// <summary>L'unità di accesso contiene un fotogramma chiave (IDR per H.264, IRAP per H.265).</summary>
    public static bool HasKeyFrame(ReadOnlySpan<byte> data, bool hevc)
    {
        foreach (var range in NalUnits(data))
        {
            var type = NalType(data[range], hevc);
            if (hevc ? type is >= 16 and <= 23 : type == 5) return true;
        }
        return false;
    }
}

// Flussi video del canale display (spice.proto: StreamCreate, StreamData, StreamClip…).

namespace SpiceKit.Wire;

/// <summary><c>SPICE_MSG_DISPLAY_STREAM_CREATE</c>.</summary>
public sealed record StreamCreate(uint SurfaceId, uint StreamId, byte Flags, byte RawCodec, ulong Stamp,
    uint StreamWidth, uint StreamHeight, uint SourceWidth, uint SourceHeight, SpiceRect Destination, SpiceClip Clip)
{
    /// <summary><c>SPICE_STREAM_FLAGS_TOP_DOWN</c>.</summary>
    public bool IsTopDown => (Flags & 1) != 0;

    public VideoCodecType? Codec => RawCodec is >= 1 and <= 5 ? (VideoCodecType)RawCodec : null;

    public static StreamCreate Decode(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        return new StreamCreate(r.ReadU32(), r.ReadU32(), r.ReadU8(), r.ReadU8(), r.ReadU64(),
            r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32(), SpiceRect.Decode(ref r), SpiceClip.Decode(ref r));
    }
}

/// <summary><c>SPICE_MSG_DISPLAY_STREAM_DATA</c> e <c>STREAM_DATA_SIZED</c> (con nuova misura e destinazione).</summary>
public sealed record StreamData(uint StreamId, uint MultimediaTime, SpiceRect? Destination, ReadOnlyMemory<byte> Data)
{
    public static StreamData Decode(ReadOnlyMemory<byte> payload, bool sized)
    {
        var r = new ByteReader(payload.Span);
        var id = r.ReadU32();
        var time = r.ReadU32();
        SpiceRect? destination = null;
        if (sized)
        {
            _ = r.ReadU32(); // larghezza e altezza del fotogramma: le dà il decoder
            _ = r.ReadU32();
            destination = SpiceRect.Decode(ref r);
        }
        var size = r.ReadU32();
        if (size > r.Remaining) throw WireException.Truncated(size > int.MaxValue ? int.MaxValue : (int)size, r.Remaining);
        return new StreamData(id, time, destination, payload.Slice(r.Offset, (int)size));
    }
}

/// <summary><c>SPICE_MSG_DISPLAY_STREAM_CLIP</c>.</summary>
public sealed record StreamClip(uint StreamId, SpiceClip Clip)
{
    public static StreamClip Decode(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        var id = r.ReadU32();
        return new StreamClip(id, SpiceClip.Decode(ref r));
    }
}

/// <summary><c>SPICE_MSG_DISPLAY_STREAM_ACTIVATE_REPORT</c>: il server vuole le statistiche di riproduzione.</summary>
public readonly record struct StreamActivateReport(uint StreamId, uint UniqueId, uint MaxWindowSize, uint TimeoutMs)
{
    public static StreamActivateReport Decode(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        return new StreamActivateReport(r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32());
    }
}

/// <summary>
/// Campi di <c>SPICE_MSGC_DISPLAY_STREAM_REPORT</c>. <c>LastFrameDelay</c>: tempo del fotogramma
/// meno tempo di riproduzione dell'ultimo (negativo = in ritardo); <c>AudioDelay</c>:
/// <c>uint.MaxValue</c> senza audio.
/// </summary>
public readonly record struct StreamReport(uint StreamId, uint UniqueId, uint StartFrameTime, uint EndFrameTime,
    uint Frames, uint Drops, int LastFrameDelay, uint AudioDelay = uint.MaxValue);

public static partial class DisplayClientEncoder
{
    public static SpiceMessage StreamReport(StreamReport report)
    {
        var w = new ByteWriter(32);
        w.WriteU32(report.StreamId);
        w.WriteU32(report.UniqueId);
        w.WriteU32(report.StartFrameTime);
        w.WriteU32(report.EndFrameTime);
        w.WriteU32(report.Frames);
        w.WriteU32(report.Drops);
        w.WriteI32(report.LastFrameDelay);
        w.WriteU32(report.AudioDelay);
        return new SpiceMessage((ushort)DisplayClientMessage.StreamReport, w.ToArray());
    }

    /// <summary>
    /// Report con <c>num_frames = 0</c> e <c>num_drops = 0xffffffff</c>: il client non sa
    /// decodificare il flusso e il server torna alle immagini (spice-server, <c>stream_report</c>).
    /// </summary>
    public static SpiceMessage StreamUnsupported(uint streamId, uint uniqueId) =>
        StreamReport(new StreamReport(streamId, uniqueId, 0, 0, 0, uint.MaxValue, 0));

    /// <summary><c>SPICE_MSGC_DISPLAY_PREFERRED_VIDEO_CODEC_TYPE</c>: codec in ordine di preferenza.</summary>
    public static SpiceMessage PreferredVideoCodecs(IReadOnlyList<VideoCodecType> codecs)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        var w = new ByteWriter(1 + codecs.Count);
        w.WriteU8((byte)codecs.Count);
        foreach (var codec in codecs) w.WriteU8((byte)codec);
        return new SpiceMessage((ushort)DisplayClientMessage.PreferredVideoCodecType, w.ToArray());
    }
}

using System.Diagnostics;
using SpiceKit.Codecs;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>Flussi video del canale display (porting della parte "Video streams" di <c>DisplayChannel.swift</c>).</summary>
/// <remarks>
/// Ogni fotogramma si decodifica qui, fuori dal thread dell'interfaccia, e si copia nella superficie
/// di destinazione (scalato, con il ritaglio del flusso): per il renderer è un aggiornamento come
/// gli altri e la contropressione resta la stessa. Fotogrammi non decodificabili si scartano e si
/// contano nei report, così il server può ridurre la qualità.
/// </remarks>
public sealed partial class DisplayChannel
{
    /// <summary>Flussi contemporanei al massimo (limite contro server ostili, AGENTS.md §6).</summary>
    internal const int MaxStreams = 64;

    private readonly IMediaDecoders? _decoders;
    private readonly MultimediaClock? _clock;
    private readonly Dictionary<uint, VideoStream> _streams = [];
    private readonly List<SpiceMessage> _pendingReports = [];

    /// <summary>Flussi attivi (test).</summary>
    internal int StreamCount
    {
        get
        {
            lock (_gate) return _streams.Count;
        }
    }

    /// <summary>Fotogrammi video applicati e scartati finora (diagnostica e autoverifica).</summary>
    public (int Decoded, int Dropped) VideoFrames
    {
        get
        {
            lock (_gate) return (_decodedFrames, _droppedFrames);
        }
    }

    private int _decodedFrames;
    private int _droppedFrames;

    /// <summary>Report dei flussi da inviare (test).</summary>
    internal IReadOnlyList<SpiceMessage> PendingReports => _pendingReports;

    internal static IReadOnlyList<VideoCodecType> VideoCodecsOf(IMediaDecoders? decoders) =>
        decoders is null ? [] : [.. decoders.VideoCodecs.Where(c => c != VideoCodecType.Mjpeg || decoders.Jpeg is not null).Distinct()];

    private sealed class VideoStream(StreamCreate create)
    {
        public StreamCreate Create { get; } = create;

        public SpiceClip Clip { get; set; } = create.Clip;

        public SpiceRect Destination { get; set; } = create.Destination;

        public IVideoDecoder? Decoder { get; init; }

        public ReportWindow? Report { get; set; }
    }

    /// <summary>Statistiche per <c>SPICE_MSGC_DISPLAY_STREAM_REPORT</c>.</summary>
    private sealed class ReportWindow(uint uniqueId, uint maxFrames, TimeSpan timeout)
    {
        public uint UniqueId { get; } = uniqueId;

        public uint MaxFrames { get; } = maxFrames;

        public TimeSpan Timeout { get; } = timeout;

        public long? Started { get; set; }

        public uint StartFrameTime { get; set; }

        public uint EndFrameTime { get; set; }

        public uint Frames { get; set; }

        public uint Drops { get; set; }

        public int LastDelay { get; set; }
    }

    private void CreateStream(StreamCreate create)
    {
        if (!_streams.ContainsKey(create.StreamId) && _streams.Count >= MaxStreams) throw WireException.LimitExceeded("troppi flussi video");
        DestroyStream(create.StreamId);
        IVideoDecoder? decoder = null;
        var codec = create.Codec;
        if (codec is { } known && known != VideoCodecType.Mjpeg && VideoCodecsOf(_decoders).Contains(known))
        {
            decoder = _decoders?.CreateVideoDecoder(known);
        }
        if (codec != VideoCodecType.Mjpeg && decoder is null)
        {
            WarnOnce((ushort)DisplayServerMessage.StreamCreate, $"codec video {create.RawCodec} non supportato");
        }
        _streams[create.StreamId] = new VideoStream(create) { Decoder = decoder };
        SpiceLog.Info("display", $"flusso {create.StreamId}: {codec?.ToString() ?? create.RawCodec.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                                 $"{create.StreamWidth}x{create.StreamHeight} in {create.Destination}");
    }

    private void DestroyStream(uint id)
    {
        if (_streams.Remove(id, out var stream)) stream.Decoder?.Dispose();
    }

    private void DestroyAllStreams()
    {
        foreach (var stream in _streams.Values) stream.Decoder?.Dispose();
        _streams.Clear();
    }

    private void ActivateReport(StreamActivateReport activate)
    {
        if (!_streams.TryGetValue(activate.StreamId, out var stream)) return;
        if (stream.Create.Codec != VideoCodecType.Mjpeg && stream.Decoder is null)
        {
            // Codec che non sappiamo decodificare: il server torna alle immagini.
            _pendingReports.Add(DisplayClientEncoder.StreamUnsupported(activate.StreamId, activate.UniqueId));
            return;
        }
        if (_clock is null) return;
        stream.Report = new ReportWindow(activate.UniqueId, Math.Max(1, activate.MaxWindowSize),
            TimeSpan.FromMilliseconds(Math.Min(activate.TimeoutMs, 60_000u)));
    }

    private void StreamFrame(StreamData data)
    {
        if (!_streams.TryGetValue(data.StreamId, out var stream)) return;
        if (data.Destination is { } destination) stream.Destination = destination;
        PixelBuffer? frame = null;
        try
        {
            frame = stream.Create.Codec == VideoCodecType.Mjpeg
                ? _decoders?.Jpeg?.Decode(data.Data.Span)
                : stream.Decoder?.Decode(data.Data.Span);
        }
        catch (CodecException error)
        {
            SpiceLog.Debug("display", $"fotogramma del flusso {data.StreamId} non decodificato: {error.Message}");
        }
        RecordFrame(stream, data.StreamId, data.MultimediaTime, dropped: frame is null);
        if (frame is not { } image)
        {
            _droppedFrames++;
            return;
        }
        _decodedFrames++;
        if (!stream.Create.IsTopDown) image.FlipVertically();
        var target = new DisplayBase(stream.Create.SurfaceId, stream.Destination, stream.Clip);
        Mutate(target, s => s.Copy(image, image.Bounds, target.Box, target.Clip, Ropd.OpPut, ScaleMode.Interpolate));
    }

    private void RecordFrame(VideoStream stream, uint id, uint frameTime, bool dropped)
    {
        if (stream.Report is not { } report || _clock?.Now is not { } now) return;
        if (report.Started is null)
        {
            report.Started = Stopwatch.GetTimestamp();
            report.StartFrameTime = frameTime;
        }
        report.Frames++;
        if (dropped) report.Drops++;
        report.EndFrameTime = frameTime;
        report.LastDelay = unchecked((int)(frameTime - now));
        if (report.Frames >= report.MaxFrames || Stopwatch.GetElapsedTime(report.Started.Value) >= report.Timeout)
        {
            _pendingReports.Add(DisplayClientEncoder.StreamReport(new StreamReport(id, report.UniqueId, report.StartFrameTime,
                report.EndFrameTime, report.Frames, report.Drops, report.LastDelay)));
            stream.Report = new ReportWindow(report.UniqueId, report.MaxFrames, report.Timeout);
        }
    }
}

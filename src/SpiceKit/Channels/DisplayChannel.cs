using System.Threading.Channels;
using SpiceKit.Codecs;
using SpiceKit.Wire;

namespace SpiceKit.Channels;

/// <summary>
/// Contropressione fra canale display e renderer: al massimo <see cref="Limit"/> aggiornamenti in
/// coda; il danno successivo si accumula nel rettangolo sporco (nessun pixel perso) finché chi
/// consuma non recupera.
/// </summary>
public sealed class DisplayUpdateCredit(int limit = 3)
{
    private int _queued;

    public int Limit { get; } = limit;

    /// <summary>Da chiamare dopo aver applicato un <see cref="DisplayEvent.Update"/>.</summary>
    public void Consumed()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _queued);
            if (current == 0) return;
        }
        while (Interlocked.CompareExchange(ref _queued, current - 1, current) != current);
    }

    internal bool TryAcquire()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _queued);
            if (current >= Limit) return false;
        }
        while (Interlocked.CompareExchange(ref _queued, current + 1, current) != current);
        return true;
    }
}

/// <summary>Regione cambiata di una superficie, con i suoi pixel (BGRA premoltiplicato, dall'alto, <c>Width × Height</c> compatti).</summary>
public sealed record SurfaceUpdate(uint SurfaceId, SpiceRect Rect, uint[] Pixels);

/// <summary>Eventi per il renderer.</summary>
public abstract record DisplayEvent
{
    /// <summary>Superficie primaria (ri)creata: la texture di presentazione va portata a questa misura.</summary>
    public sealed record PrimarySurfaceCreated(uint SurfaceId, int Width, int Height) : DisplayEvent;

    public sealed record PrimarySurfaceDestroyed(uint SurfaceId) : DisplayEvent;

    public sealed record Update(SurfaceUpdate Surface) : DisplayEvent;

    public sealed record MonitorsConfig(IReadOnlyList<MonitorHead> Heads) : DisplayEvent;
}

/// <summary>
/// Canale display (porting di <c>SpiceChannels/DisplayChannel.swift</c>): tiene le superfici in
/// memoria ed emette gli aggiornamenti della superficie primaria. Le immagini si decodificano qui,
/// fuori dal thread dell'interfaccia.
/// </summary>
/// <remarks>
/// Superfici, comandi di disegno, immagini bitmap/QUIC/LZ/GLZ/ZLIB-GLZ/LZ4 e cache (M3); JPEG e
/// flussi video MJPEG/H.264/H.265 con i decoder della piattaforma (M5, <see cref="IMediaDecoders"/>).
/// Si annunciano solo i codec che la piattaforma sa decodificare.
/// </remarks>
public sealed partial class DisplayChannel
{
    /// <summary>Pixel annunciati per la cache delle pixmap.</summary>
    public const long PixmapCacheBudget = 16 * 1024 * 1024;

    /// <summary>Pixel della finestra GLZ (32 MiB di dizionario).</summary>
    public const int GlzWindowSize = 8 * 1024 * 1024;

    // Limiti contro server ostili (AGENTS.md §6).
    internal const uint MaxSurfaceDimension = 16_384;
    internal const int MaxSurfaces = 1024;

    /// <summary>Budget complessivo delle superfici: 512 MiB a 4 byte per pixel (tre schermi 8K).</summary>
    internal const long MaxSurfacePixels = 128L * 1024 * 1024;

    private readonly ChannelConnection _connection;
    private readonly ChannelWriter<DisplayEvent> _events;
    private readonly DisplayUpdateCredit? _credit;
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, PixelBuffer> _surfaces = [];
    private readonly HashSet<ushort> _warned = [];
    private readonly ImageResolver _resolver;
    private long _surfacePixels;
    private uint? _primaryId;
    private SpiceRect _dirty;
    private bool _retryScheduled;
    private bool _closed;

    /// <param name="connection">Canale già collegato.</param>
    /// <param name="events">Destinazione degli eventi (non viene completata qui).</param>
    /// <param name="credit">Contropressione; <see langword="null"/> (test) pubblica ogni aggiornamento.</param>
    /// <param name="decoders">JPEG e video della piattaforma; <see langword="null"/>: nessuno.</param>
    /// <param name="clock">Orologio multimediale del server: abilita i report dei flussi.</param>
    public DisplayChannel(ChannelConnection connection, ChannelWriter<DisplayEvent> events, DisplayUpdateCredit? credit = null,
        IMediaDecoders? decoders = null, MultimediaClock? clock = null)
    {
        _connection = connection;
        _events = events;
        _credit = credit;
        _decoders = decoders;
        _clock = clock;
        _resolver = new ImageResolver(PixmapCacheBudget, 2 * GlzWindowSize, decoders?.Jpeg);
    }

    /// <summary>Capacità annunciate dal client senza decoder della piattaforma (nessun codec video).</summary>
    public static CapabilitySet Capabilities { get; } = CapabilitiesFor(null);

    /// <summary>Capacità annunciate dal client: i codec video sono solo quelli decodificabili.</summary>
    public static CapabilitySet CapabilitiesFor(IMediaDecoders? decoders)
    {
        List<int> bits =
        [
            (int)DisplayCapability.SizedStream,
            (int)DisplayCapability.MonitorsConfig,
            (int)DisplayCapability.PrefCompression,
            (int)DisplayCapability.Lz4Compression,
            // Multi-codec senza codec: il server non crea flussi video.
            (int)DisplayCapability.MultiCodec,
        ];
        var codecs = VideoCodecsOf(decoders);
        if (codecs.Count > 0)
        {
            bits.Add((int)DisplayCapability.StreamReport);
            bits.Add((int)DisplayCapability.PrefVideoCodecType);
        }
        foreach (var codec in codecs)
        {
            bits.Add(codec switch
            {
                VideoCodecType.Mjpeg => (int)DisplayCapability.CodecMjpeg,
                VideoCodecType.H264 => (int)DisplayCapability.CodecH264,
                VideoCodecType.H265 => (int)DisplayCapability.CodecH265,
                VideoCodecType.Vp8 => (int)DisplayCapability.CodecVp8,
                _ => (int)DisplayCapability.CodecVp9,
            });
        }
        return CapabilitySet.FromBits([.. bits]);
    }

    /// <summary>
    /// Invia <c>PREFERRED_VIDEO_CODEC_TYPE</c> (H.264 prima di MJPEG). Spento per impostazione
    /// predefinita, come in remote-viewer: QEMU 11.1 per Windows va in crash (e con lui la VM)
    /// appena lo riceve, anche con il solo MJPEG. Senza, il server sceglie fra i codec annunciati
    /// nell'ordine della sua opzione <c>video-codecs</c>.
    /// </summary>
    public bool SendVideoCodecPreference { get; init; }

    /// <summary>Budget delle superfici in pixel (ridotto solo nei test).</summary>
    internal long SurfacePixelBudget { get; init; } = MaxSurfacePixels;

    /// <summary>Messaggi che non si sono potuti decodificare (diagnostica e test).</summary>
    public int DecodeErrorCount { get; private set; }

    /// <summary>Invia <c>DISPLAY_INIT</c> e le preferenze, poi elabora i messaggi fino alla chiusura.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _connection.SendAsync(DisplayClientEncoder.Initialize(1, PixmapCacheBudget, 1, GlzWindowSize), cancellationToken)
                .ConfigureAwait(false);
            if (_connection.ServerCaps.Contains((int)DisplayCapability.PrefCompression))
            {
                // GLZ (dizionario globale) comprime meglio in WAN; il server usa QUIC per le foto.
                await _connection.SendAsync(DisplayClientEncoder.PreferredCompression(ImageCompression.AutoGlz), cancellationToken)
                    .ConfigureAwait(false);
            }
            var codecs = VideoCodecsOf(_decoders);
            if (SendVideoCodecPreference && codecs.Count > 0 && _connection.ServerCaps.Contains((int)DisplayCapability.PrefVideoCodecType))
            {
                // Prima H.264/H.265; il server ripiega su quello che sa codificare.
                await _connection.SendAsync(DisplayClientEncoder.PreferredVideoCodecs(codecs), cancellationToken).ConfigureAwait(false);
            }
            while (true)
            {
                var message = await _connection.NextMessageAsync(cancellationToken).ConfigureAwait(false);
                Handle(message);
                SpiceMessage[] reports;
                lock (_gate)
                {
                    Flush();
                    reports = [.. _pendingReports];
                    _pendingReports.Clear();
                }
                foreach (var report in reports) await _connection.SendAsync(report, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _closed = true;
                DestroyAllStreams();
            }
        }
    }

    /// <summary>Pixel attuali della superficie primaria (istantanee e test).</summary>
    public PixelBuffer? PrimarySurface()
    {
        lock (_gate)
        {
            return _primaryId is { } id && _surfaces.TryGetValue(id, out var surface)
                ? surface.Cropped(0, 0, surface.Width, surface.Height)
                : null;
        }
    }

    /// <summary>Elabora un messaggio; gli errori di decodifica si contano, i limiti superati chiudono il canale.</summary>
    internal void Handle(SpiceMessage message)
    {
        lock (_gate)
        {
            try
            {
                Dispatch(message);
            }
            catch (WireException error) when (error.Kind == WireErrorKind.LimitExceeded)
            {
                // I budget di risorse sono limiti rigidi: il server che li supera viene scollegato.
                SpiceLog.Error("display", $"limite superato ({error.Message}): chiusura");
                throw ChannelException.Malformed("limite di memoria del display superato", error);
            }
            catch (WireException error)
            {
                DecodeErrorCount++;
                SpiceLog.Error("display", $"messaggio {message.Type} non valido: {error.Message}");
            }
            catch (ImageResolveException error)
            {
                DecodeErrorCount++;
                SpiceLog.Error("display", $"immagine non decodificata nel messaggio {message.Type}: {error.Message}");
            }
        }
    }

    private void Dispatch(SpiceMessage message)
    {
        var payload = message.Payload;
        switch ((DisplayServerMessage)message.Type)
        {
            case DisplayServerMessage.SurfaceCreate:
                CreateSurface(SurfaceCreate.Decode(payload));
                break;
            case DisplayServerMessage.SurfaceDestroy:
                DestroySurface(DisplayDecoders.UInt32(payload));
                break;
            case DisplayServerMessage.DrawFill:
            {
                var draw = DrawFill.Decode(payload);
                FillSource source;
                switch (draw.Brush)
                {
                    case Brush.Solid solid:
                        source = new FillSource.Color(solid.Color);
                        break;
                    case Brush.Pattern { Image: { } image } pattern:
                        source = new FillSource.Pattern(Resolve(image), pattern.Position);
                        break;
                    default:
                        return;
                }
                var mask = ImageResolver.Mask(draw.Mask);
                Mutate(draw.Base, s => s.Fill(draw.Base.Box, draw.Base.Clip, source, draw.Rop, mask));
                break;
            }
            case DisplayServerMessage.DrawCopy or DisplayServerMessage.DrawBlend or DisplayServerMessage.DrawOpaque:
            {
                var draw = DrawCopy.Decode(payload, withBrush: message.Type == (ushort)DisplayServerMessage.DrawOpaque);
                if (draw.Source is null) return;
                var source = Resolve(draw.Source);
                var mask = ImageResolver.Mask(draw.Mask);
                Mutate(draw.Base, s => s.Copy(source, draw.SourceArea, draw.Base.Box, draw.Base.Clip, draw.Rop, draw.ScaleMode, mask));
                break;
            }
            case DisplayServerMessage.DrawBlackness or DisplayServerMessage.DrawWhiteness or DisplayServerMessage.DrawInvers:
            {
                var draw = DrawMaskOnly.Decode(payload);
                var rop = (DisplayServerMessage)message.Type switch
                {
                    DisplayServerMessage.DrawBlackness => Ropd.OpBlackness,
                    DisplayServerMessage.DrawWhiteness => Ropd.OpWhiteness,
                    _ => Ropd.OpInvers,
                };
                var mask = ImageResolver.Mask(draw.Mask);
                Mutate(draw.Base, s => s.ApplyUnary(draw.Base.Box, draw.Base.Clip, rop, mask));
                break;
            }
            case DisplayServerMessage.DrawTransparent:
            {
                var draw = DrawTransparent.Decode(payload);
                if (draw.Source is null) return;
                var source = Resolve(draw.Source);
                Mutate(draw.Base, s => s.TransparentCopy(source, draw.SourceArea, draw.Base.Box, draw.Base.Clip, draw.SourceColor));
                break;
            }
            case DisplayServerMessage.DrawAlphaBlend:
            {
                var draw = DrawAlphaBlend.Decode(payload);
                if (draw.Source is null) return;
                var source = Resolve(draw.Source);
                var hasAlpha = draw.SourceHasAlpha || source.HasAlpha;
                Mutate(draw.Base, s => s.AlphaBlend(source, draw.SourceArea, draw.Base.Box, draw.Base.Clip, draw.Alpha, hasAlpha));
                break;
            }
            case DisplayServerMessage.CopyBits:
            {
                var draw = CopyBits.Decode(payload);
                Mutate(draw.Base, s => s.CopyBits(draw.SourcePosition, draw.Base.Box, draw.Base.Clip));
                break;
            }
            case DisplayServerMessage.InvalList:
                foreach (var id in DisplayDecoders.InvalidateList(payload)) _resolver.InvalidatePixmap(id);
                break;
            case DisplayServerMessage.InvalAllPixmaps:
                _resolver.InvalidateAllPixmaps();
                break;
            case DisplayServerMessage.InvalPalette:
                _resolver.InvalidatePalette(DisplayDecoders.UInt64(payload));
                break;
            case DisplayServerMessage.InvalAllPalettes:
                _resolver.InvalidateAllPalettes();
                break;
            case DisplayServerMessage.MonitorsConfig:
                _events.TryWrite(new DisplayEvent.MonitorsConfig(DisplayDecoders.MonitorsConfig(payload)));
                break;
            case DisplayServerMessage.Reset:
                _resolver.InvalidateAllPixmaps();
                _resolver.InvalidateAllPalettes();
                DestroyAllStreams();
                break;
            case DisplayServerMessage.Mark or DisplayServerMessage.Mode:
                break;
            case DisplayServerMessage.StreamCreate:
                CreateStream(StreamCreate.Decode(payload.AsSpan()));
                break;
            case DisplayServerMessage.StreamData or DisplayServerMessage.StreamDataSized:
                StreamFrame(StreamData.Decode(payload, sized: message.Type == (ushort)DisplayServerMessage.StreamDataSized));
                break;
            case DisplayServerMessage.StreamClip:
                var clip = StreamClip.Decode(payload);
                if (_streams.TryGetValue(clip.StreamId, out var clipped)) clipped.Clip = clip.Clip;
                break;
            case DisplayServerMessage.StreamDestroy:
                DestroyStream(DisplayDecoders.UInt32(payload));
                break;
            case DisplayServerMessage.StreamDestroyAll:
                DestroyAllStreams();
                break;
            case DisplayServerMessage.StreamActivateReport:
                ActivateReport(StreamActivateReport.Decode(payload));
                break;
            case DisplayServerMessage.QualityIndicator or DisplayServerMessage.GlScanoutUnix or DisplayServerMessage.GlDraw:
                break; // non annunciati
            default:
                WarnOnce(message.Type, "comando di disegno non supportato");
                break;
        }
    }

    private void CreateSurface(SurfaceCreate create)
    {
        if (create.Width == 0 || create.Height == 0 || create.Width > MaxSurfaceDimension || create.Height > MaxSurfaceDimension)
        {
            throw WireException.LimitExceeded($"superficie {create.Width}x{create.Height}");
        }
        var exists = _surfaces.TryGetValue(create.SurfaceId, out var old);
        if (!exists && _surfaces.Count >= MaxSurfaces) throw WireException.LimitExceeded("troppe superfici");
        // Prenotazione sul budget prima di allocare (la superficie sostituita si libera).
        var replaced = exists ? (long)old!.Width * old.Height : 0;
        var requested = (long)create.Width * create.Height;
        if (_surfacePixels - replaced + requested > SurfacePixelBudget) throw WireException.LimitExceeded("memoria delle superfici");
        _surfacePixels += requested - replaced;
        var hasAlpha = create.Format is SurfaceFormat.Argb32 or SurfaceFormat.A8 or SurfaceFormat.A1;
        var buffer = new PixelBuffer((int)create.Width, (int)create.Height, hasAlpha ? 0u : 0xFF00_0000, hasAlpha);
        _surfaces[create.SurfaceId] = buffer;
        if (create.IsPrimary)
        {
            _primaryId = create.SurfaceId;
            _dirty = SpiceRect.Empty;
            _events.TryWrite(new DisplayEvent.PrimarySurfaceCreated(create.SurfaceId, buffer.Width, buffer.Height));
            _dirty = buffer.Bounds;
        }
    }

    private void DestroySurface(uint id)
    {
        if (!_surfaces.Remove(id, out var removed)) return;
        _surfacePixels -= (long)removed.Width * removed.Height;
        if (id == _primaryId)
        {
            _primaryId = null;
            _dirty = SpiceRect.Empty;
            _events.TryWrite(new DisplayEvent.PrimarySurfaceDestroyed(id));
        }
    }

    private PixelBuffer Resolve(SpiceImage image) =>
        _resolver.Resolve(image, id => _surfaces.GetValueOrDefault(id));

    /// <summary>Applica un'operazione alla superficie di destinazione e registra l'area sporca.</summary>
    private void Mutate(DisplayBase displayBase, Func<PixelBuffer, SpiceRect> body)
    {
        if (!_surfaces.TryGetValue(displayBase.SurfaceId, out var surface))
        {
            SpiceLog.Debug("display", $"disegno su superficie sconosciuta {displayBase.SurfaceId}");
            return;
        }
        var changed = body(surface);
        if (displayBase.SurfaceId == _primaryId && !changed.IsEmpty) _dirty = _dirty.Union(changed);
    }

    /// <summary>Pubblica l'area sporca accumulata della superficie primaria (con il lock preso).</summary>
    private void Flush()
    {
        if (_closed || _dirty.IsEmpty || _primaryId is not { } id || !_surfaces.TryGetValue(id, out var surface)) return;
        var clipped = _dirty.Intersect(surface.Bounds);
        if (clipped.IsEmpty)
        {
            _dirty = SpiceRect.Empty;
            return;
        }
        if (_credit is not null && !_credit.TryAcquire())
        {
            // Il renderer è indietro: il danno continua ad accumularsi; si riprova fra poco.
            if (!_retryScheduled)
            {
                _retryScheduled = true;
                _ = RetryFlushAsync();
            }
            return;
        }
        _dirty = SpiceRect.Empty;
        var region = surface.Cropped(clipped.Left, clipped.Top, clipped.Width, clipped.Height);
        _events.TryWrite(new DisplayEvent.Update(new SurfaceUpdate(id, clipped, region.Pixels)));
    }

    private async Task RetryFlushAsync()
    {
        await Task.Delay(4).ConfigureAwait(false);
        lock (_gate)
        {
            _retryScheduled = false;
            Flush();
        }
    }

    private void WarnOnce(ushort type, string text)
    {
        if (_warned.Add(type)) SpiceLog.Info("display", $"{text} (tipo {type})");
    }
}

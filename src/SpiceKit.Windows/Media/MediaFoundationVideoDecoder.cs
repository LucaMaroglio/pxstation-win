using System.Runtime.InteropServices;
using SharpGen.Runtime;
using SpiceKit.Codecs;
using SpiceKit.Wire;
using Vortice.MediaFoundation;

namespace SpiceKit.Windows.Media;

/// <summary>
/// Decodifica H.264 / H.265 dei flussi video SPICE con un decoder Media Foundation (MFT sincrono,
/// quello di Windows) e converte l'uscita NV12 in BGRA. Porting del ruolo di <c>VideoDecoder.swift</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>L'ingresso è Annex B, che il decoder di Windows accetta direttamente (insiemi di
///   parametri compresi nei fotogrammi chiave, come li produce spice-server).</item>
/// <item>Bassa latenza (<c>CODECAPI_AVLowLatencyMode</c>): ogni unità di accesso produce subito il suo fotogramma.</item>
/// <item>Fino al primo fotogramma chiave non si decodifica nulla; dopo un errore il decoder si
///   svuota e aspetta il fotogramma chiave successivo.</item>
/// <item>Decodifica software: l'accelerazione DXVA (dispositivo Direct3D condiviso) è un passo successivo.</item>
/// </list>
/// Non thread-safe: un'istanza per flusso, usata dal ciclo del canale display.
/// </remarks>
public sealed class MediaFoundationVideoDecoder : IVideoDecoder
{
    /// <summary><c>CODECAPI_AVLowLatencyMode</c> (= <c>MF_LOW_LATENCY</c>), codecapi.h.</summary>
    private static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    /// <summary><c>MF_MT_MINIMUM_DISPLAY_APERTURE</c>, mfapi.h.</summary>
    private static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");

    /// <summary><c>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES</c> | <c>MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES</c>.</summary>
    private const int ProvidesSamples = 0x100 | 0x200;

    /// <summary><c>MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER</c>.</summary>
    private const uint EnumFlags = 0x1 | 0x8 | 0x40;

    /// <summary><c>MF_E_NOTACCEPTING</c>.</summary>
    private const int NotAccepting = unchecked((int)0xC00D36B5);

    /// <summary><c>MFVideoTransferMatrix_BT709</c> (mfobjects.h).</summary>
    private const uint MatrixBt709 = 1;

    private static readonly Lazy<bool> Started = new(() => MediaFactory.MFStartup(false).Success);

    private readonly IMFTransform _transform;
    private readonly bool _hevc;
    private bool _waitingForKeyFrame = true;
    private int _width;
    private int _height;
    private int _visibleWidth;
    private int _visibleHeight;
    private int _defaultStride;
    private YuvMatrix _matrix;
    private bool _providesSamples;
    private int _outputSize;
    private long _time;

    private MediaFoundationVideoDecoder(IMFTransform transform, bool hevc)
    {
        _transform = transform;
        _hevc = hevc;
    }

    /// <summary>Codec che Windows sa decodificare su questo PC (il decoder HEVC richiede un'estensione dallo Store).</summary>
    public static bool IsSupported(VideoCodecType codec)
    {
        if (codec is not (VideoCodecType.H264 or VideoCodecType.H265) || !Started.Value) return false;
        try
        {
            using var found = Enumerate(codec);
            return found.Any();
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    /// <summary>Nuovo decoder, o <see langword="null"/> se Windows non ne ha uno per il codec.</summary>
    public static MediaFoundationVideoDecoder? Create(VideoCodecType codec)
    {
        if (!IsSupported(codec)) return null;
        IMFTransform? transform = null;
        try
        {
            // Il MFT vive da sé: l'elenco (con i suoi IMFActivate) si rilascia subito.
            using (var found = Enumerate(codec)) transform = found.First().ActivateObject<IMFTransform>();
            var decoder = new MediaFoundationVideoDecoder(transform, codec == VideoCodecType.H265);
            decoder.Configure();
            return decoder;
        }
        catch (Exception error) when (error is SharpGenException or CodecException)
        {
            transform?.Dispose();
            return null;
        }
    }

    private static IMFActivateCollection Enumerate(VideoCodecType codec) => MediaFactory.MFTEnumEx(
        TransformCategoryGuids.VideoDecoder, EnumFlags,
        new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = Subtype(codec) },
        new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 });

    private static Guid Subtype(VideoCodecType codec) => codec == VideoCodecType.H265 ? VideoFormatGuids.Hevc : VideoFormatGuids.H264;

    private void Configure()
    {
        try
        {
            _transform.Attributes.Set(LowLatencyMode, 1u);
        }
        catch (SharpGenException)
        {
            // Non tutti i decoder espongono gli attributi: senza, qualche fotogramma di ritardo.
        }
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, Subtype(_hevc ? VideoCodecType.H265 : VideoCodecType.H264));
            _transform.SetInputType(0, input, 0);
        }
        ConfigureOutput();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    /// <summary>Sceglie l'uscita NV12 e ne legge misura, passo e matrice (anche dopo un cambio di formato).</summary>
    private void ConfigureOutput()
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException)
            {
                throw CodecException.Corrupt("video: il decoder non offre NV12");
            }
            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;
                _transform.SetOutputType(0, type, 0);
                break;
            }
        }
        using var current = _transform.GetOutputCurrentType(0);
        if (MediaFactory.MFGetAttributeSize(current, MediaTypeAttributeKeys.FrameSize, out var width, out var height).Success)
        {
            _width = (int)width;
            _height = (int)height;
        }
        (_visibleWidth, _visibleHeight) = (_width, _height);
        if (current.GetBlobSize(MinimumDisplayAperture, out var size).Success && size >= 16)
        {
            // MFVideoArea: OffsetX, OffsetY (MFOffset, 4 byte ciascuno), poi SIZE { cx, cy }.
            var area = current.GetBlob(MinimumDisplayAperture);
            var cx = BitConverter.ToInt32(area, 8);
            var cy = BitConverter.ToInt32(area, 12);
            if (cx > 0 && cy > 0 && cx <= _width && cy <= _height) (_visibleWidth, _visibleHeight) = (cx, cy);
        }
        _defaultStride = current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var stride).Success ? (int)stride : _width;
        _matrix = MediaFactory.MFGetAttributeUInt32(current, MediaTypeAttributeKeys.YuvMatrix, 0) == MatrixBt709
            ? YuvMatrix.Bt709
            : YuvMatrix.Bt601;
        var info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & ProvidesSamples) != 0;
        _outputSize = info.Size;
    }

    public PixelBuffer? Decode(ReadOnlySpan<byte> accessUnit)
    {
        if (_waitingForKeyFrame)
        {
            if (!AnnexB.HasKeyFrame(accessUnit, _hevc)) return null;
            _waitingForKeyFrame = false;
        }
        try
        {
            using var sample = Sample(accessUnit);
            PixelBuffer? frame = null;
            try
            {
                _transform.ProcessInput(0, sample, 0);
            }
            catch (SharpGenException error) when (error.ResultCode.Code == NotAccepting)
            {
                // Fotogrammi ancora in uscita: prima si raccolgono, poi si riprova.
                frame = Drain();
                _transform.ProcessInput(0, sample, 0);
            }
            return Drain() ?? frame;
        }
        catch (SharpGenException error)
        {
            Reset();
            throw CodecException.Corrupt($"video: errore del decoder 0x{error.ResultCode.Code:X8}");
        }
        catch (CodecException)
        {
            Reset();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
        }
        catch (SharpGenException)
        {
        }
        _transform.Dispose();
    }

    private void Reset()
    {
        _waitingForKeyFrame = true;
        try
        {
            _transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
        }
        catch (SharpGenException)
        {
        }
    }

    private unsafe IMFSample Sample(ReadOnlySpan<byte> data)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        try
        {
            buffer.Lock(out var pointer, out _, out _);
            data.CopyTo(new Span<byte>((void*)pointer, data.Length));
            buffer.Unlock();
            buffer.CurrentLength = data.Length;
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = _time;
            sample.SampleDuration = 333_333; // 30 fotogrammi al secondo, in unità di 100 ns
            _time += 333_333;
            return sample;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>Raccoglie i fotogrammi pronti; restituisce l'ultimo (quelli prima sono già superati).</summary>
    private PixelBuffer? Drain()
    {
        PixelBuffer? last = null;
        for (var guard = 0; guard < 16; guard++)
        {
            using var provided = _providesSamples ? null : MediaFactory.MFCreateSample();
            if (provided is not null)
            {
                using var memory = MediaFactory.MFCreateMemoryBuffer(Math.Max(_outputSize, _width * _height * 3 / 2));
                provided.AddBuffer(memory);
            }
            var result = TransformOutput.Process(_transform, provided, out var produced);
            using (produced)
            {
                if (result == ResultCode.TransformNeedMoreInput) return last;
                if (result == ResultCode.TransformStreamChange)
                {
                    ConfigureOutput();
                    continue;
                }
                result.CheckError();
                if ((produced ?? provided) is { } sample) last = Convert(sample);
            }
        }
        return last;
    }

    private unsafe PixelBuffer Convert(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        using var planar = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        nint data;
        int pitch, length;
        if (planar is not null)
        {
            planar.Lock2D(out data, out pitch);
            length = pitch * (_height + ((_height + 1) / 2));
        }
        else
        {
            buffer.Lock(out data, out _, out length);
            pitch = _defaultStride;
        }
        try
        {
            if (pitch <= 0 || _width <= 0 || _height <= 0 || length < pitch * (_height + ((_height + 1) / 2)))
            {
                throw CodecException.Corrupt("video: buffer NV12 inatteso");
            }
            var bytes = new ReadOnlySpan<byte>((void*)data, length);
            return Nv12Converter.Convert(bytes[..(pitch * _height)], bytes[(pitch * _height)..], pitch,
                _visibleWidth, _visibleHeight, _matrix);
        }
        finally
        {
            if (planar is not null) planar.Unlock2D();
            else buffer.Unlock();
        }
    }
}

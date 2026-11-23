using Vortice.MediaFoundation;

namespace SpiceKit.Tests.Windows;

/// <summary>
/// Codifica fotogrammi di prova in H.264 (Annex B) con il codificatore Media Foundation di
/// Windows, per provare il decoder senza fixture binarie. <see langword="null"/> se manca
/// (edizioni N); un errore di configurazione fa fallire il test.
/// </summary>
internal static class H264TestEncoder
{
    /// <summary><c>MF_MT_MPEG2_PROFILE</c> (mfapi.h).</summary>
    private static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

    public static List<byte[]>? Encode(int width, int height, int count)
    {
        if (!MediaFactory.MFStartup(false).Success) return null;
        using var found = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x1 | 0x8 | 0x40,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });
        var activate = found.FirstOrDefault();
        if (activate is null) return null;
        using var encoder = activate.ActivateObject<IMFTransform>();
        try
        {
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, 2_000_000u);
                output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressivo
                output.Set(Mpeg2Profile, 66u); // eAVEncH264VProfile_Base
                MediaFactory.MFSetAttributeSize(output, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.FrameRate, 30, 1);
                MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                encoder.SetOutputType(0, output, 0);
            }
            using (var input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
                MediaFactory.MFSetAttributeSize(input, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.FrameRate, 30, 1);
                MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                encoder.SetInputType(0, input, 0);
            }
            encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
            encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);

            var picture = Nv12Picture(width, height);
            var frames = new List<byte[]>();
            for (var i = 0; i < count; i++)
            {
                using var sample = Sample(picture, i);
                encoder.ProcessInput(0, sample, 0);
                Drain(encoder, frames);
            }
            encoder.ProcessMessage(TMessageType.MessageCommandDrain, 0);
            Drain(encoder, frames);
            return frames.Count > 0 ? frames : null;
        }
        finally
        {
            activate.ShutdownObject();
            activate.Dispose();
        }
    }

    /// <summary>Le quattro bande di <see cref="MediaDecoderTests.ColorAt"/> in NV12 (BT.601, intervallo limitato).</summary>
    private static byte[] Nv12Picture(int width, int height)
    {
        var data = new byte[width * height * 3 / 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (luma, u, v) = Yuv(MediaDecoderTests.ColorAt(x * 32 / width));
                data[(y * width) + x] = luma;
                if ((x & 1) == 0 && (y & 1) == 0)
                {
                    var offset = (width * height) + (y / 2 * width) + x;
                    data[offset] = u;
                    data[offset + 1] = v;
                }
            }
        }
        return data;
    }

    private static (byte Y, byte U, byte V) Yuv(uint color)
    {
        double r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF;
        return ((byte)Math.Round(16 + (((65.481 * r) + (128.553 * g) + (24.966 * b)) / 255)),
            (byte)Math.Round(128 + (((-37.797 * r) - (74.203 * g) + (112.0 * b)) / 255)),
            (byte)Math.Round(128 + (((112.0 * r) - (93.786 * g) - (18.214 * b)) / 255)));
    }

    private static unsafe IMFSample Sample(byte[] picture, int index)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(picture.Length);
        buffer.Lock(out var pointer, out _, out _);
        picture.CopyTo(new Span<byte>((void*)pointer, picture.Length));
        buffer.Unlock();
        buffer.CurrentLength = picture.Length;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = index * 333_333L;
        sample.SampleDuration = 333_333;
        return sample;
    }

    private static unsafe void Drain(IMFTransform encoder, List<byte[]> frames)
    {
        while (true)
        {
            var info = encoder.GetOutputStreamInfo(0);
            using var provided = (info.Flags & 0x300) == 0 ? MediaFactory.MFCreateSample() : null;
            if (provided is not null)
            {
                using var memory = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 1 << 20));
                provided.AddBuffer(memory);
            }
            var result = SpiceKit.Windows.Media.TransformOutput.Process(encoder, provided, out var produced);
            using var owned = produced;
            if (result == ResultCode.TransformNeedMoreInput) return;
            if (result == ResultCode.TransformStreamChange) continue;
            result.CheckError();
            if ((produced ?? provided) is not { } sample) return;
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var pointer, out _, out var length);
            frames.Add(new ReadOnlySpan<byte>((void*)pointer, length).ToArray());
            buffer.Unlock();
        }
    }
}

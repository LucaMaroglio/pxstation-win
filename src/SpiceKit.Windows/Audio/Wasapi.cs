using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SpiceKit.Windows.Audio;

// Interfacce WASAPI (mmdeviceapi.h, audioclient.h) dichiarate con il generatore COM di .NET:
// nessuna dipendenza esterna. Solo i metodi usati hanno firme complete; gli altri occupano il
// loro posto nella vtable con firme semplici e non si chiamano mai.

/// <summary><c>WAVEFORMATEX</c> per PCM a 16 bit.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;

    public static WaveFormat Pcm16(int channels, int sampleRate) => new()
    {
        FormatTag = 1, // WAVE_FORMAT_PCM
        Channels = (ushort)channels,
        SamplesPerSec = (uint)sampleRate,
        AvgBytesPerSec = (uint)(sampleRate * channels * 2),
        BlockAlign = (ushort)(channels * 2),
        BitsPerSample = 16,
    };
}

internal enum DataFlow
{
    Render = 0,
    Capture = 1,
}

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(DataFlow flow, uint stateMask, out nint devices);

    /// <summary>Dispositivo predefinito; <paramref name="role"/>: 0 = eConsole, 2 = eCommunications.</summary>
    IMMDevice GetDefaultAudioEndpoint(DataFlow flow, int role);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid iid, uint context, nint parameters, out nint instance);
}

[GeneratedComInterface]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
internal unsafe partial interface IAudioClient
{
    void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, WaveFormat* format, Guid* session);

    uint GetBufferSize();

    long GetStreamLatency();

    uint GetCurrentPadding();

    void IsFormatSupported(int shareMode, WaveFormat* format, WaveFormat** closest);

    void GetMixFormat(WaveFormat** format);

    void GetDevicePeriod(long* defaultPeriod, long* minimumPeriod);

    void Start();

    void Stop();

    void Reset();

    void SetEventHandle(nint handle);

    void GetService(in Guid iid, out nint service);
}

[GeneratedComInterface]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal unsafe partial interface IAudioRenderClient
{
    byte* GetBuffer(uint frames);

    void ReleaseBuffer(uint frames, uint flags);
}

[GeneratedComInterface]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
internal unsafe partial interface IAudioCaptureClient
{
    void GetBuffer(byte** data, uint* frames, uint* flags, ulong* devicePosition, ulong* qpcPosition);

    void ReleaseBuffer(uint frames);

    uint GetNextPacketSize();
}

/// <summary>Apertura di un client audio WASAPI condiviso, con conversione automatica del formato.</summary>
internal static unsafe partial class Wasapi
{
    /// <summary><c>AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY</c>: Windows converte formato e frequenza.</summary>
    public const uint AutoConvertFlags = 0x8000_0000 | 0x0800_0000;

    /// <summary><c>AUDCLNT_BUFFERFLAGS_SILENT</c>.</summary>
    public const uint BufferSilent = 0x2;

    private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Client per il dispositivo predefinito, inizializzato per PCM 16 bit nel formato dato.</summary>
    public static IAudioClient Open(DataFlow flow, int channels, int sampleRate, TimeSpan buffer)
    {
        var enumerator = Create<IMMDeviceEnumerator>(EnumeratorClsid);
        // Microfono: il dispositivo "comunicazioni" (cuffie con microfono); altoparlanti: quello normale.
        var device = enumerator.GetDefaultAudioEndpoint(flow, flow == DataFlow.Capture ? 2 : 0);
        var iid = typeof(IAudioClient).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(iid, 0x17 /* CLSCTX_ALL */, 0, out var pointer));
        var client = Wrap<IAudioClient>(pointer);
        var format = WaveFormat.Pcm16(channels, sampleRate);
        client.Initialize(0 /* condiviso */, AutoConvertFlags, buffer.Ticks, 0, &format, null);
        return client;
    }

    public static T Service<T>(IAudioClient client) where T : class
    {
        client.GetService(typeof(T).GUID, out var pointer);
        return Wrap<T>(pointer);
    }

    private static T Create<T>(Guid clsid) where T : class
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, 0x17, typeof(T).GUID, out var pointer));
        return Wrap<T>(pointer);
    }

    private static T Wrap<T>(nint pointer) where T : class
    {
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(pointer); // il wrapper ha il suo riferimento
        }
    }

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}

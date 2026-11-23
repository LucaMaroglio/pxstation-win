using System.Runtime.InteropServices;
using SpiceKit.Audio;

namespace SpiceKit.Windows.Audio;

/// <summary>
/// Uscita audio WASAPI sul dispositivo predefinito: un thread prende i campioni dal
/// <see cref="JitterBuffer"/> e tiene pieno il buffer di Windows per circa 40 ms.
/// </summary>
/// <remarks>
/// Formato del flusso SPICE (PCM 16 bit, canali e frequenza del server); Windows converte verso il
/// formato del dispositivo. Se il dispositivo cambia o sparisce, si riapre quello nuovo.
/// Volume e silenzio dell'ospite si applicano come guadagno in software.
/// </remarks>
public sealed class WasapiRenderer : IDisposable
{
    private static readonly TimeSpan BufferDuration = TimeSpan.FromMilliseconds(100);
    private const int TargetMilliseconds = 40;

    private readonly JitterBuffer _buffer;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile float _gain = 1;

    public WasapiRenderer(JitterBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio SPICE", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Guadagno 0…1 (volume del mixer dell'ospite, 0 se in silenzio).</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 0, 1);
    }

    /// <summary>Ultimo errore del dispositivo (nessun altoparlante, accesso negato…), per la diagnostica.</summary>
    public string? DeviceError { get; private set; }

    /// <summary>Il dispositivo è aperto e suona.</summary>
    public bool IsPlaying { get; private set; }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        while (!_stopping)
        {
            try
            {
                Play();
            }
            catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException)
            {
                // Nessun dispositivo, dispositivo cambiato o scollegato: si riprova fra poco.
                IsPlaying = false;
                DeviceError = error.Message;
                for (var i = 0; i < 20 && !_stopping; i++) Thread.Sleep(50);
            }
        }
    }

    private unsafe void Play()
    {
        var client = Wasapi.Open(DataFlow.Render, _buffer.Channels, _buffer.SampleRate, BufferDuration);
        var render = Wasapi.Service<IAudioRenderClient>(client);
        var bufferFrames = (int)client.GetBufferSize();
        var target = Math.Min(bufferFrames, _buffer.SampleRate * TargetMilliseconds / 1000);
        var channels = _buffer.Channels;
        client.Start();
        IsPlaying = true;
        DeviceError = null;
        try
        {
            while (!_stopping)
            {
                var padding = (int)client.GetCurrentPadding();
                var frames = target - padding;
                if (frames > 0)
                {
                    var data = render.GetBuffer((uint)frames);
                    var samples = new Span<short>(data, frames * channels);
                    _buffer.Read(samples);
                    var gain = _gain;
                    if (gain < 0.999f)
                    {
                        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(samples[i] * gain);
                    }
                    render.ReleaseBuffer((uint)frames, 0);
                }
                Thread.Sleep(5);
            }
        }
        finally
        {
            IsPlaying = false;
            client.Stop();
        }
    }
}

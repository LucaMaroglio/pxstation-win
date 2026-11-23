using System.Runtime.InteropServices;

namespace SpiceKit.Windows.Audio;

/// <summary>
/// Microfono WASAPI (dispositivo predefinito per le comunicazioni): consegna pacchetti di 10 ms
/// di PCM S16LE interlacciato nel formato chiesto dall'ospite.
/// </summary>
/// <remarks>
/// Gira solo mentre è acceso (l'utente lo attiva e l'ospite registra). Se Windows nega l'accesso
/// al microfono (Impostazioni › Privacy), <see cref="DeviceError"/> lo dice e non si riprova.
/// </remarks>
public sealed class WasapiCapture : IDisposable
{
    /// <summary><c>E_ACCESSDENIED</c>: accesso al microfono disattivato nelle impostazioni di Windows.</summary>
    private const int AccessDenied = unchecked((int)0x80070005);

    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly Action<ReadOnlyMemory<byte>> _deliver;
    private readonly Thread _thread;
    private volatile bool _stopping;

    /// <param name="channels">Canali chiesti dall'ospite.</param>
    /// <param name="sampleRate">Frequenza chiesta dall'ospite.</param>
    /// <param name="deliver">Riceve ogni pacchetto (sul thread di cattura; non bloccare).</param>
    public WasapiCapture(int channels, int sampleRate, Action<ReadOnlyMemory<byte>> deliver)
    {
        _channels = Math.Clamp(channels, 1, 2);
        _sampleRate = sampleRate;
        _deliver = deliver;
        _thread = new Thread(Run) { IsBackground = true, Name = "Microfono SPICE", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public string? DeviceError { get; private set; }

    /// <summary>Accesso negato da Windows (privacy del microfono).</summary>
    public bool AccessDeniedByWindows { get; private set; }

    public bool IsCapturing { get; private set; }

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
                Capture();
            }
            catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException)
            {
                IsCapturing = false;
                DeviceError = error.Message;
                if (error.HResult == AccessDenied)
                {
                    AccessDeniedByWindows = true;
                    return;
                }
                for (var i = 0; i < 20 && !_stopping; i++) Thread.Sleep(50);
            }
        }
    }

    private unsafe void Capture()
    {
        var client = Wasapi.Open(DataFlow.Capture, _channels, _sampleRate, TimeSpan.FromMilliseconds(100));
        var capture = Wasapi.Service<IAudioCaptureClient>(client);
        var frameBytes = _channels * 2;
        var packetBytes = _sampleRate / 100 * frameBytes; // 10 ms
        var pending = new byte[packetBytes * 20];
        var filled = 0;
        client.Start();
        IsCapturing = true;
        DeviceError = null;
        try
        {
            while (!_stopping)
            {
                while (capture.GetNextPacketSize() > 0)
                {
                    byte* data;
                    uint frames, flags;
                    capture.GetBuffer(&data, &frames, &flags, null, null);
                    var bytes = (int)frames * frameBytes;
                    if (filled + bytes > pending.Length) filled = 0; // non si accumula mai senza limite
                    var target = pending.AsSpan(filled, bytes);
                    if ((flags & Wasapi.BufferSilent) != 0) target.Clear();
                    else new ReadOnlySpan<byte>(data, bytes).CopyTo(target);
                    filled += bytes;
                    capture.ReleaseBuffer(frames);
                    while (filled >= packetBytes)
                    {
                        _deliver(pending.AsSpan(0, packetBytes).ToArray());
                        pending.AsSpan(packetBytes, filled - packetBytes).CopyTo(pending);
                        filled -= packetBytes;
                    }
                }
                Thread.Sleep(5);
            }
        }
        finally
        {
            IsCapturing = false;
            client.Stop();
        }
    }
}

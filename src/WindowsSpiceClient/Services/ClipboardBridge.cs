using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using SpiceKit.Agent;
using SpiceKit.Session;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Input;

namespace WindowsSpiceClient.Services;

/// <summary>
/// Appunti condivisi con l'ospite tramite spice-vdagent: testo e immagini PNG (porting di
/// <c>ClipboardController.swift</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Windows → ospite: solo con la finestra della sessione attiva, un cambio degli appunti
///   viene <em>offerto</em> (solo i tipi); i dati partono quando l'ospite li chiede, cioè quando
///   l'utente incolla. Si risponde solo con ciò che è stato offerto e se gli appunti non sono
///   cambiati nel frattempo (per esempio una password copiata in un'altra app).</item>
/// <item>Ospite → Windows: quando l'ospite copia, i dati si chiedono e si scrivono negli appunti;
///   si accettano solo i dati del tipo richiesto. Le immagini si controllano (misura dichiarata)
///   e si ricodificano prima che altre app le vedano.</item>
/// </list>
/// Tutto sul thread dell'interfaccia (le API degli appunti lo richiedono).
/// </remarks>
internal sealed class ClipboardBridge : IDisposable
{
    /// <summary>Immagine più grande accettata dall'ospite (pixel).</summary>
    private const long MaxImagePixels = 64L * 1024 * 1024;

    private readonly Func<SpiceSession?> _session;
    private readonly Func<bool> _isActive;
    private readonly DispatcherQueueTimer _timer;
    private bool _available;
    private uint _handledSequence;
    private ClipboardDataType[] _offered = [];
    private ClipboardDataType? _pendingRequest;

    public ClipboardBridge(DispatcherQueue dispatcher, Func<SpiceSession?> session, Func<bool> isActive)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _session = session;
        _isActive = isActive;
        _handledSequence = Native.GetClipboardSequenceNumber();
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => OfferIfChanged();
        _timer.Start();
    }

    /// <summary>L'agente può (o non più) condividere gli appunti.</summary>
    public void SetAvailable(bool available)
    {
        _available = available;
        _pendingRequest = null;
        // Un agente appena collegato vede subito ciò che è già negli appunti di Windows.
        if (available) _handledSequence = 0;
    }

    public void Dispose() => _timer.Stop();

    // MARK: Windows → ospite

    private void OfferIfChanged()
    {
        if (!_available || !_isActive() || _session() is not { ClipboardSupported: true } session) return;
        var sequence = Native.GetClipboardSequenceNumber();
        if (sequence == _handledSequence) return;
        ClipboardDataType[] types;
        try
        {
            var view = Clipboard.GetContent();
            types = [.. Shareable(view)];
        }
        catch (COMException)
        {
            return; // appunti occupati da un'altra app: si riprova al prossimo giro
        }
        _handledSequence = sequence;
        _offered = types;
        if (types.Length == 0) session.ReleaseClipboard();
        else session.OfferClipboard(types);
    }

    private static IEnumerable<ClipboardDataType> Shareable(DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.Text)) yield return ClipboardDataType.Utf8Text;
        if (view.Contains(StandardDataFormats.Bitmap)) yield return ClipboardDataType.Png;
    }

    // MARK: ospite → Windows

    public async Task HandleAsync(ClipboardMessage message)
    {
        if (_session() is not { } session) return;
        switch (message)
        {
            case ClipboardMessage.Grab grab:
                var wanted = new[] { ClipboardDataType.Utf8Text, ClipboardDataType.Png }.FirstOrDefault(grab.Types.Contains);
                if (wanted == ClipboardDataType.None) return;
                _pendingRequest = wanted;
                session.RequestClipboard(wanted);
                break;
            case ClipboardMessage.Request request:
                // Solo esattamente ciò che è stato offerto, e solo se gli appunti non sono cambiati;
                // una risposta vuota evita che l'ospite resti in attesa.
                var allowed = _offered.Contains(request.Type) && Native.GetClipboardSequenceNumber() == _handledSequence;
                var content = allowed ? await ReadAsync(request.Type) : [];
                if (content.Length > ClipboardCodec.MaxDataSize) content = [];
                session.ProvideClipboard(request.Type, content);
                break;
            case ClipboardMessage.Data received when received.Type == _pendingRequest && received.Bytes.Length > 0:
                _pendingRequest = null;
                await WriteAsync(received.Type, received.Bytes);
                break;
        }
    }

    private static async Task<byte[]> ReadAsync(ClipboardDataType type)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (type == ClipboardDataType.Utf8Text && view.Contains(StandardDataFormats.Text))
            {
                return Encoding.UTF8.GetBytes(await view.GetTextAsync());
            }
            if (type == ClipboardDataType.Png && view.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await view.GetBitmapAsync();
                using var source = await reference.OpenReadAsync();
                return await ToPngAsync(source) ?? [];
            }
        }
        catch (COMException error)
        {
            AppLog.Info($"Appunti non leggibili: {error.Message}");
        }
        return [];
    }

    private async Task WriteAsync(ClipboardDataType type, byte[] bytes)
    {
        var package = new DataPackage();
        if (type == ClipboardDataType.Utf8Text)
        {
            package.SetText(Encoding.UTF8.GetString(bytes));
        }
        else
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            input.Seek(0);
            if (await ToPngAsync(input) is not { } png) return;
            var output = new InMemoryRandomAccessStream();
            await output.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(png));
            output.Seek(0);
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(output));
        }
        try
        {
            Clipboard.SetContent(package);
            Clipboard.Flush(); // restano negli appunti anche dopo la chiusura dell'app
        }
        catch (COMException error)
        {
            AppLog.Info($"Appunti non scrivibili: {error.Message}");
            return;
        }
        // I dati dell'ospite non gli si offrono indietro.
        _handledSequence = Native.GetClipboardSequenceNumber();
        _offered = [];
    }

    /// <summary>Decodifica (dopo aver controllato la misura dichiarata) e ricodifica in PNG; <see langword="null"/> se non valida.</summary>
    private static async Task<byte[]?> ToPngAsync(IRandomAccessStream source)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(source);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (long)decoder.PixelWidth * decoder.PixelHeight > MaxImagePixels) return null;
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            output.Seek(0);
            await output.ReadAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
        {
            AppLog.Info($"Immagine degli appunti scartata: {error.Message}");
            return null;
        }
    }
}

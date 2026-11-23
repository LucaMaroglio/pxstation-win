using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SpiceKit.Channels;
using SpiceKit.Codecs;
using WindowsSpiceClient.Display;

namespace WindowsSpiceClient.Input;

/// <summary>
/// Il cursore dell'ospite: in modalità client è il cursore di sistema sopra lo schermo (scalato
/// come lo schermo); in modalità server un'immagine sopra il pannello, nella posizione indicata
/// dal server, mentre il puntatore di Windows è nascosto e catturato.
/// </summary>
internal sealed class GuestCursor : IDisposable
{
    private readonly DisplaySurface _panel;
    private readonly Image _overlay;
    private readonly Func<SurfaceFit?> _fit;
    private readonly CursorFactory _factory = new();
    private CursorImage? _shape;
    private bool _visible = true;
    private int _x;
    private int _y;
    private bool _clientMode = true;
    private bool _locked;
    private (CursorImage Shape, double Scale, InputCursor? Cursor)? _system;
    private (CursorImage Shape, WriteableBitmap Bitmap)? _bitmap;

    public GuestCursor(DisplaySurface panel, Image overlay, Func<SurfaceFit?> fit)
    {
        _panel = panel;
        _overlay = overlay;
        _fit = fit;
    }

    public void Apply(CursorEvent value)
    {
        switch (value)
        {
            case CursorEvent.Shape shape:
                _shape = shape.Image;
                break;
            case CursorEvent.Move move:
                _x = move.X;
                _y = move.Y;
                break;
            case CursorEvent.Visible visible:
                _visible = visible.IsVisible;
                break;
            case CursorEvent.Reset:
                _shape = null;
                _visible = true;
                break;
        }
        Refresh();
    }

    /// <summary>Modalità del puntatore e cattura (modalità server).</summary>
    public void SetMode(bool clientMode, bool locked)
    {
        _clientMode = clientMode;
        _locked = locked;
        Refresh();
    }

    /// <summary>Da chiamare quando cambia l'adattamento dello schermo (misura, scala).</summary>
    public void Refresh()
    {
        var fit = _fit();
        if (fit is null)
        {
            _panel.Cursor = null;
            _overlay.Visibility = Visibility.Collapsed;
            return;
        }
        if (_clientMode)
        {
            _overlay.Visibility = Visibility.Collapsed;
            _panel.Cursor = !_visible ? _factory.Hidden : _shape is null ? null : SystemCursor(_shape, fit);
            return;
        }
        _panel.Cursor = _locked ? _factory.Hidden : null;
        if (!_visible || _shape is null)
        {
            _overlay.Visibility = Visibility.Collapsed;
            return;
        }
        if (_bitmap is not { } cached || !ReferenceEquals(cached.Shape, _shape))
        {
            _bitmap = (_shape, Bitmap(_shape.Image));
        }
        _overlay.Source = _bitmap.Value.Bitmap;
        _overlay.Width = _shape.Image.Width * fit.Scale;
        _overlay.Height = _shape.Image.Height * fit.Scale;
        var (left, top) = fit.ToPanel(_x - _shape.HotSpotX, _y - _shape.HotSpotY);
        Canvas.SetLeft(_overlay, left);
        Canvas.SetTop(_overlay, top);
        _overlay.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        _panel.Cursor = null;
        _factory.Dispose();
    }

    private InputCursor? SystemCursor(CursorImage shape, SurfaceFit fit)
    {
        // Pixel fisici per pixel dell'ospite: il cursore ha la stessa scala dello schermo.
        var scale = fit.Scale * (_panel.XamlRoot?.RasterizationScale ?? 1);
        if (_system is { } cached && ReferenceEquals(cached.Shape, shape) && Math.Abs(cached.Scale - scale) < 0.01)
        {
            return cached.Cursor;
        }
        var cursor = _factory.Create(shape, scale);
        _system = (shape, scale, cursor);
        return cursor;
    }

    private static WriteableBitmap Bitmap(PixelBuffer image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            var bytes = new byte[image.Pixels.Length * 4];
            Buffer.BlockCopy(image.Pixels, 0, bytes, 0, bytes.Length);
            stream.Write(bytes);
        }
        bitmap.Invalidate();
        return bitmap;
    }
}

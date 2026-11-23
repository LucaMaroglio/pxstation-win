using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using SpiceKit.Codecs;
using WindowsSpiceClient.Diagnostics;

namespace WindowsSpiceClient.Input;

/// <summary>
/// Cursori di sistema creati dalle forme dell'ospite (<c>HCURSOR</c> → <see cref="InputCursor"/>),
/// così in modalità client il puntatore è quello hardware di Windows, senza ritardi.
/// </summary>
/// <remarks>
/// Windows App SDK permette di avvolgere un <c>HCURSOR</c> con
/// <c>IInputCursorStaticsInterop::CreateFromHCursor</c> (Microsoft.UI.Input.InputCursor.Interop.h).
/// Gli handle restano vivi finché l'<see cref="InputCursor"/> può essere in uso: li libera
/// <see cref="Dispose"/>, e quelli sostituiti vengono liberati al cambio successivo.
/// </remarks>
internal sealed unsafe class CursorFactory : IDisposable
{
    /// <summary>Lato massimo del cursore scalato (limite pratico dei cursori di Windows).</summary>
    private const int MaxSize = 256;

    private static readonly Guid StaticsInteropIid = new("ac6f5065-90c4-46ce-beb7-05e138e54117");
    private readonly List<nint> _handles = [];
    private InputCursor? _hidden;

    /// <summary>Cursore invisibile (ospite che nasconde il puntatore, puntatore catturato).</summary>
    public InputCursor? Hidden => _hidden ??= Create(new CursorImage(new PixelBuffer(1, 1, 0u, hasAlpha: true), 0, 0), 1);

    /// <summary>Cursore di sistema per una forma dell'ospite, ingrandita di <paramref name="scale"/> (pixel fisici per pixel dell'ospite).</summary>
    public InputCursor? Create(CursorImage image, double scale)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            var handle = CreateHandle(image, scale);
            if (handle == 0) return null;
            _handles.Add(handle);
            // Tiene vivi solo gli ultimi handle: il cursore precedente può essere ancora mostrato.
            while (_handles.Count > 4)
            {
                Native.DestroyCursor(_handles[0]);
                _handles.RemoveAt(0);
            }
            return Wrap(handle);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException)
        {
            AppLog.Error("Cursore dell'ospite non creato", error);
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var handle in _handles) Native.DestroyCursor(handle);
        _handles.Clear();
    }

    private static InputCursor Wrap(nint handle)
    {
        var factory = WinRT.ActivationFactory.Get("Microsoft.UI.Input.InputCursor", StaticsInteropIid);
        var self = factory.ThisPtr;
        // Vtable: IUnknown (3) + IInspectable (3), poi CreateFromHCursor.
        var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)self)[6];
        nint result;
        Marshal.ThrowExceptionForHR(create(self, handle, &result));
        try
        {
            return WinRT.MarshalInspectable<InputCursor>.FromAbi(result);
        }
        finally
        {
            Marshal.Release(result);
        }
    }

    /// <summary>
    /// <c>CreateIconIndirect</c> con una bitmap a 32 bit (alfa non premoltiplicato, come le icone)
    /// e una maschera vuota.
    /// </summary>
    private static nint CreateHandle(CursorImage image, double scale)
    {
        var source = image.Image;
        scale = Math.Clamp(scale, 0.25, 8);
        var width = Math.Clamp((int)Math.Round(source.Width * scale), 1, MaxSize);
        var height = Math.Clamp((int)Math.Round(source.Height * scale), 1, MaxSize);
        var header = new Native.BitmapInfoHeader
        {
            Size = (uint)sizeof(Native.BitmapInfoHeader),
            Width = width,
            Height = -height, // dall'alto in basso
            Planes = 1,
            BitCount = 32,
        };
        var color = Native.CreateDIBSection(0, header, 0, out var bits, 0, 0);
        if (color == 0) return 0;
        var mask = Native.CreateBitmap(width, height, 1, 1, 0);
        try
        {
            var target = new Span<uint>(bits, width * height);
            for (var y = 0; y < height; y++)
            {
                var sy = Math.Min(source.Height - 1, y * source.Height / height);
                for (var x = 0; x < width; x++)
                {
                    var sx = Math.Min(source.Width - 1, x * source.Width / width);
                    target[(y * width) + x] = Unpremultiply(source.Pixels[(sy * source.Width) + sx]);
                }
            }
            var info = new Native.IconInfo
            {
                IsIcon = 0,
                HotspotX = (uint)Math.Min(width - 1, (int)Math.Round(image.HotSpotX * scale)),
                HotspotY = (uint)Math.Min(height - 1, (int)Math.Round(image.HotSpotY * scale)),
                Mask = mask,
                Color = color,
            };
            return Native.CreateIconIndirect(info);
        }
        finally
        {
            // CreateIconIndirect copia le bitmap.
            Native.DeleteObject(color);
            if (mask != 0) Native.DeleteObject(mask);
        }
    }

    private static uint Unpremultiply(uint pixel)
    {
        var a = pixel >> 24;
        if (a is 0 or 255) return a == 0 ? 0 : pixel;
        uint r = Math.Min(255, ((pixel >> 16) & 0xFF) * 255 / a);
        uint g = Math.Min(255, ((pixel >> 8) & 0xFF) * 255 / a);
        uint b = Math.Min(255, (pixel & 0xFF) * 255 / a);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }
}

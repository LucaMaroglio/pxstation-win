using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace WindowsSpiceClient.Display;

/// <summary>Adattatore grafico in uso, mostrato nella finestra di diagnostica.</summary>
public sealed record RendererInfo(string AdapterName, string FeatureLevel, bool IsSoftware);

/// <summary>
/// Swap chain Direct3D 11 collegata a uno <see cref="SwapChainPanel"/> (AGENTS.md §7).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Dispositivo hardware; se manca (VM senza GPU) si usa WARP, il rasterizzatore software.</item>
/// <item>Swap chain "for composition" in modello flip, BGRA8, 2 buffer.</item>
/// <item>I buffer hanno la dimensione in pixel fisici (dimensione del pannello × scala di
///   composizione) e una trasformazione inversa li riporta in DIP: nessun ridimensionamento
///   sfocato sugli schermi HiDPI.</item>
/// </list>
/// </remarks>
public sealed class D3D11Presenter : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
    ];

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView? _target;
    private int _width = 1;
    private int _height = 1;
    private float _scaleX = 1;
    private float _scaleY = 1;

    public D3D11Presenter(SwapChainPanel panel)
    {
        var flags = DeviceCreationFlags.BgraSupport;
        var (device, context, level, software) = CreateDevice(flags);
        _device = device;
        _context = context;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var adapterInfo = adapter.Description;
        // Il "Microsoft Basic Render Driver" (VendorId 0x1414, DeviceId 0x8C) è WARP esposto come
        // adattatore hardware: in una VM senza GPU lo si ottiene anche chiedendo DriverType.Hardware.
        software |= adapterInfo.VendorId == 0x1414 && adapterInfo.DeviceId == 0x8C;
        Info = new RendererInfo(adapterInfo.Description.TrimEnd('\0'), FormatLevel(level), software);

        var description = new SwapChainDescription1
        {
            Width = 1,
            Height = 1,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
        };
        _swapChain = factory.CreateSwapChainForComposition(_device, description);
        SwapChainPanelInterop.SetSwapChain(panel, _swapChain.NativePointer);
        CreateTarget();
    }

    public RendererInfo Info { get; }

    /// <summary>Adatta i buffer al pannello (dimensioni in DIP, scala di composizione).</summary>
    public void Resize(double widthDip, double heightDip, float scaleX, float scaleY)
    {
        var width = Math.Max(1, (int)Math.Round(widthDip * scaleX));
        var height = Math.Max(1, (int)Math.Round(heightDip * scaleY));
        if (width == _width && height == _height && scaleX == _scaleX && scaleY == _scaleY)
        {
            return;
        }

        _target?.Dispose();
        _target = null;
        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        _width = width;
        _height = height;
        _scaleX = scaleX;
        _scaleY = scaleY;

        using (var swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>())
        {
            if (swapChain2 is not null)
            {
                swapChain2.MatrixTransform = Matrix3x2.CreateScale(1 / scaleX, 1 / scaleY);
            }
        }

        CreateTarget();
    }

    /// <summary>Riempie lo schermo con un colore e presenta il frame.</summary>
    public void Clear(float red, float green, float blue)
    {
        if (_target is null)
        {
            return;
        }

        _context.OMSetRenderTargets(_target);
        _context.ClearRenderTargetView(_target, new Color4(red, green, blue, 1));
        _swapChain.Present(1, PresentFlags.None);
    }

    /// <summary>
    /// Autoverifica: riempie il buffer con un colore, ne rilegge il pixel centrale da una
    /// texture di staging e poi presenta. Restituisce il pixel come 0xAARRGGBB.
    /// </summary>
    public uint ClearAndProbe(float red, float green, float blue)
    {
        if (_target is null)
        {
            throw new InvalidOperationException("Swap chain senza buffer.");
        }

        _context.OMSetRenderTargets(_target);
        _context.ClearRenderTargetView(_target, new Color4(red, green, blue, 1));
        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        var description = back.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device.CreateTexture2D(description);
        _context.CopyResource(staging, back);
        var mapped = _context.Map(staging, 0, MapMode.Read);
        uint pixel;
        try
        {
            unsafe
            {
                var row = (byte*)mapped.DataPointer + (description.Height / 2 * mapped.RowPitch);
                pixel = ((uint*)row)[description.Width / 2];
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }

        _swapChain.Present(1, PresentFlags.None);
        return pixel;
    }

    public void Dispose()
    {
        _target?.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private void CreateTarget()
    {
        using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _target = _device.CreateRenderTargetView(buffer);
    }

    private static (ID3D11Device Device, ID3D11DeviceContext Context, FeatureLevel Level, bool Software)
        CreateDevice(DeviceCreationFlags flags)
    {
        foreach (var (driver, software) in new[] { (DriverType.Hardware, false), (DriverType.Warp, true) })
        {
            var result = D3D11.D3D11CreateDevice(null, driver, flags, FeatureLevels,
                out var device, out var level, out var context);
            if (result.Success && device is not null && context is not null)
            {
                return (device, context, level, software);
            }
        }

        throw new InvalidOperationException("Impossibile creare un dispositivo Direct3D 11 (né hardware né WARP).");
    }

    private static string FormatLevel(FeatureLevel level) => level switch
    {
        FeatureLevel.Level_11_1 => "Direct3D 11.1",
        FeatureLevel.Level_11_0 => "Direct3D 11.0",
        FeatureLevel.Level_10_1 => "Direct3D 10.1",
        FeatureLevel.Level_10_0 => "Direct3D 10.0",
        _ => level.ToString(),
    };
}

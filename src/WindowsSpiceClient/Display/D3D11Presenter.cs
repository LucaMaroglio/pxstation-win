using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace WindowsSpiceClient.Display;

/// <summary>Adattatore grafico in uso, mostrato nella finestra di diagnostica.</summary>
public sealed record RendererInfo(string AdapterName, string FeatureLevel, bool IsSoftware);

/// <summary>Adattamento dello schermo della VM al pannello: scala (DIP per pixel dell'ospite) e margini in DIP.</summary>
public sealed record SurfaceFit(double Scale, double OffsetX, double OffsetY, int Width, int Height)
{
    /// <summary>Pixel dell'ospite sotto un punto del pannello (in DIP), limitato allo schermo.</summary>
    public (uint X, uint Y) ToSurface(double x, double y) => (
        (uint)Math.Clamp((int)Math.Floor((x - OffsetX) / Scale), 0, Width - 1),
        (uint)Math.Clamp((int)Math.Floor((y - OffsetY) / Scale), 0, Height - 1));

    /// <summary>Punto del pannello (in DIP) di un pixel dell'ospite.</summary>
    public (double X, double Y) ToPanel(double x, double y) => (OffsetX + (x * Scale), OffsetY + (y * Scale));
}

/// <summary>Pixel letti da una texture (BGRA, righe compatte, dall'alto in basso).</summary>
public sealed record SurfaceCapture(int Width, int Height, byte[] Bgra);

/// <summary>
/// Swap chain Direct3D 11 collegata a uno <see cref="SwapChainPanel"/> (AGENTS.md §7).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Dispositivo hardware; se manca (VM senza GPU) si usa WARP, il rasterizzatore software.</item>
/// <item>Swap chain "for composition" in modello flip, BGRA8, 2 buffer.</item>
/// <item>Senza schermo della VM i buffer hanno la misura del pannello in pixel fisici e una
///   trasformazione inversa li riporta in DIP: nessuna sfocatura sugli schermi HiDPI.</item>
/// <item>Con lo schermo della VM la superficie primaria vive in una texture della sua misura,
///   aggiornata solo nei rettangoli cambiati; a ogni fotogramma la texture si copia nel buffer
///   (stessa misura) e la trasformazione della swap chain la adatta al pannello mantenendo le
///   proporzioni (lo scalamento lo fa il compositore: nessuno shader da compilare).</item>
/// </list>
/// Va usato solo dal thread dell'interfaccia.
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
    private ID3D11Texture2D? _surface;
    private int _bufferWidth = 1;
    private int _bufferHeight = 1;
    private double _panelWidth = 1;
    private double _panelHeight = 1;
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

    /// <summary>Misura della superficie della VM, se c'è.</summary>
    public (int Width, int Height)? SurfaceSize { get; private set; }

    /// <summary>Dove si trova lo schermo della VM nel pannello (in DIP), se c'è.</summary>
    public SurfaceFit? Fit { get; private set; }

    /// <summary>Scala di composizione del pannello (pixel fisici per DIP).</summary>
    public float CompositionScale => _scaleX;

    /// <summary>Il pannello è cambiato (misura in DIP, scala di composizione).</summary>
    public void Resize(double widthDip, double heightDip, float scaleX, float scaleY)
    {
        _panelWidth = Math.Max(1, widthDip);
        _panelHeight = Math.Max(1, heightDip);
        _scaleX = scaleX;
        _scaleY = scaleY;
        if (SurfaceSize is null)
        {
            ResizeBuffers(Math.Max(1, (int)Math.Round(widthDip * scaleX)), Math.Max(1, (int)Math.Round(heightDip * scaleY)));
        }
        UpdateTransform();
    }

    /// <summary>(Ri)crea la superficie della VM, nera, e porta i buffer alla sua misura.</summary>
    public void SetSurface(int width, int height)
    {
        _surface?.Dispose();
        _surface = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        _surface.DebugName = "superficie primaria SPICE";
        SurfaceSize = (width, height);
        ResizeBuffers(width, height);
        UpdateTransform();
    }

    /// <summary>La VM non ha più una superficie primaria.</summary>
    public void ClearSurface()
    {
        _surface?.Dispose();
        _surface = null;
        SurfaceSize = null;
        Resize(_panelWidth, _panelHeight, _scaleX, _scaleY);
    }

    /// <summary>Copia nella texture i pixel di un rettangolo (BGRA, <paramref name="width"/> × <paramref name="height"/> compatti).</summary>
    public unsafe void Update(int left, int top, int width, int height, uint[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (_surface is null || SurfaceSize is not var (sw, sh)) return;
        // Il canale display limita già i rettangoli alla superficie: qui solo un controllo di sicurezza.
        if (left < 0 || top < 0 || width <= 0 || height <= 0 || left + width > sw || top + height > sh
            || pixels.Length < width * height)
        {
            return;
        }
        var box = new Box(left, top, 0, left + width, top + height, 1);
        fixed (uint* data = pixels)
        {
            _context.UpdateSubresource(_surface, 0, box, (nint)data, (uint)(width * 4), 0);
        }
    }

    /// <summary>Presenta un fotogramma: lo schermo della VM, oppure un colore di sfondo.</summary>
    public void Present(float red = 0, float green = 0, float blue = 0)
    {
        if (_surface is not null)
        {
            using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _context.CopyResource(back, _surface);
        }
        else if (_target is not null)
        {
            _context.OMSetRenderTargets(_target);
            _context.ClearRenderTargetView(_target, new Color4(red, green, blue, 1));
        }
        // Senza attesa del vsync: la swap chain per composizione non strappa e il thread
        // dell'interfaccia non si blocca.
        _swapChain.Present(0, PresentFlags.None);
    }

    /// <summary>Riempie lo schermo con un colore e presenta il frame (solo senza superficie).</summary>
    public void Clear(float red, float green, float blue)
    {
        if (_surface is null) Present(red, green, blue);
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
        var capture = Read(back);
        _swapChain.Present(0, PresentFlags.None);
        var offset = ((capture.Height / 2 * capture.Width) + (capture.Width / 2)) * 4;
        return BitConverter.ToUInt32(capture.Bgra, offset);
    }

    /// <summary>Autoverifica: i pixel della superficie della VM così come sono sulla GPU.</summary>
    public SurfaceCapture? CaptureSurface() => _surface is null ? null : Read(_surface);

    public void Dispose()
    {
        _surface?.Dispose();
        _target?.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private unsafe SurfaceCapture Read(ID3D11Texture2D texture)
    {
        var description = texture.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device.CreateTexture2D(description);
        _context.CopyResource(staging, texture);
        var mapped = _context.Map(staging, 0, MapMode.Read);
        try
        {
            int width = (int)description.Width, height = (int)description.Height;
            var bytes = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>((byte*)mapped.DataPointer + (y * mapped.RowPitch), width * 4)
                    .CopyTo(bytes.AsSpan(y * width * 4));
            }
            return new SurfaceCapture(width, height, bytes);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private void ResizeBuffers(int width, int height)
    {
        if (width == _bufferWidth && height == _bufferHeight) return;
        _target?.Dispose();
        _target = null;
        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        _bufferWidth = width;
        _bufferHeight = height;
        CreateTarget();
    }

    /// <summary>
    /// Trasformazione da pixel del buffer a DIP del pannello: inversa della scala di composizione
    /// senza superficie; con la superficie, adattamento proporzionale e centratura.
    /// </summary>
    private void UpdateTransform()
    {
        using var swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (swapChain2 is null) return;
        if (SurfaceSize is not var (width, height))
        {
            Fit = null;
            swapChain2.MatrixTransform = Matrix3x2.CreateScale(1 / _scaleX, 1 / _scaleY);
            return;
        }
        var fit = (float)Math.Min(_panelWidth / width, _panelHeight / height);
        var offsetX = (float)((_panelWidth - (width * fit)) / 2);
        var offsetY = (float)((_panelHeight - (height * fit)) / 2);
        Fit = new SurfaceFit(fit, offsetX, offsetY, width, height);
        swapChain2.MatrixTransform = Matrix3x2.CreateScale(fit) * Matrix3x2.CreateTranslation(offsetX, offsetY);
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

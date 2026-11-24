using System.Runtime.InteropServices;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Shell;

/// <summary>
/// Icona nell'area di notifica (M9) con <c>Shell_NotifyIcon</c> e un menu Win32 nativo, come le
/// altre icone di sistema: tema, DPI, tastiera (Invio, Maiusc+F10) e assistive technology sono
/// quelli di Windows.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Il menu si costruisce a ogni apertura (<see cref="MenuEntry"/>), quindi è sempre aggiornato.</item>
/// <item>Clic sinistro: <c>onSelect</c> (la finestra più recente); clic destro o tasto menu: il menu.</item>
/// <item>Se Explorer si riavvia (messaggio <c>TaskbarCreated</c>) l'icona viene aggiunta di nuovo.</item>
/// <item>Una finestra nascosta di primo livello riceve i messaggi sul thread dell'interfaccia; ce n'è
///   una sola per processo.</item>
/// </list>
/// </remarks>
internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const string ClassName = "WindowsSpiceClient.TrayIcon";
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint IconId = 1;

    private static TrayIcon? s_current;
    private static uint s_taskbarCreated;
    private static bool s_registered;

    private readonly nint _window;
    private readonly nint _icon;
    private readonly Func<IReadOnlyList<MenuEntry>> _entries;
    private readonly Action _onSelect;
    private string _tooltip = AppPaths.AppName;
    private bool _menuOpen;

    /// <summary>L'icona è stata accettata dalla shell (per l'autoverifica).</summary>
    public bool IsShown { get; private set; }

    public TrayIcon(Func<IReadOnlyList<MenuEntry>> entries, Action onSelect)
    {
        if (s_current is not null) throw new InvalidOperationException("Una sola icona nell'area di notifica per processo.");
        _entries = entries;
        _onSelect = onSelect;
        var instance = GetModuleHandleW(null);
        if (!s_registered)
        {
            s_taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            fixed (char* name = ClassName)
            {
                var windowClass = new WindowClass
                {
                    Size = (uint)sizeof(WindowClass),
                    WindowProcedure = &WindowProcedure,
                    Instance = instance,
                    ClassName = name,
                };
                if (RegisterClassExW(&windowClass) == 0) throw new InvalidOperationException($"RegisterClassEx: {Marshal.GetLastPInvokeError()}");
            }
            s_registered = true;
        }
        // Finestra di primo livello mai mostrata (le finestre solo-messaggi non ricevono TaskbarCreated).
        _window = CreateWindowExW(0, ClassName, AppPaths.AppName, 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_window == 0) throw new InvalidOperationException($"CreateWindowEx: {Marshal.GetLastPInvokeError()}");
        var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem());
        _icon = LoadImageW(0, AppPaths.IconPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        s_current = this;
        Add();
    }

    /// <summary>Testo mostrato al passaggio del mouse (massimo 127 caratteri).</summary>
    public string Tooltip
    {
        get => _tooltip;
        set
        {
            if (value == _tooltip) return;
            _tooltip = value;
            if (IsShown) Notify(1 /* NIM_MODIFY */, includeIcon: false);
        }
    }

    private void Add()
    {
        IsShown = Notify(0 /* NIM_ADD */, includeIcon: true);
        if (!IsShown)
        {
            AppLog.Info("Area di notifica non disponibile: icona non aggiunta");
            return;
        }
        // Versione 4: eventi NIN_SELECT/WM_CONTEXTMENU con la posizione, anche da tastiera.
        var data = Data(0);
        data.Version = 4;
        _ = ShellNotifyIconW(4 /* NIM_SETVERSION */, &data);
    }

    private bool Notify(uint message, bool includeIcon)
    {
        var data = Data(0x1 | 0x4 | 0x80 | (includeIcon ? 0x2u : 0)); // NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP | NIF_ICON
        return ShellNotifyIconW(message, &data);
    }

    private NotifyIconData Data(uint flags)
    {
        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _window,
            Id = IconId,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
        };
        var tip = _tooltip.Length > 127 ? _tooltip[..126] + "…" : _tooltip;
        tip.AsSpan().CopyTo(new Span<char>(data.Tip, 128));
        return data;
    }

    public void Dispose()
    {
        if (s_current != this) return;
        if (IsShown)
        {
            var data = Data(0);
            _ = ShellNotifyIconW(2 /* NIM_DELETE */, &data);
            IsShown = false;
        }
        _ = DestroyWindow(_window);
        if (_icon != 0) _ = DestroyIcon(_icon);
        s_current = null;
    }

    // MARK: messaggi

    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (s_current is { } tray && tray._window == window)
            {
                if (message == CallbackMessage)
                {
                    tray.OnCallback((uint)(lParam & 0xFFFF), (short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    return 0;
                }
                if (message == s_taskbarCreated)
                {
                    tray.Add(); // Explorer è ripartito
                    return 0;
                }
            }
        }
        catch (Exception error)
        {
            // Un'eccezione non deve attraversare il confine nativo.
            AppLog.Error("Icona nell'area di notifica", error);
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void OnCallback(uint notification, int x, int y)
    {
        switch (notification)
        {
            case 0x400: // NIN_SELECT (clic sinistro)
            case 0x401: // NIN_KEYSELECT (Invio o spazio)
                _onSelect();
                break;
            case 0x7B: // WM_CONTEXTMENU (clic destro, Maiusc+F10, tasto menu)
                ShowMenu(x, y);
                break;
        }
    }

    private void ShowMenu(int x, int y)
    {
        if (_menuOpen) return;
        _menuOpen = true;
        var commands = new List<Action>();
        var menu = CreatePopupMenu();
        try
        {
            Build(menu, _entries(), commands);
            // Senza primo piano il menu non si chiude cliccando altrove (KB135788).
            _ = SetForegroundWindow(_window);
            var flags = 0x100u | 0x2u | 0x80u; // TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY
            if (GetSystemMetrics(40 /* SM_MENUDROPALIGNMENT */) != 0) flags |= 0x8; // TPM_RIGHTALIGN
            var chosen = TrackPopupMenuEx(menu, flags, x, y, _window, 0);
            _ = PostMessageW(_window, 0 /* WM_NULL */, 0, 0);
            if (chosen > 0 && chosen <= commands.Count)
            {
                // Fuori dal ciclo del menu: il comando può aprire finestre e dialoghi.
                var command = commands[chosen - 1];
                _ = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() => command());
            }
        }
        finally
        {
            _ = DestroyMenu(menu); // distrugge anche i sottomenu
            _menuOpen = false;
        }
    }

    private static void Build(nint menu, IReadOnlyList<MenuEntry> entries, List<Action> commands)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                _ = AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, 0, null);
                continue;
            }
            // «&» introduce la lettera di accesso: i nomi delle VM lo mostrano così com'è.
            var text = entry.Text.Replace("&", "&&", StringComparison.Ordinal);
            var disabled = entry.Enabled && !entry.IsInfo ? 0u : 0x1u; // MF_GRAYED
            if (entry.Children is { } children)
            {
                var submenu = CreatePopupMenu();
                Build(submenu, children, commands);
                _ = AppendMenuW(menu, 0x10 /* MF_POPUP */ | disabled, (nuint)submenu, text);
            }
            else if (entry.Invoke is { } invoke)
            {
                commands.Add(invoke);
                _ = AppendMenuW(menu, disabled, (nuint)commands.Count, text);
            }
            else
            {
                _ = AppendMenuW(menu, disabled, 0, text);
            }
        }
    }

    // MARK: Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial ushort RegisterClassExW(WindowClass* windowClass);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetDpiForSystem();

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIconW(uint message, NotifyIconData* data);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}

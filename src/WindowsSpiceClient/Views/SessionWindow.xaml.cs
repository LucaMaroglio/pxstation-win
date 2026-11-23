using System.Globalization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpiceKit.Channels;
using SpiceKit.Input;
using SpiceKit.Proxmox;
using SpiceKit.Session;
using SpiceKit.VVFile;
using SpiceKit.Wire;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Display;
using WindowsSpiceClient.Input;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>
/// Finestra di una sessione: collega la sessione SPICE appena aperta (i ticket Proxmox scadono in
/// circa 30 secondi), ne mostra lo stato, disegna lo schermo della VM con Direct3D 11 e le passa
/// tastiera e mouse.
/// </summary>
/// <remarks>
/// Tastiera: con la finestra in primo piano e lo schermo visibile, l'hook a basso livello manda
/// all'ospite tutti i tasti (anche Alt+Tab e il tasto Windows), tranne le scorciatoie della sessione.
/// «Rilascia mouse e tastiera» li restituisce a Windows fino al clic successivo sullo schermo.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "Una Window non è IDisposable: presenter, cursore e ticket vengono rilasciati nell'evento Closed.")]
public sealed partial class SessionWindow : Window
{
    private readonly D3D11Presenter? _presenter;
    private readonly SpiceSession? _session;
    private readonly ConnectionProfile? _profile;
    private readonly KeyboardState _keyboard;
    private readonly PointerInput _pointer;
    private readonly GuestCursor _cursor;
    private readonly nint _hwnd;
    private readonly string _summary;
    private string? _guestName;
    private bool _agent;
    private bool _screenVisible;
    private bool _active;
    private bool _released;

    internal SessionWindow(ConnectionProfile? profile, ProxmoxVMRef? reference, string? vmName, bool fullScreen = false)
    {
        InitializeComponent();
        _profile = profile;
        _hwnd = WindowChrome.Handle(this);
        AppTitleBar.Title = vmName ?? profile?.DisplayTitle ?? AppPaths.AppName;
        WindowChrome.Apply(this, Root, AppTitleBar, 1024, 720, minWidth: 480, minHeight: 360);
        _summary = profile is null ? "" : Summary(profile, reference);
        _keyboard = new KeyboardState(profile, vk => Native.MapVirtualKey(vk, Native.MapvkVkToVscEx));
        _cursor = new GuestCursor(DisplayPanel, CursorOverlay, () => _presenter?.Fit);
        _pointer = new PointerInput(DisplayPanel, () => _presenter?.Fit, () => _session?.MouseMode ?? MouseModes.Server,
            () => _hwnd, Send);
        _pointer.Engaged += () => _released = false;
        _pointer.LockChanged += UpdatePointerMode;
        SecureAttentionItem.KeyboardAcceleratorTextOverride = KeyboardState.HotkeyOf(profile, HotkeyAction.SecureAttention).ToString();
        FullscreenItem.KeyboardAcceleratorTextOverride = KeyboardState.HotkeyOf(profile, HotkeyAction.ToggleFullscreen).ToString();
        ReleaseCursorItem.KeyboardAcceleratorTextOverride = KeyboardState.HotkeyOf(profile, HotkeyAction.ReleaseCursor).ToString();
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            KeyboardHook.Unregister(_hwnd);
            _pointer.Release();
            _cursor.Dispose();
        };
        if (fullScreen) SetFullScreen(true);

        if (profile is not null)
        {
            _session = new SpiceSession(profile);
            Closed += (_, _) => _ = _session.DisposeAsync().AsTask();
            KeyboardHook.Register(_hwnd, OnKey);
            ShowState(SessionState.Connecting);
            _ = RunSessionAsync(_session);
            _ = RunDisplayAsync(_session);
            _ = RunCursorAsync(_session);
        }

        // Diagnostica: WSC_RENDERER=none esclude Direct3D (per distinguere problemi del renderer da quelli di WinUI).
        if (string.Equals(Environment.GetEnvironmentVariable("WSC_RENDERER"), "none", StringComparison.OrdinalIgnoreCase))
        {
            RendererInfo.Message = "Renderer disattivato (WSC_RENDERER=none)";
            return;
        }

        try
        {
            _presenter = new D3D11Presenter(DisplayPanel);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException
                                          or SharpGen.Runtime.SharpGenException)
        {
            AppLog.Error("Creazione del renderer Direct3D non riuscita", error);
            RendererInfo.Severity = InfoBarSeverity.Error;
            RendererInfo.Message = error.Message;
            return;
        }

        DisplayPanel.SizeChanged += (_, _) => Render();
        DisplayPanel.CompositionScaleChanged += (_, _) => Render();
        Closed += (_, _) => _presenter.Dispose();

        var info = _presenter.Info;
        AppLog.Info($"Renderer: {info.AdapterName}, {info.FeatureLevel}, software={info.IsSoftware}");
        RendererInfo.Message = string.Create(CultureInfo.InvariantCulture,
            $"{info.AdapterName} · {info.FeatureLevel}{(info.IsSoftware ? " · software (WARP)" : string.Empty)}");
        RendererInfo.Severity = info.IsSoftware ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
    }

    internal Grid RootElement => Root;

    /// <summary>Stato attuale della sessione (per l'autoverifica).</summary>
    internal SessionState? SessionState => _session?.State;

    /// <summary>Modalità del puntatore in uso (per l'autoverifica).</summary>
    internal MouseModes? MouseMode => _session?.MouseMode;

    /// <summary>Input inviati all'ospite (per l'autoverifica).</summary>
    internal int InputsSent => _session?.InputsSent ?? 0;

    /// <summary>Testo mostrato nella barra di stato (per l'autoverifica).</summary>
    internal string StatusText => $"{ProfileInfo.Title} · {ProfileInfo.Message}";

    /// <summary>Aggiornamenti dello schermo applicati finora (per l'autoverifica).</summary>
    internal int ScreenUpdates { get; private set; }

    /// <summary>Lo schermo della VM come si trova nella texture Direct3D (per l'autoverifica).</summary>
    internal SurfaceCapture? CaptureScreen() => _presenter?.CaptureSurface();

    /// <summary>Esegue un comando della sessione come la sua scorciatoia (menu e autoverifica).</summary>
    internal void Perform(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.SecureAttention:
                // Ctrl+Alt+Canc: Windows non lo lascia intercettare, si invia la sequenza completa.
                foreach (var code in KeyboardState.SecureAttentionSequence) Send(new InputEvent.KeyDown(code));
                foreach (var code in KeyboardState.SecureAttentionSequence.Reverse()) Send(new InputEvent.KeyUp(code));
                break;
            case HotkeyAction.ToggleFullscreen:
                SetFullScreen(AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);
                break;
            case HotkeyAction.ReleaseCursor:
                _released = true;
                ReleaseGuestInput();
                break;
        }
    }

    /// <summary>Inoltra un input all'ospite (anche per l'autoverifica).</summary>
    internal void SendInput(InputEvent input) => _session?.SendInput(input);

    private void Send(InputEvent input) => SendInput(input);

    // MARK: tastiera

    /// <summary>La tastiera va all'ospite: schermo visibile, finestra attiva, non rilasciata.</summary>
    private bool Captures => _screenVisible && _active && !_released && _session?.HasInputs == true;

    /// <summary>Chiamato dall'hook per ogni tasto con la finestra in primo piano; <see langword="true"/> lo trattiene.</summary>
    private bool OnKey(KeyStroke key)
    {
        if (!Captures) return false;
        switch (_keyboard.Process(key.VirtualKey, key.ScanCode, key.Extended, key.Down))
        {
            case KeyOutcome.Send send:
                Send(send.Down ? new InputEvent.KeyDown(send.Scancode) : new InputEvent.KeyUp(send.Scancode));
                return true;
            case KeyOutcome.Hotkey hotkey:
                // Fuori dall'hook: deve tornare subito a Windows.
                DispatcherQueue.TryEnqueue(() => Perform(hotkey.Action));
                return true;
            default:
                // Rilasci di tasti premuti prima della cattura: a Windows, che ne ha visto la pressione.
                return key.Down;
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!_active)
        {
            ReleaseGuestInput();
            return;
        }
        // Al ritorno nella finestra l'ospite prende lo stato dei blocchi dell'host.
        var locks = KeyboardLocks.None;
        if ((Native.GetKeyState(0x14) & 1) != 0) locks |= KeyboardLocks.CapsLock;
        if ((Native.GetKeyState(0x90) & 1) != 0) locks |= KeyboardLocks.NumLock;
        if ((Native.GetKeyState(0x91) & 1) != 0) locks |= KeyboardLocks.ScrollLock;
        Send(new InputEvent.Locks(locks));
    }

    /// <summary>Solleva nell'ospite tasti e pulsanti premuti e libera il puntatore (niente tasti bloccati).</summary>
    private void ReleaseGuestInput()
    {
        foreach (var code in _keyboard.ReleaseAll()) Send(new InputEvent.KeyUp(code));
        _pointer.Release();
    }

    // MARK: comandi

    private void OnSecureAttention(object sender, RoutedEventArgs e) => Perform(HotkeyAction.SecureAttention);

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => Perform(HotkeyAction.ToggleFullscreen);

    private void OnReleaseCursor(object sender, RoutedEventArgs e) => Perform(HotkeyAction.ReleaseCursor);

    private void SetFullScreen(bool on)
    {
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        // A schermo intero solo la VM: il menu resta raggiungibile con le scorciatoie.
        AppTitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdatePointerMode()
    {
        var client = _session?.MouseMode == MouseModes.Client;
        _cursor.SetMode(client, _pointer.IsLocked);
        var hotkey = KeyboardState.HotkeyOf(_profile, HotkeyAction.ReleaseCursor).ToString();
        AppTitleBar.Subtitle = !_screenVisible ? "" :
            _released ? Strings.Format("SessionReleasedHint", hotkey) :
            !client && !_pointer.IsLocked ? Strings.Format("SessionCaptureHint", hotkey) : "";
    }

    // MARK: display e cursore

    /// <summary>
    /// Applica gli eventi del canale display sul thread dell'interfaccia: tutti quelli in coda,
    /// poi un solo fotogramma. Ogni aggiornamento restituisce il credito al canale.
    /// </summary>
    private async Task RunDisplayAsync(SpiceSession session)
    {
        var reader = session.DisplayEvents;
        while (await reader.WaitToReadAsync())
        {
            var present = false;
            while (reader.TryRead(out var value))
            {
                switch (value)
                {
                    case DisplayEvent.PrimarySurfaceCreated created:
                        AppLog.Info($"Schermo della VM: {created.Width}x{created.Height}");
                        _presenter?.SetSurface(created.Width, created.Height);
                        ShowScreen(true);
                        present = true;
                        break;
                    case DisplayEvent.PrimarySurfaceDestroyed:
                        _presenter?.ClearSurface();
                        _cursor.Refresh();
                        present = true;
                        break;
                    case DisplayEvent.Update update:
                        var rect = update.Surface.Rect;
                        _presenter?.Update(rect.Left, rect.Top, rect.Width, rect.Height, update.Surface.Pixels);
                        session.DisplayCredit.Consumed();
                        ScreenUpdates++;
                        present = true;
                        break;
                    case DisplayEvent.MonitorsConfig monitors:
                        AppLog.Info($"Configurazione monitor dal server: {monitors.Heads.Count} teste");
                        break;
                }
            }
            if (present) _presenter?.Present();
        }
    }

    private async Task RunCursorAsync(SpiceSession session)
    {
        await foreach (var value in session.CursorEvents.ReadAllAsync())
        {
            _cursor.Apply(value);
        }
    }

    /// <summary>Con lo schermo della VM visibile le barre informative lasciano tutto lo spazio al display.</summary>
    private void ShowScreen(bool visible)
    {
        _screenVisible = visible;
        RendererInfo.Visibility = visible ? Visibility.Collapsed : Visibility.Visible; // anche il suo margine
        if (_session?.State.Kind == SessionStateKind.Connected) ProfileInfo.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        if (visible) DisplayPanel.Focus(FocusState.Programmatic);
        UpdatePointerMode();
    }

    private async Task RunSessionAsync(SpiceSession session)
    {
        await session.ConnectAsync();
        // Gli eventi arrivano da altri thread; l'attesa riprende sul thread dell'interfaccia.
        await foreach (var value in session.Events.ReadAllAsync())
        {
            switch (value)
            {
                case SessionEvent.StateChanged changed:
                    ShowState(changed.State);
                    break;
                case SessionEvent.ChannelsLinked linked:
                    AppLog.Info($"Sessione connessa: offerti {string.Join(", ", linked.Offered)}; collegati {string.Join(", ", linked.Linked)}");
                    ProfileInfo.Message = Strings.Format("SessionConnectedDetail", string.Join(", ", linked.Linked.Prepend(new ChannelEntry(1, 0))))
                                          + Details();
                    SessionMenuButton.IsEnabled = session.HasInputs;
                    break;
                case SessionEvent.MouseModeChanged mode:
                    AppLog.Info($"Modalità del puntatore: {mode.Mode}");
                    if (mode.Mode == MouseModes.Client) _pointer.Release();
                    UpdatePointerMode();
                    break;
                case SessionEvent.GuestNameChanged name:
                    _guestName = name.Name;
                    break;
                case SessionEvent.AgentChanged agent:
                    _agent = agent.Connected;
                    break;
            }
        }
    }

    private string Details()
    {
        var parts = new List<string> { _summary };
        if (!string.IsNullOrWhiteSpace(_guestName)) parts.Add(Strings.Format("SessionGuestName", _guestName));
        if (_agent) parts.Add(Strings.Get("SessionAgent"));
        return "\n" + string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    private void ShowState(SessionState state)
    {
        ConnectingProgress.Visibility = state.Kind == SessionStateKind.Connecting ? Visibility.Visible : Visibility.Collapsed;
        // Lo stato resta visibile finché non c'è lo schermo della VM, e torna se la sessione finisce.
        ProfileInfo.Visibility = state.Kind == SessionStateKind.Connected && _screenVisible ? Visibility.Collapsed : Visibility.Visible;
        switch (state.Kind)
        {
            case SessionStateKind.Connecting:
                ProfileInfo.Severity = InfoBarSeverity.Informational;
                ProfileInfo.Title = Strings.Get("SessionConnecting");
                ProfileInfo.Message = _summary;
                break;
            case SessionStateKind.Connected:
                ProfileInfo.Severity = InfoBarSeverity.Success;
                ProfileInfo.Title = Strings.Get("SessionConnected");
                break;
            case SessionStateKind.Disconnected when state.Failure is null:
                ProfileInfo.Severity = InfoBarSeverity.Informational;
                ProfileInfo.Title = Strings.Get("SessionClosed");
                break;
            case SessionStateKind.Disconnected when state.Failure is { } failure:
                var wasConnected = ProfileInfo.Severity == InfoBarSeverity.Success;
                AppLog.Info($"Sessione terminata: {failure.Message} (ripristino: {failure.Recovery})");
                ProfileInfo.Severity = InfoBarSeverity.Error;
                ProfileInfo.Title = Strings.Get(wasConnected ? "SessionEnded" : "SessionFailed");
                ProfileInfo.Message = failure.IsAuthenticationFailure
                    ? $"{failure.Message}\n{Strings.Get("SessionNoTicketHint")}"
                    : failure.Message;
                break;
        }
        if (state.Kind == SessionStateKind.Disconnected)
        {
            // Sessione finita: mouse e tastiera tornano a Windows, anche a schermo intero.
            SessionMenuButton.IsEnabled = false;
            _pointer.Release();
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) SetFullScreen(false);
        }
    }

    /// <summary>Riepilogo senza ticket né CA; l'host Proxmox (che contiene un ticket del proxy) non viene mostrato.</summary>
    private static string Summary(ConnectionProfile profile, ProxmoxVMRef? reference)
    {
        var parts = new List<string>();
        if (reference is not null)
        {
            parts.Add(Strings.Format("SummaryProxmox", reference.VmId, reference.Node, reference.ApiHost));
        }
        else if (!profile.Host.StartsWith("pvespiceproxy:", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(Strings.Format("SummaryHost", profile.Host));
        }
        if (profile.TlsPort is { } tls) parts.Add(Strings.Format("SummaryTls", tls));
        if (profile.Port is { } plain) parts.Add(Strings.Format("SummaryPlain", plain));
        if (profile.Proxy is { } proxy) parts.Add(Strings.Format("SummaryProxy", proxy.Host, proxy.Port));
        if (profile.HostSubject is not null || profile.CaPem is not null) parts.Add(Strings.Get("SummaryVerified"));
        return string.Join(" · ", parts);
    }

    /// <summary>Verifica del renderer per l'autoverifica: clear di colore noto e rilettura del pixel centrale.</summary>
    internal string ProbeRenderer()
    {
        if (_presenter is null) return $"direct3d: non disponibile ({RendererInfo.Message})";
        // Colore saturo e riconoscibile: (R 1.0, G 0.5, B 0.0) → 0xFFFF8000 (G 127 o 128).
        var pixel = _presenter.ClearAndProbe(1f, 0.5f, 0f);
        var ok = (pixel & 0xFFFF0000) == 0xFFFF0000 && ((pixel >> 8) & 0xFF) is 127 or 128 && (pixel & 0xFF) == 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"direct3d: {(ok ? "OK" : "ERRORE")} pixel centrale 0x{pixel:X8} (atteso 0xFFFF7F00/0xFFFF8000) · {RendererInfo.Message}");
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e) => Render();

    private void Render()
    {
        if (_presenter is null) return;
        var width = DisplayPanel.ActualWidth;
        var height = DisplayPanel.ActualHeight;
        if (width < 1 || height < 1) return;
        _presenter.Resize(width, height, DisplayPanel.CompositionScaleX, DisplayPanel.CompositionScaleY);
        _cursor.Refresh();
        // Lo schermo della VM, oppure (prima che arrivi) il nero dello sfondo.
        _presenter.Present();
    }
}

using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using SpiceKit.Channels;
using SpiceKit.Wire;
using WindowsSpiceClient.Display;
using Windows.Foundation;

namespace WindowsSpiceClient.Input;

/// <summary>
/// Mouse sopra lo schermo della VM (porting della parte mouse di <c>RemoteDisplayView.swift</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Modalità client: posizioni assolute in pixel dell'ospite, attraverso l'adattamento del presenter.</item>
/// <item>Modalità server: il primo clic cattura il puntatore (nascosto, confinato al pannello e
///   riportato al centro dopo ogni movimento); si inviano i movimenti relativi in pixel fisici. Si
///   rilascia con la scorciatoia «rilascia cursore» o quando la finestra perde il fuoco.</item>
/// <item>Pulsanti: lo stato si confronta a ogni evento, perché WinUI segnala la pressione di un
///   secondo pulsante come movimento.</item>
/// <item>Rotellina: 120 unità per scatto (anche i touchpad, sommando); quella orizzontale non si
///   inoltra (i pulsanti 6/7 sono Indietro/Avanti in QEMU).</item>
/// </list>
/// </remarks>
internal sealed class PointerInput
{
    private const int WheelStep = 120;

    private readonly DisplaySurface _panel;
    private readonly Func<SurfaceFit?> _fit;
    private readonly Func<MouseModes> _mode;
    private readonly Func<nint> _window;
    private readonly Action<InputEvent> _send;
    private MouseButtons _buttons;
    private int _wheel;

    public PointerInput(DisplaySurface panel, Func<SurfaceFit?> fit, Func<MouseModes> mode, Func<nint> window, Action<InputEvent> send)
    {
        _panel = panel;
        _fit = fit;
        _mode = mode;
        _window = window;
        _send = send;
        panel.PointerMoved += OnPointer;
        panel.PointerPressed += OnPressed;
        panel.PointerReleased += OnPointer;
        panel.PointerWheelChanged += OnWheel;
    }

    /// <summary>Il puntatore è catturato (modalità server).</summary>
    public bool IsLocked { get; private set; }

    /// <summary>L'utente ha cliccato sullo schermo (per riprendere la tastiera dopo un rilascio).</summary>
    public event Action? Engaged;

    /// <summary>Cattura o rilascio del puntatore (il cursore va aggiornato).</summary>
    public event Action? LockChanged;

    /// <summary>Rilascia la cattura del puntatore e i pulsanti premuti nell'ospite.</summary>
    public void Release()
    {
        ReleaseButtons();
        Unlock();
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        Engaged?.Invoke();
        if (_fit() is null) return;
        _panel.Focus(Microsoft.UI.Xaml.FocusState.Pointer);
        if (_mode() != MouseModes.Client && !IsLocked)
        {
            Lock();
            e.Handled = true;
            return; // il clic serve solo a catturare il puntatore
        }
        _panel.CapturePointer(e.Pointer);
        OnPointer(sender, e);
    }

    private void OnPointer(object sender, PointerRoutedEventArgs e)
    {
        if (_fit() is not { } fit) return;
        var point = e.GetCurrentPoint(_panel);
        e.Handled = true;
        if (_mode() == MouseModes.Client)
        {
            var (x, y) = fit.ToSurface(point.Position.X, point.Position.Y);
            _send(new InputEvent.Position(x, y, _buttons, 0));
        }
        else if (IsLocked)
        {
            Recenter(point.Position);
        }
        else
        {
            return; // modalità server senza cattura: il mouse resta di Windows
        }
        UpdateButtons(point.Properties);
        if (_buttons == MouseButtons.None) _panel.ReleasePointerCaptures();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_fit() is null || (_mode() != MouseModes.Client && !IsLocked)) return;
        var properties = e.GetCurrentPoint(_panel).Properties;
        e.Handled = true;
        if (properties.IsHorizontalMouseWheel) return;
        _wheel += properties.MouseWheelDelta;
        while (Math.Abs(_wheel) >= WheelStep)
        {
            var button = _wheel > 0 ? MouseButton.Up : MouseButton.Down;
            _send(new InputEvent.Press(button, _buttons | button.Mask()));
            _send(new InputEvent.Release(button, _buttons));
            _wheel -= Math.Sign(_wheel) * WheelStep;
        }
    }

    private void UpdateButtons(PointerPointProperties properties)
    {
        Change(MouseButton.Left, properties.IsLeftButtonPressed);
        Change(MouseButton.Middle, properties.IsMiddleButtonPressed);
        Change(MouseButton.Right, properties.IsRightButtonPressed);
        Change(MouseButton.Side, properties.IsXButton1Pressed);
        Change(MouseButton.Extra, properties.IsXButton2Pressed);
    }

    private void Change(MouseButton button, bool pressed)
    {
        var mask = button.Mask();
        if (pressed == _buttons.HasFlag(mask)) return;
        if (pressed)
        {
            _buttons |= mask;
            _send(new InputEvent.Press(button, _buttons));
        }
        else
        {
            _buttons &= ~mask;
            _send(new InputEvent.Release(button, _buttons));
        }
    }

    private void ReleaseButtons()
    {
        foreach (var button in new[] { MouseButton.Left, MouseButton.Middle, MouseButton.Right, MouseButton.Side, MouseButton.Extra })
        {
            Change(button, pressed: false);
        }
        _panel.ReleasePointerCaptures();
    }

    // MARK: cattura del puntatore (modalità server)

    private double Scale => _panel.XamlRoot?.RasterizationScale ?? 1;

    private Point Center => new(_panel.ActualWidth / 2, _panel.ActualHeight / 2);

    private void Lock()
    {
        IsLocked = true;
        Native.ClipCursor(ScreenRect());
        MoveTo(Center);
        LockChanged?.Invoke();
    }

    private void Unlock()
    {
        if (!IsLocked) return;
        IsLocked = false;
        Native.UnclipCursor(0);
        LockChanged?.Invoke();
    }

    /// <summary>Movimento dal centro in pixel fisici, poi il puntatore torna al centro (quello sintetico vale zero e si ignora).</summary>
    private void Recenter(Point position)
    {
        var center = Center;
        var dx = (int)Math.Round((position.X - center.X) * Scale);
        var dy = (int)Math.Round((position.Y - center.Y) * Scale);
        if (dx == 0 && dy == 0) return;
        _send(new InputEvent.Motion(dx, dy, _buttons));
        MoveTo(center);
    }

    /// <summary>Posizione in pixel dello schermo di un punto del pannello.</summary>
    private Native.Point ToScreen(Point panelPoint)
    {
        var origin = _panel.TransformToVisual(null).TransformPoint(panelPoint);
        var point = new Native.Point { X = (int)Math.Round(origin.X * Scale), Y = (int)Math.Round(origin.Y * Scale) };
        Native.ClientToScreen(_window(), ref point);
        return point;
    }

    private void MoveTo(Point panelPoint)
    {
        var screen = ToScreen(panelPoint);
        Native.SetCursorPos(screen.X, screen.Y);
    }

    private Native.Rect ScreenRect()
    {
        var topLeft = ToScreen(new Point(0, 0));
        var bottomRight = ToScreen(new Point(_panel.ActualWidth, _panel.ActualHeight));
        return new Native.Rect { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y };
    }
}

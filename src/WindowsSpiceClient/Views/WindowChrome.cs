using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>
/// Aspetto comune delle finestre: barra del titolo integrata, icona, Mica (Windows 11) o
/// sfondo del tema (Windows 10), dimensioni in DIP.
/// </summary>
internal static partial class WindowChrome
{
    public static void Apply(Window window, Panel root, TitleBar titleBar, double width, double height,
        bool resizable = true, double minWidth = 0, double minHeight = 0)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);
        window.AppWindow.SetIcon(AppPaths.IconPath);
        window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        window.Title = titleBar.Title;

        var scale = Scale(window);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = resizable;
            if (minWidth > 0) presenter.PreferredMinimumWidth = (int)(minWidth * scale);
            if (minHeight > 0) presenter.PreferredMinimumHeight = (int)(minHeight * scale);
        }
        CenterOnScreen(window);

        if (MicaController.IsSupported() && !SelfTest.IsEnabled)
        {
            window.SystemBackdrop = new MicaBackdrop();
            root.Background = null; // lascia trasparire Mica
        }
        else
        {
            // Windows 10 o autoverifica (RenderTargetBitmap non vede Mica): sfondo pieno del tema.
            root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        }
    }

    public static void BringToFront(Window window)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        window.Activate();
    }

    public static IntPtr Handle(Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    private static double Scale(Window window) => GetDpiForWindow(Handle(window)) / 96.0;

    private static void CenterOnScreen(Window window)
    {
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = window.AppWindow.Size;
        window.AppWindow.Move(new Windows.Graphics.PointInt32(
            area.X + Math.Max(0, (area.Width - size.Width) / 2), area.Y + Math.Max(0, (area.Height - size.Height) / 2)));
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetDpiForWindow(IntPtr hwnd);
}

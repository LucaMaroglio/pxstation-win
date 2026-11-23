using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace WindowsSpiceClient.Display;

/// <summary>
/// Il pannello dello schermo della VM: uno <see cref="SwapChainPanel"/> che può cambiare il
/// cursore mostrato sopra di sé (<c>ProtectedCursor</c> è accessibile solo alle sottoclassi).
/// </summary>
public sealed partial class DisplaySurface : SwapChainPanel
{
    /// <summary>Cursore sopra lo schermo; <see langword="null"/> = freccia di sistema.</summary>
    public InputCursor? Cursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}

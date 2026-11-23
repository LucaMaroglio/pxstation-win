using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace WindowsSpiceClient.Display;

/// <summary>
/// Collega una swap chain DXGI a uno <see cref="SwapChainPanel"/> tramite l'interfaccia COM
/// <c>ISwapChainPanelNative</c> (microsoft.ui.xaml.media.dxinterop.h).
/// </summary>
/// <remarks>
/// La chiamata passa dalla vtable invece che da un'interfaccia <c>[ComImport]</c>: funziona con
/// il marshalling di CsWinRT e con il trimming/AOT. Ordine dei metodi: IUnknown (0–2),
/// SetSwapChain (3).
/// </remarks>
internal static class SwapChainPanelInterop
{
    private static readonly Guid IidSwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public static unsafe void SetSwapChain(SwapChainPanel panel, nint swapChain)
    {
        var unknown = ((IWinRTObject)panel).NativeObject.ThisPtr;
        var iid = IidSwapChainPanelNative;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var native));
        try
        {
            var vtable = *(void***)native;
            var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }
}

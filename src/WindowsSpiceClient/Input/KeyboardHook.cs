using System.ComponentModel;
using System.Runtime.InteropServices;
using WindowsSpiceClient.Diagnostics;

namespace WindowsSpiceClient.Input;

/// <summary>Un tasto visto dall'hook: codice virtuale, scancode, esteso, premuto.</summary>
internal readonly record struct KeyStroke(uint VirtualKey, uint ScanCode, bool Extended, bool Down);

/// <summary>
/// Hook della tastiera a basso livello (<c>WH_KEYBOARD_LL</c>), condiviso dalle finestre di sessione.
/// </summary>
/// <remarks>
/// Serve perché le combinazioni di sistema (Alt+Tab, il tasto Windows, Alt+F4, Ctrl+Esc) arrivino
/// all'ospite come in virt-viewer: i normali eventi di tastiera di WinUI non le ricevono. L'hook
/// passa ogni tasto alla finestra di sessione in primo piano (se ce n'è una registrata), che decide
/// se trattenerlo. Ctrl+Alt+Canc resta di Windows: per l'ospite c'è la scorciatoia apposita.
/// La funzione di richiamo gira sul thread dell'interfaccia (quello che ha installato l'hook)
/// e deve essere breve: Windows rimuove gli hook che superano il tempo limite.
/// </remarks>
internal static unsafe class KeyboardHook
{
    private static readonly Dictionary<nint, Func<KeyStroke, bool>> Sinks = [];
    private static nint _hook;

    /// <summary>Riceve i tasti quando <paramref name="hwnd"/> è in primo piano; il gestore restituisce <see langword="true"/> per trattenerli.</summary>
    public static void Register(nint hwnd, Func<KeyStroke, bool> handler)
    {
        Sinks[hwnd] = handler;
        if (_hook != 0) return;
        _hook = Native.SetWindowsHookEx(Native.WhKeyboardLl, &Callback, Native.GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            AppLog.Error("Hook della tastiera non installato", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    public static void Unregister(nint hwnd)
    {
        Sinks.Remove(hwnd);
        if (Sinks.Count > 0 || _hook == 0) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    [UnmanagedCallersOnly]
    private static nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && Sinks.TryGetValue(Native.GetForegroundWindow(), out var sink))
        {
            var data = (Native.KbdLlHookStruct*)lParam;
            var stroke = new KeyStroke(data->VkCode, data->ScanCode, (data->Flags & Native.LlkhfExtended) != 0,
                (data->Flags & Native.LlkhfUp) == 0);
            try
            {
                if (sink(stroke)) return 1;
            }
            catch (Exception error)
            {
                // Mai un'eccezione verso Windows: il tasto prosegue normalmente.
                AppLog.Error("Errore nella gestione di un tasto", error);
            }
        }
        return Native.CallNextHookEx(0, code, wParam, lParam);
    }
}

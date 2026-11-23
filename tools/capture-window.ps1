<#
.SYNOPSIS
  Porta in primo piano la finestra principale di un processo e ne salva una cattura PNG.
  Serve per verificare l'interfaccia durante lo sviluppo (es. tools\capture-window.ps1 -ProcessId 1234 -Path docs\m0.png).
#>
param(
    [Parameter(Mandatory)] [int] $ProcessId,
    [Parameter(Mandatory)] [string] $Path,
    # PrintWindow(PW_RENDERFULLCONTENT): legge il contenuto composto (DirectComposition) della
    # finestra invece di copiare lo schermo; funziona anche se la finestra è coperta.
    [switch] $PrintWindow
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class WinCapture {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public static IntPtr MainWindow(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      var c = new StringBuilder(256); GetClassName(h, c, 256);
      if (p == pid && IsWindowVisible(h) && c.ToString() == "WinUIDesktopWin32WindowClass") { found = h; return false; }
      return true; }, IntPtr.Zero);
    return found;
  }
}
"@

$hwnd = [WinCapture]::MainWindow([uint32]$ProcessId)
if ($hwnd -eq [IntPtr]::Zero) { throw "Nessuna finestra WinUI visibile per il processo $ProcessId." }
[void][WinCapture]::ShowWindow($hwnd, 9)   # SW_RESTORE
[void][WinCapture]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 800
$rect = New-Object WinCapture+RECT
if ($PrintWindow) {
    [void][WinCapture]::GetWindowRect($hwnd, [ref]$rect)
} else {
    [void][WinCapture]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, 16)   # DWMWA_EXTENDED_FRAME_BOUNDS
}
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
if ($PrintWindow) {
    $hdc = $graphics.GetHdc()
    [void][WinCapture]::PrintWindow($hwnd, $hdc, 2)   # PW_RENDERFULLCONTENT
    $graphics.ReleaseHdc($hdc)
} else {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
}
New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
$bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose()
"$Path ($width x $height)"

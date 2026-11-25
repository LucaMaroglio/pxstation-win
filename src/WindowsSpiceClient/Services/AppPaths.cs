namespace WindowsSpiceClient.Services;

/// <summary>Cartelle dell'app e identità usata in Gestione credenziali.</summary>
internal static class AppPaths
{
    public const string AppName = "PxStation Windows";
    public const string Copyright = "2026 - Luca Maroglio";
    public const string AppId = "org.windowsspiceclient.WindowsSpiceClient";

    /// <summary>
    /// Impostazioni e server: <c>%APPDATA%\WindowsSpiceClient</c>. <c>WSC_DATA_DIR</c> la sostituisce
    /// (autoverifica e prove, per non toccare la configurazione reale).
    /// </summary>
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("WSC_DATA_DIR") is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsSpiceClient");

    /// <summary>Vero quando la configurazione è stata spostata con <c>WSC_DATA_DIR</c>.</summary>
    public static bool IsIsolated { get; } = Environment.GetEnvironmentVariable("WSC_DATA_DIR") is { Length: > 0 };

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WindowsSpiceClient.exe");

    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

    public static string Version => typeof(AppPaths).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

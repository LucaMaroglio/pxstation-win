namespace WindowsSpiceClient.Services;

/// <summary>Preferenze generali (<c>settings.json</c>), come <c>Settings</c> del client Linux.</summary>
internal sealed class Settings
{
    private const string FileName = "settings.json";

    public bool ReconnectAutomatically { get; set; } = true;
    public bool OpenFullScreen { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    public bool Notifications { get; set; } = true;

    /// <summary>Lingua dell'interfaccia ("en-US", "it-IT"); vuota = predefinita di sistema. Vedi <see cref="Services.Language"/>.</summary>
    public string Language { get; set; } = "";

    public static Settings Load() => JsonFiles.Load(FileName, AppJsonContext.Default.Settings, () => new Settings());

    public void Save() => JsonFiles.Save(FileName, this, AppJsonContext.Default.Settings);
}

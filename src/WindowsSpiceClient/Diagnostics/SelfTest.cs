using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SpiceKit.Channels;
using SpiceKit.Proxmox;
using SpiceKit.VVFile;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WindowsSpiceClient.Services;
using WindowsSpiceClient.Shell;
using WindowsSpiceClient.Views;

namespace WindowsSpiceClient.Diagnostics;

/// <summary>
/// Autoverifica dell'app, attivata da <c>--self-test &lt;cartella&gt;</c>.
/// </summary>
/// <remarks>
/// <para>Apre una alla volta Benvenuto, Macchine virtuali, Preferenze (entrambe le pagine) e una
/// finestra di sessione; disegna ognuna con <see cref="RenderTargetBitmap"/> in un PNG e scrive
/// l'esito in <c>self-test.txt</c>. Non dipende dalla cattura dello schermo, che in una sessione
/// remota o in una VM senza GPU può restituire fotogrammi vecchi o neri. Il renderer Direct3D si
/// verifica rileggendo il buffer, perché <see cref="RenderTargetBitmap"/> non vede gli SwapChainPanel.</para>
/// <para>Tocca la configurazione solo se è isolata con <c>WSC_DATA_DIR</c>. Con
/// <c>WSC_SELFTEST_SERVER=host:porta|tokenid|segreto|sha256</c> usa quel server (es. il finto
/// Proxmox della prova) e, se c'è una VM accesa, prova anche la connessione con <c>spiceproxy</c>.</para>
/// </remarks>
internal static class SelfTest
{
    public static string? OutputDirectory { get; } = ParseArguments(Environment.GetCommandLineArgs());

    public static bool IsEnabled => OutputDirectory is not null;

    private static string? ParseArguments(string[] args)
    {
        var index = Array.IndexOf(args, "--self-test");
        return index < 0 ? null : Path.GetFullPath(index + 1 < args.Length ? args[index + 1] : "self-test");
    }

    public static async Task RunAsync(App app)
    {
        var directory = OutputDirectory!;
        Directory.CreateDirectory(directory);
        var lines = new List<string>();
        try
        {
            await RunStepsAsync(app, directory, lines);
        }
        catch (Exception error)
        {
            lines.Add($"ERRORE inatteso: {error}");
        }
        finally
        {
            // Niente credenziali di prova lasciate in Gestione credenziali.
            if (AppPaths.IsIsolated)
            {
                foreach (var server in app.Controller.Store.Servers.ToList()) app.Controller.Store.Remove(server);
            }
        }
        await File.WriteAllLinesAsync(Path.Combine(directory, "self-test.txt"), lines);
        AppLog.Info("Autoverifica: " + string.Join(" | ", lines));
    }

    private static async Task RunStepsAsync(App app, string directory, List<string> lines)
    {
        // Testi da .resw: se le risorse non si caricano, Strings restituisce la chiave.
        var welcomeText = Strings.Get("WelcomeText/Text");
        lines.Add(welcomeText.StartsWith("Apri un file", StringComparison.Ordinal)
            ? "risorse: OK" : $"risorse: ERRORE ({welcomeText})");

        var welcome = app.ShowWelcome();
        await SettleAsync(welcome.RootElement, 800);
        lines.Add(await CaptureAsync(welcome.RootElement, directory, "welcome.png"));
        lines.Add(AccessibilityResult(welcome.RootElement, "benvenuto"));
        welcome.Close();
        lines.AddRange(ShellResults(app));

        ProxmoxServer? server = null;
        if (AppPaths.IsIsolated)
        {
            server = SeedServer(app);
            lines.Add($"server di prova: {server.Name} ({server.Host}:{server.Port})");
        }
        else
        {
            lines.Add("server di prova: saltato (WSC_DATA_DIR non impostata, configurazione reale intatta)");
        }

        var vmList = app.ShowVmList();
        await SettleAsync(vmList.RootElement, 400);
        // Elenco e poi stati dettagliati (pausa): si attende la fine dell'intero aggiornamento.
        await WaitForAsync(() => !vmList.IsLoading && vmList.SectionModels.All(s => s.Message != Strings.Get("Loading")),
            TimeSpan.FromSeconds(30));
        await SettleAsync(vmList.RootElement, 600);
        foreach (var section in vmList.SectionModels)
        {
            lines.Add(section.IsError
                ? $"vm ({section.Title}): errore mostrato: {section.Message}"
                : $"vm ({section.Title}): {string.Join(", ", section.Rows.Select(r => $"{r.Name}={r.Vm.Status}"))}");
        }
        lines.Add(await CaptureAsync(vmList.RootElement, directory, "vmlist.png"));
        lines.Add(AccessibilityResult(vmList.RootElement, "macchine virtuali"));

        // Connessione end-to-end (solo con un server vero/finto con almeno una VM accesa).
        var running = vmList.SectionModels.SelectMany(s => s.Rows).FirstOrDefault(r => r.Vm.IsRunning);
        if (server is not null && running is not null)
        {
            try
            {
                var before = app.Sessions.Count;
                await app.Controller.ConnectVmAsync(running.Server, running.Vm);
                lines.Add($"connessione {running.Name}: OK (spiceproxy letto e convalidato)");
                if (app.Sessions.Count > before)
                {
                    var opened = app.Sessions[^1];
                    lines.Add(await SessionResultAsync(opened, $"spice {running.Name}"));
                    if (opened.SessionState?.Kind == SpiceKit.Session.SessionStateKind.Connected)
                    {
                        lines.Add(await ScreenResultAsync(opened, directory, running.Name));
                        lines.Add(await ReconnectResultAsync(opened, running.Name, expectReconnect: true));
                        lines.Add(NotificationsResult(app, running.Name));
                        lines.Add(SessionTrayResult(opened, running.Name, expectPower: true));
                    }
                    opened.Close();
                }
            }
            catch (Exception error) when (error is ProxmoxException or VVFileException)
            {
                lines.Add($"connessione {running.Name}: ERRORE {error.Message}");
            }
        }
        vmList.Close();

        var preferences = app.ShowPreferences();
        await SettleAsync(preferences.RootElement, 800);
        lines.Add(await CaptureAsync(preferences.RootElement, directory, "preferences-general.png"));
        lines.Add(AccessibilityResult(preferences.RootElement, "preferenze generali"));
        preferences.ShowPage(PreferencesPage.Proxmox);
        await SettleAsync(preferences.RootElement, 600);
        lines.Add(await CaptureAsync(preferences.RootElement, directory, "preferences-proxmox.png"));
        lines.Add(AccessibilityResult(preferences.RootElement, "preferenze proxmox"));
        preferences.Close();

        // File .vv da provare (tools/selftest_spice.py: QEMU in chiaro, TLS via proxy, ticket sbagliato).
        var vvFiles = Environment.GetEnvironmentVariable("WSC_SELFTEST_VV")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
        foreach (var path in vvFiles)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var window = app.OpenSession(VVFileParser.ReadFile(path).Profile, null, name);
            lines.Add(await SessionResultAsync(window, $"spice {name}"));
            if (window.SessionState?.Kind == SpiceKit.Session.SessionStateKind.Connected)
            {
                lines.Add(await ScreenResultAsync(window, directory, name));
                if (name.Contains("video", StringComparison.Ordinal)) lines.Add(await VideoResultAsync(window, directory, name));
                else if (name.Contains("audio", StringComparison.Ordinal)) lines.Add(await AudioResultAsync(window, name));
                else if (name == "usb") lines.AddRange(await DevicesResultAsync(window, directory, name));
                else lines.Add(await InputResultAsync(window, directory, name));
                if (name == "in-chiaro")
                {
                    lines.Add(SessionTrayResult(window, name, expectPower: false));
                    lines.Add(AccessibilityResult(window.RootElement, "sessione"));
                }
                // Ticket monouso senza server Proxmox configurato: niente riconnessione, ma la spiegazione.
                if (name == "in-chiaro") lines.Add(await ReconnectResultAsync(window, name, expectReconnect: false));
            }
            await SettleAsync(window.RootElement, 300);
            lines.Add(await CaptureAsync(window.RootElement, directory, $"session-{name}.png"));
            window.Close();
        }

        // Porta 1 (tcpmux): nessun server SPICE, la connessione deve fallire con un messaggio.
        var profile = VVFileParser.Parse("[virt-viewer]\ntype=spice\nhost=127.0.0.1\nport=1\ntitle=Autoverifica\n").Profile;
        var session = app.OpenSession(profile, null, null);
        await SettleAsync(session.RootElement, 1500); // primo layout, ridimensionamento della swap chain
        lines.Add(session.ProbeRenderer());
        lines.Add(await SessionResultAsync(session, "spice senza server"));
        lines.Add(await CaptureAsync(session.RootElement, directory, "session.png"));
        session.Close();
    }

    private static ProxmoxServer SeedServer(App app)
    {
        foreach (var existing in app.Controller.Store.Servers.ToList()) app.Controller.Store.Remove(existing);
        var spec = Environment.GetEnvironmentVariable("WSC_SELFTEST_SERVER");
        if (spec?.Split('|') is [var endpoint, var token, var secret, var sha256])
        {
            var colon = endpoint.LastIndexOf(':');
            var server = new ProxmoxServer
            {
                Name = "Proxmox di prova",
                Host = endpoint[..colon],
                Port = int.Parse(endpoint[(colon + 1)..], CultureInfo.InvariantCulture),
                TokenId = token,
                PinnedSha256 = sha256.ToLowerInvariant(),
            };
            app.Controller.Store.Save(server, secret);
            return server;
        }
        // Nessun server raggiungibile: verifica che l'errore venga mostrato nella sezione.
        var unreachable = new ProxmoxServer { Name = "Server irraggiungibile", Host = "pve.invalid", TokenId = "root@pam!prova" };
        app.Controller.Store.Save(unreachable, "00000000-0000-0000-0000-000000000000");
        return unreachable;
    }

    private static async Task SettleAsync(FrameworkElement root, int milliseconds)
    {
        if (!root.IsLoaded)
        {
            var loaded = new TaskCompletionSource();
            root.Loaded += (_, _) => loaded.TrySetResult();
            await Task.WhenAny(loaded.Task, Task.Delay(5000));
        }
        await Task.Delay(milliseconds);
    }

    /// <summary>Attende che la sessione esca da "connessione in corso" e ne riporta lo stato.</summary>
    private static async Task<string> SessionResultAsync(SessionWindow window, string label)
    {
        await WaitForAsync(() => window.SessionState?.Kind is not SpiceKit.Session.SessionStateKind.Connecting
                                 and not SpiceKit.Session.SessionStateKind.Idle, TimeSpan.FromSeconds(40));
        await Task.Delay(300); // ultimi eventi (elenco dei canali) sull'interfaccia
        var state = window.SessionState;
        var text = window.StatusText.Replace('\n', ' ');
        return state?.Kind switch
        {
            SpiceKit.Session.SessionStateKind.Connected => $"{label}: connessa · {text}",
            SpiceKit.Session.SessionStateKind.Disconnected when state.Failure is { } failure =>
                $"{label}: non riuscita{(failure.IsAuthenticationFailure ? " (ticket)" : "")} · {text}",
            _ => $"{label}: stato {state?.Kind} · {text}",
        };
    }

    /// <summary>
    /// Attende lo schermo della VM e lo salva dalla texture Direct3D in <c>screen-&lt;nome&gt;.png</c>
    /// (RenderTargetBitmap non vede lo SwapChainPanel). Con il firmware della VM di prova lo schermo
    /// ha testo: si contano i colori distinti per distinguere un'immagine vera da una vuota.
    /// </summary>
    private static async Task<string> ScreenResultAsync(SessionWindow window, string directory, string name)
    {
        // Gli aggiornamenti continuano finché il firmware scrive: si attende che arrivino e si fermino un attimo.
        await WaitForAsync(() => window.ScreenUpdates > 0, TimeSpan.FromSeconds(20));
        var last = -1;
        for (var i = 0; i < 20 && window.ScreenUpdates != last; i++)
        {
            last = window.ScreenUpdates;
            await Task.Delay(500);
        }
        if (window.CaptureScreen() is not { } capture) return $"schermo {name}: ERRORE nessuna superficie ({window.ScreenUpdates} aggiornamenti)";
        var colors = new HashSet<uint>();
        for (var i = 0; i < capture.Bgra.Length && colors.Count < 1000; i += 4) colors.Add(BitConverter.ToUInt32(capture.Bgra, i));
        var file = $"screen-{name}.png";
        await File.WriteAllBytesAsync(Path.Combine(directory, file), await EncodePngAsync(capture.Bgra, capture.Width, capture.Height));
        return string.Create(CultureInfo.InvariantCulture,
            $"schermo {name}: {(colors.Count > 1 ? "OK" : "VUOTO")} {capture.Width}x{capture.Height}, {window.ScreenUpdates} aggiornamenti, {colors.Count} colori -> {file}");
    }

    /// <summary>
    /// Input verso l'ospite: un movimento e un clic del mouse, poi Ctrl+Alt+Canc come dal menu.
    /// Il BIOS della VM di prova riavvia la macchina (tools/selftest_spice.py lo verifica con QMP)
    /// e lo schermo ricomincia ad aggiornarsi; la sessione deve restare collegata.
    /// </summary>
    private static async Task<string> InputResultAsync(SessionWindow window, string directory, string name)
    {
        var mode = window.MouseMode;
        if (mode == SpiceKit.Wire.MouseModes.Client)
        {
            window.SendInput(new InputEvent.Position(100, 100, SpiceKit.Wire.MouseButtons.None, 0));
        }
        else
        {
            window.SendInput(new InputEvent.Motion(5, 5, SpiceKit.Wire.MouseButtons.None));
        }
        window.SendInput(new InputEvent.Press(SpiceKit.Wire.MouseButton.Left, SpiceKit.Wire.MouseButtons.Left));
        window.SendInput(new InputEvent.Release(SpiceKit.Wire.MouseButton.Left, SpiceKit.Wire.MouseButtons.None));
        var before = window.ScreenUpdates;
        window.Perform(SpiceKit.Input.HotkeyAction.SecureAttention);
        await WaitForAsync(() => window.ScreenUpdates > before + 2, TimeSpan.FromSeconds(10));
        await Task.Delay(1500);
        var redrawn = window.ScreenUpdates - before;
        var connected = window.SessionState?.Kind == SpiceKit.Session.SessionStateKind.Connected;
        var file = $"screen-{name}-dopo-input.png";
        if (window.CaptureScreen() is { } capture)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, file), await EncodePngAsync(capture.Bgra, capture.Width, capture.Height));
        }
        var ok = connected && window.InputsSent >= 9 && redrawn > 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"input {name}: {(ok ? "OK" : "ERRORE")} puntatore {mode}, {window.InputsSent} input inviati, {redrawn} aggiornamenti dopo Ctrl+Alt+Canc, {(connected ? "sessione collegata" : "sessione chiusa")} -> {file}");
    }

    /// <summary>
    /// Flusso video (M5): la VM di prova anima un gradiente e il server lo invia come flusso
    /// (MJPEG o H.264); i fotogrammi devono essere decodificati e disegnati sullo schermo.
    /// </summary>
    private static async Task<string> VideoResultAsync(SessionWindow window, string directory, string name)
    {
        var codecs = string.Join(", ", SpiceKit.Windows.Media.WindowsMediaDecoders.Shared.VideoCodecs);
        await WaitForAsync(() => window.VideoFrames.Decoded >= 25, TimeSpan.FromSeconds(30));
        var (decoded, dropped) = window.VideoFrames;
        var file = $"screen-{name}-flusso.png";
        if (window.CaptureScreen() is { } capture)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, file), await EncodePngAsync(capture.Bgra, capture.Width, capture.Height));
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"video {name}: {(decoded >= 25 ? "OK" : "ERRORE")} {decoded} fotogrammi decodificati, {dropped} scartati (codec annunciati: {codecs}) -> {file}");
    }

    /// <summary>
    /// Riconnessione (M6): si simula una caduta della rete. Con un server Proxmox configurato la
    /// finestra chiede un ticket nuovo all'API e torna collegata con lo schermo; con un ticket
    /// monouso deve fermarsi spiegando perché.
    /// </summary>
    private static async Task<string> ReconnectResultAsync(SessionWindow window, string name, bool expectReconnect)
    {
        var updates = window.ScreenUpdates;
        await window.SimulateConnectionLossAsync();
        await Task.Delay(500);
        if (!expectReconnect)
        {
            var ended = window.SessionState?.Kind == SpiceKit.Session.SessionStateKind.Disconnected && window.ReconnectAttempt == 0;
            return $"riconnessione {name}: {(ended ? "OK nessun tentativo" : "ERRORE")} · {window.StatusText.Replace('\n', ' ')}";
        }
        await WaitForAsync(() => window.Reconnections > 0 && window.SessionState?.Kind == SpiceKit.Session.SessionStateKind.Connected,
            TimeSpan.FromSeconds(30));
        await WaitForAsync(() => window.ScreenUpdates > updates, TimeSpan.FromSeconds(15));
        var ok = window.Reconnections > 0 && window.SessionState?.Kind == SpiceKit.Session.SessionStateKind.Connected && window.ScreenUpdates > updates;
        return string.Create(CultureInfo.InvariantCulture,
            $"riconnessione {name}: {(ok ? "OK" : "ERRORE")} {window.Reconnections} riconnessioni, {window.ScreenUpdates - updates} aggiornamenti dopo · {window.StatusText.Replace('\n', ' ')}");
    }

    /// <summary>
    /// Notifiche (M9) dopo la riconnessione: «Connessione persa» e «Riconnessa», come il client Linux
    /// (registrate soltanto: l'autoverifica non mostra notifiche vere).
    /// </summary>
    private static string NotificationsResult(App app, string name)
    {
        var history = app.Controller.Notifier.History;
        var ok = history.Any(h => h.StartsWith(Strings.Get("NotifyLostTitle"), StringComparison.Ordinal))
                 && history.Any(h => h.StartsWith(Strings.Get("NotifyReconnectedTitle"), StringComparison.Ordinal));
        return $"notifiche {name}: {(ok ? "OK" : "ERRORE")} {string.Join(" | ", history)}";
    }

    private static readonly string[] TrayCommands = ["TrayVmList", "TrayOpenFile", "TrayPreferences", "TrayQuit"];

    /// <summary>Area di notifica e jump list (M9): icona accettata dalla shell, voci del menu, attività.</summary>
    private static string[] ShellResults(App app)
    {
        var menu = MenuEntry.Describe(app.TrayEntries()).ToList();
        var expected = TrayCommands.Select(Strings.Get).ToList();
        var trayOk = app.Tray?.IsShown == true && expected.All(menu.Contains) && app.TrayTooltip() == AppPaths.AppName;
        var request = CommandLine.Parse($"\"{AppPaths.ExecutablePath}\" {CommandLine.Preferences}");
        var tasks = JumpList.Tasks;
        var jumpOk = tasks.Count == 3 && request.Command == CommandLine.Preferences && request.Files.Count == 0;
        return
        [
            $"area di notifica: {(trayOk ? "OK" : "ERRORE")} icona {(app.Tray?.IsShown == true ? "mostrata" : "assente")} · {string.Join(" / ", menu)}",
            $"jump list: {(jumpOk ? "OK" : "ERRORE")} {string.Join(", ", tasks.Select(t => $"{t.Title} ({t.Command})"))}",
        ];
    }

    /// <summary>Menu della sessione nell'icona: stato breve, comandi e alimentazione (sessioni Proxmox).</summary>
    private static string SessionTrayResult(SessionWindow window, string name, bool expectPower)
    {
        var lines = MenuEntry.Describe([window.TrayEntry()]).ToList();
        var power = lines.Any(l => l.Contains(Strings.Get("TrayPower"), StringComparison.Ordinal));
        var ok = lines.Count > 0 && lines[0].EndsWith(Strings.Get("StateConnected"), StringComparison.Ordinal) && power == expectPower
                 && lines.Any(l => l.EndsWith(Strings.Get("TrayDisconnect"), StringComparison.Ordinal));
        return $"menu sessione {name}: {(ok ? "OK" : "ERRORE")} {string.Join(" / ", lines)}";
    }

    /// <summary>
    /// Accessibilità (M9): ogni controllo interattivo deve avere un nome per l'assistente vocale
    /// (testo, <c>AutomationProperties.Name</c> o etichetta). Elenca quelli senza.
    /// </summary>
    private static string AccessibilityResult(UIElement root, string name)
    {
        var missing = new List<string>();
        var count = 0;
        Walk(root);
        return missing.Count == 0
            ? $"accessibilità {name}: OK {count} controlli con nome"
            : $"accessibilità {name}: ERRORE senza nome: {string.Join(", ", missing)}";

        void Walk(DependencyObject element)
        {
            if (element is Microsoft.UI.Xaml.Controls.Control { IsTabStop: true } control
                && element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or Microsoft.UI.Xaml.Controls.ToggleSwitch
                    or Microsoft.UI.Xaml.Controls.TextBox or Microsoft.UI.Xaml.Controls.PasswordBox
                    or Microsoft.UI.Xaml.Controls.ComboBox or Microsoft.UI.Xaml.Controls.ListView)
            {
                if (control.Visibility == Visibility.Visible)
                {
                    count++;
                    var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(control);
                    if (string.IsNullOrWhiteSpace(peer?.GetName()))
                    {
                        missing.Add(string.IsNullOrEmpty(control.Name) ? control.GetType().Name : control.Name);
                    }
                }
            }
            var children = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
            for (var i = 0; i < children; i++) Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i));
        }
    }

    /// <summary>
    /// Audio (M7): la VM di prova suona un La con l'altoparlante del PC; i pacchetti del canale
    /// playback devono arrivare ed essere decodificati. L'uscita può mancare (VM senza scheda audio).
    /// </summary>
    private static async Task<string> AudioResultAsync(SessionWindow window, string name)
    {
        await WaitForAsync(() => window.AudioStatistics.Samples > 48_000, TimeSpan.FromSeconds(20));
        var (packets, samples, output, _) = window.AudioStatistics;
        return string.Create(CultureInfo.InvariantCulture,
            $"audio {name}: {(samples > 48_000 ? "OK" : "ERRORE")} {packets} pacchetti, {samples} campioni decodificati, uscita: {output}");
    }

    /// <summary>
    /// Dispositivi (M8): la ISO di prova (<c>WSC_SELFTEST_ISO</c>) si monta come CD-ROM USB e il BIOS
    /// della VM, al riavvio successivo, ne esegue il settore di avvio (che scrive «SPICE ISO OK»:
    /// <c>selftest_spice.py</c> lo legge dalla console di debug di QEMU); poi una carta finta entra
    /// nel lettore del canale smartcard.
    /// </summary>
    private static async Task<string[]> DevicesResultAsync(SessionWindow window, string directory, string name)
    {
        await WaitForAsync(() => window.DeviceSupport.UsbSlots > 0, TimeSpan.FromSeconds(5));
        var (slots, smartcardChannel) = window.DeviceSupport;
        var iso = Environment.GetEnvironmentVariable("WSC_SELFTEST_ISO");
        var mounted = iso is not null && await window.MountIsoAsync(iso);
        var updates = window.ScreenUpdates;
        // Il BIOS si riavvia ogni 2 s senza disco: al giro dopo trova il CD-ROM USB e ne esegue l'avvio.
        await Task.Delay(TimeSpan.FromSeconds(12));
        var file = $"screen-{name}-iso.png";
        if (window.CaptureScreen() is { } capture)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, file), await EncodePngAsync(capture.Bgra, capture.Width, capture.Height));
        }
        var shared = smartcardChannel && await window.ShareSmartcardAsync(new SelfTestCard());
        return
        [
            string.Create(CultureInfo.InvariantCulture,
                $"iso {name}: {(mounted ? "OK" : "ERRORE")} {slots} slot USB, montata, {window.ScreenUpdates - updates} aggiornamenti dopo -> {file}"),
            $"smartcard {name}: {(shared ? "OK lettore accettato dalla VM e carta inserita" : "ERRORE")} (canale smartcard: {(smartcardChannel ? "sì" : "no")})",
        ];
    }

    /// <summary>Carta finta per l'autoverifica: ATR di una carta T=1, risponde 90 00.</summary>
    private sealed class SelfTestCard : ISmartcard
    {
        public string DisplayName => "Carta di prova";

        public Task<byte[]> ConnectAsync() => Task.FromResult<byte[]>([0x3B, 0x80, 0x80, 0x01, 0x01]);

        public Task<byte[]> TransmitAsync(byte[] apdu) => Task.FromResult<byte[]>([0x90, 0x00]);

        public Task DisconnectAsync() => Task.CompletedTask;
    }

    private static async Task<byte[]> EncodePngAsync(byte[] bgra, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        var png = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(png.AsBuffer(), (uint)png.Length, InputStreamOptions.None);
        return png;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(200);
    }

    private static async Task<string> CaptureAsync(UIElement root, string directory, string name)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root);
            var pixels = await bitmap.GetPixelsAsync();
            var png = await EncodePngAsync(pixels.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
            await File.WriteAllBytesAsync(Path.Combine(directory, name), png);
            return string.Create(CultureInfo.InvariantCulture, $"xaml: OK {bitmap.PixelWidth}x{bitmap.PixelHeight} -> {name}");
        }
        catch (Exception error) when (error is InvalidOperationException or IOException
                                          or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return $"xaml: ERRORE {name}: {error.Message}";
        }
    }
}

using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SpiceKit.Proxmox;
using SpiceKit.VVFile;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WindowsSpiceClient.Services;
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
        welcome.Close();

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
        preferences.ShowPage(PreferencesPage.Proxmox);
        await SettleAsync(preferences.RootElement, 600);
        lines.Add(await CaptureAsync(preferences.RootElement, directory, "preferences-proxmox.png"));
        preferences.Close();

        // File .vv da provare (tools/selftest_spice.py: QEMU in chiaro, TLS via proxy, ticket sbagliato).
        var vvFiles = Environment.GetEnvironmentVariable("WSC_SELFTEST_VV")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
        foreach (var path in vvFiles)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var window = app.OpenSession(VVFileParser.ReadFile(path).Profile, null, name);
            lines.Add(await SessionResultAsync(window, $"spice {name}"));
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
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            var png = new byte[stream.Size];
            stream.Seek(0);
            await stream.ReadAsync(png.AsBuffer(), (uint)png.Length, InputStreamOptions.None);
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

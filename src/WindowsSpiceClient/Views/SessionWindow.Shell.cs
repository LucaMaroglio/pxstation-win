using System.Diagnostics;
using SpiceKit.Proxmox;
using SpiceKit.Session;
using SpiceKit.VVFile;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;
using WindowsSpiceClient.Shell;

namespace WindowsSpiceClient.Views;

/// <summary>Stato di una sessione come lo vedono l'area di notifica e le notifiche.</summary>
internal enum SessionPhase
{
    Connecting,
    Connected,
    Reconnecting,
    Ended,
}

/// <summary>Perché una sessione è finita: decide le azioni offerte (come <c>EndKind</c> del client Linux).</summary>
internal enum SessionEndKind
{
    None,
    Dropped,
    VmNotRunning,
    NoApi,
    Cancelled,
}

/// <summary>
/// La sessione vista da fuori della finestra (M9): stato breve, azioni per l'icona nell'area di
/// notifica e per le notifiche, alimentazione della VM. Stesse voci del client Linux.
/// </summary>
public sealed partial class SessionWindow
{
    private static int s_nextId;
    private SessionEndKind _endKind;
    private bool _busy;
    private long _expectEndUntil;
    private bool _everConnected;

    /// <summary>Identificatore della sessione nelle notifiche.</summary>
    internal int Id { get; } = Interlocked.Increment(ref s_nextId);

    /// <summary>Lo stato mostrato fuori dalla finestra è cambiato (icona nell'area di notifica).</summary>
    internal event EventHandler? StatusChanged;

    internal string DisplayTitle => AppTitleBar.Title;

    internal SessionPhase Phase =>
        _reconnect is not null || (_attempt > 0 && _session?.State.Kind is SessionStateKind.Connecting or SessionStateKind.Idle)
            ? SessionPhase.Reconnecting
            : _session?.State.Kind switch
            {
                SessionStateKind.Connected => SessionPhase.Connected,
                SessionStateKind.Connecting or SessionStateKind.Idle => SessionPhase.Connecting,
                _ => SessionPhase.Ended,
            };

    internal bool IsActive => Phase != SessionPhase.Ended;

    /// <summary>Stato breve per il menu: «connessione…», «connessa», «riconnessione 2/8», «terminata».</summary>
    internal string ShortState => Phase switch
    {
        SessionPhase.Connecting => Strings.Get("StateConnecting"),
        SessionPhase.Connected => Strings.Get("StateConnected"),
        SessionPhase.Reconnecting => Strings.Format("StateReconnecting", _attempt, _policy.MaxAttempts),
        _ => Strings.Get("StateEnded"),
    };

    /// <summary>Spiegazione della fine, su una riga e al massimo 80 caratteri.</summary>
    internal string? EndMessage
    {
        get
        {
            if (Phase != SessionPhase.Ended || string.IsNullOrWhiteSpace(ProfileInfo.Message)) return null;
            var text = string.Join(' ', ProfileInfo.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return text.Length < 80 ? text : text[..77] + "…";
        }
    }

    /// <summary>La VM si può accendere e spegnere: sessione Proxmox con il server nelle preferenze.</summary>
    internal bool CanControlPower => _reference is not null && Server is not null;

    /// <summary>Che cosa offre una sessione finita (pulsanti delle notifiche e voci del menu).</summary>
    internal IReadOnlyList<NotificationAction> EndActions()
    {
        if (Phase != SessionPhase.Ended) return [];
        if (_endKind == SessionEndKind.VmNotRunning && CanControlPower) return [new("start-reconnect", Strings.Get("ActionStartReconnect"))];
        if (_endKind == SessionEndKind.NoApi) return [new("setup-proxmox", Strings.Get("TraySetupProxmox"))];
        if (_endKind != SessionEndKind.VmNotRunning && CanReconnect) return [new("reconnect", Strings.Get("SessionReconnect"))];
        return [];
    }

    /// <summary>Esegue un'azione chiesta da una notifica o dal menu dell'icona.</summary>
    internal void RunAction(string? action)
    {
        switch (action)
        {
            case "start-reconnect":
                _ = StartAndReconnectAsync();
                break;
            case "reconnect":
                if (Phase == SessionPhase.Ended) ReconnectNow();
                break;
            case "setup-proxmox" when _reference is not null:
                ((App)Microsoft.UI.Xaml.Application.Current).ShowPreferences(PreferencesPage.Proxmox, _reference.ApiHost);
                break;
            default:
                WindowChrome.BringToFront(this);
                break;
        }
    }

    /// <summary>Sottomenu della sessione nell'area di notifica (come <c>session_entries</c> su Linux).</summary>
    internal MenuEntry TrayEntry()
    {
        var entries = new List<MenuEntry> { new(Strings.Get("TrayShowWindow"), () => WindowChrome.BringToFront(this)) };
        if (EndMessage is { } message) entries.Add(MenuEntry.Info(message));
        if (Phase == SessionPhase.Reconnecting)
        {
            entries.Add(new(Strings.Get("TrayRetryNow"), ReconnectNow));
            entries.Add(new(Strings.Get("TrayCancelReconnect"), CancelReconnect));
        }
        foreach (var action in EndActions())
        {
            var id = action.Id;
            entries.Add(new(action.Label, () => RunAction(id), Enabled: !_busy));
        }
        if (CanControlPower)
        {
            var power = new List<MenuEntry>();
            foreach (var group in new[] { SessionPowerActions, SessionDestructiveActions })
            {
                if (power.Count > 0) power.Add(MenuEntry.Separator());
                power.AddRange(group.Select(a => new MenuEntry(a.Title(), () => _ = PowerAsync(a))));
            }
            entries.Add(new(Strings.Get("TrayPower"), Children: power, Enabled: !_busy));
        }
        else if (_reference is not null)
        {
            entries.Add(new(Strings.Get("TraySetupProxmox"), () => RunAction("setup-proxmox")));
        }
        entries.Add(MenuEntry.Separator());
        entries.Add(new(Strings.Get(IsActive ? "TrayDisconnect" : "TrayRemove"), Close));
        return new MenuEntry(Strings.Format("TrayEntry", DisplayTitle, ShortState), Children: entries);
    }

    private static readonly PowerAction[] SessionPowerActions = [PowerAction.Shutdown, PowerAction.Reboot, PowerAction.Suspend, PowerAction.Resume];
    private static readonly PowerAction[] SessionDestructiveActions = [PowerAction.Reset, PowerAction.Stop];

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (Phase != SessionPhase.Ended) Controller.Notifier.Withdraw(Id);
    }

    /// <summary>Notifica di Windows, se attive nelle preferenze.</summary>
    private void Notify(string title, string body, IReadOnlyList<NotificationAction>? actions = null)
    {
        if (!Controller.Settings.Notifications) return;
        Controller.Notifier.Show(Id, title, body, actions ?? []);
    }

    /// <summary>La sessione è finita: notifica con le azioni possibili, salvo annullamento o spegnimento chiesto da qui.</summary>
    private void NotifyEnded()
    {
        // Una prima connessione non riuscita si vede già nella finestra appena aperta.
        if (!_everConnected || _endKind == SessionEndKind.Cancelled || Stopwatch.GetTimestamp() < _expectEndUntil) return;
        Notify(Strings.Format("NotifyEndedTitle", DisplayTitle), EndMessage ?? Strings.Get("SessionClosed"), EndActions());
    }

    // MARK: alimentazione

    /// <summary>Accende (o riprende) la VM spenta e si riconnette con un ticket nuovo.</summary>
    private async Task StartAndReconnectAsync()
    {
        if (_reference is not { } reference || Server is not { } server || _busy) return;
        _busy = true;
        _expectEndUntil = 0;
        ProfileInfo.Message = Strings.Get("SessionStartingVm");
        RaiseStatusChanged();
        try
        {
            var text = await Task.Run(async () =>
            {
                using var client = ServerStore.Client(server);
                var status = await client.StatusAsync(reference.Node, reference.VmId).ConfigureAwait(false);
                if (status is "paused" or "suspended") await client.PerformAsync(PowerAction.Resume, reference.Node, reference.VmId).ConfigureAwait(false);
                else if (status != "running") await client.PerformAsync(PowerAction.Start, reference.Node, reference.VmId).ConfigureAwait(false);
                await client.WaitUntilRunningAsync(reference.Node, reference.VmId).ConfigureAwait(false);
                return await client.SpiceConnectionFileAsync(reference.Node, reference.VmId).ConfigureAwait(false);
            });
            var profile = VVFileParser.Parse(text).Profile;
            Controller.Store.LearnCa(server, profile);
            _busy = false;
            _attempt = 0;
            _endKind = SessionEndKind.None;
            var old = _session;
            StartSession(profile);
            if (old is not null) _ = old.DisposeAsync().AsTask();
        }
        catch (Exception error) when (error is ProxmoxException or VVFileException or HttpRequestException or TimeoutException)
        {
            _busy = false;
            AppLog.Info($"Avvio e riconnessione non riusciti: {error.Message}");
            End(SessionState.Disconnected(new SessionFailure(AppController.ErrorText(error))), kind: SessionEndKind.VmNotRunning);
        }
    }

    /// <summary>Azione di alimentazione dal menu dell'icona, con la conferma per quelle distruttive.</summary>
    private async Task PowerAsync(PowerAction action)
    {
        if (_reference is not { } reference || Server is not { } server || _busy) return;
        if (action.IsDestructive())
        {
            WindowChrome.BringToFront(this);
            if (!await AppController.ConfirmPowerAsync(Root.XamlRoot, action, DisplayTitle)) return;
        }
        // Spegnimento chiesto da qui: la fine della sessione che segue non è una sorpresa da notificare.
        if (action is PowerAction.Shutdown or PowerAction.Stop or PowerAction.Suspend)
        {
            _expectEndUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 120;
        }
        _busy = true;
        RaiseStatusChanged();
        try
        {
            await Task.Run(async () =>
            {
                using var client = ServerStore.Client(server);
                await client.PerformAsync(action, reference.Node, reference.VmId).ConfigureAwait(false);
            });
        }
        catch (Exception error) when (error is ProxmoxException or HttpRequestException)
        {
            _expectEndUntil = 0;
            WindowChrome.BringToFront(this);
            await Dialogs.MessageAsync(Root.XamlRoot, Strings.Format("PowerFailedTitle", DisplayTitle), AppController.ErrorText(error));
        }
        finally
        {
            _busy = false;
            RaiseStatusChanged();
        }
    }

    /// <summary>Riconnessione automatica spenta: si dice almeno se la VM è ancora accesa.</summary>
    private async Task CheckVmThenEndAsync(SessionState state)
    {
        if (_reference is { } reference && Server is { } server)
        {
            try
            {
                var status = await Task.Run(async () =>
                {
                    using var client = ServerStore.Client(server);
                    return await client.StatusAsync(reference.Node, reference.VmId).ConfigureAwait(false);
                });
                if (status != "running")
                {
                    End(SessionState.Disconnected(new SessionFailure(Strings.Get("SessionVmOff"))), kind: SessionEndKind.VmNotRunning);
                    return;
                }
            }
            catch (Exception error) when (error is ProxmoxException or HttpRequestException)
            {
                AppLog.Info($"Stato della VM non disponibile: {error.Message}");
            }
        }
        End(state, kind: SessionEndKind.Dropped);
    }
}

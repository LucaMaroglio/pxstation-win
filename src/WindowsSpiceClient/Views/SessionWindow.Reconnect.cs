using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpiceKit.Proxmox;
using SpiceKit.Session;
using SpiceKit.VVFile;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>
/// Riconnessione nella stessa finestra (M6), come il client Linux e quello macOS.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Solo una connessione caduta (rete, tempo scaduto) fa partire la riconnessione automatica;
///   una sessione chiusa dal server o un ticket rifiutato no (<see cref="ReconnectPolicy"/>).</item>
/// <item>Ogni tentativo ha bisogno di un ticket nuovo: le sessioni Proxmox lo chiedono all'API (serve
///   il server configurato nelle preferenze); un file scaricato vale una volta sola. Senza ticket il
///   profilo si riusa così com'è.</item>
/// <item>Tentativi dopo 1, 2, 4, 8, 15, 30, 30, 30 secondi; una sessione rimasta su per un minuto
///   ricomincia dal primo. Con l'opzione spenta resta il pulsante «Riconnetti».</item>
/// </list>
/// </remarks>
public sealed partial class SessionWindow
{
    private readonly ReconnectPolicy _policy = new();
    private DispatcherQueueTimerHolder? _countdown;
    private CancellationTokenSource? _reconnect;
    private int _attempt;
    private long? _connectedAt;
    private DateTime _retryAt;
    private bool _cancelled;

    /// <summary>Tentativo di riconnessione in corso (0 = nessuno), per l'autoverifica.</summary>
    internal int ReconnectAttempt => _attempt;

    /// <summary>Riconnessioni riuscite in questa finestra (per l'autoverifica).</summary>
    internal int Reconnections { get; private set; }

    private static AppController Controller => ((App)Application.Current).Controller;

    private ProxmoxServer? Server => _reference is null ? null : Controller.Store.ServerForHost(_reference.ApiHost);

    /// <summary>Si può avere un ticket nuovo (API Proxmox), oppure il profilo non ne usa.</summary>
    private bool CanReconnect => _profile is not null && ((_reference is not null && Server is not null) || _profile.Password is null);

    /// <summary>Diagnostica: simula una caduta della rete (autoverifica della riconnessione).</summary>
    internal Task SimulateConnectionLossAsync() => _session?.SimulateConnectionLossAsync() ?? Task.CompletedTask;

    private void OnSessionConnected()
    {
        if (_attempt > 0)
        {
            Reconnections++;
            AppLog.Info($"Sessione riconnessa al tentativo {_attempt}");
            Notify(Strings.Get("NotifyReconnectedTitle"), Strings.Format("NotifyReconnectedText", DisplayTitle));
        }
        StopReconnecting(); // il tentativo è riuscito: niente più da annullare
        _connectedAt = Stopwatch.GetTimestamp();
        _cancelled = false;
        _endKind = SessionEndKind.None;
        _everConnected = true;
    }

    private void OnSessionEnded(SessionState state)
    {
        SessionMenuButton.IsEnabled = false;
        _pointer.Release();
        RememberDevicesForReconnect();
        var failure = state.Failure;
        var wasConnected = _connectedAt is not null;
        if (_connectedAt is { } since && Stopwatch.GetElapsedTime(since) >= _policy.StableAfter) _attempt = 0;
        _connectedAt = null;
        var reconnecting = _attempt > 0;

        if (_cancelled)
        {
            End(state, Strings.Get("SessionReconnectCancelled"), SessionEndKind.Cancelled);
            return;
        }
        if (failure is null || !ReconnectPolicy.ShouldReconnect(failure, wasConnected, reconnecting))
        {
            End(state, kind: failure is null ? SessionEndKind.None : SessionEndKind.Dropped);
            return;
        }
        if (!CanReconnect)
        {
            End(state, Strings.Get(_reference is null ? "SessionOneTimeTicket" : "SessionNoApi"),
                _reference is null ? SessionEndKind.Dropped : SessionEndKind.NoApi);
            return;
        }
        if (!Controller.Settings.ReconnectAutomatically && !reconnecting)
        {
            _ = CheckVmThenEndAsync(state); // resta il pulsante «Riconnetti»
            return;
        }
        if (_attempt == 0) AppLog.Info($"Connessione persa ({failure.Message}): riconnessione");
        ScheduleAttempt(failure.Message);
    }

    /// <summary>Fine definitiva: stato finale, eventuale spiegazione e pulsante «Riconnetti» se ha senso.</summary>
    private void End(SessionState state, string? note = null, SessionEndKind kind = SessionEndKind.Dropped)
    {
        StopReconnecting();
        _attempt = 0;
        _endKind = kind;
        _restoreIso = null; // nessuna riconnessione: niente da ricollegare
        _restoreCard = null;
        ShowState(state);
        if (note is not null) ProfileInfo.Message = string.IsNullOrEmpty(ProfileInfo.Message) ? note : $"{ProfileInfo.Message}\n{note}";
        NotifyEnded();
    }

    private void ScheduleAttempt(string lastFailure)
    {
        _attempt++;
        if (_policy.Delay(_attempt) is not { } delay)
        {
            End(SessionState.Disconnected(new SessionFailure(lastFailure)), Strings.Get("SessionGaveUp"));
            return;
        }
        StopReconnecting();
        var cancel = new CancellationTokenSource();
        _reconnect = cancel;
        _retryAt = DateTime.UtcNow + delay;
        ShowReconnecting();
        _countdown = new DispatcherQueueTimerHolder(DispatcherQueue, TimeSpan.FromSeconds(1), ShowReconnecting);
        RaiseStatusChanged();
        if (_attempt == 1) Notify(Strings.Get("NotifyLostTitle"), Strings.Format("NotifyLostText", DisplayTitle));
        _ = WaitThenAttemptAsync(delay, lastFailure, cancel.Token);
    }

    private async Task WaitThenAttemptAsync(TimeSpan delay, string lastFailure, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _countdown?.Dispose();
        _countdown = null;
        await AttemptAsync(lastFailure, cancellationToken);
    }

    /// <summary>Pulsante «Riconnetti» o tentativo programmato: ticket nuovo e nuova sessione nella finestra.</summary>
    private async Task AttemptAsync(string lastFailure, CancellationToken cancellationToken)
    {
        ShowReconnecting(now: true);
        ConnectionProfile? next = _profile?.Password is null ? _profile : null;
        if (_reference is not null && Server is { } server)
        {
            try
            {
                var reference = _reference;
                var fetched = await Task.Run(async () =>
                {
                    using var client = ServerStore.Client(server);
                    var status = await client.StatusAsync(reference.Node, reference.VmId, cancellationToken).ConfigureAwait(false);
                    if (status != "running") return null;
                    return await client.SpiceConnectionFileAsync(reference.Node, reference.VmId, cancellationToken).ConfigureAwait(false);
                }, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;
                if (fetched is null)
                {
                    End(SessionState.Disconnected(new SessionFailure(Strings.Get("SessionVmOff"))), kind: SessionEndKind.VmNotRunning);
                    return;
                }
                next = VVFileParser.Parse(fetched).Profile;
                Controller.Store.LearnCa(server, next);
            }
            catch (ProxmoxException error) when (error.Kind is ProxmoxErrorKind.Unauthorized or ProxmoxErrorKind.Forbidden
                                                     or ProxmoxErrorKind.UntrustedCertificate)
            {
                End(SessionState.Disconnected(new SessionFailure(error.Message)));
                return;
            }
            catch (Exception error) when (error is ProxmoxException or VVFileException or HttpRequestException)
            {
                // Anche l'API non risponde (rete): conta come tentativo fallito.
                AppLog.Info($"Tentativo di riconnessione {_attempt}: {error.Message}");
                ScheduleAttempt(error.Message);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (next is null || cancellationToken.IsCancellationRequested) return;
        var old = _session;
        StartSession(next);
        if (old is not null) _ = old.DisposeAsync().AsTask();
    }

    private void ShowReconnecting() => ShowReconnecting(now: false);

    private void ShowReconnecting(bool now)
    {
        ProfileInfo.Visibility = Visibility.Visible;
        ConnectingProgress.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
        ProfileInfo.Severity = InfoBarSeverity.Warning;
        ProfileInfo.Title = Strings.Get("SessionConnectionLost");
        var seconds = Math.Max(0, (int)Math.Ceiling((_retryAt - DateTime.UtcNow).TotalSeconds));
        ProfileInfo.Message = now || seconds == 0
            ? Strings.Format("SessionReconnectingNow", _attempt, _policy.MaxAttempts)
            : Strings.Format("SessionReconnectingIn", seconds, _attempt, _policy.MaxAttempts);
        ProfileInfo.ActionButton = ActionButton("SessionCancelReconnect", CancelReconnect);
    }

    private void CancelReconnect()
    {
        StopReconnecting();
        _attempt = 0;
        if (_session?.State.Kind is SessionStateKind.Connecting or SessionStateKind.Idle)
        {
            // Il tentativo in corso è questa sessione: la si chiude e la fine mostra «annullata».
            _cancelled = true;
            _ = _session.DisconnectAsync();
            return;
        }
        End(SessionState.Disconnected(new SessionFailure(Strings.Get("SessionReconnectCancelled"))), kind: SessionEndKind.Cancelled);
    }

    private void ReconnectNow()
    {
        StopReconnecting();
        _attempt = Math.Max(_attempt, 1);
        _endKind = SessionEndKind.None;
        var cancel = new CancellationTokenSource();
        _reconnect = cancel;
        RaiseStatusChanged();
        _ = AttemptAsync(ProfileInfo.Message ?? "", cancel.Token);
    }

    private void StopReconnecting()
    {
        _countdown?.Dispose();
        _countdown = null;
        _reconnect?.Cancel();
        _reconnect?.Dispose();
        _reconnect = null;
    }

    /// <summary>A sessione finita, «Riconnetti» quando si può avere un ticket nuovo.</summary>
    private void UpdateReconnectButton()
    {
        var kind = _session?.State.Kind;
        ProfileInfo.ActionButton =
            kind == SessionStateKind.Disconnected && CanReconnect && _reconnect is null ? ActionButton("SessionReconnect", ReconnectNow)
            : kind == SessionStateKind.Connecting && _attempt > 0 ? ActionButton("SessionCancelReconnect", CancelReconnect)
            : null;
    }

    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = Strings.Get(text) };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Timer ripetuto del thread dell'interfaccia che si ferma con <see cref="Dispose"/>.</summary>
    private sealed class DispatcherQueueTimerHolder : IDisposable
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

        public DispatcherQueueTimerHolder(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action tick)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = interval;
            _timer.Tick += (_, _) => tick();
            _timer.Start();
        }

        public void Dispose() => _timer.Stop();
    }
}

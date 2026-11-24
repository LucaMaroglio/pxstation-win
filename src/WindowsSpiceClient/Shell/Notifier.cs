using System.Globalization;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Shell;

/// <summary>Pulsante di una notifica: identificatore dell'azione e testo.</summary>
internal sealed record NotificationAction(string Id, string Label);

/// <summary>
/// Notifiche di Windows (M9) per le sessioni: connessione persa, riconnessa, sessione terminata con
/// le azioni possibili (per esempio «Avvia e riconnetti»), come <c>notify</c> del client Linux.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Windows App SDK (<see cref="AppNotificationManager"/>): l'app non pacchettizzata si registra
///   con nome e icona; un clic sulla notifica arriva a <see cref="Invoked"/> sul thread dell'interfaccia.</item>
/// <item>Ogni sessione ha un tag: una notifica nuova sostituisce la precedente, e quella di «sessione
///   terminata» (con pulsanti) si ritira appena la sessione riparte o si chiude, così i pulsanti
///   non sono mai superati.</item>
/// <item>L'autoverifica e le configurazioni isolate non mostrano nulla: registrano soltanto in
///   <see cref="History"/> che cosa sarebbe stato mostrato.</item>
/// <item>Nessun dato riservato nel testo: titolo della VM e messaggi di stato, mai ticket o host del proxy.</item>
/// </list>
/// </remarks>
internal sealed class Notifier
{
    private const string Group = "sessions";
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly HashSet<int> _withActions = [];
    private readonly bool _system;

    /// <summary>Clic su una notifica: sessione e azione (<see langword="null"/> = la notifica stessa).</summary>
    public event Action<int, string?>? Invoked;

    /// <summary>Notifiche inviate (titolo e testo), per l'autoverifica.</summary>
    public List<string> History { get; } = [];

    public Notifier(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        if (SelfTest.IsEnabled || AppPaths.IsIsolated) return;
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnInvoked; // prima di Register, come chiede il Windows App SDK
            manager.Register(AppPaths.AppName, new Uri(AppPaths.IconPath));
            _system = true;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            AppLog.Error("Registrazione delle notifiche non riuscita", error);
        }
    }

    public void Show(int session, string title, string body, IReadOnlyList<NotificationAction> actions)
    {
        History.Add($"{title} — {body}{(actions.Count > 0 ? $" [{string.Join(", ", actions.Select(a => a.Label))}]" : "")}");
        if (actions.Count > 0) _withActions.Add(session);
        else _withActions.Remove(session);
        if (!_system) return;
        var id = session.ToString(CultureInfo.InvariantCulture);
        var builder = new AppNotificationBuilder()
            .AddArgument("session", id)
            .AddText(title)
            .AddText(body);
        foreach (var action in actions)
        {
            builder.AddButton(new AppNotificationButton(action.Label).AddArgument("session", id).AddArgument("action", action.Id));
        }
        var notification = builder.BuildNotification();
        notification.Tag = Tag(session);
        notification.Group = Group;
        try
        {
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            AppLog.Error("Notifica non mostrata", error);
        }
    }

    /// <summary>Ritira la notifica con pulsanti di una sessione che è ripartita o è stata chiusa.</summary>
    public void Withdraw(int session)
    {
        if (!_withActions.Remove(session) || !_system) return;
        _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(Tag(session), Group).AsTask()
            .ContinueWith(t => AppLog.Error("Ritiro della notifica non riuscito", t.Exception), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>All'uscita: i clic successivi avviano di nuovo l'app invece di cercare questa istanza.</summary>
    public void Shutdown()
    {
        if (!_system) return;
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            AppLog.Error("Chiusura delle notifiche non riuscita", error);
        }
    }

    /// <summary>Disinstallazione: rimuove nome, icona e attivatore registrati per l'utente.</summary>
    public static void UnregisterAll()
    {
        try
        {
            AppNotificationManager.Default.UnregisterAll();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            AppLog.Error("Rimozione della registrazione delle notifiche non riuscita", error);
        }
    }

    private static string Tag(int session) => "s" + session.ToString(CultureInfo.InvariantCulture);

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("session", out var text)
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var session))
        {
            return;
        }
        var action = args.Arguments.TryGetValue("action", out var value) ? value : null;
        _dispatcher.TryEnqueue(() => Invoked?.Invoke(session, action));
    }
}

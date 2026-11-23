using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpiceKit.Session;
using SpiceKit.Windows.Audio;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>Audio nella finestra di sessione (M7): riproduzione automatica, microfono a richiesta.</summary>
public sealed partial class SessionWindow
{
    private SessionAudio? _audio;

    /// <summary>Statistiche dell'audio (per l'autoverifica).</summary>
    internal (long Packets, long Samples, string Output, long MicrophonePackets) AudioStatistics =>
        _audio?.Statistics ?? (0, 0, "nessuna sessione", 0);

    /// <summary>Collega l'audio alla sessione nuova; dopo una riconnessione il microfono resta come l'utente l'aveva lasciato.</summary>
    private void StartAudio(SpiceSession session)
    {
        var microphone = _audio?.MicrophoneEnabled == true;
        StopAudio();
        _audio = new SessionAudio(session) { MicrophoneEnabled = microphone };
        _audio.Changed += () => DispatcherQueue.TryEnqueue(UpdateMicrophone);
        UpdateMicrophone();
    }

    private void StopAudio()
    {
        if (_audio is null) return;
        _ = _audio.DisposeAsync().AsTask();
        _audio = null;
    }

    private void OnToggleMicrophone(object sender, RoutedEventArgs e)
    {
        if (_audio is not null) _audio.MicrophoneEnabled = MicrophoneItem.IsChecked;
        UpdateMicrophone();
    }

    /// <summary>Voce del menu, indicazione «microfono in uso» e avviso se Windows nega l'accesso.</summary>
    private void UpdateMicrophone()
    {
        if (_audio is not { } audio) return;
        MicrophoneItem.IsChecked = audio.MicrophoneEnabled;
        if (audio.MicrophoneDenied)
        {
            ShowNotice(Strings.Get("MicrophoneTitle"), InfoBarSeverity.Warning, Strings.Get("MicrophoneDenied"));
        }
        UpdatePointerMode(); // il sottotitolo mostra anche il microfono in uso
    }

    /// <summary>Sottotitolo della barra del titolo quando il microfono manda audio all'ospite.</summary>
    private string MicrophoneSubtitle => _audio is { MicrophoneEnabled: true, GuestIsRecording: true } ? Strings.Get("MicrophoneInUse") : "";

    /// <summary>Avviso nella barra sotto lo schermo (la stessa della copia di file).</summary>
    private void ShowNotice(string title, InfoBarSeverity severity, string message)
    {
        TransferInfo.Title = title;
        TransferInfo.Severity = severity;
        TransferInfo.Message = message;
        TransferProgress.Visibility = Visibility.Collapsed;
        TransferInfo.ActionButton = null;
        TransferInfo.IsOpen = true;
    }
}

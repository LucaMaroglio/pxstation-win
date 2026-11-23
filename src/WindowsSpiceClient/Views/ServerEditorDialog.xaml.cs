using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpiceKit.Proxmox;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>
/// Aggiunta e modifica di un server. Il salvataggio prova prima la connessione; un certificato
/// non attendibile si può fissare dopo che l'utente ne ha confrontato l'impronta.
/// </summary>
public sealed partial class ServerEditorDialog : ContentDialog
{
    private readonly AppController _controller;
    private readonly bool _isNew;
    private ProxmoxServer _server;
    private string? _untrusted;
    private bool _working;

    internal ServerEditorDialog(AppController controller, ProxmoxServer server, bool isNew)
    {
        _controller = controller;
        _server = server;
        _isNew = isNew;
        InitializeComponent();
        Title = Strings.Get(isNew ? "AddServerTitle" : "EditServerTitle");
        PrimaryButtonText = Strings.Get(isNew ? "Add" : "Save");
        CloseButtonText = Strings.Get("Cancel");
        NameBox.Text = server.Name;
        HostBox.Text = server.Host;
        PortBox.Value = server.Port;
        TokenBox.Text = server.TokenId;
        SecretBox.PlaceholderText = isNew ? "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" : Strings.Get("SecretKeepPlaceholder");
        Validate();
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Validate();

    private void Validate()
    {
        var secret = SecretBox.Password.Trim();
        var valid = ProxmoxServer.IsValidHost(HostBox.Text.Trim()) && ProxmoxServer.IsValidTokenId(TokenBox.Text.Trim())
                    && (secret.Length > 0 ? ProxmoxServer.IsValidSecret(secret) : !_isNew);
        IsPrimaryButtonEnabled = valid && !_working;
    }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Il dialogo si chiude solo se la prova riesce.
        args.Cancel = true;
        await TestAndSaveAsync();
    }

    private void OnTrust(object sender, RoutedEventArgs e)
    {
        if (_untrusted is null) return;
        _server = _server with { PinnedSha256 = _untrusted };
        _untrusted = null;
        TrustBar.IsOpen = false;
        _ = TestAndSaveAsync();
    }

    /// <summary>Elenca le VM con i dati inseriti; salva solo se funziona.</summary>
    private async Task TestAndSaveAsync()
    {
        if (_working) return;
        var host = HostBox.Text.Trim();
        var port = double.IsNaN(PortBox.Value) ? 8006 : (int)PortBox.Value;
        var candidate = _controller.WithKnownCa(_server with
        {
            Name = NameBox.Text.Trim() is { Length: > 0 } name ? name : host,
            Host = host,
            Port = port,
            TokenId = TokenBox.Text.Trim(),
        });
        var typedSecret = SecretBox.Password.Trim();

        _working = true;
        Validate();
        Progress.IsActive = true;
        StatusText.Text = Strings.Get("Connecting");
        try
        {
            await AppController.TestConnectionAsync(candidate, typedSecret);
            _controller.Store.Save(candidate, typedSecret.Length > 0 ? typedSecret : null);
            Hide();
        }
        catch (ProxmoxException error) when (error.Kind == ProxmoxErrorKind.UntrustedCertificate && error.Sha256 is not null)
        {
            _untrusted = error.Sha256;
            FingerprintText.Text = "SHA-256: " + ProxmoxException.FormatFingerprint(error.Sha256);
            TrustBar.IsOpen = true;
            StatusText.Text = "";
        }
        catch (Exception error) when (error is ProxmoxException or IOException or UnauthorizedAccessException
                                          or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = AppController.ErrorText(error);
        }
        finally
        {
            _working = false;
            Progress.IsActive = false;
            Validate();
        }
    }
}

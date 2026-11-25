using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>Finestre di dialogo standard (ContentDialog nello stile di Windows 11).</summary>
internal static class Dialogs
{
    private static ContentDialog Create(XamlRoot root, string title, object content) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
    };

    private static TextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    public static async Task MessageAsync(XamlRoot root, string title, string text)
    {
        var dialog = Create(root, title, Body(text));
        dialog.CloseButtonText = Strings.Get("Ok");
        dialog.DefaultButton = ContentDialogButton.Close;
        await ShowAsync(dialog);
    }

    /// <summary>Chiede conferma; il pulsante principale è <paramref name="accept"/>, l'annullamento è predefinito.</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string text, string accept)
    {
        var dialog = Create(root, title, Body(text));
        dialog.PrimaryButtonText = accept;
        dialog.CloseButtonText = Strings.Get("Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public static async Task AboutAsync(XamlRoot root)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/app-256.png")),
            Width = 96,
            Height = 96,
        });
        // Come PxStation Mac e Linux: nome in grassetto, versione, anno e autore.
        panel.Children.Add(new TextBlock
        {
            Text = AppPaths.AppName,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Format("AboutVersion", AppPaths.Version),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = AppPaths.Copyright,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var dialog = Create(root, Strings.Get("AboutTitle"), panel);
        dialog.CloseButtonText = Strings.Get("Close");
        await ShowAsync(dialog);
    }

    private static bool _open;

    /// <summary>Un solo ContentDialog alla volta: le richieste in più vengono ignorate.</summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_open) return ContentDialogResult.None;
        _open = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _open = false;
        }
    }
}

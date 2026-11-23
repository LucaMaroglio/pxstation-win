using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpiceKit.Session;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>Funzioni dell'agente nella finestra di sessione (M6): risoluzione dinamica e copia di file.</summary>
public sealed partial class SessionWindow
{
    private readonly Queue<string> _pendingFiles = new();
    private DispatcherQueueTimer? _resizeTimer;
    private (int Width, int Height) _requestedSize;
    private CancellationTokenSource? _transfer;

    /// <summary>File copiati nell'ospite in questa finestra (per l'autoverifica).</summary>
    internal int FilesTransferred { get; private set; }

    /// <summary>Misura chiesta all'ospite per l'ultima volta (per l'autoverifica).</summary>
    internal (int Width, int Height) RequestedGuestSize => _requestedSize;

    // MARK: risoluzione dinamica

    /// <summary>
    /// Con l'agente collegato l'ospite prende la misura del pannello in pixel fisici (nitido, 1:1),
    /// circa 300 ms dopo l'ultimo cambio (ridimensionamenti, schermo intero, cambio di scala).
    /// </summary>
    private void ScheduleGuestResize()
    {
        if (!_agent || _session is null) return;
        if (_resizeTimer is null)
        {
            _resizeTimer = DispatcherQueue.CreateTimer();
            _resizeTimer.IsRepeating = false;
            _resizeTimer.Interval = TimeSpan.FromMilliseconds(300);
            _resizeTimer.Tick += (_, _) => RequestGuestSize();
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void RequestGuestSize()
    {
        var scale = DisplayPanel.XamlRoot?.RasterizationScale ?? 1;
        // Larghezza pari: i driver dell'ospite la gestiscono meglio. Minimo una misura utilizzabile.
        var width = Math.Max(320, (int)Math.Round(DisplayPanel.ActualWidth * scale)) & ~1;
        var height = Math.Max(200, (int)Math.Round(DisplayPanel.ActualHeight * scale));
        if ((width, height) == _requestedSize || _session is not { } session) return;
        if (session.RequestGuestResolution(width, height))
        {
            _requestedSize = (width, height);
            AppLog.Info($"Risoluzione chiesta all'ospite: {width}x{height}");
        }
    }

    // MARK: copia di file (trascinamento)

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || _session?.IsAgentConnected != true) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Strings.Get("TransferTitle");
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        finally
        {
            deferral.Complete();
        }
        var files = items.OfType<StorageFile>().Select(f => f.Path).Where(p => p.Length > 0).ToList();
        if (files.Count < items.Count) ShowTransfer(InfoBarSeverity.Warning, Strings.Get("TransferNotAFile"), progress: null);
        await SendFilesAsync(files);
    }

    /// <summary>Copia i file nell'ospite uno dopo l'altro (anche per l'autoverifica).</summary>
    internal async Task SendFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths) _pendingFiles.Enqueue(path);
        if (_transfer is not null) return; // la coda è già in lavorazione
        using var cancel = new CancellationTokenSource();
        _transfer = cancel;
        try
        {
            while (_pendingFiles.TryDequeue(out var path) && !cancel.IsCancellationRequested)
            {
                await SendFileAsync(path, cancel.Token);
            }
        }
        finally
        {
            _transfer = null;
            _pendingFiles.Clear();
        }
    }

    private async Task SendFileAsync(string path, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(path);
        if (_session is not { IsAgentConnected: true } session)
        {
            ShowTransfer(InfoBarSeverity.Warning, Strings.Get("TransferNoAgent"), progress: null);
            return;
        }
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowTransfer(InfoBarSeverity.Error, Strings.Format("TransferFailed", name, error.Message), progress: null);
            return;
        }
        ShowTransfer(InfoBarSeverity.Informational, Progress(name, 0, size), progress: 0);
        var progress = new Progress<long>(sent =>
        {
            TransferProgress.Value = size == 0 ? 1 : (double)sent / size;
            TransferInfo.Message = Progress(name, sent, size);
        });
        try
        {
            await session.SendFileAsync(path, progress, cancellationToken);
            FilesTransferred++;
            AppLog.Info($"File copiato nell'ospite: {size} byte");
            ShowTransfer(InfoBarSeverity.Success, Strings.Format("TransferDone", name), progress: null);
        }
        catch (FileTransferException error)
        {
            ShowTransfer(InfoBarSeverity.Error, Strings.Format("TransferFailed", name, error.Message), progress: null);
        }
        catch (OperationCanceledException)
        {
            ShowTransfer(InfoBarSeverity.Warning, Strings.Format("TransferFailed", name, FileTransferException.From(SpiceKit.Agent.FileTransferResult.Cancelled).Message), progress: null);
        }
    }

    private static string Progress(string name, long sent, long size) =>
        Strings.Format("TransferProgressText", name, FormatSize(sent), FormatSize(size));

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };

    private void ShowTransfer(InfoBarSeverity severity, string message, double? progress)
    {
        TransferInfo.Title = Strings.Get("TransferTitle");
        TransferInfo.Severity = severity;
        TransferInfo.Message = message;
        TransferProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (progress is { } value) TransferProgress.Value = value;
        TransferInfo.ActionButton = progress is null ? null : CancelTransferButton();
        TransferInfo.IsOpen = true;
    }

    private Button CancelTransferButton()
    {
        var button = new Button { Content = Strings.Get("TransferCancel") };
        button.Click += (_, _) => CancelTransfers();
        return button;
    }

    private void CancelTransfers()
    {
        _pendingFiles.Clear();
        _transfer?.Cancel();
    }
}

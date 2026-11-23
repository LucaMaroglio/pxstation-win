using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SpiceKit.Proxmox;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Views;

/// <summary>Base minima per i modelli con notifiche di modifica.</summary>
public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Una VM nella finestra Macchine virtuali.</summary>
public sealed class VmRowModel : ObservableModel
{
    private ProxmoxVM _vm;
    private bool _busy;

    internal VmRowModel(ProxmoxServer server, ProxmoxVM vm)
    {
        Server = server;
        _vm = vm;
    }

    internal ProxmoxServer Server { get; }

    internal ProxmoxVM Vm
    {
        get => _vm;
        set
        {
            if (!Set(ref _vm, value)) return;
            Notify(nameof(Name));
            Notify(nameof(Caption));
            Notify(nameof(StatusLabel));
            Notify(nameof(StatusBrush));
            Notify(nameof(ButtonLabel));
            Notify(nameof(ButtonToolTip));
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value)) return;
            Notify(nameof(IsIdle));
            Notify(nameof(BusyVisibility));
        }
    }

    public string Name => _vm.Name;
    public string Caption => string.Create(CultureInfo.CurrentCulture, $"{_vm.VmId} · {_vm.Node} · {_vm.StatusLabel}");
    public string StatusLabel => _vm.StatusLabel;
    public string ButtonLabel => AppController.RowAction(_vm).Label;
    public string ButtonToolTip => Strings.Format("ConnectToolTip", _vm.Name);
    public bool IsIdle => !_busy;
    public Visibility BusyVisibility => _busy ? Visibility.Visible : Visibility.Collapsed;

    public Brush StatusBrush => (Brush)Application.Current.Resources[_vm.Status switch
    {
        "running" => "SystemFillColorSuccessBrush",
        "paused" or "suspended" => "SystemFillColorCautionBrush",
        _ => "SystemFillColorNeutralBrush",
    }];
}

/// <summary>Un server Proxmox con le sue VM.</summary>
public sealed class ServerSectionModel : ObservableModel
{
    private string? _message;
    private bool _isError;

    internal ServerSectionModel(ProxmoxServer server)
    {
        Server = server;
        _message = Strings.Get("Loading");
    }

    internal ProxmoxServer Server { get; }
    public string Title => Server.Name;
    public ObservableCollection<VmRowModel> Rows { get; } = [];

    public string? Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value)) Notify(nameof(MessageVisibility));
        }
    }

    public bool IsError
    {
        get => _isError;
        private set
        {
            if (Set(ref _isError, value))
            {
                Notify(nameof(ErrorVisibility));
            }
        }
    }

    public Visibility MessageVisibility => _message is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ErrorVisibility => _isError ? Visibility.Visible : Visibility.Collapsed;

    internal void SetError(string text)
    {
        IsError = true;
        Message = text;
    }

    internal void SetVms(IReadOnlyList<ProxmoxVM> vms)
    {
        IsError = false;
        var keys = vms.Select(v => v.Key).ToList();
        if (!keys.SequenceEqual(Rows.Select(r => r.Vm.Key)))
        {
            var busy = Rows.Where(r => r.Busy).Select(r => r.Vm.Key).ToHashSet();
            Rows.Clear();
            foreach (var vm in vms) Rows.Add(new VmRowModel(Server, vm) { Busy = busy.Contains(vm.Key) });
        }
        else
        {
            for (var i = 0; i < vms.Count; i++) Rows[i].Vm = vms[i];
        }
        Message = vms.Count == 0 ? Strings.Get("EmptyTokenHint") : null;
    }
}

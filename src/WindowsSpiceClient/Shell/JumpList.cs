using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using WindowsSpiceClient.Diagnostics;
using WindowsSpiceClient.Services;

namespace WindowsSpiceClient.Shell;

/// <summary>
/// Attività della jump list (clic destro sull'icona nella barra delle applicazioni o nel menu Start):
/// «Macchine virtuali», «Apri file di connessione…», «Preferenze».
/// </summary>
/// <remarks>
/// L'app non è pacchettizzata, quindi niente <c>Windows.UI.StartScreen.JumpList</c>: si usa
/// <c>ICustomDestinationList</c> della shell con l'AppUserModelID dell'app (lo stesso del collegamento
/// creato dall'installer). Ogni attività riavvia l'eseguibile con un comando che l'istanza unica
/// passa a quella già aperta (<see cref="CommandLine"/>).
/// </remarks>
internal static partial class JumpList
{
    private static readonly Guid DestinationListClass = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ObjectCollectionClass = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Attività pubblicate (comando e titolo), per l'autoverifica.</summary>
    public static IReadOnlyList<(string Command, string Title)> Tasks =>
    [
        (CommandLine.VmList, Strings.Get("JumpVmList")),
        (CommandLine.OpenFile, Strings.Get("JumpOpenFile")),
        (CommandLine.Preferences, Strings.Get("JumpPreferences")),
    ];

    /// <summary>Pubblica le attività; un errore della shell non ferma l'app.</summary>
    public static void Publish()
    {
        try
        {
            var list = Create<ICustomDestinationList>(DestinationListClass);
            list.SetAppID(AppPaths.AppId);
            var arrayId = typeof(IObjectArray).GUID;
            list.BeginList(out _, in arrayId, out var removed);
            if (removed != 0) Marshal.Release(removed);
            var collection = Create<IObjectCollection>(ObjectCollectionClass);
            foreach (var (command, title) in Tasks) collection.AddObject(Link(command, title));
            list.AddUserTasks((IObjectArray)collection);
            list.CommitList();
            AppLog.Info($"Jump list: {Tasks.Count} attività");
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            AppLog.Error("Jump list non pubblicata", error);
        }
    }

    /// <summary>Disinstallazione: toglie le attività dalla barra delle applicazioni e dal menu Start.</summary>
    public static void Delete()
    {
        try
        {
            Create<ICustomDestinationList>(DestinationListClass).DeleteList(AppPaths.AppId);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            AppLog.Error("Rimozione della jump list non riuscita", error);
        }
    }

    private static IShellLinkW Link(string command, string title)
    {
        var link = Create<IShellLinkW>(ShellLinkClass);
        link.SetPath(AppPaths.ExecutablePath);
        link.SetArguments(command);
        link.SetIconLocation(AppPaths.ExecutablePath, 0);
        link.SetDescription(title);
        // Il titolo mostrato è PKEY_Title nelle proprietà del collegamento.
        var store = (IPropertyStore)link;
        var key = new PropertyKey { FormatId = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), PropertyId = 2 };
        var value = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(title) };
        try
        {
            store.SetValue(in key, in value);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Pointer);
        }
        return link;
    }

    private static T Create<T>(Guid classId)
    {
        var iid = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(in classId, 0, 1 /* CLSCTX_INPROC_SERVER */, in iid, out var pointer));
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid iid, out nint instance);

    // Interfacce della shell (shobjidl_core.h, propsys.h): solo i metodi usati hanno firme complete,
    // gli altri tengono il loro posto nella vtable.

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary><c>PROPVARIANT</c> a 64 bit: tipo, tre campi riservati, poi il valore (24 byte).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    [GeneratedComInterface]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    internal partial interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        void BeginList(out uint minSlots, in Guid iid, out nint removed);

        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, nint items);

        void AppendKnownCategory(int category);

        void AddUserTasks(IObjectArray tasks);

        void CommitList();

        void GetRemovedDestinations(in Guid iid, out nint removed);

        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? appId);

        void AbortList();
    }

    [GeneratedComInterface]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    internal partial interface IObjectArray
    {
        uint GetCount();

        void GetAt(uint index, in Guid iid, out nint item);
    }

    [GeneratedComInterface]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    internal partial interface IObjectCollection : IObjectArray
    {
        /// <summary>Accetta qualunque <c>IUnknown</c>: qui sempre un collegamento.</summary>
        void AddObject(IShellLinkW item);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(nint file, int length, nint data, uint flags);

        void GetIDList(out nint list);

        void SetIDList(nint list);

        void GetDescription(nint name, int length);

        void SetDescription(string name);

        void GetWorkingDirectory(nint directory, int length);

        void SetWorkingDirectory(string directory);

        void GetArguments(nint arguments, int length);

        void SetArguments(string arguments);

        void GetHotkey(out ushort hotkey);

        void SetHotkey(ushort hotkey);

        void GetShowCmd(out int show);

        void SetShowCmd(int show);

        void GetIconLocation(nint path, int length, out int index);

        void SetIconLocation(string path, int index);

        void SetRelativePath(string path, uint reserved);

        void Resolve(nint window, uint flags);

        void SetPath(string file);
    }

    [GeneratedComInterface]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        uint GetCount();

        void GetAt(uint index, out PropertyKey key);

        void GetValue(in PropertyKey key, out PropVariant value);

        void SetValue(in PropertyKey key, in PropVariant value);

        void Commit();
    }
}

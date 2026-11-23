using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsSpiceClient.Services;

/// <summary>
/// Segreti dei token API Proxmox in Gestione credenziali di Windows (credenziali generiche,
/// protette da DPAPI per l'utente corrente). Non vengono scritti altrove né nei log.
/// </summary>
internal static partial class CredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2; // solo questo PC, per questo utente
    private const int ErrorNotFound = 1168;

    private static string Target(string serverId) =>
        $"{AppPaths.AppId}{(AppPaths.IsIsolated ? ".test" : "")}/proxmox-token/{serverId}";

    public static void Save(string serverId, string secret)
    {
        var blob = Encoding.UTF8.GetBytes(secret);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(serverId),
                CredentialBlobSize = blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CredPersistLocalMachine,
                UserName = "PVEAPIToken",
                Comment = $"{AppPaths.AppName}: segreto del token API Proxmox",
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            Array.Clear(blob);
            handle.Free();
        }
    }

    public static string? Lookup(string serverId)
    {
        if (!CredRead(Target(serverId), CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            var secret = Encoding.UTF8.GetString(blob);
            Array.Clear(blob);
            return secret;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Delete(string serverId)
    {
        if (!CredDelete(Target(serverId), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorNotFound) throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CredFree(IntPtr buffer);
}

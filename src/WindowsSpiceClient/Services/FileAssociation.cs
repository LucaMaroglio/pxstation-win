using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsSpiceClient.Services;

/// <summary>
/// Associazione dei file <c>.vv</c> per l'utente corrente (senza permessi di amministratore).
/// </summary>
/// <remarks>
/// Windows protegge la scelta del programma predefinito (UserChoice): un programma può
/// registrarsi come candidato, ma è l'utente a sceglierlo nella finestra "Apri con".
/// L'installer (installer\WindowsSpiceClient.iss) fa la stessa registrazione.
/// </remarks>
internal static partial class FileAssociation
{
    public const string Extension = ".vv";
    private const string ProgId = "WindowsSpiceClient.vv";

    /// <summary>Percorso del programma che oggi apre i <c>.vv</c>, se c'è.</summary>
    public static string? CurrentHandler()
    {
        uint size = 1024;
        var buffer = new char[size];
        var result = AssocQueryString(AssocFNone, AssocStrExecutable, Extension, "open", buffer, ref size);
        return result == 0 && size > 1 ? new string(buffer, 0, (int)size - 1) : null;
    }

    public static bool IsDefault() =>
        CurrentHandler() is { } handler && string.Equals(Path.GetFullPath(handler), AppPaths.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nome leggibile del programma predefinito attuale.</summary>
    public static string? CurrentHandlerName()
    {
        uint size = 512;
        var buffer = new char[size];
        var result = AssocQueryString(AssocFNone, AssocStrFriendlyAppName, Extension, "open", buffer, ref size);
        return result == 0 && size > 1 ? new string(buffer, 0, (int)size - 1) : CurrentHandler();
    }

    /// <summary>Registra il programma per i <c>.vv</c> in HKCU e apre la scelta del programma predefinito.</summary>
    public static void Register(IntPtr owner)
    {
        using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
        {
            using (var prog = classes.CreateSubKey(ProgId))
            {
                prog.SetValue(null, "File di connessione SPICE (virt-viewer)");
                using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{AppPaths.ExecutablePath}\",0");
                using var command = prog.CreateSubKey(@"shell\open\command");
                command.SetValue(null, $"\"{AppPaths.ExecutablePath}\" \"%1\"");
            }
            using (var extension = classes.CreateSubKey(Extension))
            {
                extension.SetValue("Content Type", "application/x-virt-viewer");
                using var openWith = extension.CreateSubKey("OpenWithProgids");
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            using (var mime = classes.CreateSubKey(@"MIME\Database\Content Type\application/x-virt-viewer"))
            {
                mime.SetValue("Extension", Extension);
            }
        }
        SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);

        if (IsDefault()) return;
        // La scelta resta all'utente: si apre la finestra "Apri con" che imposta il predefinito.
        var info = new OpenAsInfo
        {
            File = "connessione" + Extension,
            Class = null,
            Flags = OaifAllowRegistration | OaifRegisterExt | OaifForceRegistration,
        };
        _ = SHOpenWithDialog(owner, ref info);
        SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private const int AssocFNone = 0;
    private const int AssocStrExecutable = 2;
    private const int AssocStrFriendlyAppName = 4;
    private const int ShcneAssocChanged = 0x08000000;
    private const int OaifAllowRegistration = 0x1;
    private const int OaifRegisterExt = 0x2;
    private const int OaifForceRegistration = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        public string File;
        public string? Class;
        public int Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHOpenWithDialog(IntPtr parent, ref OpenAsInfo info);

    [LibraryImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AssocQueryString(int flags, int str, string assoc, string? extra, [Out] char[] output, ref uint size);

    [LibraryImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}

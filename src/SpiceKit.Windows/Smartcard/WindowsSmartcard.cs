using System.Runtime.InteropServices;
using System.Text;
using SpiceKit.Channels;

namespace SpiceKit.Windows.Smartcard;

/// <summary>Un lettore di smartcard di Windows (anche le YubiKey compaiono qui per la parte PIV/CCID).</summary>
public sealed record SmartcardReaderInfo(string Name, bool HasCard);

/// <summary>Errore di WinSCard (servizio smartcard spento, lettore scollegato, carta rifiutata…).</summary>
public sealed class SmartcardIoException(string message, int code) : IOException(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// La carta di un lettore, usata in modo esclusivo mentre è condivisa con l'ospite (porting di
/// <c>MacSmartcard</c> con WinSCard al posto di CryptoTokenKit).
/// </summary>
/// <remarks>
/// La sessione esclusiva resta aperta per tutto il tempo della condivisione, così le sequenze di APDU
/// dell'ospite (selezione dell'applet → PIN → firma) non si mescolano con i comandi di Windows. Alla
/// fine la carta si resetta (<c>SCARD_RESET_CARD</c>): l'autenticazione fatta nell'ospite non resta valida.
/// </remarks>
public sealed partial class WindowsSmartcard(string readerName) : ISmartcard
{
    private readonly Lock _lock = new();
    private nint _context;
    private nint _card;
    private uint _protocol;

    public string DisplayName { get; } = readerName;

    /// <summary>Lettori collegati, in ordine di nome; vuoto se il servizio smartcard non è attivo.</summary>
    public static IReadOnlyList<SmartcardReaderInfo> Readers()
    {
        if (EstablishContext(0, 0, 0, out var context) != 0) return [];
        try
        {
            uint length = 0;
            if (ListReaders(context, null, null, ref length) != 0 || length == 0) return [];
            var buffer = new char[length];
            if (ListReaders(context, null, buffer, ref length) != 0) return [];
            var names = new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            return [.. names.Order(StringComparer.CurrentCulture).Select(name => new SmartcardReaderInfo(name, HasCard(context, name)))];
        }
        finally
        {
            _ = ReleaseContext(context); // in chiusura: un errore non cambia nulla
        }
    }

    private static unsafe bool HasCard(nint context, string reader)
    {
        fixed (char* name = reader)
        {
            var state = new ReaderState { Reader = (nint)name };
            return GetStatusChange(context, 0, ref state, 1) == 0 && (state.EventState & 0x20 /* SCARD_STATE_PRESENT */) != 0;
        }
    }

    public Task<byte[]> ConnectAsync() => Task.Run(() =>
    {
        lock (_lock)
        {
            Disconnect();
            Check(EstablishContext(0, 0, 0, out _context), "contesto");
            Check(Connect(_context, DisplayName, 1 /* SCARD_SHARE_EXCLUSIVE */, 3 /* T0 | T1 */, out _card, out _protocol), "connessione");
            return Atr();
        }
    });

    public Task<byte[]> TransmitAsync(byte[] apdu) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(apdu);
        lock (_lock)
        {
            if (_card == 0) throw new SmartcardIoException("La carta non è collegata.", 0);
            var send = new IoRequest { Protocol = _protocol, PciLength = (uint)Marshal.SizeOf<IoRequest>() };
            var receive = new byte[65538];
            var length = (uint)receive.Length;
            Check(Transmit(_card, send, apdu, (uint)apdu.Length, 0, receive, ref length), "trasmissione");
            return receive[..(int)length];
        }
    });

    public Task DisconnectAsync()
    {
        lock (_lock) Disconnect();
        return Task.CompletedTask;
    }

    private unsafe byte[] Atr()
    {
        var atr = new byte[36];
        uint atrLength = (uint)atr.Length, readerLength = 0;
        fixed (byte* buffer = atr)
        {
            Check(Status(_card, 0, ref readerLength, out _, out _, buffer, ref atrLength), "stato");
        }
        return atr[..(int)Math.Min(atrLength, (uint)atr.Length)];
    }

    private void Disconnect()
    {
        if (_card != 0) _ = CardDisconnect(_card, 1 /* SCARD_RESET_CARD */); // in chiusura: un errore non cambia nulla
        if (_context != 0) _ = ReleaseContext(_context);
        _card = 0;
        _context = 0;
    }

    private static void Check(int result, string what)
    {
        if (result == 0) return;
        var message = unchecked((uint)result) switch
        {
            0x8010001D => "Il servizio Smart Card di Windows non è attivo.",
            0x8010000C or 0x80100069 => "Nel lettore non c'è una carta.",
            0x8010000B => "La carta è usata in modo esclusivo da un'altra applicazione: chiudila e riprova.",
            0x80100009 or 0x80100017 => "Il lettore di smartcard non è più collegato.",
            0x80100068 => "La carta è stata tolta o resettata.",
            _ => $"Errore della smartcard ({what}, 0x{result:X8}).",
        };
        throw new SmartcardIoException(message, result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ReaderState
    {
        public nint Reader;
        public nint UserData;
        public uint CurrentState;
        public uint EventState;
        public uint AtrLength;
        public fixed byte Atr[36];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoRequest
    {
        public uint Protocol;
        public uint PciLength;
    }

    [LibraryImport("winscard.dll", EntryPoint = "SCardEstablishContext")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int EstablishContext(uint scope, nint reserved1, nint reserved2, out nint context);

    [LibraryImport("winscard.dll", EntryPoint = "SCardReleaseContext")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ReleaseContext(nint context);

    [LibraryImport("winscard.dll", EntryPoint = "SCardListReadersW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ListReaders(nint context, string? groups, [Out] char[]? readers, ref uint length);

    [LibraryImport("winscard.dll", EntryPoint = "SCardGetStatusChangeW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetStatusChange(nint context, uint timeout, ref ReaderState states, uint count);

    [LibraryImport("winscard.dll", EntryPoint = "SCardConnectW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int Connect(nint context, string reader, uint shareMode, uint protocols, out nint card, out uint activeProtocol);

    [LibraryImport("winscard.dll", EntryPoint = "SCardStatusW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int Status(nint card, nint readerNames, ref uint readerLength, out uint state, out uint protocol,
        byte* atr, ref uint atrLength);

    [LibraryImport("winscard.dll", EntryPoint = "SCardTransmit")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int Transmit(nint card, in IoRequest sendPci, byte[] send, uint sendLength, nint receivePci,
        [Out] byte[] receive, ref uint receiveLength);

    [LibraryImport("winscard.dll", EntryPoint = "SCardDisconnect")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CardDisconnect(nint card, uint disposition);
}

/// <summary>Testo per l'utente di un errore smartcard (per l'interfaccia).</summary>
public static class SmartcardErrors
{
    public static string Describe(Exception error) => error switch
    {
        SmartcardIoException or SmartcardException => error.Message,
        _ => new StringBuilder("Errore della smartcard: ").Append(error.Message).ToString(),
    };
}

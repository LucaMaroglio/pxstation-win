// Canale smartcard (spice.proto, channel SmartcardChannel). Nelle due direzioni ogni messaggio
// SPICE di tipo 101 (SPICE_MSG_SMARTCARD_DATA / SPICE_MSGC_SMARTCARD_DATA) porta un messaggio VSC:
// intestazione di 12 byte (tipo, reader_id, lunghezza: uint32) seguita dai dati.
//
// Ordine dei byte: sul filo SPICE l'intestazione è little-endian come ogni altro campo (spice-server
// converte in ordine di rete solo verso ccid-card-passthru di QEMU). Verificato dal client macOS su
// QEMU 8.2 / spice-server 0.15: un'intestazione big-endian fa chiudere il canale al server.

namespace SpiceKit.Wire;

/// <summary>Tipi di messaggio VSC (protocollo versione 2).</summary>
public enum VscMessageType : uint
{
    Init = 1,
    Error = 2,
    ReaderAdd = 3,
    ReaderRemove = 4,
    Atr = 5,
    CardRemove = 6,
    Apdu = 7,
    Flush = 8,
    FlushComplete = 9,
}

/// <summary>Codici di <c>VSC_Error</c>; <see cref="Success"/> fa anche da conferma di <c>READER_ADD</c>.</summary>
public enum VscErrorCode : uint
{
    Success = 0,
    GeneralError = 1,
    CannotAddMoreReaders = 2,
    CardAlreadyInserted = 3,
}

/// <summary>Un messaggio VSC.</summary>
public sealed record VscMessage(uint RawType, uint ReaderId, byte[] Data)
{
    /// <summary><c>READER_ADD</c> si manda con questo id; il server risponde con quello vero.</summary>
    public const uint UndefinedReaderId = 0xFFFF_FFFF;

    /// <summary>Dati più lunghi accettati dal server (gli APDU estesi sono sotto i 64 KiB).</summary>
    public const int MaxPayload = 128 * 1024;

    /// <summary>Tipo del messaggio SPICE che porta i dati VSC.</summary>
    public const ushort ChannelMessageType = 101;

    private const int HeaderSize = 12;

    public VscMessage(VscMessageType type, uint readerId, byte[]? data = null) : this((uint)type, readerId, data ?? [])
    {
    }

    public VscMessageType? Type => RawType is >= 1 and <= 9 ? (VscMessageType)RawType : null;

    /// <summary>Codice di <c>VSC_Error</c>, se è un messaggio di errore.</summary>
    public uint? ErrorCode => Type == VscMessageType.Error && Data.Length >= 4 ? new ByteReader(Data).ReadU32() : null;

    public static VscMessage Decode(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        var type = r.ReadU32();
        var reader = r.ReadU32();
        var length = r.ReadU32();
        if (length > MaxPayload) throw WireException.LimitExceeded($"smartcard: {length} byte");
        return new VscMessage(type, reader, r.ReadBytes((int)length).ToArray());
    }

    public static VscMessage Failure(VscErrorCode code, uint readerId)
    {
        var w = new ByteWriter(4);
        w.WriteU32((uint)code);
        return new VscMessage(VscMessageType.Error, readerId, w.ToArray());
    }

    /// <summary>Messaggio SPICE dal client al server.</summary>
    public SpiceMessage Encode()
    {
        var w = new ByteWriter(HeaderSize + Data.Length);
        w.WriteU32(RawType);
        w.WriteU32(ReaderId);
        w.WriteU32((uint)Data.Length);
        w.WriteBytes(Data);
        return new SpiceMessage(ChannelMessageType, w.ToArray());
    }
}

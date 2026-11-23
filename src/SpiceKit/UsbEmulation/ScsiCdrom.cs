using System.Text;

namespace SpiceKit.UsbEmulation;

/// <summary>Immagine in sola lettura ad accesso casuale (un file <c>.iso</c>, o la memoria nei test).</summary>
public interface IBlockSource
{
    long Size { get; }

    /// <summary>Legge <paramref name="destination"/>.Length byte da <paramref name="offset"/>; restituisce quanti ne ha letti.</summary>
    int Read(long offset, Span<byte> destination);
}

/// <summary>Un file immagine, letto a richiesta (mai caricato tutto in memoria).</summary>
public sealed class FileBlockSource : IBlockSource, IDisposable
{
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle;

    public FileBlockSource(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        Size = RandomAccess.GetLength(_handle);
    }

    public long Size { get; }

    public int Read(long offset, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(_handle, destination[total..], offset + total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Immagine in memoria (test).</summary>
public sealed class MemoryBlockSource(byte[] bytes) : IBlockSource
{
    public long Size => bytes.Length;

    public int Read(long offset, Span<byte> destination)
    {
        if (offset >= bytes.Length) return 0;
        var count = (int)Math.Min(destination.Length, bytes.Length - offset);
        bytes.AsSpan((int)offset, count).CopyTo(destination);
        return count;
    }
}

/// <summary>Dati di una risposta SCSI: byte fissi o un intervallo di blocchi dell'immagine.</summary>
internal sealed class ScsiResponse
{
    public byte Status { get; init; }

    public byte[] Bytes { get; init; } = [];

    /// <summary>Primo blocco e numero di blocchi, per le letture.</summary>
    public (long FirstLba, int Count)? Blocks { get; init; }

    public bool Ejected { get; init; }

    public int Length => Blocks is { } blocks ? blocks.Count * ScsiCdrom.BlockSize : Bytes.Length;

    /// <exception cref="IOException">Lettura corta dell'immagine.</exception>
    public byte[] Read(int offset, int count, IBlockSource source)
    {
        if (count <= 0) return [];
        if (Blocks is not { } blocks) return Bytes.AsSpan(offset, Math.Min(count, Bytes.Length - offset)).ToArray();
        var data = new byte[count];
        var read = source.Read((blocks.FirstLba * ScsiCdrom.BlockSize) + offset, data);
        if (read != count) throw new IOException("lettura corta dell'immagine");
        return data;
    }
}

/// <summary>Dati di senso SCSI (chiave, ASC, ASCQ).</summary>
internal readonly record struct Sense(byte Key, byte Asc, byte Ascq)
{
    public static readonly Sense None = new(0, 0, 0);
    public static readonly Sense InvalidCommand = new(0x05, 0x20, 0x00);
    public static readonly Sense InvalidField = new(0x05, 0x24, 0x00);
    public static readonly Sense LbaOutOfRange = new(0x05, 0x21, 0x00);
    public static readonly Sense MediumNotPresent = new(0x02, 0x3A, 0x00);
    public static readonly Sense MediumChanged = new(0x06, 0x28, 0x00);
    public static readonly Sense WriteProtected = new(0x07, 0x27, 0x00);
    public static readonly Sense UnrecoveredReadError = new(0x03, 0x11, 0x00);
}

/// <summary>
/// Sottoinsieme dei comandi SCSI Multimedia (MMC) per un CD/DVD-ROM in sola lettura (porting di
/// <c>SCSICDROM.swift</c>, scritto dalle specifiche SPC/MMC).
/// </summary>
internal sealed class ScsiCdrom(long blockCount)
{
    public const int BlockSize = 2048;
    public const byte Good = 0;

    /// <summary>Stato "comando fallito" del CSW.</summary>
    public const byte CheckCondition = 1;

    private static readonly byte[] Serial = Encoding.ASCII.GetBytes("5350494345303031");
    private static readonly HashSet<byte> NeedsMedium = [0x25, 0x28, 0xA8, 0x43, 0x51, 0x52];

    private Sense _sense = Sense.None;

    /// <summary>GET EVENT STATUS segnala "nuovo supporto" una volta dopo l'inserimento.</summary>
    private bool _newMediaPending = true;

    /// <summary>Il primo TEST UNIT READY risponde UNIT ATTENTION (supporto cambiato), come un lettore vero.</summary>
    private bool _unitAttentionPending = true;

    public long BlockCount { get; } = blockCount;

    public bool MediumPresent { get; private set; } = true;

    /// <summary>Profilo DVD per immagini più grandi di un CD.</summary>
    public bool IsDvd => BlockCount > 400_000;

    public ScsiResponse Fail(Sense sense)
    {
        _sense = sense;
        return new ScsiResponse { Status = CheckCondition };
    }

    private static ScsiResponse Ok(byte[]? bytes = null, int? allocation = null) => new()
    {
        Status = Good,
        Bytes = bytes is null ? [] : allocation is { } limit && limit < bytes.Length ? bytes[..limit] : bytes,
    };

    public ScsiResponse Execute(ReadOnlySpan<byte> cdb)
    {
        if (cdb.IsEmpty) return Fail(Sense.InvalidCommand);
        var command = cdb.ToArray();
        int Byte(int index) => index < command.Length ? command[index] : 0;
        int Be16(int index) => (Byte(index) << 8) | Byte(index + 1);
        long Be32(int index) => ((long)Be16(index) << 16) | (long)Be16(index + 2);

        var opcode = command[0];
        if (opcode != 0x03) _sense = Sense.None; // REQUEST SENSE riporta il senso del comando precedente
        if (NeedsMedium.Contains(opcode) && !MediumPresent) return Fail(Sense.MediumNotPresent);

        switch (opcode)
        {
            case 0x00: // TEST UNIT READY
                if (!MediumPresent) return Fail(Sense.MediumNotPresent);
                if (_unitAttentionPending)
                {
                    _unitAttentionPending = false;
                    return Fail(Sense.MediumChanged);
                }
                return Ok();
            case 0x03: // REQUEST SENSE (formato fisso)
                var sense = new byte[18];
                sense[0] = 0x70;
                sense[2] = _sense.Key;
                sense[7] = 10;
                sense[12] = _sense.Asc;
                sense[13] = _sense.Ascq;
                _sense = Sense.None;
                return Ok(sense, Byte(4));
            case 0x12: // INQUIRY
                return Inquiry((Byte(1) & 1) != 0, Byte(2), Be16(3));
            case 0x1A or 0x5A: // MODE SENSE (6) / (10)
                return ModeSense(opcode == 0x5A, Byte(2) & 0x3F, opcode == 0x5A ? Be16(7) : Byte(4));
            case 0x1B: // START STOP UNIT
                var loadEject = (Byte(4) & 0x02) != 0;
                var start = (Byte(4) & 0x01) != 0;
                if (loadEject && !start)
                {
                    MediumPresent = false;
                    return new ScsiResponse { Status = Good, Ejected = true };
                }
                return Ok();
            case 0x1E or 0x35 or 0xBB or 0x2B: // PREVENT/ALLOW, SYNCHRONIZE CACHE, SET CD SPEED, SEEK
                return Ok();
            case 0x25: // READ CAPACITY (10)
                return Ok([.. Be32Bytes(BlockCount > 0 ? BlockCount - 1 : 0), .. Be32Bytes(BlockSize)]);
            case 0x28 or 0xA8: // READ (10) / READ (12)
                var lba = Be32(2);
                var count = opcode == 0x28 ? Be16(7) : Be32(6);
                if (lba + count > BlockCount || count > 65_535) return Fail(Sense.LbaOutOfRange);
                return new ScsiResponse { Status = Good, Blocks = (lba, (int)count) };
            case 0x43: // READ TOC/PMA/ATIP
                return ReadToc((Byte(1) & 0x02) != 0, Byte(2) & 0x0F, Be16(7));
            case 0x46: // GET CONFIGURATION
                return GetConfiguration(Be16(7));
            case 0x4A: // GET EVENT STATUS NOTIFICATION (solo modo interrogato)
                return (Byte(1) & 0x01) == 0 ? Fail(Sense.InvalidField) : EventStatus(Byte(4), Be16(7));
            case 0x51: // READ DISC INFORMATION
                var disc = new byte[34];
                disc[1] = 0x20;
                disc[2] = 0x0E; // disco completo, ultima sessione completa
                disc[3] = 1;
                disc[4] = 1;
                disc[5] = 1;
                disc[6] = 1;
                disc[7] = 0x20; // uso senza restrizioni
                return Ok(disc, Be16(7));
            case 0x52: // READ TRACK INFORMATION
                var track = new byte[36];
                track[1] = 0x22;
                track[2] = 1;
                track[3] = 1;
                track[5] = 0x04; // traccia dati
                track[6] = 0x01; // modo 1
                Be32Bytes(BlockCount).CopyTo(track, 24);
                return Ok(track, Be16(7));
            case 0xBD: // MECHANISM STATUS
                return Ok(new byte[8], Be16(8));
            default:
                return Fail(Sense.InvalidCommand);
        }
    }

    private ScsiResponse Inquiry(bool evpd, int page, int allocation)
    {
        if (evpd)
        {
            return page switch
            {
                0x00 => Ok([0x05, 0x00, 0x00, 0x03, 0x00, 0x80, 0x83], allocation),
                0x80 => Ok([0x05, 0x80, 0x00, (byte)Serial.Length, .. Serial], allocation),
                0x83 => Designator(allocation),
                _ => Fail(Sense.InvalidField),
            };
        }
        var data = Enumerable.Repeat((byte)0x20, 36).ToArray();
        data[0] = 0x05; // dispositivo CD/DVD
        data[1] = 0x80; // rimovibile
        data[2] = 0x05; // SPC-3
        data[3] = 0x02;
        data[4] = 31;
        data[5] = data[6] = data[7] = 0;
        Encoding.ASCII.GetBytes("SPICE   ").CopyTo(data, 8);
        Encoding.ASCII.GetBytes("Virtual CD-ROM  ").CopyTo(data, 16);
        Encoding.ASCII.GetBytes("1.0 ").CopyTo(data, 32);
        return Ok(data, allocation);
    }

    private static ScsiResponse Designator(int allocation)
    {
        var id = Encoding.ASCII.GetBytes("SPICE   Virtual CD-ROM");
        return Ok([0x05, 0x83, 0x00, (byte)(4 + id.Length), 0x02, 0x00, 0x00, (byte)id.Length, .. id], allocation);
    }

    private ScsiResponse ModeSense(bool ten, int page, int allocation)
    {
        // Pagina delle capacità e dello stato meccanico CD/DVD (0x2A).
        byte[] capabilities =
        [
            0x2A, 0x14, 0x3B, 0x00, 0x71, 0x63, 0x29, 0x00,
            0x02, 0xC2, 0x00, 0x02, 0x02, 0x00, 0x02, 0xC2,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        byte[] errorRecovery = [0x01, 0x06, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00];
        byte[] pages;
        switch (page)
        {
            case 0x2A: pages = capabilities; break;
            case 0x01: pages = errorRecovery; break;
            case 0x3F: pages = [.. errorRecovery, .. capabilities]; break;
            default: return Fail(Sense.InvalidField);
        }
        return ten
            ? Ok([.. Be16Bytes(6 + pages.Length), 0x00, 0x00, 0, 0, 0, 0, .. pages], allocation)
            : Ok([(byte)(3 + pages.Length), 0x00, 0x00, 0, .. pages], allocation);
    }

    private static byte[] Address(long lba, bool msf)
    {
        if (!msf) return Be32Bytes(lba);
        var frames = lba + 150;
        return [0, (byte)(frames / (75 * 60) & 0xFF), (byte)(frames / 75 % 60), (byte)(frames % 75)];
    }

    private ScsiResponse ReadToc(bool msf, int format, int allocation)
    {
        switch (format)
        {
            case 0: // TOC formattato: traccia 1 + lead-out
                byte[] body = [1, 1, 0, 0x14, 1, 0, .. Address(0, msf), 0, 0x14, 0xAA, 0, .. Address(BlockCount, msf)];
                return Ok([.. Be16Bytes(body.Length), .. body], allocation);
            case 1: // informazioni multisessione
                byte[] session = [1, 1, 0, 0x14, 1, 0, .. Address(0, msf)];
                return Ok([.. Be16Bytes(session.Length), .. session], allocation);
            default:
                return Fail(Sense.InvalidField);
        }
    }

    private ScsiResponse GetConfiguration(int allocation)
    {
        var current = IsDvd ? 0x0010 : 0x0008;
        byte[] features =
        [
            // Elenco dei profili (feature 0x0000): DVD-ROM e CD-ROM.
            0x00, 0x00, 0x03, 0x08, 0x00, 0x10, (byte)(IsDvd ? 1 : 0), 0, 0x00, 0x08, (byte)(IsDvd ? 0 : 1), 0,
            // Core (0x0001): interfaccia fisica USB.
            0x00, 0x01, 0x0B, 0x08, 0x00, 0x00, 0x00, 0x08, 0x01, 0x00, 0x00, 0x00,
            // Supporto rimovibile (0x0003): cassetto, espulsione, blocco.
            0x00, 0x03, 0x03, 0x04, 0x29, 0x00, 0x00, 0x00,
        ];
        return Ok([.. Be32Bytes(4 + features.Length), 0, 0, (byte)(current >> 8), (byte)current, .. features], allocation);
    }

    private ScsiResponse EventStatus(int classes, int allocation)
    {
        // Nessuna classe supportata richiesta: intestazione "nessun evento".
        if ((classes & 0x10) == 0) return Ok([0x00, 0x02, 0x80, 0x10], allocation);
        var mediaEvent = (byte)(_newMediaPending && MediumPresent ? 2 : 0);
        _newMediaPending = false;
        return Ok([0x00, 0x06, 0x04, 0x10, mediaEvent, (byte)(MediumPresent ? 0x02 : 0x00), 0, 0], allocation);
    }

    internal static byte[] Be16Bytes(int value) => [(byte)(value >> 8), (byte)value];

    internal static byte[] Be32Bytes(long value)
    {
        var v = (uint)Math.Clamp(value, 0, uint.MaxValue);
        return [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }
}

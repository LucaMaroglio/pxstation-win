using System.Buffers.Binary;
using System.Threading.Channels;
using SpiceKit.Channels;
using SpiceKit.Session;
using SpiceKit.Tests.Support;
using SpiceKit.Transport;
using SpiceKit.UsbEmulation;
using SpiceKit.UsbRedirWire;
using SpiceKit.VVFile;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Usb;

/// <summary>Carta simile a una YubiKey: ATR T=1, risponde 200 byte + 90 00 dopo un attimo.</summary>
internal sealed class FakeCard : ISmartcard
{
    public static readonly byte[] Atr = [0x3B, 0xFD, 0x13, 0x00, 0x00, 0x81, 0x31, 0xFE, 0x15, 0x80, 0x73, 0xC0, 0x21, 0xC0, 0x57,
        0x59, 0x75, 0x62, 0x69, 0x4B, 0x65, 0x79, 0x40];

    public int Connects { get; private set; }

    public int Disconnects { get; private set; }

    public List<byte[]> Apdus { get; } = [];

    public string DisplayName => "Chiave finta";

    public Task<byte[]> ConnectAsync()
    {
        Connects++;
        return Task.FromResult(Atr);
    }

    public async Task<byte[]> TransmitAsync(byte[] apdu)
    {
        Apdus.Add(apdu);
        await Task.Delay(20); // carta lenta: la lettura IN deve aspettarla
        return [.. Enumerable.Repeat((byte)0x42, 200), 0x90, 0x00];
    }

    public Task DisconnectAsync()
    {
        Disconnects++;
        return Task.CompletedTask;
    }
}

public class EmulationTests
{
    // MARK: CD-ROM

    /// <summary>Immagine di 4 blocchi: ogni blocco pieno del suo numero.</summary>
    private static EmulatedCdrom Cdrom(Action? onEject = null)
    {
        var image = new byte[4 * 2048];
        for (var i = 0; i < image.Length; i++) image[i] = (byte)(i / 2048);
        return new EmulatedCdrom(new MemoryBlockSource(image), "prova.iso", onEject);
    }

    private static byte[] Cbw(uint tag, int length, bool directionIn, params byte[] cdb)
    {
        var cbw = new byte[31];
        BinaryPrimitives.WriteUInt32LittleEndian(cbw, 0x4342_5355);
        BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(4), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(8), (uint)length);
        cbw[12] = directionIn ? (byte)0x80 : (byte)0;
        cbw[14] = (byte)cdb.Length;
        cdb.CopyTo(cbw, 15);
        return cbw;
    }

    /// <summary>Un comando SCSI completo: CBW, dati IN, CSW. Restituisce (dati, stato del CSW).</summary>
    private static async Task<(byte[] Data, byte Status)> ScsiAsync(EmulatedCdrom drive, uint tag, int length, params byte[] cdb)
    {
        var sent = await drive.BulkAsync(EmulatedCdrom.BulkOut, Cbw(tag, length, true, cdb), 31);
        Assert.Equal(UsbRedirStatus.Success, sent.Status);
        var data = Array.Empty<byte>();
        if (length > 0) data = (await drive.BulkAsync(EmulatedCdrom.BulkIn, [], length)).Data;
        var csw = (await drive.BulkAsync(EmulatedCdrom.BulkIn, [], 13)).Data;
        Assert.Equal(13, csw.Length);
        Assert.Equal(0x5342_5355u, BinaryPrimitives.ReadUInt32LittleEndian(csw));
        Assert.Equal(tag, BinaryPrimitives.ReadUInt32LittleEndian(csw.AsSpan(4)));
        return (data, csw[12]);
    }

    [Fact]
    public async Task Cdrom_answers_inquiry_capacity_and_reads_blocks()
    {
        var drive = Cdrom();
        var inquiry = await ScsiAsync(drive, 1, 36, 0x12, 0, 0, 0, 36, 0);
        Assert.Equal((byte)0x05, inquiry.Data[0]); // CD/DVD
        Assert.Equal((byte)0x80, inquiry.Data[1]); // rimovibile
        Assert.Equal(0, inquiry.Status);

        // Il primo TEST UNIT READY segnala "supporto cambiato", come un lettore vero; poi è pronto.
        Assert.Equal(1, (await ScsiAsync(drive, 2, 0, 0x00, 0, 0, 0, 0, 0)).Status);
        var sense = await ScsiAsync(drive, 3, 18, 0x03, 0, 0, 0, 18, 0);
        Assert.Equal((0x06, 0x28), (sense.Data[2], sense.Data[12]));
        Assert.Equal(0, (await ScsiAsync(drive, 4, 0, 0x00, 0, 0, 0, 0, 0)).Status);

        var capacity = await ScsiAsync(drive, 5, 8, 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(capacity.Data)); // ultimo LBA
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32BigEndian(capacity.Data.AsSpan(4)));

        var read = await ScsiAsync(drive, 6, 2 * 2048, 0x28, 0, 0, 0, 0, 2, 0, 0, 2, 0); // LBA 2, 2 blocchi
        Assert.Equal(4096, read.Data.Length);
        Assert.Equal((byte)2, read.Data[0]);
        Assert.Equal((byte)3, read.Data[4095]);

        var outOfRange = await ScsiAsync(drive, 7, 2048, 0x28, 0, 0, 0, 0, 4, 0, 0, 1, 0);
        Assert.Equal(1, outOfRange.Status);
        Assert.Equal((byte)0x21, (await ScsiAsync(drive, 8, 18, 0x03, 0, 0, 0, 18, 0)).Data[12]);
    }

    [Fact]
    public async Task Cdrom_is_read_only_stalls_on_bad_cbw_and_ejects()
    {
        var ejected = false;
        var drive = Cdrom(() => ejected = true);
        // WRITE (10) con dati OUT: accettati e scartati, esito "protetto da scrittura".
        await drive.BulkAsync(EmulatedCdrom.BulkOut, Cbw(1, 2048, false, 0x2A, 0, 0, 0, 0, 0, 0, 0, 1, 0), 31);
        await drive.BulkAsync(EmulatedCdrom.BulkOut, new byte[2048], 2048);
        Assert.Equal(1, (await drive.BulkAsync(EmulatedCdrom.BulkIn, [], 13)).Data[12]);

        // START STOP UNIT con LoEj: espulso, poi niente supporto.
        Assert.Equal(0, (await ScsiAsync(drive, 2, 0, 0x1B, 0, 0, 0, 0x02, 0)).Status);
        Assert.True(ejected);
        Assert.Equal(1, (await ScsiAsync(drive, 3, 8, 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0)).Status);

        // CBW non valido: stallo finché un reset Bulk-Only non lo sblocca.
        Assert.Equal(UsbRedirStatus.Stall, (await drive.BulkAsync(EmulatedCdrom.BulkOut, new byte[31], 31)).Status);
        Assert.Equal(UsbRedirStatus.Stall, (await drive.BulkAsync(EmulatedCdrom.BulkOut, Cbw(4, 0, true, 0x00), 31)).Status);
        await drive.ControlAsync(new UsbControlSetup(0x21, 0xFF, 0, 0, 0), []);
        Assert.Equal(UsbRedirStatus.Success, (await drive.BulkAsync(EmulatedCdrom.BulkOut, Cbw(5, 0, true, 0x00), 31)).Status);
    }

    [Fact]
    public async Task Cdrom_descriptors_are_well_formed()
    {
        var drive = Cdrom();
        var device = (await drive.ControlAsync(new UsbControlSetup(0x80, 0x06, 0x0100, 0, 64), [])).Data;
        Assert.Equal(18, device.Length);
        Assert.Equal((ushort)0x1209, BinaryPrimitives.ReadUInt16LittleEndian(device.AsSpan(8)));
        var config = (await drive.ControlAsync(new UsbControlSetup(0x80, 0x06, 0x0200, 0, 255), [])).Data;
        Assert.Equal(32, config.Length);
        Assert.Equal((byte)0x08, config[9 + 5]); // Mass Storage
        Assert.Equal(UsbRedirStatus.Stall, (await drive.ControlAsync(new UsbControlSetup(0x80, 0x06, 0x0300 | 9, 0, 255), [])).Status);
        Assert.Equal([0], (await drive.ControlAsync(new UsbControlSetup(0xA1, 0xFE, 0, 0, 1), [])).Data); // Get Max LUN
    }

    // MARK: lettore CCID

    private static byte[] Command(byte type, byte seq, byte[]? data = null)
    {
        data ??= [];
        return [type, .. EmulatedCcidReader.Le32((uint)data.Length), 0, seq, 0, 0, 0, .. data];
    }

    /// <summary>Legge una risposta RDR_to_PC intera a pacchetti da 64 byte, come un controller.</summary>
    private static async Task<byte[]> ReadResponseAsync(EmulatedCcidReader reader)
    {
        var bytes = (await reader.BulkAsync(EmulatedCcidReader.BulkIn, [], 64)).Data.ToList();
        if (bytes.Count < 10) return [.. bytes];
        var total = 10 + (bytes[1] | (bytes[2] << 8));
        while (bytes.Count < total) bytes.AddRange((await reader.BulkAsync(EmulatedCcidReader.BulkIn, [], 64)).Data);
        return [.. bytes];
    }

    [Fact]
    public async Task Ccid_descriptors_are_well_formed()
    {
        var reader = new EmulatedCcidReader(new FakeCard());
        var config = (await reader.ControlAsync(new UsbControlSetup(0x80, 0x06, 0x0200, 0, 512), [])).Data;
        Assert.Equal(9 + 9 + 54 + 21, config.Length);
        Assert.Equal(config.Length, config[2] | (config[3] << 8));
        var types = new List<byte>();
        for (var offset = 0; offset < config.Length; offset += config[offset])
        {
            Assert.True(config[offset] > 0);
            types.Add(config[offset + 1]);
        }
        Assert.Equal([0x02, 0x04, 0x21, 0x05, 0x05, 0x05], types);
        Assert.Equal((byte)0x0B, config[9 + 5]);
    }

    [Fact]
    public async Task Ccid_power_on_transfer_and_slot_status()
    {
        var card = new FakeCard();
        var reader = new EmulatedCcidReader(card);
        await reader.BulkAsync(EmulatedCcidReader.BulkOut, Command(0x65, 1), 0);
        var status = await ReadResponseAsync(reader);
        Assert.Equal((0x81, 1, 0x01), (status[0], status[6], status[7])); // presente, inattiva

        await reader.BulkAsync(EmulatedCcidReader.BulkOut, Command(0x62, 2), 0);
        var powerOn = await ReadResponseAsync(reader);
        Assert.Equal((0x80, 2, 0x00), (powerOn[0], powerOn[6], powerOn[7]));
        Assert.Equal(FakeCard.Atr, powerOn[10..]);

        await reader.BulkAsync(EmulatedCcidReader.BulkOut, Command(0x6C, 3), 0);
        var parameters = await ReadResponseAsync(reader);
        Assert.Equal((0x82, 1, 17), (parameters[0], parameters[9], parameters.Length)); // T=1 dall'ATR

        // Una risposta più lunga di un pacchetto arriva in più letture; quella partita prima della carta aspetta.
        var response = ReadResponseAsync(reader);
        await reader.BulkAsync(EmulatedCcidReader.BulkOut, Command(0x6F, 4, [0x00, 0xA4, 0x04, 0x00, 0x05, 1, 2, 3, 4, 5]), 0);
        var block = await response;
        Assert.Equal((0x80, 4, 10 + 202), (block[0], block[6], block.Length));
        Assert.Equal([0x90, 0x00], block[^2..]);

        await reader.CloseAsync();
        Assert.Equal((1, 1), (card.Connects, card.Disconnects));
    }

    [Fact]
    public async Task Ccid_rejects_oversized_unsupported_and_unread_commands()
    {
        var reader = new EmulatedCcidReader(new FakeCard());
        var huge = Command(0x6F, 9);
        huge[2] = 0x10; // dwLength = 4096 > 261
        await reader.BulkAsync(EmulatedCcidReader.BulkOut, huge, 0);
        var oversized = await ReadResponseAsync(reader);
        Assert.True(oversized[0] == 0x81 && (oversized[7] & 0x40) != 0);

        await reader.BulkAsync(EmulatedCcidReader.BulkOut, Command(0x6B, 10), 0); // Escape
        var escape = await ReadResponseAsync(reader);
        Assert.True((escape[7] & 0x40) != 0 && escape[8] == 0); // CMD_NOT_SUPPORTED

        // Comandi senza leggere le risposte: oltre il limite si rifiutano.
        var stalled = false;
        for (var seq = 0; seq < 200 && !stalled; seq++)
        {
            stalled = (await reader.BulkAsync(EmulatedCcidReader.BulkOut, Command(0x65, (byte)seq), 0)).Status == UsbRedirStatus.Stall;
        }
        Assert.True(stalled);
        byte[] batch = [.. Enumerable.Range(0, 500).SelectMany(i => Command(0x65, (byte)i))];
        Assert.Equal(UsbRedirStatus.Stall, (await reader.BulkAsync(EmulatedCcidReader.BulkOut, batch, 0)).Status);
        await reader.CloseAsync();
    }

    [Fact]
    public async Task Ccid_interrupt_notifies_once_and_newer_waits_end_older_ones()
    {
        var reader = new EmulatedCcidReader(new FakeCard());
        var first = await reader.InterruptAsync(EmulatedCcidReader.InterruptIn, [], 8);
        Assert.Equal([0x50, 0x03], first.Data);
        var older = reader.InterruptAsync(EmulatedCcidReader.InterruptIn, [], 8);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        var newer = reader.InterruptAsync(EmulatedCcidReader.InterruptIn, [], 8);
        Assert.Equal(UsbRedirStatus.Cancelled, (await older).Status);
        await reader.AbortAsync(EmulatedCcidReader.InterruptIn);
        Assert.Equal(UsbRedirStatus.Cancelled, (await newer).Status);
        await reader.CloseAsync();
    }

    [Fact]
    public void Atr_protocols()
    {
        Assert.Contains(1, CcidResponse.AtrProtocols(FakeCard.Atr));
        Assert.Equal([0], CcidResponse.AtrProtocols([0x3B, 0x00]));
    }

    // MARK: canale smartcard

    [Fact]
    public async Task Smartcard_channel_adds_the_reader_inserts_and_relays_apdus()
    {
        var (client, server) = MemoryTransport.Pair();
        var events = Channel.CreateUnbounded<SmartcardEvent>();
        var channel = new SmartcardChannel(new ChannelConnection(ChannelType.Smartcard, 0, client, new CapabilitySet()), events.Writer);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var loop = channel.RunAsync(stop.Token);
        var card = new FakeCard();
        var insert = channel.InsertAsync(card);

        async Task<VscMessage> NextAsync()
        {
            var header = MiniHeader.Decode(await server.ReceiveAsync(MiniHeader.WireSize, TestContext.Current.CancellationToken));
            Assert.Equal(VscMessage.ChannelMessageType, header.Type);
            return VscMessage.Decode(await server.ReceiveAsync((int)header.Size, TestContext.Current.CancellationToken));
        }

        Task ServerSendAsync(VscMessage message) => server.SendAsync(message.Encode().Encode(), TestContext.Current.CancellationToken);

        var add = await NextAsync();
        Assert.Equal((VscMessageType.ReaderAdd, VscMessage.UndefinedReaderId), (add.Type, add.ReaderId));
        await ServerSendAsync(VscMessage.Failure(VscErrorCode.Success, 7)); // id assegnato: 7
        var atr = await NextAsync();
        Assert.Equal((VscMessageType.Atr, 7u), (atr.Type, atr.ReaderId));
        Assert.Equal(FakeCard.Atr, atr.Data);
        await insert;
        Assert.Equal("Chiave finta", channel.SharedCardName);

        await ServerSendAsync(new VscMessage(VscMessageType.Apdu, 7, [0x00, 0xA4]));
        var reply = await NextAsync();
        Assert.Equal(VscMessageType.Apdu, reply.Type);
        Assert.Equal([0x90, 0x00], reply.Data[^2..]);
        await ServerSendAsync(new VscMessage(VscMessageType.Apdu, 8, [0x00])); // lettore sbagliato
        Assert.Equal(VscMessageType.Error, (await NextAsync()).Type);
        await ServerSendAsync(new VscMessage(VscMessageType.Flush, 7));
        Assert.Equal(VscMessageType.FlushComplete, (await NextAsync()).Type);

        await Assert.ThrowsAsync<SmartcardException>(() => channel.InsertAsync(new FakeCard()));
        await channel.RemoveAsync();
        Assert.Equal(VscMessageType.CardRemove, (await NextAsync()).Type);
        Assert.Equal(1, card.Disconnects);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public void Oversized_vsc_messages_are_rejected()
    {
        var w = new ByteWriter();
        w.WriteU32((uint)VscMessageType.Apdu);
        w.WriteU32(0);
        w.WriteU32(VscMessage.MaxPayload + 1);
        Assert.Throws<WireException>(() => VscMessage.Decode(w.ToArray()));
    }

    // MARK: regole del file di connessione

    [Fact]
    public async Task Connection_file_rules_apply_to_every_device()
    {
        static SpiceSession Session(string extra) => new(
            VVFileParser.Parse($"[virt-viewer]\ntype=spice\nhost=127.0.0.1\nport=5930\n{extra}").Profile,
            new MemoryConnector((_, _) => Task.CompletedTask), new FakeEncryptor());

        await using var disabled = Session("enable-usbredir=0\n");
        Assert.False(disabled.UsbAllowed);
        Assert.Equal(UsbAttachErrorKind.BlockedByConnectionFile,
            (await Assert.ThrowsAsync<UsbAttachException>(() => disabled.AttachUsbAsync(Cdrom()))).Kind);

        // Filtro che permette solo la classe 0xFF: il CD-ROM (classe 8) è vietato.
        await using var filtered = Session("usb-filter=0xff,-1,-1,-1,1\n");
        Assert.Equal(UsbAttachErrorKind.BlockedByConnectionFile,
            (await Assert.ThrowsAsync<UsbAttachException>(() => filtered.AttachUsbAsync(Cdrom()))).Kind);

        // Smartcard disattivate: niente lettore emulato.
        await using var noSmartcard = Session("enable-smartcard=0\n");
        Assert.Equal(UsbAttachErrorKind.BlockedByConnectionFile,
            (await Assert.ThrowsAsync<UsbAttachException>(() => noSmartcard.AttachUsbAsync(new EmulatedCcidReader(new FakeCard())))).Kind);

        // Permesso, ma nessuno slot: occupato.
        await using var allowed = Session("");
        Assert.Equal(UsbAttachErrorKind.SlotBusy, (await Assert.ThrowsAsync<UsbAttachException>(() => allowed.AttachUsbAsync(Cdrom()))).Kind);
    }

    [Fact]
    public void Usb_slots_are_deduplicated_and_capped()
    {
        var offered = Enumerable.Range(0, 40).Select(i => new ChannelEntry((byte)ChannelType.Usbredir, (byte)(i % 20)))
            .Append(new ChannelEntry((byte)ChannelType.Smartcard, 0)).ToList();
        var wanted = SpiceSession.Wanted(offered, usb: true, smartcard: true);
        Assert.Equal(SpiceSession.MaxUsbSlots, wanted.Count(e => e.Type == ChannelType.Usbredir));
        Assert.Single(wanted, e => e.Type == ChannelType.Smartcard);
        Assert.Empty(SpiceSession.Wanted(offered));
    }
}

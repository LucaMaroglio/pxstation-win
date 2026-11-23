using System.Buffers.Binary;
using System.Text;
using SpiceKit.Agent;
using SpiceKit.Channels;
using SpiceKit.Session;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Agent;

public class AgentTests
{
    [Fact]
    public void Messages_are_split_into_2048_byte_chunks_and_reassembled()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var message = new AgentMessage(AgentMessageType.Clipboard, data);
        var chunks = message.EncodeChunks();
        Assert.Equal([2048, 2048, 924], chunks.Select(c => c.Length));
        Assert.Equal(AgentMessage.Protocol, BinaryPrimitives.ReadUInt32LittleEndian(chunks[0]));
        Assert.Equal((uint)AgentMessageType.Clipboard, BinaryPrimitives.ReadUInt32LittleEndian(chunks[0].AsSpan(4)));
        Assert.Equal(5000u, BinaryPrimitives.ReadUInt32LittleEndian(chunks[0].AsSpan(16)));

        var assembler = new AgentMessageAssembler();
        Assert.Empty(assembler.Feed(chunks[0]));
        Assert.Empty(assembler.Feed(chunks[1]));
        // Due messaggi nello stesso pezzo: il secondo resta in sospeso finché non è completo.
        var next = new AgentMessage(AgentMessageType.ClipboardRelease, [1, 2, 3, 4]).EncodeChunks()[0];
        var done = assembler.Feed([.. chunks[2], .. next.AsSpan(0, 10)]);
        var whole = Assert.Single(done);
        Assert.Equal(data, whole.Data);
        var second = Assert.Single(assembler.Feed(next.AsSpan(10)));
        Assert.Equal(AgentMessageType.ClipboardRelease, second.KnownType);
    }

    [Fact]
    public void Hostile_agent_headers_are_rejected()
    {
        var assembler = new AgentMessageAssembler();
        var header = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 2); // protocollo sbagliato
        Assert.Throws<WireException>(() => assembler.Feed(header));
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), AgentMessageAssembler.MaxMessageSize + 1u);
        var error = Assert.Throws<WireException>(() => assembler.Feed(header));
        Assert.Equal(WireErrorKind.LimitExceeded, error.Kind);
        // Dopo un errore si riparte puliti.
        Assert.Single(assembler.Feed(new AgentMessage(AgentMessageType.Reply, [9]).EncodeChunks()[0]));
    }

    [Fact]
    public void Capabilities_and_monitors_config_have_the_wire_layout()
    {
        var caps = AgentMessage.AnnounceCapabilities(true, [AgentCapability.MonitorsConfig, AgentCapability.ClipboardByDemand]);
        Assert.Equal([1, 0, 0, 0, 0b10_0010, 0, 0, 0], caps.Data);
        var (request, decoded) = AgentMessage.DecodeCapabilities(caps.Data);
        Assert.True(request);
        Assert.True(decoded.Contains((int)AgentCapability.ClipboardByDemand));

        var monitors = AgentMessage.MonitorsConfig([(1920u, 1080u, 0, 0)]);
        var u = (int offset) => BinaryPrimitives.ReadUInt32LittleEndian(monitors.Data.AsSpan(offset));
        Assert.Equal(new uint[] { 1, 1, 1080, 1920, 32, 0, 0 }, new[] { u(0), u(4), u(8), u(12), u(16), u(20), u(24) });
    }

    [Fact]
    public void Clipboard_messages_round_trip_with_and_without_selection_header()
    {
        foreach (var header in new[] { false, true })
        {
            var grab = ClipboardCodec.Decode(ClipboardCodec.Grab([ClipboardDataType.Utf8Text, ClipboardDataType.Png], header), header);
            Assert.Equal([ClipboardDataType.Utf8Text, ClipboardDataType.Png], Assert.IsType<ClipboardMessage.Grab>(grab).Types);
            var request = ClipboardCodec.Decode(ClipboardCodec.Request(ClipboardDataType.Png, header), header);
            Assert.Equal(ClipboardDataType.Png, Assert.IsType<ClipboardMessage.Request>(request).Type);
            var data = ClipboardCodec.Decode(ClipboardCodec.Data(ClipboardDataType.Utf8Text, "ciao"u8, header), header);
            Assert.Equal("ciao"u8.ToArray(), Assert.IsType<ClipboardMessage.Data>(data).Bytes);
            Assert.IsType<ClipboardMessage.Release>(ClipboardCodec.Decode(ClipboardCodec.Release(header), header));
        }
        // Selezione sconosciuta e tipi di altri messaggi: ignorati.
        Assert.Null(ClipboardCodec.Decode(new AgentMessage(AgentMessageType.ClipboardRelease, [9, 0, 0, 0]), true));
        Assert.Null(ClipboardCodec.Decode(new AgentMessage(AgentMessageType.Reply, []), true));
        // Troppi dati: nessun messaggio troncato verso l'ospite.
        var huge = ClipboardCodec.Data(ClipboardDataType.Png, new byte[ClipboardCodec.MaxDataSize + 1], false);
        Assert.Equal(4, huge.Data.Length);
    }

    [Fact]
    public void Text_line_endings_follow_the_guest()
    {
        Assert.Equal("a\r\nb"u8.ToArray(), ClipboardCodec.TextToGuest("a\r\nb", guestUsesCrlf: true));
        Assert.Equal("a\nb"u8.ToArray(), ClipboardCodec.TextToGuest("a\r\nb", guestUsesCrlf: false));
        Assert.Equal("a\r\nb", ClipboardCodec.TextFromGuest("a\nb\0\0"u8));
        Assert.Equal("a\r\nb", ClipboardCodec.TextFromGuest("a\r\nb"u8));
    }

    [Theory]
    [InlineData("relazione.pdf", "relazione.pdf")]
    [InlineData("..\\..\\Windows\\win.ini", "_._.._Windows_win.ini")]
    [InlineData("/etc/passwd", "_etc_passwd")]
    [InlineData(".bashrc", "_bashrc")]
    [InlineData(" spazio", "\\sspazio")]
    [InlineData("a\nb", "a_b")]
    [InlineData("", "file")]
    public void File_names_cannot_escape_the_download_folder(string name, string expected) =>
        Assert.Equal(expected, FileTransferCodec.EscapedName(name));

    [Fact]
    public void File_transfer_messages()
    {
        var start = FileTransferCodec.Start(7, "a.txt", 12);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(start.Data));
        Assert.Equal("[vdagent-file-xfer]\nname=a.txt\nsize=12\n\0", Encoding.UTF8.GetString(start.Data.AsSpan(4)));
        var status = new AgentMessage(AgentMessageType.FileXferStatus, [7, 0, 0, 0, 3, 0, 0, 0]);
        Assert.Equal((7u, FileTransferResult.Success), FileTransferCodec.DecodeStatus(status));
        var unknown = new AgentMessage(AgentMessageType.FileXferStatus, [7, 0, 0, 0, 99, 0, 0, 0]);
        Assert.Equal(FileTransferResult.Error, FileTransferCodec.DecodeStatus(unknown)!.Value.Result);
        Assert.Null(FileTransferCodec.DecodeStatus(new AgentMessage(AgentMessageType.FileXferStatus, [1])));
    }

    [Fact]
    public void The_outbox_respects_tokens_and_its_size_limit()
    {
        var outbox = new AgentOutbox();
        Assert.True(outbox.Enqueue(new AgentMessage(AgentMessageType.Clipboard, new byte[5000])));
        Assert.Null(outbox.TryTake()); // nessun gettone
        outbox.AddTokens(2);
        Assert.NotNull(outbox.TryTake());
        Assert.NotNull(outbox.TryTake());
        Assert.Null(outbox.TryTake());
        Assert.Equal(924, outbox.PendingBytes);
        // Oltre il limite il messaggio si scarta invece di accumularsi.
        Assert.False(outbox.Enqueue(new AgentMessage(AgentMessageType.Clipboard, new byte[AgentOutbox.MaxBytes])));
        outbox.Clear();
        Assert.Equal(0, outbox.PendingBytes);
    }

    [Fact]
    public void Reconnect_policy_matches_the_other_clients()
    {
        var policy = new ReconnectPolicy();
        Assert.Equal(8, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.Delay(1));
        Assert.Null(policy.Delay(9));
        var network = new SessionFailure("rete", false, SessionRecovery.Automatic);
        var server = new SessionFailure("server", false, SessionRecovery.Manual);
        var ticket = new SessionFailure("ticket", true, SessionRecovery.Manual);
        Assert.True(ReconnectPolicy.ShouldReconnect(network, wasConnected: true, reconnecting: false));
        Assert.False(ReconnectPolicy.ShouldReconnect(network, wasConnected: false, reconnecting: false));
        Assert.False(ReconnectPolicy.ShouldReconnect(server, wasConnected: true, reconnecting: false));
        Assert.True(ReconnectPolicy.ShouldReconnect(server, wasConnected: false, reconnecting: true));
        Assert.False(ReconnectPolicy.ShouldReconnect(ticket, wasConnected: false, reconnecting: true));
        Assert.False(ReconnectPolicy.ShouldReconnect(null, wasConnected: true, reconnecting: false));
    }
}

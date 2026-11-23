using System.Buffers.Binary;
using System.Text;
using SpiceKit.Agent;
using SpiceKit.Session;
using SpiceKit.Tests.Support;
using SpiceKit.Transport;
using SpiceKit.VVFile;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Agent;

/// <summary>Sessione intera contro un server finto con un agente finto (come spice-vdagent di un ospite Windows).</summary>
public sealed class SessionAgentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Server main con agente: capacità, trasferimento di file, registro di ciò che riceve.</summary>
    private sealed class AgentServer
    {
        private readonly AgentMessageAssembler _assembler = new();
        private readonly List<byte> _file = [];
        private ulong _fileSize;
        private MemoryTransport? _main;

        public List<AgentMessage> Received { get; } = [];

        public TaskCompletionSource<string> FileName { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<byte[]> FileBytes { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FileTransferResult FinalResult { get; init; } = FileTransferResult.Success;

        public async Task ServeAsync(MemoryTransport stream, SpiceRoute route)
        {
            var link = await FakeSpiceServer.ServeLinkAsync(stream);
            if (link.Type != ChannelType.Main)
            {
                await stream.ReceiveAsync(1);
                return;
            }
            _main = stream;
            try
            {
                await FakeSpiceServer.SendAsync(stream, (ushort)MainServerMessage.Init, FakeSpiceServer.MainInit(1, agent: true));
                while (true)
                {
                    var message = await FakeSpiceServer.ReceiveAsync(stream);
                    switch ((MainClientMessage)message.Type)
                    {
                        case MainClientMessage.AttachChannels:
                            await FakeSpiceServer.SendAsync(stream, (ushort)MainServerMessage.ChannelsList, FakeSpiceServer.ChannelsList());
                            break;
                        case MainClientMessage.AgentStart:
                            // L'agente dell'ospite annuncia le sue capacità (CRLF: ospite Windows).
                            await SendAgentAsync(AgentMessage.AnnounceCapabilities(false,
                                [AgentCapability.ClipboardByDemand, AgentCapability.ClipboardSelection, AgentCapability.GuestLineEndCrlf]));
                            break;
                        case MainClientMessage.AgentData:
                            await FakeSpiceServer.SendAsync(stream, (ushort)MainServerMessage.AgentToken, [1, 0, 0, 0]);
                            foreach (var agent in _assembler.Feed(message.Payload)) await HandleAsync(agent);
                            break;
                    }
                }
            }
            catch (TransportException)
            {
                // il client ha chiuso
            }
        }

        public Task SendAgentAsync(AgentMessage message) =>
            Task.WhenAll(message.EncodeChunks().Select(chunk => FakeSpiceServer.SendAsync(_main!, (ushort)MainServerMessage.AgentData, chunk)));

        private async Task HandleAsync(AgentMessage message)
        {
            lock (Received) Received.Add(message);
            switch (message.KnownType)
            {
                case AgentMessageType.FileXferStart:
                    var text = Encoding.UTF8.GetString(message.Data.AsSpan(4)).TrimEnd('\0');
                    var lines = text.Split('\n');
                    FileName.TrySetResult(lines.First(l => l.StartsWith("name=", StringComparison.Ordinal))[5..]);
                    _fileSize = ulong.Parse(lines.First(l => l.StartsWith("size=", StringComparison.Ordinal))[5..], System.Globalization.CultureInfo.InvariantCulture);
                    await SendAgentAsync(FileTransferCodec.Status(BinaryPrimitives.ReadUInt32LittleEndian(message.Data), FileTransferResult.CanSendData));
                    break;
                case AgentMessageType.FileXferData:
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(message.Data);
                    var length = (int)BinaryPrimitives.ReadUInt64LittleEndian(message.Data.AsSpan(4));
                    _file.AddRange(message.Data.AsSpan(12, length));
                    if ((ulong)_file.Count == _fileSize)
                    {
                        FileBytes.TrySetResult([.. _file]);
                        await SendAgentAsync(FileTransferCodec.Status(id, FinalResult));
                    }
                    break;
            }
        }
    }

    private static async Task<SpiceSession> ConnectAsync(AgentServer server)
    {
        var profile = VVFileParser.Parse("[virt-viewer]\ntype=spice\nhost=127.0.0.1\nport=5930\n").Profile;
        var session = new SpiceSession(profile, new MemoryConnector(server.ServeAsync), new FakeEncryptor());
        await session.ConnectAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (var value in session.Events.ReadAllAsync(timeout.Token))
        {
            if (value is SessionEvent.ClipboardAvailable { Available: true }) break;
        }
        return session;
    }

    private static async Task<AgentMessage> WaitForAsync(AgentServer server, AgentMessageType type)
    {
        for (var i = 0; i < 500; i++)
        {
            lock (server.Received)
            {
                if (server.Received.LastOrDefault(m => m.KnownType == type) is { } found) return found;
            }
            await Task.Delay(10, Ct);
        }
        throw new TimeoutException($"nessun messaggio {type}");
    }

    [Fact]
    public async Task Guest_resolution_is_requested_through_the_agent()
    {
        var server = new AgentServer();
        await using var session = await ConnectAsync(server);
        Assert.True(session.IsAgentConnected);
        Assert.True(session.RequestGuestResolution(1280, 720));
        Assert.False(session.RequestGuestResolution(0, 720));
        var config = await WaitForAsync(server, AgentMessageType.MonitorsConfig);
        Assert.Equal(720u, BinaryPrimitives.ReadUInt32LittleEndian(config.Data.AsSpan(8)));
        Assert.Equal(1280u, BinaryPrimitives.ReadUInt32LittleEndian(config.Data.AsSpan(12)));
    }

    [Fact]
    public async Task Clipboard_round_trip_with_line_endings()
    {
        var server = new AgentServer();
        await using var session = await ConnectAsync(server);
        Assert.True(session.ClipboardSupported);

        // L'ospite copia: GRAB, il client chiede, l'ospite manda il testo (LF → CRLF per Windows).
        await server.SendAgentAsync(ClipboardCodec.Grab([ClipboardDataType.Utf8Text], selectionHeader: true));
        var grab = Assert.IsType<ClipboardMessage.Grab>(await session.ClipboardEvents.ReadAsync(Ct));
        Assert.Equal([ClipboardDataType.Utf8Text], grab.Types);
        session.RequestClipboard(ClipboardDataType.Utf8Text);
        await WaitForAsync(server, AgentMessageType.ClipboardRequest);
        await server.SendAgentAsync(ClipboardCodec.Data(ClipboardDataType.Utf8Text, "uno\ndue\0"u8, selectionHeader: true));
        var data = Assert.IsType<ClipboardMessage.Data>(await session.ClipboardEvents.ReadAsync(Ct));
        Assert.Equal("uno\r\ndue", Encoding.UTF8.GetString(data.Bytes));

        // Windows → ospite Windows: CRLF resta CRLF, con l'intestazione della selezione.
        session.OfferClipboard([ClipboardDataType.Utf8Text]);
        session.ProvideClipboard(ClipboardDataType.Utf8Text, "a\r\nb"u8.ToArray());
        var sent = ClipboardCodec.Decode(await WaitForAsync(server, AgentMessageType.Clipboard), selectionHeader: true);
        Assert.Equal("a\r\nb"u8.ToArray(), Assert.IsType<ClipboardMessage.Data>(sent).Bytes);
        Assert.NotNull(await WaitForAsync(server, AgentMessageType.ClipboardGrab));
    }

    [Fact]
    public async Task Files_are_copied_in_chunks()
    {
        var server = new AgentServer();
        await using var session = await ConnectAsync(server);
        var path = Path.Combine(Path.GetTempPath(), $"wsc-test-{Guid.NewGuid():N}.bin");
        var content = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray();
        await File.WriteAllBytesAsync(path, content, Ct);
        try
        {
            long reported = 0;
            await session.SendFileAsync(path, new SyncProgress(v => reported = v), Ct);
            Assert.Equal(Path.GetFileName(path), await server.FileName.Task);
            Assert.Equal(content, await server.FileBytes.Task);
            Assert.Equal(content.Length, reported);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_refused_transfer_reports_why()
    {
        var server = new AgentServer { FinalResult = FileTransferResult.NotEnoughSpace };
        await using var session = await ConnectAsync(server);
        var path = Path.Combine(Path.GetTempPath(), $"wsc-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "ciao", Ct);
        try
        {
            var error = await Assert.ThrowsAsync<FileTransferException>(() => session.SendFileAsync(path, null, Ct));
            Assert.Equal(FileTransferResult.NotEnoughSpace, error.Result);
            await Assert.ThrowsAsync<FileTransferException>(() => session.SendFileAsync(path + ".manca", null, Ct));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>IProgress senza contesto di sincronizzazione (il valore è subito disponibile).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

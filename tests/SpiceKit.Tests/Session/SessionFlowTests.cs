using System.Buffers.Binary;
using SpiceKit.Channels;
using SpiceKit.Session;
using SpiceKit.Tests.Support;
using SpiceKit.Transport;
using SpiceKit.VVFile;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Session;

/// <summary>Flusso di una sessione contro un server finto in memoria (come <c>SessionFlowTests.swift</c>).</summary>
public sealed class SessionFlowTests
{
    private const uint SessionId = 0xABCD;

    private static ConnectionProfile Profile(string extra = "") =>
        VVFileParser.Parse($"[virt-viewer]\ntype=spice\nhost=127.0.0.1\nport=5930\npassword=secret\n{extra}").Profile;

    /// <summary>Server finto: main invia INIT e l'elenco dei canali; gli altri canali restano aperti.</summary>
    private sealed class Server
    {
        public List<ReceivedLink> Links { get; } = [];
        public List<ushort> MainMessages { get; } = [];
        public LinkError AuthResult { get; init; } = LinkError.Ok;
        public Func<MemoryTransport, Task>? AfterChannels { get; init; }

        public async Task ServeAsync(MemoryTransport stream, SpiceRoute route)
        {
            var link = await FakeSpiceServer.ServeLinkAsync(stream, authResult: AuthResult);
            lock (Links) Links.Add(link);
            if (AuthResult != LinkError.Ok) return;
            try
            {
                if (link.Type != ChannelType.Main)
                {
                    await stream.ReceiveAsync(1); // fino alla chiusura
                    return;
                }
                await FakeSpiceServer.SendAsync(stream, (ushort)MainServerMessage.Init, FakeSpiceServer.MainInit(SessionId));
                // ATTACH_CHANNELS e richiesta della modalità client.
                for (var i = 0; i < 2; i++)
                {
                    var received = await FakeSpiceServer.ReceiveAsync(stream);
                    lock (MainMessages) MainMessages.Add(received.Type);
                }
                await FakeSpiceServer.SendAsync(stream, (ushort)MainServerMessage.ChannelsList, FakeSpiceServer.ChannelsList(
                    (ChannelType.Display, 0), (ChannelType.Inputs, 0), (ChannelType.Cursor, 0), (ChannelType.Display, 0),
                    (ChannelType.Usbredir, 0), (ChannelType.Playback, 0), (ChannelType.Display, 1)));
                if (AfterChannels is not null) await AfterChannels(stream);
                await stream.ReceiveAsync(1);
            }
            catch (TransportException)
            {
                // il client ha chiuso
            }
        }
    }

    private static async Task<List<SessionEvent>> RunUntilAsync(SpiceSession session, Func<SessionEvent, bool> stop)
    {
        var events = new List<SessionEvent>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await session.ConnectAsync();
        await foreach (var value in session.Events.ReadAllAsync(timeout.Token))
        {
            events.Add(value);
            if (stop(value)) break;
        }
        return events;
    }

    [Fact]
    public async Task Main_then_channels_with_the_session_id()
    {
        var server = new Server();
        var connector = new MemoryConnector(server.ServeAsync);
        var profile = Profile();
        await using var session = new SpiceSession(profile, connector, new FakeEncryptor());
        var events = await RunUntilAsync(session, e => e is SessionEvent.ChannelsLinked);

        Assert.Equal([SessionStateKind.Connecting, SessionStateKind.Connected],
            events.OfType<SessionEvent.StateChanged>().Select(e => e.State.Kind));
        var linked = events.OfType<SessionEvent.ChannelsLinked>().Single();
        Assert.Equal(7, linked.Offered.Count);
        // Duplicati, audio (non supportato da questa sessione) e secondo schermo non collegati; gli slot USB sì, subito (M8).
        Assert.Equal(["display:0", "inputs:0", "cursor:0", "usbredir:0"], linked.Linked.Select(e => e.ToString()));

        var links = server.Links.OrderBy(l => l.Type).ToList();
        Assert.Equal([ChannelType.Main, ChannelType.Display, ChannelType.Inputs, ChannelType.Cursor, ChannelType.Usbredir], links.Select(l => l.Type));
        Assert.Equal(0u, links[0].ConnectionId);
        Assert.All(links.Skip(1), l => Assert.Equal(SessionId, l.ConnectionId));
        Assert.All(links, l => Assert.Equal("secret\0"u8.ToArray(), l.Ticket[..7]));
        Assert.Equal([(ushort)MainClientMessage.AttachChannels, (ushort)MainClientMessage.MouseModeRequest], server.MainMessages);
        Assert.True(profile.Password!.IsWiped, "il ticket va azzerato dopo l'autenticazione di tutti i canali");
        Assert.Equal(SessionStateKind.Connected, session.State.Kind);
    }

    [Fact]
    public async Task Rejected_ticket_ends_the_session_as_authentication_failure()
    {
        var profile = Profile();
        await using var session = new SpiceSession(profile, new MemoryConnector(new Server { AuthResult = LinkError.PermissionDenied }.ServeAsync), new FakeEncryptor());
        var events = await RunUntilAsync(session, e => e is SessionEvent.StateChanged { State.Kind: SessionStateKind.Disconnected });
        var failure = events.OfType<SessionEvent.StateChanged>().Last().State.Failure;
        Assert.NotNull(failure);
        Assert.True(failure.IsAuthenticationFailure);
        Assert.Equal(SessionRecovery.Manual, failure.Recovery);
        Assert.True(profile.Password!.IsWiped);
    }

    [Fact]
    public async Task Server_disconnect_ends_the_session()
    {
        var server = new Server
        {
            AfterChannels = async stream =>
            {
                var payload = new byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 1);
                await FakeSpiceServer.SendAsync(stream, (ushort)BaseServerMessage.Disconnecting, payload);
            },
        };
        await using var session = new SpiceSession(Profile(), new MemoryConnector(server.ServeAsync), new FakeEncryptor());
        var events = await RunUntilAsync(session, e => e is SessionEvent.StateChanged { State.Kind: SessionStateKind.Disconnected });
        var failure = events.OfType<SessionEvent.StateChanged>().Last().State.Failure;
        Assert.Equal(SessionRecovery.Manual, failure?.Recovery);
    }

    [Fact]
    public async Task Insecure_configuration_never_connects()
    {
        var connector = new MemoryConnector(new Server().ServeAsync);
        await using var session = new SpiceSession(Profile("secure-channels=all\n"), connector, new FakeEncryptor());
        var events = await RunUntilAsync(session, e => e is SessionEvent.StateChanged { State.Kind: SessionStateKind.Disconnected });
        Assert.Contains("TLS", events.OfType<SessionEvent.StateChanged>().Last().State.Failure?.Message, StringComparison.Ordinal);
        Assert.Empty(connector.Routes);
    }

    [Fact]
    public async Task Silent_server_times_out()
    {
        var connector = new MemoryConnector((stream, _) => stream.ReceiveAsync(1_000_000));
        await using var session = new SpiceSession(Profile(), connector, new FakeEncryptor(), TimeSpan.FromMilliseconds(300));
        var events = await RunUntilAsync(session, e => e is SessionEvent.StateChanged { State.Kind: SessionStateKind.Disconnected });
        Assert.Equal(SessionRecovery.Automatic, events.OfType<SessionEvent.StateChanged>().Last().State.Failure?.Recovery);
    }

    [Fact]
    public async Task User_disconnect_is_not_a_failure()
    {
        await using var session = new SpiceSession(Profile(), new MemoryConnector(new Server().ServeAsync), new FakeEncryptor());
        await RunUntilAsync(session, e => e is SessionEvent.ChannelsLinked);
        await session.DisconnectAsync();
        Assert.Equal(SessionState.Disconnected(null), session.State);
    }

    [Fact]
    public async Task Base_protocol_answers_set_ack_and_ping()
    {
        var (client, server) = MemoryTransport.Pair();
        var connection = new ChannelConnection(ChannelType.Display, 0, client, new CapabilitySet());
        var setAck = new ByteWriter();
        setAck.WriteU32(7);
        setAck.WriteU32(2);
        await FakeSpiceServer.SendAsync(server, (ushort)BaseServerMessage.SetAck, setAck.WrittenSpan);
        var ping = new ByteWriter();
        ping.WriteU32(9);
        ping.WriteU64(1234);
        ping.WriteBytes(new byte[100]);
        await FakeSpiceServer.SendAsync(server, (ushort)BaseServerMessage.Ping, ping.WrittenSpan);
        await FakeSpiceServer.SendAsync(server, 101, [1]);

        var message = await connection.NextMessageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(101, message.Type);
        var ackSync = await FakeSpiceServer.ReceiveAsync(server);
        Assert.Equal(((ushort)BaseClientMessage.AckSync, 7u), (ackSync.Type, BinaryPrimitives.ReadUInt32LittleEndian(ackSync.Payload)));
        // Finestra 2: dopo due messaggi ricevuti (PING e 101) parte un ACK.
        var pong = await FakeSpiceServer.ReceiveAsync(server);
        Assert.Equal((ushort)BaseClientMessage.Pong, pong.Type);
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(pong.Payload));
        Assert.Equal(1234ul, BinaryPrimitives.ReadUInt64LittleEndian(pong.Payload.AsSpan(4)));
        Assert.Equal((ushort)BaseClientMessage.Ack, (await FakeSpiceServer.ReceiveAsync(server)).Type);
    }

    [Fact]
    public async Task Oversized_message_is_refused()
    {
        var (client, server) = MemoryTransport.Pair();
        var connection = new ChannelConnection(ChannelType.Main, 0, client, new CapabilitySet());
        var header = new byte[6];
        new MiniHeader(101, ChannelConnection.MaxMessageSize + 1).Encode(header);
        await server.SendAsync(header, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ChannelException>(() => connection.NextMessageAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ChannelErrorKind.MessageTooLarge, error.Kind);
    }
}

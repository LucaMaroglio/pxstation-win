using System.Buffers.Binary;
using SpiceKit.Agent;
using SpiceKit.Channels;
using SpiceKit.Tests.Support;
using SpiceKit.Transport;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Agent;

/// <summary>Il canale main con l'agente: AGENT_START, capacità, gettoni in entrata e in uscita.</summary>
public class MainChannelAgentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SpiceMessage Init(bool agent, uint tokens)
    {
        var w = new ByteWriter();
        foreach (var value in new uint[] { 1, 1, 3, 1, agent ? 1u : 0, tokens, 0, 0 }) w.WriteU32(value);
        return new SpiceMessage((ushort)MainServerMessage.Init, w.ToArray());
    }

    private static async Task<SpiceMessage> ReceiveAsync(MemoryTransport server)
    {
        var header = MiniHeader.Decode(await server.ReceiveAsync(MiniHeader.WireSize, Ct));
        return new SpiceMessage(header.Type, await server.ReceiveAsync((int)header.Size, Ct));
    }

    private static async Task<SpiceMessage> ReceiveUntilAsync(MemoryTransport server, MainClientMessage type)
    {
        while (true)
        {
            var message = await ReceiveAsync(server);
            if (message.Type == (ushort)type) return message;
        }
    }

    private static SpiceMessage AgentData(AgentMessage message) =>
        new((ushort)MainServerMessage.AgentData, message.EncodeChunks().Single());

    [Fact]
    public async Task Agent_start_capabilities_and_tokens()
    {
        var (client, server) = MemoryTransport.Pair();
        var main = new MainChannel(new ChannelConnection(ChannelType.Main, 0, client, new CapabilitySet()));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = main.RunAsync(stop.Token);

        // Un solo gettone di invio: l'annuncio delle capacità parte, il resto aspetta.
        await server.SendAsync(Init(agent: true, tokens: 1).Encode(), Ct);
        var start = await ReceiveUntilAsync(server, MainClientMessage.AgentStart);
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(start.Payload));
        var announce = await ReceiveUntilAsync(server, MainClientMessage.AgentData);
        var assembler = new AgentMessageAssembler();
        var sent = Assert.Single(assembler.Feed(announce.Payload));
        Assert.Equal(AgentMessageType.AnnounceCapabilities, sent.KnownType);
        Assert.True(AgentMessage.DecodeCapabilities(sent.Data).Request);

        // L'ospite chiede le capacità due volte: una risposta sola, inviata quando arriva un gettone.
        var request = AgentMessage.AnnounceCapabilities(true, [AgentCapability.ClipboardByDemand]);
        await server.SendAsync(AgentData(request).Encode(), Ct);
        await server.SendAsync(AgentData(request).Encode(), Ct);
        var events = new List<MainEvent>();
        while (events.Count(e => e is MainEvent.AgentCapabilities) < 2) events.Add(await main.Events.ReadAsync(Ct));
        Assert.True(main.SendToAgent(new AgentMessage(AgentMessageType.ClipboardRelease, [])));
        await server.SendAsync(new SpiceMessage((ushort)MainServerMessage.AgentToken, [5, 0, 0, 0]).Encode(), Ct);
        var reply = Assert.Single(assembler.Feed((await ReceiveUntilAsync(server, MainClientMessage.AgentData)).Payload));
        Assert.False(AgentMessage.DecodeCapabilities(reply.Data).Request);
        var release = Assert.Single(assembler.Feed((await ReceiveUntilAsync(server, MainClientMessage.AgentData)).Payload));
        Assert.Equal(AgentMessageType.ClipboardRelease, release.KnownType);

        // Dopo 5 messaggi ricevuti si restituiscono 5 gettoni al server.
        for (var i = 0; i < 3; i++) await server.SendAsync(AgentData(new AgentMessage(AgentMessageType.Reply, [1])).Encode(), Ct);
        var token = await ReceiveUntilAsync(server, MainClientMessage.AgentToken);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(token.Payload));

        // Agente scollegato: niente più invii.
        await server.SendAsync(new SpiceMessage((ushort)MainServerMessage.AgentDisconnected, [0, 0, 0, 0]).Encode(), Ct);
        while (await main.Events.ReadAsync(Ct) is not MainEvent.AgentConnected { Connected: false })
        {
        }
        Assert.False(main.SendToAgent(new AgentMessage(AgentMessageType.Reply, [])));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}

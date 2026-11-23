using System.Buffers.Binary;
using SpiceKit.Transport;
using SpiceKit.Wire;

namespace SpiceKit.Tests.Support;

/// <summary>Restituisce 128 byte senza RSA vero: il "testo cifrato" è il ticket stesso.</summary>
public sealed class FakeEncryptor : ITicketEncryptor
{
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        var result = new byte[128];
        plaintext[..Math.Min(128, plaintext.Length)].CopyTo(result);
        return result;
    }
}

/// <summary>Messaggio di link ricevuto dal server finto.</summary>
public sealed record ReceivedLink(uint ConnectionId, ChannelType Type, byte Id, byte[] Ticket);

/// <summary>Lato server dell'handshake di link e dei messaggi con mini-header.</summary>
public static class FakeSpiceServer
{
    public static async Task<ReceivedLink> ServeLinkAsync(ISpiceTransport stream, LinkError linkError = LinkError.Ok,
        LinkError authResult = LinkError.Ok, bool offerMiniHeader = true)
    {
        var header = LinkHeader.Decode(await stream.ReceiveAsync(LinkHeader.Size));
        var body = await stream.ReceiveAsync((int)header.BodySize);
        var connectionId = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var type = (ChannelType)body[4];
        var id = body[5];

        var reply = new ByteWriter();
        reply.WriteU32((uint)linkError);
        reply.WriteBytes(new byte[SpiceConstants.TicketPublicKeyBytes]);
        reply.WriteU32(1);
        reply.WriteU32(0);
        reply.WriteU32(4 + 162 + 12);
        var caps = CapabilitySet.FromBits((int)CommonCapability.ProtocolAuthSelection, (int)CommonCapability.AuthSpice);
        if (offerMiniHeader) caps.Insert((int)CommonCapability.MiniHeader);
        reply.WriteU32(caps.Words[0]);
        var output = new ByteWriter();
        output.WriteU32(SpiceConstants.Magic);
        output.WriteU32(2);
        output.WriteU32(2);
        output.WriteU32((uint)reply.Count);
        output.WriteBytes(reply.WrittenSpan);
        await stream.SendAsync(output.ToArray());
        if (linkError != LinkError.Ok || !offerMiniHeader) return new ReceivedLink(connectionId, type, id, []);

        var mechanism = BinaryPrimitives.ReadUInt32LittleEndian(await stream.ReceiveAsync(4));
        Assert.Equal((uint)CommonCapability.AuthSpice, mechanism);
        var ticket = await stream.ReceiveAsync(128);
        var result = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)authResult);
        await stream.SendAsync(result);
        return new ReceivedLink(connectionId, type, id, ticket);
    }

    public static Task SendAsync(ISpiceTransport stream, ushort type, ReadOnlySpan<byte> payload) =>
        stream.SendAsync(new SpiceMessage(type, payload.ToArray()).Encode());

    public static async Task<SpiceMessage> ReceiveAsync(ISpiceTransport stream)
    {
        var header = MiniHeader.Decode(await stream.ReceiveAsync(MiniHeader.WireSize));
        return new SpiceMessage(header.Type, await stream.ReceiveAsync((int)header.Size));
    }

    /// <summary><c>SPICE_MSG_MAIN_INIT</c> con l'id di sessione dato.</summary>
    public static byte[] MainInit(uint sessionId, bool agent = false)
    {
        var writer = new ByteWriter();
        foreach (var value in new uint[] { sessionId, 1, 3, 1, agent ? 1u : 0u, 10, 12345, 0 }) writer.WriteU32(value);
        return writer.ToArray();
    }

    /// <summary><c>SPICE_MSG_MAIN_CHANNELS_LIST</c>.</summary>
    public static byte[] ChannelsList(params (ChannelType Type, byte Id)[] channels)
    {
        var writer = new ByteWriter();
        writer.WriteU32((uint)channels.Length);
        foreach (var (type, id) in channels)
        {
            writer.WriteU8((byte)type);
            writer.WriteU8(id);
        }
        return writer.ToArray();
    }
}

/// <summary>
/// Connettore in memoria: ogni connessione è passata a <paramref name="serve"/>, che fa la parte del server.
/// </summary>
public sealed class MemoryConnector(Func<MemoryTransport, SpiceRoute, Task> serve) : IStreamConnector
{
    private readonly List<Task> _servers = [];

    public List<SpiceRoute> Routes { get; } = [];

    public IReadOnlyList<Task> Servers
    {
        get
        {
            lock (_servers) return [.. _servers];
        }
    }

    public Task<ISpiceTransport> ConnectAsync(SpiceEndpoint endpoint, SpiceRoute route, CancellationToken cancellationToken)
    {
        var (client, server) = MemoryTransport.Pair();
        lock (_servers)
        {
            Routes.Add(route);
            _servers.Add(Task.Run(() => serve(server, route), CancellationToken.None));
        }
        return Task.FromResult<ISpiceTransport>(client);
    }
}

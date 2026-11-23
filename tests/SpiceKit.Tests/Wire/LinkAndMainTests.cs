using SpiceKit.Wire;

namespace SpiceKit.Tests.Wire;

/// <summary>Porting dei test di link e main di <c>SpiceWireTests.swift</c>.</summary>
public sealed class LinkAndMainTests
{
    [Fact]
    public void Magic_spells_REDQ()
    {
        var writer = new ByteWriter();
        writer.WriteU32(SpiceConstants.Magic);
        Assert.Equal("REDQ"u8.ToArray(), writer.ToArray());
    }

    [Fact]
    public void Capability_set()
    {
        var caps = CapabilitySet.FromBits(0, 3, 33);
        Assert.Equal([0b1001u, 0b10u], caps.Words);
        Assert.True(caps.Contains(3));
        Assert.False(caps.Contains(2));
        caps.Insert(2);
        Assert.True(caps.Contains(2));
        Assert.False(new CapabilitySet().Contains(0));
        Assert.False(caps.Contains(-1));
    }

    [Fact]
    public void Link_message_layout()
    {
        var message = new LinkMessage(0x1122_3344, ChannelType.Display, 1, CapabilitySet.FromBits(0, 1, 3), CapabilitySet.FromBits(5));
        var reader = new ByteReader(message.Encode());
        Assert.Equal(SpiceConstants.Magic, reader.ReadU32());
        Assert.Equal(2u, reader.ReadU32());
        Assert.Equal(2u, reader.ReadU32());
        Assert.Equal(18u + 8, reader.ReadU32()); // SpiceLinkMess + 2 parole di capacità
        Assert.Equal(0x1122_3344u, reader.ReadU32());
        Assert.Equal((byte)ChannelType.Display, reader.ReadU8());
        Assert.Equal(1, reader.ReadU8());
        Assert.Equal(1u, reader.ReadU32()); // num_common_caps
        Assert.Equal(1u, reader.ReadU32()); // num_channel_caps
        Assert.Equal(18u, reader.ReadU32()); // caps_offset
        Assert.Equal(0b1011u, reader.ReadU32());
        Assert.Equal(0b10_0000u, reader.ReadU32());
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void Link_reply_reads_caps_at_offset()
    {
        var writer = new ByteWriter();
        writer.WriteU32(0);
        writer.WriteBytes(Enumerable.Repeat((byte)0x42, 162).ToArray());
        writer.WriteU32(1);
        writer.WriteU32(1);
        writer.WriteU32(4 + 162 + 12);
        writer.WriteU32(0b1011);
        writer.WriteU32(0b1);
        var reply = LinkReply.Decode(writer.ToArray());
        Assert.Equal(LinkError.Ok, reply.LinkError);
        Assert.Equal(162, reply.PublicKey.Length);
        Assert.True(reply.CommonCaps.Contains(CommonCapability.MiniHeader));
        Assert.True(reply.ChannelCaps.Contains(0));
    }

    [Fact]
    public void Link_reply_rejects_hostile_caps_count()
    {
        var writer = new ByteWriter();
        writer.WriteU32(0);
        writer.WriteBytes(new byte[162]);
        writer.WriteU32(LinkReply.MaxCapWords + 1);
        writer.WriteU32(0);
        writer.WriteU32(4 + 162 + 12);
        var error = Assert.Throws<WireException>(() => LinkReply.Decode(writer.ToArray()));
        Assert.Equal(WireErrorKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public void Main_init_decoding()
    {
        var writer = new ByteWriter();
        foreach (var value in new uint[] { 7, 1, 3, 1, 1, 10, 12345, 0 }) writer.WriteU32(value);
        var message = MainInit.Decode(writer.ToArray());
        Assert.Equal(7u, message.SessionId);
        Assert.Equal(MouseModes.Server | MouseModes.Client, message.SupportedMouseModes);
        Assert.Equal(MouseModes.Server, message.CurrentMouseMode);
        Assert.True(message.AgentConnected);
        Assert.Equal(10u, message.AgentTokens);
    }

    [Fact]
    public void Channels_list_and_hostile_count()
    {
        var list = ChannelsList.Decode([3, 0, 0, 0, 2, 0, 3, 0, 9, 1]);
        Assert.Equal([new ChannelEntry(2, 0), new ChannelEntry(3, 0), new ChannelEntry(9, 1)], list);
        Assert.Equal("usbredir:1", list[2].ToString());
        Assert.Throws<WireException>(() => ChannelsList.Decode([0xFF, 0xFF, 0xFF, 0xFF, 1, 0]));
    }

    [Fact]
    public void Main_name_and_notify_strip_nul()
    {
        Assert.Equal("vm", MainDecoders.Name([3, 0, 0, 0, (byte)'v', (byte)'m', 0]));
        var notify = new ByteWriter();
        notify.WriteU64(1);
        notify.WriteU32(2);
        notify.WriteU32(0);
        notify.WriteU32(0);
        notify.WriteU32(100); // lunghezza dichiarata oltre la fine: si legge ciò che c'è
        notify.WriteBytes("ciao\0"u8);
        Assert.Equal("ciao", Notify.Decode(notify.ToArray()).Message);
    }

    [Fact]
    public void Sanitize_removes_control_characters_and_truncates()
    {
        Assert.Equal("a b", WireText.Sanitize("a\nb"));
        Assert.Equal(10, WireText.Sanitize(new string('x', 50), 10).Length);
    }

    [Fact]
    public void Mini_header_round_trip()
    {
        var bytes = new SpiceMessage(0x1234, [1, 2, 3]).Encode();
        Assert.Equal([0x34, 0x12, 3, 0, 0, 0, 1, 2, 3], bytes);
        Assert.Equal(new MiniHeader(0x1234, 3), MiniHeader.Decode(bytes));
    }

    /// <summary>Dati casuali non devono mai far cadere un decodificatore (solo WireException).</summary>
    [Fact]
    public void Fuzz_decoders()
    {
        var state = 0xC0FFEEUL;
        uint Next()
        {
            state = unchecked(state * 6_364_136_223_846_793_005UL + 1_442_695_040_888_963_407UL);
            return (uint)(state >> 33);
        }
        for (var i = 0; i < 3000; i++)
        {
            var bytes = new byte[Next() % 200];
            for (var j = 0; j < bytes.Length; j++) bytes[j] = (byte)Next();
            Try(() => LinkReply.Decode(bytes));
            Try(() => Notify.Decode(bytes));
            Try(() => ChannelsList.Decode(bytes));
            Try(() => MainInit.Decode(bytes));
            Try(() => MainDecoders.Name(bytes));
            Try(() => MainDecoders.Uuid(bytes));
            Try(() => SetAck.Decode(bytes));
        }

        static void Try(Func<object> decode)
        {
            try
            {
                decode();
            }
            catch (WireException)
            {
            }
        }
    }
}

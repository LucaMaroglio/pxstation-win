namespace SpiceKit.Wire;

// Messaggi del canale main (spice.proto, "channel MainChannel").

/// <summary><c>SPICE_MSG_MAIN_INIT</c>.</summary>
/// <param name="SessionId">Da usare come <c>connection_id</c> per collegare tutti gli altri canali.</param>
/// <param name="DisplayChannelsHint">Numero di schermi suggerito dal server.</param>
/// <param name="SupportedMouseModes">Modalità del mouse disponibili.</param>
/// <param name="CurrentMouseMode">Modalità del mouse attuale.</param>
/// <param name="AgentConnected">L'agente (spice-vdagent) è attivo nell'ospite.</param>
/// <param name="AgentTokens">Messaggi che si possono inviare all'agente.</param>
/// <param name="MultimediaTime">Orologio multimediale del server (ms).</param>
/// <param name="RamHint">Suggerimento sulla memoria per la cache delle immagini.</param>
public readonly record struct MainInit(uint SessionId, uint DisplayChannelsHint, MouseModes SupportedMouseModes,
    MouseModes CurrentMouseMode, bool AgentConnected, uint AgentTokens, uint MultimediaTime, uint RamHint)
{
    public static MainInit Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        return new MainInit(reader.ReadU32(), reader.ReadU32(), (MouseModes)reader.ReadU32(), (MouseModes)reader.ReadU32(),
            reader.ReadU32() != 0, reader.ReadU32(), reader.ReadU32(), reader.ReadU32());
    }
}

/// <summary>Una voce di <c>SPICE_MSG_MAIN_CHANNELS_LIST</c>.</summary>
public readonly record struct ChannelEntry(byte RawType, byte Id)
{
    public ChannelType Type => (ChannelType)RawType;

    public bool IsKnownType => Enum.IsDefined(Type);

    public override string ToString() => $"{Type.Name()}:{Id}";
}

/// <summary><c>SPICE_MSG_MAIN_CHANNELS_LIST</c>.</summary>
public static class ChannelsList
{
    public static IReadOnlyList<ChannelEntry> Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        var count = reader.ReadU32();
        if (count > (uint)reader.Remaining / 2) throw WireException.Truncated((int)Math.Min(count * 2L, int.MaxValue), reader.Remaining);
        var channels = new ChannelEntry[count];
        for (var i = 0; i < channels.Length; i++) channels[i] = new ChannelEntry(reader.ReadU8(), reader.ReadU8());
        return channels;
    }
}

/// <summary><c>SPICE_MSG_MAIN_MOUSE_MODE</c> (sul filo i campi sono flags16).</summary>
public readonly record struct MainMouseMode(MouseModes Supported, MouseModes Current)
{
    public static MainMouseMode Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        return new MainMouseMode((MouseModes)reader.ReadU16(), (MouseModes)reader.ReadU16());
    }
}

/// <summary>Messaggi del canale main con contenuto testuale o di lunghezza fissa.</summary>
public static class MainDecoders
{
    /// <summary><c>MULTI_MEDIA_TIME</c>, <c>AGENT_TOKEN</c>, <c>AGENT_CONNECTED_TOKENS</c>, <c>AGENT_DISCONNECTED</c>.</summary>
    public static uint UInt32(ReadOnlySpan<byte> bytes) => new ByteReader(bytes).ReadU32();

    /// <summary><c>SPICE_MSG_MAIN_NAME</c>.</summary>
    public static string Name(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        var length = reader.ReadU32();
        return WireText.Decode(reader.ReadBytes((int)Math.Min(length, (uint)reader.Remaining)));
    }

    /// <summary><c>SPICE_MSG_MAIN_UUID</c> (16 byte).</summary>
    public static Guid Uuid(ReadOnlySpan<byte> bytes) => new(new ByteReader(bytes).ReadBytes(16), bigEndian: true);
}

/// <summary>Messaggi del canale main dal client al server.</summary>
public static class MainClientEncoder
{
    public static SpiceMessage AttachChannels() => SpiceMessage.Empty((ushort)MainClientMessage.AttachChannels);

    /// <summary><c>SPICE_MSGC_MAIN_MOUSE_MODE_REQUEST</c> (flags16).</summary>
    public static SpiceMessage MouseModeRequest(MouseModes mode)
    {
        var writer = new ByteWriter(2);
        writer.WriteU16((ushort)mode);
        return new SpiceMessage((ushort)MainClientMessage.MouseModeRequest, writer.ToArray());
    }

    /// <summary><c>SPICE_MSGC_MAIN_AGENT_START</c>: quanti messaggi dell'agente possiamo tenere in coda.</summary>
    public static SpiceMessage AgentStart(uint tokens) => UInt32Message(MainClientMessage.AgentStart, tokens);

    /// <summary><c>SPICE_MSGC_MAIN_AGENT_TOKEN</c>: restituisce gettoni di ricezione al server.</summary>
    public static SpiceMessage AgentToken(uint tokens) => UInt32Message(MainClientMessage.AgentToken, tokens);

    /// <summary><c>SPICE_MSGC_MAIN_AGENT_DATA</c>: un pezzo (≤ 2048 byte) di un messaggio per l'agente.</summary>
    public static SpiceMessage AgentData(byte[] chunk) => new((ushort)MainClientMessage.AgentData, chunk);

    private static SpiceMessage UInt32Message(MainClientMessage type, uint value)
    {
        var writer = new ByteWriter(4);
        writer.WriteU32(value);
        return new SpiceMessage((ushort)type, writer.ToArray());
    }
}

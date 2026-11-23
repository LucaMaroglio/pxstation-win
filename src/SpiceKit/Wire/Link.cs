namespace SpiceKit.Wire;

/// <summary>Insieme di bit di capacità, trasmesso come array di uint32 little-endian.</summary>
public sealed class CapabilitySet : IEquatable<CapabilitySet>
{
    private readonly List<uint> _words;

    public CapabilitySet(IEnumerable<uint>? words = null) => _words = words is null ? [] : [.. words];

    public static CapabilitySet FromBits(params int[] bits)
    {
        var set = new CapabilitySet();
        foreach (var bit in bits) set.Insert(bit);
        return set;
    }

    public IReadOnlyList<uint> Words => _words;

    public void Insert(int bit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bit);
        var word = bit / 32;
        while (_words.Count <= word) _words.Add(0);
        _words[word] |= 1u << (bit % 32);
    }

    public bool Contains(int bit)
    {
        if (bit < 0) return false;
        var word = bit / 32;
        return word < _words.Count && (_words[word] & (1u << (bit % 32))) != 0;
    }

    public bool Contains(CommonCapability capability) => Contains((int)capability);

    public bool Equals(CapabilitySet? other) => other is not null && _words.SequenceEqual(other._words);

    public override bool Equals(object? obj) => Equals(obj as CapabilitySet);

    public override int GetHashCode() => _words.Aggregate(17, (hash, word) => hash * 31 + word.GetHashCode());
}

/// <summary><c>SpiceLinkHeader</c> + <c>SpiceLinkMess</c> (protocol.h), inviati dal client per aprire un canale.</summary>
public sealed record LinkMessage(uint ConnectionId, ChannelType ChannelType, byte ChannelId,
    CapabilitySet CommonCaps, CapabilitySet ChannelCaps)
{
    /// <summary><c>sizeof(SpiceLinkMess)</c>: u32 + u8 + u8 + u32 + u32 + u32.</summary>
    public const int FixedSize = 18;

    public byte[] Encode()
    {
        var capsSize = 4 * (CommonCaps.Words.Count + ChannelCaps.Words.Count);
        var writer = new ByteWriter(LinkHeader.Size + FixedSize + capsSize);
        // SpiceLinkHeader
        writer.WriteU32(SpiceConstants.Magic);
        writer.WriteU32(SpiceConstants.VersionMajor);
        writer.WriteU32(SpiceConstants.VersionMinor);
        writer.WriteU32((uint)(FixedSize + capsSize));
        // SpiceLinkMess
        writer.WriteU32(ConnectionId);
        writer.WriteU8((byte)ChannelType);
        writer.WriteU8(ChannelId);
        writer.WriteU32((uint)CommonCaps.Words.Count);
        writer.WriteU32((uint)ChannelCaps.Words.Count);
        writer.WriteU32(FixedSize); // caps_offset, relativo a SpiceLinkMess
        foreach (var word in CommonCaps.Words) writer.WriteU32(word);
        foreach (var word in ChannelCaps.Words) writer.WriteU32(word);
        return writer.ToArray();
    }
}

/// <summary><c>SpiceLinkHeader</c> ricevuto dal server.</summary>
public readonly record struct LinkHeader(uint Magic, uint Major, uint Minor, uint BodySize)
{
    public const int Size = 16;

    public static LinkHeader Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        return new LinkHeader(reader.ReadU32(), reader.ReadU32(), reader.ReadU32(), reader.ReadU32());
    }
}

/// <summary><c>SpiceLinkReply</c> (protocol.h).</summary>
public sealed record LinkReply(uint ErrorCode, byte[] PublicKey, CapabilitySet CommonCaps, CapabilitySet ChannelCaps)
{
    /// <summary>Array di capacità più lunghi di così sono rifiutati come ostili.</summary>
    public const uint MaxCapWords = 1024;

    public LinkError LinkError => (LinkError)ErrorCode;

    public static LinkReply Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        var error = reader.ReadU32();
        // Chiave RSA-1024 in formato X.509 SubjectPublicKeyInfo DER (162 byte).
        var key = reader.ReadBytes(SpiceConstants.TicketPublicKeyBytes).ToArray();
        var numCommon = reader.ReadU32();
        var numChannel = reader.ReadU32();
        var capsOffset = reader.ReadU32();
        if (numCommon > MaxCapWords || numChannel > MaxCapWords) throw WireException.LimitExceeded("capacità del link reply");
        if (capsOffset > (uint)bytes.Length) throw WireException.BadOffset(capsOffset);
        var caps = new ByteReader(bytes, (int)capsOffset);
        var common = new uint[numCommon];
        for (var i = 0; i < common.Length; i++) common[i] = caps.ReadU32();
        var channel = new uint[numChannel];
        for (var i = 0; i < channel.Length; i++) channel[i] = caps.ReadU32();
        return new LinkReply(error, key, new CapabilitySet(common), new CapabilitySet(channel));
    }
}

/// <summary><c>SpiceMiniDataHeader</c>: 6 byte, usato con <c>SPICE_COMMON_CAP_MINI_HEADER</c>.</summary>
public readonly record struct MiniHeader(ushort Type, uint Size)
{
    public const int WireSize = 6;

    public static MiniHeader Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        return new MiniHeader(reader.ReadU16(), reader.ReadU32());
    }

    public void Encode(Span<byte> destination)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(destination, Type);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[2..], Size);
    }
}

/// <summary>Un messaggio completo su un canale già stabilito.</summary>
public sealed record SpiceMessage(ushort Type, byte[] Payload)
{
    public static SpiceMessage Empty(ushort type) => new(type, []);

    /// <summary>Intestazione + contenuto, pronti da inviare.</summary>
    public byte[] Encode()
    {
        var bytes = new byte[MiniHeader.WireSize + Payload.Length];
        new MiniHeader(Type, (uint)Payload.Length).Encode(bytes);
        Payload.CopyTo(bytes, MiniHeader.WireSize);
        return bytes;
    }
}

// Costanti del protocollo trascritte da spice-protocol 0.14.3 (licenza BSD):
// spice/protocol.h e spice/enums.h. I valori devono coincidere esattamente con gli header.

namespace SpiceKit.Wire;

/// <summary>Costanti generali (protocol.h).</summary>
public static class SpiceConstants
{
    /// <summary><c>SPICE_MAGIC</c>: "REDQ" letto come uint32 little-endian.</summary>
    public const uint Magic = 0x5144_4552;

    /// <summary><c>SPICE_VERSION_MAJOR</c>.</summary>
    public const uint VersionMajor = 2;

    /// <summary><c>SPICE_VERSION_MINOR</c>.</summary>
    public const uint VersionMinor = 2;

    /// <summary><c>SPICE_TICKET_PUBKEY_BYTES</c> = 1024 / 8 + 34.</summary>
    public const int TicketPublicKeyBytes = 162;

    /// <summary>Dimensione del testo cifrato RSA-1024 (<c>SpiceLinkEncryptedTicket</c>).</summary>
    public const int EncryptedTicketBytes = 128;

    /// <summary><c>SPICE_MAX_PASSWORD_LENGTH</c>.</summary>
    public const int MaxPasswordLength = 60;

    /// <summary><c>SPICE_INPUT_MOTION_ACK_BUNCH</c>.</summary>
    public const int InputMotionAckBunch = 4;
}

/// <summary><c>SPICE_CHANNEL_*</c> (enums.h).</summary>
public enum ChannelType : byte
{
    Main = 1,
    Display,
    Inputs,
    Cursor,
    Playback,
    Record,
    Tunnel,
    Smartcard,
    Usbredir,
    Port,
    Webdav,
}

/// <summary><c>SpiceLinkErr</c> (enums.h).</summary>
public enum LinkError : uint
{
    Ok = 0,
    Error,
    InvalidMagic,
    InvalidData,
    VersionMismatch,
    NeedSecured,
    NeedUnsecured,
    PermissionDenied,
    BadConnectionId,
    ChannelNotAvailable,
}

/// <summary><c>SPICE_COMMON_CAP_*</c> (protocol.h): indici di bit.</summary>
public enum CommonCapability
{
    ProtocolAuthSelection = 0,
    AuthSpice = 1,
    AuthSasl = 2,
    MiniHeader = 3,
}

/// <summary><c>SPICE_MAIN_CAP_*</c> (protocol.h).</summary>
public enum MainCapability
{
    SemiSeamlessMigrate = 0,
    NameAndUuid = 1,
    AgentConnectedTokens = 2,
    SeamlessMigrate = 3,
}

/// <summary><c>SPICE_DISPLAY_CAP_*</c> (protocol.h).</summary>
public enum DisplayCapability
{
    SizedStream = 0,
    MonitorsConfig,
    Composite,
    A8Surface,
    StreamReport,
    Lz4Compression,
    PrefCompression,
    GlScanout,
    MultiCodec,
    CodecMjpeg,
    CodecVp8,
    CodecH264,
    PrefVideoCodecType,
    CodecVp9,
    CodecH265,
}

/// <summary><c>SPICE_INPUTS_CAP_*</c> (protocol.h).</summary>
public enum InputsCapability
{
    KeyScancode = 0,
}

/// <summary><c>SPICE_PLAYBACK_CAP_*</c> / <c>SPICE_RECORD_CAP_*</c> (protocol.h).</summary>
public enum PlaybackCapability
{
    Celt051 = 0,
    Volume,
    Latency,
    Opus,
}

/// <summary><c>SPICE_SPICEVMC_CAP_*</c> (protocol.h).</summary>
public enum SpiceVmcCapability
{
    DataCompressLz4 = 0,
}

/// <summary><c>SpiceMouseMode</c> (enums.h): flag.</summary>
[Flags]
public enum MouseModes : uint
{
    None = 0,
    Server = 1 << 0,
    Client = 1 << 1,
}

/// <summary><c>SpiceNotifySeverity</c> (enums.h).</summary>
public enum NotifySeverity : uint
{
    Info = 0,
    Warn,
    Error,
}

public static class ChannelTypes
{
    /// <summary>Nome del canale come in <c>secure-channels</c> e nei log.</summary>
    public static string Name(this ChannelType type) => type switch
    {
        ChannelType.Main => "main",
        ChannelType.Display => "display",
        ChannelType.Inputs => "inputs",
        ChannelType.Cursor => "cursor",
        ChannelType.Playback => "playback",
        ChannelType.Record => "record",
        ChannelType.Tunnel => "tunnel",
        ChannelType.Smartcard => "smartcard",
        ChannelType.Usbredir => "usbredir",
        ChannelType.Port => "port",
        ChannelType.Webdav => "webdav",
        _ => "canale " + ((byte)type).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

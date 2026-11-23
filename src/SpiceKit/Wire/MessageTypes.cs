// Numeri dei messaggi da spice-protocol enums.h (licenza BSD).
// Server → client: *ServerMessage; client → server: *ClientMessage.

namespace SpiceKit.Wire;

/// <summary><c>SPICE_MSG_*</c> comuni a tutti i canali.</summary>
public enum BaseServerMessage : ushort
{
    Migrate = 1,
    MigrateData,
    SetAck,
    Ping,
    WaitForChannels,
    Disconnecting,
    Notify,
    List,
}

/// <summary><c>SPICE_MSGC_*</c> comuni a tutti i canali.</summary>
public enum BaseClientMessage : ushort
{
    AckSync = 1,
    Ack,
    Pong,
    MigrateFlushMark,
    MigrateData,
    Disconnecting,
}

/// <summary><c>SPICE_MSG_MAIN_*</c>.</summary>
public enum MainServerMessage : ushort
{
    MigrateBegin = 101,
    MigrateCancel,
    Init,
    ChannelsList,
    MouseMode,
    MultiMediaTime,
    AgentConnected,
    AgentDisconnected,
    AgentData,
    AgentToken,
    MigrateSwitchHost,
    MigrateEnd,
    Name,
    Uuid,
    AgentConnectedTokens,
    MigrateBeginSeamless,
    MigrateDstSeamlessAck,
    MigrateDstSeamlessNack,
}

/// <summary><c>SPICE_MSGC_MAIN_*</c>.</summary>
public enum MainClientMessage : ushort
{
    ClientInfo = 101,
    MigrateConnected,
    MigrateConnectError,
    AttachChannels,
    MouseModeRequest,
    AgentStart,
    AgentData,
    AgentToken,
    MigrateEnd,
    MigrateDstDoSeamless,
    MigrateConnectedSeamless,
    QualityIndicator,
}

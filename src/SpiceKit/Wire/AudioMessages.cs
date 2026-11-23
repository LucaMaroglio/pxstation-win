// Canali playback e record (spice.proto: channel PlaybackChannel / RecordChannel; costanti da
// spice-protocol enums.h e protocol.h, licenza BSD).

namespace SpiceKit.Wire;

/// <summary><c>SpiceAudioDataMode</c>.</summary>
public enum AudioDataMode : ushort
{
    Invalid = 0,
    Raw = 1,

    /// <summary>Deprecato: questo client non lo annuncia mai.</summary>
    Celt051 = 2,
    Opus = 3,
}

/// <summary><c>SPICE_RECORD_CAP_*</c>.</summary>
public enum RecordCapability
{
    Celt051 = 0,
    Volume,
    Opus,
}

/// <summary><c>SPICE_MSG_PLAYBACK_*</c>.</summary>
public enum PlaybackServerMessage : ushort
{
    Data = 101,
    Mode,
    Start,
    Stop,
    Volume,
    Mute,
    Latency,
}

/// <summary><c>SPICE_MSG_RECORD_*</c>.</summary>
public enum RecordServerMessage : ushort
{
    Start = 101,
    Stop,
    Volume,
    Mute,
}

/// <summary><c>SPICE_MSGC_RECORD_*</c>.</summary>
public enum RecordClientMessage : ushort
{
    Data = 101,
    Mode,
    StartMark,
}

/// <summary>START di playback e record: canali, formato, frequenza (playback aggiunge il tempo).</summary>
public readonly record struct AudioStart(uint Channels, ushort Format, uint Frequency, uint? Time)
{
    /// <summary><c>SPICE_AUDIO_FMT_S16</c>: l'unico formato esistente (16 bit con segno, little-endian).</summary>
    public const ushort FormatS16 = 1;

    public static AudioStart Decode(ReadOnlySpan<byte> payload, bool hasTime)
    {
        var r = new ByteReader(payload);
        var start = new AudioStart(r.ReadU32(), r.ReadU16(), r.ReadU32(), hasTime ? r.ReadU32() : null);
        // Limiti contro server ostili: al massimo 8 canali, 8–192 kHz.
        if (start.Channels is < 1 or > 8 || start.Frequency is < 8000 or > 192_000)
        {
            throw WireException.InvalidValue($"audio {start.Channels} canali a {start.Frequency} Hz");
        }
        return start;
    }
}

/// <summary>PLAYBACK_DATA (tempo, dati) e PLAYBACK_MODE (tempo, modo, dati).</summary>
public readonly record struct AudioPacket(uint Time, AudioDataMode? Mode, ReadOnlyMemory<byte> Data)
{
    public static AudioPacket DecodeData(ReadOnlyMemory<byte> payload)
    {
        var r = new ByteReader(payload.Span);
        return new AudioPacket(r.ReadU32(), null, payload[4..]);
    }

    public static AudioPacket DecodeMode(ReadOnlyMemory<byte> payload)
    {
        var r = new ByteReader(payload.Span);
        var time = r.ReadU32();
        var mode = r.ReadU16();
        return new AudioPacket(time, mode is >= 1 and <= 3 ? (AudioDataMode)mode : AudioDataMode.Invalid, payload[6..]);
    }
}

public static class AudioDecoders
{
    /// <summary><c>SpiceMsgAudioVolume</c>: volume medio dei canali come 0…1.</summary>
    public static float Volume(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        var count = r.ReadU8();
        if (count == 0) return 1;
        long sum = 0;
        for (var i = 0; i < count; i++) sum += r.ReadU16();
        return sum / (float)count / 65535;
    }

    /// <summary><c>SpiceMsgAudioMute</c>.</summary>
    public static bool Mute(ReadOnlySpan<byte> payload) => new ByteReader(payload).ReadU8() != 0;
}

public static class RecordClientEncoder
{
    public static SpiceMessage Mode(AudioDataMode mode, uint time)
    {
        var w = new ByteWriter(6);
        w.WriteU32(time);
        w.WriteU16((ushort)mode);
        return new SpiceMessage((ushort)RecordClientMessage.Mode, w.ToArray());
    }

    public static SpiceMessage StartMark(uint time)
    {
        var w = new ByteWriter(4);
        w.WriteU32(time);
        return new SpiceMessage((ushort)RecordClientMessage.StartMark, w.ToArray());
    }

    public static SpiceMessage Data(ReadOnlySpan<byte> bytes, uint time)
    {
        var w = new ByteWriter(4 + bytes.Length);
        w.WriteU32(time);
        w.WriteBytes(bytes);
        return new SpiceMessage((ushort)RecordClientMessage.Data, w.ToArray());
    }
}

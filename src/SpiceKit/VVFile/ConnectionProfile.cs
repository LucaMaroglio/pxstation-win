using System.Globalization;
using SpiceKit.UsbRedirWire;

namespace SpiceKit.VVFile;

/// <summary>Nomi dei canali SPICE usati dalla voce <c>secure-channels</c>.</summary>
public enum ChannelName
{
    Main,
    Display,
    Inputs,
    Cursor,
    Playback,
    Record,
    Smartcard,
    Usbredir,
    Port,
    Webdav,
}

/// <summary>Insieme di <see cref="ChannelName"/> (un bit per canale).</summary>
[Flags]
public enum ChannelMask
{
    None = 0,
    Main = 1 << ChannelName.Main,
    Display = 1 << ChannelName.Display,
    Inputs = 1 << ChannelName.Inputs,
    Cursor = 1 << ChannelName.Cursor,
    Playback = 1 << ChannelName.Playback,
    Record = 1 << ChannelName.Record,
    Smartcard = 1 << ChannelName.Smartcard,
    Usbredir = 1 << ChannelName.Usbredir,
    Port = 1 << ChannelName.Port,
    Webdav = 1 << ChannelName.Webdav,
    All = Main | Display | Inputs | Cursor | Playback | Record | Smartcard | Usbredir | Port | Webdav,
}

public static class ChannelNames
{
    public static ChannelMask ToMask(this ChannelName name) => (ChannelMask)(1 << (int)name);

    public static string ToVVName(this ChannelName name) => name.ToString().ToLowerInvariant();

    public static bool TryParse(string text, out ChannelName name)
    {
        foreach (var candidate in Enum.GetValues<ChannelName>())
        {
            if (candidate.ToVVName() == text)
            {
                name = candidate;
                return true;
            }
        }
        name = default;
        return false;
    }
}

public enum SecureChannelsKind
{
    /// <summary>Voce assente: nessun requisito per canale.</summary>
    Unspecified,
    All,
    Some,
}

/// <summary>Quali canali devono usare TLS.</summary>
public readonly record struct SecureChannels(SecureChannelsKind Kind, ChannelMask Channels)
{
    public static SecureChannels Unspecified => new(SecureChannelsKind.Unspecified, ChannelMask.None);
    public static SecureChannels All => new(SecureChannelsKind.All, ChannelMask.All);
    public static SecureChannels Some(ChannelMask channels) => new(SecureChannelsKind.Some, channels);

    public bool RequiresTls(ChannelName channel) => Kind switch
    {
        SecureChannelsKind.All => true,
        SecureChannelsKind.Some => (Channels & channel.ToMask()) != 0,
        _ => false,
    };
}

/// <summary>Proxy HTTP CONNECT indicato dalla voce <c>proxy</c>.</summary>
public readonly record struct ProxyEndpoint(string Host, ushort Port);

public enum PlaintextExposureKind
{
    /// <summary>Ogni canale usa TLS (oppure viene rifiutato, mai declassato).</summary>
    Encrypted,
    /// <summary>Entrambe le porte, ma <c>secure-channels</c> elenca solo alcuni canali.</summary>
    Partial,
    /// <summary>Nessuna porta TLS: niente è cifrato né autenticato.</summary>
    Unencrypted,
}

/// <summary>Quanta parte della sessione può viaggiare senza TLS.</summary>
public readonly record struct PlaintextExposure(PlaintextExposureKind Kind, ChannelMask PlainChannels);

/// <summary>Tutto ciò che serve per aprire una sessione SPICE, letto da un file <c>.vv</c>.</summary>
public sealed class ConnectionProfile(string host)
{
    /// <summary>
    /// Host da contattare. Con Proxmox è un indirizzo opaco per il proxy, come
    /// <c>pvespiceproxy:6527b3e1:101:pve1::abc…</c>, da passare così com'è nel CONNECT.
    /// </summary>
    public string Host { get; set; } = host;
    public ushort? Port { get; set; }
    public ushort? TlsPort { get; set; }

    /// <summary>Ticket monouso: solo in memoria, mai nei log.</summary>
    public SecretTicket? Password { get; set; }
    public ProxyEndpoint? Proxy { get; set; }

    /// <summary>Certificati PEM da usare come <b>unica</b> radice di fiducia (<c>\n</c> già convertiti).</summary>
    public string? CaPem { get; set; }

    /// <summary>Soggetto atteso del certificato del server, es. <c>OU=…,O=…,CN=pve1.example.com</c>.</summary>
    public string? HostSubject { get; set; }
    public SecureChannels SecureChannels { get; set; } = SecureChannels.Unspecified;
    public string? Title { get; set; }
    public bool Fullscreen { get; set; }
    public bool DeleteThisFile { get; set; }

    public Hotkey? ToggleFullscreenHotkey { get; set; }
    public Hotkey? ReleaseCursorHotkey { get; set; }
    public Hotkey? SecureAttentionHotkey { get; set; }

    /// <summary><see langword="null"/> = voce assente (attivo se il server offre canali usbredir).</summary>
    public bool? EnableUsbRedirection { get; set; }
    public bool EnableUsbAutoshare { get; set; }
    public UsbFilter? UsbFilter { get; set; }

    /// <summary><see langword="null"/> = voce assente; <see langword="false"/> nasconde la smartcard.</summary>
    public bool? EnableSmartcard { get; set; }

    // Letti e conservati, ma non usati nella prima versione.
    public int? ColorDepth { get; set; }
    public IReadOnlyList<string> DisableEffects { get; set; } = [];
    public string? Versions { get; set; }
    public string? NewerVersionUrl { get; set; }

    /// <summary>Voci di <c>[virt-viewer]</c> non gestite, per la diagnostica. Mai <c>password</c>.</summary>
    public Dictionary<string, string> UnknownKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Titolo della finestra di sessione.</summary>
    public string DisplayTitle => string.IsNullOrEmpty(Title) ? "SPICE" : Title;

    /// <summary>Quali canali possono passare in chiaro con questo profilo.</summary>
    public PlaintextExposure PlaintextExposure
    {
        get
        {
            if (Port is null) return new(PlaintextExposureKind.Encrypted, ChannelMask.None);
            if (TlsPort is null)
            {
                // I canali elencati in secure-channels vengono rifiutati; gli altri sono in chiaro.
                return SecureChannels.Kind == SecureChannelsKind.All
                    ? new(PlaintextExposureKind.Encrypted, ChannelMask.None)
                    : new(PlaintextExposureKind.Unencrypted, ChannelMask.All);
            }
            if (SecureChannels.Kind != SecureChannelsKind.Some) return new(PlaintextExposureKind.Encrypted, ChannelMask.None);
            // "tunnel" non ha un nome .vv, quindi nemmeno un elenco completo prova la copertura
            // totale; è obsoleto e questo client non lo apre mai.
            var plain = ChannelMask.All & ~SecureChannels.Channels;
            return plain == ChannelMask.None
                ? new(PlaintextExposureKind.Encrypted, ChannelMask.None)
                : new(PlaintextExposureKind.Partial, plain);
        }
    }

    /// <summary>Riepilogo senza segreti (niente ticket né CA), adatto ai log.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"ConnectionProfile(port: {Port?.ToString(CultureInfo.InvariantCulture) ?? "-"}, tlsPort: {TlsPort?.ToString(CultureInfo.InvariantCulture) ?? "-"}, " +
        $"proxy: {Proxy is not null}, ca: {CaPem is not null}, hostSubject: {HostSubject is not null}, ticket: {Password is not null})");
}

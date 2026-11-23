using System.Text;
using SpiceKit.Tests.Support;
using SpiceKit.UsbRedirWire;
using SpiceKit.VVFile;

namespace SpiceKit.Tests.VVFile;

public class VVFileParserTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Fixtures.PathOf("vv", name));

    private static string Ticket(SecretTicket ticket) =>
        ticket.WithBytes((bytes, _) => Encoding.UTF8.GetString(bytes));

    [Fact]
    public void ProxmoxFile()
    {
        var file = VVFileParser.Parse(Fixture("proxmox.vv"));
        var profile = file.Profile;
        Assert.Equal("pvespiceproxy:00000000:101:pve-test::0000000000000000000000000000000000000000", profile.Host);
        Assert.Null(profile.Port);
        Assert.Equal((ushort)61000, profile.TlsPort);
        Assert.Equal(new ProxyEndpoint("pve-test.example.invalid", 3128), profile.Proxy);
        Assert.Equal("OU=PVE Cluster Node,O=Proxmox Virtual Environment,CN=pve-test.example.invalid", profile.HostSubject);
        Assert.Equal("-----BEGIN CERTIFICATE-----\nTUlJRkFLRUZBS0VGQUtF\n-----END CERTIFICATE-----\n", profile.CaPem);
        Assert.True(profile.DeleteThisFile);
        Assert.Equal("VM 101 - test-vm", profile.Title);
        Assert.Equal(new Hotkey(HotkeyModifiers.Shift, "F11"), profile.ToggleFullscreenHotkey);
        Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "R"), profile.ReleaseCursorHotkey);
        Assert.Equal(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Insert"), profile.SecureAttentionHotkey);
        Assert.True(profile.SecureChannels.RequiresTls(ChannelName.Main));
        Assert.True(profile.SecureChannels.RequiresTls(ChannelName.Usbredir));
        Assert.False(profile.SecureChannels.RequiresTls(ChannelName.Webdav));
        Assert.NotNull(profile.Password);
        Assert.Equal("not-a-real-ticket-0123456789", Ticket(profile.Password));
        Assert.Empty(file.Warnings);
    }

    [Fact]
    public void TicketIsRedactedAndWipeable()
    {
        var profile = VVFileParser.Parse(Fixture("proxmox.vv")).Profile;
        var ticket = profile.Password;
        Assert.NotNull(ticket);
        Assert.DoesNotContain("ticket-0123", ticket.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ticket-0123", profile.ToString(), StringComparison.Ordinal);
        ticket.Wipe();
        Assert.True(ticket.IsWiped);
        Assert.False(ticket.WithBytes((_, present) => present));
    }

    [Fact]
    public void PlainQemu()
    {
        var profile = VVFileParser.Parse(Fixture("plain-qemu.vv")).Profile;
        Assert.Equal("127.0.0.1", profile.Host);
        Assert.Equal((ushort)5930, profile.Port);
        Assert.Null(profile.TlsPort);
        Assert.Null(profile.Password);
        Assert.False(profile.Fullscreen);
        Assert.Equal(SecureChannels.Unspecified, profile.SecureChannels);
        Assert.Equal("SPICE", profile.DisplayTitle);
    }

    [Fact]
    public void CrlfBomDuplicatesUnknownKeys()
    {
        var file = VVFileParser.Parse(Fixture("crlf-bom.vv"));
        var profile = file.Profile;
        Assert.Equal("example.invalid", profile.Host);
        Assert.Equal((ushort)5900, profile.Port);
        Assert.Equal((ushort)5901, profile.TlsPort);
        Assert.Equal("second", profile.Title);
        Assert.Equal("whatever", profile.UnknownKeys["future-key"]);
        Assert.True(profile.EnableUsbRedirection);
        Assert.True(profile.EnableUsbAutoshare);
        Assert.Equal(24, profile.ColorDepth);
        Assert.Equal(["wallpaper", "font-smooth"], profile.DisableEffects);
        Assert.Equal("rhev-win64:2.0-160;rhev-win32:2.0-160", profile.Versions);
        Assert.Contains(new VVFileWarning.DuplicateKey("title", 9), file.Warnings);
        Assert.Contains(new VVFileWarning.UnknownKey("future-key"), file.Warnings);

        var filter = profile.UsbFilter;
        Assert.NotNull(filter);
        Assert.Equal(3, filter.Rules.Count);
        var hid = new UsbFilter.Device(new HashSet<byte> { 0x03 }, 0x046D, 0xC52B, 0x1200);
        var sandisk = new UsbFilter.Device(new HashSet<byte> { 0x08 }, 0x0781, 0x5581, 0x0100);
        var other = new UsbFilter.Device(new HashSet<byte> { 0x08 }, 0x1234, 0x0001, 0x0100);
        Assert.False(filter.Allows(hid));
        Assert.True(filter.Allows(sandisk));
        Assert.False(filter.Allows(other));
    }

    [Fact]
    public void OvirtSectionIgnoredAndGtkHotkeys()
    {
        var profile = VVFileParser.Parse(Fixture("ovirt.vv")).Profile;
        Assert.Equal("192.0.2.10", profile.Host);
        Assert.Null(profile.Port); // port=-1 vuol dire "non impostata"
        Assert.Equal((ushort)5901, profile.TlsPort);
        Assert.Equal(SecureChannels.All, profile.SecureChannels);
        Assert.Equal(new Hotkey(HotkeyModifiers.Shift, "F12"), profile.ReleaseCursorHotkey);
        Assert.Empty(profile.UnknownKeys); // le chiavi di [ovirt] non finiscono nel profilo
    }

    [Fact]
    public void RejectsOtherProtocols()
    {
        var error = Assert.Throws<VVFileException>(() => VVFileParser.Parse(Fixture("vnc.vv")));
        Assert.Equal(VVFileErrorKind.UnsupportedProtocol, error.Kind);
        Assert.Equal("vnc", error.Detail);
    }

    [Theory]
    [InlineData("", VVFileErrorKind.MissingVirtViewerSection, null)]
    [InlineData("[other]\ntype=spice\n", VVFileErrorKind.MissingVirtViewerSection, null)]
    [InlineData("[virt-viewer]\nhost=a\nport=1\n", VVFileErrorKind.MissingKey, "type")]
    [InlineData("[virt-viewer]\ntype=spice\nport=1\n", VVFileErrorKind.MissingKey, "host")]
    [InlineData("[virt-viewer]\ntype=spice\nhost=a\n", VVFileErrorKind.NoPort, null)]
    [InlineData("[virt-viewer]\ntype=spice\nhost=a\nport=99999\n", VVFileErrorKind.InvalidValue, "port")]
    [InlineData("[virt-viewer]\ntype=spice\nhost=a\nport=1\nproxy=socks5://p:1\n", VVFileErrorKind.InvalidValue, "proxy")]
    // Audit 8: CR/LF o spazi con escape nell'host arriverebbero nella riga CONNECT.
    [InlineData("[virt-viewer]\ntype=spice\nhost=a\\r\\nX-Evil: 1\nport=1\n", VVFileErrorKind.InvalidValue, "host")]
    [InlineData("[virt-viewer]\ntype=spice\nhost=a\\sb\nport=1\n", VVFileErrorKind.InvalidValue, "host")]
    [InlineData("[virt-viewer]\ntype=spice\nhost=a\nport=1\nproxy=http://p\\r\\nX:1:3128\n", VVFileErrorKind.InvalidValue, "proxy")]
    // Riaudit 1: un secure-channels errato o vuoto non deve declassare i canali in silenzio.
    [InlineData("[virt-viewer]\ntype=spice\nhost=a\nport=1\ntls-port=2\nsecure-channels=main;dispay\n", VVFileErrorKind.InvalidValue, "secure-channels")]
    [InlineData("[virt-viewer]\ntype=spice\nhost=a\nport=1\ntls-port=2\nsecure-channels=;\n", VVFileErrorKind.InvalidValue, "secure-channels")]
    public void Errors(string input, VVFileErrorKind kind, string? detail)
    {
        var error = Assert.Throws<VVFileException>(() => VVFileParser.Parse(input));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(detail, error.Detail);
    }

    /// <summary>Audit 7: un usb-filter illeggibile disattiva il reindirizzamento invece di consentire tutto.</summary>
    [Fact]
    public void InvalidUsbFilterDisablesRedirection()
    {
        var file = VVFileParser.Parse("[virt-viewer]\ntype=spice\nhost=h\nport=1\nusb-filter=not,a,filter\n");
        Assert.Null(file.Profile.UsbFilter);
        Assert.False(file.Profile.EnableUsbRedirection);
        Assert.Contains(new VVFileWarning.InvalidOptionalValue("usb-filter"), file.Warnings);
    }

    [Fact]
    public void RejectsInvalidUtf8()
    {
        var error = Assert.Throws<VVFileException>(() => VVFileParser.Parse((ReadOnlySpan<byte>)[0x5B, 0xFF, 0xFE, 0x5D]));
        Assert.Equal(VVFileErrorKind.NotUtf8, error.Kind);
    }

    [Fact]
    public void RejectsHugeFiles()
    {
        var error = Assert.Throws<VVFileException>(() => VVFileParser.Parse(new byte[VVFileParser.MaxFileBytes + 1]));
        Assert.Equal(VVFileErrorKind.TooLarge, error.Kind);
    }

    [Fact]
    public void MalformedLinesAreWarnings()
    {
        var file = VVFileParser.Parse("garbage\n[virt-viewer]\ntype=spice\nnot a pair\nhost=h\nport=1\n[broken\n");
        Assert.Equal("h", file.Profile.Host);
        Assert.Contains(new VVFileWarning.MalformedLine(1), file.Warnings);
        Assert.Contains(new VVFileWarning.MalformedLine(4), file.Warnings);
        Assert.Contains(new VVFileWarning.MalformedLine(7), file.Warnings);
    }

    [Fact]
    public void Unescape()
    {
        var (value, ok) = VVFileParser.Unescape("a\\nb\\tc\\sd\\\\e");
        Assert.Equal("a\nb\tc d\\e", value);
        Assert.True(ok);
        Assert.False(VVFileParser.Unescape("bad\\q").Ok);
        Assert.False(VVFileParser.Unescape("trailing\\").Ok);
    }

    [Theory]
    [InlineData("http://proxy.example:3128", "proxy.example", 3128)]
    [InlineData("http://proxy.example", "proxy.example", 3128)]
    [InlineData("proxy.example:8080", "proxy.example", 8080)]
    [InlineData("http://user:pw@proxy.example:81/", "proxy.example", 81)]
    [InlineData("http://[2001:db8::1]:3128", "2001:db8::1", 3128)]
    public void Proxies(string input, string host, int port) =>
        Assert.Equal(new ProxyEndpoint(host, (ushort)port), VVFileParser.ParseProxy(input));

    [Theory]
    [InlineData("Ctrl+Alt+Del", HotkeyModifiers.Control | HotkeyModifiers.Alt, "Delete")]
    [InlineData("shift+f11", HotkeyModifiers.Shift, "F11")]
    [InlineData("<Control><Alt>r", HotkeyModifiers.Control | HotkeyModifiers.Alt, "R")]
    [InlineData("Super+PgUp", HotkeyModifiers.Super, "PageUp")]
    public void Hotkeys(string input, HotkeyModifiers modifiers, string key) =>
        Assert.Equal(new Hotkey(modifiers, key), Hotkey.Parse(input));

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyperdrive+R")]
    [InlineData("Ctrl+Alt+NoSuchKey")]
    [InlineData("F25")]
    public void InvalidHotkeys(string input) => Assert.Null(Hotkey.Parse(input));

    /// <summary>Fuzzing deterministico: mutazioni casuali delle fixture non devono mai causare crash.</summary>
    [Fact]
    public void FuzzMutationsNeverCrash()
    {
        var seeds = new[] { "proxmox.vv", "plain-qemu.vv", "crlf-bom.vv", "ovirt.vv" }.Select(Fixture).ToArray();
        var rng = new SplitMix64(0x5EED);
        for (var round = 0; round < 4000; round++)
        {
            var bytes = seeds[(int)(rng.Next() % (ulong)seeds.Length)].ToList();
            var mutations = 1 + (int)(rng.Next() % 8);
            for (var m = 0; m < mutations && bytes.Count > 0; m++)
            {
                var index = (int)(rng.Next() % (ulong)bytes.Count);
                switch (rng.Next() % 4)
                {
                    case 0: bytes[index] = (byte)rng.Next(); break;
                    case 1: bytes.RemoveAt(index); break;
                    case 2: bytes.Insert(index, (byte)rng.Next()); break;
                    default: bytes.InsertRange(index, [0x5C, 0x0A, 0x3D, 0x5B]); break;
                }
            }
            try
            {
                VVFileParser.Parse(bytes.ToArray());
            }
            catch (VVFileException)
            {
                // atteso
            }
        }
    }

    /// <summary>Audit: l'avviso "in chiaro" deve coprire anche le sessioni cifrate in parte.</summary>
    [Fact]
    public void PlaintextExposure()
    {
        var profile = new ConnectionProfile("h") { Port = 5900 };
        Assert.Equal(PlaintextExposureKind.Unencrypted, profile.PlaintextExposure.Kind);
        profile.SecureChannels = SecureChannels.All;
        Assert.Equal(PlaintextExposureKind.Encrypted, profile.PlaintextExposure.Kind); // rifiutato, mai declassato

        profile.TlsPort = 5901;
        profile.SecureChannels = SecureChannels.Unspecified;
        Assert.Equal(PlaintextExposureKind.Encrypted, profile.PlaintextExposure.Kind);
        profile.SecureChannels = SecureChannels.Some(ChannelMask.Main | ChannelMask.Inputs);
        var exposure = profile.PlaintextExposure;
        Assert.Equal(PlaintextExposureKind.Partial, exposure.Kind);
        Assert.True(exposure.PlainChannels.HasFlag(ChannelMask.Display));
        Assert.False(exposure.PlainChannels.HasFlag(ChannelMask.Main));
        Assert.False(exposure.PlainChannels.HasFlag(ChannelMask.Inputs));
        profile.SecureChannels = SecureChannels.Some(ChannelMask.All);
        Assert.Equal(PlaintextExposureKind.Encrypted, profile.PlaintextExposure.Kind);

        profile.Port = null;
        profile.SecureChannels = SecureChannels.Some(ChannelMask.Main);
        Assert.Equal(PlaintextExposureKind.Encrypted, profile.PlaintextExposure.Kind);
    }
}

internal struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        _state += 0x9E37_79B9_7F4A_7C15;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9;
        z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EB;
        return z ^ (z >> 31);
    }
}

using System.Globalization;
using System.Text;
using SpiceKit.UsbRedirWire;

namespace SpiceKit.VVFile;

/// <summary>Risultato della lettura di un file <c>.vv</c>.</summary>
public sealed record VVFile(ConnectionProfile Profile, IReadOnlyList<VVFileWarning> Warnings);

/// <summary>
/// Lettore dei file di connessione virt-viewer (INI in stile GKeyFile).
/// </summary>
/// <remarks>
/// Tollera BOM UTF-8, fine riga CRLF, commenti <c>#</c>/<c>;</c>, righe vuote, chiavi duplicate
/// (vince l'ultima), chiavi e sezioni sconosciute. Porting di <c>VVFileParser.swift</c>.
/// </remarks>
public static class VVFileParser
{
    /// <summary>Dimensione massima accettata per un file <c>.vv</c>.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Legge un file dal disco (al massimo <see cref="MaxFileBytes"/>).</summary>
    public static VVFile ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxFileBytes) throw new VVFileException(VVFileErrorKind.TooLarge);
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return Parse(buffer);
    }

    public static VVFile Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new VVFileException(VVFileErrorKind.TooLarge);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new VVFileException(VVFileErrorKind.NotUtf8);
        }
        return Parse(text);
    }

    public static VVFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var warnings = new List<VVFileWarning>();
        var sawVirtViewer = false;
        string? currentSection = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        var body = text.StartsWith('﻿') ? text.AsSpan(1) : text.AsSpan();
        var lineNumber = 0;
        foreach (var range in SplitLines(body))
        {
            lineNumber++;
            var line = body[range];
            var trimmed = line.Trim();
            if (trimmed.IsEmpty || trimmed[0] is '#' or ';') continue;

            if (trimmed[0] == '[')
            {
                if (trimmed.Length < 2 || trimmed[^1] != ']')
                {
                    warnings.Add(new VVFileWarning.MalformedLine(lineNumber));
                    continue;
                }
                currentSection = trimmed[1..^1].ToString();
                if (currentSection == "virt-viewer") sawVirtViewer = true;
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals < 0)
            {
                warnings.Add(new VVFileWarning.MalformedLine(lineNumber));
                continue;
            }
            if (currentSection is null)
            {
                warnings.Add(new VVFileWarning.KeyOutsideSection(lineNumber));
                continue;
            }
            if (currentSection != "virt-viewer") continue; // es. [ovirt]: ignorata

            var key = line[..equals].Trim().ToString();
            var value = line[(equals + 1)..].TrimStart(" \t");
            if (key.Length == 0)
            {
                warnings.Add(new VVFileWarning.MalformedLine(lineNumber));
                continue;
            }
            if (values.ContainsKey(key)) warnings.Add(new VVFileWarning.DuplicateKey(key, lineNumber));
            values[key] = value.ToString();
        }

        if (!sawVirtViewer) throw new VVFileException(VVFileErrorKind.MissingVirtViewerSection);

        var reader = new ValueReader(values);
        var profile = reader.BuildProfile();
        warnings.AddRange(reader.Warnings);
        return new VVFile(profile, warnings);
    }

    /// <summary>Divide su <c>\n</c>, <c>\r\n</c> e <c>\r</c>, mantenendo le righe vuote.</summary>
    private static List<Range> SplitLines(ReadOnlySpan<char> text)
    {
        var lines = new List<Range>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r')) continue;
            lines.Add(start..i);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add(start..text.Length);
        return lines;
    }

    /// <summary>Stesso insieme di caratteri di <c>HTTPConnect.isSafeTarget</c> su macOS.</summary>
    public static bool IsSafeHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.Length == 0 || host.Length > 1024) return false;
        foreach (var c in host)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || "-._~:[]%".Contains(c, StringComparison.Ordinal))) return false;
        }
        return true;
    }

    /// <summary>Accetta <c>http://host:porta</c>, <c>http://host</c> (porta 3128) e <c>host:porta</c>.</summary>
    internal static ProxyEndpoint? ParseProxy(string text)
    {
        var rest = text.AsSpan();
        var schemeEnd = rest.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            if (!rest[..schemeEnd].Equals("http", StringComparison.OrdinalIgnoreCase)) return null;
            rest = rest[(schemeEnd + 3)..];
        }
        var slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest[..slash];
        var at = rest.LastIndexOf('@');
        if (at >= 0) rest = rest[(at + 1)..]; // scarta utente e password
        if (rest.IsEmpty) return null;

        var host = rest;
        ushort port = 3128;
        if (rest[0] == '[') // [IPv6]:porta
        {
            var close = rest.IndexOf(']');
            if (close < 0) return null;
            host = rest[1..close];
            var after = rest[(close + 1)..];
            if (after.StartsWith(":"))
            {
                if (!TryParsePort(after[1..], out port)) return null;
            }
            else if (!after.IsEmpty)
            {
                return null;
            }
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            if (colon >= 0)
            {
                host = rest[..colon];
                if (!TryParsePort(rest[(colon + 1)..], out port)) return null;
            }
        }
        if (host.IsEmpty) return null;
        return new ProxyEndpoint(host.ToString(), port);
    }

    private static bool TryParsePort(ReadOnlySpan<char> text, out ushort port) =>
        ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port > 0;

    /// <summary>Sequenze GKeyFile: <c>\s</c> <c>\n</c> <c>\t</c> <c>\r</c> <c>\\</c>. Le altre restano letterali.</summary>
    internal static (string Value, bool Ok) Unescape(string raw)
    {
        if (!raw.Contains('\\', StringComparison.Ordinal)) return (raw, true);
        var result = new StringBuilder(raw.Length);
        var ok = true;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '\\')
            {
                result.Append(c);
                continue;
            }
            if (i + 1 >= raw.Length)
            {
                ok = false;
                result.Append('\\');
                break;
            }
            var next = raw[++i];
            switch (next)
            {
                case 'n': result.Append('\n'); break;
                case 't': result.Append('\t'); break;
                case 'r': result.Append('\r'); break;
                case 's': result.Append(' '); break;
                case '\\': result.Append('\\'); break;
                default:
                    ok = false;
                    result.Append('\\').Append(next);
                    break;
            }
        }
        return (result.ToString(), ok);
    }

    /// <summary>
    /// Rigido: un'impostazione di sicurezza non compresa del tutto non deve diventare più debole,
    /// quindi nomi sconosciuti o un elenco vuoto rifiutano il file (<see langword="null"/>).
    /// </summary>
    internal static SecureChannels? ParseSecureChannels(string text)
    {
        var mask = ChannelMask.None;
        var sawAll = false;
        foreach (var token in text.Split([';', ',']))
        {
            var name = TrimHorizontal(token).ToLowerInvariant();
            if (name.Length == 0) continue;
            if (name == "all") sawAll = true;
            else if (ChannelNames.TryParse(name, out var channel)) mask |= channel.ToMask();
            else return null;
        }
        if (sawAll) return SecureChannels.All;
        return mask == ChannelMask.None ? null : SecureChannels.Some(mask);
    }

    /// <summary>Toglie spazi e tabulazioni (non i ritorni a capo), come <c>.whitespaces</c> in Swift.</summary>
    internal static string TrimHorizontal(string text)
    {
        static bool IsHorizontal(char c) => c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        var start = 0;
        var end = text.Length;
        while (start < end && IsHorizontal(text[start])) start++;
        while (end > start && IsHorizontal(text[end - 1])) end--;
        return text[start..end];
    }

    /// <summary>Accesso tipizzato alle coppie chiave/valore, con raccolta degli avvisi.</summary>
    private sealed class ValueReader(Dictionary<string, string> values)
    {
        private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

        public List<VVFileWarning> Warnings { get; } = [];

        private string? String(string key)
        {
            _consumed.Add(key);
            if (!values.TryGetValue(key, out var raw)) return null;
            var (value, ok) = Unescape(raw);
            if (!ok) Warnings.Add(new VVFileWarning.InvalidEscape(key));
            return value;
        }

        private ushort? RequiredPort(string key)
        {
            var text = String(key) is { } s ? TrimHorizontal(s) : null;
            if (string.IsNullOrEmpty(text)) return null;
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value > 65535)
            {
                throw new VVFileException(VVFileErrorKind.InvalidValue, key);
            }
            // I generatori scrivono port=-1 o 0 per dire "non impostata".
            return value > 0 ? (ushort)value : null;
        }

        private bool? Bool(string key)
        {
            if (String(key) is not { } s) return null;
            switch (TrimHorizontal(s).ToLowerInvariant())
            {
                case "1" or "true" or "yes": return true;
                case "0" or "false" or "no": return false;
                default:
                    Warnings.Add(new VVFileWarning.InvalidOptionalValue(key));
                    return null;
            }
        }

        private Hotkey? HotkeyValue(string key)
        {
            var text = String(key);
            if (string.IsNullOrEmpty(text)) return null;
            var hotkey = Hotkey.Parse(text);
            if (hotkey is null) Warnings.Add(new VVFileWarning.InvalidOptionalValue(key));
            return hotkey;
        }

        public ConnectionProfile BuildProfile()
        {
            var type = String("type") is { } t ? TrimHorizontal(t) : null;
            if (string.IsNullOrEmpty(type)) throw new VVFileException(VVFileErrorKind.MissingKey, "type");
            if (!type.Equals("spice", StringComparison.OrdinalIgnoreCase))
            {
                throw new VVFileException(VVFileErrorKind.UnsupportedProtocol, type);
            }

            var host = String("host") is { } h ? TrimHorizontal(h) : null;
            if (string.IsNullOrEmpty(host)) throw new VVFileException(VVFileErrorKind.MissingKey, "host");
            // L'host finisce in risoluzioni DNS e nelle righe HTTP CONNECT: niente spazi, caratteri
            // di controllo o delimitatori URL/HTTP ("\r\n" con escape inietterebbe intestazioni).
            if (!IsSafeHost(host)) throw new VVFileException(VVFileErrorKind.InvalidValue, "host");

            var profile = new ConnectionProfile(host)
            {
                Port = RequiredPort("port"),
                TlsPort = RequiredPort("tls-port"),
            };
            if (profile.Port is null && profile.TlsPort is null) throw new VVFileException(VVFileErrorKind.NoPort);

            if (String("password") is { } password) profile.Password = new SecretTicket(password);

            if (String("proxy") is { } proxyText && TrimHorizontal(proxyText) is { Length: > 0 } proxy)
            {
                if (ParseProxy(proxy) is not { } endpoint || !IsSafeHost(endpoint.Host))
                {
                    throw new VVFileException(VVFileErrorKind.InvalidValue, "proxy");
                }
                profile.Proxy = endpoint;
            }

            if (String("ca") is { Length: > 0 } ca) profile.CaPem = ca;
            if (String("host-subject") is { } subjectText && TrimHorizontal(subjectText) is { Length: > 0 } subject)
            {
                profile.HostSubject = subject;
            }

            if (String("secure-channels") is { } secure)
            {
                profile.SecureChannels = ParseSecureChannels(secure)
                    ?? throw new VVFileException(VVFileErrorKind.InvalidValue, "secure-channels");
            }

            profile.Title = String("title");
            profile.Fullscreen = Bool("fullscreen") ?? false;
            profile.DeleteThisFile = Bool("delete-this-file") ?? false;
            profile.ToggleFullscreenHotkey = HotkeyValue("toggle-fullscreen");
            profile.ReleaseCursorHotkey = HotkeyValue("release-cursor");
            profile.SecureAttentionHotkey = HotkeyValue("secure-attention");

            profile.EnableUsbRedirection = Bool("enable-usbredir");
            profile.EnableUsbAutoshare = Bool("enable-usb-autoshare") ?? false;
            if (String("usb-filter") is { Length: > 0 } filter)
            {
                try
                {
                    profile.UsbFilter = UsbFilter.Parse(filter);
                }
                catch (FormatException)
                {
                    // Una restrizione illeggibile non deve diventare "consenti tutto":
                    // si disattiva il reindirizzamento USB (e il montaggio ISO).
                    Warnings.Add(new VVFileWarning.InvalidOptionalValue("usb-filter"));
                    profile.EnableUsbRedirection = false;
                }
            }

            if (String("color-depth") is { } depth)
            {
                if (int.TryParse(TrimHorizontal(depth), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                {
                    profile.ColorDepth = value;
                }
                else
                {
                    Warnings.Add(new VVFileWarning.InvalidOptionalValue("color-depth"));
                }
            }
            if (String("disable-effects") is { } effects)
            {
                profile.DisableEffects = [.. effects.Split(';').Select(TrimHorizontal).Where(e => e.Length > 0)];
            }
            profile.EnableSmartcard = Bool("enable-smartcard");
            profile.Versions = String("versions");
            profile.NewerVersionUrl = String("newer-version-url");

            foreach (var key in values.Keys.Order(StringComparer.Ordinal).ToList())
            {
                if (_consumed.Contains(key)) continue;
                Warnings.Add(new VVFileWarning.UnknownKey(key));
                if (String(key) is { } value) profile.UnknownKeys[key] = value;
            }
            return profile;
        }
    }
}

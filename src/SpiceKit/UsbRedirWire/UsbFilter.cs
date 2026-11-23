using System.Globalization;

namespace SpiceKit.UsbRedirWire;

/// <summary>
/// Filtro dispositivi usbredir, come nella voce <c>usb-filter</c> dei file <c>.vv</c>.
/// </summary>
/// <remarks>
/// Formato (usbredirfilter.h): regole separate da <c>|</c>, ognuna
/// <c>classe,vendor,product,versione,consenti</c>. <c>-1</c> vale "qualsiasi"; i numeri possono
/// essere decimali o esadecimali con <c>0x</c>. Decide la prima regola che corrisponde; un
/// dispositivo che non corrisponde a nessuna regola è negato.
/// </remarks>
public sealed record UsbFilter(IReadOnlyList<UsbFilter.Rule> Rules)
{
    public sealed record Rule(byte? DeviceClass, ushort? VendorId, ushort? ProductId, ushort? DeviceVersion, bool Allow)
    {
        internal bool Matches(Device device) =>
            (DeviceClass is not { } c || device.Classes.Contains(c))
            && (VendorId is not { } v || v == device.VendorId)
            && (ProductId is not { } p || p == device.ProductId)
            && (DeviceVersion is not { } d || d == device.DeviceVersion);
    }

    /// <summary>Descrizione minima di un dispositivo USB per valutare il filtro.</summary>
    /// <param name="Classes">Classe del dispositivo più quelle di ogni interfaccia.</param>
    /// <param name="VendorId">idVendor.</param>
    /// <param name="ProductId">idProduct.</param>
    /// <param name="DeviceVersion">bcdDevice.</param>
    public sealed record Device(IReadOnlySet<byte> Classes, ushort VendorId, ushort ProductId, ushort DeviceVersion);

    /// <summary>Legge una stringa di filtro; le regole vuote (es. <c>|</c> finale) sono ignorate.</summary>
    /// <exception cref="FormatException">Regola con numero di campi o valori non validi.</exception>
    public static UsbFilter Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rules = new List<Rule>();
        foreach (var raw in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            var fields = trimmed.Split(',');
            if (fields.Length != 5) throw new FormatException($"Regola del filtro USB con un numero di campi errato: {trimmed}");
            var values = fields.Select(f => ParseNumber(f.Trim())).ToArray();
            if (values[4] is not (0 or 1)) throw new FormatException($"Valore fuori intervallo: {values[4]}");
            rules.Add(new Rule(
                (byte?)Optional(values[0], byte.MaxValue),
                (ushort?)Optional(values[1], ushort.MaxValue),
                (ushort?)Optional(values[2], ushort.MaxValue),
                (ushort?)Optional(values[3], ushort.MaxValue),
                values[4] == 1));
        }
        return new UsbFilter(rules);
    }

    /// <summary>Consentito se la prima regola che corrisponde lo consente; altrimenti negato.</summary>
    public bool Allows(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        foreach (var rule in Rules)
        {
            if (rule.Matches(device)) return rule.Allow;
        }
        return false;
    }

    public bool Equals(UsbFilter? other) => other is not null && Rules.SequenceEqual(other.Rules);

    public override int GetHashCode() => Rules.Count;

    private static long? Optional(long value, long max)
    {
        if (value == -1) return null;
        if (value > max) throw new FormatException($"Valore fuori intervallo: {value}");
        return value;
    }

    private static long ParseNumber(string text)
    {
        if (text == "-1") return -1;
        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (!ok || value < 0 || text.Length > 18) throw new FormatException($"Numero non valido: {text}");
        return value;
    }
}

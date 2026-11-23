using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace WindowsSpiceClient.Services;

/// <summary>Testi dell'interfaccia da <c>Strings/it-IT/Resources.resw</c> (gli stessi del client Linux).</summary>
internal static class Strings
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string name)
    {
        var value = Loader.GetString(name);
        return string.IsNullOrEmpty(value) ? name : value;
    }

    public static string Format(string name, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(name), args);
}

using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace WindowsSpiceClient.Services;

/// <summary>
/// Testi dell'interfaccia da <c>Strings/&lt;lingua&gt;/Resources.resw</c>, nella lingua decisa da
/// <see cref="Language"/>; una chiave senza traduzione ricade sull'inglese (lingua predefinita).
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Manager = new();
    private static readonly ResourceMap Map = Manager.MainResourceMap.GetSubtree("Resources");
    private static readonly ResourceContext Context = CreateContext();

    private static ResourceContext CreateContext()
    {
        var context = Manager.CreateResourceContext();
        context.QualifierValues["Language"] = Language.Current;
        return context;
    }

    public static string Get(string name)
    {
        var value = Map.TryGetValue(name, Context)?.ValueAsString;
        return string.IsNullOrEmpty(value) ? name : value;
    }

    public static string Format(string name, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(name), args);
}

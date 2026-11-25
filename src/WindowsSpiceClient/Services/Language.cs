using System.Globalization;
using WindowsSpiceClient.Diagnostics;

namespace WindowsSpiceClient.Services;

/// <summary>
/// Lingua dell'interfaccia, con le stesse regole di PxStation Mac e PxStation Linux:
/// <list type="number">
/// <item>quella scelta in Preferenze › Generale (<c>Language</c> in settings.json), oppure</item>
/// <item>la prima lingua supportata fra le lingue preferite di Windows, oppure</item>
/// <item>l'inglese, se nessuna è supportata.</item>
/// </list>
/// <see cref="Apply"/> va chiamato in <c>Program.Main</c> prima di creare l'app: i testi XAML
/// (<c>x:Uid</c>) si risolvono al caricamento delle finestre, quindi un cambio vale al riavvio.
/// </summary>
internal static class Language
{
    /// <summary>Lingua dei testi di riserva (Strings\en-US).</summary>
    public const string Fallback = "en-US";

    /// <summary>Una cartella Strings\&lt;tag&gt; per ciascuna; l'inglese per primo.</summary>
    public static readonly IReadOnlyList<string> Supported = ["en-US", "it-IT"];

    /// <summary>La scelta con cui gira questo processo ("" = predefinita di sistema).</summary>
    public static string ChosenAtStart { get; private set; } = "";

    /// <summary>La lingua in uso (uno dei tag di <see cref="Supported"/>).</summary>
    public static string Current { get; private set; } = Fallback;

    /// <summary>Il nome della lingua nella lingua stessa ("English", "Italiano").</summary>
    public static string DisplayName(string tag)
    {
        var name = CultureInfo.GetCultureInfo(tag).Parent.NativeName;
        return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.GetCultureInfo(tag)) + name[1..] : tag;
    }

    /// <summary>Il tag supportato che corrisponde a <paramref name="tag"/> ("it", "it-CH" → "it-IT"), o null.</summary>
    public static string? Match(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var exact = Supported.FirstOrDefault(s => string.Equals(s, tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var primary = tag.Split('-', '_')[0];
        return Supported.FirstOrDefault(s => string.Equals(s.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Le lingue preferite dell'utente in Windows, in ordine.</summary>
    public static IReadOnlyList<string> SystemPreferences()
    {
        try
        {
            var languages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
            if (languages.Count > 0) return [.. languages];
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Info($"lingue preferite non leggibili: {error.Message}");
        }
        return [CultureInfo.CurrentUICulture.Name];
    }

    /// <summary>La lingua supportata a cui portano le preferenze di Windows (inglese se nessuna).</summary>
    public static string SystemLanguage(IEnumerable<string>? preferences = null) =>
        (preferences ?? SystemPreferences()).Select(Match).FirstOrDefault(tag => tag is not null) ?? Fallback;

    /// <summary>Decide la lingua e la impone a risorse MRT, XAML e formati del testo.</summary>
    public static string Apply(Settings settings)
    {
        ChosenAtStart = Match(settings.Language) ?? "";
        Current = ChosenAtStart.Length > 0 ? ChosenAtStart : SystemLanguage();
        try
        {
            // Vale anche per le app non pacchettizzate (Windows App SDK) e guida la scelta delle
            // risorse x:Uid. Non viene salvata: si reimposta a ogni avvio.
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Current;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Info($"PrimaryLanguageOverride non impostabile: {error.Message}");
        }
        var culture = CultureInfo.GetCultureInfo(Current);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return Current;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SpiceKit.Proxmox;
using WindowsSpiceClient.Diagnostics;

namespace WindowsSpiceClient.Services;

/// <summary>Contesto JSON generato in compilazione (niente reflection: pronto per il trimming).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(List<ProxmoxServer>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

/// <summary>Lettura e scrittura atomica dei file JSON nella cartella dati.</summary>
internal static class JsonFiles
{
    public static T Load<T>(string name, JsonTypeInfo<T> info, Func<T> fallback)
    {
        var path = Path.Combine(AppPaths.DataDirectory, name);
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, info) ?? fallback();
        }
        catch (FileNotFoundException)
        {
            return fallback();
        }
        catch (DirectoryNotFoundException)
        {
            return fallback();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Error($"Impossibile leggere {name}", error);
            return fallback();
        }
    }

    /// <summary>Scrive in un file temporaneo e poi lo sostituisce, così un'interruzione non lascia file troncati.</summary>
    public static void Save<T>(string name, T value, JsonTypeInfo<T> info)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var path = Path.Combine(AppPaths.DataDirectory, name);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, info));
        File.Move(temporary, path, overwrite: true);
    }
}

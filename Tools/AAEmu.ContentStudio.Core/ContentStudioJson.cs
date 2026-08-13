using System.Text.Json;
using System.Text.Json.Serialization;

namespace AAEmu.ContentStudio.Core;

public static class ContentStudioJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value, bool indented = true)
    {
        var options = new JsonSerializerOptions(Options) { WriteIndented = indented };
        return JsonSerializer.Serialize(value, options);
    }

    public static T Deserialize<T>(string json, string sourceName)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new ContentStudioException($"{sourceName} did not contain a {typeof(T).Name} value.");
        }
        catch (JsonException exception)
        {
            throw new ContentStudioException($"Unable to parse {sourceName}: {exception.Message}", exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

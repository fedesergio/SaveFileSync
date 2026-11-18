using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaveFileSync.Core.Model;

/// <summary>Shared JSON settings for every document the app writes.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options);

    public static T? Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);
}

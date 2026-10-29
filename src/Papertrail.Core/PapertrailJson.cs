using System.Text.Json;
using System.Text.Json.Serialization;

namespace Papertrail;

public static class PapertrailJson
{
    public static JsonSerializerOptions Options { get; } = Create(writeIndented: false);

    public static JsonSerializerOptions Indented { get; } = Create(writeIndented: true);

    private static JsonSerializerOptions Create(bool writeIndented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = writeIndented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Serializes enums as camelCase strings ("blackWhite") and rejects numeric values.</summary>
public sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;

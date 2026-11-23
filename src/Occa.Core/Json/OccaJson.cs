using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Occa.Core.Json;

/// <summary>
/// JSON conventions shared with CakraCore's <c>JSONEncoder</c>/<c>JSONDecoder</c>: ISO 8601 dates without fractional
/// seconds, sorted keys, enums as lower-camel strings, optionals omitted when null. Unknown fields survive a round
/// trip through <c>[JsonExtensionData]</c> on every model (NFR-3).
/// </summary>
public static class OccaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new Iso8601DateConverter(), new GuidUpperConverter() },
    };

    public static T Deserialize<T>(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Empty JSON for {typeof(T).Name}.");
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Empty JSON for {typeof(T).Name}.");

    /// <summary>Serialises with keys sorted at every level, like Swift's <c>.sortedKeys</c>, so files compare equal.</summary>
    public static string SerializeSorted<T>(T value, bool indented = false)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        var sorted = Sorted(node);
        return sorted?.ToJsonString(new JsonSerializerOptions { WriteIndented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
    }

    public static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, Sorted(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}

/// <summary>Enum ⇄ lower-camel string ("doubleTags"), the Swift raw value convention of CakraCore's enums.</summary>
public sealed class CamelEnumConverter<T> : JsonStringEnumConverter<T> where T : struct, Enum
{
    public CamelEnumConverter() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

/// <summary>"2026-11-20T10:00:00Z": Swift's <c>.iso8601</c> strategy (whole seconds, UTC).</summary>
public sealed class Iso8601DateConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? throw new JsonException("Date expected.");
        if (DateTime.TryParseExact(s, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)) return d;
        return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}

/// <summary>UUIDs upper-case, as Foundation writes them.</summary>
public sealed class GuidUpperConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Guid.Parse(reader.GetString() ?? "");
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString().ToUpperInvariant());
}

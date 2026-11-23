using System.Text.Json;
using System.Text.Json.Serialization;
using Occa.Core.Geometry;

namespace Occa.Core.Vector;

/// <summary>
/// A single sketch stroke. Points in project coordinates (UTM, metres); width in paper mm. Stored as on Mac with
/// snake_case keys and the points as a flat <c>xy</c> array.
/// </summary>
[JsonConverter(typeof(StrokeJsonConverter))]
public sealed class Stroke
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ColorHex { get; set; } = "#000000";
    public double WidthMM { get; set; }
    public bool PressureEnabled { get; set; }
    public List<Point2D> Points { get; set; } = new();
    public List<double> Pressures { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Fields this version does not know, kept for the round trip.</summary>
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public Rect2D? Bounds => Rect2D.FromPoints(Points);

    public double DistanceTo(Point2D p)
    {
        if (Points.Count == 0) return double.PositiveInfinity;
        if (Points.Count == 1) return p.DistanceTo(Points[0]);
        double best = double.PositiveInfinity;
        for (int i = 1; i < Points.Count; i++) best = Math.Min(best, p.DistanceToSegment(Points[i - 1], Points[i]));
        return best;
    }

    public double PixelWidth(int dpi) => WidthMM * dpi / 25.4;
}

public sealed class StrokeJsonConverter : JsonConverter<Stroke>
{
    private static readonly HashSet<string> Known = new() { "id", "color", "width_mm", "pressure_enabled", "xy", "pressures", "created_at" };

    public override Stroke Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var o = doc.RootElement;
        var s = new Stroke
        {
            Id = Guid.Parse(o.GetProperty("id").GetString()!),
            ColorHex = o.GetProperty("color").GetString() ?? "#000000",
            WidthMM = o.GetProperty("width_mm").GetDouble(),
            PressureEnabled = o.TryGetProperty("pressure_enabled", out var pe) && pe.ValueKind == JsonValueKind.True,
            CreatedAt = JsonSerializer.Deserialize<DateTime>(o.GetProperty("created_at").GetRawText(), options),
        };
        var xy = o.GetProperty("xy").EnumerateArray().Select(e => e.GetDouble()).ToList();
        if (xy.Count % 2 != 0) throw new JsonException("Odd number of coordinates in stroke xy.");
        for (int i = 0; i < xy.Count; i += 2) s.Points.Add(new Point2D(xy[i], xy[i + 1]));
        if (o.TryGetProperty("pressures", out var pr)) s.Pressures = pr.EnumerateArray().Select(e => e.GetDouble()).ToList();
        foreach (var p in o.EnumerateObject())
            if (!Known.Contains(p.Name)) (s.Extra ??= new())[p.Name] = p.Value.Clone();
        return s;
    }

    public override void Write(Utf8JsonWriter writer, Stroke s, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", s.Id.ToString().ToUpperInvariant());
        writer.WriteString("color", s.ColorHex);
        writer.WriteNumber("width_mm", s.WidthMM);
        writer.WriteBoolean("pressure_enabled", s.PressureEnabled);
        writer.WritePropertyName("xy");
        writer.WriteStartArray();
        foreach (var p in s.Points) { writer.WriteNumberValue(p.X); writer.WriteNumberValue(p.Y); }
        writer.WriteEndArray();
        if (s.Pressures.Count > 0)
        {
            writer.WritePropertyName("pressures");
            JsonSerializer.Serialize(writer, s.Pressures, options);
        }
        writer.WritePropertyName("created_at");
        JsonSerializer.Serialize(writer, s.CreatedAt, options);
        foreach (var (k, v) in s.Extra ?? new())
        {
            writer.WritePropertyName(k);
            v.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}

/// <summary>Pen presets following ISOM colours and line widths (FR-3.4).</summary>
public sealed record PenPreset(string Name, string ColorHex, double WidthMM)
{
    public static readonly (double Min, double Max) WidthRange = (0.05, 3.00);

    public static readonly PenPreset[] Isom =
    {
        new("Black", "#000000", 0.14), new("Brown", "#D15C00", 0.14), new("Blue", "#00A0E6", 0.18),
        new("Yellow", "#FFBA35", 0.35), new("Green", "#3EB54A", 0.35), new("Red", "#E30613", 0.25),
    };

    /// <summary>Clamp to 0.05–3.00 mm in steps of 0.01 mm.</summary>
    public static double ClampWidth(double mm) => Math.Round(Math.Clamp(mm, WidthRange.Min, WidthRange.Max) * 100) / 100;
}

public static class ColorHex
{
    /// <summary>"#RRGGBB" → 0–1 components, or null.</summary>
    public static (double R, double G, double B)? Rgb(string hex)
    {
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return (((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }

    public static string Normalized(string hex) => "#" + hex.Trim().TrimStart('#').ToUpperInvariant();
}

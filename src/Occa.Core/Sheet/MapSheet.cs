using System.Text.Json.Serialization;
using Occa.Core.Geometry;

namespace Occa.Core.Sheet;

[JsonConverter(typeof(Occa.Core.Json.CamelEnumConverter<SheetOrientation>))]
public enum SheetOrientation
{
    Portrait,
    Landscape,
}

/// <summary>Paper size in mm, always stored as short side × long side.</summary>
public sealed class PaperSize
{
    [JsonPropertyName("name")] public string Name { get; set; } = "A4";
    [JsonPropertyName("shortMM")] public double ShortMM { get; set; } = 210;
    [JsonPropertyName("longMM")] public double LongMM { get; set; } = 297;
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public static PaperSize A4 => new();
}

/// <summary>Scale and dpi options (FR-1.1, FR-3.1).</summary>
public static class MapPresets
{
    public static readonly int[] Scales = Enumerable.Range(0, 29).Select(i => 1000 + i * 500).ToArray();
    public static readonly int[] Dpis = { 300, 600, 900 };
    public static readonly double[] MarginsMM = { 0, 5, 10 };
    public static readonly int[] SnapStepsM = { 0, 10, 50, 100 };
    public static readonly int[] IndoorScales = { 250, 300, 350, 400, 500, 600, 700, 800, 900, 1000, 1250, 1500, 2000 };
}

/// <summary>Map sheet: paper + orientation + margin + scale. All derived sizes come from this (<c>SheetSpec</c> on Mac).</summary>
public sealed class SheetSpec
{
    [JsonPropertyName("paper")] public PaperSize Paper { get; set; } = PaperSize.A4;
    [JsonPropertyName("orientation")] public SheetOrientation Orientation { get; set; } = SheetOrientation.Portrait;
    [JsonPropertyName("marginMM")] public double MarginMM { get; set; }
    [JsonPropertyName("scaleDenominator")] public int ScaleDenominator { get; set; } = 4000;
    /// <summary>The user's choice ("Indoor map (ISInOM)"); null = automatic from the scale.</summary>
    [JsonPropertyName("standardOverride")] public MapStandard? StandardOverride { get; set; }
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    [JsonIgnore] public MapStandard MapStandard => StandardOverride ?? MapStandardExtensions.ForScale(ScaleDenominator);
    [JsonIgnore] public bool IsIndoor => MapStandard == MapStandard.Isinom;
    [JsonIgnore] public double SymbolEnlargement => MapStandard.Enlargement(ScaleDenominator);

    [JsonIgnore] public double MappingWidthMM => (Orientation == SheetOrientation.Portrait ? Paper.ShortMM : Paper.LongMM) - 2 * MarginMM;
    [JsonIgnore] public double MappingHeightMM => (Orientation == SheetOrientation.Portrait ? Paper.LongMM : Paper.ShortMM) - 2 * MarginMM;
    [JsonIgnore] public double GroundWidthM => MappingWidthMM / 1000 * ScaleDenominator;
    [JsonIgnore] public double GroundHeightM => MappingHeightMM / 1000 * ScaleDenominator;

    public double PixelSizeM(int dpi) => ScaleDenominator * 0.0254 / dpi;
    public (int Width, int Height) PixelDimensions(int dpi) => ((int)Math.Round(MappingWidthMM / 25.4 * dpi), (int)Math.Round(MappingHeightMM / 25.4 * dpi));
    /// <summary>Ground metres for a size in paper mm.</summary>
    public double GroundMeters(double paperMM) => paperMM / 1000 * ScaleDenominator;
}

/// <summary>North-up frame relative to the UTM grid. origin = lower-left corner.</summary>
public readonly record struct MapFrame(Point2D Origin, double WidthM, double HeightM)
{
    public static MapFrame Centered(Point2D center, SheetSpec sheet)
        => new(new Point2D(center.X - sheet.GroundWidthM / 2, center.Y - sheet.GroundHeightM / 2), sheet.GroundWidthM, sheet.GroundHeightM);

    public Rect2D Extent => new(Origin.X, Origin.Y, Origin.X + WidthM, Origin.Y + HeightM);
    public Point2D Center => Extent.Center;

    /// <summary>Corners clockwise from upper-left: UL, UR, LR, LL.</summary>
    public Point2D[] Corners
    {
        get
        {
            var e = Extent;
            return new[] { new Point2D(e.MinX, e.MaxY), new Point2D(e.MaxX, e.MaxY), new Point2D(e.MaxX, e.MinY), new Point2D(e.MinX, e.MinY) };
        }
    }

    public MapFrame SnappedToStep(int stepM)
    {
        if (stepM <= 0) return this;
        double s = stepM;
        return this with { Origin = new Point2D(Math.Round(Origin.X / s) * s, Math.Round(Origin.Y / s) * s) };
    }

    public MapFrame ResizedTo(SheetSpec sheet) => Centered(Center, sheet);
}

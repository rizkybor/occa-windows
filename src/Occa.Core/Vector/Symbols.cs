using System.Text.Json;
using System.Text.Json.Serialization;
using Occa.Core.Geometry;
using Occa.Core.Json;

namespace Occa.Core.Vector;

/// <summary>A single drawing element of a point symbol, in paper mm relative to the symbol centre (x right, y up).</summary>
public sealed class PointElement
{
    public enum Kind { Line = 1, Area = 2, Circle = 3, Dot = 4 }

    [JsonPropertyName("kind")] public Kind ElementKind { get; set; }
    [JsonPropertyName("colorHex")] public string ColorHex { get; set; } = "#000000";
    /// <summary>Colour order in the colour table; smaller number = drawn on top.</summary>
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("lineWidthMM")] public double LineWidthMM { get; set; }
    [JsonPropertyName("diameterMM")] public double DiameterMM { get; set; }
    [JsonPropertyName("points")] public List<Point2D> Points { get; set; } = new();
    /// <summary>Lines: flat ends and mitred corners; null = round ends and joins.</summary>
    [JsonPropertyName("flatEnds")] public bool? FlatEnds { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Outermost radius of the element (mm), for the symbol size and selection area.</summary>
    [JsonIgnore]
    public double ExtentMM => ElementKind switch
    {
        Kind.Dot => DiameterMM / 2,
        Kind.Circle => DiameterMM / 2 + LineWidthMM / 2,
        _ => (Points.Count == 0 ? 0 : Points.Max(p => p.Length)) + LineWidthMM / 2,
    };

    public PointElement Clone() => new()
    {
        ElementKind = ElementKind, ColorHex = ColorHex, Priority = Priority, LineWidthMM = LineWidthMM, DiameterMM = DiameterMM,
        Points = new List<Point2D>(Points), FlatEnds = FlatEnds, Extra = Extra,
    };
}

public static class PointElementExtensions
{
    /// <summary>Symbol diameter (mm) at standard size.</summary>
    public static double DiameterMM(this IReadOnlyList<PointElement> elements) => 2 * (elements.Count == 0 ? 0.3 : elements.Max(e => e.ExtentMM));

    /// <summary>Drawing order: lowest colour priority first (bottommost). Stable for equal priorities.</summary>
    public static IEnumerable<PointElement> DrawingOrder(this IEnumerable<PointElement> elements) => elements.OrderByDescending(e => e.Priority);
}

/// <summary>IOF line style. Double line: edge <c>Casing.ColorHex</c> <c>WidthMM</c> wide underneath, fill <c>ColorHex</c> <c>Casing.InnerWidthMM</c> wide on top.</summary>
public sealed class LineStyle
{
    public sealed class CasingStyle
    {
        [JsonPropertyName("colorHex")] public string ColorHex { get; set; } = "#000000";
        [JsonPropertyName("innerWidthMM")] public double InnerWidthMM { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    [JsonPropertyName("colorHex")] public string ColorHex { get; set; } = "#000000";
    [JsonPropertyName("widthMM")] public double WidthMM { get; set; }
    [JsonPropertyName("casing")] public CasingStyle? Casing { get; set; }
    [JsonPropertyName("dashMM")] public List<double>? DashMM { get; set; }
    [JsonPropertyName("innerDashMM")] public List<double>? InnerDashMM { get; set; }
    [JsonPropertyName("roundCaps")] public bool? RoundCaps { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A repeating area pattern: parallel lines (solid or dashed), a dot/triangle grid (regular, jittered or random) or holes.</summary>
public sealed class AreaPattern
{
    [JsonConverter(typeof(CamelEnumConverter<Kind>))]
    public enum Kind { Lines, Dots, Triangles, Holes }

    [JsonPropertyName("kind")] public Kind PatternKind { get; set; }
    [JsonPropertyName("colorHex")] public string ColorHex { get; set; } = "#000000";
    [JsonPropertyName("sizeMM")] public double SizeMM { get; set; }
    [JsonPropertyName("spacingMM")] public double SpacingMM { get; set; }
    /// <summary>Direction of the lines / grid, degrees from map north (0 = north–south lines).</summary>
    [JsonPropertyName("angleDeg")] public double AngleDeg { get; set; }
    [JsonPropertyName("dashMM")] public List<double>? DashMM { get; set; }
    [JsonPropertyName("staggered")] public bool? Staggered { get; set; }
    [JsonPropertyName("random")] public bool? Random { get; set; }
    [JsonPropertyName("sidesMM")] public List<double>? SidesMM { get; set; }
    [JsonPropertyName("jitterMM")] public double? JitterMM { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The pattern with all its sizes × k (map enlargement); angles and flags unchanged.</summary>
    public AreaPattern ScaledBy(double k)
    {
        double R(double v) => Math.Round(v * k * 1000) / 1000;
        return new AreaPattern
        {
            PatternKind = PatternKind, ColorHex = ColorHex, SizeMM = R(SizeMM), SpacingMM = R(SpacingMM), AngleDeg = AngleDeg,
            DashMM = DashMM?.Select(R).ToList(), Staggered = Staggered, Random = Random, SidesMM = SidesMM?.Select(R).ToList(),
            JitterMM = JitterMM is double j ? R(j) : null, Extra = Extra,
        };
    }
}

/// <summary>Repeated marks along a line: tags, dots, half dots, ticks, chevrons, triangles, bars, rungs, edges or a taper.</summary>
public sealed class LineDecoration
{
    [JsonConverter(typeof(CamelEnumConverter<Kind>))]
    public enum Kind { Tags, DoubleTags, Dots, HalfDots, Ticks, DoubleTicks, Bars, Chevrons, Triangles, EndTicks, Rungs, Edges, Taper }

    [JsonPropertyName("kind")] public Kind DecorationKind { get; set; }
    [JsonPropertyName("sizeMM")] public double SizeMM { get; set; }
    [JsonPropertyName("widthMM")] public double WidthMM { get; set; }
    [JsonPropertyName("spacingMM")] public double SpacingMM { get; set; }
    [JsonPropertyName("angleDeg")] public double AngleDeg { get; set; } = 90;
    [JsonPropertyName("pairMM")] public double? PairMM { get; set; }
    [JsonPropertyName("offsetMM")] public double? OffsetMM { get; set; }
    [JsonPropertyName("onDashes")] public bool? OnDashes { get; set; }
    [JsonPropertyName("hidesLine")] public bool? HidesLine { get; set; }
    [JsonPropertyName("more")] public List<LineDecoration>? More { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>This decoration and its <c>More</c> decorations, in drawing order.</summary>
    [JsonIgnore]
    public IEnumerable<LineDecoration> All
    {
        get
        {
            var head = ShallowCopy();
            head.More = null;
            yield return head;
            foreach (var m in More ?? Enumerable.Empty<LineDecoration>())
                foreach (var d in m.All) yield return d;
        }
    }

    [JsonIgnore] public bool ReplacesLine => All.Any(d => d.HidesLine == true);

    public LineDecoration ScaledBy(double k)
    {
        double R(double v) => Math.Round(v * k * 1000) / 1000;
        var d = ShallowCopy();
        d.SizeMM = R(SizeMM); d.WidthMM = R(WidthMM); d.SpacingMM = R(SpacingMM);
        d.PairMM = PairMM is double p ? R(p) : null; d.OffsetMM = OffsetMM is double o ? R(o) : null;
        d.More = More?.Select(m => m.ScaledBy(k)).ToList();
        return d;
    }

    private LineDecoration ShallowCopy() => new()
    {
        DecorationKind = DecorationKind, SizeMM = SizeMM, WidthMM = WidthMM, SpacingMM = SpacingMM, AngleDeg = AngleDeg, PairMM = PairMM,
        OffsetMM = OffsetMM, OnDashes = OnDashes, HidesLine = HidesLine, More = More, Extra = Extra,
    };
}

/// <summary>A single map symbol (ISOM/ISSprOM/ISInOM) from a symbol set (<c>MapSymbol</c> on Mac).</summary>
public sealed class MapSymbol
{
    [JsonConverter(typeof(CamelEnumConverter<Kind>))]
    public enum Kind { Point, Line, Area, Text }

    /// <summary>OCAD number ×1000 (101000 = 101.0); unique within a set.</summary>
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public Kind SymbolKind { get; set; }
    [JsonPropertyName("colorHex")] public string ColorHex { get; set; } = "#000000";
    [JsonPropertyName("lineWidthMM")] public double? LineWidthMM { get; set; }
    [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    [JsonPropertyName("pointElements")] public List<PointElement>? PointElements { get; set; }
    [JsonPropertyName("lineStyle")] public LineStyle? LineStyle { get; set; }
    [JsonPropertyName("colorPriority")] public int? ColorPriority { get; set; }
    [JsonPropertyName("casingPriority")] public int? CasingPriority { get; set; }
    [JsonPropertyName("patterns")] public List<AreaPattern>? Patterns { get; set; }
    [JsonPropertyName("decoration")] public LineDecoration? Decoration { get; set; }
    [JsonPropertyName("textSizeMM")] public double? TextSizeMM { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public string Code => $"{Number / 1000}.{Number % 1000}";
    [JsonIgnore] public bool IsDrawable => SymbolKind is Kind.Line or Kind.Area || (SymbolKind == Kind.Point && (PointElements?.Count ?? 0) > 0);
    /// <summary>IOF standard width for drawing: single line width, total double-line width, or 0.1 mm for area outlines.</summary>
    [JsonIgnore] public double DrawingWidthMM => LineStyle?.WidthMM ?? LineWidthMM ?? (SymbolKind == Kind.Area ? 0.1 : 0.25);
    [JsonIgnore] public SymbolCategory Category => Enum.IsDefined(typeof(SymbolCategory), Number / 100_000) ? (SymbolCategory)(Number / 100_000) : SymbolCategory.Other;
}

public enum SymbolCategory
{
    Other = 0, Landform = 1, Rock = 2, Water = 3, Vegetation = 4, ManMade = 5, Technical = 6, Course = 7,
    IndoorConstruction = 11, IndoorFloor = 12, IndoorWater = 13, IndoorFurniture = 14, IndoorDirections = 17,
}

/// <summary>Symbol set in the app library, shared across projects.</summary>
public sealed class SymbolSet
{
    public const int CurrentReaderVersion = 7;

    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sourceFile")] public string SourceFile { get; set; } = "";
    [JsonPropertyName("scale")] public int Scale { get; set; }
    [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
    [JsonPropertyName("symbols")] public List<MapSymbol> Symbols { get; set; } = new();
    [JsonPropertyName("readerVersion")] public int? ReaderVersion { get; set; } = CurrentReaderVersion;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool IsOutdated => (ReaderVersion ?? 0) < CurrentReaderVersion;
    [JsonIgnore] public int EnabledCount => Symbols.Count(s => s.IsEnabled);
}

/// <summary>OCCA symbol set file (<c>.occasym</c>): metadata, colour table (CMYK + tint, top → bottom) and symbols.</summary>
public sealed class OccaSymbolFile
{
    public const string FileExtension = ".occasym";
    public const string FormatName = "OCCA symbol set";
    public const int CurrentVersion = 1;

    public sealed class Color
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("cmyk")] public List<double> Cmyk { get; set; } = new() { 0, 0, 0, 1 };
        [JsonPropertyName("tint")] public double Tint { get; set; } = 1;
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

        /// <summary>Screen colour: CMYK → RGB, then the tint mixed with white paper (same formula as Mac).</summary>
        [JsonIgnore]
        public string Hex
        {
            get
            {
                double C(int i) => i < Cmyk.Count ? Math.Clamp(Cmyk[i], 0, 1) : 0;
                int Ch(double x) => (int)(((1 - Tint) + Tint * (1 - x) * (1 - C(3))) * 255 + 0.5);
                return $"#{Ch(C(0)):X2}{Ch(C(1)):X2}{Ch(C(2)):X2}";
            }
        }
    }

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = CurrentVersion;
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("standard")] public string Standard { get; set; } = "";
    [JsonPropertyName("specification")] public string Specification { get; set; } = "";
    [JsonPropertyName("scale")] public int Scale { get; set; }
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("license")] public string License { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("colors")] public List<Color> Colors { get; set; } = new();
    [JsonPropertyName("symbols")] public List<MapSymbol> Symbols { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static OccaSymbolFile Read(ReadOnlySpan<byte> data)
    {
        OccaSymbolFile? file;
        try { file = JsonSerializer.Deserialize<OccaSymbolFile>(data, OccaJson.Options); }
        catch (JsonException) { file = null; }
        if (file is null || file.Format != FormatName) throw new InvalidDataException("This is not an OCCA symbol set (.occasym) file.");
        if (file.FormatVersion > CurrentVersion) throw new InvalidDataException($"This symbol set was made with a newer OCCA (format {file.FormatVersion}). Update OCCA to open it.");
        return file;
    }

    public SymbolSet ToSymbolSet(string sourceFile, string? name = null) => new() { Name = name ?? Name, SourceFile = sourceFile, Scale = Scale, Symbols = Symbols };
}

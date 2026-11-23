using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Occa.Core.Geometry;
using Occa.Core.Json;
using Occa.Core.Sheet;
using Occa.Core.Vector;

namespace Occa.Core.Project;

/// <summary>Project metadata (<c>project.json</c>), the same fields as <c>Project</c> in CakraCore. Unknown fields are kept.</summary>
public sealed class Project
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sheet")] public SheetSpec Sheet { get; set; } = new();
    [JsonPropertyName("sketchDPI")] public int SketchDpi { get; set; } = 900;
    /// <summary>EPSG UTM WGS84; null until the frame is first placed.</summary>
    [JsonPropertyName("crsEPSG")] public int? CrsEpsg { get; set; }
    /// <summary>Lower-left corner of the frame in the project CRS (metres).</summary>
    [JsonPropertyName("frameOrigin")] public Point2D? FrameOrigin { get; set; }
    [JsonPropertyName("areaLocked")] public bool AreaLocked { get; set; }
    [JsonPropertyName("snapStepM")] public int SnapStepM { get; set; } = 10;
    [JsonPropertyName("basemapOpacity")] public double? BasemapOpacity { get; set; }
    [JsonPropertyName("northGrid")] public NorthGrid? NorthGrid { get; set; }
    /// <summary>Contour settings (M4); kept as JSON until ported.</summary>
    [JsonPropertyName("contourSettings")] public JsonObject? ContourSettings { get; set; }
    [JsonPropertyName("areaOpacity")] public double? AreaOpacity { get; set; }
    [JsonPropertyName("basemapImage")] public BasemapImage? BasemapImage { get; set; }
    [JsonPropertyName("areaBackup")] public AreaBackup? AreaBackup { get; set; }
    /// <summary>true = live basemap, false/null = the saved HD image.</summary>
    [JsonPropertyName("liveBasemap")] public bool? LiveBasemap { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public UtmZone? Zone => CrsEpsg is int e ? UtmZone.FromEpsg(e) : null;
    [JsonIgnore] public double BasemapOpacityValue => Math.Clamp(BasemapOpacity ?? 1, 0, 1);
    [JsonIgnore] public bool UsesLiveBasemap => LiveBasemap ?? false;
    [JsonIgnore] public NorthGrid Grid => NorthGrid ?? Occa.Core.Project.NorthGrid.Standard(Sheet.ScaleDenominator, Sheet.MapStandard);
    [JsonIgnore] public double AreaOpacityValue => Math.Clamp(AreaOpacity ?? 1, 0.1, 1);

    /// <summary>Frame from the origin + ground size of the sheet; null if not yet placed.</summary>
    [JsonIgnore] public MapFrame? Frame => FrameOrigin is { } o ? new MapFrame(o, Sheet.GroundWidthM, Sheet.GroundHeightM) : null;

    /// <summary>Technical summary, e.g. "1:4000 · 900 dpi · UTM 48S".</summary>
    [JsonIgnore]
    public string TechnicalSummary
    {
        get
        {
            var parts = new List<string> { $"1:{Sheet.ScaleDenominator}", $"{SketchDpi} dpi" };
            if (Zone is { } z) parts.Add(z.Label);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Locked map area saved before "Reselect area".</summary>
public sealed class AreaBackup
{
    [JsonPropertyName("crsEPSG")] public int? CrsEpsg { get; set; }
    [JsonPropertyName("frameOrigin")] public Point2D? FrameOrigin { get; set; }
    [JsonPropertyName("sheet")] public SheetSpec Sheet { get; set; } = new();
    [JsonPropertyName("snapStepM")] public int SnapStepM { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>North lines inside the frame, like ISOM symbol 601 (<c>NorthGrid</c> on Mac).</summary>
public sealed class NorthGrid
{
    [JsonConverter(typeof(CamelEnumConverter<Reference>))]
    public enum Reference { Grid, Magnetic }

    [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
    [JsonPropertyName("reference")] public Reference ReferenceKind { get; set; } = Reference.Grid;
    [JsonPropertyName("declinationDeg")] public double DeclinationDeg { get; set; }
    [JsonPropertyName("spacingM")] public double SpacingM { get; set; } = 250;
    [JsonPropertyName("colorHex")] public string ColorHex { get; set; } = "#00A0E6";
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.8;
    [JsonPropertyName("lineWidthMM")] public double LineWidthMM { get; set; } = 0.18;
    [JsonPropertyName("includeInExport")] public bool IncludeInExport { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static readonly double[] SpacingPresetsM = { 10, 20, 25, 50, 100, 120, 125, 200, 250, 300, 400, 500, 1000 };

    /// <summary>IOF 601 magnetic north lines for the scale and standard. Off until the user shows them.</summary>
    public static NorthGrid Standard(int scale, MapStandard s)
    {
        double w = Math.Round(s.NorthLineBlueWidthMM() * s.Enlargement(scale) * 1000) / 1000;
        return new NorthGrid { SpacingM = s.NorthLineSpacingM(), Opacity = 1, LineWidthMM = w };
    }

    [JsonIgnore] public double AngleDeg => ReferenceKind == Reference.Magnetic ? DeclinationDeg : 0;
    public double SpacingPaperMM(int scale) => SpacingM / scale * 1000;

    /// <summary>Line segments clipped to the frame edges, ordered west to east.</summary>
    public List<(Point2D A, Point2D B)> Lines(MapFrame frame)
    {
        var result = new List<(Point2D, Point2D)>();
        if (!(SpacingM > 0)) return result;
        var e = frame.Extent;
        double a = AngleDeg * Math.PI / 180;
        var d = new Point2D(Math.Sin(a), Math.Cos(a));
        var n = new Point2D(Math.Cos(a), -Math.Sin(a));
        var offsets = frame.Corners.Select(c => c.X * n.X + c.Y * n.Y).ToArray();
        int lo = (int)Math.Ceiling(offsets.Min() / SpacingM), hi = (int)Math.Floor(offsets.Max() / SpacingM);
        if (lo > hi || hi - lo >= 10_000) return result;
        for (int k = lo; k <= hi; k++)
        {
            double o = k * SpacingM;
            var b = new Point2D(n.X * o, n.Y * o);
            double t0 = double.NegativeInfinity, t1 = double.PositiveInfinity;
            foreach (var (p, dir, mn, mx) in new[] { (b.X, d.X, e.MinX, e.MaxX), (b.Y, d.Y, e.MinY, e.MaxY) })
            {
                if (Math.Abs(dir) < 1e-12)
                {
                    if (p < mn - 1e-9 || p > mx + 1e-9) { t0 = 1; t1 = 0; }
                }
                else
                {
                    double ta = (mn - p) / dir, tb = (mx - p) / dir;
                    t0 = Math.Max(t0, Math.Min(ta, tb));
                    t1 = Math.Min(t1, Math.Max(ta, tb));
                }
            }
            if (!(t1 - t0 > 1e-6)) continue;
            result.Add((new Point2D(b.X + d.X * t0, b.Y + d.Y * t0), new Point2D(b.X + d.X * t1, b.Y + d.Y * t1)));
        }
        return result;
    }
}

/// <summary>Georeferenced image basemap (GeoTIFF) beneath the map objects; its file lives in the project package.</summary>
public sealed class BasemapImage
{
    /// <summary>UTM (EPSG 326xx/327xx) or WGS84 lat/lon. Encoded as Swift does: <c>{"utm":{"epsg":32748}}</c> or <c>{"geographic":{}}</c>.</summary>
    [JsonConverter(typeof(CrsConverter))]
    public readonly record struct Crs(int? Epsg)
    {
        public bool IsGeographic => Epsg is null;
        public static Crs Utm(int epsg) => new(epsg);
        public static readonly Crs Geographic = new((int?)null);
    }

    public sealed class CrsConverter : JsonConverter<Crs>
    {
        public override Crs Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var o = doc.RootElement;
            if (o.TryGetProperty("utm", out var utm) && utm.TryGetProperty("epsg", out var epsg)) return Crs.Utm(epsg.GetInt32());
            return Crs.Geographic;
        }

        public override void Write(Utf8JsonWriter writer, Crs value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            if (value.Epsg is int e)
            {
                writer.WritePropertyName("utm");
                writer.WriteStartObject();
                writer.WriteNumber("epsg", e);
                writer.WriteEndObject();
            }
            else
            {
                writer.WritePropertyName("geographic");
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
    }

    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
    [JsonPropertyName("originalName")] public string OriginalName { get; set; } = "";
    [JsonPropertyName("crs")] public Crs CrsValue { get; set; } = Crs.Geographic;
    [JsonPropertyName("originX")] public double OriginX { get; set; }
    [JsonPropertyName("originY")] public double OriginY { get; set; }
    [JsonPropertyName("pixelSizeX")] public double PixelSizeX { get; set; }
    [JsonPropertyName("pixelSizeY")] public double PixelSizeY { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1;
    [JsonPropertyName("isVisible")] public bool IsVisible { get; set; } = true;
    [JsonPropertyName("dateTime")] public string? DateTime { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public double OpacityValue => Math.Clamp(Opacity, 0, 1);
    [JsonIgnore] public bool HasSafeFileName => IsSafeFileName(FileName);

    public static bool IsSafeFileName(string name)
        => name.Length is > 0 and <= 255 && !name.StartsWith('.') && name != ".." && !name.Contains('/') && !name.Contains('\\') && !name.Contains('\0');

    /// <summary>Image CRS point at pixel (u right, v down; pixel edges).</summary>
    public Point2D CrsPoint(double u, double v) => new(OriginX + u * PixelSizeX, OriginY - v * PixelSizeY);

    public LatLon LatLonAt(double u, double v)
    {
        var p = CrsPoint(u, v);
        if (CrsValue.Epsg is int epsg) return UtmZone.FromEpsg(epsg) is { } zone ? zone.Unproject(p) : new LatLon(0, 0);
        return new LatLon(p.Y, p.X);
    }

    /// <summary>Upper-left, upper-right, lower-right, lower-left corners in project UTM.</summary>
    public Point2D[] Corners(UtmZone zone)
    {
        double w = Width, h = Height;
        return new[] { (0.0, 0.0), (w, 0.0), (w, h), (0.0, h) }.Select(c => zone.Project(LatLonAt(c.Item1, c.Item2))).ToArray();
    }

    public Rect2D? Extent(UtmZone zone) => Rect2D.FromPoints(Corners(zone));
}

/// <summary>A static HD image of the basemap captured for the locked area, stored under <c>imagery/</c> as a per-device cache.</summary>
public sealed class ImagerySnapshot
{
    public const string Folder = "imagery";
    public const double TargetMetersPerPixel = 0.25;
    public const int MaxPixelSide = 4096;

    [JsonPropertyName("mapType")] public string MapType { get; set; } = "satellite";
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
    [JsonPropertyName("north")] public double North { get; set; }
    [JsonPropertyName("south")] public double South { get; set; }
    [JsonPropertyName("west")] public double West { get; set; }
    [JsonPropertyName("east")] public double East { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("capturedAt")] public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Windows only: when the tiles this image was stitched from expire (Esri caching headers); null = unknown.</summary>
    [JsonPropertyName("expiresAt")] public DateTime? ExpiresAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static readonly HashSet<string> MapTypes = new() { "satellite", "hybrid", "standard" };
    public static string ImagePath(string mapType) => $"{Folder}/{mapType}.jpg";
    public static string MetadataPath(string mapType) => $"{Folder}/{mapType}.json";

    [JsonIgnore] public bool IsValid => MapTypes.Contains(MapType) && FileName == ImagePath(MapType) && Width > 0 && Height > 0 && North > South && East > West;

    [JsonIgnore]
    public double MetersPerPixel
    {
        get
        {
            double midLat = (North + South) / 2 * Math.PI / 180;
            return (East - West) * 111_320 * Math.Cos(midLat) / Math.Max(Width, 1);
        }
    }

    public static (int Width, int Height) PixelSize(double widthM, double heightM)
    {
        if (!(widthM > 0) || !(heightM > 0)) return (1, 1);
        double w = widthM / TargetMetersPerPixel, h = heightM / TargetMetersPerPixel;
        double k = Math.Min(1, MaxPixelSide / Math.Max(w, h));
        return (Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
    }
}

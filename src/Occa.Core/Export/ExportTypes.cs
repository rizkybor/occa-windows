using System.Globalization;
using Occa.Core.Geometry;
using Occa.Core.Project;
using Occa.Core.Sheet;
using Occa.Core.Vector;

namespace Occa.Core.Export;

/// <summary>Georeferenced basemap image placed under the map in exports (JPEG; PNG is converted by the app before export).</summary>
public sealed class ExportBackground
{
    public byte[] ImageData { get; init; } = Array.Empty<byte>();
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    /// <summary>Image corners in project UTM: top left, top right, bottom right, bottom left.</summary>
    public Point2D[] Corners { get; init; } = Array.Empty<Point2D>();
    public double Opacity { get; init; } = 1;
    public string? Attribution { get; init; }
    /// <summary>"jpg" or "png".</summary>
    public string FileExtension { get; init; } = "jpg";

    public string WorldFileExtension => FileExtension == "png" ? "pgw" : "jgw";

    /// <summary>World file of the image (affine; the small rotation between Web Mercator/lat-lon and UTM is kept).</summary>
    public WorldFile WorldFile
    {
        get
        {
            double w = Math.Max(PixelWidth, 1), h = Math.Max(PixelHeight, 1);
            var tl = Corners[0]; var tr = Corners[1]; var bl = Corners[3];
            double a = (tr.X - tl.X) / w, d = (tr.Y - tl.Y) / w, b = (bl.X - tl.X) / h, e = (bl.Y - tl.Y) / h;
            return new WorldFile(a, d, b, e, tl.X + (a + b) / 2, tl.Y + (d + e) / 2);
        }
    }

    /// <summary>Writes image + world file + .prj as &lt;baseName&gt;_basemap.*; returns the written paths.</summary>
    public List<string> WriteSidecar(string baseName, UtmZone zone, string directory)
    {
        var basePath = Path.Combine(directory, baseName + "_basemap");
        var image = basePath + "." + FileExtension;
        var world = basePath + "." + WorldFileExtension;
        var prj = basePath + ".prj";
        ProjectPackage.WriteAtomic(image, ImageData);
        File.WriteAllText(world, WorldFile.Text);
        File.WriteAllText(prj, zone.PrjWkt);
        return new List<string> { image, world, prj };
    }
}

/// <summary>Sketch symbol = one colour + width combination; strokes get the neutral code 990.x.</summary>
public sealed record SketchSymbol(int Index, string ColorHex, double WidthMM, SketchSymbol.SymbolKind Kind, string? IsomCode, string? IsomName)
{
    public enum SymbolKind { Line, Point, Area }

    public List<AreaPattern>? Patterns { get; set; }
    public List<double>? DashMM { get; set; }

    public bool IsPoint => Kind == SymbolKind.Point;
    public bool IsArea => Kind == SymbolKind.Area;
    public string Code => IsomCode ?? $"990.{Index + 1}";

    public string ColorName => PenPreset.Isom.FirstOrDefault(p => string.Equals(p.ColorHex, ColorHex, StringComparison.OrdinalIgnoreCase))?.Name ?? ColorHex.ToUpperInvariant();

    public string Name
    {
        get
        {
            if (!string.IsNullOrEmpty(IsomName)) return IsomName;
            if (IsArea) return $"Area {ColorName}";
            return $"Sketch {ColorName}{(IsPoint ? " point" : "")} {WidthMM.ToString("F2", CultureInfo.InvariantCulture)} mm";
        }
    }

    /// <summary>DXF layer name: only A–Z, 0–9, _ (safe for R12).</summary>
    public string DxfLayer
    {
        get
        {
            int mm = (int)Math.Round(WidthMM * 100);
            string raw;
            if (IsomCode is not null)
            {
                raw = $"{IsomCode.Replace(".", "_")}_{IsomName ?? ""}".ToUpperInvariant();
                raw = raw.Length > 31 ? raw[..31] : raw;
            }
            else
            {
                raw = (IsArea ? $"990_{Index + 1}_AREA_{ColorName}" : $"990_{Index + 1}_SKETCH_{ColorName}_{mm:D3}{(IsPoint ? "_POINT" : "")}").ToUpperInvariant();
            }
            return new string(raw.Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '_').ToArray());
        }
    }
}

/// <summary>Unique symbols from strokes and vector objects, in order of first appearance.</summary>
public sealed class SketchSymbolTable
{
    public List<SketchSymbol> Symbols { get; } = new();
    private readonly Dictionary<string, int> _lookup = new();

    public SketchSymbolTable(IReadOnlyList<Stroke> strokes, IReadOnlyList<Feature>? features = null)
    {
        foreach (var s in strokes) Add(KeyOf(s));
        foreach (var f in features ?? Array.Empty<Feature>())
        {
            var sym = Add(KeyOf(f));
            if (f.Kind == FeatureKind.Area && sym.Patterns is null && f.Patterns is { Count: > 0 } p) sym.Patterns = p;
            if (f.Kind == FeatureKind.Line && !f.IsDoubleLine && sym.DashMM is null && f.EffectiveDashMM is { } d && (d.Count == 2 || d.Count == 4)) sym.DashMM = d;
        }
    }

    private readonly record struct SymbolKey(string Color, int Width, SketchSymbol.SymbolKind Kind, string? Code, string? Name)
    {
        public string Text => Code is not null ? $"isom|{Code}|{Kind}" : $"{Color}|{Width}|{Kind}";
    }

    private static SymbolKey KeyOf(Stroke s) => new(s.ColorHex.ToUpperInvariant(), (int)Math.Round(s.WidthMM * 100), s.Points.Count == 1 ? SketchSymbol.SymbolKind.Point : SketchSymbol.SymbolKind.Line, null, null);

    private static SymbolKey KeyOf(Feature f) => f.Kind switch
    {
        FeatureKind.Point => new SymbolKey(f.ColorHex.ToUpperInvariant(), (int)Math.Round(f.WidthMM * 100), SketchSymbol.SymbolKind.Point, f.SymbolCode, f.SymbolName),
        FeatureKind.Area => new SymbolKey(f.ColorHex.ToUpperInvariant(), 0, SketchSymbol.SymbolKind.Area, f.SymbolCode, f.SymbolName),
        _ => new SymbolKey(f.ColorHex.ToUpperInvariant(), (int)Math.Round(f.WidthMM * 100), SketchSymbol.SymbolKind.Line, f.SymbolCode, f.SymbolName),
    };

    public SketchSymbol? SymbolOf(Stroke s) => _lookup.TryGetValue(KeyOf(s).Text, out var i) ? Symbols[i] : null;
    public SketchSymbol? SymbolOf(Feature f) => _lookup.TryGetValue(KeyOf(f).Text, out var i) ? Symbols[i] : null;

    private SketchSymbol Add(SymbolKey k)
    {
        if (_lookup.TryGetValue(k.Text, out var i)) return Symbols[i];
        var sym = new SketchSymbol(Symbols.Count, k.Color, k.Width / 100.0, k.Kind, k.Code, k.Name);
        _lookup[k.Text] = Symbols.Count;
        Symbols.Add(sym);
        return sym;
    }
}

public enum VectorFormat { Omap, Dxf }

public sealed class VectorExportRequest
{
    public required string BaseName { get; init; }
    public required MapFrame Frame { get; init; }
    public required SheetSpec Sheet { get; init; }
    public required UtmZone Zone { get; init; }
    public List<Stroke> Strokes { get; init; } = new();
    public List<Feature> Features { get; init; } = new();
    public NorthGrid? Grid { get; init; }
    public ExportBackground? Background { get; init; }

    /// <summary>Strokes entirely outside the frame are excluded (FR-1.8).</summary>
    public List<Stroke> ExportedStrokes => Strokes.Where(s => s.Bounds is { } b && Frame.Extent.Intersects(b)).ToList();

    /// <summary>Visible objects touching the frame, plus north lines (ISOM 601) when the grid is exported.</summary>
    public List<Feature> ExportedFeatures
    {
        get
        {
            var e = Frame.Extent;
            var own = Features.Where(f => f.IsVisible && f.Nodes.Count >= (f.Kind == FeatureKind.Point ? 1 : 2) && f.Bounds is { } b && e.Intersects(b)).ToList();
            if (Grid is not { IsEnabled: true, IncludeInExport: true } g) return own;
            return GridFeatures(g, Frame).Concat(own).ToList();
        }
    }

    /// <summary>Grid lines as vector objects with ISOM symbol 601.</summary>
    public static List<Feature> GridFeatures(NorthGrid g, MapFrame frame) => g.Lines(frame).Select((seg, i) => new Feature
    {
        Name = $"North line {i + 1}", Kind = FeatureKind.Line, ColorHex = g.ColorHex, WidthMM = g.LineWidthMM,
        Nodes = new List<BezierNode> { new(seg.A), new(seg.B) }, SymbolCode = "601.0", SymbolName = "Magnetic north line",
    }).ToList();
}

public static class VectorExporter
{
    public static List<string> Export(VectorExportRequest req, IReadOnlySet<VectorFormat> formats, string directory)
    {
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        foreach (var format in new[] { VectorFormat.Omap, VectorFormat.Dxf })
        {
            if (!formats.Contains(format)) continue;
            var path = Path.Combine(directory, req.BaseName + (format == VectorFormat.Omap ? ".omap" : ".dxf"));
            var text = format == VectorFormat.Omap ? OmapWriter.Document(req) : DxfWriter.Document(req);
            ProjectPackage.WriteAtomic(path, new System.Text.UTF8Encoding(false).GetBytes(text));
            paths.Add(path);
        }
        if (req.Background is { } bg && formats.Count > 0) paths.AddRange(bg.WriteSidecar(req.BaseName, req.Zone, directory));
        return paths;
    }
}

public enum RasterFormat { Png, Tiff, Jpeg }

public static class RasterFormatExtensions
{
    public static string FileExtension(this RasterFormat f) => f switch { RasterFormat.Tiff => "tif", RasterFormat.Jpeg => "jpg", _ => "png" };
    /// <summary>.pgw (PNG), .tfw (TIFF), .jgw (JPEG) per FR-6.2.</summary>
    public static string WorldFileExtension(this RasterFormat f) => f switch { RasterFormat.Tiff => "tfw", RasterFormat.Jpeg => "jgw", _ => "pgw" };
    public static bool SupportsTransparency(this RasterFormat f) => f != RasterFormat.Jpeg;
}

/// <summary>What a raster export needs; the drawing itself happens in Occa.Render (Win2D), the sidecar files here.</summary>
public sealed class RasterExportRequest
{
    public required string BaseName { get; init; }
    public required MapFrame Frame { get; init; }
    public required SheetSpec Sheet { get; init; }
    public required UtmZone Zone { get; init; }
    public int Dpi { get; init; } = 300;
    public RasterFormat Format { get; init; } = RasterFormat.Png;
    public List<Stroke> Strokes { get; init; } = new();
    public List<Feature> Features { get; init; } = new();
    public NorthGrid? Grid { get; init; }
    public bool IncludePrj { get; init; } = true;
    public ExportBackground? Background { get; init; }

    public double PixelSize => Sheet.PixelSizeM(Dpi);
    public (int Width, int Height) PixelDimensions => Sheet.PixelDimensions(Dpi);
    public long EstimatedBitmapBytes => (long)PixelDimensions.Width * PixelDimensions.Height * 4;
    public WorldFile WorldFile => WorldFile.For(Frame, PixelSize);

    public string ImagePath(string directory) => Path.Combine(directory, BaseName + "." + Format.FileExtension());
    public string WorldFilePath(string directory) => Path.Combine(directory, BaseName + "." + Format.WorldFileExtension());
    public string PrjPath(string directory) => Path.Combine(directory, BaseName + ".prj");

    /// <summary>Writes the world file and .prj next to the image; returns their paths.</summary>
    public List<string> WriteSidecar(string directory)
    {
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        File.WriteAllText(WorldFilePath(directory), WorldFile.Text);
        paths.Add(WorldFilePath(directory));
        if (IncludePrj) { File.WriteAllText(PrjPath(directory), Zone.PrjWkt); paths.Add(PrjPath(directory)); }
        return paths;
    }
}

/// <summary>Pre-export check: strokes outside the frame.</summary>
public sealed record ExportCheck(int Outside, int PartlyOutside)
{
    public static ExportCheck Of(IReadOnlyList<Stroke> strokes, MapFrame frame)
    {
        var e = frame.Extent;
        int outside = 0, partly = 0;
        foreach (var s in strokes)
        {
            if (s.Bounds is not { } b) continue;
            if (!b.Intersects(e)) outside++; else if (!e.Contains(b)) partly++;
        }
        return new ExportCheck(outside, partly);
    }

    public bool IsClean => Outside == 0 && PartlyOutside == 0;
}

internal static class Num
{
    public static string F(double v, int decimals) => v.ToString("F" + decimals, CultureInfo.InvariantCulture);
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Occa.Core.Geometry;
using Occa.Core.Json;
using Occa.Core.Sheet;
using Occa.Core.Vector;

namespace Occa.Core.Contour;

/// <summary>
/// Per-project contour settings (FR-8.3 – FR-8.13). Default widths follow the map's IOF standard (<see cref="MapStandard"/>):
/// ISOM contour 0.14 / index 0.25 mm, ISSprOM 0.21 / 0.30 mm, enlarged with the map; form lines ≈ 0.71 × the contour width,
/// dashed. Port of <c>ContourSettings</c> (stored in project.json).
/// </summary>
public sealed record ContourSettings
{
    [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
    [JsonPropertyName("intervalM")] public double IntervalM { get; set; } = 5;
    [JsonPropertyName("indexEvery")] public int IndexEvery { get; set; } = 5;
    [JsonPropertyName("baseOffsetM")] public double BaseOffsetM { get; set; }
    [JsonPropertyName("smoothing")] public int Smoothing { get; set; } = 1;
    [JsonPropertyName("minLengthMM")] public double MinLengthMM { get; set; } = 1.5;
    [JsonPropertyName("showFormLines")] public bool ShowFormLines { get; set; }
    [JsonPropertyName("colorHex")] public string ColorHex { get; set; } = "#D15C00";
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1;
    [JsonPropertyName("contourWidthMM")] public double ContourWidthMM { get; set; } = 0.14;
    [JsonPropertyName("indexWidthMM")] public double IndexWidthMM { get; set; } = 0.25;
    [JsonPropertyName("includeInExport")] public bool IncludeInExport { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The part of the settings that changes line geometry. Colour, opacity, and width are drawing style only.</summary>
    public readonly record struct GeometryKey(double IntervalM, int IndexEvery, double BaseOffsetM, int Smoothing, double MinLengthMM, bool ShowFormLines);

    [JsonIgnore] public GeometryKey Geometry => new(IntervalM, IndexEvery, BaseOffsetM, Smoothing, MinLengthMM, ShowFormLines);

    public static readonly (double Min, double Max) OpacityRange = (0.05, 1);
    public static readonly double[] IntervalPresetsM = { 1, 2, 2.5, 5, 10 };
    public static readonly (double Min, double Max) IntervalRangeM = (0.5, 50);

    /// <summary>
    /// The contour intervals the map's standard allows (one interval per map): ISOM 2017-2 §2.10: 5 m; 2.5 m only in flat
    /// terrain. ISSprOM 2019-2 §3.2: 2 m or 2.5 m; 5 m in special cases of steep areas. ISInOM maps have no contours (§3.2).
    /// </summary>
    public static double[] IntervalPresets(MapStandard standard) => standard == MapStandard.Isom ? new[] { 5.0, 2.5 } : new[] { 2.0, 2.5, 5.0 };

    /// <summary>Hint about the chosen interval for the standard (null = the standard value).</summary>
    public static string? IntervalHint(double v, MapStandard standard)
    {
        if ((standard == MapStandard.Isom && v == 5) || (standard != MapStandard.Isom && (v == 2 || v == 2.5))) return null;
        if (standard == MapStandard.Isom && v == 2.5) return "ISOM: 2.5 m only for flat terrain (slope under 5 %, contours more than 7 mm apart).";
        if (standard == MapStandard.Isom) return "ISOM 2017-2 uses a 5 m interval (2.5 m in flat terrain).";
        if (v == 5) return "ISSprOM: 5 m only in special cases of steep areas.";
        return "ISSprOM 2019-2 uses a 2 m or 2.5 m interval (5 m in steep areas).";
    }

    /// <summary>FR-8.4: 1:3000–1:5000 = 2 m, 1:7500–1:15000 = 5 m.</summary>
    public static double DefaultInterval(int scale) => scale <= 5000 ? 2 : 5;

    public static ContourSettings Defaults(int scale)
    {
        var w = StandardWidths(scale);
        return new ContourSettings { IntervalM = DefaultInterval(scale), ContourWidthMM = w.Contour, IndexWidthMM = w.Index };
    }

    /// <summary>Defaults for a sheet: its standard (indoor maps: ISSprOM widths at the ISInOM enlargement, 1:700 = 100 %).</summary>
    public static ContourSettings Defaults(SheetSpec sheet)
    {
        var w = StandardWidths(sheet);
        return new ContourSettings { IntervalM = sheet.MapStandard == MapStandard.Isom ? 5 : 2, ContourWidthMM = w.Contour, IndexWidthMM = w.Index };
    }

    /// <summary>IOF contour / index widths for a sheet: the sheet's standard × its symbol enlargement.</summary>
    public static (double Contour, double Index) StandardWidths(SheetSpec sheet)
    {
        var s = sheet.MapStandard; double k = sheet.SymbolEnlargement;
        return (Math.Round(s.ContourWidthMM() * k * 1000) / 1000, Math.Round(s.IndexContourWidthMM() * k * 1000) / 1000);
    }

    /// <summary>IOF contour / index contour widths for the scale (incl. enlargement, e.g. ISOM at 1:10000 = 0.21 / 0.375 mm).</summary>
    public static (double Contour, double Index) StandardWidths(int scale)
    {
        var s = MapStandardExtensions.ForScale(scale); double k = s.Enlargement(scale);
        return (Math.Round(s.ContourWidthMM() * k * 1000) / 1000, Math.Round(s.IndexContourWidthMM() * k * 1000) / 1000);
    }

    /// <summary>Form line width and [dash, gap] for a contour width (all paper mm).</summary>
    public static (double Width, List<double> Dash) FormLine(double contourWidthMM)
    {
        double f = contourWidthMM * MapStandardExtensions.FormLineWidthRatio;
        return (f, new List<double> { f * MapStandardExtensions.FormLineDashPerWidth, f * MapStandardExtensions.FormLineGapPerWidth });
    }

    /// <summary>FR-8.11: an interval below ±1/4 of the DEM's horizontal resolution usually just draws noise.</summary>
    public string? IntervalWarning(double nativeResolutionM)
    {
        double minimum = Math.Ceiling(nativeResolutionM / 4);
        if (IntervalM >= minimum) return null;
        var interval = IntervalM.ToString(IntervalM % 1 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);
        return $"Interval {interval} m is too fine for a ±{(int)Math.Round(nativeResolutionM, MidpointRounding.AwayFromZero)} m DEM; ≥ {(int)minimum} m recommended.";
    }
}

/// <summary>One contour line in project coordinates (<c>ContourLine</c> on Mac; stored as contours.json when imported).</summary>
public sealed class ContourLine
{
    [JsonConverter(typeof(CamelEnumConverter<Kind>))]
    public enum Kind { Contour, Index, Form }

    [JsonPropertyName("elevation")] public double Elevation { get; set; }
    [JsonPropertyName("kind")] public Kind LineKind { get; set; }
    [JsonPropertyName("points")] public List<Point2D> Points { get; set; } = new();
    [JsonPropertyName("isClosed")] public bool IsClosed { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public ContourLine() { }

    public ContourLine(double elevation, Kind kind, List<Point2D> points, bool isClosed)
    {
        Elevation = elevation;
        LineKind = kind;
        Points = points;
        IsClosed = isClosed;
    }

    /// <summary>ISOM symbol code: 101.0 contour, 102.0 index contour, 103.0 form line.</summary>
    [JsonIgnore] public string SymbolCode => LineKind switch { Kind.Contour => "101.0", Kind.Index => "102.0", _ => "103.0" };
    [JsonIgnore] public string SymbolName => LineKind switch { Kind.Contour => "Contour", Kind.Index => "Index contour", _ => "Form line" };

    [JsonIgnore] public double Length => Enumerable.Range(0, Math.Max(Points.Count - 1, 0)).Sum(i => Points[i].DistanceTo(Points[i + 1]));

    /// <summary>Line kind for a given elevation: index if it is a multiple of (interval × N) from the base elevation.</summary>
    public static Kind KindOf(double e, ContourSettings s)
    {
        double step = s.IntervalM * Math.Max(s.IndexEvery, 1);
        double k = (e - s.BaseOffsetM) / step;
        if (Math.Abs(k - Math.Round(k)) < 1e-6) return Kind.Index;
        double c = (e - s.BaseOffsetM) / s.IntervalM;
        return Math.Abs(c - Math.Round(c)) < 1e-6 ? Kind.Contour : Kind.Form;
    }

    /// <summary>
    /// Contour line as an ISOM-symbolised vector object (for export and "Convert to vector objects").
    /// Closed contours stay lines (not filled areas): the last point returns to the first.
    /// </summary>
    public Feature ToFeature(ContourSettings s)
    {
        var pts = new List<Point2D>(Points);
        if (IsClosed && pts.Count > 0 && pts[^1] != pts[0]) pts.Add(pts[0]);
        var elev = Elevation.ToString(Elevation % 1 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);
        var form = ContourSettings.FormLine(s.ContourWidthMM);
        var f = new Feature
        {
            Name = $"{SymbolCode} {SymbolName} {elev} m", Kind = FeatureKind.Line, ColorHex = s.ColorHex,
            WidthMM = LineKind == Kind.Index ? s.IndexWidthMM : LineKind == Kind.Form ? form.Width : s.ContourWidthMM,
            Nodes = pts.Select(p => new BezierNode(p)).ToList(), SymbolCode = SymbolCode, SymbolName = SymbolName,
        };
        if (LineKind == Kind.Form) f.DashMM = form.Dash;
        return f;
    }

    public const string ImportedFile = "contours.json";

    /// <summary>Imported contour lines (GeoJSON) are stored in the package as contours.json.</summary>
    public static void WriteImported(IReadOnlyList<ContourLine> lines, string directory)
        => Project.ProjectPackage.WriteAtomic(Path.Combine(directory, ImportedFile), System.Text.Encoding.UTF8.GetBytes(OccaJson.SerializeSorted(lines)));

    public static List<ContourLine>? ReadImported(string directory)
    {
        var path = Path.Combine(directory, ImportedFile);
        if (!File.Exists(path)) return null;
        try { return OccaJson.Deserialize<List<ContourLine>>(File.ReadAllBytes(path)); } catch (JsonException) { return null; }
    }

    public static void RemoveImported(string directory)
    {
        var path = Path.Combine(directory, ImportedFile);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Reclassify imported lines (contour/index/form) according to the current interval settings.</summary>
    public static List<ContourLine> Classify(IEnumerable<ContourLine> lines, ContourSettings s)
        => lines.Select(l => new ContourLine(l.Elevation, KindOf(l.Elevation, s), l.Points, l.IsClosed) { Extra = l.Extra }).ToList();
}

/// <summary>
/// Contours from a DEM grid: marching squares per level, then segments are joined into polylines, simplified, smoothed
/// (Chaikin), and short lines are dropped (FR-8.5 – FR-8.8). Port of <c>ContourGenerator</c>.
/// </summary>
public static class ContourGenerator
{
    public sealed record Result(List<ContourLine> Lines, double MinElevation, double MaxElevation)
    {
        public double Relief => MaxElevation - MinElevation;
    }

    public static Result Generate(DemGrid grid, ContourSettings s, SheetSpec sheet, Rect2D? clip = null)
    {
        if (grid.Range is not { } range || s.IntervalM <= 0) return new Result(new List<ContourLine>(), 0, 0);
        double lo = range.Min, hi = range.Max;
        double step = s.ShowFormLines ? s.IntervalM / 2 : s.IntervalM;
        double first = Math.Ceiling((lo - s.BaseOffsetM) / step), last = Math.Floor((hi - s.BaseOffsetM) / step);
        if (last < first || last - first >= 5000) return new Result(new List<ContourLine>(), lo, hi);

        double minLengthM = sheet.GroundMeters(s.MinLengthMM);
        double simplifyTolerance = grid.CellSize * 0.1;
        var lines = new List<ContourLine>();
        for (long k = (long)first; k <= (long)last; k++)
        {
            double level = s.BaseOffsetM + k * step;
            var kind = ContourLine.KindOf(level, s);
            foreach (var (pts, closed) in Trace(grid, (float)level))
            {
                var p = Simplify(pts, simplifyTolerance);
                if (s.Smoothing > 0) p = Chaikin(p, s.Smoothing, closed);
                if (clip is { } c && !(Rect2D.FromPoints(p)?.Intersects(c) ?? false)) continue;
                var line = new ContourLine(level, kind, p, closed);
                if (line.Length < minLengthM) continue;
                if (closed && line.Points.Count > 0 && line.Points[0] != line.Points[^1]) line.Points.Add(line.Points[0]);
                lines.Add(line);
            }
        }
        return new Result(lines, lo, hi);
    }

    private readonly record struct EdgeKey(int Col, int Row, bool Horizontal);

    /// <summary>Marching squares: segments per cell, then joined through shared end points (key = cell edge).</summary>
    internal static List<(List<Point2D> Points, bool Closed)> Trace(DemGrid g, float level)
    {
        // Edge crossings get a unique key so adjacent segments share exactly the same point.
        var points = new Dictionary<EdgeKey, Point2D>();
        var adjacency = new Dictionary<EdgeKey, List<EdgeKey>>();
        var order = new List<EdgeKey>();   // insertion order of adjacency keys (Swift dictionaries are unordered; any order works)

        EdgeKey? Crossing((int C, int R) a, (int C, int R) b, bool horizontal)
        {
            float va = g.Value(a.C, a.R), vb = g.Value(b.C, b.R);
            if (float.IsNaN(va) || float.IsNaN(vb) || (va < level) == (vb < level)) return null;
            var key = new EdgeKey(a.C, a.R, horizontal);
            if (!points.ContainsKey(key))
            {
                double t = (level - va) / (vb - va);
                var pa = g.Center(a.C, a.R); var pb = g.Center(b.C, b.R);
                points[key] = new Point2D(pa.X + (pb.X - pa.X) * t, pa.Y + (pb.Y - pa.Y) * t);
            }
            return key;
        }
        void Connect(EdgeKey a, EdgeKey b)
        {
            if (!adjacency.TryGetValue(a, out var la)) { adjacency[a] = la = new List<EdgeKey>(); order.Add(a); }
            la.Add(b);
            if (!adjacency.TryGetValue(b, out var lb)) { adjacency[b] = lb = new List<EdgeKey>(); order.Add(b); }
            lb.Add(a);
        }

        for (int r = 0; r < g.Rows - 1; r++)
        {
            for (int c = 0; c < g.Cols - 1; c++)
            {
                // Edges: top (c,r)-(c+1,r), bottom (c,r+1)-(c+1,r+1), left (c,r)-(c,r+1), right (c+1,r)-(c+1,r+1).
                var top = Crossing((c, r), (c + 1, r), true);
                var bottom = Crossing((c, r + 1), (c + 1, r + 1), true);
                var left = Crossing((c, r), (c, r + 1), false);
                var right = Crossing((c + 1, r), (c + 1, r + 1), false);
                var hits = new[] { top, right, bottom, left }.Where(h => h is not null).Select(h => h!.Value).ToList();
                if (hits.Count == 2) Connect(hits[0], hits[1]);
                else if (hits.Count == 4)
                {
                    // Saddle: pick the pairing from the average of the four corners.
                    float mean = (g.Value(c, r) + g.Value(c + 1, r) + g.Value(c, r + 1) + g.Value(c + 1, r + 1)) / 4;
                    bool tlHigh = g.Value(c, r) >= level;
                    if ((mean >= level) == tlHigh) { Connect(top!.Value, right!.Value); Connect(bottom!.Value, left!.Value); }
                    else { Connect(top!.Value, left!.Value); Connect(bottom!.Value, right!.Value); }
                }
            }
        }

        // Chain: start from free ends (degree 1) first so open lines stay whole; the rest are closed loops.
        var visited = new HashSet<EdgeKey>();
        var result = new List<(List<Point2D>, bool)>();
        (List<Point2D>, bool) Walk(EdgeKey start)
        {
            var chain = new List<EdgeKey> { start };
            visited.Add(start);
            var current = start;
            while (true)
            {
                EdgeKey? next = null;
                foreach (var n in adjacency[current]) if (!visited.Contains(n)) { next = n; break; }
                if (next is null) break;
                visited.Add(next.Value);
                chain.Add(next.Value);
                current = next.Value;
            }
            bool closed = chain.Count > 2 && adjacency[current].Contains(start);
            return (chain.Where(points.ContainsKey).Select(k => points[k]).ToList(), closed);
        }
        foreach (var k in order) if (adjacency[k].Count == 1 && !visited.Contains(k)) result.Add(Walk(k));
        foreach (var k in order) if (!visited.Contains(k)) result.Add(Walk(k));
        return result.Where(x => x.Item1.Count >= 2).ToList();
    }

    /// <summary>Douglas–Peucker.</summary>
    public static List<Point2D> Simplify(List<Point2D> pts, double tolerance)
    {
        if (pts.Count <= 2 || tolerance <= 0) return pts;
        var keep = new bool[pts.Count];
        keep[0] = true;
        keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b <= a + 1) continue;
            double best = 0; int index = a;
            for (int i = a + 1; i < b; i++)
            {
                double d = pts[i].DistanceToSegment(pts[a], pts[b]);
                if (d > best) { best = d; index = i; }
            }
            if (best > tolerance)
            {
                keep[index] = true;
                stack.Push((a, index));
                stack.Push((index, b));
            }
        }
        var result = new List<Point2D>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    /// <summary>Chaikin corner cutting; end points of open lines are kept.</summary>
    public static List<Point2D> Chaikin(List<Point2D> pts, int iterations, bool closed)
    {
        var p = pts;
        for (int it = 0; it < Math.Min(iterations, 4); it++)
        {
            if (p.Count <= 2) return p;
            var q = closed ? new List<Point2D>() : new List<Point2D> { p[0] };
            int n = closed ? p.Count : p.Count - 1;
            for (int i = 0; i < n; i++)
            {
                var a = p[i]; var b = p[(i + 1) % p.Count];
                q.Add(new Point2D(0.75 * a.X + 0.25 * b.X, 0.75 * a.Y + 0.25 * b.Y));
                q.Add(new Point2D(0.25 * a.X + 0.75 * b.X, 0.25 * a.Y + 0.75 * b.Y));
            }
            if (!closed) q.Add(p[^1]);
            p = q;
        }
        return p;
    }
}

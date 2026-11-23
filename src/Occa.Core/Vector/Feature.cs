using System.Text.Json;
using System.Text.Json.Serialization;
using Occa.Core.Geometry;
using Occa.Core.Json;
using Occa.Core.Sheet;

namespace Occa.Core.Vector;

/// <summary>Bezier node: position + in/out handles (absolute UTM coordinates). No handles = corner node.</summary>
public sealed class BezierNode
{
    [JsonPropertyName("point")] public Point2D Point { get; set; }
    [JsonPropertyName("inHandle")] public Point2D? InHandle { get; set; }
    [JsonPropertyName("outHandle")] public Point2D? OutHandle { get; set; }
    /// <summary>Smart node: handles computed from the neighbours; null = not smart (not written).</summary>
    [JsonPropertyName("smart")] public bool? Smart { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public BezierNode() { }
    public BezierNode(Point2D point, Point2D? inHandle = null, Point2D? outHandle = null, bool? smart = null)
    {
        Point = point; InHandle = inHandle; OutHandle = outHandle; Smart = smart;
    }

    [JsonIgnore] public bool IsSmooth => InHandle is not null || OutHandle is not null;

    public BezierNode MovedBy(Point2D d) => new(Point + d, InHandle + d, OutHandle + d, Smart) { Extra = Extra };
    public BezierNode Clone() => new(Point, InHandle, OutHandle, Smart) { Extra = Extra };
    /// <summary>The node with its handles swapped (for a reversed ring).</summary>
    public BezierNode Reversed() => new(Point, OutHandle, InHandle, Smart) { Extra = Extra };
}

/// <summary>A single cubic segment p0 → p3 with control points c1, c2. Straight segment: c1 = p0, c2 = p3.</summary>
public readonly record struct CubicSegment(Point2D P0, Point2D C1, Point2D C2, Point2D P3, bool IsCurve)
{
    public Point2D PointAt(double t)
    {
        if (!IsCurve) return new Point2D(P0.X + (P3.X - P0.X) * t, P0.Y + (P3.Y - P0.Y) * t);
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new Point2D(a * P0.X + b * C1.X + c * C2.X + d * P3.X, a * P0.Y + b * C1.Y + c * C2.Y + d * P3.Y);
    }
}

[JsonConverter(typeof(CamelEnumConverter<FeatureKind>))]
public enum FeatureKind { Point, Line, Area }

/// <summary>Vector object: a point, line or area of bezier nodes in project coordinates (UTM). Mirrors <c>Feature</c> on Mac.</summary>
public sealed class Feature
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public FeatureKind Kind { get; set; }
    [JsonPropertyName("colorHex")] public string ColorHex { get; set; } = "#000000";
    [JsonPropertyName("widthMM")] public double WidthMM { get; set; }
    [JsonPropertyName("nodes")] public List<BezierNode> Nodes { get; set; } = new();
    /// <summary>Holes of an area: closed rings inside the outer ring, wound opposite to it. null = none.</summary>
    [JsonPropertyName("holes")] public List<List<BezierNode>>? Holes { get; set; }
    [JsonPropertyName("isVisible")] public bool IsVisible { get; set; } = true;
    [JsonPropertyName("isLocked")] public bool IsLocked { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("symbolCode")] public string? SymbolCode { get; set; }
    [JsonPropertyName("symbolName")] public string? SymbolName { get; set; }
    [JsonPropertyName("pointElements")] public List<PointElement>? PointElements { get; set; }
    [JsonPropertyName("sizeFactor")] public double? SizeFactor { get; set; }
    [JsonPropertyName("casingColorHex")] public string? CasingColorHex { get; set; }
    [JsonPropertyName("innerWidthMM")] public double? InnerWidthMM { get; set; }
    [JsonPropertyName("standardWidthMM")] public double? StandardWidthMM { get; set; }
    [JsonPropertyName("dashMM")] public List<double>? DashMM { get; set; }
    [JsonPropertyName("innerDashMM")] public List<double>? InnerDashMM { get; set; }
    [JsonPropertyName("roundCaps")] public bool? RoundCaps { get; set; }
    [JsonPropertyName("rotationDeg")] public double? RotationDeg { get; set; }
    [JsonPropertyName("colorPriority")] public int? ColorPriority { get; set; }
    [JsonPropertyName("casingPriority")] public int? CasingPriority { get; set; }
    [JsonPropertyName("patterns")] public List<AreaPattern>? Patterns { get; set; }
    [JsonPropertyName("decoration")] public LineDecoration? Decoration { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public Feature ShallowCopy() => (Feature)MemberwiseClone();

    // MARK: Geometry

    /// <summary>Areas are always closed; lines are open.</summary>
    [JsonIgnore] public bool IsClosed => Kind == FeatureKind.Area;

    /// <summary>A line whose end node lies exactly on its start node.</summary>
    [JsonIgnore] public bool IsRingLine => Kind == FeatureKind.Line && Nodes.Count >= 3 && Nodes[0].Point.DistanceTo(Nodes[^1].Point) < 1e-6;

    /// <summary>Path is drawn closed: areas and closed lines.</summary>
    [JsonIgnore] public bool ClosesPath => IsClosed || IsRingLine;

    [JsonIgnore]
    public IReadOnlyList<CubicSegment> Segments
    {
        get
        {
            if (Nodes.Count < 2) return Array.Empty<CubicSegment>();
            int count = IsClosed ? Nodes.Count : Nodes.Count - 1;
            var list = new List<CubicSegment>(count);
            for (int i = 0; i < count; i++)
            {
                var a = Nodes[i];
                var b = Nodes[(i + 1) % Nodes.Count];
                list.Add(new CubicSegment(a.Point, a.OutHandle ?? a.Point, b.InHandle ?? b.Point, b.Point, a.OutHandle is not null || b.InHandle is not null));
            }
            return list;
        }
    }

    /// <summary>Box guaranteed to contain the curve (hull of the control points). Points: a 1 m box.</summary>
    [JsonIgnore]
    public Rect2D? Bounds
    {
        get
        {
            if (Kind == FeatureKind.Point && Nodes.Count > 0)
            {
                var p = Nodes[0].Point;
                return new Rect2D(p.X - 1, p.Y - 1, p.X + 1, p.Y + 1);
            }
            return Rect2D.FromPoints(Nodes.SelectMany(n => new[] { (Point2D?)n.Point, n.InHandle, n.OutHandle }).Where(p => p.HasValue).Select(p => p!.Value));
        }
    }

    [JsonIgnore] public double EffectiveSizeFactor => SizeFactor ?? 1;

    /// <summary>Point symbol radius on the ground (m) at the sheet scale.</summary>
    public double PointRadiusM(SheetSpec sheet) => sheet.GroundMeters((PointElements ?? new List<PointElement>()).DiameterMM() / 2 * EffectiveSizeFactor);

    /// <summary>Point elements scaled by SizeFactor and rotated by RotationDeg (coordinates + diameter + width).</summary>
    [JsonIgnore]
    public List<PointElement> ScaledPointElements
    {
        get
        {
            double k = EffectiveSizeFactor;
            return (PointElements ?? new List<PointElement>()).Select(e =>
            {
                var c = e.Clone();
                c.Points = e.Points.Select(p => Rotate(new Point2D(p.X * k, p.Y * k), Point2D.Zero, RotationDegrees)).ToList();
                c.DiameterMM *= k;
                c.LineWidthMM *= k;
                return c;
            }).ToList();
        }
    }

    /// <summary>Point symbol object at position p with standard size × sizeFactor.</summary>
    public static Feature PointAt(Point2D p, List<PointElement> elements, double sizeFactor = 1, string name = "", string? symbolCode = null, string? symbolName = null)
    {
        var f = new Feature
        {
            Name = name, Kind = FeatureKind.Point, ColorHex = elements.DrawingOrder().LastOrDefault()?.ColorHex ?? "#000000",
            WidthMM = elements.DiameterMM() * sizeFactor, Nodes = new List<BezierNode> { new(p) }, SymbolCode = symbolCode, SymbolName = symbolName,
            PointElements = elements, SizeFactor = sizeFactor == 1 ? null : sizeFactor,
        };
        return f;
    }

    /// <summary>Polyline approximation of the curve; maximum deviation ≤ tolerance metres.</summary>
    public List<Point2D> Flattened(double tolerance)
    {
        var pts = new List<Point2D>();
        if (Nodes.Count == 0) return pts;
        pts.Add(Nodes[0].Point);
        foreach (var s in Segments)
        {
            if (!s.IsCurve) { pts.Add(s.P3); continue; }
            double hull = s.P0.DistanceTo(s.C1) + s.C1.DistanceTo(s.C2) + s.C2.DistanceTo(s.P3);
            int steps = Math.Max(4, Math.Min(256, (int)Math.Ceiling(Math.Sqrt(hull / Math.Max(tolerance, 1e-6))) * 2));
            for (int k = 1; k <= steps; k++) pts.Add(s.PointAt((double)k / steps));
        }
        return pts;
    }

    [JsonIgnore] public string DisplayName => Name.Length == 0 ? $"{Kind} {Id.ToString().ToUpperInvariant()[..4]}" : Name;

    // MARK: Holes (FeatureHoles.swift)

    [JsonIgnore] public IReadOnlyList<List<BezierNode>> HoleRings => Kind == FeatureKind.Area ? (IReadOnlyList<List<BezierNode>>?)Holes ?? Array.Empty<List<BezierNode>>() : Array.Empty<List<BezierNode>>();
    [JsonIgnore] public bool HasHoles => HoleRings.Count > 0;
    /// <summary>Outer ring first, then the holes.</summary>
    [JsonIgnore] public IReadOnlyList<List<BezierNode>> Rings => new[] { Nodes }.Concat(HoleRings).ToList();

    /// <summary>Ring k (0 = outer, 1… = holes) as a plain object of its own: same id and style, no holes.</summary>
    public Feature RingFeature(int k)
    {
        var f = ShallowCopy();
        f.Holes = null;
        if (k > 0 && k - 1 < HoleRings.Count) f.Nodes = HoleRings[k - 1];
        return f;
    }

    [JsonIgnore] public IReadOnlyList<Feature> RingFeatures => Enumerable.Range(0, Rings.Count).Select(RingFeature).ToList();

    /// <summary>Signed area of a ring's node points (positive = counterclockwise, y north).</summary>
    public static double SignedArea(IReadOnlyList<BezierNode> ring)
    {
        if (ring.Count < 3) return 0;
        double s = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i].Point;
            var b = ring[(i + 1) % ring.Count].Point;
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    /// <summary>Restore the winding convention (holes opposite to the outer ring), drop degenerate holes.</summary>
    public void NormalizeHoles()
    {
        if (Kind != FeatureKind.Area || Holes is null) { Holes = null; return; }
        double outer = SignedArea(Nodes);
        var kept = new List<List<BezierNode>>();
        foreach (var ring in Holes)
        {
            if (ring.Count < 3) continue;
            double a = SignedArea(ring);
            if (Math.Abs(a) <= 1e-12) continue;
            kept.Add((a > 0) == (outer > 0) ? ring.AsEnumerable().Reverse().Select(n => n.Reversed()).ToList() : ring);
        }
        Holes = kept.Count == 0 ? null : kept;
    }

    public List<List<Point2D>> FlattenedRings(double tolerance) => RingFeatures.Select(r => r.Flattened(tolerance)).ToList();

    /// <summary>Point inside the filled part of the area (even-odd over all rings).</summary>
    public bool ContainsPoint(Point2D p, double tolerance)
    {
        if (Kind != FeatureKind.Area) return false;
        bool inside = false;
        foreach (var pts in FlattenedRings(tolerance))
        {
            if (pts.Count < 3) continue;
            int j = pts.Count - 1;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                j = i;
            }
        }
        return inside;
    }

    // MARK: Line style

    /// <summary>Apply the IOF line style and store its standard width.</summary>
    public void Apply(LineStyle style)
    {
        ColorHex = style.ColorHex;
        WidthMM = style.WidthMM;
        CasingColorHex = style.Casing?.ColorHex;
        InnerWidthMM = style.Casing?.InnerWidthMM;
        StandardWidthMM = style.WidthMM;
        DashMM = style.DashMM?.ToList();
        InnerDashMM = style.InnerDashMM?.ToList();
        RoundCaps = style.RoundCaps;
    }

    /// <summary>Symbolized areas are drawn like OCAD/OOM: full-colour fill without an outline.</summary>
    [JsonIgnore] public bool IsSymbolArea => Kind == FeatureKind.Area && SymbolCode is not null;

    /// <summary>A usable dash pattern: an even number of positive values.</summary>
    public static List<double>? ValidDash(List<double>? d)
        => d is { Count: >= 2 } && d.Count % 2 == 0 && d.All(v => v > 0) ? d : null;

    private double DashScale => (StandardWidthMM ?? WidthMM) > 0 ? WidthMM / (StandardWidthMM ?? WidthMM) : 1;

    /// <summary>Dash pattern for drawing, scaled by the width (paper mm).</summary>
    [JsonIgnore] public List<double>? EffectiveDashMM => ValidDash(DashMM)?.Select(v => v * DashScale).ToList();
    [JsonIgnore] public List<double>? EffectiveInnerDashMM => ValidDash(InnerDashMM)?.Select(v => v * DashScale).ToList();

    /// <summary>The symbol's decoration replaces the main line: the line itself is not stroked.</summary>
    [JsonIgnore] public bool HidesMainLine => Kind == FeatureKind.Line && CasingColorHex is null && Decoration?.ReplacesLine == true;

    /// <summary>Seed for random area patterns: the first 8 bytes of the UUID, big-endian (as the Mac's <c>uuid.0…7</c>).</summary>
    [JsonIgnore]
    public ulong PatternSeed
    {
        get
        {
            // Guid.ToByteArray() scrambles the first three groups on little-endian machines; use the textual bytes.
            var bytes = Convert.FromHexString(Id.ToString("N"));
            ulong v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | bytes[i];
            return v;
        }
    }

    [JsonIgnore] public bool IsDoubleLine => CasingColorHex is not null && InnerWidthMM is not null;

    // MARK: Rotation

    [JsonIgnore] public double RotationDegrees => RotationDeg ?? 0;

    /// <summary>Rotate a point clockwise (y axis north).</summary>
    public static Point2D Rotate(Point2D p, Point2D c, double degrees)
    {
        if (degrees == 0) return p;
        double a = degrees * Math.PI / 180;
        double dx = p.X - c.X, dy = p.Y - c.Y;
        return new Point2D(c.X + dx * Math.Cos(a) + dy * Math.Sin(a), c.Y - dx * Math.Sin(a) + dy * Math.Cos(a));
    }

    public static double NormalizedDegrees(double d)
    {
        double r = d % 360;
        return r < 0 ? r + 360 : r;
    }

    // MARK: Symbol size

    /// <summary>Line/area width (paper mm); a double line keeps its casing ratio, dashes follow through <c>StandardWidthMM</c> (Mac's setWidth).</summary>
    public void SetWidth(double widthMM)
    {
        double w = Math.Max(0.01, widthMM);
        if (InnerWidthMM is double inner && WidthMM > 0) InnerWidthMM = Math.Round(inner * w / WidthMM * 1000) / 1000;
        WidthMM = Math.Round(w * 1000) / 1000;
    }

    /// <summary>This object with its symbol size × k (line width, inner width, dashes, point shape); no symbol = unchanged.</summary>
    public Feature WithSymbolSize(double k)
    {
        if (SymbolCode is null || Math.Abs(k - 1) <= 1e-9) return this;
        double R(double v) => Math.Round(v * k * 1000) / 1000;
        var f = ShallowCopy();
        if (Kind == FeatureKind.Area) return f;
        f.WidthMM = R(WidthMM);
        f.StandardWidthMM = StandardWidthMM is double s ? R(s) : null;
        f.InnerWidthMM = InnerWidthMM is double i ? R(i) : null;
        f.DashMM = DashMM?.Select(R).ToList();
        f.InnerDashMM = InnerDashMM?.Select(R).ToList();
        f.PointElements = PointElements?.Select(e =>
        {
            var c = e.Clone();
            c.LineWidthMM = R(e.LineWidthMM);
            c.DiameterMM = R(e.DiameterMM);
            c.Points = e.Points.Select(p => new Point2D(p.X * k, p.Y * k)).ToList();
            return c;
        }).ToList();
        return f;
    }
}

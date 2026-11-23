using Occa.Core.Geometry;

namespace Occa.Core.Vector;

/// <summary>One mark of an area pattern or line decoration, in the caller's units (metres, pixels…).</summary>
public abstract record OrnamentMark(string ColorHex)
{
    public sealed record Line(Point2D A, Point2D B, double WidthMM, string Color) : OrnamentMark(Color);
    public sealed record Dot(Point2D Center, double DiameterMM, string Color) : OrnamentMark(Color);
    public sealed record Polygon(List<Point2D> Points, string Color) : OrnamentMark(Color);
    /// <summary>Open polyline, butt caps and mitre joins (edges of a transparent double line).</summary>
    public sealed record Polyline(List<Point2D> Points, double WidthMM, string Color) : OrnamentMark(Color);
}

/// <summary>
/// The repeated parts of ISOM/ISSprOM symbols: area patterns and line decorations as pure geometry, shared by every
/// renderer. Port of <c>SymbolOrnaments</c> in CakraCore; the arithmetic is kept identical so the golden values match.
/// Coordinates are in any linear space with <c>unitsPerMM</c> units per paper millimetre; <c>yUp</c> tells which way north is.
/// </summary>
public static class SymbolOrnaments
{
    /// <summary>Above this many marks in one area the pattern is drawn as a tint instead (zoomed far out).</summary>
    public const int MaxMarks = 40_000;

    /// <summary>SplitMix64 of a seed and a grid cell: the same cell of the same object always gets the same numbers.</summary>
    public static double Random(ulong seed, int i, int j, ulong k = 0)
    {
        unchecked
        {
            ulong z = seed + (ulong)(long)i * 0x9E37_79B9_7F4A_7C15UL + (ulong)(long)j * 0xC2B2_AE3D_27D4_EB4FUL + k * 0x1656_67B1_9E37_79F9UL;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            z ^= z >> 31;
            return (z >> 11) / (double)(1UL << 53);
        }
    }

    // MARK: Area patterns

    /// <summary>Marks of a pattern covering rect, anchored to the coordinate origin. null = too many marks; use <see cref="Tint"/>.</summary>
    public static List<OrnamentMark>? PatternMarks(AreaPattern pattern, Rect2D rect, double unitsPerMM, bool yUp, int maxMarks = MaxMarks, ulong seed = 0)
    {
        double u = unitsPerMM;
        double sp = pattern.SpacingMM * u;
        if (!(sp > 0) || !(u > 0) || !(rect.MaxX > rect.MinX) || !(rect.MaxY > rect.MinY)) return new List<OrnamentMark>();
        double a = pattern.AngleDeg * Math.PI / 180;
        var d = new Point2D(Math.Sin(a), yUp ? Math.Cos(a) : -Math.Cos(a));
        var n = new Point2D(-d.Y, d.X);
        var corners = new[] { new Point2D(rect.MinX, rect.MinY), new Point2D(rect.MaxX, rect.MinY), new Point2D(rect.MinX, rect.MaxY), new Point2D(rect.MaxX, rect.MaxY) };
        (double, double) Range(Point2D v)
        {
            var p = corners.Select(c => c.X * v.X + c.Y * v.Y).ToArray();
            return (p.Min(), p.Max());
        }
        var (dn0, dn1) = Range(n);
        var (dd0, dd1) = Range(d);
        int lines = (int)Math.Ceiling((dn1 - dn0) / sp) + 1;
        double size = pattern.SizeMM;
        var color = pattern.ColorHex;
        if (pattern.PatternKind == AreaPattern.Kind.Lines)
        {
            if (lines > maxMarks) return null;
            var dash = (pattern.DashMM ?? new List<double>()).Select(v => v * u).ToList();
            double period = dash.Sum();
            bool dashed = dash.Count >= 2 && dash.All(v => v > 0);
            if (dashed && lines * (dd1 - dd0) / period > maxMarks) return null;
            var outLines = new List<OrnamentMark>();
            double k = Math.Floor(dn0 / sp);
            while (k * sp <= dn1)
            {
                var o = new Point2D(n.X * k * sp, n.Y * k * sp);
                Point2D Pt(double s) => new(o.X + d.X * s, o.Y + d.Y * s);
                if (dashed)
                {
                    double phase = pattern.Staggered == true && ((long)k) % 2 != 0 ? period / 2 : 0;
                    double s = Math.Floor((dd0 - phase) / period) * period + phase;
                    while (s <= dd1)
                    {
                        double t = s;
                        for (int i = 0; i < dash.Count; i++)
                        {
                            double len = dash[i];
                            if (i % 2 == 0 && t + len >= dd0 && t <= dd1)
                                outLines.Add(new OrnamentMark.Line(Pt(t), Pt(t + len), size, color));
                            t += len;
                        }
                        s += period;
                    }
                }
                else
                {
                    outLines.Add(new OrnamentMark.Line(Pt(dd0), Pt(dd1), size, color));
                }
                k += 1;
            }
            return outLines;
        }

        int perLine = (int)Math.Ceiling((dd1 - dd0) / sp) + 1;
        if ((long)lines * perLine > maxMarks) return null;
        var result = new List<OrnamentMark>();
        double r = Math.Max(size, pattern.SidesMM?.Max() ?? 0) * u / 2 + (pattern.JitterMM ?? 0) * u;
        bool isRandom = pattern.Random == true;
        double jitter = (pattern.JitterMM ?? 0) * u;
        double j = Math.Floor(dn0 / sp) - 1;
        while (j * sp <= dn1 + sp)
        {
            double shift = pattern.Staggered == true && ((long)j) % 2 != 0 ? 0.5 : 0;
            double i = Math.Floor(dd0 / sp) - 1;
            while (i * sp <= dd1 + sp)
            {
                int ii = (int)i, jj = (int)j;
                double fu = isRandom ? Random(seed, ii, jj, 1) : 0, fv = isRandom ? Random(seed, ii, jj, 2) : 0;
                var c = new Point2D(n.X * (j + fv) * sp + d.X * (i + shift + fu) * sp, n.Y * (j + fv) * sp + d.Y * (i + shift + fu) * sp);
                if (jitter > 0)
                {
                    double ang = Random(seed, ii, jj, 3) * 2 * Math.PI, dist = Random(seed, ii, jj, 4) * jitter;
                    c = new Point2D(c.X + Math.Cos(ang) * dist, c.Y + Math.Sin(ang) * dist);
                }
                if (c.X >= rect.MinX - r && c.X <= rect.MaxX + r && c.Y >= rect.MinY - r && c.Y <= rect.MaxY + r)
                {
                    if (pattern.PatternKind == AreaPattern.Kind.Triangles)
                    {
                        double rot = isRandom ? Random(seed, ii, jj, 5) * 360 : 0;
                        var pts = (pattern.SidesMM is { } sides ? Scalene(sides, c, rot, u, yUp) : null) ?? Triangle(c, size, u, yUp, rot);
                        result.Add(new OrnamentMark.Polygon(pts, color));
                    }
                    else
                    {
                        result.Add(new OrnamentMark.Dot(c, size, color));
                    }
                }
                i += 1;
            }
            j += 1;
        }
        return result;
    }

    /// <summary>Share of the area the pattern's marks cover (0…1): the colour strength when the marks are too small to draw.</summary>
    public static double Tint(AreaPattern p)
    {
        if (!(p.SpacingMM > 0)) return 0;
        double cell = p.SpacingMM * p.SpacingMM;
        switch (p.PatternKind)
        {
            case AreaPattern.Kind.Lines:
                double cover = p.SizeMM / p.SpacingMM;
                if (p.DashMM is { Count: >= 2 } d)
                {
                    double total = d.Sum(), on = d.Where((_, i) => i % 2 == 0).Sum();
                    if (total > 0) cover *= on / total;
                }
                return Math.Min(1, cover);
            case AreaPattern.Kind.Dots:
            case AreaPattern.Kind.Holes:
                return Math.Min(1, Math.PI * p.SizeMM * p.SizeMM / 4 / cell);
            default:
                if (p.SidesMM is { Count: 3 } s)
                {
                    double h = (s[0] + s[1] + s[2]) / 2;
                    return Math.Min(1, Math.Sqrt(h * (h - s[0]) * (h - s[1]) * (h - s[2])) / cell);
                }
                return Math.Min(1, 0.433 * p.SizeMM * p.SizeMM / cell);
        }
    }

    /// <summary>Pattern-only areas (marsh, boulder field…): the white "fill" is not painted, only the pattern.</summary>
    public static bool IsPatternOnly(string fillHex, List<AreaPattern>? patterns)
    {
        if (patterns is null || patterns.Count == 0) return false;
        return fillHex.Trim().TrimStart('#').ToUpperInvariant() == "FFFFFF";
    }

    internal static List<Point2D> Triangle(Point2D c, double sideMM, double u, bool yUp, double rotationDeg = 0)
    {
        double h = sideMM * Math.Sqrt(3) / 2;
        return Place(new[] { (0.0, 2 * h / 3), (-sideMM / 2, -h / 3), (sideMM / 2, -h / 3) }, c, rotationDeg, u, yUp);
    }

    internal static List<Point2D>? Scalene(List<double> sides, Point2D c, double rotationDeg, double u, bool yUp)
    {
        if (sides.Count != 3 || !(sides[0] > 0)) return null;
        double a = sides[0], b = sides[1], cc = sides[2];
        double x = (b * b - cc * cc + a * a) / (2 * a), y2 = b * b - x * x;
        if (!(y2 > 0)) return null;
        double gy = Math.Sqrt(y2) / 3, gx = (a + x) / 3;
        return Place(new[] { (0.0 - gx, 0.0 - gy), (a - gx, 0.0 - gy), (x - gx, Math.Sqrt(y2) - gy) }, c, rotationDeg, u, yUp);
    }

    private static List<Point2D> Place((double, double)[] pts, Point2D c, double rotationDeg, double u, bool yUp)
    {
        double t = rotationDeg * Math.PI / 180, up = yUp ? 1 : -1;
        return pts.Select(p =>
        {
            double x = p.Item1 * Math.Cos(t) + p.Item2 * Math.Sin(t), y = -p.Item1 * Math.Sin(t) + p.Item2 * Math.Cos(t);
            return new Point2D(c.X + x * u, c.Y + up * y * u);
        }).ToList();
    }

    // MARK: Line decorations

    /// <summary>Marks along a flattened line for the decoration and its More decorations. Tags, half dots, triangles and one-sided ticks go on the right side of the drawing direction.</summary>
    public static List<OrnamentMark> DecorationMarks(LineDecoration deco, IReadOnlyList<Point2D> points, double unitsPerMM, double lineWidthMM, string colorHex, bool yUp, List<double>? dashMM = null)
    {
        if (points.Count < 2 || !(unitsPerMM > 0)) return new List<OrnamentMark>();
        var path = new ArcPath(points);
        if (!(path.Total > 0)) return new List<OrnamentMark>();
        return deco.All.SelectMany(d => Single(d, path, unitsPerMM, lineWidthMM, colorHex, yUp, dashMM)).ToList();
    }

    /// <summary>A flattened line with arc-length lookup.</summary>
    public sealed class ArcPath
    {
        public readonly IReadOnlyList<Point2D> Points;
        public readonly List<(Point2D A, Point2D B, double Len, double Start)> Seg = new();
        public double Total;

        public ArcPath(IReadOnlyList<Point2D> points)
        {
            Points = points;
            for (int i = 1; i < points.Count; i++)
            {
                double l = points[i - 1].DistanceTo(points[i]);
                if (!(l > 0)) continue;
                Seg.Add((points[i - 1], points[i], l, Total));
                Total += l;
            }
        }

        /// <summary>Position and unit tangent at distance s along the line.</summary>
        public (Point2D P, Point2D T) At(double s)
        {
            s = Math.Min(Math.Max(s, 0), Total);
            int lo = 0, hi = Seg.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Seg[mid].Start + Seg[mid].Len < s) lo = mid + 1; else hi = mid;
            }
            var g = Seg[lo];
            double f = (s - g.Start) / g.Len;
            return (new Point2D(g.A.X + (g.B.X - g.A.X) * f, g.A.Y + (g.B.Y - g.A.Y) * f), new Point2D((g.B.X - g.A.X) / g.Len, (g.B.Y - g.A.Y) / g.Len));
        }
    }

    private static List<OrnamentMark> Single(LineDecoration deco, ArcPath path, double u, double lineWidthMM, string colorHex, bool yUp, List<double>? dashMM)
    {
        var points = path.Points;
        double total = path.Total;
        Point2D Right(Point2D t) => yUp ? new Point2D(t.Y, -t.X) : new Point2D(-t.Y, t.X);
        Point2D Add(Point2D p, Point2D v, double k) => new(p.X + v.X * k, p.Y + v.Y * k);
        double ang = deco.AngleDeg * Math.PI / 180;
        Point2D Slanted(Point2D t, double side = 1)
        {
            var r = Right(t);
            return new Point2D(t.X * Math.Cos(ang) + r.X * Math.Sin(ang) * side, t.Y * Math.Cos(ang) + r.Y * Math.Sin(ang) * side);
        }
        double half = lineWidthMM / 2 * u, size = deco.SizeMM * u, w = deco.WidthMM;
        string c = colorHex;
        OrnamentMark Tag(Point2D p, Point2D t, double side = 1) { var v = Slanted(t, side); return new OrnamentMark.Line(Add(p, v, half), Add(p, v, half + size), w, c); }
        OrnamentMark Tick(Point2D p, Point2D t) { var v = Slanted(t); return new OrnamentMark.Line(Add(p, v, -size / 2), Add(p, v, size / 2), w, c); }
        OrnamentMark Across(Point2D p, Point2D t, double length) { var r = Right(t); return new OrnamentMark.Line(Add(p, r, -length / 2), Add(p, r, length / 2), w, c); }
        List<OrnamentMark> Paired(Point2D p, Point2D t, double? pair, Func<Point2D, List<OrnamentMark>> mark)
        {
            if (pair is not double pr || !(pr > 0)) return mark(p);
            double o = pr / 2 * u;
            return mark(Add(p, t, -o)).Concat(mark(Add(p, t, o))).ToList();
        }

        var result = new List<OrnamentMark>();
        switch (deco.DecorationKind)
        {
            case LineDecoration.Kind.Edges:
            {
                double o = size / 2;
                return new List<OrnamentMark>
                {
                    new OrnamentMark.Polyline(Offset(points, o, yUp), w, c),
                    new OrnamentMark.Polyline(Offset(points, -o, yUp), w, c),
                };
            }
            case LineDecoration.Kind.Taper:
                return new List<OrnamentMark> { new OrnamentMark.Polygon(Tapered(path, lineWidthMM, deco.WidthMM, deco.SizeMM, u, yUp), c) };
            case LineDecoration.Kind.EndTicks:
            {
                var sides = deco.PairMM is null ? new[] { 1.0 } : new[] { 1.0, -1.0 };
                double from = deco.PairMM is double pm ? pm / 2 * u : half;
                foreach (var (s, dir) in new[] { (0.0, -1.0), (total, 1.0) })
                {
                    var (p, t) = path.At(s);
                    var out2 = new Point2D(t.X * dir, t.Y * dir);
                    foreach (var side in sides)
                    {
                        var r = Right(t);
                        var rs = new Point2D(r.X * side, r.Y * side);
                        var b = Add(p, rs, from);
                        var v = new Point2D(out2.X * Math.Cos(ang) + rs.X * Math.Sin(ang), out2.Y * Math.Cos(ang) + rs.Y * Math.Sin(ang));
                        result.Add(new OrnamentMark.Line(b, Add(b, v, size), w, c));
                    }
                }
                return result;
            }
        }

        // Spacing 0: marks at the ends (bridge / tunnel ticks) or at every vertex (power line bars).
        if (deco.SpacingMM <= 0 && deco.OnDashes != true)
        {
            if (deco.DecorationKind == LineDecoration.Kind.Bars)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    var a = points[Math.Max(0, i - 1)];
                    var b = points[Math.Min(points.Count - 1, i + 1)];
                    double l = a.DistanceTo(b);
                    if (!(l > 0)) continue;
                    result.Add(Across(points[i], new Point2D((b.X - a.X) / l, (b.Y - a.Y) / l), size));
                }
                return result;
            }
            foreach (var (s, dir) in new[] { (0.0, -1.0), (total, 1.0) })
            {
                var (p, t) = path.At(s);
                var t2 = new Point2D(t.X * dir, t.Y * dir);
                foreach (var side in new[] { 1.0, -1.0 })
                {
                    var r = Right(t2);
                    var b = Add(p, r, half * side);
                    var v = new Point2D(t2.X * Math.Cos(ang) + r.X * Math.Sin(ang) * side, t2.Y * Math.Cos(ang) + r.Y * Math.Sin(ang) * side);
                    result.Add(new OrnamentMark.Line(b, Add(b, v, size), w, c));
                }
            }
            return result;
        }

        // Positions along the line: centred on each dash, or evenly spaced with equal margins at both ends.
        var positions = new List<double>();
        if (deco.OnDashes == true && dashMM is { Count: >= 2 } dash && dash.All(v => v > 0))
        {
            var dd = dash.Select(v => v * u).ToList();
            double period = dd.Sum();
            if (total / period >= 20_000) return result;
            double s = 0;
            while (s < total)
            {
                double t = s;
                for (int i = 0; i < dd.Count; i++)
                {
                    if (i % 2 == 0 && t + dd[i] / 2 <= total) positions.Add(t + dd[i] / 2);
                    t += dd[i];
                }
                s += period;
            }
        }
        else
        {
            double sp = Math.Max(deco.SpacingMM, 0.01) * u;
            int count = (int)(total / sp);
            if (count >= 20_000) return result;
            double start = (total - Math.Max(count - 1, 0) * sp) / 2;
            int n = Math.Max(count, total > sp / 2 ? 1 : 0);
            for (int i = 0; i < n; i++) positions.Add(start + i * sp);
        }

        foreach (var s in positions)
        {
            var (p, t) = path.At(s);
            switch (deco.DecorationKind)
            {
                case LineDecoration.Kind.Tags:
                    result.Add(Tag(p, t));
                    break;
                case LineDecoration.Kind.DoubleTags:
                    result.AddRange(Paired(p, t, deco.PairMM ?? 0.5, q => new List<OrnamentMark> { Tag(q, t) }));
                    break;
                case LineDecoration.Kind.Dots:
                    result.AddRange(Paired(p, t, deco.PairMM, q => new List<OrnamentMark> { new OrnamentMark.Dot(q, deco.SizeMM, c) }));
                    break;
                case LineDecoration.Kind.HalfDots:
                {
                    var r = Right(t);
                    double rad = size / 2;
                    var centre = Add(p, r, (deco.OffsetMM ?? 0) * u);
                    var pts = Enumerable.Range(0, 13).Select(k =>
                    {
                        double a = Math.PI * k / 12;
                        return new Point2D(centre.X + (t.X * Math.Cos(a) + r.X * Math.Sin(a)) * rad, centre.Y + (t.Y * Math.Cos(a) + r.Y * Math.Sin(a)) * rad);
                    }).ToList();
                    result.Add(new OrnamentMark.Polygon(pts, c));
                    break;
                }
                case LineDecoration.Kind.Ticks:
                    result.Add(Tick(p, t));
                    break;
                case LineDecoration.Kind.DoubleTicks:
                    result.AddRange(Paired(p, t, deco.PairMM ?? 0.5, q => new List<OrnamentMark> { Tick(q, t) }));
                    break;
                case LineDecoration.Kind.Bars:
                case LineDecoration.Kind.Rungs:
                    result.Add(Across(p, t, size));
                    break;
                case LineDecoration.Kind.Chevrons:
                    result.AddRange(Paired(p, t, deco.PairMM, v => new[] { 1.0, -1.0 }.Select(side => (OrnamentMark)new OrnamentMark.Line(v, Add(v, Slanted(t, side), size), w, c)).ToList()));
                    break;
                case LineDecoration.Kind.Triangles:
                {
                    var r = Right(t);
                    double h = size * Math.Sqrt(3) / 2;
                    result.Add(new OrnamentMark.Polygon(new List<Point2D> { Add(p, t, -size / 2), Add(p, t, size / 2), Add(p, r, h) }, c));
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>The polyline moved sideways by d units (positive = right of the drawing direction), with mitre joins.</summary>
    public static List<Point2D> Offset(IReadOnlyList<Point2D> points, double d, bool yUp)
    {
        var pts = new List<Point2D>();
        for (int i = 0; i < points.Count; i++)
            if (i == 0 || points[i].DistanceTo(points[i - 1]) > 1e-12) pts.Add(points[i]);
        if (pts.Count < 2) return pts;
        Point2D Normal(Point2D a, Point2D b)
        {
            double l = a.DistanceTo(b);
            var t = new Point2D((b.X - a.X) / l, (b.Y - a.Y) / l);
            return yUp ? new Point2D(t.Y, -t.X) : new Point2D(-t.Y, t.X);
        }
        bool closed = pts.Count > 2 && pts[0].DistanceTo(pts[^1]) < 1e-9;
        var result = new List<Point2D>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            Point2D? prev = i > 0 ? Normal(pts[i - 1], pts[i]) : (closed ? Normal(pts[^2], pts[0]) : null);
            Point2D? next = i < pts.Count - 1 ? Normal(pts[i], pts[i + 1]) : (closed ? Normal(pts[0], pts[1]) : null);
            if (prev is { } a && next is { } b)
            {
                var m = new Point2D(a.X + b.X, a.Y + b.Y);
                double dot = 1 + a.X * b.X + a.Y * b.Y;
                result.Add(dot > 0.15 ? new Point2D(pts[i].X + m.X * d / dot, pts[i].Y + m.Y * d / dot) : new Point2D(pts[i].X + b.X * d, pts[i].Y + b.Y * d));
            }
            else if (prev is { } a2) result.Add(new Point2D(pts[i].X + a2.X * d, pts[i].Y + a2.Y * d));
            else if (next is { } b2) result.Add(new Point2D(pts[i].X + b2.X * d, pts[i].Y + b2.Y * d));
            else result.Add(pts[i]);
        }
        return result;
    }

    /// <summary>Outline of a line whose ends narrow to tipMM over lengthMM (107 erosion gully).</summary>
    public static List<Point2D> Tapered(ArcPath path, double widthMM, double tipMM, double lengthMM, double u, bool yUp)
    {
        double total = path.Total, taper = Math.Min(lengthMM * u, total / 2);
        var s = new List<double> { 0 };
        foreach (var g in path.Seg) s.Add(g.Start + g.Len);
        if (taper > 0) for (int k = 1; k <= 8; k++) { s.Add(taper * k / 8); s.Add(total - taper * k / 8); }
        var samples = s.Select(x => Math.Round(x * 1e6) / 1e6).Distinct().OrderBy(x => x).ToList();
        var left = new List<Point2D>();
        var right = new List<Point2D>();
        foreach (var x in samples)
        {
            var (p, t) = path.At(x);
            double f = taper > 0 ? Math.Min(1, Math.Min(x, total - x) / taper) : 1;
            double hw = (tipMM + (widthMM - tipMM) * f) / 2 * u;
            var r = yUp ? new Point2D(t.Y, -t.X) : new Point2D(-t.Y, t.X);
            right.Add(new Point2D(p.X + r.X * hw, p.Y + r.Y * hw));
            left.Add(new Point2D(p.X - r.X * hw, p.Y - r.Y * hw));
        }
        right.Reverse();
        return left.Concat(right).ToList();
    }

    // MARK: Geometry helpers

    /// <summary>A feature's outline as a polyline (curves flattened in fixed steps), mapped by transform.</summary>
    public static List<Point2D> Polyline(Feature f, Func<Point2D, Point2D> transform, int steps = 8)
    {
        var result = new List<Point2D>();
        if (f.Nodes.Count == 0) return result;
        result.Add(transform(f.Nodes[0].Point));
        foreach (var s in f.Segments)
        {
            if (s.IsCurve)
            {
                for (int i = 1; i <= steps; i++)
                {
                    double t = (double)i / steps, m = 1 - t;
                    double x = m * m * m * s.P0.X + 3 * m * m * t * s.C1.X + 3 * m * t * t * s.C2.X + t * t * t * s.P3.X;
                    double y = m * m * m * s.P0.Y + 3 * m * m * t * s.C1.Y + 3 * m * t * t * s.C2.Y + t * t * t * s.P3.Y;
                    result.Add(transform(new Point2D(x, y)));
                }
            }
            else
            {
                result.Add(transform(s.P3));
            }
        }
        if (f.ClosesPath) result.Add(result[0]);
        return result;
    }
}

using Occa.Core.Geometry;

namespace Occa.Core.Vector;

/// <summary>How a corner is cut off: a circular arc or a straight chamfer.</summary>
public enum CornerStyle { Rounded, Straight }

/// <summary>The sharp corner at one node: where its two edges leave it and how much room there is to round it.</summary>
public readonly record struct CornerGeometry(Point2D Point, Point2D TowardPrevious, Point2D TowardNext, double InteriorAngle, double MaxTangentDistance)
{
    public Point2D Bisector
    {
        get
        {
            var s = TowardPrevious + TowardNext;
            double l = s.Length;
            return l > 1e-12 ? s * (1 / l) : Point2D.Zero;
        }
    }

    public double TangentDistance(double radius) => radius > 0 ? Math.Min(radius / Math.Tan(InteriorAngle / 2), MaxTangentDistance) : 0;
    public double EffectiveRadius(double r) => TangentDistance(r) * Math.Tan(InteriorAngle / 2);
    public double MaxRadius => MaxTangentDistance * Math.Tan(InteriorAngle / 2);
}

/// <summary>Corner Tool (Affinity "Corner Tool"): round sharp corners selectively. Port of FeatureCorners.swift.</summary>
public static class FeatureCorners
{
    private const int LengthSteps = 64;

    public static CornerGeometry? Corner(this Feature f, int i)
    {
        if (f.Kind == FeatureKind.Point || f.RingLocation(i) is not { } loc) return null;
        return RingCorner(f.RingFeature(loc.Ring), loc.Index);
    }

    public static List<int> RoundableCornerIndices(this Feature f) => Enumerable.Range(0, f.AllNodes().Count).Where(i => f.Corner(i) is not null).ToList();

    public static Feature RoundingCorners(this Feature f, IEnumerable<int> indices, double radius, CornerStyle style = CornerStyle.Rounded)
        => f.RoundingCorners(indices.Distinct().ToDictionary(i => i, _ => radius), style);

    /// <summary>Round each node in radii (Node Tool index → radius in metres) and return the new object.</summary>
    public static Feature RoundingCorners(this Feature f, IReadOnlyDictionary<int, double> radii, CornerStyle style = CornerStyle.Rounded)
    {
        if (f.Kind == FeatureKind.Point) return f;
        var perRing = new Dictionary<int, Dictionary<int, double>>();
        foreach (var (i, r) in radii)
        {
            if (!(r > 0) || f.RingLocation(i) is not { } loc) continue;
            if (!perRing.TryGetValue(loc.Ring, out var d)) perRing[loc.Ring] = d = new Dictionary<int, double>();
            d[loc.Index] = r;
        }
        if (perRing.Count == 0) return f;
        var result = f.Clone();
        foreach (var (ring, local) in perRing) result.SetRing(ring, RoundedRingNodes(f.RingFeature(ring), local, style));
        return result;
    }

    private static CornerGeometry? RingCorner(Feature f, int i)
    {
        int n = f.Nodes.Count;
        if (i < 0 || i >= n || n < 3) return null;
        if (!f.IsClosed && (i == 0 || i == n - 1)) return null;
        var segs = f.Segments;
        var inSeg = segs[(i - 1 + segs.Count) % segs.Count];
        var outSeg = segs[i % segs.Count];
        if (Unit(EndTangentBackward(inSeg)) is not { } u || Unit(StartTangent(outSeg)) is not { } v) return null;
        double dot = Math.Clamp(u.X * v.X + u.Y * v.Y, -1, 1);
        double theta = Math.Acos(dot);
        if (!(Math.PI - theta > 0.5 * Math.PI / 180) || !(theta > 1e-6)) return null;
        double lin = ArcLength(inSeg), lout = ArcLength(outSeg);
        if (!(lin > 1e-9) || !(lout > 1e-9)) return null;
        return new CornerGeometry(f.Nodes[i].Point, u, v, theta, Math.Min(lin, lout) / 2);
    }

    private static List<BezierNode> RoundedRingNodes(Feature f, Dictionary<int, double> radii, CornerStyle style)
    {
        int n = f.Nodes.Count;
        var segs = f.Segments;
        if (n < 3 || segs.Count == 0) return f.Nodes;
        var trim = new Dictionary<int, (double D, CornerGeometry Corner)>();
        foreach (var (i, r) in radii)
        {
            if (RingCorner(f, i) is not { } c) continue;
            double d = c.TangentDistance(r);
            if (d > 1e-9) trim[i] = (d, c);
        }
        if (trim.Count == 0) return f.Nodes;
        var pieces = new List<CubicSegment>();
        for (int k = 0; k < segs.Count; k++)
        {
            var s = segs[k];
            double a = trim.TryGetValue(k, out var ta) ? ta.D : 0, b = trim.TryGetValue((k + 1) % n, out var tb) ? tb.D : 0;
            if (!(a > 0) && !(b > 0)) { pieces.Add(s); continue; }
            double len = ArcLength(s);
            double t0 = a > 0 ? Parameter(s, a) : 0;
            double t1 = b > 0 ? Parameter(s, len - b) : 1;
            pieces.Add(s.Sub(t0, Math.Max(t0, t1)));
        }
        var result = new List<BezierNode>();
        for (int i = 0; i < n; i++)
        {
            CubicSegment? inPiece = (f.IsClosed || i > 0) ? pieces[(i - 1 + segs.Count) % segs.Count] : null;
            CubicSegment? outPiece = i < segs.Count ? pieces[i] : null;
            if (trim.TryGetValue(i, out var t) && inPiece is { } a && outPiece is { } b)
            {
                var p1 = a.P3;
                var p2 = b.P0;
                var first = new BezierNode(p1, a.IsCurve ? a.C2 : null);
                var second = new BezierNode(p2, null, b.IsCurve ? b.C1 : null);
                var d1 = Unit(EndTangentForward(a)) ?? Unit(t.Corner.Point - p1);
                var d2 = Unit(StartTangent(b)) ?? Unit(p2 - t.Corner.Point);
                if (style == CornerStyle.Rounded && d1 is { } dd1 && d2 is { } dd2)
                {
                    double phi = Math.Acos(Math.Clamp(dd1.X * dd2.X + dd1.Y * dd2.Y, -1, 1));
                    double chord = p1.DistanceTo(p2);
                    double k;
                    if (phi > 1e-9 && Math.Sin(phi / 2) > 1e-12)
                    {
                        double r = chord / (2 * Math.Sin(phi / 2));
                        k = 4.0 / 3.0 * Math.Tan(phi / 4) * r;
                    }
                    else k = chord / 3;
                    first.OutHandle = p1 + dd1 * k;
                    second.InHandle = p2 - dd2 * k;
                }
                result.Add(first);
                result.Add(second);
            }
            else
            {
                var node = f.Nodes[i].Clone();
                node.InHandle = inPiece is { IsCurve: true } ip ? ip.C2 : null;
                node.OutHandle = outPiece is { IsCurve: true } op ? op.C1 : null;
                if (!f.IsClosed && i == 0) node.InHandle = f.Nodes[i].InHandle;
                if (!f.IsClosed && i == n - 1) node.OutHandle = f.Nodes[i].OutHandle;
                result.Add(node);
            }
        }
        return MergingCoincident(result, f.IsClosed);
    }

    private static List<BezierNode> MergingCoincident(List<BezierNode> nodes, bool closed)
    {
        var result = new List<BezierNode>();
        foreach (var n in nodes)
        {
            if (result.Count > 0 && result[^1].Point.DistanceTo(n.Point) < 1e-9)
                result[^1] = new BezierNode(result[^1].Point, result[^1].InHandle, n.OutHandle);
            else result.Add(n);
        }
        if (closed && result.Count > 2 && result[0].Point.DistanceTo(result[^1].Point) < 1e-9)
        {
            result[0] = new BezierNode(result[0].Point, result[^1].InHandle, result[0].OutHandle);
            result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    private static Point2D? Unit(Point2D v)
    {
        double l = v.Length;
        return l > 1e-12 ? v * (1 / l) : null;
    }

    private static Point2D StartTangent(CubicSegment s)
    {
        foreach (var q in new[] { s.C1, s.C2, s.P3 }) if (q.DistanceTo(s.P0) > 1e-12) return q - s.P0;
        return Point2D.Zero;
    }

    private static Point2D EndTangentForward(CubicSegment s)
    {
        foreach (var q in new[] { s.C2, s.C1, s.P0 }) if (q.DistanceTo(s.P3) > 1e-12) return s.P3 - q;
        return Point2D.Zero;
    }

    private static Point2D EndTangentBackward(CubicSegment s) => EndTangentForward(s) * -1;

    /// <summary>Length of the segment (exact for straight segments, a 64-step polyline for curves).</summary>
    public static double ArcLength(CubicSegment s)
    {
        if (!s.IsCurve) return s.P0.DistanceTo(s.P3);
        double len = 0;
        var prev = s.P0;
        for (int k = 1; k <= LengthSteps; k++)
        {
            var q = s.PointAt((double)k / LengthSteps);
            len += prev.DistanceTo(q);
            prev = q;
        }
        return len;
    }

    /// <summary>Parameter t at which the length along the segment from P0 is length.</summary>
    public static double Parameter(CubicSegment s, double length)
    {
        double total = ArcLength(s);
        if (!(total > 1e-12)) return 0;
        if (length <= 0) return 0;
        if (length >= total) return 1;
        if (!s.IsCurve) return length / total;
        double acc = 0;
        var prev = s.P0;
        for (int k = 1; k <= LengthSteps; k++)
        {
            double t = (double)k / LengthSteps;
            var q = s.PointAt(t);
            double step = prev.DistanceTo(q);
            if (acc + step >= length)
            {
                double f = step > 0 ? (length - acc) / step : 0;
                return t - (1 - f) / LengthSteps;
            }
            acc += step;
            prev = q;
        }
        return 1;
    }
}

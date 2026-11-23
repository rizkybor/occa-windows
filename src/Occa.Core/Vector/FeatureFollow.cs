using Occa.Core.Geometry;

namespace Occa.Core.Vector;

/// <summary>Position along an object's outline: segment number and parameter t (0…1) within that segment.</summary>
public readonly record struct PathPosition(int Segment, double T)
{
    /// <summary>Order along the path (for comparing two positions).</summary>
    public double Order => Segment + T;
}

public static class CubicSegmentExtensions
{
    /// <summary>Curve piece between t0 and t1 (de Casteljau). Straight segments stay straight.</summary>
    public static CubicSegment Sub(this CubicSegment s, double t0, double t1)
    {
        if (!s.IsCurve)
        {
            var a = s.PointAt(t0);
            var b = s.PointAt(t1);
            return new CubicSegment(a, a, b, b, false);
        }
        var right = t0 <= 0 ? s : s.Split(t0).Item2;
        double u = t0 >= 1 ? 0 : (t1 - t0) / (1 - t0);
        return u >= 1 ? right : right.Split(u).Item1;
    }

    /// <summary>Split at t into two curves.</summary>
    public static (CubicSegment, CubicSegment) Split(this CubicSegment s, double t)
    {
        Point2D Lerp(Point2D a, Point2D b) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        var a1 = Lerp(s.P0, s.C1);
        var b1 = Lerp(s.C1, s.C2);
        var c1 = Lerp(s.C2, s.P3);
        var d = Lerp(a1, b1);
        var e = Lerp(b1, c1);
        var m = Lerp(d, e);
        return (new CubicSegment(s.P0, a1, d, m, s.IsCurve), new CubicSegment(m, e, c1, s.P3, s.IsCurve));
    }

    /// <summary>Nearest point on the segment: (t, point, distance).</summary>
    public static (double T, Point2D Point, double Distance) Nearest(this CubicSegment s, Point2D p)
    {
        if (!s.IsCurve)
        {
            var d = s.P3 - s.P0;
            double len2 = d.X * d.X + d.Y * d.Y;
            double t = len2 > 0 ? Math.Clamp(((p.X - s.P0.X) * d.X + (p.Y - s.P0.Y) * d.Y) / len2, 0, 1) : 0;
            var q = s.PointAt(t);
            return (t, q, q.DistanceTo(p));
        }
        var best = (T: 0.0, Point: s.P0, Distance: s.P0.DistanceTo(p));
        const int n = 32;
        for (int k = 0; k <= n; k++)
        {
            double t = (double)k / n;
            var q = s.PointAt(t);
            double dd = q.DistanceTo(p);
            if (dd < best.Distance) best = (t, q, dd);
        }
        double step = 1.0 / n;
        for (int i = 0; i < 12; i++)
        {
            step /= 2;
            foreach (var t in new[] { best.T - step, best.T + step })
            {
                if (t < 0 || t > 1) continue;
                var q = s.PointAt(t);
                double dd = q.DistanceTo(p);
                if (dd < best.Distance) best = (t, q, dd);
            }
        }
        return best;
    }

    /// <summary>Approximate length (16-step polyline).</summary>
    public static double ApproximateLength(this CubicSegment s)
    {
        if (!s.IsCurve) return s.P0.DistanceTo(s.P3);
        double len = 0;
        var prev = s.P0;
        for (int k = 1; k <= 16; k++)
        {
            var q = s.PointAt(k / 16.0);
            len += prev.DistanceTo(q);
            prev = q;
        }
        return len;
    }

    /// <summary>The same segment run backwards.</summary>
    public static CubicSegment Reversed(this CubicSegment s) => new(s.P3, s.C2, s.C1, s.P0, s.IsCurve);
}

public static class FeatureFollow
{
    /// <summary>Nearest point on the object's outline (line or area, outer ring only).</summary>
    public static (PathPosition Position, Point2D Point, double Distance)? NearestPathPosition(this Feature f, Point2D p)
    {
        if (f.Kind == FeatureKind.Point) return null;
        (PathPosition, Point2D, double)? best = null;
        var segs = f.Segments;
        for (int i = 0; i < segs.Count; i++)
        {
            var r = segs[i].Nearest(p);
            if (r.Distance < (best?.Item3 ?? double.PositiveInfinity)) best = (new PathPosition(i, r.T), r.Point, r.Distance);
        }
        return best;
    }

    /// <summary>Position of the i-th node on the path.</summary>
    public static PathPosition PathPositionOfNode(this Feature f, int i)
    {
        int count = f.Segments.Count;
        if (!f.IsClosed && i >= count) return new PathPosition(Math.Max(count - 1, 0), 1);
        return new PathPosition(i % Math.Max(count, 1), 0);
    }

    public static Point2D? PointAt(this Feature f, PathPosition pos)
    {
        var segs = f.Segments;
        if (pos.Segment < 0 || pos.Segment >= segs.Count) return f.Nodes.Count > 0 ? f.Nodes[0].Point : null;
        return segs[pos.Segment].PointAt(pos.T);
    }

    private static List<CubicSegment> ForwardSegments(Feature f, PathPosition a, PathPosition b)
    {
        var segs = f.Segments;
        var result = new List<CubicSegment>();
        if (segs.Count == 0) return result;
        int n = segs.Count;
        if (a.Segment == b.Segment && a.T <= b.T)
        {
            if (b.T - a.T > 1e-9) result.Add(segs[a.Segment].Sub(a.T, b.T));
            return result;
        }
        if (a.T < 1) result.Add(segs[a.Segment].Sub(a.T, 1));
        int k = (a.Segment + 1) % n, guardCount = 0;
        while (k != b.Segment && guardCount < n)
        {
            result.Add(segs[k]);
            k = (k + 1) % n;
            guardCount++;
        }
        if (b.T > 0) result.Add(segs[b.Segment].Sub(0, b.T));
        return result;
    }

    /// <summary>Object shape between two positions as nodes (both inclusive). Lines follow the position order; areas and closed lines take the shorter side, or the side nearer <paramref name="via"/>.</summary>
    public static List<BezierNode> FollowNodes(this Feature f, PathPosition a, PathPosition b, Point2D? via = null)
    {
        List<CubicSegment> segs;
        if (f.IsClosed || f.IsRingLine)
        {
            var fwd = ForwardSegments(f, a, b);
            var back = ReversedSegments(ForwardSegments(f, b, a));
            if (via is { } v)
            {
                double Gap(List<CubicSegment> s) => s.Count == 0 ? double.PositiveInfinity : s.Min(x => x.Nearest(v).Distance);
                segs = Gap(fwd) <= Gap(back) ? fwd : back;
            }
            else
            {
                segs = fwd.Sum(x => x.ApproximateLength()) <= back.Sum(x => x.ApproximateLength()) ? fwd : back;
            }
        }
        else if (a.Order <= b.Order) segs = ForwardSegments(f, a, b);
        else segs = ReversedSegments(ForwardSegments(f, b, a));
        return NodesFrom(segs);
    }

    /// <summary>Path piece forward from a to b in node order (areas may wrap past the start).</summary>
    public static List<BezierNode> ForwardNodes(this Feature f, PathPosition a, PathPosition b) => NodesFrom(ForwardSegments(f, a, b));

    public static List<BezierNode> NodesFrom(IEnumerable<CubicSegment> segments)
    {
        var segs = segments.Where(s => !(s.P0.DistanceTo(s.P3) < 1e-9 && !s.IsCurve)).ToList();
        var nodes = new List<BezierNode>();
        if (segs.Count == 0) return nodes;
        nodes.Add(new BezierNode(segs[0].P0));
        foreach (var s in segs)
        {
            if (s.IsCurve) nodes[^1].OutHandle = s.C1;
            nodes.Add(new BezierNode(s.P3, s.IsCurve ? s.C2 : null));
        }
        return nodes;
    }

    public static List<CubicSegment> ReversedSegments(IReadOnlyList<CubicSegment> segs)
    {
        var result = new List<CubicSegment>(segs.Count);
        for (int i = segs.Count - 1; i >= 0; i--) result.Add(segs[i].Reversed());
        return result;
    }
}

public static class PolylineSimplify
{
    /// <summary>Douglas–Peucker simplification (<c>ContourGenerator.simplify</c> on Mac).</summary>
    public static List<Point2D> Simplify(IReadOnlyList<Point2D> pts, double tolerance)
    {
        if (pts.Count <= 2 || !(tolerance > 0)) return pts.ToList();
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b <= a + 1) continue;
            double best = 0;
            int index = a;
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
        return pts.Where((_, i) => keep[i]).ToList();
    }
}

/// <summary>Freehand drawing along existing objects: a stroke part hugging one object (within radius) for at least 2 × radius is replaced by that object's exact shape.</summary>
public static class GuideTrace
{
    public static List<BezierNode> Trace(IReadOnlyList<Point2D> points, IReadOnlyList<Feature> guides, double radius, double simplifyTolerance)
    {
        if (points.Count < 2) return points.Select(p => new BezierNode(p)).ToList();
        var candidates = guides.Where(g => g.Kind != FeatureKind.Point).ToList();
        var hits = points.Select(p =>
        {
            (int, PathPosition, Point2D, double)? best = null;
            for (int k = 0; k < candidates.Count; k++)
            {
                var f = candidates[k];
                if (f.Bounds is not { } b || p.X < b.MinX - radius || p.X > b.MaxX + radius || p.Y < b.MinY - radius || p.Y > b.MaxY + radius) continue;
                var r = f.NearestPathPosition(p);
                if (r is null || r.Value.Distance > radius || r.Value.Distance >= (best?.Item4 ?? double.PositiveInfinity)) continue;
                best = (k, r.Value.Position, r.Value.Point, r.Value.Distance);
            }
            return best is { } h ? ((int, PathPosition, Point2D)?)(h.Item1, h.Item2, h.Item3) : null;
        }).ToList();
        var result = new List<BezierNode>();
        var free = new List<Point2D>();
        void Flush()
        {
            if (free.Count == 0) return;
            var simple = free.Count > 2 ? PolylineSimplify.Simplify(free, simplifyTolerance) : free;
            foreach (var p in simple)
                if (result.Count == 0 || result[^1].Point.DistanceTo(p) > 1e-9) result.Add(new BezierNode(p));
            free = new List<Point2D>();
        }
        int i = 0;
        while (i < points.Count)
        {
            if (hits[i] is not { } h) { free.Add(points[i]); i++; continue; }
            int j = i;
            while (j + 1 < points.Count && hits[j + 1] is { } n && n.Item1 == h.Item1) j++;
            double length = 0;
            for (int k = i; k < j; k++) length += points[k].DistanceTo(points[k + 1]);
            if (length >= radius * 2 && hits[j] is { } last)
            {
                Flush();
                var via = points[(i + j) / 2];
                foreach (var node in candidates[h.Item1].FollowNodes(h.Item2, last.Item2, via))
                {
                    if (result.Count > 0 && result[^1].Point.DistanceTo(node.Point) <= 1e-9) result[^1].OutHandle = node.OutHandle;
                    else result.Add(node);
                }
            }
            else
            {
                for (int k = i; k <= j; k++) free.Add(points[k]);
            }
            i = j + 1;
        }
        Flush();
        return result;
    }
}

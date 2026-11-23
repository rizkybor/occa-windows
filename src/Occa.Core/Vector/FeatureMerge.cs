using Occa.Core.Geometry;

namespace Occa.Core.Vector;

/// <summary>Result of merging two objects.</summary>
public sealed record MergeResult(Feature Feature, bool FlattenedCurves, bool FormedHole);

/// <summary>Merge objects (OCAD "Merge" / OOM "Unify areas" + "Connect paths") and fill a line with an area. Port of FeatureMerge.swift.</summary>
public static class FeatureMerge
{
    /// <summary>Same kind (line–line or area–area) and same symbol; objects without a symbol match on colour.</summary>
    public static bool CanMerge(this Feature f, Feature o)
    {
        if (f.Id == o.Id || f.Kind != o.Kind || f.Kind == FeatureKind.Point) return false;
        if (f.SymbolCode is { } a) return a == o.SymbolCode;
        return o.SymbolCode is null && string.Equals(f.ColorHex, o.ColorHex, StringComparison.OrdinalIgnoreCase);
    }

    public static double EndpointGap(this Feature f, Feature o)
    {
        if (f.Kind != FeatureKind.Line || o.Kind != FeatureKind.Line || f.Nodes.Count == 0 || o.Nodes.Count == 0) return double.PositiveInfinity;
        var a0 = f.Nodes[0].Point; var a1 = f.Nodes[^1].Point; var b0 = o.Nodes[0].Point; var b1 = o.Nodes[^1].Point;
        return new[] { a0.DistanceTo(b0), a0.DistanceTo(b1), a1.DistanceTo(b0), a1.DistanceTo(b1) }.Min();
    }

    /// <summary>Join two lines at the nearest pair of endpoints (≤ tolerance). The result keeps the identity and style of f.</summary>
    public static Feature? JoiningLine(this Feature f, Feature o, double tolerance)
    {
        if (!f.CanMerge(o) || f.Kind != FeatureKind.Line || f.Nodes.Count < 2 || o.Nodes.Count < 2) return null;
        var a0 = f.Nodes[0].Point; var a1 = f.Nodes[^1].Point; var b0 = o.Nodes[0].Point; var b1 = o.Nodes[^1].Point;
        var options = new (double D, List<BezierNode> Front, List<BezierNode> Back, bool SelfEnd, bool OtherEnd)[]
        {
            (a1.DistanceTo(b0), f.Nodes, o.Nodes, true, false),
            (a1.DistanceTo(b1), f.Nodes, o.ReversedNodes(), true, true),
            (a0.DistanceTo(b0), f.ReversedNodes(), o.Nodes, false, false),
            (a0.DistanceTo(b1), o.Nodes, f.Nodes, false, true),
        };
        var best = options.MinBy(x => x.D);
        if (best.D > tolerance) return null;
        const double overlap = 0.05;
        var fs = f.Segments; var os = o.Segments;
        var sSelf = best.SelfEnd ? fs[^1] : fs[0];
        if (o.NearestPathPosition(sSelf.PointAt(0.5)) is { } r1 && r1.Distance <= overlap) return null;
        var sOther = best.OtherEnd ? os[^1] : os[0];
        if (f.NearestPathPosition(sOther.PointAt(0.5)) is { } r2 && r2.Distance <= overlap) return null;
        var head = best.Front.Select(n => n.Clone()).ToList();
        var tail = best.Back.Select(n => n.Clone()).ToList();
        var end = head[^1]; head.RemoveAt(head.Count - 1);
        var start = tail[0]; tail.RemoveAt(0);
        var meet = new Point2D((end.Point.X + start.Point.X) / 2, (end.Point.Y + start.Point.Y) / 2);
        var joint = new BezierNode(meet, end.InHandle + (meet - end.Point), start.OutHandle + (meet - start.Point));
        var result = f.Clone();
        result.Nodes = head.Concat(new[] { joint }).Concat(tail).ToList();
        return result;
    }

    /// <summary>Unite two areas. Adjoining areas are stitched without changing curves; overlapping areas are united exactly or via polygons. null = not touching.</summary>
    public static MergeResult? UnitingArea(this Feature f, Feature o, double vertexTolerance, double flattenTolerance, bool allowOverlap = true)
    {
        if (!f.CanMerge(o) || f.Kind != FeatureKind.Area || f.Nodes.Count < 3 || o.Nodes.Count < 3) return null;
        int holesBefore = f.HoleRings.Count + o.HoleRings.Count;
        MergeResult Result(Feature r, bool flattened) => new(r, flattened, r.HoleRings.Count > holesBefore);
        if (StitchingArea(f, o, vertexTolerance) is { } st) return Result(KeepingHoles(st, f, o), false);
        var a2 = InsertingVertices(f, o, vertexTolerance);
        var b2 = InsertingVertices(o, f, vertexTolerance);
        if ((a2.Nodes.Count != f.Nodes.Count || b2.Nodes.Count != o.Nodes.Count) && StitchingArea(a2, b2, vertexTolerance) is { } st2)
            return Result(KeepingHoles(st2, f, o), false);
        bool ringOfSides = SharedSideChains(f, o, vertexTolerance) > 1 || SharedSideChains(a2, b2, vertexTolerance) > 1;
        if (!(allowOverlap || ringOfSides) || f.Bounds is not { } ba || o.Bounds is not { } bb || !ba.InsetBy(-vertexTolerance).Intersects(bb)) return null;
        if (ShapeCut.Region.Of(f, vertexTolerance) is { } ra && ShapeCut.Region.Of(o, vertexTolerance) is { } rb
            && ShapeCut.Combine(ra, rb, ShapeCut.BooleanOp.Union, vertexTolerance) is { } u)
        {
            var pieces = FeatureCut.Pieces(f, u.Loops, ra, u.Points, vertexTolerance);
            if (pieces.Pieces is not { } list) return null;
            return list.Count == 1 ? Result(list[0], false) : null;
        }
        if (!allowOverlap) return null;
        var pa = PolygonUnion.Ring(f.Flattened(flattenTolerance));
        var pb = PolygonUnion.Ring(o.Flattened(flattenTolerance));
        if (PolygonUnion.Union(pa, pb, Math.Max(vertexTolerance, 1e-6)) is not { } un) return null;
        var g = f.Clone();
        g.Nodes = un.Outer.Select(p => new BezierNode(p)).ToList();
        g.Holes = un.Holes.Select(h => h.Select(p => new BezierNode(p)).ToList()).ToList();
        g = KeepingHoles(g, f, o, replacing: false);
        g.RotationDeg = f.RotationDeg;
        bool curved = f.Nodes.Any(n => n.IsSmooth) || o.Nodes.Any(n => n.IsSmooth);
        return Result(g, curved);
    }

    private static Feature KeepingHoles(Feature merged, Feature a, Feature b, bool replacing = true)
    {
        var own = replacing ? new List<List<BezierNode>>() : merged.HoleRings.ToList();
        var all = own.Concat(a.HoleRings).Concat(b.HoleRings).Select(h => h.Select(n => n.Clone()).ToList()).ToList();
        merged.Holes = all.Count == 0 ? null : all;
        merged.NormalizeHoles();
        return merged;
    }

    /// <summary>Copy with an extra node wherever a corner of o lies exactly on a side of this object.</summary>
    public static Feature InsertingVertices(this Feature f, Feature o, double tolerance)
    {
        var g = f.Clone();
        foreach (var v in o.Nodes)
        {
            if (g.Nodes.Any(n => n.Point.DistanceTo(v.Point) <= tolerance)) continue;
            if (g.NearestPathPosition(v.Point) is not { } r || r.Distance > tolerance) continue;
            if (g.InsertNode(r.Position) is not { } i) continue;
            g.Nodes[i] = g.Nodes[i].MovedBy(v.Point - g.Nodes[i].Point);
        }
        return g;
    }

    private static (int?[] Match, int Count, Func<int, bool> Shared) SharedSides(Feature f, Feature o, double tolerance)
    {
        var a = f.SignedArea() >= 0 ? f.Nodes : f.ReversedNodes();
        var b = o.SignedArea() >= 0 ? o.Nodes : o.ReversedNodes();
        int n = a.Count, m = b.Count;
        var match = new int?[n];
        for (int i = 0; i < n; i++)
        {
            (int, double)? best = null;
            for (int k = 0; k < m; k++)
            {
                double d = a[i].Point.DistanceTo(b[k].Point);
                if (d <= tolerance && d < (best?.Item2 ?? double.PositiveInfinity)) best = (k, d);
            }
            match[i] = best?.Item1;
        }
        bool Shared(int i) => match[i] is int p && match[(i + 1) % n] is int q && q == (p - 1 + m) % m;
        return (match, n, Shared);
    }

    /// <summary>Number of separate chains of shared sides between the outer rings of two areas.</summary>
    public static int SharedSideChains(this Feature f, Feature o, double tolerance)
    {
        var (_, n, shared) = SharedSides(f, o, tolerance);
        var edges = Enumerable.Range(0, n).Where(shared).ToList();
        if (edges.Count >= n) return 1;
        return edges.Count(e => !shared((e - 1 + n) % n));
    }

    /// <summary>Areas sharing a chain of consecutive nodes: the shared chain is dropped, the rest joined.</summary>
    public static Feature? StitchingArea(this Feature f, Feature o, double tolerance)
    {
        var a = f.SignedArea() >= 0 ? f.Nodes : f.ReversedNodes();
        var b = o.SignedArea() >= 0 ? o.Nodes : o.ReversedNodes();
        int n = a.Count, m = b.Count;
        var (match, _, shared) = SharedSides(f, o, tolerance);
        var edges = Enumerable.Range(0, n).Where(shared).ToList();
        if (edges.Count == 0 || edges.Count >= n) return null;
        var starts = edges.Where(e => !shared((e - 1 + n) % n)).ToList();
        if (starts.Count != 1) return null;
        int s = starts[0], e2 = s;
        while (shared((e2 + 1) % n)) e2 = (e2 + 1) % n;
        int i = s, j = (e2 + 1) % n;
        if (match[i] is not int bi || match[j] is not int bj) return null;
        var result = new List<BezierNode>();
        int k = j;
        while (true)
        {
            result.Add(a[k].Clone());
            if (k == i) break;
            k = (k + 1) % n;
        }
        result[0].InHandle = b[bj].InHandle + (a[j].Point - b[bj].Point);
        result[^1].OutHandle = b[bi].OutHandle + (a[i].Point - b[bi].Point);
        k = (bi + 1) % m;
        while (k != bj)
        {
            result.Add(b[k].Clone());
            k = (k + 1) % m;
        }
        if (result.Count < 3) return null;
        var g = f.Clone();
        g.Nodes = result;
        return g;
    }

    // MARK: Fill line with area

    public static bool CanFillWithArea(this Feature f) => f.Kind == FeatureKind.Line && f.Nodes.Count >= 3;

    public static bool IsClosedLine(this Feature f, double tolerance)
        => f.Kind == FeatureKind.Line && f.Nodes.Count >= 3 && f.Nodes[0].Point.DistanceTo(f.Nodes[^1].Point) <= tolerance;

    /// <summary>New area object with this line's shape and area symbol s; the line itself is unchanged.</summary>
    public static Feature? FilledArea(this Feature f, MapSymbol s, double closeTolerance)
    {
        if (!f.CanFillWithArea() || s.SymbolKind != MapSymbol.Kind.Area) return null;
        var pts = f.Nodes.Select(n => n.Clone()).ToList();
        if (f.IsClosedLine(closeTolerance))
        {
            var last = pts[^1];
            pts.RemoveAt(pts.Count - 1);
            pts[0].InHandle = last.InHandle + (pts[0].Point - last.Point);
        }
        if (pts.Count < 3) return null;
        var g = new Feature { Kind = FeatureKind.Line, ColorHex = s.ColorHex, WidthMM = s.DrawingWidthMM, Nodes = pts };
        if (!g.SwitchSymbol(s)) return null;
        g.Name = "";
        return g;
    }
}

/// <summary>Union of two simple polygons by edge classification (robust to coincident points/edges).</summary>
public static class PolygonUnion
{
    public static List<Point2D> Ring(IReadOnlyList<Point2D> pts)
    {
        var r = new List<Point2D>();
        foreach (var p in pts) if (r.Count == 0 || r[^1].DistanceTo(p) > 1e-9) r.Add(p);
        if (r.Count > 1 && r[0].DistanceTo(r[^1]) <= 1e-9) r.RemoveAt(r.Count - 1);
        if (Area(r) < 0) r.Reverse();
        return r;
    }

    public static double Area(IReadOnlyList<Point2D> p) => ShapeCut.SignedArea(p);

    public static (List<Point2D> Outer, List<List<Point2D>> Holes)? Union(List<Point2D> a, List<Point2D> b, double eps)
    {
        if (a.Count < 3 || b.Count < 3) return null;
        var sa = Split(a, b, eps);
        var sb = Split(b, a, eps);
        var kept = new List<(Point2D, Point2D)>();
        foreach (var (p, q) in sa)
        {
            var mid = new Point2D((p.X + q.X) / 2, (p.Y + q.Y) / 2);
            switch (Locate(mid, b, eps))
            {
                case ShapeCut.Location.Outside: kept.Add((p, q)); break;
                case ShapeCut.Location.Boundary:
                    if (sb.Any(e => e.Item1.DistanceTo(p) <= eps && e.Item2.DistanceTo(q) <= eps)) kept.Add((p, q));
                    break;
            }
        }
        foreach (var (p, q) in sb)
        {
            var mid = new Point2D((p.X + q.X) / 2, (p.Y + q.Y) / 2);
            if (Locate(mid, a, eps) == ShapeCut.Location.Outside) kept.Add((p, q));
        }
        var rings = Trace(kept, eps);
        var outers = rings.Where(r => Area(r) > 0).ToList();
        if (outers.Count != 1) return null;
        var holes = rings.Where(r => Area(r) < -eps * eps).Select(r => Simplify(r, eps)).ToList();
        return (Simplify(outers[0], eps), holes);
    }

    private static ShapeCut.Location Locate(Point2D p, List<Point2D> poly, double eps)
    {
        bool inside = false;
        int j = poly.Count - 1;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[j];
            if (p.DistanceToSegment(a, b) <= eps) return ShapeCut.Location.Boundary;
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            j = i;
        }
        return inside ? ShapeCut.Location.Inside : ShapeCut.Location.Outside;
    }

    private static List<(Point2D, Point2D)> Split(List<Point2D> poly, List<Point2D> other, double eps)
    {
        var result = new List<(Point2D, Point2D)>();
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % poly.Count];
            var d = q - p;
            double len2 = d.X * d.X + d.Y * d.Y;
            if (!(len2 > 0)) continue;
            var cuts = new List<(double, Point2D)>();
            for (int k = 0; k < other.Count; k++)
            {
                var r = other[k]; var s = other[(k + 1) % other.Count];
                if (r.DistanceToSegment(p, q) <= eps) cuts.Add((Param(r, p, d, len2), r));
                if (Intersection(p, q, r, s) is { } x) cuts.Add((Param(x, p, d, len2), x));
            }
            var pts = new List<Point2D> { p };
            foreach (var (_, c) in cuts.Where(c => c.Item1 > 0 && c.Item1 < 1).OrderBy(c => c.Item1))
                if (c.DistanceTo(pts[^1]) > eps && c.DistanceTo(q) > eps) pts.Add(c);
            pts.Add(q);
            for (int k = 1; k < pts.Count; k++) result.Add((pts[k - 1], pts[k]));
        }
        return result;
    }

    private static double Param(Point2D x, Point2D p, Point2D d, double len2) => ((x.X - p.X) * d.X + (x.Y - p.Y) * d.Y) / len2;

    private static Point2D? Intersection(Point2D p, Point2D q, Point2D r, Point2D s)
    {
        var d1 = q - p; var d2 = s - r;
        double den = d1.X * d2.Y - d1.Y * d2.X;
        if (!(Math.Abs(den) > 1e-12 * Math.Max(1, d1.Length * d2.Length))) return null;
        var w = r - p;
        double t = (w.X * d2.Y - w.Y * d2.X) / den, u = (w.X * d1.Y - w.Y * d1.X) / den;
        if (!(t > 0 && t < 1 && u >= 0 && u <= 1)) return null;
        return new Point2D(p.X + t * d1.X, p.Y + t * d1.Y);
    }

    private static List<List<Point2D>> Trace(List<(Point2D, Point2D)> edges, double eps)
    {
        var used = new bool[edges.Count];
        var rings = new List<List<Point2D>>();
        for (int s = 0; s < edges.Count; s++)
        {
            if (used[s]) continue;
            var ring = new List<Point2D>();
            int cur = s, guardCount = 0;
            while (!used[cur] && guardCount <= edges.Count)
            {
                guardCount++;
                used[cur] = true;
                var (p, q) = edges[cur];
                ring.Add(p);
                if (q.DistanceTo(edges[s].Item1) <= eps) break;
                double inDir = Math.Atan2(q.Y - p.Y, q.X - p.X);
                int? next = null;
                double bestTurn = double.PositiveInfinity;
                for (int i = 0; i < edges.Count; i++)
                {
                    if (used[i] || edges[i].Item1.DistanceTo(q) > eps) continue;
                    double t = Turn(inDir, edges[i]);
                    if (t < bestTurn) { bestTurn = t; next = i; }
                }
                if (next is not int nx) { ring.Clear(); break; }
                cur = nx;
            }
            if (ring.Count >= 3) rings.Add(ring);
        }
        return rings;
    }

    private static double Turn(double inDir, (Point2D, Point2D) e)
    {
        double o = Math.Atan2(e.Item2.Y - e.Item1.Y, e.Item2.X - e.Item1.X);
        double a = o - inDir + Math.PI;
        while (a <= 0) a += 2 * Math.PI;
        while (a > 2 * Math.PI) a -= 2 * Math.PI;
        return a;
    }

    private static List<Point2D> Simplify(List<Point2D> r, double eps)
    {
        var pts = r.ToList();
        bool changed = true;
        while (changed && pts.Count > 3)
        {
            changed = false;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[(i - 1 + pts.Count) % pts.Count]; var b = pts[i]; var c = pts[(i + 1) % pts.Count];
                if (b.DistanceToSegment(a, c) <= eps * 0.5) { pts.RemoveAt(i); changed = true; break; }
            }
        }
        return pts;
    }
}

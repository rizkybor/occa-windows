using Occa.Core.Geometry;

namespace Occa.Core.Vector;

/// <summary>Why a shape/path cut changed nothing.</summary>
public enum ShapeCutFailure { NoOverlap, InsideHole, NotCrossing, Unresolved }

/// <summary>Pieces of a cut, or the reason it changed nothing.</summary>
public sealed record CutResult(List<Feature>? Pieces, ShapeCutFailure? Failure)
{
    public static CutResult Success(List<Feature> pieces) => new(pieces, null);
    public static CutResult Fail(ShapeCutFailure f) => new(null, f);
    public bool IsSuccess => Pieces is not null;
}

/// <summary>
/// Cutting engine (port of ShapeCut in CakraCore): two outlines of cubic segments are split where they meet, the
/// pieces between those points ("chunks") are classified inside/outside the other outline, and the kept chunks are
/// chained into loops by point id, turning as far left as possible at branch points.
/// </summary>
public static class ShapeCut
{
    public enum Location { Inside, Outside, Boundary }

    // MARK: Outline

    public sealed class Outline
    {
        public List<BezierNode> Nodes;
        public bool Closed;
        public List<CubicSegment> Segments;
        public List<Point2D> Polygon;
        public double Area;
        public Rect2D Box;
        public double FlattenTolerance;
        public double Eps;

        public Outline(List<BezierNode> nodes, bool closed, double eps)
        {
            Nodes = nodes;
            Closed = closed;
            Eps = eps;
            var tmp = new Feature { Kind = closed ? FeatureKind.Area : FeatureKind.Line, Nodes = nodes };
            Segments = tmp.Segments.Where(s => !IsDegenerate(s)).ToList();
            var pts = nodes.SelectMany(n => new[] { (Point2D?)n.Point, n.InHandle, n.OutHandle }).Where(p => p.HasValue).Select(p => p!.Value);
            Box = (Rect2D.FromPoints(pts) ?? new Rect2D(0, 0, 0, 0)).InsetBy(-eps);
            FlattenTolerance = Math.Max(eps, Math.Min(Math.Max(Box.Width, Box.Height) / 2000, eps * 20));
            Polygon = Flatten(Segments, FlattenTolerance);
            Area = closed ? SignedArea(Polygon) : 0;
        }

        private Outline(Outline o)
        {
            Nodes = o.Nodes; Closed = o.Closed; Segments = o.Segments; Polygon = o.Polygon; Area = o.Area; Box = o.Box; FlattenTolerance = o.FlattenTolerance; Eps = o.Eps;
        }

        public double LocateTolerance => Math.Max(Eps, FlattenTolerance) * 2;
        public Outline Counterclockwise => Area < 0 ? ReversedOutline : this;
        public Outline Clockwise => Area > 0 ? ReversedOutline : this;

        private Outline ReversedOutline
        {
            get
            {
                if (!Closed) return this;
                var o = new Outline(this)
                {
                    Nodes = Nodes.AsEnumerable().Reverse().Select(n => n.Reversed()).ToList(),
                    Segments = FeatureFollow.ReversedSegments(Segments),
                    Polygon = Polygon.AsEnumerable().Reverse().ToList(),
                    Area = -Area,
                };
                return o;
            }
        }
    }

    // MARK: Region

    /// <summary>A filled region: counterclockwise outer ring and clockwise holes, so the filled side is on the left of every ring.</summary>
    public sealed class Region
    {
        public Outline Outer;
        public List<Outline> Holes;

        public Region(Outline outer, List<Outline> holes) { Outer = outer; Holes = holes; }

        public static Region? Of(Feature f, double eps)
        {
            var o = new Outline(f.Nodes, true, eps);
            if (o.Area == 0) return null;
            var holes = f.HoleRings.Select(h => new Outline(h, true, eps)).Where(h => h.Area != 0).Select(h => h.Clockwise).ToList();
            return new Region(o.Counterclockwise, holes);
        }

        public List<Outline> Rings => new[] { Outer }.Concat(Holes).ToList();
        public Rect2D Box => Outer.Box;
        public double LocateTolerance => Outer.LocateTolerance;
        public double FlattenTolerance => Outer.FlattenTolerance;

        public Location Locate(Point2D p, double? eps = null)
        {
            double e = eps ?? LocateTolerance;
            bool inside = false;
            foreach (var r in Rings)
            {
                switch (ShapeCut.Locate(p, r.Polygon, e))
                {
                    case Location.Boundary: return Location.Boundary;
                    case Location.Inside: inside = !inside; break;
                }
            }
            return inside ? Location.Inside : Location.Outside;
        }
    }

    public enum BooleanOp { Subtract, Union }

    public sealed record Combined(List<List<CubicSegment>> Loops, List<Point2D> Points, bool Changed);

    /// <summary>Boolean of two regions on their exact segments: a − b or a ∪ b. null = unresolvable geometry.</summary>
    public static Combined? Combine(Region a, Region b, BooleanOp op, double eps)
    {
        double locEps = a.LocateTolerance;
        var points = new List<Point2D>();
        var aRings = a.Rings;
        var bRings = b.Rings;
        var hitsA = aRings.Select(_ => new List<Hit>()).ToList();
        var hitsB = bRings.Select(_ => new List<Hit>()).ToList();
        for (int i = 0; i < aRings.Count; i++)
            for (int j = 0; j < bRings.Count; j++)
            {
                if (!aRings[i].Box.Intersects(bRings[j].Box)) continue;
                var h = Hits(aRings[i].Segments, bRings[j].Segments, eps, points);
                hitsA[i].AddRange(h.A);
                hitsB[j].AddRange(h.B);
            }
        var kept = new List<Chunk>();
        var whole = new List<List<CubicSegment>>();
        bool changed = false;
        bool KeepA(Point2D m, Point2D tangent)
        {
            switch (b.Locate(m, locEps))
            {
                case Location.Outside: return true;
                case Location.Inside: return false;
                default:
                    var left = new Point2D(-tangent.Y, tangent.X) * (locEps * 4);
                    var probe = op == BooleanOp.Subtract ? m + left : m - left;
                    return b.Locate(probe, 0) != Location.Inside;
            }
        }
        for (int i = 0; i < aRings.Count; i++)
        {
            var ring = aRings[i];
            if (hitsA[i].Count == 0)
            {
                if (ring.Segments.Count == 0) continue;
                var s = ring.Segments[0];
                var m = s.PointAt(0.5);
                if (KeepA(m, Unit(s.PointAt(0.501) - s.PointAt(0.499)) ?? new Point2D(1, 0))) whole.Add(ring.Segments); else changed = true;
                continue;
            }
            foreach (var c in Chunks(ring.Segments, hitsA[i], true, points))
            {
                var (m, tangent) = c.Midpoint;
                if (KeepA(m, tangent)) kept.Add(c); else changed = true;
            }
        }
        bool AddB(Point2D m)
        {
            var loc = a.Locate(m, locEps);
            return op == BooleanOp.Subtract ? loc == Location.Inside : loc == Location.Outside;
        }
        for (int j = 0; j < bRings.Count; j++)
        {
            var ring = bRings[j];
            if (hitsB[j].Count == 0)
            {
                if (ring.Segments.Count == 0 || !AddB(ring.Segments[0].PointAt(0.5)))
                {
                    if (op == BooleanOp.Union) changed = true;
                    continue;
                }
                whole.Add(op == BooleanOp.Subtract ? FeatureFollow.ReversedSegments(ring.Segments) : ring.Segments);
                changed = true;
                continue;
            }
            foreach (var c in Chunks(ring.Segments, hitsB[j], true, points))
            {
                if (!AddB(c.Midpoint.Point)) continue;
                kept.Add(op == BooleanOp.Subtract ? c.ReversedChunk() : c);
                changed = true;
            }
            if (op == BooleanOp.Union) changed = true;
        }
        var loops = Trace(kept);
        if (loops is null) return null;
        loops.AddRange(whole);
        return new Combined(loops, points, changed);
    }

    /// <summary>A point strictly inside a closed loop, found just beside the middle of one of its segments.</summary>
    public static Point2D? InteriorProbe(List<CubicSegment> loop, List<Point2D> polygon, double eps)
    {
        foreach (var s in loop)
        {
            var m = s.PointAt(0.5);
            var t = Unit(s.PointAt(0.501) - s.PointAt(0.499)) ?? Unit(s.P3 - s.P0);
            if (t is not { } tt) continue;
            var normal = new Point2D(-tt.Y, tt.X);
            foreach (var d in new[] { eps * 4, eps * 20, eps * 100 })
                foreach (var q in new[] { m + normal * d, m - normal * d })
                    if (Locate(q, polygon, eps) == Location.Inside) return q;
        }
        return null;
    }

    // MARK: Intersections

    public struct Hit
    {
        public int Segment;
        public double T;
        public int Id;
        public Hit(int segment, double t, int id) { Segment = segment; T = t; Id = id; }
    }

    public static (List<Hit> A, List<Hit> B, List<Point2D> Points) Hits(List<CubicSegment> a, List<CubicSegment> b, double eps)
    {
        var points = new List<Point2D>();
        var h = Hits(a, b, eps, points);
        return (h.A, h.B, points);
    }

    public static (List<Hit> A, List<Hit> B) Hits(List<CubicSegment> a, List<CubicSegment> b, double eps, List<Point2D> points)
    {
        var ha = new List<Hit>();
        var hb = new List<Hit>();
        int Id(Point2D p)
        {
            int i = points.FindIndex(q => q.DistanceTo(p) <= eps);
            if (i >= 0) return i;
            points.Add(p);
            return points.Count - 1;
        }
        var boxesB = b.Select(ControlBox).ToList();
        for (int i = 0; i < a.Count; i++)
        {
            var boxA = ControlBox(a[i]);
            for (int k = 0; k < b.Count; k++)
            {
                if (!Overlaps(boxA, boxesB[k], eps)) continue;
                foreach (var (ta, tb, p) in Intersections(a[i], b[k], eps))
                {
                    int n = Id(p);
                    ha.Add(new Hit(i, ta, n));
                    hb.Add(new Hit(k, tb, n));
                }
            }
        }
        return (ha, hb);
    }

    public static List<(double, double, Point2D)> Intersections(CubicSegment a, CubicSegment b, double eps)
    {
        var result = new List<(double, double, Point2D)>();
        void Recurse(CubicSegment x, double x0, double x1, CubicSegment y, double y0, double y1, int depth)
        {
            if (!Overlaps(ControlBox(x), ControlBox(y), eps) || result.Count >= 512) return;
            bool fx = IsFlat(x, eps), fy = IsFlat(y, eps);
            if ((fx && fy) || depth > 40)
            {
                foreach (var (s, u, p) in LineHits(x.P0, x.P3, y.P0, y.P3, eps))
                {
                    double ta = x0 + s * (x1 - x0), tb = y0 + u * (y1 - y0);
                    if (a.IsCurve) ta = Refine(a, p, ta, x1 - x0);
                    if (b.IsCurve) tb = Refine(b, p, tb, y1 - y0);
                    result.Add((ta, tb, p));
                }
                return;
            }
            if (!fx && (fy || Extent(x) >= Extent(y)))
            {
                var (l, r) = x.Split(0.5);
                double m = (x0 + x1) / 2;
                Recurse(l, x0, m, y, y0, y1, depth + 1);
                Recurse(r, m, x1, y, y0, y1, depth + 1);
            }
            else
            {
                var (l, r) = y.Split(0.5);
                double m = (y0 + y1) / 2;
                Recurse(x, x0, x1, l, y0, m, depth + 1);
                Recurse(x, x0, x1, r, m, y1, depth + 1);
            }
        }
        Recurse(a, 0, 1, b, 0, 1, 0);
        return result;
    }

    public static List<(double, double, Point2D)> LineHits(Point2D p, Point2D q, Point2D r, Point2D s, double eps)
    {
        var result = new List<(double, double, Point2D)>();
        var d1 = q - p;
        var d2 = s - r;
        double l1 = d1.Length, l2 = d2.Length;
        double Param(Point2D x, Point2D a, Point2D d, double l) => l > 0 ? Math.Clamp(((x.X - a.X) * d.X + (x.Y - a.Y) * d.Y) / (l * l), 0, 1) : 0;
        double den = d1.X * d2.Y - d1.Y * d2.X;
        if (l1 > 0 && l2 > 0 && Math.Abs(den) > 1e-12 * l1 * l2)
        {
            var w = r - p;
            double t = (w.X * d2.Y - w.Y * d2.X) / den, u = (w.X * d1.Y - w.Y * d1.X) / den;
            double st = eps / l1, su = eps / l2;
            if (t >= -st && t <= 1 + st && u >= -su && u <= 1 + su)
            {
                double tc = Math.Clamp(t, 0, 1);
                result.Add((tc, Math.Clamp(u, 0, 1), p + d1 * tc));
            }
        }
        foreach (var (e, u) in new[] { (r, 0.0), (s, 1.0) }) if (e.DistanceToSegment(p, q) <= eps) result.Add((Param(e, p, d1, l1), u, e));
        foreach (var (e, t) in new[] { (p, 0.0), (q, 1.0) }) if (e.DistanceToSegment(r, s) <= eps) result.Add((t, Param(e, r, d2, l2), e));
        return result;
    }

    public static double Refine(CubicSegment s, Point2D p, double t, double span)
    {
        double best = t, bestD = s.PointAt(t).DistanceTo(p);
        double step = Math.Max(span, 1e-6);
        for (int i = 0; i < 40; i++)
        {
            step /= 2;
            foreach (var c in new[] { best - step, best + step })
            {
                if (c < 0 || c > 1) continue;
                double d = s.PointAt(c).DistanceTo(p);
                if (d < bestD) { best = c; bestD = d; }
            }
        }
        return best;
    }

    public static Rect2D ControlBox(CubicSegment s) => Rect2D.FromPoints(s.IsCurve ? new[] { s.P0, s.C1, s.C2, s.P3 } : new[] { s.P0, s.P3 })!.Value;
    public static bool Overlaps(Rect2D a, Rect2D b, double eps) => a.MinX <= b.MaxX + eps && a.MaxX >= b.MinX - eps && a.MinY <= b.MaxY + eps && a.MaxY >= b.MinY - eps;
    public static bool IsFlat(CubicSegment s, double eps)
    {
        if (!s.IsCurve) return true;
        double tol = eps * 0.25;
        return s.C1.DistanceToSegment(s.P0, s.P3) <= tol && s.C2.DistanceToSegment(s.P0, s.P3) <= tol;
    }
    public static double Extent(CubicSegment s) { var b = ControlBox(s); return Math.Max(b.Width, b.Height); }
    public static bool IsDegenerate(CubicSegment s) => s.P0.DistanceTo(s.P3) < 1e-12 && (!s.IsCurve || (s.C1.DistanceTo(s.P0) < 1e-12 && s.C2.DistanceTo(s.P0) < 1e-12));

    // MARK: Chunks

    public sealed class Chunk
    {
        public List<CubicSegment> Segments;
        public int? Start;
        public int? End;
        public int? Twin;

        public Chunk(List<CubicSegment> segments, int? start, int? end) { Segments = segments; Start = start; End = end; }

        public Chunk ReversedChunk() => new(FeatureFollow.ReversedSegments(Segments), End, Start);

        public (Point2D Point, Point2D Tangent) Midpoint
        {
            get
            {
                var lengths = Segments.Select(s => s.ApproximateLength()).ToList();
                double target = lengths.Sum() / 2;
                for (int k = 0; k < Segments.Count; k++)
                {
                    double l = lengths[k];
                    var s = Segments[k];
                    if (target <= l || k == Segments.Count - 1)
                    {
                        double t = l > 0 ? Math.Clamp(target / l, 0, 1) : 0.5;
                        var d = s.PointAt(Math.Min(1, t + 1e-3)) - s.PointAt(Math.Max(0, t - 1e-3));
                        return (s.PointAt(t), Unit(d) ?? Unit(s.P3 - s.P0) ?? new Point2D(1, 0));
                    }
                    target -= l;
                }
                return (Segments.Count > 0 ? Segments[0].P0 : Point2D.Zero, new Point2D(1, 0));
            }
        }

        public Point2D StartDirection
        {
            get
            {
                if (Segments.Count == 0) return new Point2D(1, 0);
                var s = Segments[0];
                var options = s.IsCurve ? new[] { s.C1 - s.P0, s.C2 - s.P0, s.P3 - s.P0 } : new[] { s.P3 - s.P0 };
                foreach (var o in options) if (Unit(o) is { } u) return u;
                return new Point2D(1, 0);
            }
        }

        public Point2D EndDirection
        {
            get
            {
                if (Segments.Count == 0) return new Point2D(1, 0);
                var s = Segments[^1];
                var options = s.IsCurve ? new[] { s.P3 - s.C2, s.P3 - s.C1, s.P3 - s.P0 } : new[] { s.P3 - s.P0 };
                foreach (var o in options) if (Unit(o) is { } u) return u;
                return new Point2D(1, 0);
            }
        }
    }

    public static List<Hit> Ordered(List<Hit> hits, int count, bool closed)
    {
        var normalized = hits.Select(h =>
        {
            if (h.T >= 1 - 1e-9 && (closed || h.Segment < count - 1)) { h.Segment = (h.Segment + 1) % count; h.T = 0; }
            else if (h.T <= 1e-9) h.T = 0;
            return h;
        }).OrderBy(h => h.Segment).ThenBy(h => h.T).ToList();
        var seen = new HashSet<int>();
        return normalized.Where(h => seen.Add(h.Id)).ToList();
    }

    public static List<Chunk> Chunks(List<CubicSegment> segs, List<Hit> hits, bool closed, List<Point2D> points, bool includeEnds = false)
    {
        int n = segs.Count;
        var h = Ordered(hits, n, closed);
        var result = new List<Chunk>();
        if (n == 0 || h.Count == 0) return result;
        void Add((int, double) from, (int, double) to, int? a, int? b)
        {
            var s = Piece(segs, from, to, closed).Where(x => !IsDegenerate(x)).ToList();
            if (s.Count == 0) return;
            if (a is int ai)
            {
                var first = s[0];
                s[0] = new CubicSegment(points[ai], first.IsCurve ? first.C1 : points[ai], first.C2, first.P3, first.IsCurve);
            }
            if (b is int bi)
            {
                var last = s[^1];
                s[^1] = new CubicSegment(last.P0, last.C1, last.IsCurve ? last.C2 : points[bi], points[bi], last.IsCurve);
            }
            result.Add(new Chunk(s, a, b));
        }
        if (closed)
        {
            for (int k = 0; k < h.Count; k++)
            {
                var x = h[k];
                var y = h[(k + 1) % h.Count];
                Add((x.Segment, x.T), (y.Segment, y.T), x.Id, y.Id);
            }
        }
        else
        {
            if (includeEnds && (h[0].Segment > 0 || h[0].T > 0)) Add((0, 0), (h[0].Segment, h[0].T), null, h[0].Id);
            for (int k = 0; k < h.Count - 1; k++) Add((h[k].Segment, h[k].T), (h[k + 1].Segment, h[k + 1].T), h[k].Id, h[k + 1].Id);
            if (includeEnds) { var l = h[^1]; Add((l.Segment, l.T), (n - 1, 1), l.Id, null); }
        }
        return result;
    }

    public static List<CubicSegment> Piece(List<CubicSegment> segs, (int, double) from, (int, double) to, bool closed)
    {
        int n = segs.Count;
        var (i0, t0) = from;
        var (i1, t1) = to;
        if (i0 == i1 && t0 < t1) return new List<CubicSegment> { segs[i0].Sub(t0, t1) };
        var result = new List<CubicSegment>();
        if (t0 < 1) result.Add(segs[i0].Sub(t0, 1));
        int k = i0 + 1, guardCount = 0;
        while (guardCount <= n)
        {
            guardCount++;
            if (closed) k %= n;
            if (k == i1 || k >= n) break;
            result.Add(segs[k]);
            k++;
        }
        if (t1 > 0 && i1 < n) result.Add(segs[i1].Sub(0, t1));
        return result;
    }

    // MARK: Tracing

    public static List<List<CubicSegment>>? Trace(List<Chunk> chunks)
    {
        var used = new bool[chunks.Count];
        var loops = new List<List<CubicSegment>>();
        var byStart = new Dictionary<int, List<int>>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (c.Start is not int s || c.End is null) return null;
            if (!byStart.TryGetValue(s, out var list)) byStart[s] = list = new List<int>();
            list.Add(i);
        }
        for (int s = 0; s < chunks.Count; s++)
        {
            if (used[s]) continue;
            used[s] = true;
            var loop = new List<CubicSegment>(chunks[s].Segments);
            int cur = s, steps = 0;
            while (true)
            {
                steps++;
                if (steps > chunks.Count + 1 || chunks[cur].End is not int end) return null;
                var inDir = chunks[cur].EndDirection;
                var options = (byStart.TryGetValue(end, out var cands) ? cands : new List<int>()).Where(i => (i == s || !used[i]) && i != chunks[cur].Twin).ToList();
                if (options.Count == 0) return null;
                int next = options[0];
                double bestTurn = Turn(inDir, chunks[next].StartDirection);
                foreach (var o in options.Skip(1))
                {
                    double t = Turn(inDir, chunks[o].StartDirection);
                    if (t > bestTurn) { bestTurn = t; next = o; }
                }
                if (next == s) break;
                used[next] = true;
                loop.AddRange(chunks[next].Segments);
                cur = next;
            }
            loops.Add(loop);
        }
        return loops;
    }

    public static double Turn(Point2D a, Point2D b) => Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);

    // MARK: Helpers

    public static Location Locate(Point2D p, IReadOnlyList<Point2D> poly, double eps)
    {
        if (poly.Count < 3) return Location.Outside;
        bool inside = false;
        int j = poly.Count - 1;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[j];
            if (eps > 0 && p.DistanceToSegment(a, b) <= eps) return Location.Boundary;
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            j = i;
        }
        return inside ? Location.Inside : Location.Outside;
    }

    public static List<Point2D> Flatten(IReadOnlyList<CubicSegment> segs, double tolerance)
    {
        var pts = new List<Point2D>();
        if (segs.Count == 0) return pts;
        pts.Add(segs[0].P0);
        foreach (var s in segs)
        {
            if (!s.IsCurve) { pts.Add(s.P3); continue; }
            double hull = s.P0.DistanceTo(s.C1) + s.C1.DistanceTo(s.C2) + s.C2.DistanceTo(s.P3);
            int steps = Math.Max(4, Math.Min(256, (int)Math.Ceiling(Math.Sqrt(hull / Math.Max(tolerance, 1e-9))) * 2));
            for (int k = 1; k <= steps; k++) pts.Add(s.PointAt((double)k / steps));
        }
        if (pts.Count > 1 && pts[0].DistanceTo(pts[^1]) < 1e-12) pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    public static double SignedArea(IReadOnlyList<Point2D> p)
    {
        if (p.Count < 3) return 0;
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    public static Point2D? Unit(Point2D v)
    {
        double l = v.Length;
        return l > 1e-12 ? new Point2D(v.X / l, v.Y / l) : null;
    }

    /// <summary>Corner nodes the cut added in the middle of a straight edge (the cut only touched there): removed again.</summary>
    public static List<BezierNode> RemovingStraightCutNodes(List<BezierNode> nodes, List<Point2D> cutPoints, List<BezierNode> original, double eps)
    {
        var n = nodes.ToList();
        int i = 0;
        while (i < n.Count && n.Count > 3)
        {
            var a = n[(i - 1 + n.Count) % n.Count];
            var b = n[i];
            var c = n[(i + 1) % n.Count];
            bool straight = a.OutHandle is null && b.InHandle is null && b.OutHandle is null && c.InHandle is null;
            if (straight && b.Point.DistanceToSegment(a.Point, c.Point) <= eps * 0.5
                && cutPoints.Any(p => p.DistanceTo(b.Point) <= eps) && !original.Any(o => o.Point.DistanceTo(b.Point) <= eps))
                n.RemoveAt(i);
            else i++;
        }
        return n;
    }
}

public static class FeatureCut
{
    /// <summary>Area minus a closed shape. Success with several features = the area fell apart; success with [] = the shape covered the whole area.</summary>
    public static CutResult SubtractingShape(this Feature f, List<BezierNode> shape, double eps = 1e-4)
    {
        if (f.Kind != FeatureKind.Area || f.Nodes.Count < 3 || shape.Count < 3) return CutResult.Fail(ShapeCutFailure.Unresolved);
        if (ShapeCut.Region.Of(f, eps) is not { } a) return CutResult.Fail(ShapeCutFailure.Unresolved);
        var b = new ShapeCut.Outline(shape, true, eps);
        if (b.Area == 0) return CutResult.Fail(ShapeCutFailure.Unresolved);
        var bRegion = new ShapeCut.Region(b.Counterclockwise, new List<ShapeCut.Outline>());
        if (!a.Box.Intersects(bRegion.Box)) return CutResult.Fail(ShapeCutFailure.NoOverlap);
        if (ShapeCut.Combine(a, bRegion, ShapeCut.BooleanOp.Subtract, eps) is not { } r) return CutResult.Fail(ShapeCutFailure.Unresolved);
        if (!r.Changed)
        {
            if (b.Segments.Count > 0 && a.Holes.Any(h => ShapeCut.Locate(b.Segments[0].P0, h.Polygon, a.LocateTolerance) == ShapeCut.Location.Inside))
                return CutResult.Fail(ShapeCutFailure.InsideHole);
            return CutResult.Fail(ShapeCutFailure.NoOverlap);
        }
        if (r.Loops.Count == 0) return CutResult.Success(new List<Feature>());
        return Pieces(f, r.Loops, a, r.Points, eps);
    }

    /// <summary>Split an area along an open path: every stretch inside the filled part becomes a new edge on both sides.</summary>
    public static CutResult SplittingArea(this Feature f, List<BezierNode> path, double eps = 1e-4)
    {
        if (f.Kind != FeatureKind.Area || f.Nodes.Count < 3 || path.Count < 2) return CutResult.Fail(ShapeCutFailure.Unresolved);
        if (ShapeCut.Region.Of(f, eps) is not { } a) return CutResult.Fail(ShapeCutFailure.Unresolved);
        var p = new ShapeCut.Outline(path, false, eps);
        if (!a.Box.Intersects(p.Box)) return CutResult.Fail(ShapeCutFailure.NoOverlap);
        var points = new List<Point2D>();
        var pathHits = new List<ShapeCut.Hit>();
        var kept = new List<ShapeCut.Chunk>();
        var whole = new List<List<CubicSegment>>();
        foreach (var ring in a.Rings)
        {
            var h = ShapeCut.Hits(ring.Segments, p.Segments, eps, points);
            pathHits.AddRange(h.B);
            if (h.A.Count == 0) whole.Add(ring.Segments);
            else kept.AddRange(ShapeCut.Chunks(ring.Segments, h.A, true, points));
        }
        if (pathHits.Count == 0)
            return p.Segments.Any(s => a.Locate(s.P0) == ShapeCut.Location.Inside) ? CutResult.Fail(ShapeCutFailure.NotCrossing) : CutResult.Fail(ShapeCutFailure.NoOverlap);
        foreach (var c in ShapeCut.Chunks(p.Segments, pathHits, false, points))
        {
            if (a.Locate(c.Midpoint.Point) != ShapeCut.Location.Inside) continue;
            var forward = c;
            var backward = c.ReversedChunk();
            forward.Twin = kept.Count + 1;
            backward.Twin = kept.Count;
            kept.Add(forward);
            kept.Add(backward);
        }
        if (!kept.Any(c => c.Twin is not null)) return CutResult.Fail(ShapeCutFailure.NotCrossing);
        var loops = ShapeCut.Trace(kept);
        if (loops is null) return CutResult.Fail(ShapeCutFailure.Unresolved);
        loops.AddRange(whole);
        var result = Pieces(f, loops, a, points, eps);
        if (result.Pieces is { Count: < 2 }) return CutResult.Fail(ShapeCutFailure.NotCrossing);
        return result;
    }

    /// <summary>Cut out the parts of a line inside a polygon. [] = the box covers the whole line, null = no part inside.</summary>
    public static List<Feature>? CuttingOut(this Feature f, IReadOnlyList<Point2D> polygon, double eps = 1e-4)
    {
        if (f.Kind != FeatureKind.Line || f.Nodes.Count < 2 || polygon.Count < 3) return null;
        var line = new ShapeCut.Outline(f.Nodes, false, eps);
        var box = new ShapeCut.Outline(polygon.Select(p => new BezierNode(p)).ToList(), true, eps);
        if (!line.Box.Intersects(box.Box)) return null;
        bool ring = f.IsRingLine;
        var hits = ShapeCut.Hits(line.Segments, box.Segments, eps);
        double locEps = box.LocateTolerance;
        if (hits.A.Count == 0)
        {
            if (line.Segments.Count == 0) return null;
            return ShapeCut.Locate(line.Segments[0].PointAt(0.5), box.Polygon, locEps) == ShapeCut.Location.Inside ? new List<Feature>() : null;
        }
        var chunks = ShapeCut.Chunks(line.Segments, hits.A, ring, hits.Points, includeEnds: !ring);
        var keep = chunks.Select(c => ShapeCut.Locate(c.Midpoint.Point, box.Polygon, locEps) != ShapeCut.Location.Inside).ToList();
        if (!keep.Contains(false)) return null;
        var runs = new List<List<CubicSegment>>();
        List<CubicSegment>? current = null;
        for (int i = 0; i < chunks.Count; i++)
        {
            if (keep[i]) { current ??= new List<CubicSegment>(); current.AddRange(chunks[i].Segments); }
            else if (current is not null) { runs.Add(current); current = null; }
        }
        if (current is not null)
        {
            if (ring && keep[0] && runs.Count > 0) runs[0] = current.Concat(runs[0]).ToList();
            else runs.Add(current);
        }
        double minLength = eps * 10;
        var result = new List<Feature>();
        foreach (var r in runs)
        {
            if (r.Sum(s => s.ApproximateLength()) <= minLength) continue;
            var n = FeatureFollow.NodesFrom(r);
            if (n.Count < 2) continue;
            var piece = f.Clone();
            if (result.Count > 0) { piece.Id = Guid.NewGuid(); piece.Name = ""; piece.CreatedAt = DateTime.UtcNow; }
            piece.Nodes = n;
            result.Add(piece);
        }
        return result;
    }

    /// <summary>Features from traced loops: positive loops are pieces (largest first, keeping the id), negative loops are holes given to the smallest piece containing them.</summary>
    internal static CutResult Pieces(Feature f, List<List<CubicSegment>> loops, ShapeCut.Region region, List<Point2D> cutPoints, double eps)
    {
        var original = region.Rings.SelectMany(r => r.Nodes).ToList();
        var outers = new List<(List<BezierNode> Nodes, double Area, List<Point2D> Polygon)>();
        var holes = new List<(List<BezierNode> Nodes, Point2D Probe)>();
        foreach (var loop in loops)
        {
            var polygon = ShapeCut.Flatten(loop, region.FlattenTolerance);
            double area = ShapeCut.SignedArea(polygon);
            if (Math.Abs(area) <= eps * eps) continue;
            var n = FeatureFollow.NodesFrom(loop);
            if (n.Count < 4) continue;
            var last = n[^1];
            n.RemoveAt(n.Count - 1);
            n[0].InHandle = last.InHandle is { } ih ? ih + (n[0].Point - last.Point) : null;
            n = ShapeCut.RemovingStraightCutNodes(n, cutPoints, original, eps);
            if (n.Count < 3) continue;
            if (area > 0) outers.Add((n, area, polygon));
            else if (ShapeCut.InteriorProbe(loop, polygon, region.LocateTolerance) is { } probe) holes.Add((n, probe));
        }
        if (outers.Count == 0) return CutResult.Fail(ShapeCutFailure.Unresolved);
        outers = outers.OrderByDescending(o => o.Area).ToList();
        var holesOf = outers.Select(_ => new List<List<BezierNode>>()).ToList();
        foreach (var h in holes)
        {
            int? owner = null;
            double ownerArea = double.PositiveInfinity;
            for (int k = 0; k < outers.Count; k++)
                if (ShapeCut.Locate(h.Probe, outers[k].Polygon, 0) == ShapeCut.Location.Inside && outers[k].Area < ownerArea) { owner = k; ownerArea = outers[k].Area; }
            if (owner is int o) holesOf[o].Add(h.Nodes);
        }
        var result = new List<Feature>();
        bool clockwise = Feature.SignedArea(f.Nodes) < 0;
        List<BezierNode> Orient(List<BezierNode> ring) => clockwise ? ring.AsEnumerable().Reverse().Select(x => x.Reversed()).ToList() : ring;
        for (int k = 0; k < outers.Count; k++)
        {
            var piece = f.Clone();
            if (k > 0) { piece.Id = Guid.NewGuid(); piece.Name = ""; piece.CreatedAt = DateTime.UtcNow; }
            piece.Nodes = Orient(outers[k].Nodes);
            var hs = holesOf[k].Select(Orient).ToList();
            piece.Holes = hs.Count == 0 ? null : hs;
            result.Add(piece);
        }
        return CutResult.Success(result);
    }
}

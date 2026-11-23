using Occa.Core.Geometry;

namespace Occa.Core.Vector;

/// <summary>Node type for Convert to Sharp / Smooth / Smart.</summary>
public enum NodeStyle { Sharp, Smooth, Smart }

/// <summary>
/// Editing operations on objects (OCAD-like tools), ported from CakraCore's Feature.swift, FeatureEdit.swift and
/// FeatureHoles.swift. Methods named with a verb mutate the object; methods returning a <see cref="Feature"/> leave
/// the receiver untouched and return a copy (Swift value semantics).
/// </summary>
public static class FeatureEdit
{
    /// <summary>Copy with its own node lists (holes included), so edits do not leak into the original.</summary>
    public static Feature Clone(this Feature f)
    {
        var c = f.ShallowCopy();
        c.Nodes = f.Nodes.Select(n => n.Clone()).ToList();
        c.Holes = f.Holes?.Select(h => h.Select(n => n.Clone()).ToList()).ToList();
        c.PointElements = f.PointElements?.Select(e => e.Clone()).ToList();
        c.DashMM = f.DashMM?.ToList();
        c.InnerDashMM = f.InnerDashMM?.ToList();
        return c;
    }

    private static Point2D? Unit(Point2D v)
    {
        double l = v.Length;
        return l > 1e-12 ? new Point2D(v.X / l, v.Y / l) : null;
    }

    // MARK: Node Tool indices across rings

    /// <summary>All nodes, outer ring first then each hole (Node Tool indices).</summary>
    public static List<BezierNode> AllNodes(this Feature f) => f.HasHoles ? f.Nodes.Concat(f.HoleRings.SelectMany(h => h)).ToList() : f.Nodes.ToList();

    /// <summary>Replace all nodes keeping the ring sizes.</summary>
    public static void SetAllNodes(this Feature f, IReadOnlyList<BezierNode> all)
    {
        if (!f.HasHoles) { f.Nodes = all.ToList(); return; }
        int offset = f.Nodes.Count;
        var holes = new List<List<BezierNode>>();
        foreach (var h in f.HoleRings)
        {
            holes.Add(all.Skip(offset).Take(h.Count).ToList());
            offset += h.Count;
        }
        f.Nodes = all.Take(f.Nodes.Count).ToList();
        f.Holes = holes;
    }

    /// <summary>Ring and index within that ring of Node Tool index i.</summary>
    public static (int Ring, int Index)? RingLocation(this Feature f, int i)
    {
        if (i < 0) return null;
        int start = 0, k = 0;
        foreach (var r in f.Rings)
        {
            if (i < start + r.Count) return (k, i - start);
            start += r.Count;
            k++;
        }
        return null;
    }

    public static int NodeIndex(this Feature f, int ring, int index) => f.Rings.Take(ring).Sum(r => r.Count) + index;

    public static void SetRing(this Feature f, int k, List<BezierNode> ring)
    {
        if (k == 0) { f.Nodes = ring; return; }
        if (f.Holes is null || k - 1 >= f.Holes.Count) return;
        f.Holes[k - 1] = ring;
    }

    /// <summary>Edit the ring holding Node Tool index i; body gets that ring as a plain object and the local index.</summary>
    public static bool EditRing(this Feature f, int i, Action<Feature, int> body)
    {
        if (f.RingLocation(i) is not { } loc) return false;
        var ring = f.RingFeature(loc.Ring);
        ring.Nodes = ring.Nodes.Select(n => n.Clone()).ToList();
        body(ring, loc.Index);
        f.SetRing(loc.Ring, ring.Nodes);
        return true;
    }

    public static Feature MappingAllNodes(this Feature f, Func<BezierNode, BezierNode> transform)
    {
        var c = f.ShallowCopy();
        c.Nodes = f.Nodes.Select(transform).ToList();
        c.Holes = f.Holes?.Select(h => h.Select(transform).ToList()).ToList();
        return c;
    }

    public static Feature MovedBy(this Feature f, Point2D d) => f.MappingAllNodes(n => n.MovedBy(d));

    public static void RemoveHole(this Feature f, int k)
    {
        if (f.Holes is null || k < 0 || k >= f.Holes.Count) return;
        f.Holes.RemoveAt(k);
        if (f.Holes.Count == 0) f.Holes = null;
    }

    public static int? HoleIndex(this Feature f, Point2D p, double tolerance)
    {
        for (int k = 0; k < f.HoleRings.Count; k++)
            if (f.RingFeature(k + 1).ContainsPoint(p, tolerance)) return k;
        return null;
    }

    /// <summary>Filled ground area (m²): the outer ring minus its holes.</summary>
    public static double FilledArea(this Feature f, double tolerance)
    {
        var rings = f.FlattenedRings(tolerance).Select(r => Math.Abs(ShapeCut.SignedArea(r))).ToList();
        if (rings.Count == 0) return 0;
        return Math.Max(0, rings[0] - rings.Skip(1).Sum());
    }

    /// <summary>Distance to the nearest outline (areas: the outer ring or a hole edge).</summary>
    public static double DistanceTo(this Feature f, Point2D p, double tolerance)
    {
        if (f.HasHoles) return f.RingFeatures.Min(r => r.DistanceTo(p, tolerance));
        var pts = f.Flattened(tolerance);
        if (pts.Count == 0) return double.PositiveInfinity;
        if (pts.Count == 1) return p.DistanceTo(pts[0]);
        double best = double.PositiveInfinity;
        for (int i = 1; i < pts.Count; i++) best = Math.Min(best, p.DistanceToSegment(pts[i - 1], pts[i]));
        return best;
    }

    /// <summary>Nearest point on any ring: ring number (0 = outer), position, point, distance.</summary>
    public static (int Ring, PathPosition Position, Point2D Point, double Distance)? NearestRingPosition(this Feature f, Point2D p)
    {
        (int, PathPosition, Point2D, double)? best = null;
        var rings = f.RingFeatures;
        for (int k = 0; k < rings.Count; k++)
        {
            var r = rings[k].NearestPathPosition(p);
            if (r is null || r.Value.Distance >= (best?.Item4 ?? double.PositiveInfinity)) continue;
            best = (k, r.Value.Position, r.Value.Point, r.Value.Distance);
        }
        return best;
    }

    /// <summary>Signed area of the outer ring's node points (positive = counterclockwise).</summary>
    public static double SignedArea(this Feature f) => Feature.SignedArea(f.Nodes);

    public static List<BezierNode> ReversedNodes(this Feature f) => f.Nodes.AsEnumerable().Reverse().Select(n => n.Reversed()).ToList();

    // MARK: Basic shapes

    public const double EllipseKappa = 0.5522847498;

    /// <summary>Rectangle from 4 corners clockwise from upper-left.</summary>
    public static Feature Rectangle(IReadOnlyList<Point2D> corners, string colorHex, double widthMM)
        => new() { Kind = FeatureKind.Area, ColorHex = colorHex, WidthMM = widthMM, Nodes = corners.Take(4).Select(p => new BezierNode(p)).ToList() };

    /// <summary>Ellipse inscribed in the parallelogram of 4 corners (UL, UR, LR, LL): 4 smooth nodes with kappa handles.</summary>
    public static Feature Ellipse(IReadOnlyList<Point2D> c, string colorHex, double widthMM)
    {
        var center = new Point2D((c[0].X + c[2].X) / 2, (c[0].Y + c[2].Y) / 2);
        Point2D Half(Point2D a, Point2D b) => new((b.X - a.X) / 2, (b.Y - a.Y) / 2);
        var u = Half(c[0], c[1]);
        var v = Half(c[0], c[3]);
        double k = EllipseKappa;
        var top = center - v;
        var right = center + u;
        var bottom = center + v;
        var left = center - u;
        return new Feature
        {
            Kind = FeatureKind.Area, ColorHex = colorHex, WidthMM = widthMM,
            Nodes = new List<BezierNode>
            {
                new(top, top - u * k, top + u * k),
                new(right, right - v * k, right + v * k),
                new(bottom, bottom + u * k, bottom - u * k),
                new(left, left + v * k, left - v * k),
            },
        };
    }

    // MARK: Nodes and handles

    /// <summary>Close the line if its end node overlaps its start node (within tolerance). false if unchanged.</summary>
    public static bool CloseLineIfEndsMeet(this Feature f, double tolerance)
    {
        if (f.Kind != FeatureKind.Line || f.Nodes.Count < 3 || f.IsRingLine) return false;
        var a = f.Nodes[0].Point;
        var b = f.Nodes[^1].Point;
        if (a.DistanceTo(b) > tolerance) return false;
        var d = a - b;
        var last = f.Nodes[^1];
        last.Point = a;
        last.InHandle = last.InHandle + d;
        last.OutHandle = null;
        return true;
    }

    private static Point2D? Tangent(Feature f, int i)
    {
        int n = f.Nodes.Count;
        if (n < 2) return null;
        var p = f.Nodes[i].Point;
        var prev = (i > 0 || f.IsClosed) ? f.Nodes[(i - 1 + n) % n].Point : p;
        var next = (i < n - 1 || f.IsClosed) ? f.Nodes[(i + 1) % n].Point : p;
        return Unit(next - prev);
    }

    private static (double Prev, double Next) NeighborReach(Feature f, int i)
    {
        int n = f.Nodes.Count;
        var p = f.Nodes[i].Point;
        double prev = (i > 0 || f.IsClosed) ? p.DistanceTo(f.Nodes[(i - 1 + n) % n].Point) / 3 : 0;
        double next = (i < n - 1 || f.IsClosed) ? p.DistanceTo(f.Nodes[(i + 1) % n].Point) / 3 : 0;
        return (prev, next);
    }

    /// <summary>Turn node i into a corner (no handles) or smooth (handles along the neighbours, ⅓ of the nearest distance).</summary>
    public static void ToggleSmooth(this Feature f, int i)
    {
        if (i >= f.Nodes.Count && f.HasHoles) { f.EditRing(i, (r, j) => r.ToggleSmooth(j)); return; }
        if (i < 0 || i >= f.Nodes.Count) return;
        var node = f.Nodes[i];
        if (node.IsSmooth) { node.InHandle = null; node.OutHandle = null; node.Smart = null; return; }
        int n = f.Nodes.Count;
        var p = node.Point;
        var prev = (i > 0 || f.IsClosed) ? f.Nodes[(i - 1 + n) % n].Point : p;
        var next = (i < n - 1 || f.IsClosed) ? f.Nodes[(i + 1) % n].Point : p;
        if (Unit(next - prev) is not { } dir) return;
        double dp = p.DistanceTo(prev), dn = p.DistanceTo(next);
        double reach = Math.Min(dp == 0 ? double.PositiveInfinity : dp, dn == 0 ? double.PositiveInfinity : dn) / 3;
        if (double.IsInfinity(reach)) return;
        node.InHandle = p - dir * reach;
        node.OutHandle = p + dir * reach;
    }

    /// <summary>Both handles present and collinear (opposite directions) = smooth node.</summary>
    public static bool HasAlignedHandles(this BezierNode node)
    {
        if (node.InHandle is not { } a || node.OutHandle is not { } b) return false;
        var u = a - node.Point;
        var v = b - node.Point;
        double lu = u.Length, lv = v.Length;
        if (!(lu > 0) || !(lv > 0)) return false;
        double cross = (u.X * v.Y - u.Y * v.X) / (lu * lv), dot = (u.X * v.X + u.Y * v.Y) / (lu * lv);
        return Math.Abs(cross) < 0.02 && dot < 0;
    }

    public static NodeStyle NodeStyleAt(this Feature f, int i)
    {
        if (i >= f.Nodes.Count && f.HasHoles && f.RingLocation(i) is { } loc) return f.RingFeature(loc.Ring).NodeStyleAt(loc.Index);
        if (i < 0 || i >= f.Nodes.Count) return NodeStyle.Sharp;
        var n = f.Nodes[i];
        if (n.Smart == true) return NodeStyle.Smart;
        if (n.HasAlignedHandles()) return NodeStyle.Smooth;
        bool isEnd = !f.IsClosed && (i == 0 || i == f.Nodes.Count - 1);
        return isEnd && n.IsSmooth ? NodeStyle.Smooth : NodeStyle.Sharp;
    }

    public static void ConvertNode(this Feature f, int i, NodeStyle style)
    {
        if (i >= f.Nodes.Count && f.HasHoles) { f.EditRing(i, (r, j) => r.ConvertNode(j, style)); return; }
        if (i < 0 || i >= f.Nodes.Count) return;
        var node = f.Nodes[i];
        switch (style)
        {
            case NodeStyle.Sharp:
                node.InHandle = null; node.OutHandle = null; node.Smart = null;
                break;
            case NodeStyle.Smooth:
            {
                node.Smart = null;
                if (Tangent(f, i) is not { } dir) return;
                var p = node.Point;
                var (prevLen, nextLen) = NeighborReach(f, i);
                double inLen = node.InHandle is { } ih && ih.DistanceTo(p) > 0 ? ih.DistanceTo(p) : prevLen;
                double outLen = node.OutHandle is { } oh && oh.DistanceTo(p) > 0 ? oh.DistanceTo(p) : nextLen;
                node.InHandle = inLen > 0 ? p - dir * inLen : null;
                node.OutHandle = outLen > 0 ? p + dir * outLen : null;
                break;
            }
            case NodeStyle.Smart:
                node.Smart = true;
                ApplySmartHandles(f, i);
                break;
        }
    }

    private static void ApplySmartHandles(Feature f, int i)
    {
        var node = f.Nodes[i];
        var p = node.Point;
        int n = f.Nodes.Count;
        bool isEnd = !f.IsClosed && (i == 0 || i == n - 1);
        if (isEnd || Tangent(f, i) is not { } dir) { node.InHandle = null; node.OutHandle = null; return; }
        var (prevLen, nextLen) = NeighborReach(f, i);
        node.InHandle = p - dir * prevLen;
        node.OutHandle = p + dir * nextLen;
    }

    /// <summary>Recompute the handles of all smart nodes (after nodes are moved/added/deleted).</summary>
    public static void RefreshSmartNodes(this Feature f)
    {
        for (int i = 0; i < f.Nodes.Count; i++) if (f.Nodes[i].Smart == true) ApplySmartHandles(f, i);
        if (f.Holes is not null && f.Kind == FeatureKind.Area)
        {
            for (int k = 0; k < f.Holes.Count; k++)
            {
                var ring = f.RingFeature(k + 1);
                for (int i = 0; i < ring.Nodes.Count; i++) if (ring.Nodes[i].Smart == true) ApplySmartHandles(ring, i);
            }
        }
    }

    /// <summary>Move one handle. On a smooth node the opposite handle rotates along (collinear) with unchanged length.</summary>
    public static void MoveHandle(this Feature f, int i, bool isIn, Point2D h)
    {
        if (i >= f.Nodes.Count && f.HasHoles) { f.EditRing(i, (r, j) => r.MoveHandle(j, isIn, h)); return; }
        if (i < 0 || i >= f.Nodes.Count) return;
        var node = f.Nodes[i];
        var p = node.Point;
        bool linked = node.Smart == true || node.HasAlignedHandles();
        node.Smart = null;
        if (isIn) node.InHandle = h; else node.OutHandle = h;
        var other = isIn ? node.OutHandle : node.InHandle;
        if (!linked || other is not { } o) return;
        var d = h - p;
        double len = d.Length;
        if (!(len > 0)) return;
        double otherLen = o.DistanceTo(p);
        var mirrored = p - new Point2D(d.X / len * otherLen, d.Y / len * otherLen);
        if (isIn) node.OutHandle = mirrored; else node.InHandle = mirrored;
    }

    // MARK: Rotation

    /// <summary>Rotation centre: the point's position, or the mean of the outer nodes.</summary>
    public static Point2D? RotationPivot(this Feature f)
    {
        if (f.Nodes.Count == 0) return null;
        double n = f.Nodes.Count;
        return new Point2D(f.Nodes.Sum(x => x.Point.X) / n, f.Nodes.Sum(x => x.Point.Y) / n);
    }

    private static void RotateGeometry(Feature f, double delta)
    {
        if (f.Kind == FeatureKind.Point || delta == 0 || f.RotationPivot() is not { } c) return;
        var rotated = f.MappingAllNodes(n => new BezierNode(Feature.Rotate(n.Point, c, delta),
            n.InHandle is { } ih ? Feature.Rotate(ih, c, delta) : null, n.OutHandle is { } oh ? Feature.Rotate(oh, c, delta) : null, n.Smart) { Extra = n.Extra });
        f.Nodes = rotated.Nodes;
        f.Holes = rotated.Holes;
    }

    /// <summary>Set the absolute angle (0.1° steps). Lines/areas: nodes + handles are rotated by the difference around the centre.</summary>
    public static void SetRotation(this Feature f, double degrees)
    {
        double target = Math.Round(Feature.NormalizedDegrees(degrees) * 10) / 10;
        RotateGeometry(f, target - f.RotationDegrees);
        f.RotationDeg = target == 0 ? null : target;
    }

    /// <summary>Set the absolute angle without rounding (precision rotation).</summary>
    public static void SetRotationExactly(this Feature f, double degrees)
    {
        double target = Feature.NormalizedDegrees(degrees);
        RotateGeometry(f, target - f.RotationDegrees);
        f.RotationDeg = Math.Abs(target) < 1e-9 ? null : target;
    }

    // MARK: Change symbol

    public static bool CanSwitch(this Feature f, MapSymbol s) => (f.Kind, s.SymbolKind) switch
    {
        (FeatureKind.Point, MapSymbol.Kind.Point) => (s.PointElements?.Count ?? 0) > 0,
        (FeatureKind.Line, MapSymbol.Kind.Line) or (FeatureKind.Area, MapSymbol.Kind.Area) => true,
        (FeatureKind.Line, MapSymbol.Kind.Area) => f.Nodes.Count >= 3,
        (FeatureKind.Area, MapSymbol.Kind.Line) => true,
        _ => false,
    };

    /// <summary>Change symbol: geometry, rotation and point size are kept; colour, width, double line, dashes and shape follow the new symbol.</summary>
    public static bool SwitchSymbol(this Feature f, MapSymbol s)
    {
        if (!f.CanSwitch(s)) return false;
        string? oldPrefix = f.SymbolCode is { } oc ? oc + " " : null;
        f.SymbolCode = s.Code;
        f.SymbolName = s.Name;
        f.ColorHex = s.ColorHex;
        f.CasingColorHex = null; f.InnerWidthMM = null; f.DashMM = null; f.InnerDashMM = null; f.RoundCaps = null;
        switch (s.SymbolKind)
        {
            case MapSymbol.Kind.Point:
                f.PointElements = s.PointElements?.Select(e => e.Clone()).ToList();
                f.WidthMM = (s.PointElements ?? new List<PointElement>()).DiameterMM() * f.EffectiveSizeFactor;
                break;
            case MapSymbol.Kind.Line:
                f.Kind = FeatureKind.Line;
                f.Holes = null;
                if (s.LineStyle is { } style) f.Apply(style); else { f.WidthMM = s.DrawingWidthMM; f.StandardWidthMM = s.DrawingWidthMM; }
                break;
            case MapSymbol.Kind.Area:
                f.Kind = FeatureKind.Area;
                f.WidthMM = s.DrawingWidthMM;
                f.StandardWidthMM = s.DrawingWidthMM;
                break;
        }
        if (oldPrefix is not null && f.Name.StartsWith(oldPrefix, StringComparison.Ordinal))
        {
            var last = f.Name.Split(' ').LastOrDefault();
            string suffix = int.TryParse(last, out var num) ? $" {num}" : "";
            f.Name = $"{s.Code} {s.Name}{suffix}";
        }
        return true;
    }

    // MARK: OCAD-like tools

    /// <summary>Add a node at a path position without changing the shape. Returns the new node's index.</summary>
    public static int? InsertNode(this Feature f, PathPosition pos)
    {
        var segs = f.Segments;
        if (f.Kind == FeatureKind.Point || pos.Segment < 0 || pos.Segment >= segs.Count || pos.T <= 1e-6 || pos.T >= 1 - 1e-6) return null;
        var s = segs[pos.Segment];
        int i = pos.Segment, j = (i + 1) % f.Nodes.Count;
        if (s.IsCurve)
        {
            var (l, r) = s.Split(pos.T);
            f.Nodes[i].OutHandle = l.C1;
            f.Nodes[j].InHandle = r.C2;
            f.Nodes.Insert(i + 1, new BezierNode(l.P3, l.C2, r.C1));
        }
        else
        {
            f.Nodes.Insert(i + 1, new BezierNode(s.PointAt(pos.T)));
        }
        return i + 1;
    }

    /// <summary>Add a node on ring k without changing the shape. Returns its Node Tool index.</summary>
    public static int? InsertNode(this Feature f, int ring, PathPosition pos)
    {
        if (ring <= 0) return f.InsertNode(pos);
        var r = f.RingFeature(ring);
        r.Nodes = r.Nodes.Select(n => n.Clone()).ToList();
        if (r.InsertNode(pos) is not { } local) return null;
        f.SetRing(ring, r.Nodes);
        return f.NodeIndex(ring, local);
    }

    /// <summary>Drag a segment: the point at t on segment index moves by d by changing that segment's two handles.</summary>
    public static Feature BendingSegment(this Feature f, int index, double t, Point2D d)
    {
        var segs = f.Segments;
        if (index < 0 || index >= segs.Count) return f;
        var s = segs[index];
        t = Math.Clamp(t, 0.05, 0.95);
        var c1 = s.IsCurve ? s.C1 : s.P0 + (s.P3 - s.P0) * (1.0 / 3);
        var c2 = s.IsCurve ? s.C2 : s.P0 + (s.P3 - s.P0) * (2.0 / 3);
        double b1 = 3 * (1 - t) * (1 - t) * t, b2 = 3 * (1 - t) * t * t;
        double k = 1 / (b1 * (1 - t) + b2 * t);
        var g = f.Clone();
        g.MoveHandle(index, false, c1 + d * ((1 - t) * k));
        g.MoveHandle((index + 1) % f.Nodes.Count, true, c2 + d * (t * k));
        return g;
    }

    private static Feature NewPiece(Feature f, List<BezierNode> nodes)
    {
        var p = f.Clone();
        p.Id = Guid.NewGuid();
        p.Name = "";
        p.CreatedAt = DateTime.UtcNow;
        p.Nodes = nodes;
        return p;
    }

    /// <summary>Cut a line at one position into two lines (OCAD Cut).</summary>
    public static (Feature, Feature)? SplitLine(this Feature f, PathPosition pos)
    {
        if (f.Kind != FeatureKind.Line || f.Nodes.Count < 2) return null;
        var end = new PathPosition(f.Segments.Count - 1, 1);
        var start = new PathPosition(0, 0);
        if (pos.Order <= 1e-6 || pos.Order >= end.Order - 1e-6) return null;
        var a = f.ForwardNodes(start, pos);
        var b = f.ForwardNodes(pos, end);
        if (a.Count < 2 || b.Count < 2) return null;
        var first = f.Clone();
        first.Nodes = a;
        return (first, NewPiece(f, b));
    }

    /// <summary>Split an area into two with a straight line between two outline positions (OCAD Cut area).</summary>
    public static (Feature, Feature)? SplitArea(this Feature f, PathPosition a, PathPosition b)
    {
        if (f.Kind != FeatureKind.Area || f.PointAt(a) is not { } pa || f.PointAt(b) is not { } pb || pa.DistanceTo(pb) <= 1e-6) return null;
        var one = f.ForwardNodes(a, b);
        var two = f.ForwardNodes(b, a);
        if (one.Count < 3 || two.Count < 3) return null;
        var first = f.Clone();
        first.Nodes = one;
        return (first, NewPiece(f, two));
    }

    /// <summary>Every node and handle through transform; smart flags kept; holes re-normalised.</summary>
    public static Feature Mapped(this Feature f, Func<Point2D, Point2D> transform)
    {
        var g = f.MappingAllNodes(n => new BezierNode(transform(n.Point), n.InHandle is { } i ? transform(i) : null, n.OutHandle is { } o ? transform(o) : null, n.Smart) { Extra = n.Extra });
        g.NormalizeHoles();
        return g;
    }

    public static Feature ScaledBy(this Feature f, double k, Point2D center)
    {
        if (f.Kind == FeatureKind.Point || !(k > 0)) return f;
        Point2D S(Point2D p) => new(center.X + (p.X - center.X) * k, center.Y + (p.Y - center.Y) * k);
        return f.MappingAllNodes(n => new BezierNode(S(n.Point), n.InHandle is { } i ? S(i) : null, n.OutHandle is { } o ? S(o) : null) { Extra = n.Extra });
    }

    /// <summary>Parallel line/area at distance m (positive = left of the line direction; counterclockwise area: inward).</summary>
    public static Feature Parallel(this Feature f, double distance)
    {
        if (f.Kind == FeatureKind.Point || f.Nodes.Count < 2 || distance == 0) return f;
        if (f.HasHoles)
        {
            var result = f.RingFeature(0).Parallel(distance);
            var holes = new List<List<BezierNode>>();
            foreach (var ring in f.HoleRings)
            {
                var h = f.RingFeature(0);
                h.Nodes = ring;
                var moved = h.Parallel(distance).Nodes;
                double tolerance = Math.Abs(distance) / 50;
                bool folded = moved.Any(n => h.DistanceTo(n.Point, tolerance) < Math.Abs(distance) * 0.9);
                if (!folded && Math.Abs(Feature.SignedArea(moved)) >= 1e-9) holes.Add(moved);
            }
            result.Holes = holes.Count == 0 ? null : holes;
            result.NormalizeHoles();
            return result;
        }
        int n = f.Nodes.Count;
        Point2D Left(Point2D t) => new(-t.Y, t.X);
        var g = f.Clone();
        for (int i = 0; i < n; i++)
        {
            var node = f.Nodes[i];
            var p = node.Point;
            bool hasPrev = i > 0 || f.IsClosed, hasNext = i < n - 1 || f.IsClosed;
            var prev = f.Nodes[(i - 1 + n) % n];
            var next = f.Nodes[(i + 1) % n];
            Point2D? tin = hasPrev ? Unit(p - (node.InHandle ?? prev.OutHandle ?? prev.Point)) ?? Unit(p - prev.Point) : null;
            Point2D? tout = hasNext ? Unit((node.OutHandle ?? next.InHandle ?? next.Point) - p) ?? Unit(next.Point - p) : null;
            var normals = new[] { tin, tout }.Where(t => t.HasValue).Select(t => Left(t!.Value)).ToList();
            if (normals.Count == 0) continue;
            var dir = normals[0];
            double factor = 1;
            if (normals.Count == 2 && Unit(normals[0] + normals[1]) is { } avg)
            {
                dir = avg;
                factor = 1 / Math.Max(1.0 / 3, dir.X * normals[0].X + dir.Y * normals[0].Y);
            }
            g.Nodes[i] = node.MovedBy(new Point2D(dir.X * distance * factor, dir.Y * distance * factor));
        }
        return g;
    }

    /// <summary>Reverse the line direction (OCAD Reverse); holes stay opposite to the outer ring.</summary>
    public static void Reverse(this Feature f)
    {
        f.Nodes = f.ReversedNodes();
        f.Holes = f.Holes?.Select(h => h.AsEnumerable().Reverse().Select(n => n.Reversed()).ToList()).ToList();
    }
}

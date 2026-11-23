using Occa.Core.Geometry;
using Occa.Core.Vector;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

/// <summary>Ports of FeatureMergeTests, FeatureFollowTests, ShapeToolTests, RotationSnapTests and AreaTJunctionMergeTests.</summary>
public class FeatureMergeTests
{
    private static Feature L(params (double, double)[] pts) => Line(Ring(pts));
    private static Feature LNoSymbol(params (double, double)[] pts) => Line(Ring(pts), null);
    private static Feature A(params (double, double)[] pts) => Area(Ring(pts));

    [Fact]
    public void JoinLinesAtEveryEndCombination()
    {
        var a = L((0, 0), (10, 0));
        var b = L((20, 5), (10, 0));
        var j = a.JoiningLine(b, 0.01)!;
        Assert.Equal(new[] { P(0, 0), P(10, 0), P(20, 5) }, Points(j));
        Assert.Equal(a.Id, j.Id);
        var c = L((-10, 0), (0, 0));
        Assert.Equal(new[] { -10.0, 0, 10 }, Points(a.JoiningLine(c, 0.01)!).Select(p => p.X));
    }

    [Fact]
    public void FollowedLineOnTopIsNotJoined()
    {
        var curve = L((0, 0), (10, 5), (20, 0));
        var follow = L((5, 2.5), (10, 5), (20, 0));
        Assert.Null(follow.JoiningLine(curve, 0.01));
        Assert.Null(curve.JoiningLine(follow, 0.01));
        Assert.NotNull(curve.JoiningLine(L((20, 0), (30, -5)), 0.01));
        Assert.NotNull(curve.JoiningLine(L((20.8, 0.3), (12, -3)), 1.8));
    }

    [Fact]
    public void LineEndsMeetingItsOwnStartAreClosed()
    {
        var f = L((0, 0), (10, 0), (10, 10), (0, 10), (0.03, 0.04));
        Assert.False(f.IsRingLine);
        Assert.False(f.CloseLineIfEndsMeet(0.01));
        Assert.True(f.CloseLineIfEndsMeet(0.5));
        Assert.Equal(f.Nodes[0].Point, f.Nodes[^1].Point);
        Assert.True(f.IsRingLine && f.ClosesPath);
        Assert.False(f.IsClosed);
        Assert.False(f.CloseLineIfEndsMeet(0.5));
    }

    [Fact]
    public void FollowRingLineThroughItsStart()
    {
        var ring = L((0, 0), (10, 0), (10, 10), (0, 10), (0, 0));
        Assert.True(ring.IsRingLine);
        var a = ring.NearestPathPosition(P(5, 0))!.Value.Position;
        var b = ring.NearestPathPosition(P(0, 5))!.Value.Position;
        var nodes = ring.FollowNodes(a, b);
        Assert.Equal(new[] { P(5, 0), P(0, 0), P(0, 5) }, nodes.Select(n => n.Point));
        var area = ring.Clone();
        area.Kind = FeatureKind.Area;
        area.Nodes.RemoveAt(area.Nodes.Count - 1);
        var pa = area.NearestPathPosition(P(5, 0))!.Value.Position;
        var pb = area.NearestPathPosition(P(0, 5))!.Value.Position;
        Assert.Equal(nodes.Select(n => n.Point), area.FollowNodes(pa, pb).Select(n => n.Point));
    }

    [Fact]
    public void JoinLinesWithGapMeetsInTheMiddle()
    {
        var a = L((0, 0), (10, 0));
        var b = L((12, 0), (20, 0));
        Assert.Null(a.JoiningLine(b, 1));
        var j = a.JoiningLine(b, 3)!;
        Assert.Equal(3, j.Nodes.Count);
        Assert.Equal(P(11, 0), j.Nodes[1].Point);
    }

    [Fact]
    public void JoinKeepsCurveHandles()
    {
        var a = L((0, 0), (10, 0));
        a.Nodes[1].InHandle = P(8, 2);
        var b = L((10, 0), (20, 0));
        b.Nodes[0].OutHandle = P(12, -2);
        var j = a.JoiningLine(b, 0.01)!;
        Assert.Equal(P(8, 2), j.Nodes[1].InHandle);
        Assert.Equal(P(12, -2), j.Nodes[1].OutHandle);
    }

    [Fact]
    public void OnlySameSymbolMerges()
    {
        var a = L((0, 0), (10, 0));
        Assert.Null(a.JoiningLine(Line(Ring((10, 0), (20, 0)), "101.0"), 1));
        Assert.Null(a.JoiningLine(LNoSymbol((10, 0), (20, 0)), 1));
        Assert.NotNull(LNoSymbol((0, 0), (10, 0)).JoiningLine(LNoSymbol((10, 0), (20, 0)), 1));
        Assert.False(a.CanMerge(Area(Ring((0, 0), (1, 0), (1, 1)), "506.0")));
    }

    [Fact]
    public void StitchAdjacentAreasKeepsSharedEdgeOut()
    {
        var a = A((0, 0), (10, 0), (10, 10), (0, 10));
        var b = Area(Reversed(Ring((10, 10), (10, 0), (20, 0), (20, 10))));
        var r = a.UnitingArea(b, 0.01, 0.1, allowOverlap: false)!;
        Assert.False(r.FlattenedCurves);
        Assert.Equal(200, Math.Abs(r.Feature.SignedArea()), 9);
        Assert.Equal(new HashSet<Point2D> { P(0, 0), P(10, 0), P(20, 0), P(20, 10), P(10, 10), P(0, 10) }, r.Feature.Nodes.Select(n => n.Point).ToHashSet());
    }

    [Fact]
    public void StitchKeepsBezierCurves()
    {
        var a = A((0, 0), (10, 0), (10, 10), (0, 10));
        a.Nodes[3].OutHandle = P(-3, 7);
        a.Nodes[0].InHandle = P(-3, 3);
        var b = A((10, 0), (20, 0), (20, 10), (10, 10));
        var r = a.UnitingArea(b, 0.01, 0.1, allowOverlap: false)!;
        Assert.Contains(r.Feature.Nodes, n => n.OutHandle == P(-3, 7));
        Assert.Equal(6, r.Feature.Nodes.Count);
    }

    [Fact]
    public void OverlappingAreasUnion()
    {
        var a = A((0, 0), (10, 0), (10, 10), (0, 10));
        var b = A((5, 5), (15, 5), (15, 15), (5, 15));
        Assert.Null(a.UnitingArea(b, 0.01, 0.1, allowOverlap: false));
        var r = a.UnitingArea(b, 0.01, 0.1)!;
        Assert.Equal(8, r.Feature.Nodes.Count);
        Assert.Equal(175, Math.Abs(r.Feature.SignedArea()), 6);
        Assert.False(r.FormedHole);
    }

    [Fact]
    public void AreaContainedAndSeparate()
    {
        var big = A((0, 0), (10, 0), (10, 10), (0, 10));
        Assert.Equal(100, Math.Abs(big.UnitingArea(A((2, 2), (4, 2), (4, 4), (2, 4)), 0.01, 0.1)!.Feature.SignedArea()), 6);
        Assert.Null(big.UnitingArea(A((20, 0), (30, 0), (30, 10), (20, 10)), 0.01, 0.1));
    }

    [Fact]
    public void UnionTouchingAlongPartOfEdge()
    {
        var a = A((0, 0), (10, 0), (10, 10), (0, 10));
        var b = A((10, 0), (20, 0), (20, 5), (10, 5));
        Assert.Equal(150, Math.Abs(a.UnitingArea(b, 0.01, 0.1)!.Feature.SignedArea()), 6);
    }

    [Fact]
    public void UnionWithHoleKeepsHole()
    {
        var u = A((0, 0), (30, 0), (30, 30), (20, 30), (20, 10), (10, 10), (10, 30), (0, 30));
        var lid = A((0, 25), (30, 25), (30, 35), (0, 35));
        var r = u.UnitingArea(lid, 0.01, 0.1)!;
        Assert.True(r.FormedHole);
        Assert.Equal(30 * 35, Math.Abs(r.Feature.SignedArea()), 6);
        Assert.Single(r.Feature.HoleRings);
        Assert.Equal(30 * 35 - 10 * 15, r.Feature.FilledArea(0.01), 6);
    }

    [Fact]
    public void AreasJoinedAtTJunctionAreUnited()
    {
        var first = System.Text.Json.JsonSerializer.Deserialize<List<BezierNode>>(Fixtures.Bytes("tjunction-first.json"), Core.Json.OccaJson.Options)!;
        var second = System.Text.Json.JsonSerializer.Deserialize<List<BezierNode>>(Fixtures.Bytes("tjunction-second.json"), Core.Json.OccaJson.Options)!;
        Feature Paved(List<BezierNode> n) => new() { Kind = FeatureKind.Area, ColorHex = "#F2DCC6", WidthMM = 0.1, Nodes = n, SymbolCode = "501.2", SymbolName = "Paved area" };
        var a = Paved(first);
        var b = Paved(second);
        var r = a.UnitingArea(b, 0.01, 0.2)!;
        Assert.False(r.FlattenedCurves);
        Assert.Equal(Math.Abs(a.SignedArea()) + Math.Abs(b.SignedArea()), Math.Abs(r.Feature.SignedArea()), 0.5);
        Assert.NotNull(b.UnitingArea(a, 0.01, 0.2, allowOverlap: false));
        var a2 = a.InsertingVertices(b, 0.01);
        Assert.Equal(a.Nodes.Count + 1, a2.Nodes.Count);
        Assert.True(Math.Abs(a.SignedArea() - a2.SignedArea()) < 1e-2, "Shape unchanged by the inserted vertex");
    }
}

public class FeatureFollowTests
{
    private static Feature Square() => Area(Rect(0, 0, 10, 10));

    [Fact]
    public void NearestPathPosition()
    {
        var r = Square().NearestPathPosition(P(4, -1))!.Value;
        Assert.Equal(0, r.Position.Segment);
        Assert.Equal(4, r.Point.X, 9);
        Assert.Equal(1, r.Distance, 9);
    }

    [Fact]
    public void FollowAreaTakesShorterSideOrViaSide()
    {
        var square = Square();
        var a = square.NearestPathPosition(P(5, -1))!.Value.Position;
        var b = square.NearestPathPosition(P(11, 5))!.Value.Position;
        Assert.Equal(new[] { P(5, 0), P(10, 0), P(10, 5) }, square.FollowNodes(a, b).Select(n => n.Point));
        Assert.Equal(new[] { P(5, 0), P(0, 0), P(0, 10), P(10, 10), P(10, 5) }, square.FollowNodes(a, b, P(-1, 5)).Select(n => n.Point));
    }

    [Fact]
    public void FollowLineBackwardsAndCurveSplit()
    {
        var line = new Feature { Kind = FeatureKind.Line, ColorHex = "#000000", WidthMM = 0.25, SymbolCode = "506.0", Nodes = new List<BezierNode> { new(P(0, 0), null, P(0, 10)), new(P(10, 0), P(10, 10)) } };
        var full = line.FollowNodes(line.PathPositionOfNode(1), line.PathPositionOfNode(0));
        Assert.Equal(P(10, 0), full[0].Point);
        Assert.Equal(P(0, 0), full[^1].Point);
        Assert.Equal(P(10, 10), full[0].OutHandle);
        var half = line.FollowNodes(new PathPosition(0, 0), new PathPosition(0, 0.5));
        Assert.Equal(5, half[^1].Point.X, 9);
        Assert.Equal(7.5, half[^1].Point.Y, 9);
    }

    [Fact]
    public void FreehandTraceSnapsRunAlongObject()
    {
        var pts = new List<Point2D> { P(-10, -10), P(-5, -5) };
        for (double x = 0.5; x <= 9.5 + 1e-9; x += 0.5) pts.Add(P(x, (x * 7) % 0.6 - 0.3));
        pts.Add(P(15, -5));
        pts.Add(P(20, -10));
        var nodes = GuideTrace.Trace(pts, new[] { Square() }, 1, 0.1);
        var ys = nodes.Select(n => n.Point).Where(p => p.X > 0.4 && p.X < 9.6).Select(p => p.Y).ToList();
        Assert.NotEmpty(ys);
        Assert.All(ys, y => Assert.True(Math.Abs(y) < 1e-9));
        Assert.Equal(P(-10, -10), nodes[0].Point);
        Assert.Equal(P(20, -10), nodes[^1].Point);
    }

    [Fact]
    public void ShortCrossingIsNotFollowed()
    {
        var nodes = GuideTrace.Trace(new[] { P(5, -5), P(5, -0.2), P(5, 0.2), P(5, 5) }, new[] { Square() }, 1, 0.1);
        Assert.Equal(new[] { P(5, -5), P(5, 5) }, nodes.Select(n => n.Point));
    }
}

public class ShapeToolTests
{
    private static readonly Point2D[] Square = { P(0, 100), P(100, 100), P(100, 0), P(0, 0) };

    [Fact]
    public void RectangleIsClosedAreaThroughCorners()
    {
        var r = FeatureEdit.Rectangle(Square, "#000000", 0.14);
        Assert.Equal(FeatureKind.Area, r.Kind);
        Assert.Equal(Square, Points(r));
        Assert.Equal(4, r.Segments.Count);
    }

    [Fact]
    public void EllipseApproximatesCircle()
    {
        var e = FeatureEdit.Ellipse(Square, "#000000", 0.14);
        Assert.Equal(4, e.Nodes.Count);
        var c = P(50, 50);
        foreach (var s in e.Segments)
            for (int k = 0; k <= 20; k++) Assert.Equal(50, s.PointAt(k / 20.0).DistanceTo(c), 50 * 0.0003);
    }

    [Fact]
    public void ToggleSmoothAndMirroredHandles()
    {
        var f = new Feature { Kind = FeatureKind.Line, ColorHex = "#000000", WidthMM = 0.14, Nodes = Ring((0, 0), (30, 30), (60, 0)) };
        f.ToggleSmooth(1);
        Assert.True(f.Nodes[1].IsSmooth);
        Assert.Equal(30, f.Nodes[1].OutHandle!.Value.Y, 9);
        double otherLen = f.Nodes[1].InHandle!.Value.DistanceTo(f.Nodes[1].Point);
        f.MoveHandle(1, false, P(30, 50));
        Assert.Equal(30, f.Nodes[1].InHandle!.Value.X, 9);
        Assert.Equal(30 - otherLen, f.Nodes[1].InHandle!.Value.Y, 9);
        f.ToggleSmooth(1);
        Assert.False(f.Nodes[1].IsSmooth);
    }

    [Fact]
    public void InsertSplitAndBend()
    {
        var line = Line(Ring((0, 0), (10, 0), (20, 0)));
        Assert.Equal(1, line.InsertNode(new PathPosition(0, 0.5)));
        Assert.Equal(P(5, 0), line.Nodes[1].Point);
        var (a, b) = line.SplitLine(new PathPosition(1, 0.5))!.Value;
        Assert.Equal(new[] { P(0, 0), P(5, 0), P(7.5, 0) }, Points(a));
        Assert.Equal(new[] { P(7.5, 0), P(10, 0), P(20, 0) }, Points(b));
        Assert.NotEqual(a.Id, b.Id);
        var bent = Line(Ring((0, 0), (10, 0))).BendingSegment(0, 0.5, P(0, 3));
        Assert.Equal(3, bent.Segments[0].PointAt(0.5).Y, 6);
        var area = Area(Rect(0, 0, 10, 10));
        var (one, two) = area.SplitArea(new PathPosition(0, 0.5), new PathPosition(2, 0.5))!.Value;
        Assert.Equal(50, PolygonArea(one), 6);
        Assert.Equal(50, PolygonArea(two), 6);
    }
}

public class RotationSnapTests
{
    private static Feature L(params Point2D[] pts) => new() { Kind = FeatureKind.Line, ColorHex = "#000000", WidthMM = 0.3, Nodes = pts.Select(p => new BezierNode(p)).ToList() };

    [Fact]
    public void BearingOfLineDirection()
    {
        Assert.Equal(0, RotationSnap.Bearing(P(0, 0), P(0, 10)), 9);
        Assert.Equal(90, RotationSnap.Bearing(P(0, 0), P(10, 0)), 9);
        Assert.Equal(90, RotationSnap.Bearing(P(0, 0), P(-10, 0)), 9);
        Assert.Equal(45, RotationSnap.Bearing(P(0, 0), P(10, 10)), 9);
    }

    [Fact]
    public void LineSnapsParallelToNeighbourEdge()
    {
        var moving = L(P(0, 0), P(20, 0));
        var neighbour = L(P(50, 0), P(50 + 10 * Math.Sin(120 * Math.PI / 180), 10 * Math.Cos(120 * Math.PI / 180)));
        var r = RotationSnap.Snap(28, 0, moving.StraightEdges(1), neighbour.StraightEdges(1), includeNorth: false);
        Assert.Equal(30, r.Degrees, 9);
        Assert.False(r.IsStep);
        Assert.NotNull(r.Reference);
        var p = RotationSnap.Snap(-58, 0, moving.StraightEdges(1), neighbour.StraightEdges(1), includeNorth: false);
        Assert.Equal(300, p.Degrees, 9);
    }

    [Fact]
    public void FarFromAnyEdgeFallsBackToSteps()
    {
        var r = RotationSnap.Snap(38, 0, L(P(0, 0), P(20, 0)).StraightEdges(1), L(P(50, 0), P(50, 10)).StraightEdges(1), includeNorth: false);
        Assert.True(r.IsStep);
        Assert.Equal(45, r.Degrees, 9);
    }

    [Fact]
    public void ExactRotationKeepsEdgeExactlyParallel()
    {
        var f = L(P(0, 0), P(20, 0));
        f.SetRotationExactly(30.123456);
        Assert.Equal(120.123456, f.StraightEdges(1)[0].Bearing, 6);
        Assert.Equal(30.123456, f.RotationDegrees, 9);
    }
}

using Occa.Core.Geometry;
using Occa.Core.Vector;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

/// <summary>Port of FeatureCutTests.swift: cutting areas with a shape/path and lines with a box.</summary>
public class FeatureCutTests
{
    private static readonly List<BezierNode> Square = Rect(0, 0, 10, 10);
    private static Feature SquareArea() => Area(Rect(0, 0, 10, 10));
    private static List<Point2D> Box(double x0, double y0, double x1, double y1) => new() { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1) };

    [Fact]
    public void RectangleAcrossOneEdge()
    {
        var f = SquareArea();
        var parts = f.SubtractingShape(Rect(8, 2, 14, 6)).Pieces!;
        Assert.Single(parts);
        Assert.Equal(f.Id, parts[0].Id);
        Assert.Equal("406.0", parts[0].SymbolCode);
        Assert.Equal(100 - 2 * 4, PolygonArea(parts[0]), 6);
        Assert.Equal(8, parts[0].Nodes.Count);
        foreach (var c in Square) Assert.Contains(parts[0].Nodes, n => n.Point.DistanceTo(c.Point) < 1e-9);
    }

    [Fact]
    public void RectangleCutsCorner()
    {
        var parts = SquareArea().SubtractingShape(Rect(5, 5, 12, 12)).Pieces!;
        Assert.Single(parts);
        Assert.Equal(75, PolygonArea(parts[0]), 6);
        Assert.Equal(6, parts[0].Nodes.Count);
    }

    [Fact]
    public void StripSplitsAreaInTwo()
    {
        var f = SquareArea();
        var parts = f.SubtractingShape(Rect(4, -2, 6, 12)).Pieces!;
        Assert.Equal(2, parts.Count);
        Assert.Equal(80, parts.Sum(PolygonArea), 6);
        Assert.Equal(f.Id, parts[0].Id);
        Assert.NotEqual(f.Id, parts[1].Id);
        Assert.Equal("406.0", parts[1].SymbolCode);
        foreach (var p in parts) Assert.Equal(40, PolygonArea(p), 6);
    }

    [Fact]
    public void ShapeInsideMakesHoleOrNoOp()
    {
        var holed = SquareArea().SubtractingShape(Rect(2, 2, 4, 4)).Pieces!;
        Assert.Single(holed);
        Assert.Single(holed[0].HoleRings);
        Assert.Equal(ShapeCutFailure.NoOverlap, SquareArea().SubtractingShape(Rect(20, 20, 30, 30)).Failure);
        Assert.Empty(SquareArea().SubtractingShape(Rect(-1, -1, 11, 11)).Pieces!);
    }

    [Fact]
    public void ShapeSharingAnEdge()
    {
        Assert.Equal(ShapeCutFailure.NoOverlap, SquareArea().SubtractingShape(Rect(10, 2, 14, 6)).Failure);
        var parts = SquareArea().SubtractingShape(Rect(8, 2, 10, 6)).Pieces!;
        Assert.Single(parts);
        Assert.Equal(92, PolygonArea(parts[0]), 6);
    }

    [Fact]
    public void ClockwiseAreaAndShapeThroughCorners()
    {
        var f = Area(Reversed(Rect(0, 0, 10, 10)));
        Assert.Empty(f.SubtractingShape(Rect(-2, -2, 10, 10)).Pieces!);
        var tri = f.SubtractingShape(Ring((0, 0), (10, 0), (10, 10))).Pieces!;
        Assert.Single(tri);
        Assert.Equal(50, PolygonArea(tri[0]), 6);
        Assert.True(tri[0].SignedArea() < 0, "Original (clockwise) direction kept");
    }

    [Fact]
    public void EllipseCutKeepsCurves()
    {
        var e = FeatureEdit.Ellipse(new[] { P(6, 14), P(14, 14), P(14, 6), P(6, 6) }, "#000", 0);
        var parts = SquareArea().SubtractingShape(e.Nodes).Pieces!;
        Assert.Single(parts);
        Assert.Equal(100 - Math.PI * 16 / 4, PolygonArea(parts[0]), 0.05);
        Assert.Contains(parts[0].Nodes, n => n.IsSmooth);
    }

    [Fact]
    public void CurvedAreaKeepsUntouchedCurves()
    {
        var circle = FeatureEdit.Ellipse(new[] { P(-10, 10), P(10, 10), P(10, -10), P(-10, -10) }, "#000", 0);
        var f = SquareArea();
        f.Nodes = circle.Nodes;
        var parts = f.SubtractingShape(Rect(5, -20, 20, 20)).Pieces!;
        Assert.Single(parts);
        Assert.True(parts[0].Segments.Count(s => s.IsCurve) >= 3);
        Assert.True(parts[0].Nodes.Max(n => n.Point.X) < 5 + 1e-6);
    }

    [Fact]
    public void PathSplitTwoCrossings()
    {
        var f = SquareArea();
        var parts = f.SplittingArea(Ring((-2, 3), (5, 6), (12, 3))).Pieces!;
        Assert.Equal(2, parts.Count);
        Assert.Equal(100, parts.Sum(PolygonArea), 6);
        Assert.Equal(f.Id, parts[0].Id);
    }

    [Fact]
    public void PathSplitFourCrossings()
    {
        var parts = SquareArea().SplittingArea(Ring((-2, 2), (12, 2), (12, 8), (-2, 8))).Pieces!;
        Assert.Equal(3, parts.Count);
        var areas = parts.Select(PolygonArea).OrderBy(a => a).ToList();
        Assert.Equal(20, areas[0], 6);
        Assert.Equal(20, areas[1], 6);
        Assert.Equal(60, areas[2], 6);
    }

    [Fact]
    public void PathSplitFromEdgeToEdgeAndCurvedPath()
    {
        var path = new List<BezierNode> { new(P(0, 5), null, P(3, 9)), new(P(10, 5), P(7, 1)) };
        var parts = SquareArea().SplittingArea(path).Pieces!;
        Assert.Equal(2, parts.Count);
        Assert.Equal(100, parts.Sum(PolygonArea), 2);
        Assert.All(parts, p => Assert.Contains(p.Nodes, n => n.IsSmooth));
    }

    [Fact]
    public void PathNotCrossing()
    {
        Assert.Equal(ShapeCutFailure.NotCrossing, SquareArea().SplittingArea(Ring((2, 2), (8, 8))).Failure);
        Assert.Equal(ShapeCutFailure.NotCrossing, SquareArea().SplittingArea(Ring((-2, 5), (5, 5))).Failure);
        Assert.Equal(ShapeCutFailure.NoOverlap, SquareArea().SplittingArea(Ring((20, 0), (30, 10))).Failure);
    }

    [Fact]
    public void LineBoxCutMiddle()
    {
        var f = Line(Ring((0, 0), (10, 0), (20, 0)));
        var parts = f.CuttingOut(Box(8, -1, 12, 1))!;
        Assert.Equal(2, parts.Count);
        Assert.Equal(f.Id, parts[0].Id);
        Assert.NotEqual(f.Id, parts[1].Id);
        Assert.Equal(new[] { P(0, 0), P(8, 0) }, Points(parts[0]));
        Assert.Equal(new[] { P(12, 0), P(20, 0) }, Points(parts[1]));
        Assert.Equal("506.0", parts[1].SymbolCode);
    }

    [Fact]
    public void LineBoxCoversEnd()
    {
        var parts = Line(Ring((0, 0), (10, 0), (20, 0))).CuttingOut(Box(15, -1, 25, 1))!;
        Assert.Single(parts);
        Assert.Equal(new[] { P(0, 0), P(10, 0), P(15, 0) }, Points(parts[0]));
    }

    [Fact]
    public void LineBoxCoversAllOrNothing()
    {
        Assert.Empty(Line(Ring((0, 0), (10, 0), (20, 0))).CuttingOut(Box(-1, -1, 21, 1))!);
        Assert.Null(Line(Ring((0, 0), (10, 0), (20, 0))).CuttingOut(Box(0, 5, 10, 8)));
    }

    [Fact]
    public void LineBoxCrossingSeveralTimes()
    {
        var parts = Line(Ring((0, 0), (10, 0), (10, 4), (0, 4))).CuttingOut(Box(4, -1, 6, 5))!;
        Assert.Equal(3, parts.Count);
    }

    [Fact]
    public void BezierLineBoxCut()
    {
        var f = new Feature { Kind = FeatureKind.Line, ColorHex = "#000", WidthMM = 0.25, Nodes = new List<BezierNode> { new(P(0, 0), null, P(5, 10)), new(P(20, 0), P(15, -10)) } };
        var parts = f.CuttingOut(Box(8, -10, 12, 10))!;
        Assert.Equal(2, parts.Count);
        Assert.Equal(8, parts[0].Nodes[^1].Point.X, 6);
        Assert.Equal(12, parts[1].Nodes[0].Point.X, 6);
        Assert.All(parts, p => Assert.All(p.Segments, s => Assert.True(s.IsCurve)));
        foreach (var p in parts)
            foreach (var s in p.Segments)
                Assert.True(f.DistanceTo(s.PointAt(0.5), 0.0001) < 0.001);
    }

    [Fact]
    public void ClosedLineBoxCut()
    {
        var f = Line(Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0)));
        Assert.True(f.IsRingLine);
        var parts = f.CuttingOut(Box(3, -1, 7, 1))!;
        Assert.Single(parts);
        Assert.Equal(P(7, 0), parts[0].Nodes[0].Point);
        Assert.Equal(P(3, 0), parts[0].Nodes[^1].Point);
        Assert.Equal(6, parts[0].Nodes.Count);
        var corner = f.CuttingOut(Box(-1, -1, 2, 2))!;
        Assert.Single(corner);
        Assert.Equal(P(2, 0), corner[0].Nodes[0].Point);
        Assert.Equal(P(0, 2), corner[0].Nodes[^1].Point);
    }

    [Fact]
    public void RotatedBox()
    {
        var c = P(10, 0);
        const double r = 2;
        var diamond = new List<Point2D> { P(c.X, c.Y - r), P(c.X + r, c.Y), P(c.X, c.Y + r), P(c.X - r, c.Y) };
        var parts = Line(Ring((0, 0), (20, 0))).CuttingOut(diamond)!;
        Assert.Equal(2, parts.Count);
        Assert.Equal(8, parts[0].Nodes[^1].Point.X, 9);
        Assert.Equal(12, parts[1].Nodes[0].Point.X, 9);
    }
}

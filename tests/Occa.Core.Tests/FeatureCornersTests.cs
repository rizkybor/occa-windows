using Occa.Core.Geometry;
using Occa.Core.Vector;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

/// <summary>Port of FeatureCornersTests.swift.</summary>
public class FeatureCornersTests
{
    private static Feature Building(List<BezierNode> nodes) => Area(nodes, "521.1", "#000000", "Building");
    private static Feature PathLine(params (double, double)[] pts) => Line(Ring(pts), "507.0");

    [Fact]
    public void SquareOneRoundedCorner()
    {
        var square = Building(Rect(0, 0, 10, 10));
        var c = square.Corner(2)!.Value;
        Assert.Equal(Math.PI / 2, c.InteriorAngle, 12);
        Assert.Equal(5, c.MaxRadius, 12);
        AssertClose(c.Bisector, P(-1 / Math.Sqrt(2), -1 / Math.Sqrt(2)));

        var r = square.RoundingCorners(new[] { 2 }, 2);
        Assert.Equal(square.Id, r.Id);
        Assert.Equal("521.1", r.SymbolCode);
        Assert.Equal(5, r.Nodes.Count);
        AssertClose(r.Nodes[2].Point, P(10, 8));
        AssertClose(r.Nodes[3].Point, P(8, 10));
        double k = 4.0 / 3.0 * Math.Tan(Math.PI / 8) * 2;
        AssertClose(r.Nodes[2].OutHandle, P(10, 8 + k));
        AssertClose(r.Nodes[3].InHandle, P(8 + k, 10));
        Assert.Null(r.Nodes[2].InHandle);
        Assert.Null(r.Nodes[3].OutHandle);
        foreach (var i in new[] { 0, 1, 4 }) Assert.False(r.Nodes[i].IsSmooth);
        var mid = r.Segments[2].PointAt(0.5);
        Assert.Equal(2, mid.DistanceTo(P(8, 8)), 3);
        Assert.Equal(100 - (4 - Math.PI), r.FilledArea(0.0005), 2);
    }

    [Fact]
    public void StraightCornerIsAChamfer()
    {
        var r = Building(Rect(0, 0, 10, 10)).RoundingCorners(new[] { 2 }, 2, CornerStyle.Straight);
        Assert.Equal(5, r.Nodes.Count);
        Assert.DoesNotContain(r.Nodes, n => n.IsSmooth);
        Assert.Equal(98, r.FilledArea(0.0005), 9);
    }

    [Fact]
    public void AreaWithHoleRoundsHoleCorners()
    {
        var donut = Building(Rect(0, 0, 20, 20));
        donut.Holes = new() { Rect(5, 5, 15, 15) };
        donut.NormalizeHoles();
        var r = donut.RoundingCorners(new[] { 0, 4 }, 1);
        Assert.Equal(5, r.Nodes.Count);
        Assert.Single(r.HoleRings);
        Assert.Equal(5, r.HoleRings[0].Count);
        Assert.True(Feature.SignedArea(r.HoleRings[0]) * Feature.SignedArea(r.Nodes) < 0);
        Assert.Equal(300, r.FilledArea(0.0005), 2);
        Assert.Same(r, r.RoundingCorners(Array.Empty<int>(), 1));
    }

    [Fact]
    public void OpenLineRoundsInteriorCornerOnly()
    {
        var l = PathLine((0, 0), (10, 0), (10, 10));
        Assert.Null(l.Corner(0));
        Assert.Null(l.Corner(2));
        Assert.Equal(new[] { 1 }, l.RoundableCornerIndices());
        var r = l.RoundingCorners(new[] { 0, 1, 2 }, 3);
        Assert.Equal(FeatureKind.Line, r.Kind);
        Assert.Equal(4, r.Nodes.Count);
        AssertClose(r.Nodes[0].Point, P(0, 0));
        AssertClose(r.Nodes[1].Point, P(7, 0));
        AssertClose(r.Nodes[2].Point, P(10, 3));
        AssertClose(r.Nodes[3].Point, P(10, 10));
        Assert.Null(r.Nodes[0].OutHandle);
        Assert.Null(r.Nodes[3].InHandle);
    }

    [Fact]
    public void ClampingNeverOverlaps()
    {
        var f = Building(Rect(0, 0, 4, 10));
        Assert.Equal(2, f.Corner(0)!.Value.EffectiveRadius(50), 12);
        var r = f.RoundingCorners(new[] { 0, 1, 2, 3 }, 50);
        Assert.Equal(6, r.Nodes.Count);
        for (int i = 0; i < r.Nodes.Count; i++) Assert.True(r.Nodes[i].Point.DistanceTo(r.Nodes[(i + 1) % r.Nodes.Count].Point) > 1e-6);
        Assert.Contains(r.Nodes, n => n.Point.DistanceTo(P(2, 0)) < 1e-9);
        Assert.Contains(r.Nodes, n => n.Point.DistanceTo(P(2, 10)) < 1e-9);
        Assert.Equal(40 - 4 * (4 - Math.PI), r.FilledArea(0.0005), 1);
    }

    [Fact]
    public void ZeroRadiusAndStraightThroughNodesUnchanged()
    {
        var square = Building(Rect(0, 0, 10, 10));
        Assert.Same(square, square.RoundingCorners(new[] { 0, 1, 2, 3 }, 0));
        Assert.Same(square, square.RoundingCorners(new[] { 0 }, -1));
        var withMid = Building(Ring((0, 0), (5, 0), (10, 0), (10, 10), (0, 10)));
        Assert.Null(withMid.Corner(1));
        Assert.Equal(withMid.Nodes.Select(n => n.Point), withMid.RoundingCorners(new[] { 1 }, 2).Nodes.Select(n => n.Point));
        var ellipse = FeatureEdit.Ellipse(new[] { P(0, 10), P(10, 10), P(10, 0), P(0, 0) }, "#000000", 0);
        Assert.Empty(ellipse.RoundableCornerIndices());
        Assert.Null(new Feature { Kind = FeatureKind.Point, Nodes = Ring((0, 0)) }.Corner(0));
    }

    [Fact]
    public void CurvedNeighbourIsSplitOnTheCurve()
    {
        var f = Building(Ring((0, 0), (10, 0), (5, 8)));
        f.Nodes[0].OutHandle = P(3, -3);
        f.Nodes[1].InHandle = P(7, -3);
        var original = f.Segments[0];
        var c = f.Corner(1)!.Value;
        AssertClose(c.TowardPrevious, P(-3, -3) * (1 / Math.Sqrt(18)));
        var r = f.RoundingCorners(new[] { 1 }, 1);
        Assert.Equal(4, r.Nodes.Count);
        var start = r.Nodes[1];
        Assert.True(original.Nearest(start.Point).Distance < 1e-3);
        var inDir = start.InHandle!.Value - start.Point;
        var outDir = start.OutHandle!.Value - start.Point;
        double cross = (inDir.X * outDir.Y - inDir.Y * outDir.X) / (inDir.Length * outDir.Length);
        Assert.Equal(0, cross, 6);
        Assert.True(inDir.X * outDir.X + inDir.Y * outDir.Y < 0);
        AssertClose(r.Nodes[0].Point, P(0, 0));
        var expected = original.Sub(0, FeatureCorners.Parameter(original, FeatureCorners.ArcLength(original) - c.TangentDistance(1))).PointAt(0.25);
        AssertClose(r.Segments[0].PointAt(0.25), expected);
    }
}

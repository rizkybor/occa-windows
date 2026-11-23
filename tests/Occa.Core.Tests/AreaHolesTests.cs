using System.Text.Json;
using Occa.Core.Json;
using Occa.Core.Vector;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

/// <summary>Port of AreaHolesTests.swift (model, transforms, cut, split, merge; export parts come with M3).</summary>
public class AreaHolesTests
{
    private static Feature Square() => Area(Rect(0, 0, 10, 10));

    [Fact]
    public void CodableRoundTripAndOldFilesWithoutHoles()
    {
        var f = Donut();
        var json = JsonSerializer.Serialize(f, OccaJson.Options);
        var back = JsonSerializer.Deserialize<Feature>(json, OccaJson.Options)!;
        Assert.Single(back.HoleRings);
        Assert.Equal(f.HoleRings[0].Select(n => n.Point), back.HoleRings[0].Select(n => n.Point));
        var plain = JsonSerializer.Serialize(Square(), OccaJson.Options);
        Assert.DoesNotContain("holes", plain);
        Assert.Null(JsonSerializer.Deserialize<Feature>(plain, OccaJson.Options)!.Holes);
    }

    [Fact]
    public void WindingConventionHolesOppositeOuter()
    {
        Assert.True(Feature.SignedArea(Donut().Nodes) > 0);
        Assert.True(Feature.SignedArea(Donut().HoleRings[0]) < 0);
        var cw = Area(Reversed(Rect(0, 0, 20, 20)));
        cw.Holes = new() { Reversed(Rect(5, 5, 15, 15)) };
        cw.NormalizeHoles();
        Assert.True(Feature.SignedArea(cw.HoleRings[0]) > 0);
        var tiny = Square();
        tiny.Holes = new() { Ring((1, 1), (2, 2)) };
        tiny.NormalizeHoles();
        Assert.Null(tiny.Holes);
    }

    [Fact]
    public void ContainsPointExcludesHolesAndArea()
    {
        var f = Donut();
        Assert.True(f.ContainsPoint(P(2, 2), 0.01));
        Assert.False(f.ContainsPoint(P(10, 10), 0.01));
        Assert.False(f.ContainsPoint(P(30, 30), 0.01));
        Assert.Equal(0, f.HoleIndex(P(10, 10), 0.01));
        Assert.Equal(300, Filled(f), 9);
        Assert.Equal(1, f.DistanceTo(P(10, 14), 0.01), 9);
        Assert.Equal(1, f.NearestRingPosition(P(10, 14))!.Value.Ring);
        Assert.Equal(2, f.RingFeatures.Count);
    }

    [Fact]
    public void NodeToolIndicesCoverHoles()
    {
        var f = Donut();
        Assert.Equal(8, f.AllNodes().Count);
        Assert.Equal((1, 1), f.RingLocation(5));
        Assert.Equal(5, f.NodeIndex(1, 1));
        var all = f.AllNodes();
        all[5] = all[5].MovedBy(P(1, 0));
        f.SetAllNodes(all);
        Assert.Equal(4, f.Nodes.Count);
        Assert.Equal(Donut().HoleRings[0][1].Point + P(1, 0), f.HoleRings[0][1].Point);
        f.ConvertNode(6, NodeStyle.Smooth);
        Assert.True(f.HoleRings[0][2].IsSmooth);
        Assert.DoesNotContain(f.Nodes, n => n.IsSmooth);
        Assert.Equal(5, f.InsertNode(1, new PathPosition(0, 0.5)));
        Assert.Equal(5, f.HoleRings[0].Count);
    }

    [Fact]
    public void TransformsCarryHoles()
    {
        var f = Donut();
        var moved = f.MovedBy(P(100, 50));
        Assert.Equal(f.HoleRings[0][0].Point + P(100, 50), moved.HoleRings[0][0].Point);

        var rotated = Donut();
        rotated.SetRotation(90);
        var rotatedSet = rotated.HoleRings[0].Select(n => (Math.Round(n.Point.X * 1e6) / 1e6, Math.Round(n.Point.Y * 1e6) / 1e6)).ToHashSet();
        Assert.Equal(f.HoleRings[0].Select(n => (n.Point.X, n.Point.Y)).ToHashSet(), rotatedSet);

        var precise = Area(Rect(0, 0, 20, 10));
        precise.Holes = new() { Rect(2, 2, 4, 4) };
        precise.NormalizeHoles();
        precise.SetRotationExactly(90);
        Assert.Equal(11, precise.HoleRings[0].Min(n => n.Point.Y), 9);
        Assert.Equal(9, precise.HoleRings[0].Max(n => n.Point.X), 9);

        Assert.Equal(4 * 300, Filled(f.ScaledBy(2, P(10, 10))), 6);
        var mirrored = f.Mapped(p => P(-p.X, p.Y));
        Assert.True(Feature.SignedArea(mirrored.Nodes) * Feature.SignedArea(mirrored.HoleRings[0]) < 0);

        var reversed = Donut();
        reversed.Reverse();
        Assert.True(Feature.SignedArea(reversed.Nodes) * Feature.SignedArea(reversed.HoleRings[0]) < 0);

        var inward = f.Parallel(1);
        Assert.Equal(18 * 18, Math.Abs(Feature.SignedArea(inward.Nodes)), 6);
        Assert.Equal(12 * 12, Math.Abs(Feature.SignedArea(inward.HoleRings[0])), 6);
        Assert.Equal(8 * 8, Math.Abs(Feature.SignedArea(f.Parallel(-1).HoleRings[0])), 6);
        Assert.Null(f.Parallel(-6).Holes);

        Assert.Equal(f.Holes!.Count, f.WithSymbolSize(1.5).Holes!.Count);
        var line = Donut();
        Assert.True(line.SwitchSymbol(new MapSymbol { Number = 506_000, Name = "Path", SymbolKind = MapSymbol.Kind.Line, ColorHex = "#000000", LineWidthMM = 0.25 }));
        Assert.Null(line.Holes);
    }

    [Fact]
    public void RectangleInsideMakesHole()
    {
        var f = Square();
        var parts = f.SubtractingShape(Rect(2, 2, 4, 4)).Pieces!;
        Assert.Single(parts);
        Assert.Equal(f.Id, parts[0].Id);
        Assert.Equal(4, parts[0].Nodes.Count);
        Assert.Single(parts[0].HoleRings);
        Assert.Equal(96, Filled(parts[0]), 9);
        Assert.True(Feature.SignedArea(parts[0].HoleRings[0]) < 0);
    }

    [Fact]
    public void EllipseInsideMakesCurvedHole()
    {
        var e = FeatureEdit.Ellipse(new[] { P(3, 7), P(7, 7), P(7, 3), P(3, 3) }, "#000", 0);
        var parts = Square().SubtractingShape(e.Nodes).Pieces!;
        Assert.Single(parts);
        Assert.Single(parts[0].HoleRings);
        Assert.Contains(parts[0].HoleRings[0], n => n.IsSmooth);
        Assert.Equal(100 - Math.PI * 4, Filled(parts[0]), 2);
    }

    [Fact]
    public void SecondCutOverlappingHoleEnlargesIt()
    {
        var one = Square().SubtractingShape(Rect(2, 2, 4, 4)).Pieces![0];
        var two = one.SubtractingShape(Rect(3, 3, 6, 5)).Pieces!;
        Assert.Single(two);
        Assert.Single(two[0].HoleRings);
        Assert.Equal(100 - 9, Filled(two[0]), 6);
    }

    [Fact]
    public void ShapeCoveringHoleAbsorbsIt()
    {
        var parts = Donut().SubtractingShape(Rect(3, 3, 17, 17)).Pieces!;
        Assert.Single(parts);
        Assert.Single(parts[0].HoleRings);
        Assert.Equal(400 - 14 * 14, Filled(parts[0]), 6);
    }

    [Fact]
    public void ShapeInsideHoleIsReported()
    {
        Assert.Equal(ShapeCutFailure.InsideHole, Donut().SubtractingShape(Rect(7, 7, 9, 9)).Failure);
        Assert.Equal(ShapeCutFailure.NoOverlap, Donut().SubtractingShape(Rect(30, 30, 40, 40)).Failure);
    }

    [Fact]
    public void CutAcrossEdgeAndHoleOpensHole()
    {
        var parts = Donut().SubtractingShape(Rect(12, 8, 25, 12)).Pieces!;
        Assert.Single(parts);
        Assert.Null(parts[0].Holes);
        Assert.Equal(300 - 5 * 4, Filled(parts[0]), 6);
        var halves = Donut().SubtractingShape(Rect(8, -5, 12, 25)).Pieces!;
        Assert.Equal(2, halves.Count);
        Assert.All(halves, h => Assert.Null(h.Holes));
        Assert.Equal(300 - 4 * 10, halves.Sum(Filled), 6);
    }

    [Fact]
    public void PiecesKeepTheirOwnHoles()
    {
        var f = Area(Rect(0, 0, 30, 10));
        f.Holes = new() { Rect(2, 2, 6, 6), Rect(22, 2, 26, 6) };
        f.NormalizeHoles();
        var parts = f.SubtractingShape(Rect(13, -5, 17, 15)).Pieces!;
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Single(p.HoleRings));
        foreach (var p in parts)
        {
            double x = p.HoleRings[0].Average(n => n.Point.X);
            Assert.True(p.ContainsPoint(P(x < 15 ? 1 : 29, 1), 0.01));
        }
    }

    [Fact]
    public void ClockwiseAreaWithHoleKeepsDirections()
    {
        var f = Area(Reversed(Rect(0, 0, 20, 20)));
        f.Holes = new() { Rect(5, 5, 15, 15) };
        f.NormalizeHoles();
        var parts = f.SubtractingShape(Rect(1, 1, 3, 3)).Pieces!;
        Assert.Single(parts);
        Assert.Equal(2, parts[0].HoleRings.Count);
        Assert.True(Feature.SignedArea(parts[0].Nodes) < 0);
        Assert.All(parts[0].HoleRings, h => Assert.True(Feature.SignedArea(h) > 0));
    }

    [Fact]
    public void SplitWithHoleGivesHoleToItsPiece()
    {
        var f = Area(Rect(0, 0, 20, 10));
        f.Holes = new() { Rect(2, 2, 6, 6) };
        f.NormalizeHoles();
        var parts = f.SplittingArea(Ring((10, -2), (10, 12))).Pieces!;
        Assert.Equal(2, parts.Count);
        var left = parts.Single(p => p.Nodes.Min(n => n.Point.X) < 1);
        var right = parts.Single(p => p.Nodes.Min(n => n.Point.X) > 9);
        Assert.Single(left.HoleRings);
        Assert.Null(right.Holes);
        Assert.Equal(200 - 16, Filled(left) + Filled(right), 6);
    }

    [Fact]
    public void SplitThroughHoleSplitsIt()
    {
        var parts = Donut().SplittingArea(Ring((10, -2), (10, 22))).Pieces!;
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Null(p.Holes));
        foreach (var p in parts) Assert.Equal(150, Filled(p), 6);
    }

    [Fact]
    public void SplitFromEdgeIntoHoleOnlyIsRejected()
    {
        Assert.Equal(ShapeCutFailure.NotCrossing, Donut().SplittingArea(Ring((10, -2), (10, 8))).Failure);
        Assert.Equal(ShapeCutFailure.NotCrossing, Donut().SplittingArea(Ring((5, 10), (15, 10))).Failure);
        Assert.Equal(ShapeCutFailure.NoOverlap, Donut().SplittingArea(Ring((6, 10), (14, 10))).Failure);
    }

    [Fact]
    public void MergeFormingRingMakesHole()
    {
        var left = Area(Ring((0, 0), (10, 0), (10, 5), (5, 5), (5, 15), (10, 15), (10, 20), (0, 20)));
        var right = Area(Ring((10, 0), (20, 0), (20, 20), (10, 20), (10, 15), (15, 15), (15, 5), (10, 5)));
        var r = left.UnitingArea(right, 0.01, 0.1, allowOverlap: false)!;
        Assert.True(r.FormedHole);
        Assert.False(r.FlattenedCurves);
        Assert.Single(r.Feature.HoleRings);
        Assert.Equal(400 - 100, Filled(r.Feature), 6);
    }

    [Fact]
    public void MergeKeepsHolesOfBothAreas()
    {
        var a = Area(Rect(0, 0, 10, 10));
        a.Holes = new() { Rect(2, 2, 4, 4) };
        a.NormalizeHoles();
        var b = Area(Rect(10, 0, 20, 10));
        b.Holes = new() { Rect(12, 2, 14, 4) };
        b.NormalizeHoles();
        var stitched = a.UnitingArea(b, 0.01, 0.1, allowOverlap: false)!;
        Assert.Equal(2, stitched.Feature.HoleRings.Count);
        Assert.False(stitched.FormedHole);
        Assert.Equal(200 - 8, Filled(stitched.Feature), 6);
        var c = Area(Rect(3, 3, 8, 8));
        var r = a.UnitingArea(c, 0.01, 0.1)!;
        Assert.Single(r.Feature.HoleRings);
        Assert.Equal(100 - 3, Filled(r.Feature), 6);
        var plug = Area(Rect(2, 2, 4, 4));
        var full = a.UnitingArea(plug, 0.01, 0.1)!;
        Assert.Null(full.Feature.Holes);
        Assert.Equal(100, Filled(full.Feature), 6);
    }

    [Fact]
    public void MergeOverlapKeepsCurves()
    {
        var circle = FeatureEdit.Ellipse(new[] { P(5, 15), P(15, 15), P(15, 5), P(5, 5) }, "#000", 0);
        var r = Square().UnitingArea(Area(circle.Nodes), 0.01, 0.1)!;
        Assert.False(r.FlattenedCurves);
        Assert.Contains(r.Feature.Nodes, n => n.IsSmooth);
    }
}

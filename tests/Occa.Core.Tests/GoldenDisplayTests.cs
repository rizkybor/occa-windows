using System.Text.Json;
using System.Text.Json.Nodes;
using Occa.Core.Geometry;
using Occa.Core.Project;
using Occa.Core.Render;
using Occa.Core.Vector;

namespace Occa.Core.Tests;

/// <summary>
/// <c>symbol-display.occa</c> was drawn by CakraCore with OCCA's ISOM symbols; <c>symbol-display.golden.json</c> holds
/// what CakraCore derives for it (colour priorities, paint order, flattened outlines, decoration and pattern marks).
/// The Windows port must derive the same, so the two apps draw the same map (PRD §10 golden files).
/// </summary>
public class GoldenDisplayTests
{
    private const double OriginX = 786_000, OriginY = 9_234_000;
    private static readonly JsonObject Golden = (JsonObject)JsonNode.Parse(Fixtures.Bytes("symbol-display.golden.json"))!;
    private static readonly ProjectDocument Doc = ProjectPackage.Read(ProjectArchive.ReadEntries(Fixtures.Path("symbol-display.occa")));
    private static readonly List<Feature> Resolved = Doc.Features.WithColorPriorities(StandardSymbolSets.LookupFor(Doc.Project.Sheet));

    private static double R(double v) => Math.Round(v * 1e4) / 1e4;

    [Fact]
    public void ReadsTheFixture()
    {
        Assert.Equal("Symbol Display Fixture", Doc.Project.Name);
        Assert.Equal(15000, Doc.Project.Sheet.ScaleDenominator);
        Assert.Equal(14, Doc.Features.Count);
        Assert.Single(Doc.Strokes);
        Assert.Equal(15.0, Doc.Project.Sheet.GroundMeters(1));
        Assert.Equal(Golden["unitsPerMM"]!.GetValue<double>(), Doc.Project.Sheet.GroundMeters(1));
        // Holes, dashes and double lines survive the read.
        Assert.True(Doc.Features[1].HasHoles);
        Assert.NotNull(Doc.Features[5].DashMM);
        Assert.NotNull(Doc.Features[6].CasingColorHex);
        Assert.Equal(1.5, Doc.Features[13].SizeFactor);
        Assert.Equal(45, Doc.Features[13].RotationDeg);
    }

    [Fact]
    public void PaintOrderMatchesMac()
    {
        var expected = Golden["paintOrder"]!.AsArray().Select(p => (p!["index"]!.GetValue<int>(), p["part"]!.GetValue<string>(), p["priority"]!.GetValue<int>())).ToList();
        var actual = ColorHierarchy.PaintOrder(Resolved).Select(p => (p.Index, p.Part.ToString(), p.Priority)).ToList();
        Assert.Equal(expected, actual);
    }

    public static IEnumerable<object[]> FeatureIndices() => Enumerable.Range(0, 14).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(FeatureIndices))]
    public void FeatureDerivationsMatchMac(int i)
    {
        var g = Golden["features"]!.AsArray()[i]!.AsObject();
        var f = Resolved[i];
        Assert.Equal(g["id"]!.GetValue<string>(), f.Id.ToString().ToUpperInvariant());
        Assert.Equal(g["colorPriority"]?.GetValue<int>(), f.ColorPriority);
        Assert.Equal(g["casingPriority"]?.GetValue<int>(), f.CasingPriority);
        Assert.Equal(g["patternCount"]?.GetValue<int>(), f.Patterns?.Count);
        Assert.Equal(g["hasDecoration"]!.GetValue<bool>(), f.Decoration is not null);
        Assert.Equal(g["hidesMainLine"]!.GetValue<bool>(), f.HidesMainLine);
        Assert.Equal(g["isPatternOnly"]!.GetValue<bool>(), f.IsSymbolArea && SymbolOrnaments.IsPatternOnly(f.ColorHex, f.Patterns));
        Assert.Equal(g["effectiveDashMM"]?.AsArray().Select(v => v!.GetValue<double>()).ToList(), f.EffectiveDashMM);
        Assert.Equal(g["ringCount"]!.GetValue<int>(), f.Rings.Count);
        Assert.Equal(g["patternSeed"]!.GetValue<string>(), f.PatternSeed.ToString());

        var pts = SymbolOrnaments.Polyline(f, p => p);
        Assert.Equal(g["flattenedCount"]!.GetValue<int>(), pts.Count);
        Assert.Equal(g["flattenedSumX"]!.GetValue<double>(), R(pts.Sum(p => p.X - OriginX)), 3);
        Assert.Equal(g["flattenedSumY"]!.GetValue<double>(), R(pts.Sum(p => p.Y - OriginY)), 3);

        double u = Doc.Project.Sheet.GroundMeters(1);
        if (f.Kind == FeatureKind.Point)
        {
            Assert.Equal(g["pointRadiusM"]!.GetValue<double>(), R(f.PointRadiusM(Doc.Project.Sheet)), 3);
            var e = f.ScaledPointElements;
            Assert.Equal(g["scaledPointElementCount"]!.GetValue<int>(), e.Count);
            Assert.Equal(g["scaledPointSumX"]!.GetValue<double>(), R(e.SelectMany(x => x.Points).Sum(p => p.X)), 3);
            Assert.Equal(g["scaledPointSumY"]!.GetValue<double>(), R(e.SelectMany(x => x.Points).Sum(p => p.Y)), 3);
        }
        if (f.Kind == FeatureKind.Line && f.Decoration is { } d)
        {
            var marks = SymbolOrnaments.DecorationMarks(d, pts, u, f.WidthMM, f.ColorHex, yUp: true, f.EffectiveDashMM);
            var (sx, sy) = Sum(marks);
            Assert.Equal(g["decorationMarks"]!.GetValue<int>(), marks.Count);
            Assert.Equal(g["decorationSumX"]!.GetValue<double>(), R(sx), 2);
            Assert.Equal(g["decorationSumY"]!.GetValue<double>(), R(sy), 2);
        }
        if (f.Kind == FeatureKind.Area && f.Patterns is { } ps)
        {
            var counts = g["patternMarks"]!.AsArray().Select(v => v!.GetValue<int>()).ToList();
            var sxs = g["patternSumX"]!.AsArray().Select(v => v!.GetValue<double>()).ToList();
            var sys = g["patternSumY"]!.AsArray().Select(v => v!.GetValue<double>()).ToList();
            var tints = g["patternTint"]!.AsArray().Select(v => v!.GetValue<double>()).ToList();
            for (int k = 0; k < ps.Count; k++)
            {
                var marks = SymbolOrnaments.PatternMarks(ps[k], f.Bounds!.Value, u, yUp: true, seed: f.PatternSeed) ?? new List<OrnamentMark>();
                var (sx, sy) = Sum(marks);
                Assert.Equal(counts[k], marks.Count);
                Assert.Equal(sxs[k], R(sx), 2);
                Assert.Equal(sys[k], R(sy), 2);
                Assert.Equal(tints[k], R(SymbolOrnaments.Tint(ps[k])), 3);
            }
        }
    }

    private static (double, double) Sum(IEnumerable<OrnamentMark> marks)
    {
        double sx = 0, sy = 0;
        foreach (var m in marks)
        {
            switch (m)
            {
                case OrnamentMark.Line l: sx += l.A.X + l.B.X - 2 * OriginX; sy += l.A.Y + l.B.Y - 2 * OriginY; break;
                case OrnamentMark.Dot d: sx += d.Center.X - OriginX; sy += d.Center.Y - OriginY; break;
                case OrnamentMark.Polygon p: sx += p.Points.Sum(q => q.X - OriginX); sy += p.Points.Sum(q => q.Y - OriginY); break;
                case OrnamentMark.Polyline pl: sx += pl.Points.Sum(q => q.X - OriginX); sy += pl.Points.Sum(q => q.Y - OriginY); break;
            }
        }
        return (sx, sy);
    }

    [Fact]
    public void SceneHasOneItemPerPaint()
    {
        var scene = MapScene.Build(Doc.Features, Doc.Strokes, Doc.Project.Sheet);
        Assert.Equal(16, scene.Items.Count);
        Assert.Single(scene.Strokes);
        var road = scene.Items.OfType<MapScene.Outline>().Where(o => o.Feature.SymbolCode == "502.0").ToList();
        Assert.Equal(2, road.Count);
        Assert.NotNull(road[0].Casing);
        var marsh = scene.Items.OfType<MapScene.Outline>().Single(o => o.Feature.SymbolCode == "308.0");
        Assert.Null(marsh.Fill);
        Assert.NotNull(marsh.Patterns);
        var bush = scene.Items.OfType<MapScene.PointSymbol>().Where(p => p.Feature.SymbolCode == "418.0").ToList();
        Assert.Equal(2, bush.Count);
        Assert.All(bush, b => Assert.NotNull(b.Level));
    }

    [Fact]
    public void RoundTripsThePackageJson()
    {
        var expected = new Dictionary<string, byte[]>
        {
            ["project.json"] = Fixtures.Bytes("symbol-display/project.json"),
            ["features.json"] = Fixtures.Bytes("symbol-display/features.json"),
            ["strokes.json"] = Fixtures.Bytes("symbol-display/strokes.json"),
        };
        var written = ProjectPackage.Serialize(Doc);
        foreach (var (name, bytes) in expected)
        {
            var a = JsonNode.Parse(bytes);
            var b = JsonNode.Parse(written[name]);
            Assert.True(JsonNode.DeepEquals(a, b), $"{name} differs after a round trip:\n{written[name][..Math.Min(400, written[name].Length)]}");
        }
    }
}

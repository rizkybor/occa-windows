using Occa.Core.Export;
using Occa.Core.Geometry;
using Occa.Core.Import;
using Occa.Core.Sheet;
using Occa.Core.Vector;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

public class OcadReaderTests
{
    [Fact]
    public void ReadsGeoreferenceSymbolsAndBezierLine()
    {
        var map = OcadReader.Read(OcadFixture.Make());
        Assert.Equal(12, map.Version);
        Assert.Equal(10000, map.Scale);
        Assert.Equal(P(680_000, 9_304_000), map.RealWorldOffset);
        Assert.Equal("Contour", map.Symbols[101_000].Name);
        Assert.Equal("101.0", map.Symbols[101_000].Code);
        Assert.Equal(0.21, map.Symbols[101_000].LineWidthMM);
        var (features, report) = map.Features();
        Assert.Equal(1, report.Imported);
        var f = features[0];
        Assert.Equal("101.0 Contour", f.Name);
        Assert.Equal(FeatureKind.Line, f.Kind);
        Assert.Equal("#D15C00", f.ColorHex);
        Assert.Equal(2, f.Nodes.Count);
        Assert.Equal(P(680_000, 9_304_000), f.Nodes[0].Point);
        Assert.Equal(P(680_100, 9_304_000), f.Nodes[0].OutHandle);
        Assert.Equal(P(680_200, 9_304_100), f.Nodes[1].InHandle);
        Assert.Equal(P(680_200, 9_304_200), f.Nodes[1].Point);
    }

    [Fact]
    public void AreaDropsRepeatedClosingPointAndKeepsHoles()
    {
        var ring = new List<(int, int, byte, byte)> { (0, 0, 0, 0), (1000, 0, 0, 0), (1000, 1000, 0, 0), (0, 1000, 0, 0), (0, 0, 0, 0), (200, 200, 0, 2), (400, 200, 0, 0), (400, 400, 0, 0) };
        var map = OcadReader.Read(OcadFixture.Make(objects: new() { (526_000, 3, ring), (999_000, 1, new() { (0, 0, 0, 0) }) }));
        var (features, report) = map.Features();
        Assert.Equal(FeatureKind.Area, features[0].Kind);
        Assert.Equal(4, features[0].Nodes.Count);
        Assert.Single(features[0].HoleRings);
        Assert.Equal(3, features[0].HoleRings[0].Count);
        Assert.Equal(0, report.DroppedHoles);
        Assert.Equal(1, report.SkippedPoints);
    }

    [Fact]
    public void RejectsBadInputWithoutCrashing()
    {
        Assert.Equal(OcadReadErrorKind.NotOcad, Assert.Throws<OcadReadException>(() => OcadReader.Read("bukan ocad"u8.ToArray())).Kind);
        var v8 = OcadFixture.Make();
        v8[4] = 8;
        Assert.Equal(OcadReadErrorKind.UnsupportedVersion, Assert.Throws<OcadReadException>(() => OcadReader.Read(v8)).Kind);
        var full = OcadFixture.Make();
        Assert.Throws<OcadReadException>(() => OcadReader.Read(full[..^40]));
        var noGeo = OcadReader.Read(OcadFixture.Make(scalePar: "\tm4000\tr0"));
        Assert.Equal(OcadReadErrorKind.NotGeoreferenced, Assert.Throws<OcadReadException>(() => noGeo.Features()).Kind);
        for (int n = 0; n < full.Length; n += 997)
        {
            try { OcadReader.Read(full[..n]); } catch (OcadReadException) { }
        }
    }

    [Fact]
    public void SymbolSetFromOcadAndPointSymbols()
    {
        var map = OcadReader.Read(OcadFixture.Make());
        var set = map.ToSymbolSet("Uji", "uji.ocd");
        Assert.Equal(10000, set.Scale);
        var s = set.Symbols[0];
        Assert.Equal("101.0", s.Code);
        Assert.Equal(MapSymbol.Kind.Line, s.SymbolKind);
        Assert.Equal(SymbolCategory.Landform, s.Category);
        Assert.Equal("#D15C00", s.ColorHex);
        Assert.Equal(0.21, s.DrawingWidthMM);
        Assert.True(s.IsEnabled && s.IsDrawable);
        Assert.Equal("503.1", new MapSymbol { Number = 503_001 }.Code);

        var boulder = OcadReader.Read(OcadFixture.Make(objects: new() { (204_000, 1, new() { (500, 500, 0, 0) }) }));
        var elements = boulder.PointElementsOf(boulder.Symbols[204_000]);
        Assert.Single(elements);
        Assert.Equal(PointElement.Kind.Dot, elements[0].ElementKind);
        Assert.Equal(0.6, elements[0].DiameterMM, 9);
        Assert.Equal("#D15C00", elements[0].ColorHex);
        var (features, report) = boulder.Features();
        Assert.Equal(0, report.SkippedPoints);
        Assert.Equal(FeatureKind.Point, features[0].Kind);
        Assert.Equal("204.0", features[0].SymbolCode);
        Assert.Equal(P(680_050, 9_304_050), features[0].Nodes[0].Point);
        // Double line 505: 0.35 between borders + 0.10 borders → 0.45 total, 0.25 inner.
        var road = map.LineStyleOf(map.Symbols[505_000])!;
        Assert.NotNull(road.Casing);
        Assert.Equal(0.45, road.WidthMM, 9);
        Assert.Equal(0.25, road.Casing!.InnerWidthMM, 9);
    }

    [Fact]
    public void IsomSymbolsGoToOmapAndDxf()
    {
        var sheet = new SheetSpec { ScaleDenominator = 10000 };
        var frame = new MapFrame(P(679_150, 9_303_770), sheet.GroundWidthM, sheet.GroundHeightM);
        var (features, _) = OcadReader.Read(OcadFixture.Make()).Features();
        Assert.Equal("101.0", features[0].SymbolCode);
        var req = new VectorExportRequest { BaseName = "uji", Frame = frame, Sheet = sheet, Zone = UtmZone.FromEpsg(32748)!.Value, Features = features };
        Assert.Contains("code=\"101.0\" name=\"Contour\"", OmapWriter.Document(req));
        Assert.Contains("\r\n101_0_CONTOUR\r\n", DxfWriter.Document(req));
    }
}

public class MapFileImportTests
{
    [Fact]
    public void OmapGetsItsZoneFromTheFile()
    {
        var sheet = new SheetSpec { ScaleDenominator = 4000 };
        var zone = UtmZone.FromEpsg(32748)!.Value;
        var frame = new MapFrame(P(788_000, 9_237_000), sheet.GroundWidthM, sheet.GroundHeightM);
        var line = new Feature { Name = "505.0", Kind = FeatureKind.Line, ColorHex = "#000000", WidthMM = 0.25, Nodes = Ring((788_100, 9_237_100), (788_300, 9_237_200)), SymbolCode = "505.0", SymbolName = "Footpath" };
        var xml = OmapWriter.Document(new VectorExportRequest { BaseName = "t", Frame = frame, Sheet = sheet, Zone = zone, Features = new() { line } });
        var content = MapFileImport.Read(System.Text.Encoding.UTF8.GetBytes(xml), "omap");
        Assert.Equal(zone, content.KnownZone);
        Assert.Equal(4000, content.Scale);
        Assert.Single(content.Features);
        Assert.Equal(788_100, content.Features[0].Nodes[0].Point.X, 2);
        Assert.Equal("505.0", content.Features[0].SymbolCode);
        var doc = MapFileImport.Document("Uji", content, zone);
        Assert.Equal(32748, doc.Project.CrsEpsg);
        Assert.True(doc.Project.AreaLocked);
        Assert.NotNull(doc.Project.Frame);
    }

    [Fact]
    public void OcadBecomesAProjectCentredOnTheContent()
    {
        var content = MapFileImport.Read(OcadFixture.Make(), "ocd");
        Assert.Equal(MapFileContent.SourceKind.Ocad, content.Source);
        Assert.Null(content.KnownZone);
        Assert.Equal(10000, content.Scale);
        var zone = UtmZone.Create(50, false)!.Value;
        var doc = MapFileImport.Document("Ampenan", content, zone);
        Assert.Equal(32750, doc.Project.CrsEpsg);
        Assert.Equal(10000, doc.Project.Sheet.ScaleDenominator);
        var f = doc.Project.Frame!.Value.Extent;
        var e = content.Extent!.Value;
        Assert.InRange(f.Center.X, e.Center.X - 10, e.Center.X + 10);
        Assert.InRange(f.Center.Y, e.Center.Y - 10, e.Center.Y + 10);
    }

    [Fact]
    public void RejectsEmptyAndUnknownFiles()
    {
        Assert.Equal(MapImportException.Kind.UnsupportedExtension, Assert.Throws<MapImportException>(() => MapFileImport.Read("x"u8.ToArray(), "pdf")).Reason);
        Assert.Equal(5000, MapFileImport.SheetScale(5000));
        Assert.Equal(5000, MapFileImport.SheetScale(4800));
        Assert.Equal(15000, MapFileImport.SheetScale(25000));
    }

    [Fact]
    public void CandidateZonesTryNeighboursFirstThenOtherHemisphere()
    {
        var z = UtmZone.Create(48, false)!.Value;
        var c = MapFileImport.CandidateZones(z, 2);
        Assert.Equal(new[] { "UTM 49S", "UTM 47S", "UTM 50S", "UTM 46S", "UTM 48N", "UTM 49N", "UTM 47N", "UTM 50N", "UTM 46N" }, c.Select(x => x.Label));
        Assert.DoesNotContain(z, c);
        Assert.Equal(new[] { "UTM 1N", "UTM 59N" }, MapFileImport.CandidateZones(UtmZone.Create(60, true)!.Value, 1).Take(2).Select(x => x.Label));
    }
}

public class OmapSymbolSetTests
{
    private static SymbolSet Load(string file, bool course = false)
        => OmapSymbolSet.Read(Fixtures.Bytes("oom/" + file), file, file, course ? n => n is >= 700 and <= 799 : null);

    private static MapSymbol Sym(SymbolSet set, string code) => set.Symbols.Single(s => s.Code == code);

    [Fact]
    public void Codes()
    {
        Assert.Equal(101_000, OmapSymbolSet.Number("101"));
        Assert.Equal(501_001, OmapSymbolSet.Number("501.1"));
        Assert.Equal(512_010, OmapSymbolSet.Number("512.10"));
        Assert.Null(OmapSymbolSet.Number("abc"));
    }

    [Fact]
    public void Isom10000()
    {
        var set = Load("ISOM 2017-2_10000.omap");
        Assert.Equal(10000, set.Scale);
        Assert.Equal(MapStandard.Isom, set.Standard());
        Assert.True(set.Symbols.Count(s => s.IsDrawable) > 150);
        Assert.Equal(0.21, Sym(set, "101.0").LineStyle!.WidthMM, 3);
        Assert.Equal(0.375, Sym(set, "102.0").LineStyle!.WidthMM, 3);
        var form = Sym(set, "103.0").LineStyle!;
        Assert.Equal(0.15, form.WidthMM, 3);
        Assert.Equal(new List<double> { 3.0, 0.3 }, form.DashMM);
        var road = Sym(set, "502.0").LineStyle!;
        Assert.Equal("#000000", road.Casing!.ColorHex);
        Assert.Equal(0.45, road.Casing.InnerWidthMM, 3);
        Assert.Equal(0.87, road.WidthMM, 3);
        Assert.Equal(new List<double> { 3.0, 0.375 }, Sym(set, "505.0").LineStyle!.DashMM);
        Assert.Equal(MapSymbol.Kind.Area, Sym(set, "521.0").SymbolKind);
        var tree = Sym(set, "417.0");
        Assert.Equal(MapSymbol.Kind.Point, tree.SymbolKind);
        Assert.Contains(tree.PointElements!, e => e.ElementKind == PointElement.Kind.Circle && e.ColorHex == tree.ColorHex);
        Assert.True(Sym(set, "201.0").ColorPriority < Sym(set, "101.0").ColorPriority);
        Assert.True(Sym(set, "101.0").ColorPriority < Sym(set, "401.0").ColorPriority);
        var marsh = Sym(set, "308.0");
        Assert.True(marsh.IsEnabled);
        Assert.Equal("#FFFFFF", marsh.ColorHex);
        var ml = marsh.Patterns![0];
        Assert.Equal(AreaPattern.Kind.Lines, ml.PatternKind);
        Assert.Equal(0.45, ml.SpacingMM, 3);
        Assert.Equal(0.15, ml.SizeMM, 3);
        Assert.Equal(90, ml.AngleDeg, 3);
        Assert.Equal(0, Sym(set, "407.0").Patterns![0].AngleDeg, 3);
        var trees = Sym(set, "402.0").Patterns![0];
        Assert.Equal(AreaPattern.Kind.Holes, trees.PatternKind);
        Assert.Equal(0.6, trees.SizeMM, 3);
        Assert.Equal(1.05, trees.SpacingMM, 3);
        var cult = Sym(set, "412.0");
        Assert.NotEqual("#FFFFFF", cult.ColorHex);
        Assert.Equal(AreaPattern.Kind.Dots, cult.Patterns![0].PatternKind);
    }

    [Fact]
    public void Issprom4000()
    {
        var set = Load("ISSprOM 2019_4000.omap");
        Assert.Equal(4000, set.Scale);
        Assert.Equal(MapStandard.Issprom, set.Standard());
        Assert.Equal(0.21, Sym(set, "101.0").LineStyle!.WidthMM, 3);
        Assert.Equal(0.30, Sym(set, "102.0").LineStyle!.WidthMM, 3);
        Assert.Equal(new List<double> { 3.0, 0.3 }, Sym(set, "103.0").LineStyle!.DashMM);
        var rail = Sym(set, "509.1").LineStyle!;
        Assert.Equal("#000000", rail.Casing!.ColorHex);
        Assert.NotNull(rail.InnerDashMM);
        Assert.Equal("#666666", Sym(set, "521.0").ColorHex);
    }

    [Fact]
    public void CourseDesignAndScaling()
    {
        var set = Load("Course_Design_4000.omap", course: true);
        Assert.True(set.IsCourseDesign());
        var finish = Sym(set, "706.0").PointElements!.Where(e => e.ElementKind == PointElement.Kind.Circle).Select(e => e.DiameterMM).OrderBy(d => d).ToList();
        Assert.Equal(2, finish.Count);
        Assert.Equal(5.0, finish[0], 3);
        Assert.Equal(7.0, finish[1], 3);
        Assert.Equal(6.0, Sym(set, "702.0").PointElements![0].DiameterMM, 3);

        var isom = Load("ISOM 2017-2_10000.omap");
        double k = isom.SizeFactor(15000);
        Assert.Equal(1 / 1.5, k, 3);
        var baseSet = isom.ScaledBy(k);
        Assert.Equal(0.14, Sym(baseSet, "101.0").LineStyle!.WidthMM, 3);
        Assert.Equal(0.3, Sym(baseSet, "502.0").LineStyle!.Casing!.InnerWidthMM, 3);
        Assert.Equal(1, isom.SizeFactor(10000), 3);
        Assert.Equal(1, isom.SizeFactor(4000), 3);
    }

    [Fact]
    public void SelfReferencingCombinedSymbolDoesNotRecurse()
    {
        const string xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <map xmlns="http://openorienteering.org/apps/mapper/xml/v2" version="9"><georeferencing scale="4000"/>
        <colors count="1"><color priority="0" name="Black"><rgb r="0" g="0" b="0"/></color></colors>
        <barrier version="6"><symbols count="2">
        <symbol type="16" id="0" code="900" name="Loop"><combined_symbol parts="1"><part symbol="0"/></combined_symbol></symbol>
        <symbol type="2" id="1" code="901" name="Line"><line_symbol color="0" line_width="300"/></symbol>
        </symbols></barrier></map>
        """;
        var set = OmapSymbolSet.Read(System.Text.Encoding.UTF8.GetBytes(xml), "x", "x.omap");
        Assert.Equal(new[] { "901.0" }, set.Symbols.Select(s => s.Code));
        Assert.Throws<OmapSymbolSet.ReadException>(() => OmapSymbolSet.Read("<html/>"u8.ToArray(), "x", "x"));
    }
}

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Occa.Core.Export;
using Occa.Core.Geometry;
using Occa.Core.Import;
using Occa.Core.Project;
using Occa.Core.Sheet;
using Occa.Core.Vector;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

public class VectorExportTests
{
    private static readonly SheetSpec Sheet = new() { ScaleDenominator = 4000 };
    private static readonly MapFrame Frame = new(P(788_100, 9_236_860), Sheet.GroundWidthM, Sheet.GroundHeightM);
    private static readonly UtmZone Zone = UtmZone.FromEpsg(32748)!.Value;

    private static Stroke S(string color, double w, params (double, double)[] pts) => new() { ColorHex = color, WidthMM = w, Points = pts.Select(p => P(p.Item1, p.Item2)).ToList() };

    private static List<Stroke> Strokes() => new()
    {
        S("#D15C00", 0.14, (788_200.1234, 9_237_000.5678), (788_250.5, 9_237_100.25), (788_301, 9_237_050)),
        S("#000000", 0.35, (788_500, 9_237_500), (788_600, 9_237_520)),
        S("#D15C00", 0.14, (788_700, 9_237_900), (788_710, 9_237_910)),
        S("#00A0E6", 0.5, (788_400, 9_237_300)),
        S("#000000", 0.14, (700_000, 9_000_000), (700_010, 9_000_010)),
    };

    private static VectorExportRequest Request() => new() { BaseName = "uji", Frame = Frame, Sheet = Sheet, Zone = Zone, Strokes = Strokes() };

    [Fact]
    public void SymbolTableGroupsByColorWidthAndKind()
    {
        var t = new SketchSymbolTable(Request().ExportedStrokes);
        Assert.Equal(new[] { "990.1", "990.2", "990.3" }, t.Symbols.Select(s => s.Code));
        Assert.Equal("Sketch Brown 0.14 mm", t.Symbols[0].Name);
        Assert.Equal("990_1_SKETCH_BROWN_014", t.Symbols[0].DxfLayer);
        Assert.True(t.Symbols[2].IsPoint);
    }

    [Fact]
    public void OmapIsWellFormedAndRoundTripsCoordinates()
    {
        var xml = OmapWriter.Document(Request());
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://openorienteering.org/apps/mapper/xml/v2";
        Assert.Equal("4000", doc.Root!.Element(ns + "georeferencing")!.Attribute("scale")!.Value);
        var symbols = doc.Descendants(ns + "symbols").First();
        Assert.Equal("3", symbols.Attribute("count")!.Value);
        var objects = doc.Descendants(ns + "object").ToList();
        Assert.Equal(4, objects.Count);
        Assert.Equal(new[] { "0", "1", "0", "2" }, objects.Select(o => o.Attribute("symbol")!.Value));
        var refPoint = doc.Descendants(ns + "projected_crs").First().Element(ns + "ref_point")!;
        var reference = P(double.Parse(refPoint.Attribute("x")!.Value, System.Globalization.CultureInfo.InvariantCulture), double.Parse(refPoint.Attribute("y")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        double f = 4000 / 1_000_000.0;
        var expected = Request().ExportedStrokes;
        for (int i = 0; i < objects.Count; i++)
        {
            var coords = objects[i].Element(ns + "coords")!.Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Split(' ').Select(int.Parse).ToArray()).ToList();
            Assert.Equal(expected[i].Points.Count, coords.Count);
            for (int k = 0; k < coords.Count; k++)
            {
                var back = P(reference.X + coords[k][0] * f, reference.Y - coords[k][1] * f);
                Assert.True(back.DistanceTo(expected[i].Points[k]) <= 0.5e-6 * 4000 * 1.5);
            }
        }
        Assert.Contains("+proj=utm +zone=48 +south +datum=WGS84", xml);
        // Our own reader reads it back with the same zone.
        var content = MapFileImport.Read(Encoding.UTF8.GetBytes(xml), "omap");
        Assert.Equal(Zone, content.KnownZone);
        Assert.Equal(3, content.Features.Count);   // the point stroke is skipped (reported), 3 lines
        Assert.Equal(1, content.Report.SkippedPoints);
    }

    [Fact]
    public void DxfKeepsUtmMetresPerLayer()
    {
        var dxf = DxfWriter.Document(Request());
        var lines = dxf.Split("\r\n");
        Assert.Equal("", lines[^1]);
        var pairs = new List<(string, string)>();
        for (int i = 0; i + 1 < lines.Length - 1; i += 2) pairs.Add((lines[i].Trim(), lines[i + 1]));
        Assert.Equal("SECTION", pairs[0].Item2);
        Assert.Equal("EOF", pairs[^1].Item2);
        var vertices = new Dictionary<string, List<Point2D>>();
        var points = new Dictionary<string, List<Point2D>>();
        int j = 0;
        while (j < pairs.Count)
        {
            if (pairs[j] == ("0", "VERTEX") || pairs[j] == ("0", "POINT"))
            {
                bool isVertex = pairs[j].Item2 == "VERTEX";
                string layer = ""; double x = 0, y = 0;
                j++;
                while (j < pairs.Count && pairs[j].Item1 != "0")
                {
                    switch (pairs[j].Item1)
                    {
                        case "8": layer = pairs[j].Item2; break;
                        case "10": x = double.Parse(pairs[j].Item2, System.Globalization.CultureInfo.InvariantCulture); break;
                        case "20": y = double.Parse(pairs[j].Item2, System.Globalization.CultureInfo.InvariantCulture); break;
                    }
                    j++;
                }
                var dict = isVertex ? vertices : points;
                if (!dict.ContainsKey(layer)) dict[layer] = new();
                dict[layer].Add(P(x, y));
            }
            else j++;
        }
        Assert.Equal(Frame.Corners, vertices["FRAME"]);
        var brown = vertices["990_1_SKETCH_BROWN_014"];
        Assert.Equal(5, brown.Count);
        Assert.Equal(788_200.1234, brown[0].X, 9);
        Assert.Equal(9_237_000.5678, brown[0].Y, 9);
        Assert.Equal(2, vertices["990_2_SKETCH_BLACK_035"].Count);
        Assert.Equal(new[] { P(788_400, 9_237_300) }, points["990_3_SKETCH_BLUE_050_POINT"]);
        Assert.DoesNotContain(vertices.Keys, k => k.Contains("700"));
    }

    [Fact]
    public void ExportWritesSelectedFilesAndSidecar()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = VectorExporter.Export(Request(), new HashSet<VectorFormat> { VectorFormat.Omap, VectorFormat.Dxf }, dir);
            Assert.Equal(new[] { "uji.omap", "uji.dxf" }, paths.Select(Path.GetFileName));
            var bg = Background();
            var req = new VectorExportRequest { BaseName = "bg", Frame = Frame, Sheet = Sheet, Zone = Zone, Background = bg };
            var all = VectorExporter.Export(req, new HashSet<VectorFormat> { VectorFormat.Omap, VectorFormat.Dxf }, dir);
            Assert.Equal(new HashSet<string> { "bg.omap", "bg.dxf", "bg_basemap.jpg", "bg_basemap.jgw", "bg_basemap.prj" }, all.Select(Path.GetFileName).ToHashSet());
            var xml = OmapWriter.Document(req);
            Assert.Contains("<template type=\"TemplateImage\"", xml);
            Assert.Contains("path=\"bg_basemap.jpg\"", xml);
            Assert.Contains("first_front_template=\"1\"", xml);
            XDocument.Parse(xml);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    internal static ExportBackground Background(double opacity = 1)
    {
        var e = Frame.Extent;
        return new ExportBackground
        {
            ImageData = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, PixelWidth = 40, PixelHeight = 60, Opacity = opacity, Attribution = "Imagery © Esri", FileExtension = "jpg",
            Corners = new[] { P(e.MinX - 10, e.MaxY + 10), P(e.MaxX + 10, e.MaxY + 10), P(e.MaxX + 10, e.MinY - 10), P(e.MinX - 10, e.MinY - 10) },
        };
    }

    [Fact]
    public void BackgroundWorldFileMapsPixelCentresToCorners()
    {
        var bg = Background();
        var wf = bg.WorldFile;
        var tl = wf.World(-0.5, -0.5); var br = wf.World(39.5, 59.5);
        Assert.Equal(bg.Corners[0].X, tl.X, 6);
        Assert.Equal(bg.Corners[0].Y, tl.Y, 6);
        Assert.Equal(bg.Corners[2].X, br.X, 6);
        Assert.Equal(bg.Corners[2].Y, br.Y, 6);
        var text = WorldFile.For(Frame, 0.3387).Text;
        var parsed = WorldFile.Parse(text)!.Value;
        Assert.Equal(0.3387, parsed.PixelSizeX, 9);
        Assert.Equal(-0.3387, parsed.PixelSizeY, 9);
        Assert.Equal(Frame.Extent.MinX + 0.3387 / 2, parsed.UpperLeftX, 9);
        Assert.Null(WorldFile.Parse("1\n2\n3"));
    }

    [Fact]
    public void FeatureCoordsCurveAndCloseFlags()
    {
        var curve = new Feature { Kind = FeatureKind.Line, ColorHex = "#000000", WidthMM = 0.25, Nodes = new List<BezierNode> { new(P(788_300, 9_237_200), null, P(788_300, 9_237_250)), new(P(788_400, 9_237_200), P(788_400, 9_237_150)) } };
        var reference = P(788_350, 9_237_200);
        Assert.Equal(new[] { "-12500 0 1;", "-12500 -12500;", "12500 12500;", "12500 0;" }, OmapWriter.FeatureCoords(curve, reference, 4000));
        var area = curve.Clone();
        area.Kind = FeatureKind.Area;
        var closed = OmapWriter.FeatureCoords(area, reference, 4000);
        Assert.Equal("-12500 0 2;", closed[^1]);
        Assert.Equal(5, closed.Count);
        // Hole flags: outer ring ends with close + hole, last part with close only.
        var donut = Donut();
        var coords = OmapWriter.FeatureCoords(donut, P(10, 10), 4000);
        Assert.Equal(10, coords.Count);
        Assert.EndsWith(" 18;", coords[4]);
        Assert.EndsWith(" 2;", coords[9]);
    }
}

public class GeoPdfTests
{
    private static GeoPdfRequest Request(double margin = 5, ExportBackground? bg = null)
    {
        var sheet = new SheetSpec { MarginMM = margin, ScaleDenominator = 4000 };
        var frame = new MapFrame(P(788_100, 9_236_860), sheet.GroundWidthM, sheet.GroundHeightM);
        var curve = new Feature { Kind = FeatureKind.Line, ColorHex = "#000000", WidthMM = 0.25, Nodes = new List<BezierNode> { new(P(788_300, 9_237_200), null, P(788_300, 9_237_250)), new(P(788_400, 9_237_200), P(788_400, 9_237_150)) } };
        var area = FeatureEdit.Rectangle(new[] { P(788_500, 9_237_600), P(788_600, 9_237_600), P(788_600, 9_237_500), P(788_500, 9_237_500) }, "#3EB54A", 0.14);
        return new GeoPdfRequest
        {
            Title = "Uji (PDF)", Frame = frame, Sheet = sheet, Zone = UtmZone.FromEpsg(32748)!.Value,
            Strokes = new() { new Stroke { ColorHex = "#D15C00", WidthMM = 0.14, Points = new() { P(788_200, 9_237_000), P(788_250, 9_237_100) } } },
            Features = new() { curve, area }, Grid = new NorthGrid { IsEnabled = true, SpacingM = 250 }, Background = bg,
        };
    }

    [Fact]
    public void OpensAsA4WithGeospatialViewport()
    {
        var req = Request();
        var text = Encoding.Latin1.GetString(GeoPdfExporter.Document(req));
        Assert.StartsWith("%PDF-1.7\n", text);
        Assert.EndsWith("%%EOF\n", text);
        var media = Regex.Match(text, @"/MediaBox \[0 0 ([\d.]+) ([\d.]+)\]");
        Assert.Equal(210 / 25.4 * 72, double.Parse(media.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 2);
        Assert.Equal(297 / 25.4 * 72, double.Parse(media.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 2);
        Assert.Contains("/VP [<< /Type /Viewport /BBox [", text);
        Assert.Contains("/Subtype /GEO", text);
        var gpts = Regex.Match(text, @"/GPTS \[([^\]]+)\]").Groups[1].Value.Split(' ').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(8, gpts.Count);
        var ll = req.Zone.Unproject(req.Frame.Corners[3]);
        Assert.Equal(ll.Lat, gpts[0], 8);
        Assert.Equal(ll.Lon, gpts[1], 8);
        var bbox = Regex.Match(text, @"/BBox \[([\d.]+) ").Groups[1].Value;
        Assert.Equal(5 / 25.4 * 72, double.Parse(bbox, System.Globalization.CultureInfo.InvariantCulture), 2);
        // xref offsets point at "N 0 obj".
        var startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n").Groups[1].Value);
        Assert.StartsWith("xref", text[startxref..]);
        var firstOffset = int.Parse(Regex.Match(text, @"xref\n0 \d+\n0000000000 65535 f \n(\d{10})").Groups[1].Value);
        Assert.StartsWith("1 0 obj", text[firstOffset..]);
    }

    [Fact]
    public void ContentHasVectorCurvesGridAndCmyk()
    {
        var text = Encoding.Latin1.GetString(GeoPdfExporter.Document(Request()));
        Assert.Contains(" c\n", text);
        Assert.Contains("/GSGrid gs", text);
        Assert.Contains("WGS_1984_UTM_Zone_48S", text);
        Assert.Contains("Uji \\(PDF\\)", text);
        // Sketch colours stay RGB (RG / rg); map symbols use DeviceCMYK (K / k).
        Assert.Contains("0.82 0.361 0 RG", text);
        var symbolReq = Request();
        symbolReq.Features[0].SymbolCode = "505.0";
        var withSymbol = Encoding.Latin1.GetString(GeoPdfExporter.Document(symbolReq));
        Assert.Contains("0 0 0 1 K", withSymbol);
        Assert.False(Encoding.Latin1.GetString(GeoPdfExporter.Document(Request(margin: 0))).Contains("BT /F1"));
    }

    [Fact]
    public void EmbedsBackgroundWithOpacityAndCredit()
    {
        var text = Encoding.Latin1.GetString(GeoPdfExporter.Document(Request(bg: VectorExportTests.Background(0.6))));
        Assert.Contains("/ImBg Do", text);
        Assert.Contains("/GSBg << /Type /ExtGState /ca 0.6", text);
        Assert.Contains("/Filter /DCTDecode", text);
        Assert.Contains("Esri) Tj", text);
        Assert.DoesNotContain("/ImBg", Encoding.Latin1.GetString(GeoPdfExporter.Document(Request())));
    }

    [Fact]
    public void NumbersMatchTheMacFormatter()
    {
        Assert.Equal("0", GeoPdfExporter.N(0));
        Assert.Equal("0.5", GeoPdfExporter.N(0.5));
        Assert.Equal("12.345", GeoPdfExporter.N(12.345));
        Assert.Equal("-3.1", GeoPdfExporter.N(-3.1));
        Assert.Equal("100", GeoPdfExporter.N(99.9996));
        Assert.Equal("0.12", GeoPdfExporter.N(0.125, 2));
        Assert.Equal("-6.175400123", GeoPdfExporter.N(-6.175400123, 9));
        Assert.Equal("Caf\\351 \\(x\\) ?", GeoPdfExporter.PdfString("Café (x) ✓"));
    }

    [Fact]
    public void SymbolDisplayFixtureExportsEverySymbolKind()
    {
        var doc = ProjectPackage.Read(ProjectArchive.ReadEntries(Fixtures.Path("symbol-display.occa")));
        var frame = doc.Project.Frame!.Value;
        var features = doc.Features.WithColorPriorities(StandardSymbolSets.LookupFor(doc.Project.Sheet));
        var req = new GeoPdfRequest { Title = "Fixture", Frame = frame, Sheet = doc.Project.Sheet, Zone = doc.Project.Zone!.Value, Strokes = doc.Strokes, Features = features };
        var text = Encoding.Latin1.GetString(GeoPdfExporter.Document(req));
        Assert.Contains("f*\n", text);                                   // area fill, even-odd
        Assert.Contains("W* n\n", text);                                 // pattern clip
        Assert.Contains("] 0 d", text);                                  // dashed footpath
        Assert.Contains("0.25 0.75 1 0 K", text);                        // brown contour in IOF CMYK
        Assert.True(text.Length > 20_000);
    }
}

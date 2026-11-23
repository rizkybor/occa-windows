using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Occa.Core.Courses;
using Occa.Core.Export;
using Occa.Core.Geometry;
using Occa.Core.Json;
using Occa.Core.Project;
using Occa.Core.Sheet;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

/// <summary>Port of CakraCore's CoursePlannerTests (+ its extensions): lengths, loads, codes, overprint, descriptions, IOF XML, PDF.</summary>
public class CoursePlannerTests
{
    private static readonly UtmZone Zone = UtmZone.FromEpsg(32748)!.Value;

    /// <summary>Start (0,0) → 31 (300,0) → 32 (300,400) → Finish (0,400). Courses A and B share control 31.</summary>
    internal static (CourseSet Set, Course A, Course B) Sample()
    {
        var s = new ControlPoint(ControlKind.Start, P(0, 0));
        var c31 = new ControlPoint(ControlKind.Control, P(300, 0), 31, new ControlDescription(feature: "2.4", dimension: "1.5", location: "12.1E"));
        var c32 = new ControlPoint(ControlKind.Control, P(300, 400), 32);
        var f = new ControlPoint(ControlKind.Finish, P(0, 400));
        var a = new Course("A", new[] { s.Id, c31.Id, c32.Id, f.Id }, competitors: 40, climbM: 25);
        var b = new Course("B", new[] { s.Id, c31.Id, f.Id }, competitors: 15);
        return (new CourseSet("Uji Lintasan", new[] { s, c31, c32, f }, new[] { a, b }), a, b);
    }

    private static List<double> Circles(CourseOverprint o) => o.Marks.OfType<CourseMark.Circle>().Select(c => c.RadiusM).OrderBy(r => r).ToList();
    private static List<IReadOnlyList<Point2D>> Legs(CourseOverprint o) => o.Marks.OfType<CourseMark.Polyline>().Where(p => !p.Closed).Select(p => p.Points).ToList();
    private static HashSet<string> Texts(CourseOverprint o) => o.Marks.OfType<CourseMark.Text>().Select(t => t.Value).ToHashSet();

    private static string Pdf(GeoPdfRequest req) => Encoding.Latin1.GetString(GeoPdfExporter.Document(req));

    [Fact]
    public void LengthLegsSequenceAndLoad()
    {
        var (set, a, b) = Sample();
        Assert.Equal(new[] { 300.0, 400, 300 }, set.LegLengths(a));
        Assert.Equal(1000, set.Length(a), 9);
        Assert.Equal(800, set.Length(b), 9);   // 300 + 500
        var c31 = set.Controls[1].Id; var c32 = set.Controls[2].Id;
        Assert.Equal(2, set.SequenceNumbers(a)[c32]);
        var loads = set.ControlLoads().ToDictionary(l => l.ControlId);
        Assert.Equal(55, loads[c31].Visits);
        Assert.Equal(2, loads[c31].CourseCount);
        Assert.Equal(40, loads[c32].Visits);
        var shared = set.SharedLegLoads();
        Assert.Single(shared);
        Assert.Equal(55, shared[0].Visits);
        Assert.Empty(set.Warnings());
    }

    [Fact]
    public void AutomaticCodesSkipAmbiguous()
    {
        Assert.True(CourseSet.IsAmbiguous(66));
        Assert.True(CourseSet.IsAmbiguous(68));
        Assert.False(CourseSet.IsAmbiguous(61));
        Assert.False(CourseSet.IsAmbiguous(88));
        Assert.False(CourseSet.IsAmbiguous(31));
        var set = new CourseSet(firstCode: 65) { Controls = { new ControlPoint(ControlKind.Control, P(0, 0), 65) } };
        Assert.Equal(67, set.NextFreeCode());
        set.AvoidAmbiguousCodes = false;
        Assert.Equal(66, set.NextFreeCode());
    }

    [Fact]
    public void WarningsForMissingFinishAndCloseControls()
    {
        var (set, _, _) = Sample();
        set.Controls.Add(new ControlPoint(ControlKind.Control, P(310, 10), 33));
        set.Courses[0].ControlIds.RemoveAt(set.Courses[0].ControlIds.Count - 1);
        var w = set.Warnings();
        Assert.Contains(w, s => s.Contains("A: no finish yet"));
        Assert.Contains(w, s => s.Contains("31 and 33"));
    }

    [Fact]
    public void OverprintGeometry()
    {
        var (set, a, _) = Sample();
        var sheet = new SheetSpec { ScaleDenominator = 10000 };   // ISOM enlarged to 150 %: 7.5 mm circle = 75 m
        var o = CourseOverprint.Make(set, a, sheet);
        Assert.Equal(5.25, o.LineWidthM, 9);
        Assert.Equal(new[] { 30.0, 37.5, 37.5, 45 }, Circles(o));
        // Start triangle points to the first control (east).
        var tri = o.Marks.OfType<CourseMark.Polyline>().First(p => p.Closed).Points;
        Assert.Equal(0, tri[0].Y, 6);
        Assert.True(tri[0].X > 0);
        // First leg stops at the edges of the triangle and circle 31.
        var legs = Legs(o);
        Assert.Equal(3, legs.Count);
        Assert.Equal(300 - 37.5 - 4.5, legs[0][1].X, 6);
        Assert.Equal(new HashSet<string> { "1", "2" }, Texts(o));
        // All controls: label = code, no legs.
        var all = CourseOverprint.Make(set, null, sheet);
        Assert.Equal(new HashSet<string> { "31", "32" }, Texts(all));
        Assert.Empty(Legs(all));
        Assert.Equal(6, CourseStyle.Standard(4000).ControlDiameterMM);
    }

    [Fact]
    public void DescriptionText()
    {
        var d = new ControlDescription(which: "0.1N", feature: "2.4", dimension: "1.5", location: "12.1E");
        Assert.Equal("Northern boulder, 1.5 m, east side", d.Text);
        var (set, a, _) = Sample();
        var sheet = new DescriptionSheet(set, a);
        Assert.Equal("1.0 km", sheet.LengthText);
        Assert.Equal(new[] { ControlKind.Start, ControlKind.Control, ControlKind.Control, ControlKind.Finish }, sheet.Rows.Select(r => r.Kind));
        Assert.Equal("31", sheet.Rows[1].Code);
        Assert.Equal("Navigate 300 m to finish", sheet.Rows[^1].Text);
    }

    [Fact]
    public void IofXmlCourseData()
    {
        var (set, _, _) = Sample();
        var frame = new MapFrame(P(-100, -100), 840, 1188);
        var data = IofXmlExporter.CourseData(set, frame, new SheetSpec { ScaleDenominator = 4000 }, Zone);
        var xml = Encoding.UTF8.GetString(data);
        XDocument.Parse(xml);
        Assert.Contains("iofVersion=\"3.0\"", xml);
        Assert.Contains("<Control type=\"Start\">\n      <Id>S1</Id>", xml);
        Assert.Contains("<Id>31</Id>", xml);
        Assert.Contains("<Length>1000</Length>", xml);
        Assert.Contains("<Climb>25</Climb>", xml);
        Assert.Contains("<LegLength>400</LegLength>", xml);
        Assert.Contains("<NumberOfCompetitors>40</NumberOfCompetitors>", xml);
        // MapPosition 31: (300+100) m × 0.25 mm/m = 100 mm.
        Assert.Contains("<MapPosition x=\"100\" y=\"25\" unit=\"mm\"/>", xml);
    }

    [Fact]
    public void PackageRoundTripAndPdfPages()
    {
        var (set, a, _) = Sample();
        var dir = Path.Combine(Path.GetTempPath(), $"course-{Guid.NewGuid()}.cakra");
        try
        {
            ProjectPackage.Write(new ProjectDocument { Project = new Core.Project.Project { Name = "Uji" }, Courses = set }, dir);
            Assert.True(File.Exists(Path.Combine(dir, ProjectPackage.CoursesFile)));
            var back = ProjectPackage.Read(dir).Courses;
            Assert.Equal(OccaJson.SerializeSorted(set), OccaJson.SerializeSorted(back));
            Assert.Equal(2024, back.DescriptionFormat);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        var sheet = new SheetSpec { ScaleDenominator = 4000 };
        var frame = new MapFrame(P(-100, -100), sheet.GroundWidthM, sheet.GroundHeightM);
        var pdf = Pdf(new GeoPdfRequest
        {
            Title = "Uji", Frame = frame, Sheet = sheet, Zone = Zone,
            Overprint = CourseOverprint.Make(set, a, sheet), Descriptions = new DescriptionSheet(set, a),
        });
        Assert.Contains("/Count 2", pdf);
        Assert.Contains("(1.5) Tj", pdf);
        Assert.Contains("0.35 0.85 0 0 K", pdf);
        Assert.DoesNotContain("/Multiply", pdf);
        Assert.StartsWith("%PDF-1.7", pdf);
        Assert.EndsWith("%%EOF\n", pdf);
    }

    [Fact]
    public void EmptyCourseSetWritesNoCoursesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"course-{Guid.NewGuid()}.cakra");
        try
        {
            ProjectPackage.Write(new ProjectDocument { Project = new Core.Project.Project { Name = "Uji" } }, dir);
            Assert.False(File.Exists(Path.Combine(dir, ProjectPackage.CoursesFile)));
            Assert.True(ProjectPackage.Read(dir).Courses.IsEmpty);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void RenumberFollowsCourseOrder()
    {
        var (set, _, _) = Sample();
        var spare = new ControlPoint(ControlKind.Control, P(900, 900), 90);
        set.Controls.Insert(0, spare);
        set.Controls[2].Code = 77;   // 31 → 77
        set.FirstCode = 65;
        var r = set.Renumbered();
        var codes = r.Controls.Where(c => c.Code is not null).ToDictionary(c => c.Id, c => c.Code!.Value);
        Assert.Equal(65, codes[set.Controls[2].Id]);
        Assert.Equal(67, codes[set.Controls[3].Id]);
        Assert.Equal(69, codes[spare.Id]);
        Assert.Null(r.Controls.First(c => c.Kind == ControlKind.Start).Code);
        Assert.Equal(77, set.Controls[2].Code);   // the original set is untouched
    }

    [Fact]
    public void ControlSizeGlobalAndPerControl()
    {
        var (set, a, _) = Sample();
        var sheet = new SheetSpec { ScaleDenominator = 10000 };   // ISOM 5 mm × 150 % = 75 m → r 37.5 m
        List<double> Radii(CourseSet s) => Circles(CourseOverprint.Make(s, a, sheet));
        Assert.Equal(new[] { 30.0, 37.5, 37.5, 45 }, Radii(set));
        set.Controls[1].SizeFactor = 1.5;   // control 31
        Assert.Equal(new[] { 30.0, 37.5, 45, 56.25 }, Radii(set));
        var legs = Legs(CourseOverprint.Make(set, a, sheet));
        Assert.Equal(300 - 56.25 - 4.5, legs[0][1].X, 6);
        set.Appearance = new CourseAppearance(0.8, 1.25, 2);
        var o = CourseOverprint.Make(set, a, sheet);
        Assert.Equal(10.5, o.LineWidthM, 9);
        double digits = 60 * 1.25 * CourseStyle.DigitHeightPerFontSize;   // font 4 mm × 150 % = 60 m, × 125 %
        Assert.Contains(o.Marks.OfType<CourseMark.Text>(), t => Math.Abs(t.HeightM - digits) < 1e-9);
        Assert.Equal(24, Radii(set)[0], 9);
    }

    [Fact]
    public void OnePageCompetitionMapLayout()
    {
        var (set, a, _) = Sample();
        var sheet = new SheetSpec { ScaleDenominator = 4000 };
        var frame = new MapFrame(P(-100, -100), sheet.GroundWidthM, sheet.GroundHeightM);
        string PdfFor(CoursePrintLayout l) => Pdf(new GeoPdfRequest
        {
            Title = "Uji", Frame = frame, Sheet = sheet, Zone = Zone, Overprint = CourseOverprint.Make(set, a, sheet),
            Descriptions = new DescriptionSheet(set, a), PrintLayout = l, ContourIntervalM = 2.5,
        });
        var layout = new CoursePrintLayout();
        var one = PdfFor(layout);
        Assert.Contains("/Count 1", one);
        Assert.Contains("(1.5) Tj", one);
        var textLayout = new CoursePrintLayout { DescriptionSymbols = false };
        Assert.Contains("(Boulder, 1.5 m, east side) Tj", PdfFor(textLayout));
        Assert.DoesNotContain("(Scale 1:4000) Tj", one);
        Assert.DoesNotContain("(N) Tj", one);
        Assert.Contains("/F2", one);
        layout.SeparateDescriptionPage = true;
        Assert.Contains("/Count 2", PdfFor(layout));
        Assert.Equal(250, GeoPdfExporter.ScaleBarMeters(10000));
    }

    [Fact]
    public void DescriptionColumnsAndPosition()
    {
        var (set, a, _) = Sample();
        var d = new DescriptionSheet(set, a);
        Assert.True(d.Rows.Count >= 3);
        var cols = d.Columns(2);
        Assert.Equal((d.Rows.Count + 1) / 2, cols.Count);
        Assert.False(cols[0].IsContinuation);
        Assert.True(cols[1].IsContinuation);
        Assert.Equal(d.Rows, cols.SelectMany(c => c.Rows).ToList());
        Assert.Single(d.Columns(null));
        Assert.Single(d.Columns(99));

        var layout = new CoursePrintLayout { DescriptionCellMM = 6 };
        var one = GeoPdfExporter.DescriptionBlockSizeMM(d, layout);
        layout.DescriptionMaxRows = 2;
        var two = GeoPdfExporter.DescriptionBlockSizeMM(d, layout);
        Assert.Equal(cols.Count * 48, two.W, 9);
        Assert.True(two.H < one.H);

        var sheet = new SheetSpec { ScaleDenominator = 4000 };
        var tr = GeoPdfExporter.DescriptionOriginMM(two, layout, sheet);
        Assert.Equal(sheet.MappingWidthMM - 4 - two.W, tr.X, 9);
        Assert.Equal(4, tr.Y, 9);
        layout.DescriptionOffsetMM = P(20, 30);
        Assert.Equal(P(20, 30), GeoPdfExporter.DescriptionOriginMM(two, layout, sheet));
        layout.DescriptionOffsetMM = P(9999, -5);
        var clamped = GeoPdfExporter.DescriptionOriginMM(two, layout, sheet);
        Assert.Equal(sheet.MappingWidthMM - two.W, clamped.X, 9);
        Assert.Equal(0, clamped.Y);

        // PDF: second column is on the map page.
        var frame = new MapFrame(P(-100, -100), sheet.GroundWidthM, sheet.GroundHeightM);
        layout.DescriptionOffsetMM = null;
        layout.DescriptionSymbols = false;
        var pdf = Pdf(new GeoPdfRequest { Title = "Uji", Frame = frame, Sheet = sheet, Zone = Zone, Descriptions = d, PrintLayout = layout });
        Assert.Contains($"({d.Rows[^1].Text}) Tj", pdf);
    }

    [Fact]
    public void IofPictogramsCoverCatalogAndPrint()
    {
        // Every catalog symbol C–H has a pictogram.
        Assert.Empty(IofSymbols.All.Where(s => IofPictogram.Shapes(s.Id) is null).Select(s => s.Id));
        var dim = IofPictogram.DimensionShapes("1.5")!;
        Assert.Single(dim);
        Assert.Equal("text 1.5 (0.5,0.5) 0.4", dim[0].Describe());
        Assert.Single(IofPictogram.DimensionShapes("8x4")!);
        Assert.Equal(3, IofPictogram.DimensionShapes("1.0/2.5")!.Count);
        Assert.NotNull(IofPictogram.DimensionShapes("10.2"));
        Assert.Null(IofPictogram.Shapes("99.9"));

        var (set, a, _) = Sample();
        var id = set.ControlsOf(a).First(c => c.Kind == ControlKind.Control).Id;
        var i = set.Controls.FindIndex(c => c.Id == id);
        set.Controls[i].Description = new ControlDescription(which: "0.1N", feature: "2.4", appearance: "8.6", dimension: "1.5", location: "12.1E", other: "13.2");
        var d = new DescriptionSheet(set, a);
        Assert.Equal("2.4", d.Rows.First(r => r.Kind == ControlKind.Control).Description.Feature);
        Assert.NotNull(d.Rows[^1].FinishMeters);

        var sheet = new SheetSpec { ScaleDenominator = 4000 };
        var frame = new MapFrame(P(-100, -100), sheet.GroundWidthM, sheet.GroundHeightM);
        var layout = new CoursePrintLayout();
        Assert.True(layout.DescriptionSymbols);
        var pdf = Pdf(new GeoPdfRequest { Title = "Uji", Frame = frame, Sheet = sheet, Zone = Zone, Descriptions = d, PrintLayout = layout });
        Assert.Contains("(1.5) Tj", pdf);
        Assert.Contains($"({d.Rows[^1].FinishMeters} m) Tj", pdf);
        Assert.DoesNotContain($"({d.Rows.First(r => r.Kind == ControlKind.Control).Text}) Tj", pdf);
        Assert.DoesNotContain("(?) Tj", pdf);
    }

    [Fact]
    public void SampleFixtureCoursesReadAsTypedSet()
    {
        var doc = ProjectPackage.Read(ProjectArchive.ReadEntries(Fixtures.Bytes("sample-park-sprint.occa")));
        Assert.Equal("Sample Sprint", doc.Courses.EventTitle);
        Assert.Equal(5, doc.Courses.Controls.Count);
        Assert.Equal(new[] { "Long", "Short" }, doc.Courses.Courses.Select(c => c.Name));
        Assert.Equal(new[] { 31, 32, 33 }, doc.Courses.Controls.Where(c => c.Code is not null).Select(c => c.Code!.Value));
        // Written back byte-identical to the Mac's courses.json.
        var json = ProjectPackage.Serialize(doc)[ProjectPackage.CoursesFile];
        Assert.Equal(Encoding.UTF8.GetString(Fixtures.Bytes("sample-park-sprint/courses.json")), json);
    }

    [Fact]
    public void CourseItemsScopeAndMarks()
    {
        var (set, a, b) = Sample();
        var sheet = new SheetSpec { ScaleDenominator = 4000 };
        var text = new CourseItem(CourseItem.Kind.Text, new[] { P(100, 100) }, text: "Hello\nWorld", textHeightMM: 4) { Bold = true, ColorHex = "#000000" };
        text.SetScope(new List<Guid> { a.Id });
        Assert.True(text.Shows(a));
        Assert.False(text.Shows(b));
        Assert.True(text.Shows(null));
        var marks = text.Marks(sheet);
        Assert.Equal(2, marks.OfType<CourseMark.Text>().Count());
        Assert.IsType<CourseMark.Color>(marks[0]);
        Assert.Contains(marks, m => m is CourseMark.Font { Bold: true });
        Assert.Contains(marks, m => m is CourseMark.Level { Value: CourseLevel.Upper });
        var oob = new CourseItem(CourseItem.Kind.OutOfBoundsArea, new[] { P(0, 0), P(80, 60) });
        var oobMarks = oob.Marks(sheet);
        Assert.Contains(oobMarks, m => m is CourseMark.Polyline { Closed: true });
        Assert.Contains(oobMarks, m => m is CourseMark.Polyline { Closed: false });   // hatching clipped to the outline
        Assert.Equal(0, oob.HitDistance(P(40, 30), sheet));
        var route = new CourseItem(CourseItem.Kind.OutOfBoundsRoute, new[] { P(0, 0), P(200, 0) });
        Assert.Contains(route.Marks(sheet), m => m is CourseMark.Polyline);   // crosses along the route
        set.Items = new List<CourseItem> { text, oob };
        Assert.Equal(2, set.ItemsFor(a).Count);
        Assert.Single(set.ItemsFor(b));
        var o = CourseOverprint.Make(set, a, sheet);
        Assert.Contains(o.Marks, m => m is CourseMark.Text { Value: "Hello" });
        var levels = o.Levels;
        Assert.False(levels.Upper.IsEmpty);
        Assert.False(levels.Lower.IsEmpty);
    }

    [Fact]
    public void PngDecoderAndPdfImage()
    {
        // 2×2 RGBA PNG written with the standard encoder layout (filter 0 rows).
        var png = TestPng.Rgba(2, 2, new byte[] { 255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 255, 255, 255, 255, 0 });
        var rgba = PngDecoder.Decode(png)!;
        Assert.Equal(16, rgba.Length);
        Assert.Equal(255, rgba[0]);
        Assert.Equal(128, rgba[7]);
        Assert.Equal(CourseImages.Format.Png, CourseImages.FormatOf(png));
        Assert.Equal((2, 2, 3), CourseImages.Dimensions(png));
        var img = CourseImages.PdfImageOf(png)!;
        Assert.Contains("/FlateDecode", img.Dictionary);
        Assert.NotNull(img.Mask);
        var prepared = CourseImages.Prepare(png);
        Assert.Equal(1, prepared.Aspect);
        Assert.Throws<CourseImages.ImageException>(() => CourseImages.Prepare(new byte[] { 1, 2, 3 }));
    }
}

/// <summary>Minimal PNG encoder for tests (8-bit RGBA, filter 0).</summary>
internal static class TestPng
{
    public static byte[] Rgba(int w, int h, byte[] rgba)
    {
        var raw = new byte[(w * 4 + 1) * h];
        for (int y = 0; y < h; y++) Array.Copy(rgba, y * w * 4, raw, y * (w * 4 + 1) + 1, w * 4);
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] body)
        {
            var len = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
            ms.Write(len);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes);
            ms.Write(body);
            var crc = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Core.Contour.ZipWriter.Crc32(typeBytes.Concat(body).ToArray()));
            ms.Write(crc);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", CourseImages.Zlib(raw));
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }
}

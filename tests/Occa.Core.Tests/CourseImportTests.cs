using System.Buffers.Binary;
using System.Text;
using Occa.Core.Courses;
using Occa.Core.Geometry;
using Occa.Core.Import;
using Occa.Core.Json;
using Occa.Core.Project;
using Occa.Core.Sheet;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

/// <summary>Little-endian byte builder shared by the OCAD course fixtures (port of the Swift test helpers).</summary>
internal sealed class OcadBuilder
{
    private readonly List<byte> _d = new(new byte[64]);

    private void Ensure(int n) { while (_d.Count < n) _d.Add(0); }
    private Span<byte> At(int o, int n) { Ensure(o + n); return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_d).Slice(o, n); }

    public void U8(byte v, int o) { Ensure(o + 1); _d[o] = v; }
    public void U16(ushort v, int o) => BinaryPrimitives.WriteUInt16LittleEndian(At(o, 2), v);
    public void I16(short v, int o) => BinaryPrimitives.WriteInt16LittleEndian(At(o, 2), v);
    public void U32(uint v, int o) => BinaryPrimitives.WriteUInt32LittleEndian(At(o, 4), v);
    public void I32(int v, int o) => BinaryPrimitives.WriteInt32LittleEndian(At(o, 4), v);
    public int Append(byte[] bytes) { int o = _d.Count; _d.AddRange(bytes); return o; }
    public int Append(int zeros) => Append(new byte[zeros]);
    public byte[] ToArray() => _d.ToArray();
}

/// <summary>
/// OCAD 10 files (572-byte symbol header with a ShortString name, 40-byte object header) and OCAD course setting data
/// (control strings type 1 linked to their objects, course strings type 2). Port of Ocad10CourseTests.
/// </summary>
public class Ocad10CourseTests
{
    /// <summary>
    /// Minimal OCAD 10 file: line symbol 101.0, course symbols 701/702/706, one contour line, and optionally a course setting
    /// with start S1, controls 31 and 32, finish F1, and course "A" = S1 32 31 F1.
    /// </summary>
    public static byte[] MakeOcad10(bool withCourses)
    {
        var d = new OcadBuilder();
        d.U16(0x0CAD, 0);
        d.I16(10, 4);

        // Symbols (TBaseSym 572 bytes; type data from +572).
        int symBlock = d.Append(4 + 256 * 4);
        int Symbol(int number, byte kind, string name, int size)
        {
            int sp = d.Append(size);
            d.I32(size, sp);
            d.I32(number, sp + 4);
            d.U8(kind, sp + 8);
            d.I16(5, sp + 28);
            var bytes = Encoding.UTF8.GetBytes(name);
            d.U8((byte)bytes.Length, sp + 56);
            for (int i = 0; i < bytes.Length; i++) d.U8(bytes[i], sp + 57 + i);
            return sp;
        }
        int contour = Symbol(101_000, 2, "Contour", 648);
        d.I16(5, contour + 572);    // line colour
        d.I16(21, contour + 574);   // 0.21 mm
        var symbols = new List<int> { contour };
        foreach (var (n, name) in new[] { (701_000, "Start"), (702_000, "Control point"), (706_000, "Finish") }) symbols.Add(Symbol(n, 1, name, 600));
        for (int i = 0; i < symbols.Count; i++) d.U32((uint)symbols[i], symBlock + 4 + i * 4);
        d.U32((uint)symBlock, 8);

        // Objects (TElement header 40 bytes, nItem at +8). Paper mm × 100 << 8.
        int objBlock = d.Append(4 + 256 * 40);
        var objects = new List<(int Sym, byte Kind, (int X, int Y)[] Pts)> { (101_000, 2, new[] { (0, 0), (1000, 0), (2000, 1000) }) };
        if (withCourses)
            objects.AddRange(new (int, byte, (int, int)[])[] { (701_000, 1, new[] { (0, 0) }), (702_000, 1, new[] { (1000, 0) }), (702_000, 1, new[] { (2000, 1000) }), (706_000, 1, new[] { (100, 100) }) });
        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            int pos = d.Append(40 + o.Pts.Length * 8);
            d.I32(o.Sym, pos);
            d.U8(o.Kind, pos + 4);
            d.I32(o.Pts.Length, pos + 8);
            for (int k = 0; k < o.Pts.Length; k++)
            {
                d.I32(o.Pts[k].X << 8, pos + 40 + k * 8);
                d.I32(o.Pts[k].Y << 8, pos + 44 + k * 8);
            }
            int e = objBlock + 4 + i * 40;
            d.U32((uint)pos, e + 16);
            d.U32((uint)(40 + o.Pts.Length * 8), e + 20);
            d.I32(o.Sym, e + 24);
            d.U8(o.Kind, e + 28);
            d.U8(1, e + 30);   // status normal
        }
        d.U32((uint)objBlock, 12);

        // Strings: scale/georeference, colour, and the course setting (object index is 1-based).
        int strBlock = d.Append(4 + 256 * 16);
        var strings = new List<(string Text, int Type, int Obj)> { ("\tm10000\tg100\tr1\tx680000\ty9304000\ta0", 1039, 0), ("Brown\tn5\tc0\tm56\ty100\tk18", 9, 0) };
        if (withCourses)
            strings.AddRange(new[] { ("S1\tYs", 1, 2), ("31\tYc\td5.11", 1, 3), ("32\tYc", 1, 4), ("F1\tYf", 1, 5), ("A\tsS1\tc32\tc31\tfF1", 2, 0), ("B\tsS1\tc31\tc99\tfF1", 2, 0) });
        for (int i = 0; i < strings.Count; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(strings[i].Text).Concat(new byte[] { 0 }).ToArray();
            int pos = d.Append(bytes);
            int e = strBlock + 4 + i * 16;
            d.U32((uint)pos, e);
            d.U32((uint)bytes.Length, e + 4);
            d.I32(strings[i].Type, e + 8);
            d.I32(strings[i].Obj, e + 12);
        }
        d.U32((uint)strBlock, 32);
        return d.ToArray();
    }

    [Fact]
    public void ReadsOcad10SymbolsAndObjects()
    {
        var map = OcadReader.Read(MakeOcad10(false));
        Assert.Equal(10, map.Version);
        Assert.Equal("Contour", map.Symbols[101_000].Name);
        Assert.Equal(0.21, map.Symbols[101_000].LineWidthMM);
        var (features, report) = map.Features();
        Assert.Equal(1, report.Imported);
        Assert.Equal(3, features[0].Nodes.Count);
        // (10 mm, 0) on paper at 1:10000 = 100 m east of the offset.
        Assert.Equal(680_100, features[0].Nodes[1].Point.X, 6);
    }

    [Fact]
    public void ReadsCourseSetting()
    {
        var c = OcadCourseReader.Read(MakeOcad10(true));
        Assert.Equal(new[] { ControlKind.Start, ControlKind.Control, ControlKind.Control, ControlKind.Finish }, c.Controls.Select(x => x.Kind));
        Assert.Equal(new[] { 31, 32 }, c.Controls.Where(x => x.Code is not null).Select(x => x.Code!.Value));
        Assert.Equal(680_200, c.Controls[2].Position.X, 6);
        Assert.Equal(9_304_100, c.Controls[2].Position.Y, 6);
        Assert.Equal(1, c.SkippedDescriptions);
        var a = c.Courses.First(x => x.Name == "A");
        var order = a.ControlIds.Select(id => c.Controls.First(x => x.Id == id)).Select(x => x.Code?.ToString() ?? x.Kind.RawValue()).ToList();
        Assert.Equal(new[] { "start", "32", "31", "finish" }, order);
        Assert.Equal(1, c.MissingReferences);
    }

    [Fact]
    public void MapWithoutCourseSettingHasNoControls() => Assert.Empty(OcadCourseReader.Read(MakeOcad10(false)).Controls);
}

/// <summary>OCAD 12+ course setting files keep each control's string in the control object itself ("object string"); the code is parameter a.</summary>
public class Ocad12CourseTests
{
    public static byte[] MakeOcad12Courses()
    {
        var d = new OcadBuilder();
        d.U16(0x0CAD, 0);
        d.U8(1, 2);        // file type: course setting
        d.I16(12, 4);

        int symBlock = d.Append(4 + 256 * 4);
        var syms = new[] { (701_000, "Start"), (702_000, "Control point"), (706_000, "Finish") };
        for (int i = 0; i < syms.Length; i++)
        {
            int sp = d.Append(820);
            d.I32(820, sp);
            d.I32(syms[i].Item1, sp + 4);
            d.U8(1, sp + 8);
            for (int k = 0; k < syms[i].Item2.Length; k++) d.U16(syms[i].Item2[k], sp + 56 + k * 2);
            d.U32((uint)sp, symBlock + 4 + i * 4);
        }
        d.U32((uint)symBlock, 8);

        // Objects: 56-byte TElement header; object string (UTF-16, padded to 8 bytes) after the coordinates.
        int objBlock = d.Append(4 + 256 * 40);
        var objects = new[]
        {
            (701_000, (0, 0), "00S1\tYs\taS1"), (702_000, (1000, 0), "1031\tYc\ta31\td5.011"), (702_000, (2000, 1000), "1032\tYc\ta32"), (706_000, (100, 100), "30F1\tYf\taF1"),
        };
        for (int i = 0; i < objects.Length; i++)
        {
            var (sym, p, text) = objects[i];
            var units = text.Select(ch => (ushort)ch).ToList();
            while (units.Count % 4 != 0) units.Add(0);
            int nString = units.Count / 4;
            int pos = d.Append(56 + 8 + nString * 8);
            d.I32(sym, pos);
            d.U8(1, pos + 4);
            d.I32(1, pos + 44);                // nItem
            d.I16(0, pos + 48);                // nText
            d.I16((short)nString, pos + 50);   // nObjectString (8-byte units)
            d.U8(1, pos + 54);                 // ObjectStringType: control
            d.I32(p.Item1 << 8, pos + 56);
            d.I32(p.Item2 << 8, pos + 60);
            for (int k = 0; k < units.Count; k++) d.U16(units[k], pos + 64 + k * 2);
            int e = objBlock + 4 + i * 40;
            d.U32((uint)pos, e + 16);
            d.I32(sym, e + 24);
            d.U8(1, e + 28);
            d.U8(1, e + 30);
        }
        d.U32((uint)objBlock, 12);

        int strBlock = d.Append(4 + 256 * 16);
        var strings = new[] { ("\tm4000\tg100\tr1\tx694000\ty9311000\ta0", 1039), ("Purple\tn11\tc0\tm100\ty0\tk0", 9), ("Men\tmM2\tsS1\tc32\tc31\tmM1\tfF1\tE1.10", 2) };
        for (int i = 0; i < strings.Length; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(strings[i].Item1).Concat(new byte[] { 0 }).ToArray();
            int pos = d.Append(bytes);
            d.U32((uint)pos, strBlock + 4 + i * 16);
            d.U32((uint)bytes.Length, strBlock + 8 + i * 16);
            d.I32(strings[i].Item2, strBlock + 12 + i * 16);
        }
        d.U32((uint)strBlock, 32);
        return d.ToArray();
    }

    [Fact]
    public void ControlsFromObjectStringsAndCourseOrder()
    {
        var c = OcadCourseReader.Read(MakeOcad12Courses());
        Assert.Equal(new[] { ControlKind.Start, ControlKind.Control, ControlKind.Control, ControlKind.Finish }, c.Controls.Select(x => x.Kind));
        Assert.Equal(new[] { 31, 32 }, c.Controls.Where(x => x.Code is not null).Select(x => x.Code!.Value));
        // (10 mm, 0) on paper at 1:4000 = 40 m east of the offset.
        Assert.Equal(694_040, c.Controls[1].Position.X, 6);
        var men = c.Courses[0];
        Assert.Equal("Men", men.Name);
        var order = men.ControlIds.Select(id => c.Controls.First(x => x.Id == id)).Select(x => x.Code?.ToString() ?? x.Kind.RawValue()).ToList();
        Assert.Equal(new[] { "start", "32", "31", "finish" }, order);
        Assert.Equal(0, c.MissingReferences);
    }
}

/// <summary>Course page → Import course: .occa, OCAD course setting, and OOM course symbols (port of CourseFileImportTests).</summary>
public class CourseFileImportTests
{
    private static byte[] Archive(UtmZone zone, IEnumerable<ControlPoint> controls, IEnumerable<Course> courses)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var project = new Core.Project.Project { Name = "Source", CrsEpsg = zone.Epsg, AreaLocked = true };
            ProjectPackage.Write(new ProjectDocument { Project = project, Courses = new CourseSet(controls: controls, courses: courses) }, dir);
            return ProjectArchive.Pack(dir);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void OccaCoursesKeepDescriptionsAndOrderWithNewIds()
    {
        var zone = UtmZone.FromEpsg(32748)!.Value;
        var c31 = new ControlPoint(ControlKind.Control, P(700_100, 9_300_100), 31) { Description = new ControlDescription(feature: "2.4") };
        var start = new ControlPoint(ControlKind.Start, P(700_000, 9_300_000));
        var course = new Course("Elite", new[] { start.Id, c31.Id });
        var data = Archive(zone, new[] { start, c31 }, new[] { course });

        var imported = CourseFileImport.Read(data, "occa", zone);
        Assert.Equal(2, imported.Controls.Count);
        Assert.DoesNotContain(imported.Controls, c => c.Id == c31.Id);
        var i31 = imported.Controls.First(c => c.Code == 31);
        Assert.Equal("2.4", i31.Description.Feature);
        Assert.Equal(c31.Position, i31.Position);
        Assert.Equal("Elite", imported.Courses[0].Name);
        Assert.Equal(new[] { imported.Controls[0].Id, i31.Id }, imported.Courses[0].ControlIds);
    }

    [Fact]
    public void OccaPositionsAreConvertedToTheCurrentZone()
    {
        var source = UtmZone.FromEpsg(32748)!.Value; var target = UtmZone.FromEpsg(32749)!.Value;
        var c = new ControlPoint(ControlKind.Control, P(820_000, 9_250_000), 40);
        var data = Archive(source, new[] { c }, new[] { new Course("A", new[] { c.Id }) });
        var imported = CourseFileImport.Read(data, "occa", target);
        var ll = source.Unproject(c.Position); var back = target.Unproject(imported.Controls[0].Position);
        Assert.Equal(ll.Lat, back.Lat, 7);
        Assert.Equal(ll.Lon, back.Lon, 7);
        Assert.True(Math.Abs(imported.Controls[0].Position.X - c.Position.X) > 1);
    }

    [Fact]
    public void OcadCourseSettingAndMapOnlyFiles()
    {
        var zone = UtmZone.FromEpsg(32748)!.Value;
        var c = CourseFileImport.Read(Ocad10CourseTests.MakeOcad10(true), "ocd", zone);
        Assert.Equal(new[] { "A", "B" }, c.Courses.Select(x => x.Name));
        Assert.Throws<CourseImportException>(() => CourseFileImport.Read(Ocad10CourseTests.MakeOcad10(false), "ocd", zone));
        Assert.Throws<CourseImportException>(() => CourseFileImport.Read(new byte[] { 1, 2 }, "gpx", zone));
    }

    [Fact]
    public void OmapCourseSymbolsBecomeControls()
    {
        // OMAP with a start (701) and a control (702) point object, written by hand in the OOM format.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <map xmlns="http://openorienteering.org/apps/mapper/xml/v2" version="9">
            <georeferencing scale="4000" grid_scale_factor="1" auxiliary_scale_factor="1" declination="0" grivation="0">
            <ref_point x="0" y="0"/>
            <projected_crs id="UTM"><spec language="PROJ.4">+proj=utm +zone=48 +south +datum=WGS84</spec><ref_point x="700000" y="9300000"/></projected_crs>
            <geographic_crs id="Geographic coordinates"><spec language="PROJ.4">+proj=latlong +datum=WGS84</spec><ref_point_deg lat="-6.3241" lon="106.8098"/></geographic_crs>
            </georeferencing>
            <colors count="1"><color priority="0" name="Purple" c="0" m="1" y="0" k="0" opacity="1"/></colors>
            <barrier version="6" required="0.6.0">
            <symbols count="2">
            <symbol type="1" id="0" code="701.0" name="Start"/>
            <symbol type="1" id="1" code="702.0" name="Control point"/>
            </symbols>
            <parts count="1" current="0"><part name="default"><objects count="2">
            <object type="0" symbol="0"><coords count="1">0 0;</coords></object>
            <object type="0" symbol="1"><coords count="1">25000 0;</coords></object>
            </objects></part></parts>
            </barrier>
            </map>
            """;
        var zone = UtmZone.FromEpsg(32748)!.Value;
        var c = CourseFileImport.Read(Encoding.UTF8.GetBytes(xml), "omap", zone);
        Assert.Equal(new[] { ControlKind.Start, ControlKind.Control }, c.Controls.Select(x => x.Kind));
        Assert.Empty(c.Courses);
        // 25 mm on paper at 1:4000 = 100 m east of the start.
        Assert.Equal(100, c.Controls[1].Position.X - c.Controls[0].Position.X, 2);
    }
}

/// <summary>Cut leg: a gap in the course line splits the leg into two pieces; gaps belong to one leg of one course (port of LegGapTests).</summary>
public class LegGapTests
{
    private static List<IReadOnlyList<Point2D>> Legs(CourseOverprint o) => o.Marks.OfType<CourseMark.Polyline>().Where(p => !p.Closed && p.Points.Count == 2).Select(p => p.Points).ToList();

    [Fact]
    public void GapSplitsLeg()
    {
        var sheet = new SheetSpec { ScaleDenominator = 4000 };
        var s = new ControlPoint(ControlKind.Start, P(0, 0));
        var c = new ControlPoint(ControlKind.Control, P(400, 0), 31);
        var course = new Course("A", new[] { s.Id, c.Id });
        var plain = CourseOverprint.Make(new CourseSet(controls: new[] { s, c }, courses: new[] { course }), course, sheet);
        Assert.Single(Legs(plain));

        course.LegGaps = new List<LegGap> { new(s.Id, c.Id, 0.5) };
        var cut = CourseOverprint.Make(new CourseSet(controls: new[] { s, c }, courses: new[] { course }), course, sheet);
        var pieces = Legs(cut);
        Assert.Equal(2, pieces.Count);
        // 2.5 mm at 1:4000 = 10 m, centred at x = 200.
        Assert.Equal(195, pieces[0][1].X, 2);
        Assert.Equal(205, pieces[1][0].X, 2);

        // The All controls view has no legs, so no gaps either.
        Assert.Empty(Legs(CourseOverprint.Make(new CourseSet(controls: new[] { s, c }, courses: new[] { course }), null, sheet)));
    }

    [Fact]
    public void OldCourseFilesDecodeWithoutGaps()
    {
        const string json = """{"id":"6B8C2D1A-0000-4000-8000-000000000001","name":"A","kind":"normal","controlIDs":[],"competitors":0}""";
        var course = OccaJson.Deserialize<Course>(json);
        Assert.Null(course.LegGaps);
    }
}

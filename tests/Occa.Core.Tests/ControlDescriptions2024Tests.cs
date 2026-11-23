using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Occa.Core.Courses;
using Occa.Core.Export;
using Occa.Core.Geometry;
using Occa.Core.Json;
using Occa.Core.Sheet;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

/// <summary>
/// IOF Control Descriptions 2024: catalog, migration of 2018 data, directional column G symbols, special rows,
/// thick lines, heading rules, and JSON compatibility (port of ControlDescriptions2024Tests).
/// </summary>
public class ControlDescriptions2024Tests
{
    /// <summary>Start → 6 controls (100 m apart) → finish.</summary>
    private static (CourseSet Set, Course Course) MakeCourse(Action<Course>? configure = null)
    {
        var s = new ControlPoint(ControlKind.Start, P(0, 0));
        var cs = Enumerable.Range(1, 6).Select(i => new ControlPoint(ControlKind.Control, P(i * 100, 0), 30 + i)).ToList();
        var f = new ControlPoint(ControlKind.Finish, P(600, 250));
        var c = new Course("A", new[] { s.Id }.Concat(cs.Select(x => x.Id)).Concat(new[] { f.Id }), climbM: 213);
        configure?.Invoke(c);
        return (new CourseSet("Event", new[] { s }.Concat(cs).Concat(new[] { f }), new[] { c }), c);
    }

    private static string Dump(IEnumerable<IofPictogram.Shape>? shapes) => shapes is null ? "null" : string.Join("\n", shapes.Select(s => s.Describe()));

    [Fact]
    public void CatalogNames2024()
    {
        Assert.Equal("Cliff, crag", IofSymbols.Name("2.1"));
        Assert.Equal("Prominent tree", IofSymbols.Name("4.9"));
        Assert.Equal("Wall", IofSymbols.Name("5.8"));
        Assert.Equal("Canopy", IofSymbols.Name("5.21"));
        Assert.Equal("Out of bounds area", IofSymbols.Name("5.23"));
        Assert.Equal("Railway", IofSymbols.Name("5.24"));
        Assert.Equal("ruined", IofSymbols.Name("8.11"));
        Assert.Equal(IofColumn.F, IofSymbols.Symbol("11.1")?.Column);
        Assert.Equal("manned control", IofSymbols.Name("13.3"));
        Assert.Equal("north east corner (inside)", IofSymbols.Name("12.4NE"));
        Assert.Equal("south west tip", IofSymbols.Name("12.6SW"));
        Assert.Equal("north east foot", IofSymbols.Name("12.12NE"));
        Assert.Equal("foot", IofSymbols.Name("12.11"));
        // Picker: no legacy symbols, column G once per base symbol, bend in F.
        Assert.DoesNotContain(IofSymbols.PickerSymbols(IofColumn.D), s => s.IsLegacy);
        Assert.Equal(Enumerable.Range(1, 14).Select(i => $"12.{i}"), IofSymbols.PickerSymbols(IofColumn.G).Select(s => s.Id));
        Assert.Contains(IofSymbols.PickerSymbols(IofColumn.F), s => s.Id == "11.1");
        Assert.DoesNotContain(IofSymbols.PickerSymbols(IofColumn.H), s => s.Id == IofSymbols.Legacy.Radio);
        Assert.Equal(IofSymbols.IndoorGroup, IofSymbols.PickerSymbols(IofColumn.D, indoor: true)[0].Group);
        // Every stored id (incl. all directions and legacy ids) draws a pictogram.
        Assert.Empty(IofSymbols.All.Where(s => IofPictogram.Shapes(s.Id) is null).Select(s => s.Id));
    }

    [Fact]
    public void DirectionalLocationIds()
    {
        Assert.Equal(("12.4", "NE"), IofSymbols.SplitDirection("12.4NE"));
        Assert.Equal(("12.12", "SW"), IofSymbols.SplitDirection("12.12SW"));
        Assert.Equal(("12.1", "N"), IofSymbols.SplitDirection("12.1N"));
        Assert.Equal(("12.10", (string?)null), IofSymbols.SplitDirection("12.10"));
        Assert.Equal(("x.bend", (string?)null), IofSymbols.SplitDirection("x.bend"));
        foreach (var b in IofSymbols.DirectionalLocations)
            foreach (var dir in IofSymbols.Directions) Assert.NotNull(IofSymbols.Symbol(b + dir));
        Assert.Null(IofSymbols.Symbol("12.8N"));
        // Rotation: the corner (inside) dot points the way the corner points.
        Point2D Dot(string id) => IofPictogram.Shapes(id)!.OfType<IofPictogram.Shape.Dots>().First().Points[0];
        var n = Dot("12.4N"); var e = Dot("12.4E"); var sw = Dot("12.4SW");
        Assert.True(n.Y > 0.5);
        Assert.True(e.X > 0.5);
        Assert.Equal(0.5, e.Y, 9);
        Assert.True(sw.X < 0.5);
        Assert.True(sw.Y < 0.5);
        Assert.Equal(Dump(IofPictogram.Shapes("12.4N")), Dump(IofPictogram.Shapes("12.4")));
        Assert.Equal("Hill, north east foot", new ControlDescription(feature: "1.9", location: "12.12NE").Text);
        Assert.Equal("Open land, east corner (inside)", new ControlDescription(feature: "4.1", location: "12.4E").Text);
        Assert.Equal("Track / path, bend", new ControlDescription(feature: "5.2", dimension: "11.1").Text);
        Assert.Equal("Cliff, 0.5 m / 3 m", new ControlDescription(feature: "2.1", dimension: "0.5/3").Text);
        Assert.Equal("Quarry, 5 m x 5 m", new ControlDescription(feature: "1.5", dimension: "5x5").Text);
        Assert.Equal(3, IofPictogram.DimensionShapes("2/3")?.Count);
    }

    private static ControlDescription Migrated(ControlDescription d) => d.MigratedFrom2018();

    [Fact]
    public void MigrationColumnG()
    {
        var table = new Dictionary<string, string>
        {
            ["11.1N"] = "12.1N", ["11.1NE"] = "12.1NE", ["11.1SW"] = "12.1SW", ["11.3"] = "12.8", ["11.4"] = "12.9", ["11.5"] = "12.10",
            ["11.6"] = "12.13", ["11.7"] = "12.11", ["11.8"] = "12.4N", ["11.9"] = "12.5N", ["11.10"] = "12.6N", ["11.12"] = "12.7N",
            ["11.13"] = "12.3N", ["11.15"] = "12.14", ["11.2"] = IofSymbols.Legacy.TopEdge, ["11.14"] = IofSymbols.Legacy.Beginning,
        };
        foreach (var (old, @new) in table) Assert.Equal(@new, Migrated(new ControlDescription(feature: "2.4", location: old)).Location);
        // Bend: to column F when F is empty, otherwise kept as a legacy column G symbol.
        var bend = Migrated(new ControlDescription(feature: "5.2", location: "11.11"));
        Assert.Equal("11.1", bend.Dimension);
        Assert.Null(bend.Location);
        var bendWithF = Migrated(new ControlDescription(feature: "5.2", dimension: "10.2", location: "11.11"));
        Assert.Equal("10.2", bendWithF.Dimension);
        Assert.Equal(IofSymbols.Legacy.Bend, bendWithF.Location);
    }

    [Fact]
    public void MigrationColumnsHAndD()
    {
        Assert.Equal("13.1", Migrated(new ControlDescription(other: "12.1")).Other);
        Assert.Equal("13.2", Migrated(new ControlDescription(other: "12.2")).Other);
        Assert.Equal("13.3", Migrated(new ControlDescription(other: "12.4")).Other);
        Assert.Equal(IofSymbols.Legacy.Radio, Migrated(new ControlDescription(other: "12.3")).Other);
        Assert.Equal(IofSymbols.Legacy.Barrier, Migrated(new ControlDescription(feature: "5.23")).Feature);
        Assert.Equal(IofSymbols.Legacy.RubbishBin, Migrated(new ControlDescription(feature: "5.24")).Feature);
        Assert.Equal("5.21", Migrated(new ControlDescription(feature: "5.21")).Feature);
        // Per field: old H 12.x and new G 12.x never collide.
        var both = Migrated(new ControlDescription(feature: "2.4", location: "11.13", other: "12.1"));
        Assert.Equal("12.3N", both.Location);
        Assert.Equal("13.1", both.Other);
        var unchanged = new ControlDescription(which: "0.1NE", feature: "2.4", appearance: "8.1", dimension: "1.5");
        Assert.Equal(unchanged, Migrated(unchanged));
        // Legacy ids keep a name and pictogram, hidden from the picker.
        Assert.Equal("Barrier, radio / TV control", new ControlDescription(feature: IofSymbols.Legacy.Barrier, other: IofSymbols.Legacy.Radio).Text);
    }

    [Fact]
    public void MigrationRunsOnlyOnOldData()
    {
        var (set, _) = MakeCourse();
        const int i = 1;
        set.Controls[i].Description = new ControlDescription(feature: "5.24", location: "11.8", other: "12.4");
        var json = JsonNode.Parse(OccaJson.SerializeSorted(set))!.AsObject();
        Assert.Equal(2024, json["descriptionFormat"]!.GetValue<int>());
        // New data: 5.24 is the 2024 railway, nothing changes.
        var same = OccaJson.Deserialize<CourseSet>(OccaJson.SerializeSorted(set));
        Assert.Equal(set.Controls[i].Description, same.Controls[i].Description);
        Assert.Equal(OccaJson.SerializeSorted(set), OccaJson.SerializeSorted(same));
        // Saved before the update (no format field): converted once.
        json.Remove("descriptionFormat");
        var old = OccaJson.Deserialize<CourseSet>(json.ToJsonString());
        Assert.Equal(new ControlDescription(feature: IofSymbols.Legacy.RubbishBin, location: "12.4N", other: "13.3"), old.Controls[i].Description);
        Assert.Equal(2024, old.DescriptionFormat);
        var again = OccaJson.Deserialize<CourseSet>(OccaJson.SerializeSorted(old));
        Assert.Equal(old.Controls[i].Description, again.Controls[i].Description);
    }

    [Fact]
    public void SpecialRowsAndThickLines()
    {
        var (set, c) = MakeCourse(c =>
        {
            c.Classes = " M45 M50 W21 ";
            c.StartDistanceM = 150;
            c.FinishRoute = FinishRoute.Taped;
            c.SpecialInstructions = new List<SpecialInstruction>
            {
                new(SpecialInstructionKind.TapedAway, 2, 60), new(SpecialInstructionKind.MapFlip, 4), new(SpecialInstructionKind.CrossingPoint, 99, 40),
            };
        });
        var d = new DescriptionSheet(set, c);
        Assert.Equal("M45 M50 W21", d.Classes);
        Assert.Equal(2, d.HeadingRowCount);
        Assert.Equal("215 m", d.ClimbText);
        var kinds = d.Rows.Select(r => r.SpecialRow switch
        {
            DescriptionSheet.Special.StartDistance => "14.1",
            DescriptionSheet.Special.Instruction ins => ins.Kind.RawValue(),
            _ => r.Kind == ControlKind.Control ? r.Number : r.Kind.RawValue(),
        }).ToList();
        Assert.Equal(new[] { "14.1", "start", "1", "2", "15.1", "3", "4", "15.6", "5", "6", "15.3", "finish" }, kinds);
        Assert.Equal("Distance to start triangle: 150 m", d.Rows[0].Text);
        Assert.Equal("Follow taped route 60 m away from control", d.Rows[4].Text);
        Assert.Equal("Mandatory crossing point", d.Rows[10].Text);
        Assert.Equal(FinishRoute.Taped, d.Rows[^1].FinishRoute);
        Assert.Equal("250 m from last control to finish, follow taped route", d.Rows[^1].Text);
        // Thick: both sides of each special instruction and after every 3rd control.
        var thick = d.Rows.Select((r, i) => (r, i)).Where(x => x.r.ThickBelow).Select(x => x.i).ToList();
        Assert.Equal(new[] { 3, 4, 5, 6, 7, 9, 10 }, thick);
        Assert.Null(d.Rows[0].ControlIndex);
        Assert.Equal(1, d.Rows[2].ControlIndex);
        Assert.Equal(5, d.Rows.Count(r => r.IsFullWidth(true)));
        Assert.Equal(4, d.Rows.Count(r => r.IsFullWidth(false)));
        // Continuation columns keep rows and thick lines.
        var cols = d.Columns(5);
        Assert.Equal(d.Rows, cols.SelectMany(x => x.Rows).ToList());
        Assert.True(cols[1].Rows[0].ThickBelow);
        Assert.Equal((d.Rows.Count + 2 + 1) * 10.0, GeoPdfExporter.DescriptionTableHeight(d, 10));
        // Every full-width row has a drawing; text rows have none.
        foreach (var r in d.Rows.Where(r => r.IsFullWidth(true))) Assert.NotNull(IofPictogram.RowShapes(r));
        Assert.Null(IofPictogram.RowShapes(d.Rows[2]));
    }

    [Fact]
    public void FinishVariantsAndDefaults()
    {
        foreach (var (route, text) in new[]
        {
            (FinishRoute.Taped, "250 m from last control to finish, follow taped route"),
            (FinishRoute.Funnel, "250 m from last control to finish, navigate to finish funnel, then follow tapes"),
            (FinishRoute.Navigate, "Navigate 250 m to finish"),
        })
        {
            var (set, c) = MakeCourse(c => c.FinishRoute = route);
            Assert.Equal(text, new DescriptionSheet(set, c).Rows[^1].Text);
        }
        var (set2, c2) = MakeCourse();
        Assert.Equal(FinishRoute.Navigate, c2.EffectiveFinishRoute);
        var d = new DescriptionSheet(set2, c2);
        Assert.Equal(new[] { 3, 6 }, d.Rows.Select((r, i) => (r, i)).Where(x => x.r.ThickBelow).Select(x => x.i));
        Assert.Equal("210 m", DescriptionSheet.ClimbTextFor(212));
        Assert.Equal("0 m", DescriptionSheet.ClimbTextFor(2));
        Assert.Equal("", DescriptionSheet.ClimbTextFor(null));
    }

    [Fact]
    public void PdfPrintsSpecialRows()
    {
        var (set, c) = MakeCourse(c =>
        {
            c.Classes = "M45 W21";
            c.StartDistanceM = 150;
            c.FinishRoute = FinishRoute.Funnel;
            c.SpecialInstructions = new List<SpecialInstruction> { new(SpecialInstructionKind.TapedBetween, 1, 300) };
        });
        var d = new DescriptionSheet(set, c);
        var sheet = new SheetSpec { ScaleDenominator = 4000 };
        var frame = new MapFrame(P(-100, -100), sheet.GroundWidthM, sheet.GroundHeightM);
        string Pdf(bool symbols)
        {
            var layout = new CoursePrintLayout { DescriptionSymbols = symbols, SeparateDescriptionPage = true };
            var data = GeoPdfExporter.Document(new GeoPdfRequest
            {
                Title = "Event", Frame = frame, Sheet = sheet, Zone = UtmZone.FromEpsg(32748)!.Value, Descriptions = d, PrintLayout = layout,
            });
            return Encoding.Latin1.GetString(data);
        }
        var symbolsPdf = Pdf(true);
        Assert.Contains("(M45 W21) Tj", symbolsPdf);
        Assert.Contains("(150 m) Tj", symbolsPdf);
        Assert.Contains("(300 m) Tj", symbolsPdf);
        Assert.Contains("(250 m) Tj", symbolsPdf);
        Assert.Contains("IOF control descriptions 2024", symbolsPdf);
        var text = Pdf(false);
        Assert.Contains("(Distance to start triangle: 150 m) Tj", text);
        Assert.Contains("(Follow taped route 300 m between controls) Tj", text);
    }

    [Fact]
    public void CellSizeClamp()
    {
        Assert.Equal((5, 7), CoursePrintLayout.CellRangeMM);
        var l = new CoursePrintLayout { DescriptionCellMM = 8 };
        var back = OccaJson.Deserialize<CoursePrintLayout>(OccaJson.SerializeSorted(l));
        Assert.Equal(7, back.DescriptionCellMM);
        l.DescriptionCellMM = 6.5;
        Assert.Equal(6.5, OccaJson.Deserialize<CoursePrintLayout>(OccaJson.SerializeSorted(l)).DescriptionCellMM);
    }

    [Fact]
    public void CourseFieldsRoundTripAndOldJson()
    {
        var (set, _) = MakeCourse(c =>
        {
            c.Classes = "M21";
            c.StartDistanceM = 120;
            c.FinishRoute = FinishRoute.Funnel;
            c.SpecialInstructions = new List<SpecialInstruction> { new(SpecialInstructionKind.TapedToMapExchange, 3, 50), new(SpecialInstructionKind.OutOfBoundsPassage, 1) };
        });
        var json = OccaJson.SerializeSorted(set);
        var back = OccaJson.Deserialize<CourseSet>(json);
        Assert.Equal(json, OccaJson.SerializeSorted(back));
        Assert.Equal(new[] { SpecialInstructionKind.TapedToMapExchange, SpecialInstructionKind.OutOfBoundsPassage }, back.Courses[0].SpecialInstructions!.Select(s => s.Kind));
        Assert.Contains("\"kind\":\"15.5\"", json);
        Assert.Contains("\"finishRoute\":\"16.2\"", json);
        // A course saved by an older version (no new keys) still decodes.
        const string old = """{"id":"6F2C1F7E-8B0B-4C8B-9A47-3E1F0D2B9C11","name":"Old","kind":"normal","controlIDs":[],"competitors":0}""";
        var c = OccaJson.Deserialize<Course>(old);
        Assert.Null(c.Classes);
        Assert.Null(c.StartDistanceM);
        Assert.Null(c.SpecialInstructions);
        Assert.Equal(FinishRoute.Navigate, c.EffectiveFinishRoute);
    }

    /// <summary>Distance rows (14.1, 15.1/15.2/15.5, finish 16.1/16.2): the dashes stop the same distance from the label on both sides.</summary>
    [Fact]
    public void DistanceRowDashesAreSymmetricAroundTheLabel()
    {
        var rows = new[] { 50, 150, 1250 }.SelectMany(m => new[]
        {
            DescriptionSheet.Row.ForSpecial(new DescriptionSheet.Special.StartDistance(m)),
            DescriptionSheet.Row.ForSpecial(new DescriptionSheet.Special.Instruction(SpecialInstructionKind.TapedAway, m)),
            DescriptionSheet.Row.ForSpecial(new DescriptionSheet.Special.Instruction(SpecialInstructionKind.TapedBetween, m)),
            DescriptionSheet.Row.ForSpecial(new DescriptionSheet.Special.Instruction(SpecialInstructionKind.TapedToMapExchange, m)),
        });
        foreach (var row in rows)
        {
            var shapes = IofPictogram.RowShapes(row);
            Assert.NotNull(shapes);
            var runs = shapes!.OfType<IofPictogram.Shape.Dashed>().ToList();
            Assert.Equal(2, runs.Count);
            // Inner ends (next to the label centred at x = 4).
            var inner = runs.Select(r => r.Points.OrderBy(p => Math.Abs(p.X - 4)).First()).ToList();
            Assert.Equal(Math.Abs(inner[0].X - 4), Math.Abs(inner[1].X - 4), 9);
            // Each run is filled exactly with whole dashes: the dash pattern fits the run length.
            foreach (var r in runs)
            {
                double length = Math.Abs(r.Points[1].X - r.Points[0].X);
                double n = Math.Round((length + r.Gap) / (r.Dash + r.Gap));
                Assert.Equal(length, n * r.Dash + (n - 1) * r.Gap, 9);
            }
        }
    }
}

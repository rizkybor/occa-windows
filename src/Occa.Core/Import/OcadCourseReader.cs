using Occa.Core.Courses;
using Occa.Core.Geometry;

namespace Occa.Core.Import;

/// <summary>Result of reading courses from another file (<c>OcadCourseImport</c> on Mac).</summary>
public sealed class OcadCourseImport
{
    public List<ControlPoint> Controls { get; init; } = new();
    public List<Course> Courses { get; init; } = new();
    /// <summary>Controls that had OCAD description columns (re-enter them in the Descriptions tab).</summary>
    public int SkippedDescriptions { get; init; }
    /// <summary>Course entries referring to controls that are not in the file.</summary>
    public int MissingReferences { get; init; }
}

/// <summary>
/// Courses from an OCAD course setting file (.ocd, versions 10–2018), based on the OCAD file format documentation
/// (port of <c>OcadCourseReader.swift</c>):
///
/// - Control parameter strings (type 1, OCAD 10–11): "&lt;code&gt;\tY&lt;type&gt;…", type s = start, c = control, f = finish. The
///   string index links each control to its object, whose first point is the control position.
/// - OCAD 12+: the control string is stored in the control object itself (its "object string", type 1), e.g.
///   "1031\tYc\ta31\td5.011"; the code is parameter <c>a</c> (the leading text is OCAD's sort key).
/// - Course parameter strings (type 2): "&lt;name&gt;\ts&lt;start&gt;\tc&lt;code&gt;…\tf&lt;finish&gt;" in course order.
///
/// Control descriptions use OCAD's own numbering and are not converted (reported in <c>SkippedDescriptions</c>);
/// the course symbols themselves (701–799) are not imported as map objects.
/// </summary>
public static class OcadCourseReader
{
    /// <summary>Read the course setting part of an OCAD file; empty <c>Controls</c> when the file has no course setting data.</summary>
    public static OcadCourseImport Read(byte[] data)
    {
        var map = OcadReader.Read(data);
        if (map.RealWorldOffset is not { } offset) throw new OcadReadException(OcadReadErrorKind.NotGeoreferenced);
        var r = new OcadReader.Bytes(data);
        double a = map.GridAngleDeg * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
        Point2D World(double x, double y)
        {
            double mx = x / 1000 * map.Scale, my = y / 1000 * map.Scale;
            return new Point2D(offset.X + mx * ca - my * sa, offset.Y + mx * sa + my * ca);
        }

        // Object positions by 1-based object index (as used in the string index). Point objects only need their first
        // coordinate, right after the TElement header.
        int version = map.Version;
        int elementHeader = version >= 11 ? 56 : 40;
        var objectPositions = new Dictionary<int, Point2D>();
        var controlStrings = new List<(string Text, int Object)>();
        int index = 0;
        OcadReader.Walk((int)r.U32(12), r, 40, o =>
        {
            index++;
            int pos = (int)r.U32(o + 16);
            if (pos <= 0) return;
            int rx = r.I32(pos + elementHeader), ry = r.I32(pos + elementHeader + 4);
            objectPositions[index] = World((rx >> 8) / 100.0, (ry >> 8) / 100.0);
            // OCAD 12+: TElement nItem (+44), nText (+48), nObjectString (+50, 8-byte units), ObjectStringType (+54);
            // the object string (UTF-16) follows the coordinates and the text.
            if (version < 12 || r.U8(o + 30) != 1) return;
            int nItem = (int)r.U32(pos + 44), nText = r.I16(pos + 48), nString = r.I16(pos + 50);
            if (r.U8(pos + 54) != 1 || nString <= 0 || nItem < 1 || nItem >= 100_000 || nText < 0) return;
            var text = r.Utf16(pos + elementHeader + (nItem + nText) * 8, nString * 4);
            if (text.Length > 0) controlStrings.Add((text, index));
        });

        var courseStrings = new List<string>();
        OcadReader.Walk((int)r.U32(32), r, 16, o =>
        {
            int pos = (int)r.U32(o), len = (int)r.U32(o + 4), type = r.I32(o + 8), obj = r.I32(o + 12);
            if (pos <= 0 || len <= 0) return;
            if (type == 1) controlStrings.Add((r.String(pos, len), obj));
            if (type == 2) courseStrings.Add(r.String(pos, len));
        });

        var controls = new List<ControlPoint>();
        var byCode = new Dictionary<string, Guid>();
        int described = 0;
        foreach (var (text, obj) in controlStrings)
        {
            var par = OcadReader.ParseParams(text);
            // The code: parameter a when present (OCAD 12+), otherwise the leading text.
            var name = (par.TryGetValue("a", out var av) ? av : text.Split('\t')[0]).Trim();
            ControlKind kind;
            switch (par.TryGetValue("Y", out var y) ? y : null)
            {
                case "s": kind = ControlKind.Start; break;
                case "c": kind = ControlKind.Control; break;
                case "f": kind = ControlKind.Finish; break;
                default: continue;   // marked route, crossing point, … not supported yet
            }
            if (name.Length == 0 || byCode.ContainsKey(name) || !objectPositions.TryGetValue(obj, out var position)) continue;
            int? code = kind == ControlKind.Control && int.TryParse(name, out var n) ? n : null;
            var c = new ControlPoint(kind, position, code);
            if (new[] { "c", "d", "e", "f", "g", "h" }.Any(par.ContainsKey)) described++;
            controls.Add(c);
            byCode[name] = c.Id;
        }

        var courses = new List<Course>();
        int missing = 0;
        foreach (var text in courseStrings)
        {
            var parts = text.Split('\t');
            var name = parts[0].Trim();
            var ids = new List<Guid>();
            foreach (var part in parts.Skip(1))
            {
                if (part.Length == 0 || !"scf".Contains(part[0])) continue;
                var reference = part[1..];
                if (byCode.TryGetValue(reference, out var id)) ids.Add(id); else missing++;
            }
            if (name.Length == 0 || ids.Count == 0) continue;
            courses.Add(new Course(name, ids));
        }
        return new OcadCourseImport { Controls = controls, Courses = courses, SkippedDescriptions = described, MissingReferences = missing };
    }
}

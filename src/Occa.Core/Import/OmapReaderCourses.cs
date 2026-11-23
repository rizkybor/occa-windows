using System.Globalization;
using Occa.Core.Courses;
using Occa.Core.Geometry;

namespace Occa.Core.Import;

public static partial class OmapReader
{
    /// <summary>Start (701), control (702), and finish (706) point symbols drawn on an OOM map, as controls (no codes or order).</summary>
    public static OcadCourseImport CourseControls(byte[] data, UtmZone zone)
    {
        var p = Parse(data);
        if (p.RefLatLon is not { } ll || p.Scale <= 0) throw OmapReadException.NotGeo();
        var reference = zone.Project(ll);
        double k = p.Scale / 1_000_000.0 / Math.Max(p.GridScaleFactor, 0.5);
        double g = p.GrivationDeg * Math.PI / 180, cg = Math.Cos(g), sg = Math.Sin(g);
        var mapRef = p.MapRef;
        Point2D World(double x, double y)
        {
            double dx = (x - mapRef.X) * k, dy = -(y - mapRef.Y) * k;
            return new Point2D(reference.X + dx * cg + dy * sg, reference.Y - dx * sg + dy * cg);
        }
        var controls = new List<ControlPoint>();
        foreach (var o in p.Objects)
        {
            if (o.Type != 0 || o.Coords.Count == 0 || !p.Symbols.TryGetValue(o.Symbol, out var sym)) continue;
            if (!double.TryParse(sym.Code, NumberStyles.Float, CultureInfo.InvariantCulture, out var code)) continue;
            ControlKind kind;
            switch ((int)code)
            {
                case 701: kind = ControlKind.Start; break;
                case 702: kind = ControlKind.Control; break;
                case 706: kind = ControlKind.Finish; break;
                default: continue;
            }
            controls.Add(new ControlPoint(kind, World(o.Coords[0].X, o.Coords[0].Y)));
        }
        return new OcadCourseImport { Controls = controls };
    }
}

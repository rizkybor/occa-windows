using System.Globalization;
using System.Text;
using Occa.Core.Export;
using Occa.Core.Geometry;
using Occa.Core.Sheet;

namespace Occa.Core.Courses;

/// <summary>
/// Export courses to IOF XML 3.0 <c>CourseData</c> (read by MeOS, OE/SportSoftware, SI Config/SportIdent, Purple Pen,
/// OCAD): control positions (lat/lon + map mm), order, leg lengths, course length/climb, number of competitors.
/// Port of <c>IOFXMLExporter</c>; same text as the Mac.
/// </summary>
public static class IofXmlExporter
{
    public static byte[] CourseData(CourseSet set, MapFrame frame, SheetSpec sheet, UtmZone zone, DateTime? createTime = null, string creator = AppInfo.Name)
    {
        var e = frame.Extent;
        double mmPerM = 1000.0 / sheet.ScaleDenominator;
        string F(double v, int d = 2) => GeoPdfExporter.N(v, d);
        var time = (createTime ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string Type(ControlKind k) => k switch { ControlKind.Start => "Start", ControlKind.Control => "Control", _ => "Finish" };

        var x = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        x.Append("<CourseData xmlns=\"http://www.orienteering.org/datastandard/3.0\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ");
        x.Append($"iofVersion=\"3.0\" createTime=\"{time}\" creator=\"{Esc(creator)}\">\n");
        x.Append($"  <Event>\n    <Name>{Esc(set.EventTitle.Length == 0 ? AppInfo.Name : set.EventTitle)}</Name>\n  </Event>\n");
        x.Append("  <RaceCourseData>\n");
        // Map: mm coordinates from the bottom-left corner of the map area, y axis up (IOF XML 3.0 convention).
        x.Append($"    <Map>\n      <Scale>{sheet.ScaleDenominator}</Scale>\n");
        x.Append($"      <MapPositionTopLeft x=\"0\" y=\"{F(frame.HeightM * mmPerM)}\" unit=\"mm\"/>\n");
        x.Append($"      <MapPositionBottomRight x=\"{F(frame.WidthM * mmPerM)}\" y=\"0\" unit=\"mm\"/>\n    </Map>\n");

        foreach (var c in set.Controls)
        {
            var ll = zone.Unproject(c.Position);
            x.Append($"    <Control type=\"{Type(c.Kind)}\">\n");
            x.Append($"      <Id>{Esc(c.IofId(set))}</Id>\n");
            x.Append($"      <Position lng=\"{F(ll.Lon, 7)}\" lat=\"{F(ll.Lat, 7)}\"/>\n");
            x.Append($"      <MapPosition x=\"{F((c.Position.X - e.MinX) * mmPerM)}\" y=\"{F((c.Position.Y - e.MinY) * mmPerM)}\" unit=\"mm\"/>\n");
            x.Append("    </Control>\n");
        }

        foreach (var course in set.Courses)
        {
            var list = set.ControlsOf(course);
            x.Append($"    <Course>\n      <Name>{Esc(course.Name)}</Name>\n");
            if (course.Kind == CourseKind.Normal) x.Append($"      <Length>{(long)Math.Round(set.Length(course), MidpointRounding.AwayFromZero)}</Length>\n");
            if (course.ClimbM is int climb) x.Append($"      <Climb>{climb}</Climb>\n");
            ControlPoint? previous = null;
            foreach (var c in list)
            {
                x.Append($"      <CourseControl type=\"{Type(c.Kind)}\">\n        <Control>{Esc(c.IofId(set))}</Control>\n");
                if (course.Kind == CourseKind.Normal && previous is not null)
                    x.Append($"        <LegLength>{(long)Math.Round(previous.Position.DistanceTo(c.Position), MidpointRounding.AwayFromZero)}</LegLength>\n");
                x.Append("      </CourseControl>\n");
                previous = c;
            }
            if (course.Competitors > 0) x.Append($"      <NumberOfCompetitors>{course.Competitors}</NumberOfCompetitors>\n");
            x.Append("    </Course>\n");
        }
        x.Append("  </RaceCourseData>\n</CourseData>\n");
        return Encoding.UTF8.GetBytes(x.ToString());
    }

    internal static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

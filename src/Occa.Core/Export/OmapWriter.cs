using System.Globalization;
using System.Text;
using Occa.Core.Geometry;
using Occa.Core.Vector;

namespace Occa.Core.Export;

/// <summary>
/// OpenOrienteering Mapper (.omap) XML writer, our own (port of OmapWriter.swift). Map coordinates = micrometres on
/// paper, y down, relative to the reference point (0, 0) paired with the frame centre in UTM.
/// </summary>
public static class OmapWriter
{
    public const int CurveStartFlag = 1, ClosePointFlag = 2, HolePointFlag = 16;

    public static (int X, int Y) MapCoordinate(Point2D p, Point2D reference, int scale)
    {
        double f = 1_000_000.0 / scale;
        return ((int)Math.Round((p.X - reference.X) * f), (int)Math.Round(-(p.Y - reference.Y) * f));
    }

    private static string N(double v, int d) => Num.F(v, d);

    public static string Document(VectorExportRequest req)
    {
        var strokes = req.ExportedStrokes;
        var features = req.ExportedFeatures;
        var table = new SketchSymbolTable(strokes, features);
        var colors = OrderedColors(table.Symbols, features);
        var reference = req.Frame.Center;
        var refLL = req.Zone.Unproject(reference);
        int scale = req.Sheet.ScaleDenominator;
        var zone = req.Zone;
        var proj = $"+proj=utm +zone={zone.Number}{(zone.IsNorth ? "" : " +south")} +datum=WGS84 +units=m +no_defs";

        var x = new StringBuilder();
        x.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        x.Append("<map xmlns=\"http://openorienteering.org/apps/mapper/xml/v2\" version=\"9\">\n");
        x.Append($"<notes>Created with {AppInfo.Name}. Symbols 990.x are sketches; switch them to ISOM symbols via Change symbol.</notes>\n");
        x.Append($"<georeferencing scale=\"{scale}\" grid_scale_factor=\"1\" auxiliary_scale_factor=\"1\" declination=\"0\" grivation=\"0\">\n");
        x.Append("<ref_point x=\"0\" y=\"0\"/>\n");
        x.Append($"<projected_crs id=\"UTM\"><spec language=\"PROJ.4\">{proj}</spec><parameter>{zone.Number} {(zone.IsNorth ? "N" : "S")}</parameter><ref_point x=\"{N(reference.X, 3)}\" y=\"{N(reference.Y, 3)}\"/></projected_crs>\n");
        x.Append($"<geographic_crs id=\"Geographic coordinates\"><spec language=\"PROJ.4\">+proj=latlong +datum=WGS84</spec><ref_point_deg lat=\"{N(refLL.Lat, 8)}\" lon=\"{N(refLL.Lon, 8)}\"/></geographic_crs>\n");
        x.Append("</georeferencing>\n");
        x.Append($"<colors count=\"{colors.Count}\">\n");
        for (int i = 0; i < colors.Count; i++)
        {
            var hex = colors[i];
            var rgb = ColorHex.Rgb(hex) ?? (0, 0, 0);
            var spec = IofColors.Cmyk(hex);
            x.Append($"<color priority=\"{i}\" name=\"{Escape(ColorName(hex))}\" c=\"{N(spec[0], 3)}\" m=\"{N(spec[1], 3)}\" y=\"{N(spec[2], 3)}\" k=\"{N(spec[3], 3)}\" opacity=\"1\"><spotcolors/><cmyk method=\"custom\"/><rgb method=\"custom\" r=\"{N(rgb.R, 3)}\" g=\"{N(rgb.G, 3)}\" b=\"{N(rgb.B, 3)}\"/></color>\n");
        }
        x.Append($"</colors>\n<barrier version=\"6\" required=\"0.6.0\">\n<symbols count=\"{table.Symbols.Count}\">\n");
        foreach (var s in table.Symbols)
        {
            int color = Math.Max(colors.IndexOf(s.ColorHex), 0);
            int um = (int)Math.Round(s.WidthMM * 1000);
            if (s.IsArea)
            {
                var patterns = s.Patterns ?? new List<AreaPattern>();
                int inner = SymbolOrnaments.IsPatternOnly(s.ColorHex, patterns) ? -1 : color;
                x.Append($"<symbol type=\"4\" id=\"{s.Index}\" code=\"{s.Code}\" name=\"{Escape(s.Name)}\"><area_symbol inner_color=\"{inner}\" min_area=\"0\" patterns=\"{patterns.Count}\">{OmapPatterns(patterns, colors)}</area_symbol></symbol>\n");
            }
            else if (s.IsPoint)
            {
                x.Append($"<symbol type=\"1\" id=\"{s.Index}\" code=\"{s.Code}\" name=\"{Escape(s.Name)}\"><point_symbol rotatable=\"true\" inner_radius=\"{um / 2}\" inner_color=\"{color}\" outer_width=\"0\" outer_color=\"-1\" elements=\"0\"/></symbol>\n");
            }
            else
            {
                var d = (s.DashMM ?? new List<double>()).Select(v => (int)Math.Round(v * 1000)).ToList();
                bool dashed = d.Count == 2 || d.Count == 4;
                int dash = dashed ? d[0] : 4000, brk = dashed ? d[^1] : 1000;
                int group = d.Count == 4 ? 2 : 1, inGroup = d.Count == 4 ? d[1] : 500;
                x.Append($"<symbol type=\"2\" id=\"{s.Index}\" code=\"{s.Code}\" name=\"{Escape(s.Name)}\"><line_symbol color=\"{color}\" line_width=\"{um}\" minimum_length=\"0\" join_style=\"2\" cap_style=\"1\" start_offset=\"0\" end_offset=\"0\" dashed=\"{(dashed ? "true" : "false")}\" segment_length=\"4000\" end_length=\"0\" show_at_least_one_symbol=\"true\" minimum_mid_symbol_count=\"0\" minimum_mid_symbol_count_when_closed=\"0\" dash_length=\"{dash}\" break_length=\"{brk}\" dashes_in_group=\"{group}\" in_group_break_length=\"{inGroup}\" half_outer_dashes=\"false\" mid_symbols_per_spot=\"1\" mid_symbol_distance=\"0\"/></symbol>\n");
            }
        }
        x.Append($"</symbols>\n<parts count=\"1\" current=\"0\"><part name=\"Sketch\"><objects count=\"{strokes.Count + features.Count}\">\n");
        foreach (var s in strokes)
        {
            if (table.SymbolOf(s) is not { } sym) continue;
            var coords = s.Points.Select(p => { var c = MapCoordinate(p, reference, scale); return $"{c.X} {c.Y};"; }).ToList();
            if (sym.IsPoint) x.Append($"<object type=\"0\" symbol=\"{sym.Index}\" rotation=\"0\"><coords count=\"1\">{coords[0]}</coords></object>\n");
            else x.Append($"<object type=\"1\" symbol=\"{sym.Index}\"><coords count=\"{coords.Count}\">{string.Concat(coords)}</coords></object>\n");
        }
        foreach (var f in features)
        {
            if (table.SymbolOf(f) is not { } sym) continue;
            var coords = FeatureCoords(f, reference, scale);
            if (sym.IsPoint)
            {
                var rotation = N(-f.RotationDegrees * Math.PI / 180, 6);
                x.Append($"<object type=\"0\" symbol=\"{sym.Index}\" rotation=\"{rotation}\"><coords count=\"1\">{coords[0]}</coords></object>\n");
                continue;
            }
            x.Append($"<object type=\"1\" symbol=\"{sym.Index}\"><coords count=\"{coords.Count}\">{string.Concat(coords)}</coords></object>\n");
        }
        x.Append("</objects></part></parts>\n");
        x.Append(Templates(req, proj));
        x.Append("\n</barrier>\n</map>\n");
        return x.ToString();
    }

    /// <summary>Bezier object coordinates in OOM format: "x y flag;" per point; holes follow the outer ring as further parts.</summary>
    public static List<string> FeatureCoords(Feature f, Point2D reference, int scale)
    {
        string C(Point2D p, int flag = 0)
        {
            var m = MapCoordinate(p, reference, scale);
            return flag == 0 ? $"{m.X} {m.Y};" : $"{m.X} {m.Y} {flag};";
        }
        var parts = f.RingFeatures.Where(r => r.Segments.Count > 0).ToList();
        if (parts.Count == 0) return f.Nodes.Count > 0 ? new List<string> { C(f.Nodes[0].Point) } : new List<string>();
        var result = new List<string>();
        for (int k = 0; k < parts.Count; k++)
        {
            var segs = parts[k].Segments;
            foreach (var s in segs)
            {
                if (s.IsCurve) { result.Add(C(s.P0, CurveStartFlag)); result.Add(C(s.C1)); result.Add(C(s.C2)); }
                else result.Add(C(s.P0));
            }
            int hole = k < parts.Count - 1 ? HolePointFlag : 0;
            result.Add(C(f.IsClosed ? segs[0].P0 : segs[^1].P3, (f.IsClosed ? ClosePointFlag : 0) | hole));
        }
        return result;
    }

    /// <summary>Unique colours sorted by the Colour Hierarchy of the objects using them (topmost first); sketch colours on top.</summary>
    public static List<string> OrderedColors(IReadOnlyList<SketchSymbol> symbols, IReadOnlyList<Feature> features)
    {
        var priority = new Dictionary<string, int>();
        foreach (var f in features)
        {
            var key = f.ColorHex.ToUpperInvariant();
            int p = ColorHierarchy.Priority(f);
            priority[key] = Math.Min(priority.TryGetValue(key, out var old) ? old : int.MaxValue, p);
        }
        return UniqueColors(symbols).Select((c, i) => (c, i))
            .OrderBy(x => priority.TryGetValue(x.c.ToUpperInvariant(), out var p) ? p : -1).ThenBy(x => x.i).Select(x => x.c).ToList();
    }

    public static List<string> UniqueColors(IReadOnlyList<SketchSymbol> symbols)
    {
        var seen = new List<string>();
        foreach (var s in symbols)
            foreach (var c in new[] { s.ColorHex }.Concat((s.Patterns ?? new List<AreaPattern>()).Select(p => p.ColorHex.ToUpperInvariant())))
                if (!seen.Contains(c)) seen.Add(c);
        return seen;
    }

    /// <summary>OOM pattern elements (µm; radians, 0 = east–west) for OCCA patterns (mm; 0° = north–south).</summary>
    public static string OmapPatterns(IReadOnlyList<AreaPattern> patterns, IReadOnlyList<string> colors)
    {
        var sb = new StringBuilder();
        foreach (var p in patterns)
        {
            int c = Math.Max(colors.ToList().IndexOf(p.ColorHex.ToUpperInvariant()), 0);
            var rad = N((90 - p.AngleDeg) * Math.PI / 180, 6);
            int sp = (int)Math.Round(p.SpacingMM * 1000), size = (int)Math.Round(p.SizeMM * 1000);
            switch (p.PatternKind)
            {
                case AreaPattern.Kind.Lines:
                    sb.Append($"<pattern type=\"1\" angle=\"{rad}\" line_spacing=\"{sp}\" line_offset=\"0\" offset_along_line=\"0\" color=\"{c}\" line_width=\"{size}\"/>");
                    break;
                case AreaPattern.Kind.Dots:
                case AreaPattern.Kind.Holes:
                    sb.Append($"<pattern type=\"2\" angle=\"{rad}\" line_spacing=\"{sp}\" line_offset=\"0\" offset_along_line=\"0\" point_distance=\"{sp}\">");
                    sb.Append($"<symbol type=\"1\" code=\"\"><point_symbol rotatable=\"true\" inner_radius=\"{size / 2}\" inner_color=\"{c}\" outer_width=\"0\" outer_color=\"-1\" elements=\"0\"/></symbol></pattern>");
                    break;
                default:
                {
                    double h = size * Math.Sqrt(3) / 2;
                    var pts = new[] { (0.0, -2 * h / 3), (-size / 2.0, h / 3), (size / 2.0, h / 3) };
                    var coords = string.Concat(pts.Select(q => $"{(int)Math.Round(q.Item1)} {(int)Math.Round(q.Item2)};"));
                    sb.Append($"<pattern type=\"2\" angle=\"0\" line_spacing=\"{sp}\" line_offset=\"0\" offset_along_line=\"0\" point_distance=\"{sp}\">");
                    sb.Append("<symbol type=\"1\" code=\"\"><point_symbol rotatable=\"true\" inner_radius=\"0\" inner_color=\"-1\" outer_width=\"0\" outer_color=\"-1\" elements=\"1\">");
                    sb.Append($"<element><symbol type=\"4\" code=\"\"><area_symbol inner_color=\"{c}\" min_area=\"0\" patterns=\"0\"/></symbol>");
                    sb.Append($"<object type=\"1\"><coords count=\"3\">{coords}</coords></object></element></point_symbol></symbol></pattern>");
                    break;
                }
            }
        }
        return sb.ToString();
    }

    public static string ColorName(string hex) => PenPreset.Isom.FirstOrDefault(p => string.Equals(p.ColorHex, hex, StringComparison.OrdinalIgnoreCase))?.Name ?? hex;

    public static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>Template list: the basemap image (when exported) as a georeferenced image template behind the map.</summary>
    public static string Templates(VectorExportRequest req, string proj)
    {
        const string defaults = "<defaults use_meters_per_pixel=\"true\" meters_per_pixel=\"0\" dpi=\"0\" scale=\"0\"/>";
        if (req.Background is not { } bg) return $"<templates count=\"0\" first_front_template=\"0\">{defaults}</templates>";
        var file = Escape($"{req.BaseName}_basemap.{bg.FileExtension}");
        return $"<templates count=\"1\" first_front_template=\"1\"><template type=\"TemplateImage\" open=\"true\" name=\"{file}\" path=\"{file}\" relpath=\"{file}\" georef=\"true\" group=\"-1\"><crs_spec language=\"PROJ.4\">{Escape(proj)}</crs_spec></template>{defaults}</templates>";
    }
}

/// <summary>Spec CMYK of OCCA's colours (IOF table × screen) by screen hex; other colours converted from RGB.</summary>
public static class IofColors
{
    private static readonly Lazy<Dictionary<string, double[]>> ByHex = new(() =>
    {
        var map = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ColorHierarchy.ColorRows) map.TryAdd(row.Hex, row.Cmyk.Select(v => v / 100).ToArray());
        foreach (var c in StandardSymbolSets.Isinom.Colors) map.TryAdd(c.Hex, c.Cmyk.Select(v => v * c.Tint).ToArray());
        return map;
    });

    /// <summary>Spec CMYK (0–1) of a colour OCCA's sets draw with; null = not an OCCA colour.</summary>
    public static double[]? SpecCmyk(string hex) => ByHex.Value.TryGetValue(ColorHex.Normalized(hex), out var v) ? v : null;

    /// <summary>Spec CMYK when known, else RGB → CMYK.</summary>
    public static double[] Cmyk(string hex) => SpecCmyk(hex) ?? FromHex(hex);

    public static double[] FromHex(string hex)
    {
        var rgb = ColorHex.Rgb(hex);
        if (rgb is null) return new[] { 0.0, 0, 0, 1 };
        var (r, g, b) = rgb.Value;
        double k = 1 - Math.Max(r, Math.Max(g, b));
        if (k >= 1) return new[] { 0.0, 0, 0, 1 };
        double R3(double x) => Math.Round(x * 1000) / 1000;
        return new[] { R3((1 - r - k) / (1 - k)), R3((1 - g - k) / (1 - k)), R3((1 - b - k) / (1 - k)), R3(k) };
    }
}

/// <summary>DXF R12 (AC1009) ASCII: UTM metre coordinates as-is, one layer per sketch symbol.</summary>
public static class DxfWriter
{
    public static string Document(VectorExportRequest req)
    {
        var strokes = req.ExportedStrokes;
        var features = req.ExportedFeatures;
        var table = new SketchSymbolTable(strokes, features);
        var e = req.Frame.Extent;
        var lines = new List<string>();
        void G(int code, string value) { lines.Add(code.ToString()); lines.Add(value); }

        G(0, "SECTION"); G(2, "HEADER");
        G(9, "$ACADVER"); G(1, "AC1009");
        G(9, "$EXTMIN"); G(10, Num.F(e.MinX, 4)); G(20, Num.F(e.MinY, 4)); G(30, "0.0");
        G(9, "$EXTMAX"); G(10, Num.F(e.MaxX, 4)); G(20, Num.F(e.MaxY, 4)); G(30, "0.0");
        G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "TABLES");
        G(0, "TABLE"); G(2, "LAYER"); G(70, (table.Symbols.Count + 1).ToString());
        foreach (var (name, color) in new[] { ("FRAME", 8) }.Concat(table.Symbols.Select(s => (s.DxfLayer, Aci(s.ColorHex)))))
        {
            G(0, "LAYER"); G(2, name); G(70, "0"); G(62, color.ToString()); G(6, "CONTINUOUS");
        }
        G(0, "ENDTAB"); G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "ENTITIES");
        Polyline("FRAME", req.Frame.Corners, true, G);
        foreach (var s in strokes)
        {
            if (table.SymbolOf(s) is not { } sym) continue;
            if (sym.IsPoint && s.Points.Count > 0) { var p = s.Points[0]; G(0, "POINT"); G(8, sym.DxfLayer); G(10, Num.F(p.X, 4)); G(20, Num.F(p.Y, 4)); G(30, "0.0"); }
            else Polyline(sym.DxfLayer, s.Points, false, G);
        }
        double tolerance = req.Sheet.GroundMeters(0.05);
        foreach (var f in features)
        {
            if (table.SymbolOf(f) is not { } sym) continue;
            if (f.Kind == FeatureKind.Point && f.Nodes.Count > 0) { var p = f.Nodes[0].Point; G(0, "POINT"); G(8, sym.DxfLayer); G(10, Num.F(p.X, 4)); G(20, Num.F(p.Y, 4)); G(30, "0.0"); continue; }
            foreach (var ring in f.RingFeatures)
            {
                var pts = ring.Flattened(tolerance);
                if (f.IsClosed && pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
                Polyline(sym.DxfLayer, pts, f.IsClosed, G);
            }
        }
        G(0, "ENDSEC"); G(0, "EOF");
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static void Polyline(string layer, IReadOnlyList<Point2D> points, bool closed, Action<int, string> g)
    {
        g(0, "POLYLINE"); g(8, layer); g(66, "1"); g(10, "0.0"); g(20, "0.0"); g(30, "0.0"); g(70, closed ? "1" : "0");
        foreach (var p in points) { g(0, "VERTEX"); g(8, layer); g(10, Num.F(p.X, 4)); g(20, Num.F(p.Y, 4)); g(30, "0.0"); }
        g(0, "SEQEND"); g(8, layer);
    }

    public static int Aci(string hex) => hex.ToUpperInvariant() switch
    {
        "#000000" => 7, "#D15C00" => 30, "#00A0E6" => 5, "#FFBA35" => 2, "#3EB54A" => 3, "#E30613" => 1, _ => 7,
    };
}

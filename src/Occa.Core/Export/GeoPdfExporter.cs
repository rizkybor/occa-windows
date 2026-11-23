using System.Globalization;
using System.Text;
using Occa.Core.Courses;
using Occa.Core.Geometry;
using Occa.Core.Project;
using Occa.Core.Sheet;
using Occa.Core.Vector;

namespace Occa.Core.Export;

/// <summary>What the GeoPDF export draws (port of <c>GeoPDFRequest</c>).</summary>
public sealed class GeoPdfRequest
{
    public required string Title { get; init; }
    public required MapFrame Frame { get; init; }
    public required SheetSpec Sheet { get; init; }
    public required UtmZone Zone { get; init; }
    public List<Stroke> Strokes { get; init; } = new();
    public List<Feature> Features { get; init; } = new();
    public NorthGrid? Grid { get; init; }
    /// <summary>Course overprint (purple) on top of the map.</summary>
    public CourseOverprint? Overprint { get; init; }
    /// <summary>Control description sheet (on the map per <see cref="PrintLayout"/>, and/or on a second page).</summary>
    public DescriptionSheet? Descriptions { get; init; }
    /// <summary>Single-page race map layout; null = descriptions on a second page (old format).</summary>
    public CoursePrintLayout? PrintLayout { get; init; }
    /// <summary>Contour interval (informational; not drawn since the title box was removed).</summary>
    public double? ContourIntervalM { get; init; }
    /// <summary>Course object image file contents (JPG/PNG) by file name.</summary>
    public Dictionary<string, byte[]> Images { get; init; } = new();
    /// <summary>Georeferenced basemap image (JPEG) under the map objects; null = none.</summary>
    public ExportBackground? Background { get; init; }
    /// <summary>Small credit in the bottom-left corner of the map area (Starter plan); null = none.</summary>
    public string? Watermark { get; init; }
    /// <summary>Colour priority at which the lower purple course overprint is painted (see <c>RasterExportRequest</c>).</summary>
    public int OverprintLowerPriority { get; init; } = ColorHierarchy.LowerPurplePriority();
}

/// <summary>
/// Paper-sized vector PDF (A4 + margin) with Geospatial PDF georeferencing (ISO 32000-2 / Adobe extension): the page
/// carries /VP → /Measure /GEO with lat/lon control points at the four corners of the map area and the project CRS WKT.
/// Written by hand, byte-compatible with the Mac's GeoPDFExporter: map symbols and the course overprint as DeviceCMYK
/// with the IOF Rev 4 values, sketches, north lines and basemaps in RGB, objects in the Colour Hierarchy order (NFR-4).
/// The course goes into the colour order (lower purple below black, blue and brown 100 %; upper purple on top), and
/// control descriptions are drawn on the map and/or on a second page.
/// </summary>
public static class GeoPdfExporter
{
    public const double PtPerMM = 72 / 25.4;

    public static void Export(GeoPdfRequest req, string path) => ProjectPackage.WriteAtomic(path, Document(req));

    /// <summary>Full page size (pt) and the map area box inside it (pt, origin bottom-left).</summary>
    public static ((double W, double H) Page, (double X0, double Y0, double X1, double Y1) Map) Layout(SheetSpec sheet)
    {
        double m = sheet.MarginMM * PtPerMM;
        double w = (sheet.MappingWidthMM + 2 * sheet.MarginMM) * PtPerMM;
        double h = (sheet.MappingHeightMM + 2 * sheet.MarginMM) * PtPerMM;
        return ((w, h), (m, m, w - m, h - m));
    }

    public static byte[] Document(GeoPdfRequest req)
    {
        var (page, box) = Layout(req.Sheet);
        var e = req.Frame.Extent;
        double k = 1000.0 / req.Sheet.ScaleDenominator * PtPerMM;
        string Pt(Point2D p) => $"{N(box.X0 + (p.X - e.MinX) * k)} {N(box.Y0 + (p.Y - e.MinY) * k)}";
        Point2D PagePoint(Point2D p) => new(box.X0 + (p.X - e.MinX) * k, box.Y0 + (p.Y - e.MinY) * k);

        var c = new StringBuilder();
        c.Append("q\n");
        c.Append($"{N(box.X0)} {N(box.Y0)} {N(box.X1 - box.X0)} {N(box.Y1 - box.Y0)} re W n\n");
        c.Append("1 J 1 j\n");
        var bg = req.Background is { Corners.Length: 4, ImageData.Length: > 0 } b && b.FileExtension != "png" ? b : null;
        if (bg is not null)
        {
            var tl = PagePoint(bg.Corners[0]); var br = PagePoint(bg.Corners[2]); var bl = PagePoint(bg.Corners[3]);
            c.Append($"q /GSBg gs {N(br.X - bl.X)} {N(br.Y - bl.Y)} {N(tl.X - bl.X)} {N(tl.Y - bl.Y)} {N(bl.X)} {N(bl.Y)} cm /ImBg Do Q\n");
        }
        var visible = req.Features.Where(f => f.IsVisible && f.Nodes.Count >= (f.Kind == FeatureKind.Point ? 1 : 2)).ToList();

        string PathOf(Feature f)
        {
            var s = new StringBuilder();
            foreach (var ring in f.RingFeatures)
            {
                if (ring.Nodes.Count == 0) continue;
                s.Append($"{Pt(ring.Nodes[0].Point)} m\n");
                foreach (var seg in ring.Segments) s.Append(seg.IsCurve ? $"{Pt(seg.C1)} {Pt(seg.C2)} {Pt(seg.P3)} c\n" : $"{Pt(seg.P3)} l\n");
                if (f.ClosesPath) s.Append("h\n");
            }
            return s.ToString();
        }
        string Rgb(string hex) { var v = ColorHex.Rgb(hex) ?? (0, 0, 0); return $"{N(v.R)} {N(v.G)} {N(v.B)}"; }
        string FillOp(string hex, bool cmyk = true) => cmyk ? $"{CmykString(hex)} k" : $"{Rgb(hex)} rg";
        string StrokeOp(string hex, bool cmyk = true) => cmyk ? $"{CmykString(hex)} K" : $"{Rgb(hex)} RG";

        void DrawPoint(Feature f, int? level)
        {
            if (f.Nodes.Count == 0) return;
            var center = f.Nodes[0].Point;
            string At(Point2D mm) => $"{N(box.X0 + (center.X - e.MinX) * k + mm.X * PtPerMM)} {N(box.Y0 + (center.Y - e.MinY) * k + mm.Y * PtPerMM)}";
            foreach (var el in f.ScaledPointElements.DrawingOrder())
            {
                if (level is int l && el.Priority != l) continue;
                var o = el.Points.Count > 0 ? el.Points[0] : Point2D.Zero;
                switch (el.ElementKind)
                {
                    case PointElement.Kind.Dot:
                        c.Append($"{FillOp(el.ColorHex)}\n{CirclePath(o, el.DiameterMM / 2, At)}f\n");
                        break;
                    case PointElement.Kind.Circle:
                        c.Append($"{StrokeOp(el.ColorHex)} {N(el.LineWidthMM * PtPerMM)} w\n{CirclePath(o, el.DiameterMM / 2, At)}S\n");
                        break;
                    default:
                        if (el.Points.Count == 0) continue;
                        var s = new StringBuilder($"{At(el.Points[0])} m\n");
                        foreach (var q in el.Points.Skip(1)) s.Append($"{At(q)} l\n");
                        var ends = el.FlatEnds == true ? "0 J 0 j " : "";
                        c.Append(el.ElementKind == PointElement.Kind.Area ? $"{FillOp(el.ColorHex)}\n{s}h f\n"
                            : $"{ends}{StrokeOp(el.ColorHex)} {N(el.LineWidthMM * PtPerMM)} w\n{s}S\n{(ends.Length == 0 ? "" : "1 J 1 j\n")}");
                        break;
                }
            }
        }

        string Marks(IEnumerable<OrnamentMark> list)
        {
            var s = new StringBuilder("0 J 0 j [] 0 d\n");
            foreach (var m in list)
            {
                switch (m)
                {
                    case OrnamentMark.Line l:
                        s.Append($"{StrokeOp(l.ColorHex)} {N(l.WidthMM * PtPerMM)} w {N(l.A.X)} {N(l.A.Y)} m {N(l.B.X)} {N(l.B.Y)} l S\n");
                        break;
                    case OrnamentMark.Polyline pl when pl.Points.Count > 1:
                        s.Append($"{StrokeOp(pl.ColorHex)} {N(pl.WidthMM * PtPerMM)} w {N(pl.Points[0].X)} {N(pl.Points[0].Y)} m ");
                        foreach (var q in pl.Points.Skip(1)) s.Append($"{N(q.X)} {N(q.Y)} l ");
                        s.Append("S\n");
                        break;
                    case OrnamentMark.Dot d:
                    {
                        double r = d.DiameterMM * PtPerMM / 2, kk = r * 0.5523;
                        var p = d.Center;
                        s.Append($"{FillOp(d.ColorHex)} {N(p.X + r)} {N(p.Y)} m ");
                        s.Append($"{N(p.X + r)} {N(p.Y + kk)} {N(p.X + kk)} {N(p.Y + r)} {N(p.X)} {N(p.Y + r)} c ");
                        s.Append($"{N(p.X - kk)} {N(p.Y + r)} {N(p.X - r)} {N(p.Y + kk)} {N(p.X - r)} {N(p.Y)} c ");
                        s.Append($"{N(p.X - r)} {N(p.Y - kk)} {N(p.X - kk)} {N(p.Y - r)} {N(p.X)} {N(p.Y - r)} c ");
                        s.Append($"{N(p.X + kk)} {N(p.Y - r)} {N(p.X + r)} {N(p.Y - kk)} {N(p.X + r)} {N(p.Y)} c f\n");
                        break;
                    }
                    case OrnamentMark.Polygon pg when pg.Points.Count > 0:
                        s.Append($"{FillOp(pg.ColorHex)} {N(pg.Points[0].X)} {N(pg.Points[0].Y)} m ");
                        foreach (var q in pg.Points.Skip(1)) s.Append($"{N(q.X)} {N(q.Y)} l ");
                        s.Append("h f\n");
                        break;
                }
            }
            return s.ToString();
        }

        // Course object images → XObject /Im0, /Im1, …; transparent fills → one ExtGState per opacity.
        var imageNames = new Dictionary<string, string>();
        var opacities = new List<double>();
        if (req.Overprint is { } op)
        {
            foreach (var m in op.Marks)
            {
                if (m is CourseMark.Image img && !imageNames.ContainsKey(img.Name) && req.Images.ContainsKey(img.Name)) imageNames[img.Name] = $"Im{imageNames.Count}";
                if (m is CourseMark.Fill fill && !opacities.Contains(fill.Opacity)) opacities.Add(fill.Opacity);
            }
        }
        // The course in the colour order: lower purple below black, blue and brown 100 % (painted when the map's paint
        // order passes its level), upper purple after the map, grid and sketch.
        var levels = req.Overprint?.Levels;
        bool lowerDone = levels?.Lower.IsEmpty ?? true;
        void DrawLower()
        {
            if (lowerDone || levels is null) return;
            lowerDone = true;
            c.Append(OverprintContent(levels.Value.Lower, Pt, k, CmykString, imageNames, opacities)).Append("1 J 1 j [] 0 d\n");
        }

        foreach (var paint in ColorHierarchy.PaintOrder(visible))
        {
            if (paint.Priority < req.OverprintLowerPriority) DrawLower();
            var f = visible[paint.Index];
            switch (f.Kind)
            {
                case FeatureKind.Area:
                    if (f.IsSymbolArea)
                    {
                        if (!SymbolOrnaments.IsPatternOnly(f.ColorHex, f.Patterns)) c.Append($"{FillOp(f.ColorHex)}\n{PathOf(f)}f*\n");
                        if (f.Patterns is { Count: > 0 } patterns && f.Bounds is { } bb)
                        {
                            var lo = PagePoint(new Point2D(bb.MinX, bb.MinY)); var hi = PagePoint(new Point2D(bb.MaxX, bb.MaxY));
                            var r = new Rect2D(Math.Max(lo.X, box.X0), Math.Max(lo.Y, box.Y0), Math.Min(hi.X, box.X1), Math.Min(hi.Y, box.Y1));
                            if (r.MaxX > r.MinX && r.MaxY > r.MinY)
                            {
                                c.Append($"q\n{PathOf(f)}W* n\n");
                                foreach (var p in patterns)
                                {
                                    var list = SymbolOrnaments.PatternMarks(p, r, PtPerMM, yUp: true, maxMarks: 200_000, seed: f.PatternSeed);
                                    if (list is not null) c.Append(Marks(list));
                                }
                                c.Append("Q 1 J 1 j\n");
                            }
                        }
                    }
                    else
                    {
                        c.Append($"q /GSArea gs {Rgb(f.ColorHex)} rg\n{PathOf(f)}f*\nQ\n");
                        c.Append($"1 J 1 j [] 0 d {Rgb(f.ColorHex)} RG {N(f.WidthMM * PtPerMM)} w\n{PathOf(f)}S\n");
                    }
                    break;
                case FeatureKind.Line:
                {
                    var caps = f.RoundCaps == false ? "0 J 0 j" : "1 J 1 j";
                    bool sym = f.SymbolCode is not null;
                    string Dash(List<double>? d) => d is null ? "[] 0 d" : $"[{string.Join(" ", d.Select(v => N(v * PtPerMM)))}] 0 d";
                    if (f.CasingColorHex is { } casing && f.InnerWidthMM is double inner)
                    {
                        if (paint.Part.PartKind != ColorHierarchy.Part.Kind.Main) c.Append($"{caps} {Dash(f.EffectiveDashMM)} {StrokeOp(casing, sym)} {N(f.WidthMM * PtPerMM)} w\n{PathOf(f)}S\n");
                        if (paint.Part.PartKind != ColorHierarchy.Part.Kind.Casing) c.Append($"{caps} {Dash(f.EffectiveInnerDashMM)} {StrokeOp(f.ColorHex, sym)} {N(inner * PtPerMM)} w\n{PathOf(f)}S\n");
                    }
                    else
                    {
                        if (!f.HidesMainLine) c.Append($"{caps} {Dash(f.EffectiveDashMM)} {StrokeOp(f.ColorHex, sym)} {N(f.WidthMM * PtPerMM)} w\n{PathOf(f)}S\n");
                        if (f.Decoration is { } deco)
                        {
                            var pts = SymbolOrnaments.Polyline(f, PagePoint);
                            c.Append(Marks(SymbolOrnaments.DecorationMarks(deco, pts, PtPerMM, f.WidthMM, f.ColorHex, yUp: true, f.EffectiveDashMM)) + $"{caps}\n");
                        }
                    }
                    break;
                }
                default:
                    c.Append("1 J 1 j [] 0 d\n");
                    DrawPoint(f, paint.Part.PartKind == ColorHierarchy.Part.Kind.Elements ? paint.Part.Level : null);
                    break;
            }
        }
        DrawLower();
        c.Append("1 J 1 j [] 0 d\n");
        if (req.Grid is { IsEnabled: true, IncludeInExport: true } g)
        {
            c.Append($"q /GSGrid gs {Rgb(g.ColorHex)} RG {N(g.LineWidthMM * PtPerMM)} w\n");
            foreach (var (a, bpt) in g.Lines(req.Frame)) c.Append($"{Pt(a)} m {Pt(bpt)} l S\n");
            c.Append("Q\n");
        }
        foreach (var s in req.Strokes)
        {
            if (s.Points.Count == 0) continue;
            double w = s.WidthMM * PtPerMM;
            var first = s.Points[0];
            if (s.Points.Count == 1) { c.Append($"{Rgb(s.ColorHex)} RG {N(w)} w {Pt(first)} m {Pt(first)} l S\n"); continue; }
            c.Append($"{Rgb(s.ColorHex)} RG {N(w)} w {Pt(first)} m\n");
            foreach (var p in s.Points.Skip(1)) c.Append($"{Pt(p)} l\n");
            c.Append("S\n");
        }
        if (levels is { Upper.IsEmpty: false } lv) c.Append(OverprintContent(lv.Upper, Pt, k, CmykString, imageNames, opacities));
        if (bg is { Attribution: { Length: > 0 } text })
        {
            double w = text.Length * 5 * 0.5;
            c.Append($"q 1 1 1 rg {N(box.X1 - w - 4)} {N(box.Y0 + 1)} {N(w + 3)} 7 re f Q\n");
            c.Append($"BT /F1 5 Tf 0.25 0.25 0.25 rg {N(box.X1 - w - 2.5)} {N(box.Y0 + 2.8)} Td ({PdfString(text)}) Tj ET\n");
        }
        if (req.Watermark is { Length: > 0 } mark)
        {
            double w = mark.Length * 6 * 0.5;
            c.Append($"q 1 1 1 rg {N(box.X0 + 1)} {N(box.Y0 + 1)} {N(w + 5)} 8.5 re f Q\n");
            c.Append($"BT /F2 6 Tf 0.2 0.2 0.2 rg {N(box.X0 + 3.5)} {N(box.Y0 + 3.2)} Td ({PdfString(mark)}) Tj ET\n");
        }
        c.Append("Q\n");
        // Race map: control descriptions on top of the map (inside the map area).
        if (req.PrintLayout is { } layout && req.Descriptions is { } desc) c.Append(Decorations(layout, desc, req, box));
        // Map area border + caption in the bottom margin (if there is a margin).
        c.Append($"0.6 0.6 0.6 RG 0.3 w {N(box.X0)} {N(box.Y0)} {N(box.X1 - box.X0)} {N(box.Y1 - box.Y0)} re S\n");
        if (req.Sheet.MarginMM >= 4)
        {
            var caption = $"{req.Title} · 1:{req.Sheet.ScaleDenominator} · {req.Zone.Label} (EPSG:{req.Zone.Epsg}) · {AppInfo.Name}";
            c.Append($"BT /F1 6 Tf 0.35 0.35 0.35 rg {N(box.X0)} {N(box.Y0 - 8)} Td ({PdfString(caption)}) Tj ET\n");
        }

        // Georeferencing: LPTS = viewport corners (normalised); GPTS = lat lon of the same corners.
        var corners = new[] { new Point2D(e.MinX, e.MinY), new Point2D(e.MinX, e.MaxY), new Point2D(e.MaxX, e.MaxY), new Point2D(e.MaxX, e.MinY) };
        var gpts = string.Join(" ", corners.Select(p => req.Zone.Unproject(p)).Select(ll => $"{N(ll.Lat, 9)} {N(ll.Lon, 9)}"));
        const string lpts = "0 0 0 1 1 1 1 0";

        var content = Encoding.Latin1.GetBytes(c.ToString());
        var objects = new List<byte[]>();
        int Add(string s) { objects.Add(Encoding.Latin1.GetBytes(s)); return objects.Count; }
        int AddStream(string dict, byte[] body)
        {
            objects.Add(Encoding.Latin1.GetBytes($"<< {dict} /Length {body.Length} >>\nstream\n").Concat(body).Concat(Encoding.Latin1.GetBytes("\nendstream")).ToArray());
            return objects.Count;
        }
        int catalog = Add("");
        int pages = Add("");
        int gcs = Add($"<< /Type /PROJCS /EPSG {req.Zone.Epsg} /WKT ({PdfString(req.Zone.PrjWkt)}) >>");
        int measure = Add($"<< /Type /Measure /Subtype /GEO /Bounds [0 0 0 1 1 1 1 0] /GPTS [{gpts}] /LPTS [{lpts}] /GCS {gcs} 0 R /PDU [/M /SQM /DEG] >>");
        int font = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int bold = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        int oblique = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>");
        int boldOblique = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-BoldOblique /Encoding /WinAnsiEncoding >>");
        var xobjects = new List<string>();
        if (bg is not null)
        {
            // JPEG straight into the PDF (DCTDecode); the app converts PNG basemaps to JPEG before export.
            int img = AddStream($"/Type /XObject /Subtype /Image /Width {bg.PixelWidth} /Height {bg.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", bg.ImageData);
            xobjects.Add($"/ImBg {img} 0 R");
        }
        foreach (var (file, name) in imageNames.OrderBy(kv => kv.Value, StringComparer.Ordinal))
        {
            if (!req.Images.TryGetValue(file, out var data) || CourseImages.PdfImageOf(data) is not { } img) continue;
            var dict = img.Dictionary;
            if (img.Mask is { } m) dict += $" /SMask {AddStream(m.Dictionary, m.Stream)} 0 R";
            xobjects.Add($"/{name} {AddStream(dict, img.Stream)} 0 R");
        }
        int stream = AddStream("", content);
        var extraPages = new List<int>();
        var fills = string.Join(" ", opacities.Select((o, i) => $"/GSFill{i} << /Type /ExtGState /ca {N(o)} >>"));
        int pageObj = Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {N(page.W)} {N(page.H)}] /Contents {stream} 0 R "
                          + $"/Resources << /Font << /F1 {font} 0 R /F2 {bold} 0 R /F3 {oblique} 0 R /F4 {boldOblique} 0 R >> /XObject << {string.Join(" ", xobjects)} >> "
                          + $"/ExtGState << /GSBg << /Type /ExtGState /ca {N(req.Background?.Opacity ?? 1)} >> /GSArea << /Type /ExtGState /ca 0.35 >> /GSGrid << /Type /ExtGState /CA {N(req.Grid?.Opacity ?? 1)} >> "
                          + $"{fills} >> >> "
                          + $"/VP [<< /Type /Viewport /BBox [{N(box.X0)} {N(box.Y0)} {N(box.X1)} {N(box.Y1)}] /Name ({PdfString(req.Title)}) /Measure {measure} 0 R >>] >>");
        objects[catalog - 1] = Encoding.Latin1.GetBytes($"<< /Type /Catalog /Pages {pages} 0 R >>");
        bool separatePage = req.PrintLayout is { } pl ? pl.SeparateDescriptionPage || !pl.DescriptionOnMap : true;
        if (req.Descriptions is { } sheet && separatePage)
        {
            var body = Encoding.Latin1.GetBytes(DescriptionContent(sheet, req.PrintLayout?.DescriptionMaxRows, req.PrintLayout?.DescriptionSymbols ?? true,
                                                                   req.PrintLayout?.DescriptionColorValue ?? CoursePrintLayout.DescriptionColor.Black));
            int streamObj = AddStream("", body);
            extraPages.Add(Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 595.276 841.89] /Contents {streamObj} 0 R /Resources << /Font << /F1 {font} 0 R /F2 {bold} 0 R /F3 {oblique} 0 R /F4 {boldOblique} 0 R >> >> >>"));
        }
        var kids = string.Join(" ", new[] { pageObj }.Concat(extraPages).Select(p => $"{p} 0 R"));
        objects[pages - 1] = Encoding.Latin1.GetBytes($"<< /Type /Pages /Kids [{kids}] /Count {1 + extraPages.Count} >>");
        int info = Add($"<< /Title ({PdfString(req.Title)}) /Producer ({AppInfo.Name}) /Creator ({AppInfo.Name}) >>");

        using var ms = new MemoryStream();
        void W(string s) { var bts = Encoding.Latin1.GetBytes(s); ms.Write(bts, 0, bts.Length); }
        W("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Length);
            W($"{i + 1} 0 obj\n");
            ms.Write(objects[i], 0, objects[i].Length);
            W("\nendobj\n");
        }
        long xref = ms.Length;
        var trailer = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) trailer.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        trailer.Append($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        W(trailer.ToString());
        return ms.ToArray();
    }

    // MARK: Courses

    /// <summary>
    /// Purple overprint: circles, start triangle, legs, and numbers (Helvetica, digit height = IOF number height).
    /// <paramref name="cmyk"/>: hex → "c m y k" (DeviceCMYK); purple is the IOF 35/85/0/0. No blend mode: the caller places
    /// the lower and upper purple parts in the colour order (<see cref="CourseOverprint.Levels"/>).
    /// </summary>
    internal static string OverprintContent(CourseOverprint o, Func<Point2D, string> pt, double k, Func<string, string> cmyk,
                                            IReadOnlyDictionary<string, string>? images = null, IReadOnlyList<double>? opacities = null)
    {
        var c = new StringBuilder($"q 1 J 1 j [] 0 d {cmyk(o.ColorHex)} K {cmyk(o.ColorHex)} k {N(o.LineWidthM * k)} w\n");
        (double X, double Y)? Xy(Point2D p)
        {
            var v = pt(p).Split(' ').Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToList();
            return v.Count == 2 && !double.IsNaN(v[0]) && !double.IsNaN(v[1]) ? (v[0], v[1]) : null;
        }
        var font = "F1";
        foreach (var mark in o.Marks)
        {
            switch (mark)
            {
                case CourseMark.Font f:
                    font = f.Bold ? (f.Italic ? "F4" : "F2") : (f.Italic ? "F3" : "F1");
                    break;
                case CourseMark.Color col:
                {
                    var v = cmyk(col.Hex ?? o.ColorHex);
                    c.Append($"{v} K {v} k\n");
                    break;
                }
                case CourseMark.Width w:
                    c.Append($"{N((w.WidthM ?? o.LineWidthM) * k)} w\n");
                    break;
                case CourseMark.Level:
                    break;
                case CourseMark.Circle ci:
                    c.Append(CirclePath(ci.Center, ci.RadiusM, pt)).Append("S\n");
                    break;
                case CourseMark.Polyline pl when pl.Points.Count > 0:
                    c.Append($"{pt(pl.Points[0])} m\n").Append(string.Concat(pl.Points.Skip(1).Select(p => $"{pt(p)} l\n"))).Append(pl.Closed ? "h\n" : "").Append("S\n");
                    break;
                case CourseMark.Text t:
                {
                    // Helvetica digit height ≈ 0.72 em; digit width 0.556 em.
                    double size = t.HeightM * k / 0.72;
                    double w = t.Value.Length * 0.556 * size;
                    if (Xy(t.Center) is not { } xy) continue;
                    c.Append($"BT /{font} {N(size)} Tf {N(xy.X - w / 2)} {N(xy.Y - t.HeightM * k / 2)} Td ({PdfString(t.Value)}) Tj ET\n");
                    break;
                }
                case CourseMark.Fill fi when fi.Points.Count > 0:
                {
                    int gs = opacities is null ? -1 : opacities.ToList().IndexOf(fi.Opacity);
                    if (gs < 0) continue;
                    c.Append($"q /GSFill{gs} gs {pt(fi.Points[0])} m\n").Append(string.Concat(fi.Points.Skip(1).Select(p => $"{pt(p)} l\n"))).Append("h f Q\n");
                    break;
                }
                case CourseMark.Image im:
                {
                    if (images is null || !images.TryGetValue(im.Name, out var res) || Xy(im.Center) is not { } p) continue;
                    double pw = im.WidthM * k, ph = im.HeightM * k;
                    c.Append($"q {N(pw)} 0 0 {N(ph)} {N(p.X - pw / 2)} {N(p.Y - ph / 2)} cm /{res} Do Q\n");
                    break;
                }
            }
        }
        return c.Append("Q\n").ToString();
    }

    /// <summary>DeviceCMYK components (0–1) of a colour: the IOF spec CMYK × screen for OCCA's colours, else converted from RGB.</summary>
    public static string CmykString(string hex) => string.Join(" ", IofColors.Cmyk(hex).Take(4).Select(v => N(v)));

    /// <summary>A4 page with the description table (7 mm cells) at the top left.</summary>
    internal static string DescriptionContent(DescriptionSheet d, int? maxRows = null, bool symbols = true,
                                              CoursePrintLayout.DescriptionColor color = CoursePrintLayout.DescriptionColor.Black)
    {
        double cell = 7 * PtPerMM;
        var c = new StringBuilder();
        double height = 0;
        var cols = d.Columns(maxRows);
        for (int i = 0; i < cols.Count; i++)
        {
            var (table, h) = DescriptionTable(cols[i], 40 + i * cell * 8, 800, cell, background: false, symbols, color);
            c.Append(table);
            height = Math.Max(height, h);
        }
        return c.Append($"BT /F1 6 Tf 40 {N(800 - height - 14)} Td ({PdfString($"{AppInfo.Name} · IOF control descriptions {(symbols ? "2024" : "(text)")}")}) Tj ET\n").ToString();
    }

    /// <summary>Description table height (pt) for cell size <paramref name="cell"/>.</summary>
    public static double DescriptionTableHeight(DescriptionSheet d, double cell) => (d.Rows.Count + d.HeadingRowCount + (d.IsContinuation ? 0 : 1)) * cell;

    /// <summary>Distance of the description block from the map area edge when in a corner (mm).</summary>
    public const double DescriptionInsetMM = 4.0;

    /// <summary>Description block size on paper (mm): columns side by side, height = tallest column.</summary>
    public static (double W, double H) DescriptionBlockSizeMM(DescriptionSheet d, CoursePrintLayout layout)
    {
        double cell = ClampedCellMM(layout);
        var cols = d.Columns(layout.DescriptionMaxRows);
        return (cols.Count * cell * 8, cols.Count == 0 ? 0 : cols.Max(col => DescriptionTableHeight(col, cell)));
    }

    /// <summary>Top-left corner of the description block from the top-left of the map area (mm), inside the map area.</summary>
    public static Point2D DescriptionOriginMM((double W, double H) block, CoursePrintLayout layout, SheetSpec sheet)
    {
        double mw = sheet.MappingWidthMM, mh = sheet.MappingHeightMM, inset = DescriptionInsetMM;
        Point2D o;
        if (layout.DescriptionOffsetMM is { } off) o = off;
        else
        {
            var corner = layout.DescriptionCorner;
            o = new Point2D(corner.IsLeft() ? inset : mw - inset - block.W, corner.IsTop() ? inset : mh - inset - block.H);
        }
        return new Point2D(Math.Min(Math.Max(o.X, 0), Math.Max(mw - block.W, 0)), Math.Min(Math.Max(o.Y, 0), Math.Max(mh - block.H, 0)));
    }

    internal static double ClampedCellMM(CoursePrintLayout layout) => Math.Clamp(layout.DescriptionCellMM, CoursePrintLayout.CellRangeMM.Min, CoursePrintLayout.CellRangeMM.Max);

    /// <summary>
    /// Description table: column A (number), B (code), then C–H as IOF pictograms (<paramref name="symbols"/>) or a text
    /// description spanning 6 cells; 14.1/15.x/16.x as full-width rows; thick lines per <c>Row.ThickBelow</c>.
    /// <paramref name="background"/>: white fill (for placing on top of the map).
    /// </summary>
    internal static (string Content, double Height) DescriptionTable(DescriptionSheet d, double x0, double yTop, double cell, bool background,
                                                                     bool symbols = true, CoursePrintLayout.DescriptionColor color = CoursePrintLayout.DescriptionColor.Black)
    {
        double s = cell / (7 * PtPerMM);   // font size follows the cell size
        double width = cell * 8;
        double height = DescriptionTableHeight(d, cell);
        double y = yTop;
        var c = new StringBuilder("q\n");
        if (background) c.Append($"1 1 1 rg {N(x0)} {N(yTop - height)} {N(width)} {N(height)} re f\n");
        var ink = ColorHex.Rgb(color.Hex()) ?? (0, 0, 0);
        var inkRgb = $"{N(ink.R)} {N(ink.G)} {N(ink.B)}";
        c.Append($"{inkRgb} RG {inkRgb} rg {N(0.5 * s)} w\n");
        void Text(string str, double x, double ty, double size, bool center = false, double maxWidth = 0, string font = "F1")
        {
            double w = TextWidth(str, size, font == "F2");
            double tx = center ? x + (maxWidth - w) / 2 : x;
            c.Append($"BT /{font} {N(size)} Tf {N(tx)} {N(ty)} Td ({PdfString(str)}) Tj ET\n");
        }
        void Box(double x, double by, double w, double h) => c.Append($"{N(x)} {N(by)} {N(w)} {N(h)} re S\n");

        if (!d.IsContinuation && d.Title.Length > 0)
        {
            y -= cell;
            Box(x0, y, width, cell);
            Text(Fit(d.Title, width - 4, 9 * s, true), x0, y + cell * 0.32, 9 * s, true, width, "F2");
        }
        if (!d.IsContinuation && d.Organizer.Length > 0)
        {
            y -= cell;
            Box(x0, y, width, cell);
            Text(Fit(d.Organizer, width - 4, 7.5 * s), x0, y + cell * 0.34, 7.5 * s, true, width);
        }
        if (!d.IsContinuation && d.Classes.Length > 0)
        {
            y -= cell;
            Box(x0, y, width, cell);
            Text(Fit(d.Classes, width - 4, 7.5 * s), x0, y + cell * 0.34, 7.5 * s, true, width);
        }
        if (!d.IsContinuation)
        {
            y -= cell;
            Box(x0, y, cell * 4, cell); Box(x0 + cell * 4, y, cell * 2, cell); Box(x0 + cell * 6, y, cell * 2, cell);
            Text(Fit(d.CourseName, cell * 4 - 4, 9 * s, true), x0 + 3 * s, y + cell * 0.32, 9 * s, font: "F2");
            Text(d.LengthText, x0 + cell * 4, y + cell * 0.32, 8 * s, true, cell * 2);
            Text(d.ClimbText, x0 + cell * 6, y + cell * 0.32, 8 * s, true, cell * 2);
        }

        void ThickLine(DescriptionSheet.Row row, double ly)
        {
            if (row.ThickBelow) c.Append($"{N(1.5 * s)} w {N(x0)} {N(ly)} m {N(x0 + width)} {N(ly)} l S {N(0.5 * s)} w\n");
        }
        foreach (var row in d.Rows)
        {
            y -= cell;
            if (row.IsFullWidth(symbols))
            {
                Box(x0, y, width, cell);
                if (symbols && IofPictogram.RowShapes(row) is { } rowShapes)
                {
                    c.Append(IofPictogram.Pdf(rowShapes, x0, y, cell, (str, size) => TextWidth(str, size)));
                }
                else
                {
                    // Text line across the whole row: 1–2 lines.
                    var one = Wrap(row.Text, 46); var two = Wrap(row.Text, 56);
                    if (one.Count <= 1) Text(one.Count > 0 ? one[0] : "", x0 + 3 * s, y + cell * 0.32, 7 * s);
                    else
                    {
                        Text(two[0], x0 + 3 * s, y + cell * 0.56, 5.8 * s);
                        Text(two.Count > 2 ? two[1] + "..." : two[1], x0 + 3 * s, y + cell * 0.14, 5.8 * s);
                    }
                }
                ThickLine(row, y);
                continue;
            }
            Box(x0, y, cell, cell); Box(x0 + cell, y, cell, cell);
            if (symbols)
            {
                // C–H: one cell per column; thick lines after C and F like the IOF sheet.
                for (int kk = 2; kk < 8; kk++) Box(x0 + cell * kk, y, cell, cell);
                c.Append($"{N(1.2 * s)} w {N(x0 + cell * 3)} {N(y)} m {N(x0 + cell * 3)} {N(y + cell)} l S "
                         + $"{N(x0 + cell * 6)} {N(y)} m {N(x0 + cell * 6)} {N(y + cell)} l S {N(0.5 * s)} w\n");
            }
            else Box(x0 + cell * 2, y, cell * 6, cell);
            var mid = (X: x0 + cell / 2, Y: y + cell / 2);
            switch (row.Kind)
            {
                case ControlKind.Start:
                {
                    double r = cell * 0.32;
                    var pts = Enumerable.Range(0, 3).Select(i =>
                    {
                        double a = Math.PI / 2 + i * 2 * Math.PI / 3;
                        return $"{N(mid.X + r * Math.Cos(a))} {N(mid.Y + r * Math.Sin(a))}";
                    }).ToList();
                    c.Append($"{pts[0]} m {pts[1]} l {pts[2]} l h S\n");
                    break;
                }
                case ControlKind.Finish:
                    c.Append(CirclePath(Point2D.Zero, cell * 0.22, q => $"{N(mid.X + q.X)} {N(mid.Y + q.Y)}")).Append("S\n");
                    c.Append(CirclePath(Point2D.Zero, cell * 0.34, q => $"{N(mid.X + q.X)} {N(mid.Y + q.Y)}")).Append("S\n");
                    break;
                default:
                    Text(row.Number, x0, y + cell * 0.3, 10 * s, true, cell, "F2");
                    Text(row.Code, x0 + cell, y + cell * 0.3, 10 * s, true, cell);
                    break;
            }
            if (symbols)
            {
                var desc = row.Description;
                var cols = new[]
                {
                    IofPictogram.Shapes(desc.Which), IofPictogram.Shapes(desc.Feature), IofPictogram.Shapes(desc.Appearance),
                    IofPictogram.DimensionShapes(desc.Dimension), IofPictogram.Shapes(desc.Location), IofPictogram.Shapes(desc.Other),
                };
                double pad = cell * 0.1;
                for (int kk = 0; kk < cols.Length; kk++)
                {
                    if (cols[kk] is not { } shapes) continue;
                    c.Append(IofPictogram.Pdf(shapes, x0 + cell * (kk + 2) + pad, y + pad, cell - 2 * pad, (str, size) => TextWidth(str, size)));
                }
                // Columns that are not filled are left empty.
                ThickLine(row, y);
                continue;
            }
            // Description: 1–2 lines, smaller font when long.
            var lines = Wrap(row.Text, 34);
            var small = Wrap(row.Text, 42);
            if (lines.Count <= 1) Text(lines.Count > 0 ? lines[0] : "", x0 + cell * 2 + 3 * s, y + cell * 0.32, 7 * s);
            else if (small.Count == 1) Text(small[0], x0 + cell * 2 + 3 * s, y + cell * 0.34, 5.8 * s);
            else
            {
                Text(small[0], x0 + cell * 2 + 3 * s, y + cell * 0.56, 5.8 * s);
                Text(small.Count > 2 ? small[1] + "..." : small[1], x0 + cell * 2 + 3 * s, y + cell * 0.14, 5.8 * s);
            }
            ThickLine(row, y);
        }
        c.Append($"{N(1.5 * s)} w {N(x0)} {N(y)} {N(width)} {N(yTop - y)} re S\nQ\n");
        return (c.ToString(), height);
    }

    /// <summary>Helvetica text width (estimate from average character width).</summary>
    internal static double TextWidth(string s, double size, bool bold = false) => s.Length * size * (bold ? 0.58 : 0.52);

    /// <summary>Truncate text to fit the width (pt), with "...".</summary>
    internal static string Fit(string s, double width, double size, bool bold = false)
    {
        if (TextWidth(s, size, bold) <= width) return s;
        var t = s;
        while (t.Length > 0 && TextWidth(t + "...", size, bold) > width) t = t[..^1];
        return t + "...";
    }

    /// <summary>A readable scale bar length (m): 15–30 mm on paper.</summary>
    public static double ScaleBarMeters(int scale)
    {
        double[] options = { 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000 };
        double Mm(double m) => m * 1000 / scale;
        return options.LastOrDefault(o => Mm(o) <= 30, options[0]);
    }

    // MARK: Race map (single page)

    /// <summary>
    /// Control descriptions on top of the map (white background): in the chosen corner or a free position, split into several
    /// side-by-side columns when rows exceed the per-column limit.
    /// </summary>
    internal static string Decorations(CoursePrintLayout layout, DescriptionSheet d, GeoPdfRequest req, (double X0, double Y0, double X1, double Y1) box)
    {
        if (!layout.DescriptionOnMap) return "";
        double cellMM = ClampedCellMM(layout);
        var block = DescriptionBlockSizeMM(d, layout);
        var o = DescriptionOriginMM(block, layout, req.Sheet);
        var c = new StringBuilder();
        var cols = d.Columns(layout.DescriptionMaxRows);
        for (int i = 0; i < cols.Count; i++)
        {
            double x = box.X0 + (o.X + i * cellMM * 8) * PtPerMM;
            c.Append(DescriptionTable(cols[i], x, box.Y1 - o.Y * PtPerMM, cellMM * PtPerMM, background: true, layout.DescriptionSymbols, layout.DescriptionColorValue).Content);
        }
        return c.ToString();
    }

    internal static List<string> Wrap(string s, int maxChars)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length == 0) line = word;
            else if (line.Length + 1 + word.Length <= maxChars) line += " " + word;
            else { lines.Add(line); line = word; }
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>Circle as 4 cubic curves (kappa), mm coordinates relative to the symbol centre.</summary>
    public static string CirclePath(Point2D o, double r, Func<Point2D, string> at)
    {
        double k = FeatureEdit.EllipseKappa * r;
        string P(double x, double y) => at(new Point2D(o.X + x, o.Y + y));
        return $"{P(r, 0)} m\n{P(r, k)} {P(k, r)} {P(0, r)} c\n{P(-k, r)} {P(-r, k)} {P(-r, 0)} c\n{P(-r, -k)} {P(-k, -r)} {P(0, -r)} c\n{P(k, -r)} {P(r, -k)} {P(r, 0)} c\nh\n";
    }

    private static readonly long[] Pow10 = Enumerable.Range(0, 10).Select(i => (long)Math.Pow(10, i)).ToArray();

    /// <summary>PDF number: decimal point, no trailing zeros, rounded half to even like printf (the Mac's n()).</summary>
    public static string N(double v, int decimals = 3)
    {
        if (!double.IsFinite(v)) return "0";
        int d = Math.Clamp(decimals, 0, 9);
        long p = Pow10[d];
        double scaled = Math.Round(Math.Abs(v) * p, MidpointRounding.ToEven);
        if (scaled >= 9e15)
        {
            var s = v.ToString("F" + d, CultureInfo.InvariantCulture);
            if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
            return s;
        }
        long i = (long)scaled;
        long frac = i % p;
        int digits = d;
        while (digits > 0 && frac % 10 == 0) { frac /= 10; digits--; }
        var sb = new StringBuilder(v < 0 && i != 0 ? "-" : "");
        sb.Append((i / p).ToString(CultureInfo.InvariantCulture));
        if (digits > 0)
        {
            var f = frac.ToString(CultureInfo.InvariantCulture);
            sb.Append('.').Append('0', digits - f.Length).Append(f);
        }
        return sb.ToString();
    }

    /// <summary>PDF string literal: escape \ ( ) and encode Latin-1 characters as octal escapes; others become "?".</summary>
    public static string PdfString(string s)
    {
        var sb = new StringBuilder();
        foreach (var rune in s.EnumerateRunes())
        {
            int u = rune.Value;
            switch (u)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                default:
                    if (u < 128) sb.Append((char)u);
                    else if (u is >= 160 and <= 255) sb.Append('\\').Append(Convert.ToString(u, 8));
                    else sb.Append('?');
                    break;
            }
        }
        return sb.ToString();
    }
}

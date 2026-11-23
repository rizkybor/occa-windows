using System.Globalization;
using System.Text;
using Occa.Core.Geometry;
using Occa.Core.Project;
using Occa.Core.Sheet;
using Occa.Core.Vector;

namespace Occa.Core.Export;

/// <summary>What the GeoPDF export draws (port of GeoPDFRequest; course overprint and descriptions arrive with M4).</summary>
public sealed class GeoPdfRequest
{
    public required string Title { get; init; }
    public required MapFrame Frame { get; init; }
    public required SheetSpec Sheet { get; init; }
    public required UtmZone Zone { get; init; }
    public List<Stroke> Strokes { get; init; } = new();
    public List<Feature> Features { get; init; } = new();
    public NorthGrid? Grid { get; init; }
    /// <summary>Georeferenced basemap image (JPEG) under the map objects; null = none.</summary>
    public ExportBackground? Background { get; init; }
    /// <summary>Small credit in the bottom-left corner of the map area (Starter plan); null = none.</summary>
    public string? Watermark { get; init; }
}

/// <summary>
/// Paper-sized vector PDF (A4 + margin) with Geospatial PDF georeferencing (ISO 32000-2 / Adobe extension): the page
/// carries /VP → /Measure /GEO with lat/lon control points at the four corners of the map area and the project CRS WKT.
/// Written by hand, byte-compatible with the Mac's GeoPDFExporter: map symbols as DeviceCMYK with the IOF Rev 4 values,
/// sketches, north lines and basemaps in RGB, objects in the Colour Hierarchy order (NFR-4).
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

        foreach (var paint in ColorHierarchy.PaintOrder(visible))
        {
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
        int stream = AddStream("", content);
        int pageObj = Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {N(page.W)} {N(page.H)}] /Contents {stream} 0 R "
                          + $"/Resources << /Font << /F1 {font} 0 R /F2 {bold} 0 R /F3 {oblique} 0 R /F4 {boldOblique} 0 R >> /XObject << {string.Join(" ", xobjects)} >> "
                          + $"/ExtGState << /GSBg << /Type /ExtGState /ca {N(req.Background?.Opacity ?? 1)} >> /GSArea << /Type /ExtGState /ca 0.35 >> /GSGrid << /Type /ExtGState /CA {N(req.Grid?.Opacity ?? 1)} >> >> >> "
                          + $"/VP [<< /Type /Viewport /BBox [{N(box.X0)} {N(box.Y0)} {N(box.X1)} {N(box.Y1)}] /Name ({PdfString(req.Title)}) /Measure {measure} 0 R >>] >>");
        objects[catalog - 1] = Encoding.Latin1.GetBytes($"<< /Type /Catalog /Pages {pages} 0 R >>");
        objects[pages - 1] = Encoding.Latin1.GetBytes($"<< /Type /Pages /Kids [{pageObj} 0 R] /Count 1 >>");
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

    /// <summary>DeviceCMYK components (0–1) of a colour: the IOF spec CMYK × screen for OCCA's colours, else converted from RGB.</summary>
    public static string CmykString(string hex) => string.Join(" ", IofColors.Cmyk(hex).Take(4).Select(v => N(v)));

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

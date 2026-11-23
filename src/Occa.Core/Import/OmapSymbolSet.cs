using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Occa.Core.Geometry;
using Occa.Core.Sheet;
using Occa.Core.Vector;

namespace Occa.Core.Import;

/// <summary>
/// Reads the symbol set of an OpenOrienteering Mapper file (.omap / .xmap, XML v2) into a SymbolSet: point symbols
/// with their elements, line symbols (width, dashes, caps, double lines from borders), area symbols (fill, hatch and
/// dot patterns), combined symbols, and the colour order. Port of OmapSymbolSet.swift.
/// </summary>
public static class OmapSymbolSet
{
    public sealed class ReadException : Exception
    {
        public ReadException(string m) : base(m) { }
    }

    private const int MaxDepth = 8;

    public static SymbolSet Read(byte[] data, string name, string sourceFile, Func<int, bool>? codeFilter = null)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { throw new ReadException("This is not an OpenOrienteering Mapper file (.omap / .xmap in XML format)."); }
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "map") throw new ReadException("This is not an OpenOrienteering Mapper file (.omap / .xmap in XML format).");

        int scale = (int)D(First(root, "georeferencing")?.Attribute("scale")?.Value);
        var colors = new Dictionary<int, (string Hex, int Priority)>();
        int idx = 0;
        foreach (var c in Children(First(root, "colors"), "color"))
        {
            int priority = Int(c.Attribute("priority")?.Value) ?? idx;
            var rgb = First(c, "rgb");
            colors[priority] = (Hex(D(rgb?.Attribute("r")?.Value), D(rgb?.Attribute("g")?.Value), D(rgb?.Attribute("b")?.Value)), priority);
            idx++;
        }
        var symbolsNode = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "symbols") ?? throw new ReadException("The file has no symbols.");
        var topLevel = Children(symbolsNode, "symbol").ToList();
        var byId = new Dictionary<string, XElement>();
        foreach (var s in topLevel) if (s.Attribute("id")?.Value is { } id) byId[id] = s;
        var ctx = new Context(colors, byId);
        var used = new HashSet<int>();
        var symbols = new List<MapSymbol>();
        foreach (var node in topLevel)
        {
            if (node.Attribute("code")?.Value is not { } code || Number(code) is not int number || used.Contains(number)) continue;
            if (codeFilter is not null && !codeFilter(number / 1000)) continue;
            if (ctx.Symbol(node, number) is not { } symbol) continue;
            symbol.IsEnabled = node.Attribute("is_hidden")?.Value != "true";
            used.Add(number);
            symbols.Add(symbol);
        }
        if (symbols.Count == 0) throw new ReadException("The file has no symbols.");
        return new SymbolSet { Name = name, SourceFile = sourceFile, Scale = scale, Symbols = symbols.OrderBy(s => s.Number).ToList() };
    }

    /// <summary>"101" → 101000, "501.1" → 501001, "512.10" → 512010; null for codes that aren't numbers.</summary>
    public static int? Number(string code)
    {
        var parts = code.Trim().Split('.');
        if (!int.TryParse(parts[0], out var main) || main < 0 || main >= 1_000_000) return null;
        int sub = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0;
        return sub < 1000 ? main * 1000 + sub : null;
    }

    private static double D(string? s, double fallback = 0) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static string Hex(double r, double g, double b) { int C(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255); return $"#{C(r):X2}{C(g):X2}{C(b):X2}"; }
    private static XElement? First(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    private static IEnumerable<XElement> Children(XElement? e, string name) => e?.Elements().Where(x => x.Name.LocalName == name) ?? Enumerable.Empty<XElement>();

    private sealed class Line
    {
        public int ColorIndex;
        public string ColorHex = "";
        public int? Priority;
        public double Width;
        public List<double>? Dash;
        public bool RoundCaps;
        public (int ColorIndex, string Hex, int? Priority, double Total, double Inner, List<double>? Dash)? Casing;
        public List<double>? InnerDash;

        public double TotalWidth => Casing?.Total ?? Width;

        public MapSymbol Symbol(int number, string name)
        {
            LineStyle style = Casing is { } c
                ? new LineStyle { ColorHex = ColorHex, WidthMM = c.Total, Casing = new LineStyle.CasingStyle { ColorHex = c.Hex, InnerWidthMM = c.Inner }, DashMM = c.Dash, InnerDashMM = InnerDash ?? Dash, RoundCaps = RoundCaps }
                : new LineStyle { ColorHex = ColorHex, WidthMM = Width, DashMM = Dash, RoundCaps = RoundCaps };
            return new MapSymbol
            {
                Number = number, Name = name, SymbolKind = MapSymbol.Kind.Line,
                ColorHex = Casing is null ? ColorHex : (ColorIndex >= 0 ? ColorHex : Casing.Value.Hex),
                LineWidthMM = Casing is null ? Width : null, IsEnabled = true, LineStyle = style,
                ColorPriority = Priority ?? Casing?.Priority, CasingPriority = Casing?.Priority,
            };
        }
    }

    private sealed class Context
    {
        private readonly Dictionary<int, (string Hex, int Priority)> _colors;
        private readonly Dictionary<string, XElement> _byId;

        public Context(Dictionary<int, (string, int)> colors, Dictionary<string, XElement> byId) { _colors = colors; _byId = byId; }

        private string? HexOf(int i) => _colors.TryGetValue(i, out var c) ? c.Hex : null;
        private int? Priority(int i) => _colors.TryGetValue(i, out var c) ? c.Priority : null;
        private int? ColorPriority(string hex) => _colors.Values.Where(c => c.Hex == hex).Select(c => (int?)c.Priority).Min();

        public MapSymbol? Symbol(XElement node, int number, int depth = 0)
        {
            if (depth > MaxDepth) return null;
            var name = (node.Attribute("name")?.Value ?? "").Trim();
            switch (node.Attribute("type")?.Value)
            {
                case "1":
                {
                    if (First(node, "point_symbol") is not { } p) return null;
                    var elements = PointElements(p);
                    if (elements.Count == 0) return null;
                    return new MapSymbol { Number = number, Name = name, SymbolKind = MapSymbol.Kind.Point, ColorHex = elements.DrawingOrder().Last().ColorHex, IsEnabled = true, PointElements = elements, ColorPriority = elements.Min(e => e.Priority) };
                }
                case "2":
                    return First(node, "line_symbol") is { } l && LineOf(l) is { } line ? line.Symbol(number, name) : null;
                case "4":
                    return First(node, "area_symbol") is { } a ? AreaSymbol(new[] { a }, number, name) : null;
                case "8":
                {
                    int color = Int(First(First(node, "text_symbol"), "text")?.Attribute("color")?.Value) ?? -1;
                    return new MapSymbol { Number = number, Name = name, SymbolKind = MapSymbol.Kind.Text, ColorHex = HexOf(color) ?? "#000000", IsEnabled = true, ColorPriority = Priority(color) };
                }
                case "16":
                    return Combined(node, number, name, depth);
                default:
                    return null;
            }
        }

        private List<XElement> Parts(XElement node) => Children(First(node, "combined_symbol"), "part")
            .Select(part => part.Attribute("symbol")?.Value is { } r ? (_byId.TryGetValue(r, out var e) ? e : null) : First(part, "symbol"))
            .Where(e => e is not null).Select(e => e!).ToList();

        private MapSymbol? Combined(XElement node, int number, string name, int depth)
        {
            var parts = Parts(node);
            var areaParts = parts.Where(p => p.Attribute("type")?.Value == "4").Select(p => First(p, "area_symbol")).Where(a => a is not null).Select(a => a!).ToList();
            if (areaParts.Count > 0 && AreaSymbol(areaParts, number, name) is { } m) return m;
            var lines = parts.Where(p => p.Attribute("type")?.Value == "2").Select(p => First(p, "line_symbol")).Where(l => l is not null).Select(l => LineOf(l!)).Where(l => l is not null).Select(l => l!).ToList();
            if (lines.Count == 0)
            {
                foreach (var p in parts)
                {
                    if (Symbol(p, number, depth + 1) is { } s) { s.Name = name; return s; }
                }
                return null;
            }
            var dbl = lines.FirstOrDefault(l => l.Casing is not null);
            if (dbl is not null)
            {
                var dashed = lines.FirstOrDefault(l => l.Casing is null && l.Dash is not null && l.ColorIndex == dbl.Casing!.Value.ColorIndex);
                if (dashed is not null)
                {
                    dbl.InnerDash = dashed.Dash is { } d ? new List<double> { d[1], d[0] } : null;
                    return dbl.Symbol(number, name);
                }
            }
            return lines.MaxBy(l => l.TotalWidth)!.Symbol(number, name);
        }

        private Line? LineOf(XElement l)
        {
            double Mm(string k, XElement? n = null) => D((n ?? l).Attribute(k)?.Value) / 1000;
            double R(double v) => Math.Round(v * 1000) / 1000;
            int color = Int(l.Attribute("color")?.Value) ?? -1;
            double width = Mm("line_width");
            bool dashed = l.Attribute("dashed")?.Value == "true";
            var dash = dashed && Mm("dash_length") > 0 ? new List<double> { Mm("dash_length"), Mm("break_length") } : null;
            string? hex = HexOf(color);
            int? prio = Priority(color);
            (int, string, int?, double, double, List<double>?)? casing = null;
            if (Children(First(l, "borders"), "border").FirstOrDefault() is { } b && Int(b.Attribute("color")?.Value) is int bc && bc >= 0 && HexOf(bc) is { } bhex)
            {
                double bw = Mm("width", b), shift = Mm("shift", b);
                double total = R((width / 2 + shift + bw / 2) * 2);
                double inner = R(Math.Max((width / 2 + shift - bw / 2) * 2, 0.01));
                var bdash = b.Attribute("dashed")?.Value == "true" && Mm("dash_length", b) > 0 ? new List<double> { Mm("dash_length", b), Mm("break_length", b) } : null;
                casing = (bc, bhex, Priority(bc), total, inner, bdash);
            }
            if (hex is null && casing is null && First(First(First(l, "mid_symbol"), "symbol"), "point_symbol") is { } mid)
            {
                var els = PointElements(mid);
                if (els.Count == 0) return null;
                var top = els.DrawingOrder().Last();
                hex = top.ColorHex;
                prio = top.Priority;
                width = Math.Max(els.DiameterMM(), 0.1);
            }
            var fill = hex ?? (casing is not null ? "#FFFFFF" : null);
            if (fill is null || !(width > 0 || casing is not null)) return null;
            return new Line { ColorIndex = color, ColorHex = fill, Priority = prio, Width = R(Math.Max(width, 0.01)), Dash = dash?.Select(R).ToList(), RoundCaps = l.Attribute("cap_style")?.Value == "1", Casing = casing };
        }

        private MapSymbol? AreaSymbol(IReadOnlyList<XElement> areas, int number, string name)
        {
            var patterns = areas.SelectMany(AreaPatterns).ToList();
            (string Hex, int? Priority)? fill = null;
            foreach (var a in areas)
                if (Int(a.Attribute("inner_color")?.Value) is int c && c >= 0 && HexOf(c) is { } h) { fill = (h, Priority(c)); break; }
            if (fill is null && patterns.Count > 0) fill = ("#FFFFFF", ColorPriority(patterns[0].ColorHex));
            fill ??= areas.Select(AreaColor).FirstOrDefault(c => c is not null);
            if (fill is null) return null;
            return new MapSymbol { Number = number, Name = name, SymbolKind = MapSymbol.Kind.Area, ColorHex = fill.Value.Hex, IsEnabled = true, ColorPriority = fill.Value.Priority, Patterns = patterns.Count == 0 ? null : patterns };
        }

        private List<AreaPattern> AreaPatterns(XElement a)
        {
            var result = new List<AreaPattern>();
            foreach (var p in Children(a, "pattern"))
            {
                double Mm(string k) => D(p.Attribute(k)?.Value) / 1000;
                double oomDeg = Math.Round(D(p.Attribute("angle")?.Value) * 180 / Math.PI * 100) / 100;
                double angle = ((90 - oomDeg) % 180 + 180) % 180;
                double spacing = Mm("line_spacing");
                if (!(spacing > 0)) continue;
                if (p.Attribute("type")?.Value == "1")
                {
                    if (Int(p.Attribute("color")?.Value) is not int c || c < 0 || HexOf(c) is not { } h || !(Mm("line_width") > 0)) continue;
                    result.Add(new AreaPattern { PatternKind = AreaPattern.Kind.Lines, ColorHex = h, SizeMM = Mm("line_width"), SpacingMM = spacing, AngleDeg = angle });
                    continue;
                }
                if (First(First(p, "symbol"), "point_symbol") is not { } ps) continue;
                var els = PointElements(ps);
                double cell = Math.Max(Mm("point_distance"), 0.01) * spacing;
                var dots = els.Where(e => e.ElementKind == PointElement.Kind.Dot).ToList();
                if (dots.Count > 0)
                {
                    var d = dots[0];
                    bool white = d.ColorHex.ToUpperInvariant() == "#FFFFFF";
                    result.Add(new AreaPattern { PatternKind = white ? AreaPattern.Kind.Holes : AreaPattern.Kind.Dots, ColorHex = d.ColorHex, SizeMM = d.DiameterMM, SpacingMM = Math.Sqrt(cell / dots.Count), AngleDeg = angle });
                    continue;
                }
                var polys = els.Where(e => e.ElementKind == PointElement.Kind.Area && e.Points.Count >= 3).ToList();
                if (polys.Count > 0)
                {
                    var t = polys[0];
                    result.Add(new AreaPattern { PatternKind = AreaPattern.Kind.Triangles, ColorHex = t.ColorHex, SizeMM = t.Points.Max(q => q.X) - t.Points.Min(q => q.X), SpacingMM = Math.Sqrt(cell / polys.Count), AngleDeg = angle });
                }
            }
            return result;
        }

        private (string Hex, int? Priority)? AreaColor(XElement a)
        {
            if (Int(a.Attribute("inner_color")?.Value) is int c && c >= 0 && HexOf(c) is { } h) return (h, Priority(c));
            foreach (var p in Children(a, "pattern"))
            {
                if (Int(p.Attribute("color")?.Value) is int pc && pc >= 0 && HexOf(pc) is { } ph) return (ph, Priority(pc));
                if (First(First(p, "symbol"), "point_symbol") is { } ps && PointElements(ps).DrawingOrder().LastOrDefault() is { } top) return (top.ColorHex, top.Priority);
            }
            return null;
        }

        private List<PointElement> PointElements(XElement p, Point2D? offsetIn = null, int depth = 0)
        {
            var offset = offsetIn ?? Point2D.Zero;
            var result = new List<PointElement>();
            if (depth > MaxDepth) return result;
            double Mm(string k) => D(p.Attribute(k)?.Value) / 1000;
            double r = Mm("inner_radius"), ow = Mm("outer_width");
            if (Int(p.Attribute("inner_color")?.Value) is int ic && ic >= 0 && r > 0 && HexOf(ic) is { } ih)
                result.Add(new PointElement { ElementKind = PointElement.Kind.Dot, ColorHex = ih, Priority = Priority(ic) ?? 0, LineWidthMM = 0, DiameterMM = 2 * r, Points = new List<Point2D> { offset } });
            if (Int(p.Attribute("outer_color")?.Value) is int oc && oc >= 0 && ow > 0 && HexOf(oc) is { } oh)
                result.Add(new PointElement { ElementKind = PointElement.Kind.Circle, ColorHex = oh, Priority = Priority(oc) ?? 0, LineWidthMM = ow, DiameterMM = 2 * (r + ow / 2), Points = new List<Point2D> { offset } });
            foreach (var e in Children(p, "element"))
            {
                if (First(e, "symbol") is not { } s || First(e, "object") is not { } obj) continue;
                var pts = Coords(First(obj, "coords")?.Value ?? "").Select(q => new Point2D(q.X + offset.X, q.Y + offset.Y)).ToList();
                switch (s.Attribute("type")?.Value)
                {
                    case "1":
                        if (First(s, "point_symbol") is { } ps) result.AddRange(PointElements(ps, pts.Count > 0 ? pts[0] : offset, depth + 1));
                        break;
                    case "2":
                        if (First(s, "line_symbol") is not { } l || Int(l.Attribute("color")?.Value) is not int lc || lc < 0 || HexOf(lc) is not { } lh || pts.Count <= 1) continue;
                        result.Add(new PointElement { ElementKind = PointElement.Kind.Line, ColorHex = lh, Priority = Priority(lc) ?? 0, LineWidthMM = D(l.Attribute("line_width")?.Value) / 1000, DiameterMM = 0, Points = pts });
                        break;
                    case "4":
                        if (First(s, "area_symbol") is not { } a || AreaColor(a) is not { } fill || pts.Count <= 2) continue;
                        result.Add(new PointElement { ElementKind = PointElement.Kind.Area, ColorHex = fill.Hex, Priority = fill.Priority ?? 0, LineWidthMM = 0, DiameterMM = 0, Points = pts });
                        break;
                }
            }
            return result;
        }

        /// <summary>OOM coordinates "x y flags;…" (µm, y down) → paper mm, y up; curves flattened into 8 straight steps.</summary>
        public static List<Point2D> Coords(string text)
        {
            var raw = new List<(Point2D P, int Flags)>();
            foreach (var item in text.Split(';'))
            {
                var v = item.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => D(x, double.NaN)).Where(x => !double.IsNaN(x)).ToList();
                if (v.Count >= 2) raw.Add((new Point2D(v[0] / 1000, -v[1] / 1000), v.Count > 2 ? (int)v[2] : 0));
            }
            var result = new List<Point2D>();
            int i = 0;
            while (i < raw.Count)
            {
                var a = raw[i];
                result.Add(a.P);
                if ((a.Flags & 1) != 0 && i + 3 < raw.Count)
                {
                    var (c1, c2, b) = (raw[i + 1].P, raw[i + 2].P, raw[i + 3].P);
                    for (int step = 1; step < 8; step++)
                    {
                        double t = step / 8.0, u = 1 - t;
                        result.Add(new Point2D(u * u * u * a.P.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * b.X,
                                               u * u * u * a.P.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * b.Y));
                    }
                    i += 3;
                }
                else i++;
            }
            return result;
        }
    }
}

public static class SymbolSetExtensions
{
    /// <summary>The same set at another size: all widths, dashes and point shapes × k.</summary>
    public static SymbolSet ScaledBy(this SymbolSet set, double k)
    {
        if (Math.Abs(k - 1) <= 1e-9) return set;
        double R(double v) => Math.Round(v * k * 1000) / 1000;
        return new SymbolSet
        {
            Id = set.Id, Name = set.Name, SourceFile = set.SourceFile, Scale = set.Scale, IsEnabled = set.IsEnabled, ReaderVersion = set.ReaderVersion, Extra = set.Extra,
            Symbols = set.Symbols.Select(s => new MapSymbol
            {
                Number = s.Number, Name = s.Name, SymbolKind = s.SymbolKind, ColorHex = s.ColorHex, IsEnabled = s.IsEnabled,
                LineWidthMM = s.LineWidthMM is double w ? R(w) : null,
                LineStyle = s.LineStyle is { } l ? new LineStyle
                {
                    ColorHex = l.ColorHex, WidthMM = R(l.WidthMM), RoundCaps = l.RoundCaps,
                    Casing = l.Casing is { } c ? new LineStyle.CasingStyle { ColorHex = c.ColorHex, InnerWidthMM = R(c.InnerWidthMM) } : null,
                    DashMM = l.DashMM?.Select(R).ToList(), InnerDashMM = l.InnerDashMM?.Select(R).ToList(),
                } : null,
                PointElements = s.PointElements?.Select(e => new PointElement
                {
                    ElementKind = e.ElementKind, ColorHex = e.ColorHex, Priority = e.Priority, LineWidthMM = R(e.LineWidthMM), DiameterMM = R(e.DiameterMM),
                    Points = e.Points.Select(p => new Point2D(p.X * k, p.Y * k)).ToList(), FlatEnds = e.FlatEnds,
                }).ToList(),
                ColorPriority = s.ColorPriority, CasingPriority = s.CasingPriority,
                Patterns = s.Patterns?.Select(p => p.ScaledBy(k)).ToList(), Decoration = s.Decoration?.ScaledBy(k),
                TextSizeMM = s.TextSizeMM is double t ? R(t) : null,
            }).ToList(),
        };
    }

    public static bool IsCourseDesign(this SymbolSet set) => set.Symbols.Count > 0 && set.Symbols.All(s => s.Number is >= 700_000 and < 800_000);

    /// <summary>The IOF standard of the set, from its scale (null if unknown); sets at 1:700 are indoor sets.</summary>
    public static MapStandard? Standard(this SymbolSet set)
    {
        if (set.Scale <= 0) return null;
        return set.Scale == MapStandard.Isinom.BaseScale() ? MapStandard.Isinom : MapStandardExtensions.ForScale(set.Scale);
    }

    /// <summary>Size factor to use this set on a map of mapScale with the same standard.</summary>
    public static double SizeFactor(this SymbolSet set, int mapScale)
    {
        var s = set.Standard();
        if (s == MapStandard.Isinom) return MapStandard.Isinom.Enlargement(mapScale);
        if (s is null || s != MapStandardExtensions.ForScale(mapScale)) return 1;
        return s.Value.Enlargement(mapScale) / s.Value.Enlargement(set.Scale);
    }
}

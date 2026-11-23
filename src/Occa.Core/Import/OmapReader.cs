using System.Globalization;
using System.Xml;
using Occa.Core.Geometry;
using Occa.Core.Vector;

namespace Occa.Core.Import;

public sealed class OmapReadException : Exception
{
    public bool NotGeoreferenced { get; }
    public OmapReadException(string message, bool notGeoreferenced = false) : base(message) => NotGeoreferenced = notGeoreferenced;
    public static OmapReadException NotGeo() => new("The OOM map is not georeferenced (Map → Georeferencing in OOM), so its position is unknown.", true);
}

/// <summary>
/// Reader for OpenOrienteering Mapper maps (.omap / .xmap): lines and areas with bezier curves, their symbol code,
/// name, colour, width and dashes. Position from the OOM georeferencing (lat/lon reference, scale, grivation).
/// Points and text are skipped (reported); area holes are kept. Port of OmapReader.swift on XmlReader.
/// </summary>
public static class OmapReader
{
    private const int CurveStart = 1, HolePoint = 16;

    private sealed class Symbol
    {
        public int Type;
        public string Code = "";
        public string Name = "";
        public int? Color;
        public int? LineWidthUM;
        public List<double>? Dash;
        public List<int> Parts = new();
        public bool HasArea;
    }

    private sealed class ObjectRecord
    {
        public int Type;
        public int Symbol = -1;
        public List<(double X, double Y, int Flags)> Coords = new();
    }

    private sealed class Parsed
    {
        public int Scale;
        public double GrivationDeg;
        public double GridScaleFactor = 1;
        public (double X, double Y) MapRef;
        public LatLon? RefLatLon;
        public List<string> Colors = new();
        public Dictionary<int, Symbol> Symbols = new();
        public List<ObjectRecord> Objects = new();
    }

    private static double D(string? s, double fallback = 0) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static int? I(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static string Hex(double r, double g, double b) { int C(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255); return $"#{C(r):X2}{C(g):X2}{C(b):X2}"; }

    private static Parsed Parse(byte[] data)
    {
        var p = new Parsed();
        var symbolStack = new List<(int? Id, Symbol Symbol)>();
        ObjectRecord? current = null;
        bool inProjected = false;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = false, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data), settings);
            bool advance = true;
            while (!advance || reader.Read())
            {
                advance = true;
                if (reader.NodeType == XmlNodeType.Element)
                {
                    string name = reader.LocalName;
                    bool empty = reader.IsEmptyElement;
                    string? A(string k) => reader.GetAttribute(k);
                    switch (name)
                    {
                        case "georeferencing":
                            p.Scale = I(A("scale")) ?? 0;
                            p.GrivationDeg = D(A("grivation"));
                            p.GridScaleFactor = D(A("grid_scale_factor") ?? A("combined_scale_factor"), 1);
                            break;
                        case "projected_crs": inProjected = true; break;
                        case "ref_point":
                            if (!inProjected && current is null) p.MapRef = (D(A("x")), D(A("y")));
                            break;
                        case "ref_point_deg":
                            if (A("lat") is { } la && A("lon") is { } lo) p.RefLatLon = new LatLon(D(la), D(lo));
                            break;
                        case "color":
                            p.Colors.Add("#000000");
                            if (A("c") is { } cs && A("m") is { } ms && A("y") is { } ys && A("k") is { } ks)
                            {
                                double c = D(cs), m = D(ms), y = D(ys), k = D(ks);
                                p.Colors[^1] = Hex((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
                            }
                            break;
                        case "rgb":
                            if (p.Colors.Count > 0 && A("r") is { } rs && A("g") is { } gs && A("b") is { } bs) p.Colors[^1] = Hex(D(rs), D(gs), D(bs));
                            break;
                        case "symbol":
                            symbolStack.Add((I(A("id")), new Symbol { Type = I(A("type")) ?? 0, Code = A("code") ?? "", Name = A("name") ?? "" }));
                            break;
                        case "line_symbol":
                            if (symbolStack.Count > 0)
                            {
                                var s = symbolStack[0].Symbol;
                                if (s.Color is null && I(A("color")) is int c && c >= 0)
                                {
                                    s.Color = c;
                                    s.LineWidthUM = I(A("line_width"));
                                    if (A("dashed") == "true" && A("dash_length") is { } dl && A("break_length") is { } bl) s.Dash = new List<double> { D(dl) / 1000, D(bl) / 1000 };
                                }
                            }
                            break;
                        case "area_symbol":
                            if (symbolStack.Count > 0)
                            {
                                var s = symbolStack[0].Symbol;
                                s.HasArea = true;
                                if (s.Color is null && I(A("inner_color")) is int ic && ic >= 0) s.Color = ic;
                            }
                            break;
                        case "part":
                            if (symbolStack.Count > 0 && I(A("symbol")) is int refId) symbolStack[0].Symbol.Parts.Add(refId);
                            break;
                        case "object":
                            current = new ObjectRecord { Type = I(A("type")) ?? 0, Symbol = I(A("symbol")) ?? -1 };
                            break;
                        case "coords":
                            if (current is not null && !empty)
                            {
                                var text = reader.ReadElementContentAsString();   // consumes the end tag; nested <coord> elements are not used by OOM v9
                                foreach (var item in text.Split(';'))
                                {
                                    var v = item.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(x => D(x, double.NaN)).Where(x => !double.IsNaN(x)).ToList();
                                    if (v.Count >= 2) current.Coords.Add((v[0], v[1], v.Count > 2 ? (int)v[2] : 0));
                                }
                                // ReadElementContentAsString leaves the reader on the next node: handle it without advancing.
                                advance = false;
                                continue;
                            }
                            break;
                        case "coord":
                            if (current is not null && A("x") is { } cx && A("y") is { } cy) current.Coords.Add((D(cx), D(cy), I(A("flags")) ?? 0));
                            break;
                    }
                    if (empty) HandleEnd(name);
                }
                else if (reader.NodeType == XmlNodeType.EndElement) HandleEnd(reader.LocalName);
            }
        }
        catch (XmlException e)
        {
            throw new OmapReadException($"Cannot read OOM file: {e.Message}");
        }
        return p;

        void HandleEnd(string name)
        {
            switch (name)
            {
                case "projected_crs": inProjected = false; break;
                case "symbol":
                    if (symbolStack.Count == 0) break;
                    var top = symbolStack[^1];
                    symbolStack.RemoveAt(symbolStack.Count - 1);
                    if (symbolStack.Count == 0 && top.Id is int id) p.Symbols[id] = top.Symbol;
                    break;
                case "object":
                    if (current is not null) p.Objects.Add(current);
                    current = null;
                    break;
            }
        }
    }

    /// <summary>Map scale and lat/lon reference point from the georeferencing; null when not georeferenced.</summary>
    public static (int Scale, LatLon Reference)? Georeference(byte[] data)
    {
        var p = Parse(data);
        return p.RefLatLon is { } ll && p.Scale > 0 ? (p.Scale, ll) : null;
    }

    private static Symbol? Resolved(Parsed p, int id, int depth = 0)
    {
        if (!p.Symbols.TryGetValue(id, out var s)) return null;
        if (s.Color is null && depth < 4)
        {
            foreach (var r in s.Parts)
            {
                var part = Resolved(p, r, depth + 1);
                if (part?.Color is null) continue;
                s.Color = part.Color;
                s.LineWidthUM ??= part.LineWidthUM;
                s.Dash ??= part.Dash;
                s.HasArea = s.HasArea || part.HasArea;
                break;
            }
        }
        return s;
    }

    public static (List<Feature> Features, OcadImportReport Report) Read(byte[] data, UtmZone zone)
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
        var report = new OcadImportReport();
        var result = new List<Feature>();
        foreach (var o in p.Objects)
        {
            if (o.Type == 0) { report.SkippedPoints++; continue; }
            if (o.Type != 1) { report.SkippedText++; continue; }
            var sym = Resolved(p, o.Symbol);
            bool isArea = sym?.Type == 4 || (sym?.Type == 16 && sym.HasArea);
            var parts = new List<List<(double X, double Y, int Flags)>> { new() };
            foreach (var c in o.Coords)
            {
                parts[^1].Add(c);
                if ((c.Flags & HolePoint) != 0) parts.Add(new());
            }
            parts.RemoveAll(x => x.Count == 0);
            List<BezierNode> Ring(List<(double X, double Y, int Flags)> pts)
            {
                var nodes = new List<BezierNode>();
                if (pts.Count == 0) return nodes;
                nodes.Add(new BezierNode(World(pts[0].X, pts[0].Y)));
                int i = 0;
                while (i < pts.Count - 1)
                {
                    if ((pts[i].Flags & CurveStart) != 0 && i + 3 < pts.Count)
                    {
                        nodes[^1].OutHandle = World(pts[i + 1].X, pts[i + 1].Y);
                        nodes.Add(new BezierNode(World(pts[i + 3].X, pts[i + 3].Y), World(pts[i + 2].X, pts[i + 2].Y)));
                        i += 3;
                    }
                    else { nodes.Add(new BezierNode(World(pts[i + 1].X, pts[i + 1].Y))); i++; }
                }
                if (nodes.Count > 1 && nodes[0].Point.DistanceTo(nodes[^1].Point) < 1e-6)
                {
                    var last = nodes[^1];
                    nodes.RemoveAt(nodes.Count - 1);
                    nodes[0].InHandle = last.InHandle;
                }
                return nodes;
            }
            if (parts.Count == 0) continue;
            var outer = Ring(parts[0]);
            if (outer.Count < (isArea ? 3 : 2)) continue;
            var holes = parts.Skip(1).Select(Ring).Where(r => r.Count >= 3).ToList();
            if (!isArea && parts.Count > 1) report.DroppedHoles += parts.Count - 1;
            var code = sym?.Code ?? "";
            var f = new Feature
            {
                Name = string.Join(" ", new[] { code, sym?.Name ?? "" }.Where(x => x.Length > 0)),
                Kind = isArea ? FeatureKind.Area : FeatureKind.Line,
                ColorHex = sym?.Color is int ci && ci >= 0 && ci < p.Colors.Count ? p.Colors[ci] : "#000000",
                WidthMM = sym?.LineWidthUM is int um ? um / 1000.0 : (isArea ? 0.1 : 0.14),
                Nodes = outer, SymbolCode = code.Length == 0 ? null : code, SymbolName = sym?.Name,
            };
            f.StandardWidthMM = f.WidthMM;
            if (!isArea) f.DashMM = sym?.Dash;
            if (isArea && holes.Count > 0) { f.Holes = holes; f.NormalizeHoles(); }
            result.Add(f);
            report.Imported++;
        }
        return (result, report);
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Occa.Core.Geometry;
using Occa.Core.Vector;

namespace Occa.Core.Import;

public enum OcadReadErrorKind { NotOcad, UnsupportedVersion, Corrupt, NotGeoreferenced }

public sealed class OcadReadException : Exception
{
    public OcadReadErrorKind Kind { get; }
    public int Version { get; }

    public OcadReadException(OcadReadErrorKind kind, string? detail = null, int version = 0) : base(Describe(kind, detail, version))
    {
        Kind = kind;
        Version = version;
    }

    private static string Describe(OcadReadErrorKind kind, string? detail, int version) => kind switch
    {
        OcadReadErrorKind.NotOcad => "Not an OCAD file (.ocd).",
        OcadReadErrorKind.UnsupportedVersion => $"OCAD version {version} is not supported yet. Re-save it in OCAD 10 or later.",
        OcadReadErrorKind.Corrupt => $"Corrupt OCAD file: {detail}.",
        _ => "This OCAD map is not georeferenced (real-world coordinates are off), so its position cannot be determined.",
    };
}

public sealed class OcadImportReport
{
    public int Imported { get; set; }
    public int SkippedPoints { get; set; }
    public int SkippedText { get; set; }
    public int DroppedHoles { get; set; }
}

/// <summary>Reader for OCAD (.ocd) files, versions 10–2018 (port of OcadReader.swift). All reads are bounds-checked.</summary>
public sealed class OcadMap
{
    public sealed class Symbol
    {
        public int Number;
        public byte SymbolKind;
        public string Name = "";
        public int ColorNumber;
        public double? LineWidthMM;
        public (double WidthMM, double EdgeWidthMM, int? FillColor, int EdgeColor)? DoubleLine;
        public (double WidthMM, int Color)? Background;
        public int LineCapStyle = 1;
        public (double Length, double Gap)? Dash;
        public (double Length, double Gap)? DoubleDash;
        public List<(int Kind, int Color, double Lw, double Diam, List<Point2D> Pts)> RawPointElements = new();
        /// <summary>null = unknown (OCAD 10); otherwise the fill colour when the fill is on, or null when off.</summary>
        public bool HasAreaInfo;
        public int? AreaFill;
        public List<(int Color, double Lw, double Dist, double Angle)> RawHatch = new();
        public (int Color, double Diam, double Width, double Height, double Angle)? RawStructure;

        public string Code => $"{Number / 1000}.{Number % 1000}";
    }

    public sealed record ObjectRecord(int Symbol, byte Kind, List<(double X, double Y, byte XFlags, byte YFlags)> Points);

    public int Version;
    public double Scale;
    public Point2D? RealWorldOffset;
    public double GridAngleDeg;
    public Dictionary<int, Symbol> Symbols = new();
    public Dictionary<int, string> Colors = new();
    public Dictionary<int, int> ColorOrder = new();
    public List<ObjectRecord> Objects = new();

    public LineStyle? LineStyleOf(Symbol s)
    {
        double Mm(double v) => Math.Round(v * 100) / 100;
        if (s.DoubleLine is { } d)
        {
            return new LineStyle
            {
                ColorHex = d.FillColor is int fc && Colors.TryGetValue(fc, out var fh) ? fh : "#FFFFFF",
                WidthMM = Mm(d.WidthMM + d.EdgeWidthMM),
                Casing = new LineStyle.CasingStyle { ColorHex = Colors.TryGetValue(d.EdgeColor, out var eh) ? eh : "#000000", InnerWidthMM = Math.Max(Mm(d.WidthMM - d.EdgeWidthMM), 0.01) },
                DashMM = s.DoubleDash is { } dd ? new List<double> { dd.Length, dd.Gap } : null,
                RoundCaps = s.LineCapStyle == 1,
            };
        }
        if (s.LineWidthMM is not double w) return null;
        if (s.Background is { } bg)
        {
            return new LineStyle
            {
                ColorHex = Colors.TryGetValue(s.ColorNumber, out var ch) ? ch : "#000000", WidthMM = Mm(bg.WidthMM),
                Casing = new LineStyle.CasingStyle { ColorHex = Colors.TryGetValue(bg.Color, out var bh) ? bh : "#000000", InnerWidthMM = Mm(w) },
                InnerDashMM = s.Dash is { } sd ? new List<double> { sd.Length, sd.Gap } : null, RoundCaps = s.LineCapStyle == 1,
            };
        }
        return new LineStyle
        {
            ColorHex = Colors.TryGetValue(s.ColorNumber, out var c) ? c : "#000000", WidthMM = w,
            DashMM = s.Dash is { } sd2 ? new List<double> { sd2.Length, sd2.Gap } : null, RoundCaps = s.LineCapStyle == 1,
        };
    }

    public List<PointElement> PointElementsOf(Symbol s) => s.RawPointElements.Where(e => e.Kind is >= 1 and <= 4).Select(e => new PointElement
    {
        ElementKind = (PointElement.Kind)e.Kind, ColorHex = Colors.TryGetValue(e.Color, out var h) ? h : "#000000",
        Priority = ColorOrder.TryGetValue(e.Color, out var p) ? p : 0, LineWidthMM = e.Lw, DiameterMM = e.Diam,
        Points = e.Pts.Count == 0 ? new List<Point2D> { Point2D.Zero } : e.Pts,
    }).ToList();

    /// <summary>Convert objects into Features in project UTM: paper mm → metres, rotated by the grid angle, shifted by the offset.</summary>
    public (List<Feature> Features, OcadImportReport Report) Features()
    {
        if (RealWorldOffset is not { } offset) throw new OcadReadException(OcadReadErrorKind.NotGeoreferenced);
        double a = GridAngleDeg * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
        Point2D World(double x, double y)
        {
            double mx = x / 1000 * Scale, my = y / 1000 * Scale;
            return new Point2D(offset.X + mx * ca - my * sa, offset.Y + mx * sa + my * ca);
        }
        var report = new OcadImportReport();
        var result = new List<Feature>();
        foreach (var obj in Objects)
        {
            if (obj.Kind == 1)
            {
                if (!Symbols.TryGetValue(obj.Symbol, out var ps) || obj.Points.Count == 0) { report.SkippedPoints++; continue; }
                var elements = PointElementsOf(ps);
                if (elements.Count == 0) { report.SkippedPoints++; continue; }
                var first = obj.Points[0];
                result.Add(Feature.PointAt(World(first.X, first.Y), elements, name: $"{ps.Code} {ps.Name}", symbolCode: ps.Code, symbolName: ps.Name));
                report.Imported++;
                continue;
            }
            if (obj.Kind is not (2 or 3 or 7)) { report.SkippedText++; continue; }
            bool isArea = obj.Kind is 3 or 7;
            var parts = new List<List<(double X, double Y, byte XFlags, byte YFlags)>>();
            int partStart = 0;
            for (int k = 1; k < obj.Points.Count; k++)
            {
                if ((obj.Points[k].YFlags & OcadReader.YHoleStart) == 0) continue;
                parts.Add(obj.Points.GetRange(partStart, k - partStart));
                partStart = k;
            }
            parts.Add(obj.Points.GetRange(partStart, obj.Points.Count - partStart));
            List<BezierNode> Ring(List<(double X, double Y, byte XFlags, byte YFlags)> pts)
            {
                var nodes = new List<BezierNode>();
                int i = 0;
                while (i < pts.Count)
                {
                    var p = pts[i];
                    if ((p.XFlags & OcadReader.XFirstBezier) != 0 && i + 2 < pts.Count && nodes.Count > 0)
                    {
                        nodes[^1].OutHandle = World(p.X, p.Y);
                        var c2 = pts[i + 1]; var end = pts[i + 2];
                        nodes.Add(new BezierNode(World(end.X, end.Y), World(c2.X, c2.Y)));
                        i += 3;
                    }
                    else { nodes.Add(new BezierNode(World(p.X, p.Y))); i++; }
                }
                if (isArea && nodes.Count > 1 && nodes[0].Point.DistanceTo(nodes[^1].Point) < 1e-6)
                {
                    var last = nodes[^1];
                    nodes.RemoveAt(nodes.Count - 1);
                    nodes[0].InHandle = last.InHandle;
                }
                return nodes;
            }
            var outer = Ring(parts[0]);
            if (outer.Count < (isArea ? 3 : 2)) continue;
            var holes = isArea ? parts.Skip(1).Select(Ring).Where(r => r.Count >= 3).ToList() : new List<List<BezierNode>>();
            if (!isArea && parts.Count > 1) report.DroppedHoles += parts.Count - 1;
            Symbols.TryGetValue(obj.Symbol, out var sym);
            var code = sym?.Code ?? $"{obj.Symbol / 1000}.{obj.Symbol % 1000}";
            var style = isArea || sym is null ? null : LineStyleOf(sym);
            var f = new Feature
            {
                Name = string.Join(" ", new[] { code, sym?.Name ?? "" }.Where(x => x.Length > 0)),
                Kind = isArea ? FeatureKind.Area : FeatureKind.Line,
                ColorHex = style?.ColorHex ?? (sym is not null && Colors.TryGetValue(sym.ColorNumber, out var ch) ? ch : "#000000"),
                WidthMM = style?.WidthMM ?? sym?.LineWidthMM ?? 0.14,
                Nodes = outer, SymbolCode = code, SymbolName = sym?.Name,
            };
            if (style is not null) f.Apply(style);
            if (holes.Count > 0) { f.Holes = holes; f.NormalizeHoles(); }
            result.Add(f);
            report.Imported++;
        }
        return (result, report);
    }

    /// <summary>This file's symbol list as a symbol set, sorted by number (port of OcadMap.symbolSet).</summary>
    public SymbolSet ToSymbolSet(string name, string sourceFile)
    {
        var symbols = Symbols.Values.OrderBy(s => s.Number).Select(s =>
        {
            var kind = s.SymbolKind switch { 1 => MapSymbol.Kind.Point, 2 => MapSymbol.Kind.Line, 3 or 7 => MapSymbol.Kind.Area, _ => MapSymbol.Kind.Text };
            var elements = kind == MapSymbol.Kind.Point ? PointElementsOf(s) : new List<PointElement>();
            var style = kind == MapSymbol.Kind.Line ? LineStyleOf(s) : null;
            var patterns = new List<AreaPattern>();
            if (kind == MapSymbol.Kind.Area)
            {
                double OccaAngle(double ocad) => ((90 - ocad) % 180 + 180) % 180;
                foreach (var h in s.RawHatch)
                    if (Colors.TryGetValue(h.Color, out var hex)) patterns.Add(new AreaPattern { PatternKind = AreaPattern.Kind.Lines, ColorHex = hex, SizeMM = h.Lw, SpacingMM = h.Dist, AngleDeg = OccaAngle(h.Angle) });
                if (s.RawStructure is { } st && Colors.TryGetValue(st.Color, out var shex))
                    patterns.Add(new AreaPattern { PatternKind = shex.ToUpperInvariant() == "#FFFFFF" ? AreaPattern.Kind.Holes : AreaPattern.Kind.Dots, ColorHex = shex, SizeMM = st.Diam, SpacingMM = Math.Sqrt(st.Width * st.Height), AngleDeg = st.Angle });
            }
            bool patternOnly = kind == MapSymbol.Kind.Area && patterns.Count > 0 && s.HasAreaInfo && s.AreaFill is null;
            string? areaFillHex = s.AreaFill is int af && Colors.TryGetValue(af, out var afh) ? afh : null;
            var color = patternOnly ? "#FFFFFF" : (elements.DrawingOrder().LastOrDefault()?.ColorHex ?? style?.ColorHex ?? areaFillHex ?? (Colors.TryGetValue(s.ColorNumber, out var cc) ? cc : "#000000"));
            var m = new MapSymbol
            {
                Number = s.Number, Name = s.Name.Trim(), SymbolKind = kind, ColorHex = color, LineWidthMM = s.LineWidthMM, IsEnabled = true,
                PointElements = elements.Count == 0 ? null : elements, LineStyle = style, Patterns = patterns.Count == 0 ? null : patterns,
            };
            int? Order(int c) => ColorOrder.TryGetValue(c, out var o) ? o : null;
            if (kind == MapSymbol.Kind.Point && elements.Count > 0) m.ColorPriority = elements.Min(e => e.Priority);
            else if (kind == MapSymbol.Kind.Line && s.Background is { } bg && style?.Casing is not null) { m.ColorPriority = Order(s.ColorNumber); m.CasingPriority = Order(bg.Color); }
            else if (kind == MapSymbol.Kind.Line && s.DoubleLine is { } d && style?.Casing is not null) { m.ColorPriority = (d.FillColor is int fc ? Order(fc) : null) ?? Order(d.EdgeColor); m.CasingPriority = Order(d.EdgeColor); }
            else if (patternOnly) { int first = s.RawHatch.Count > 0 ? s.RawHatch[0].Color : s.RawStructure!.Value.Color; m.ColorPriority = Order(first); }
            else if (kind == MapSymbol.Kind.Area && s.AreaFill is int fill) m.ColorPriority = Order(fill);
            else m.ColorPriority = Order(s.ColorNumber);
            return m;
        }).ToList();
        return new SymbolSet { Name = name, SourceFile = sourceFile, Scale = (int)Scale, Symbols = symbols };
    }
}

public static class OcadReader
{
    public const byte XFirstBezier = 1, XSecondBezier = 2, YHoleStart = 2;
    private const int MaxBlocks = 4096;

    public static OcadMap Read(byte[] data)
    {
        var r = new Bytes(data);
        if (data.Length < 48 || r.U16(0) != 0x0CAD) throw new OcadReadException(OcadReadErrorKind.NotOcad);
        int version = r.I16(4);
        if (version is < 10 or > 2018) throw new OcadReadException(OcadReadErrorKind.UnsupportedVersion, version: version);
        int b = version >= 11 ? 796 : 572;
        int elementHeader = version >= 11 ? 56 : 40;
        int nItemOffset = version >= 11 ? 44 : 8;
        int symbolBlock = (int)r.U32(8), objectBlock = (int)r.U32(12), stringBlock = (int)r.U32(32);

        var map = new OcadMap { Version = version };
        string? scalePar = null;
        Walk(stringBlock, r, 16, o =>
        {
            int pos = (int)r.U32(o), len = (int)r.U32(o + 4), type = r.I32(o + 8);
            if (pos <= 0 || len <= 0 || (type != 9 && type != 1039)) return;
            var text = r.String(pos, len);
            if (type == 1039) scalePar = text;
            else if (ParseColor(text) is { } c) { map.Colors[c.Number] = c.Hex; map.ColorOrder[c.Number] = map.ColorOrder.Count; }
        });
        var par = ParseParams(scalePar ?? "");
        double scale = par.TryGetValue("m", out var ms) && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) ? sc : 0;
        if (!(scale > 0)) throw new OcadReadException(OcadReadErrorKind.Corrupt, "map scale missing");
        map.Scale = scale;
        if (par.TryGetValue("r", out var rr) && rr == "1" && par.TryGetValue("x", out var xs) && par.TryGetValue("y", out var ys)
            && double.TryParse(xs, NumberStyles.Float, CultureInfo.InvariantCulture, out var ox) && double.TryParse(ys, NumberStyles.Float, CultureInfo.InvariantCulture, out var oy))
            map.RealWorldOffset = new Point2D(ox, oy);
        map.GridAngleDeg = par.TryGetValue("a", out var ang) && double.TryParse(ang, NumberStyles.Float, CultureInfo.InvariantCulture, out var av) ? av : 0;

        Walk(symbolBlock, r, 4, o =>
        {
            int sp = (int)r.U32(o);
            if (sp <= 0) return;
            int size = r.I32(sp);
            if (size < (version >= 11 ? 184 : 88)) throw new OcadReadException(OcadReadErrorKind.Corrupt, "symbol too short");
            var sym = new OcadMap.Symbol { Number = r.I32(sp + 4), SymbolKind = r.U8(sp + 8), ColorNumber = r.I16(sp + 28) };
            sym.Name = version >= 11 ? r.Utf16(sp + 56, 64) : r.ShortString(sp + 56, 31);
            if (sym.SymbolKind == 2 && size >= b + 44)
            {
                int lineColor = r.I16(sp + b), lw = r.I16(sp + b + 2);
                if (lw > 0) { sym.LineWidthMM = lw / 100.0; sym.ColorNumber = lineColor; }
                int dblMode = r.I16(sp + b + 26), dblFlags = r.I16(sp + b + 28), dblWidth = r.I16(sp + b + 36);
                int edge = (r.I16(sp + b + 38) + r.I16(sp + b + 40)) / 2;
                if (lw == 0 && dblMode != 0 && dblWidth > 0)
                    sym.DoubleLine = (dblWidth / 100.0, edge / 100.0, (dblFlags & 1) != 0 ? r.I16(sp + b + 30) : null, r.I16(sp + b + 32));
                else if (lw > 0 && dblMode != 0 && (dblFlags & 1) != 0 && dblWidth > lw)
                    sym.Background = (dblWidth / 100.0, r.I16(sp + b + 30));
            }
            if (sym.SymbolKind == 1 && size >= b + 4)
            {
                int dataSize = r.U16(sp + b);
                int eo = sp + b + 4, end = Math.Min(eo + dataSize * 8, sp + size);
                while (eo + 16 <= end)
                {
                    int type = r.I16(eo), col = r.I16(eo + 4);
                    double lw = r.I16(eo + 6) / 100.0, diam = r.I16(eo + 8) / 100.0;
                    int n = r.I16(eo + 10);
                    if (type is < 1 or > 4 || n is < 0 or > 10_000 || eo + 16 + n * 8 > end) break;
                    var pts = new List<Point2D>();
                    for (int k = 0; k < n; k++) pts.Add(new Point2D((r.I32(eo + 16 + k * 8) >> 8) / 100.0, (r.I32(eo + 20 + k * 8) >> 8) / 100.0));
                    sym.RawPointElements.Add((type, col, lw, diam, pts));
                    eo += 16 + n * 8;
                }
            }
            if (sym.SymbolKind == 3 && version >= 11 && size >= b + 36)
            {
                sym.HasAreaInfo = true;
                bool fillOn = r.U8(sp + b + 18) != 0;
                sym.AreaFill = fillOn ? r.I16(sp + b + 4) : null;
                int hatchMode = r.I16(sp + b + 6), hcol = r.I16(sp + b + 8);
                double hlw = r.I16(sp + b + 10) / 100.0, hdist = r.I16(sp + b + 12) / 100.0, a1 = r.I16(sp + b + 14) / 10.0, a2 = r.I16(sp + b + 16) / 10.0;
                if (hatchMode >= 1 && hlw > 0 && hdist > 0) sym.RawHatch.Add((hcol, hlw, hdist, a1));
                if (hatchMode == 2 && hlw > 0 && hdist > 0) sym.RawHatch.Add((hcol, hlw, hdist, a2));
                int structMode = r.I16(sp + b + 20);
                double sw = r.I16(sp + b + 22) / 100.0, sh = r.I16(sp + b + 24) / 100.0;
                if (structMode >= 1 && sw > 0 && sh > 0 && size >= b + 52)
                {
                    int eo = sp + b + 36;
                    int type = r.I16(eo), col = r.I16(eo + 4);
                    double diam = r.I16(eo + 8) / 100.0;
                    if (type == 4 && diam > 0) sym.RawStructure = (col, diam, sw, sh, r.I16(sp + b + 26) / 10.0);
                }
            }
            if (sym.SymbolKind == 2 && size >= b + 46)
            {
                sym.LineCapStyle = r.I16(sp + b + 4);
                int main = r.I16(sp + b + 10), gap = r.I16(sp + b + 14);
                if (main > 0 && gap > 0) sym.Dash = (main / 100.0, gap / 100.0);
                int dblLength = r.I16(sp + b + 42), dblGap = r.I16(sp + b + 44);
                if (sym.DoubleLine is not null && dblLength > 0 && dblGap > 0) sym.DoubleDash = (dblLength / 100.0, dblGap / 100.0);
            }
            map.Symbols[sym.Number] = sym;
        });

        Walk(objectBlock, r, 40, o =>
        {
            int pos = (int)r.U32(o + 16);
            byte status = r.U8(o + 30);
            if (pos <= 0 || status != 1) return;
            int sym = r.I32(pos);
            byte kind = r.U8(pos + 4);
            int n = (int)r.U32(pos + nItemOffset);
            if (n > 1_000_000) throw new OcadReadException(OcadReadErrorKind.Corrupt, "implausible object point count");
            var pts = new List<(double, double, byte, byte)>(n);
            for (int k = 0; k < n; k++)
            {
                int rx = r.I32(pos + elementHeader + k * 8), ry = r.I32(pos + elementHeader + 4 + k * 8);
                pts.Add(((rx >> 8) / 100.0, (ry >> 8) / 100.0, (byte)rx, (byte)ry));
            }
            map.Objects.Add(new OcadMap.ObjectRecord(sym, kind, pts));
        });
        return map;
    }

    private static void Walk(int first, Bytes r, int entrySize, Action<int> body)
    {
        int pos = first;
        var seen = new HashSet<int>();
        while (pos > 0)
        {
            if (!seen.Add(pos) || seen.Count > MaxBlocks) throw new OcadReadException(OcadReadErrorKind.Corrupt, "circular index chain");
            int next = (int)r.U32(pos);
            for (int i = 0; i < 256; i++) body(pos + 4 + i * entrySize);
            pos = next;
        }
    }

    /// <summary>"Name\tn152\tc0\tm60\ty90\tk20…" → (152, "#RRGGBB").</summary>
    public static (int Number, string Hex)? ParseColor(string s)
    {
        var p = ParseParams(s);
        if (!p.TryGetValue("n", out var ns) || !int.TryParse(ns, out var n)) return null;
        double V(string k) => p.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Math.Clamp(d / 100, 0, 1) : 0;
        double c = V("c"), m = V("m"), y = V("y"), kk = V("k");
        int R(double x) => (int)Math.Round(x * 255);
        return (n, $"#{R((1 - c) * (1 - kk)):X2}{R((1 - m) * (1 - kk)):X2}{R((1 - y) * (1 - kk)):X2}");
    }

    /// <summary>OCAD parameter: leading text, then pairs "\t&lt;code&gt;&lt;value&gt;". The first occurrence of a code wins.</summary>
    public static Dictionary<string, string> ParseParams(string s)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in s.Split('\t').Skip(1))
        {
            if (part.Length == 0) continue;
            var k = part[..1];
            if (!result.ContainsKey(k)) result[k] = part[1..];
        }
        return result;
    }

    /// <summary>Little-endian byte reader with bounds checking.</summary>
    public sealed class Bytes
    {
        private readonly byte[] _d;
        public Bytes(byte[] d) => _d = d;

        private void Check(int o, int n)
        {
            if (o < 0 || n < 0 || o > _d.Length - n) throw new OcadReadException(OcadReadErrorKind.Corrupt, $"pointer outside file ({o})");
        }

        public byte U8(int o) { Check(o, 1); return _d[o]; }
        public ushort U16(int o) { Check(o, 2); return BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(o)); }
        public short I16(int o) { Check(o, 2); return BinaryPrimitives.ReadInt16LittleEndian(_d.AsSpan(o)); }
        public uint U32(int o) { Check(o, 4); return BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(o)); }
        public int I32(int o) { Check(o, 4); return BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(o)); }

        public string String(int o, int len)
        {
            Check(o, len);
            var span = _d.AsSpan(o, len);
            int end = span.IndexOf((byte)0);
            if (end >= 0) span = span[..end];
            try { return new UTF8Encoding(false, true).GetString(span); } catch (DecoderFallbackException) { return Encoding.ASCII.GetString(span); }
        }

        public string ShortString(int o, int capacity)
        {
            int n = Math.Min(U8(o), capacity);
            Check(o + 1, n);
            return Encoding.Latin1.GetString(_d, o + 1, n);
        }

        public string Utf16(int o, int maxChars)
        {
            Check(o, maxChars * 2);
            var sb = new StringBuilder();
            for (int i = 0; i < maxChars; i++)
            {
                ushort u = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(o + i * 2));
                if (u == 0) break;
                sb.Append((char)u);
            }
            return sb.ToString();
        }
    }
}

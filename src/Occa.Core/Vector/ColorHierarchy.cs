using Occa.Core.Sheet;

namespace Occa.Core.Vector;

/// <summary>
/// Colour Hierarchy (OCAD colour table / OOM colour priority): colours higher in the table are drawn above colours
/// below them, regardless of object order. Objects of the same colour follow the object order. Port of
/// <c>ColorHierarchy</c> in CakraCore.
/// </summary>
public static class ColorHierarchy
{
    /// <summary>Part of an object being drawn: the whole object, a double line's casing or main line, or point elements at one level.</summary>
    public readonly record struct Part(Part.Kind PartKind, int Level = 0)
    {
        public enum Kind { Whole, Casing, Main, Elements }

        public static readonly Part Whole = new(Kind.Whole);
        public static readonly Part Casing = new(Kind.Casing);
        public static readonly Part Main = new(Kind.Main);
        public static Part Elements(int level) => new(Kind.Elements, level);

        public override string ToString() => PartKind == Kind.Elements ? $"elements:{Level}" : PartKind.ToString().ToLowerInvariant();
    }

    public readonly record struct Paint(int Index, Part Part, int Priority);

    /// <summary>Objects without a symbol (sketches) above all map colours; unknown symbols below everything.</summary>
    public const int SketchPriority = -1;

    private static int FallbackPriority(Feature f)
    {
        if (f.SymbolCode is null) return SketchPriority;
        return f.Kind switch { FeatureKind.Area => 30_000, FeatureKind.Line => 20_000, _ => 10_000 };
    }

    /// <summary>Priority of the object's main colour (0 = topmost).</summary>
    public static int Priority(Feature f)
    {
        if (f.ColorPriority is int p) return p;
        if (f.Kind == FeatureKind.Point && f.SymbolCode is not null && f.PointElements is { Count: > 0 } e) return e.Min(x => x.Priority);
        return FallbackPriority(f);
    }

    /// <summary>Drawing order (bottom → top): higher priority values first; equal priority = object order.</summary>
    public static List<Paint> PaintOrder(IReadOnlyList<Feature> features)
    {
        var paints = new List<Paint>(features.Count);
        for (int i = 0; i < features.Count; i++)
        {
            var f = features[i];
            int main = Priority(f);
            if (f.Kind == FeatureKind.Line && f.CasingColorHex is not null && f.CasingPriority is int casing && casing != main)
            {
                paints.Add(new Paint(i, Part.Casing, casing));
                paints.Add(new Paint(i, Part.Main, main));
            }
            else if (f.Kind == FeatureKind.Point && f.ColorPriority is not null && f.PointElements is not null
                     && f.PointElements.Select(e => e.Priority).Distinct().Count() > 1)
            {
                foreach (var level in f.PointElements.Select(e => e.Priority).Distinct().OrderByDescending(x => x))
                    paints.Add(new Paint(i, Part.Elements(level), level));
            }
            else
            {
                paints.Add(new Paint(i, Part.Whole, main));
            }
        }
        // Stable: object order is preserved for equal priorities (OrderBy is stable).
        return paints.Select((p, order) => (p, order)).OrderByDescending(x => x.p.Priority).ThenBy(x => x.order).Select(x => x.p).ToList();
    }

    /// <summary>Object indices from topmost to bottommost.</summary>
    public static List<int> TopDownIndices(IReadOnlyList<Feature> features)
    {
        var seen = new HashSet<int>();
        var result = new List<int>();
        foreach (var p in PaintOrder(features).AsEnumerable().Reverse())
            if (seen.Add(p.Index)) result.Add(p.Index);
        return result;
    }

    /// <summary>Copies of the objects with colour priorities, patterns, decorations and point levels from their symbols.</summary>
    public static List<Feature> WithColorPriorities(this IEnumerable<Feature> features, Func<string, string?, MapSymbol?> lookup)
    {
        var cache = new Dictionary<string, MapSymbol?>();
        var result = new List<Feature>();
        foreach (var f in features)
        {
            if (f.SymbolCode is null) { result.Add(f); continue; }
            var key = f.SymbolCode + "\u0001" + (f.SymbolName ?? "");
            if (!cache.TryGetValue(key, out var s)) { s = lookup(f.SymbolCode, f.SymbolName); cache[key] = s; }
            if (s is null) { result.Add(f); continue; }
            var g = f.ShallowCopy();
            if (s.ColorPriority is not null)
            {
                g.ColorPriority = s.ColorPriority;
                g.CasingPriority = s.CasingPriority;
            }
            if (f.Kind == FeatureKind.Area) g.Patterns = s.SymbolKind == MapSymbol.Kind.Area ? s.Patterns : null;
            if (f.Kind == FeatureKind.Line) g.Decoration = s.SymbolKind == MapSymbol.Kind.Line ? s.Decoration : null;
            if (f.Kind == FeatureKind.Point && s.PointElements is not null && g.PointElements is not null && s.PointElements.Count == g.PointElements.Count)
            {
                var fe = g.PointElements.Select(e => e.Clone()).ToList();
                for (int k = 0; k < fe.Count; k++) fe[k].Priority = s.PointElements[k].Priority;
                g.PointElements = fe;
            }
            result.Add(g);
        }
        return result;
    }

    // MARK: One colour order for all symbol sets (IOF Printing and Colour Definitions Rev 4)

    /// <summary>A row of the IOF colour table: name, CMYK (0–100), the standards using it, and the symbol kinds it matches.</summary>
    public sealed record IofColorRow(string Id, string Name, double[] Cmyk, bool Isom, bool Sprint, MapSymbol.Kind[] Match, bool Edge = false)
    {
        public string Hex => new OccaSymbolFile.Color { Id = Id, Name = Name, Cmyk = Cmyk.Select(v => v / 100).ToList() }.Hex;
        public bool Uses(MapStandard s) => s == MapStandard.Isom ? Isom : Sprint;
    }

    private static readonly MapSymbol.Kind[] P = { MapSymbol.Kind.Point };
    private static readonly MapSymbol.Kind[] L = { MapSymbol.Kind.Line };
    private static readonly MapSymbol.Kind[] A = { MapSymbol.Kind.Area };
    private static readonly MapSymbol.Kind[] All = { MapSymbol.Kind.Point, MapSymbol.Kind.Line, MapSymbol.Kind.Area, MapSymbol.Kind.Text };
    private static readonly MapSymbol.Kind[] None = Array.Empty<MapSymbol.Kind>();

    private static IofColorRow R(string id, string name, double[] cmyk, string standards, MapSymbol.Kind[] match, bool edge = false)
        => new(id, name, cmyk, standards.Contains('I'), standards.Contains('S'), match, edge);

    /// <summary>The Rev 4 table rows for ISOM (I) and ISSprOM (S), top → bottom: the same list as <c>OccaStandardSymbols.colorRows</c>.</summary>
    public static readonly IofColorRow[] ColorRows =
    {
        R("purpleUpper", "Upper purple for course overprint", new double[] { 35, 85, 0, 0 }, "IS", A),
        R("whiteCourse", "White for course overprint", new double[] { 0, 0, 0, 0 }, "S", None),
        R("purple50", "Purple 50% area symbol", new double[] { 18, 43, 0, 0 }, "S", All),
        R("whiteRailroad", "White for railroad", new double[] { 0, 0, 0, 0 }, "IS", L),
        R("black", "Black 100%", new double[] { 0, 0, 0, 100 }, "IS", All),
        R("bluePoint", "Blue 100% point symbols", new double[] { 100, 0, 0, 0 }, "I", P),
        R("brownPoint", "Brown 100% point symbols", new double[] { 25, 75, 100, 0 }, "I", P),
        R("whitePoint", "White for point symbols (OCCA)", new double[] { 0, 0, 0, 0 }, "IS", None),
        R("greenPoint", "Green 100% point symbols", new double[] { 80, 0, 100, 0 }, "IS", new[] { MapSymbol.Kind.Point, MapSymbol.Kind.Line }),
        R("blueLine", "Blue 100% line symbols", new double[] { 100, 0, 0, 0 }, "I", L),
        R("darkGreenEdge", "Dark green for forest edges", new double[] { 100, 0, 80, 30 }, "I", All),
        R("brownLine", "Brown 100% line symbols", new double[] { 25, 75, 100, 0 }, "I", new[] { MapSymbol.Kind.Line, MapSymbol.Kind.Area, MapSymbol.Kind.Text }),
        R("purpleLower", "Lower purple for course overprint", new double[] { 35, 85, 0, 0 }, "IS", new[] { MapSymbol.Kind.Point, MapSymbol.Kind.Line, MapSymbol.Kind.Text }),
        R("darkGreenLine", "Dark green line symbols", new double[] { 100, 0, 80, 30 }, "S", new[] { MapSymbol.Kind.Point, MapSymbol.Kind.Line }),
        R("bluePointLine", "Blue 100% point and line symbols", new double[] { 100, 0, 0, 0 }, "S", new[] { MapSymbol.Kind.Point, MapSymbol.Kind.Line }),
        R("brown", "Brown 100%", new double[] { 25, 75, 100, 0 }, "S", All),
        R("whiteStripes", "White stripes for area passable at two levels", new double[] { 0, 0, 0, 0 }, "S", None),
        R("brown50Road", "Brown 50% for road infill", new double[] { 10, 35, 50, 0 }, "IS", L),
        R("brown30Road", "Brown 30% for road infill", new double[] { 6, 23, 33, 0 }, "S", L),
        R("blackRoadOutline", "Black 100% for road outline", new double[] { 0, 0, 0, 100 }, "IS", None, edge: true),
        R("black50", "Black 50% for large buildings", new double[] { 0, 0, 0, 50 }, "IS", All),
        R("black20", "Black 20% for canopy", new double[] { 0, 0, 0, 20 }, "IS", All),
        R("blue", "Blue 100% area symbols", new double[] { 100, 0, 0, 0 }, "IS", A),
        R("blue70", "Blue 70% area symbols", new double[] { 70, 0, 0, 0 }, "IS", All),
        R("blue50", "Blue 50% area symbols", new double[] { 50, 0, 0, 0 }, "IS", All),
        R("blue30", "Blue 30% area symbols", new double[] { 30, 0, 0, 0 }, "S", All),
        R("whiteOverGreen", "White over green and brown", new double[] { 0, 0, 0, 0 }, "IS", P),
        R("brown50Paved", "Brown 50% for paved area", new double[] { 10, 35, 50, 0 }, "IS", A),
        R("brown30Paved", "Brown 30% for paved area", new double[] { 6, 23, 33, 0 }, "S", A),
        R("olive", "Olive green", new double[] { 38, 27, 100, 0 }, "IS", All),
        R("darkGreenArea", "Dark green area symbols", new double[] { 100, 0, 80, 30 }, "S", A),
        R("green100", "Green 100% area symbols", new double[] { 80, 0, 100, 0 }, "IS", A),
        R("green60", "Green 60% area symbols", new double[] { 48, 0, 60, 0 }, "IS", All),
        R("green30", "Green 30% area symbols", new double[] { 24, 0, 30, 0 }, "IS", All),
        R("black35", "Black 35% area symbols", new double[] { 0, 0, 0, 35 }, "IS", All),
        R("whiteOverYellow", "White over yellow", new double[] { 0, 0, 0, 0 }, "IS", None),
        R("blackCultivated", "Black for cultivated land and sandy ground", new double[] { 0, 0, 0, 100 }, "IS", None),
        R("yellow100", "Yellow 100% area symbols", new double[] { 0, 27, 79, 0 }, "IS", All),
        R("yellow75", "Yellow 75% area symbols", new double[] { 0, 20, 59, 0 }, "IS", All),
        R("yellow50", "Yellow 50% area symbols", new double[] { 0, 14, 40, 0 }, "IS", All),
        R("white", "White (paper, forest; OCCA)", new double[] { 0, 0, 0, 0 }, "IS", A),
    };

    /// <summary>Row of a colour id in <see cref="ColorRows"/> (the colour priority of OCCA's ISOM / ISSprOM symbols).</summary>
    public static int ColorRow(string id) => Array.FindIndex(ColorRows, r => r.Id == id);

    private sealed record OrderEntry(int Priority, (double R, double G, double B) Rgb, MapSymbol.Kind[] Match, bool Edge);

    private static (double, double, double) Rgb255(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        int v = int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var x) ? x : 0;
        return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }

    private static OrderEntry[] Rows(Func<IofColorRow, bool> keep)
        => ColorRows.Select((r, i) => (r, i)).Where(x => keep(x.r)).Select(x => new OrderEntry(x.i, Rgb255(x.r.Hex), x.r.Match, x.r.Edge)).ToArray();

    private static readonly Lazy<OrderEntry[]> IsomOrder = new(() => Rows(r => r.Isom));
    private static readonly Lazy<OrderEntry[]> SprintOrder = new(() => Rows(r => r.Sprint));
    private static readonly Lazy<OrderEntry[]> BothOrder = new(() => Rows(_ => true));
    private static readonly Lazy<OrderEntry[]> IsinomOrder = new(() => StandardSymbolSets.Isinom.Colors.Select((c, i) => new OrderEntry(i, Rgb255(c.Hex), All, false)).ToArray());

    private static OrderEntry[] Order(MapStandard? standard) => standard switch
    {
        MapStandard.Isinom => IsinomOrder.Value,
        MapStandard.Isom => IsomOrder.Value,
        MapStandard.Issprom => SprintOrder.Value,
        _ => BothOrder.Value,
    };

    /// <summary>Position of a colour in the IOF order of the standard (null = ISOM + ISSprOM): the nearest OCCA colour, resolved by symbol kind.</summary>
    public static int IofPriority(string hex, MapSymbol.Kind kind, MapStandard? standard = null, bool edge = false)
    {
        var c = Rgb255(hex);
        var table = Order(standard);
        double Dist(OrderEntry o) => (c.Item1 - o.Rgb.R) * (c.Item1 - o.Rgb.R) + (c.Item2 - o.Rgb.G) * (c.Item2 - o.Rgb.G) + (c.Item3 - o.Rgb.B) * (c.Item3 - o.Rgb.B);
        if (standard == MapStandard.Isinom)
        {
            int best = int.MaxValue;
            double bestD = double.PositiveInfinity;
            foreach (var o in table)
            {
                double d = Dist(o);
                if (d < bestD - 0.5 || (Math.Abs(d - bestD) <= 0.5 && kind == MapSymbol.Kind.Area)) { best = o.Priority; bestD = d; }
            }
            return best;
        }
        var candidates = table.Where(o => edge ? (o.Edge || o.Match.Length > 0) : o.Match.Length > 0).ToList();
        if (candidates.Count == 0) return int.MaxValue;
        double min = candidates.Min(Dist);
        var near = candidates.Where(o => Dist(o) <= min + 0.5).ToList();
        if (edge && near.FirstOrDefault(o => o.Edge) is { } e) return e.Priority;
        return (near.FirstOrDefault(o => o.Match.Contains(kind)) ?? near[0]).Priority;
    }

    public static int LowerPurplePriority(MapStandard standard = MapStandard.Isom)
        => standard == MapStandard.Isinom ? StandardSymbolSets.Isinom.Colors.FindIndex(c => c.Id == "n52") : ColorRow("purpleLower");

    public static int UpperPurplePriority(MapStandard standard = MapStandard.Isom)
        => standard == MapStandard.Isinom ? StandardSymbolSets.Isinom.Colors.FindIndex(c => c.Id == "n50") : ColorRow("purpleUpper");

    /// <summary>This symbol with its colour priorities in the IOF order of OCCA's sets, so symbols from other sets layer on one map.</summary>
    public static MapSymbol WithIofColorOrder(this MapSymbol symbol, MapStandard? standard = null)
    {
        var s = (MapSymbol)Clone(symbol);
        var kind = symbol.LineStyle?.Casing is not null ? MapSymbol.Kind.Line : symbol.SymbolKind;
        if (symbol.SymbolKind == MapSymbol.Kind.Area && SymbolOrnaments.IsPatternOnly(symbol.ColorHex, symbol.Patterns) && symbol.Patterns is { Count: > 0 } ps)
        {
            bool white = ColorHex.Normalized(ps[0].ColorHex) == "#FFFFFF";
            s.ColorPriority = IofPriority(white ? symbol.ColorHex : ps[0].ColorHex, MapSymbol.Kind.Area, standard);
        }
        else
        {
            s.ColorPriority = IofPriority(symbol.LineStyle?.ColorHex ?? symbol.ColorHex, kind, standard);
        }
        if (symbol.LineStyle?.Casing is { } casing) s.CasingPriority = IofPriority(casing.ColorHex, MapSymbol.Kind.Line, standard, edge: true);
        s.PointElements = symbol.PointElements?.Select(e =>
        {
            var c = e.Clone();
            c.Priority = IofPriority(e.ColorHex, MapSymbol.Kind.Point, standard);
            return c;
        }).ToList();
        if (symbol.SymbolKind == MapSymbol.Kind.Point && s.PointElements is { Count: > 0 } pe) s.ColorPriority = pe.Min(e => e.Priority);
        return s;
    }

    private static object Clone(MapSymbol m) => new MapSymbol
    {
        Number = m.Number, Name = m.Name, SymbolKind = m.SymbolKind, ColorHex = m.ColorHex, LineWidthMM = m.LineWidthMM, IsEnabled = m.IsEnabled,
        PointElements = m.PointElements, LineStyle = m.LineStyle, ColorPriority = m.ColorPriority, CasingPriority = m.CasingPriority,
        Patterns = m.Patterns, Decoration = m.Decoration, TextSizeMM = m.TextSizeMM, Extra = m.Extra,
    };
}

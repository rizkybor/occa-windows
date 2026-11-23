using Occa.Core.Geometry;
using Occa.Core.Sheet;
using Occa.Core.Vector;

namespace Occa.Core.Render;

/// <summary>
/// Renderer-independent description of what to draw for a project, in project coordinates (UTM metres, y north),
/// already in Colour Hierarchy order. The Win2D, PDF and raster renderers only translate these items into their
/// own primitives, as the Mac's <c>FeatureOverlay</c> items feed <c>FeatureRenderer</c>.
/// </summary>
public sealed class MapScene
{
    /// <summary>One ring of an outline: the cubic segments in order; closed when the outline closes.</summary>
    public sealed record Ring(IReadOnlyList<CubicSegment> Segments, bool Closed);

    public abstract record Item
    {
        /// <summary>Colour priority of this item in the IOF colour order (smaller = drawn later / on top); see <c>ColorHierarchy.PaintOrder</c>.</summary>
        public int Priority { get; init; }
    }

    /// <summary>Fill (even-odd over all rings) and/or stroke of a line or area. Widths in metres on the ground.</summary>
    public sealed record Outline(
        Feature Feature,
        IReadOnlyList<Ring> Rings,
        Rect2D Bounds,
        string ColorHex,
        /// <summary>Fill colour and alpha, or null (lines, pattern-only areas).</summary>
        (string Hex, double Alpha)? Fill,
        double WidthM,
        bool RoundCaps,
        IReadOnlyList<double>? DashM,
        /// <summary>Double line: casing colour and the inner (fill) width in metres.</summary>
        (string Hex, double InnerWidthM)? Casing,
        IReadOnlyList<double>? InnerDashM,
        bool FillOnly,
        bool HidesLine,
        IReadOnlyList<AreaPattern>? Patterns,
        double PatternAlpha,
        ulong PatternSeed,
        /// <summary>Decoration marks in metres (already computed along the flattened line).</summary>
        IReadOnlyList<OrnamentMark> Decoration,
        double MetersPerMM,
        ColorHierarchy.Part Part) : Item;

    /// <summary>A point symbol: centre, scaled elements (paper mm, y up) and the colour level to paint (null = all).</summary>
    public sealed record PointSymbol(Feature Feature, Point2D Center, Rect2D Bounds, IReadOnlyList<PointElement> Elements, double MetersPerMM, int? Level) : Item;

    /// <summary>A sketch stroke: polyline in metres, width in metres.</summary>
    public sealed record SketchStroke(Stroke Stroke, Rect2D Bounds, string ColorHex, double WidthM) : Item;

    public IReadOnlyList<Item> Items { get; }
    public IReadOnlyList<SketchStroke> Strokes { get; }
    public double MetersPerMM { get; }

    private MapScene(IReadOnlyList<Item> items, IReadOnlyList<SketchStroke> strokes, double metersPerMM)
    {
        Items = items;
        Strokes = strokes;
        MetersPerMM = metersPerMM;
    }

    /// <summary>Builds the scene: features get their colour levels, patterns and decorations from the symbol sets, then are ordered by colour.</summary>
    public static MapScene Build(IReadOnlyList<Feature> features, IReadOnlyList<Stroke> strokes, SheetSpec sheet, double areaOpacity = 1,
                                 Func<string, string?, MapSymbol?>? lookup = null)
    {
        lookup ??= StandardSymbolSets.LookupFor(sheet);
        double mpm = sheet.GroundMeters(1);
        var resolved = features.WithColorPriorities(lookup);
        var built = resolved.Select(f => BuildItemParts(f, sheet, mpm, areaOpacity)).ToList();
        var items = new List<Item>();
        foreach (var paint in ColorHierarchy.PaintOrder(resolved))
        {
            var parts = built[paint.Index];
            if (parts is null) continue;
            items.Add(parts(paint.Part) with { Priority = paint.Priority });
        }
        var sketch = strokes.Where(s => s.Points.Count > 0)
            .Select(s => new SketchStroke(s, s.Bounds!.Value.InsetBy(-sheet.GroundMeters(s.WidthMM)), s.ColorHex, sheet.GroundMeters(s.WidthMM))).ToList();
        return new MapScene(items, sketch, mpm);
    }

    private static Func<ColorHierarchy.Part, Item>? BuildItemParts(Feature f, SheetSpec sheet, double mpm, double areaOpacity)
    {
        if (!f.IsVisible || f.Nodes.Count == 0 || f.Bounds is not { } b) return null;
        if (f.Kind == FeatureKind.Point)
        {
            var c = f.Nodes[0].Point;
            double r = f.PointRadiusM(sheet);
            var bounds = new Rect2D(c.X - r, c.Y - r, c.X + r, c.Y + r);
            var elements = f.ScaledPointElements;
            return part => new PointSymbol(f, c, bounds, elements, mpm, part.PartKind == ColorHierarchy.Part.Kind.Elements ? part.Level : null);
        }
        var rings = f.RingFeatures.Select(r => new Ring(r.Segments, f.ClosesPath)).ToList();
        double widthM = sheet.GroundMeters(f.WidthMM);
        double reach = Math.Max(widthM * 2, ((f.Decoration?.SizeMM ?? 0) + f.WidthMM) * mpm);
        var rect = b.InsetBy(-reach);
        bool patternOnly = f.IsSymbolArea && SymbolOrnaments.IsPatternOnly(f.ColorHex, f.Patterns);
        (string, double)? fill = f.Kind == FeatureKind.Area && !patternOnly ? (f.ColorHex, f.IsSymbolArea ? areaOpacity : 0.35) : null;
        IReadOnlyList<AreaPattern>? patterns = f.Kind == FeatureKind.Area && f.IsSymbolArea && f.Patterns is { Count: > 0 } ? f.Patterns : null;
        IReadOnlyList<OrnamentMark> decoration = Array.Empty<OrnamentMark>();
        bool hides = false;
        if (f.Kind == FeatureKind.Line && f.Decoration is { } deco)
        {
            var pts = SymbolOrnaments.Polyline(f, p => p);
            decoration = SymbolOrnaments.DecorationMarks(deco, pts, mpm, f.WidthMM, f.ColorHex, yUp: true, f.EffectiveDashMM);
            hides = f.HidesMainLine;
        }
        var dash = f.EffectiveDashMM?.Select(v => v * mpm).ToList();
        (string, double)? casing = f.Kind == FeatureKind.Line && f.CasingColorHex is { } ch && f.InnerWidthMM is double inner ? (ch, inner * mpm) : null;
        var innerDash = casing is not null ? f.EffectiveInnerDashMM?.Select(v => v * mpm).ToList() : null;
        return part => new Outline(f, rings, rect, f.ColorHex, fill, widthM, f.RoundCaps != false, dash, casing, innerDash,
                                   f.IsSymbolArea, hides, patterns, areaOpacity, f.PatternSeed, decoration, mpm, part);
    }
}

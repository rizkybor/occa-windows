using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Occa.Core.Geometry;
using Occa.Core.Render;
using Occa.Core.Sheet;
using Occa.Core.Vector;
using Windows.UI;

namespace Occa.Render;

/// <summary>
/// Draws a <see cref="MapScene"/> with Win2D in the same way the Mac's <c>FeatureRenderer</c> draws its items:
/// area fills (even-odd), area patterns clipped to the outline, lines with dashes and caps, double lines split by
/// colour level, decorations, point symbols per colour level, then sketch strokes. Everything is drawn in project
/// metres under the view transform, so widths follow the map scale; a minimum width in pixels keeps thin lines visible.
/// </summary>
public sealed class MapRenderer
{
    /// <summary>Paper white under the map (CP.mapPaper).</summary>
    public static readonly Color PaperColor = Color.FromArgb(255, 0xFB, 0xF9, 0xF4);

    private readonly Dictionary<Guid, (Feature Feature, CanvasGeometry Geometry)> _geometryCache = new();
    private ICanvasResourceCreator? _device;

    public static Color ToColor(string hex, double alpha = 1)
    {
        var rgb = ColorHex.Rgb(hex) ?? (0, 0, 0);
        return Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), (byte)Math.Round(rgb.R * 255), (byte)Math.Round(rgb.G * 255), (byte)Math.Round(rgb.B * 255));
    }

    public void InvalidateCache()
    {
        foreach (var g in _geometryCache.Values) g.Geometry.Dispose();
        _geometryCache.Clear();
    }

    /// <summary>Draws the scene. The session's transform must be identity on entry; it is restored on exit.</summary>
    public void Draw(CanvasDrawingSession ds, MapScene scene, MapTransform view, SheetSpec sheet, double minLinePx = 0.5)
    {
        if (!ReferenceEquals(_device, ds.Device)) { InvalidateCache(); _device = ds.Device; }
        var visible = view.Visible;
        double ppm = view.PixelsPerMeter;
        double minWidthM = minLinePx / ppm;
        var saved = ds.Transform;
        ds.Transform = view.ToPixels * saved;
        ds.Antialiasing = CanvasAntialiasing.Antialiased;

        foreach (var item in scene.Items)
        {
            switch (item)
            {
                case MapScene.Outline o when o.Bounds.Intersects(visible):
                    DrawOutline(ds, o, visible, ppm, minWidthM);
                    break;
                case MapScene.PointSymbol p when p.Bounds.Intersects(visible):
                    DrawPoint(ds, p, minWidthM);
                    break;
            }
        }
        foreach (var s in scene.Strokes)
        {
            if (!s.Bounds.Intersects(visible)) continue;
            DrawStroke(ds, s, minWidthM);
        }
        ds.Transform = saved;
    }

    // MARK: Outlines

    private CanvasGeometry GeometryFor(ICanvasResourceCreator rc, MapScene.Outline o)
    {
        if (_geometryCache.TryGetValue(o.Feature.Id, out var hit) && ReferenceEquals(hit.Feature, o.Feature)) return hit.Geometry;
        var geometry = BuildGeometry(rc, o.Rings);
        if (hit.Geometry is not null) hit.Geometry.Dispose();
        _geometryCache[o.Feature.Id] = (o.Feature, geometry);
        return geometry;
    }

    private static CanvasGeometry BuildGeometry(ICanvasResourceCreator rc, IReadOnlyList<MapScene.Ring> rings)
    {
        using var builder = new CanvasPathBuilder(rc);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);
        foreach (var ring in rings)
        {
            if (ring.Segments.Count == 0) continue;
            builder.BeginFigure(V(ring.Segments[0].P0));
            foreach (var s in ring.Segments)
            {
                if (s.IsCurve) builder.AddCubicBezier(V(s.C1), V(s.C2), V(s.P3));
                else builder.AddLine(V(s.P3));
            }
            builder.EndFigure(ring.Closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        }
        return CanvasGeometry.CreatePath(builder);
    }

    private static Vector2 V(Point2D p) => new((float)p.X, (float)p.Y);

    private void DrawOutline(CanvasDrawingSession ds, MapScene.Outline o, Rect2D visible, double ppm, double minWidthM)
    {
        var geometry = GeometryFor(ds, o);
        if (o.Fill is { } fill) ds.FillGeometry(geometry, ToColor(fill.Hex, fill.Alpha));
        if (o.Patterns is { Count: > 0 } patterns)
        {
            var rect = visible.Intersection(o.Bounds);
            if (rect.Width > 0 && rect.Height > 0)
            {
                using var layer = ds.CreateLayer((float)o.PatternAlpha, geometry);
                foreach (var p in patterns)
                {
                    // Zoomed far out the marks would be closer than 1.5 px: paint an even tint instead (as Mac).
                    var marks = p.SpacingMM * o.MetersPerMM * ppm < 1.5 ? null : SymbolOrnaments.PatternMarks(p, rect, o.MetersPerMM, yUp: true, seed: o.PatternSeed);
                    if (marks is not null) DrawMarks(ds, marks, o.MetersPerMM, minWidthM * 0.8);
                    else ds.FillRectangle((float)rect.MinX, (float)rect.MinY, (float)rect.Width, (float)rect.Height, ToColor(p.ColorHex, SymbolOrnaments.Tint(p)));
                }
            }
        }
        if ((o.Fill is not null || o.Patterns is not null) && o.FillOnly) return;

        var style = new CanvasStrokeStyle
        {
            StartCap = o.RoundCaps ? CanvasCapStyle.Round : CanvasCapStyle.Flat,
            EndCap = o.RoundCaps ? CanvasCapStyle.Round : CanvasCapStyle.Flat,
            DashCap = o.RoundCaps ? CanvasCapStyle.Round : CanvasCapStyle.Flat,
            LineJoin = o.RoundCaps ? CanvasLineJoin.Round : CanvasLineJoin.Miter,
            MiterLimit = 4,
        };
        if (o.Casing is { } casing)
        {
            // Double line: casing and fill each on their own colour layer (Colour Hierarchy).
            if (o.Part.PartKind != ColorHierarchy.Part.Kind.Main)
            {
                ApplyDash(style, o.DashM, Math.Max(o.WidthM, minWidthM));
                ds.DrawGeometry(geometry, ToColor(casing.Hex), (float)Math.Max(o.WidthM, minWidthM), style);
            }
            if (o.Part.PartKind != ColorHierarchy.Part.Kind.Casing)
            {
                double w = Math.Max(casing.InnerWidthM, minWidthM * 0.5);
                ApplyDash(style, o.InnerDashM, w);
                ds.DrawGeometry(geometry, ToColor(o.ColorHex), (float)w, style);
            }
            return;
        }
        if (!o.HidesLine)
        {
            double w = Math.Max(o.WidthM, minWidthM);
            ApplyDash(style, o.DashM, w);
            ds.DrawGeometry(geometry, ToColor(o.ColorHex), (float)w, style);
        }
        if (o.Decoration.Count > 0) DrawMarks(ds, o.Decoration, o.MetersPerMM, minWidthM * 0.6);
    }

    /// <summary>Win2D dash lengths are multiples of the stroke width; the scene holds them in metres.</summary>
    private static void ApplyDash(CanvasStrokeStyle style, IReadOnlyList<double>? dashM, double widthM)
    {
        if (dashM is { Count: >= 2 } && widthM > 0)
        {
            style.CustomDashStyle = dashM.Select(d => (float)(d / widthM)).ToArray();
            style.DashOffset = 0;
        }
        else
        {
            style.CustomDashStyle = Array.Empty<float>();
            style.DashStyle = CanvasDashStyle.Solid;
        }
    }

    private static readonly CanvasStrokeStyle MarkStyle = new() { StartCap = CanvasCapStyle.Flat, EndCap = CanvasCapStyle.Flat, LineJoin = CanvasLineJoin.Miter };

    private static void DrawMarks(CanvasDrawingSession ds, IReadOnlyList<OrnamentMark> marks, double metersPerMM, double minWidthM)
    {
        foreach (var m in marks)
        {
            switch (m)
            {
                case OrnamentMark.Line l:
                    ds.DrawLine(V(l.A), V(l.B), ToColor(l.ColorHex), (float)Math.Max(l.WidthMM * metersPerMM, minWidthM), MarkStyle);
                    break;
                case OrnamentMark.Dot d:
                    ds.FillCircle(V(d.Center), (float)Math.Max(d.DiameterMM * metersPerMM / 2, minWidthM / 2), ToColor(d.ColorHex));
                    break;
                case OrnamentMark.Polygon p when p.Points.Count >= 3:
                    using (var g = CanvasGeometry.CreatePolygon(ds, p.Points.Select(V).ToArray())) ds.FillGeometry(g, ToColor(p.ColorHex));
                    break;
                case OrnamentMark.Polyline pl when pl.Points.Count >= 2:
                    for (int i = 1; i < pl.Points.Count; i++)
                        ds.DrawLine(V(pl.Points[i - 1]), V(pl.Points[i]), ToColor(pl.ColorHex), (float)Math.Max(pl.WidthMM * metersPerMM, minWidthM), MarkStyle);
                    break;
            }
        }
    }

    // MARK: Point symbols (PointSymbolRenderer.swift)

    private static void DrawPoint(CanvasDrawingSession ds, MapScene.PointSymbol p, double minWidthM)
    {
        double k = p.MetersPerMM;
        Vector2 At(Point2D mm) => new((float)(p.Center.X + mm.X * k), (float)(p.Center.Y + mm.Y * k));
        foreach (var e in p.Elements.DrawingOrder())
        {
            if (p.Level is int level && e.Priority != level) continue;
            var color = ToColor(e.ColorHex);
            var c = At(e.Points.Count > 0 ? e.Points[0] : Point2D.Zero);
            switch (e.ElementKind)
            {
                case PointElement.Kind.Dot:
                    ds.FillCircle(c, (float)(e.DiameterMM / 2 * k), color);
                    break;
                case PointElement.Kind.Circle:
                    ds.DrawCircle(c, (float)(e.DiameterMM / 2 * k), color, (float)Math.Max(e.LineWidthMM * k, minWidthM));
                    break;
                case PointElement.Kind.Area when e.Points.Count >= 3:
                    using (var g = CanvasGeometry.CreatePolygon(ds, e.Points.Select(At).ToArray())) ds.FillGeometry(g, color);
                    break;
                case PointElement.Kind.Line when e.Points.Count >= 2:
                {
                    var style = new CanvasStrokeStyle
                    {
                        StartCap = e.FlatEnds == true ? CanvasCapStyle.Flat : CanvasCapStyle.Round,
                        EndCap = e.FlatEnds == true ? CanvasCapStyle.Flat : CanvasCapStyle.Round,
                        LineJoin = e.FlatEnds == true ? CanvasLineJoin.Miter : CanvasLineJoin.Round,
                    };
                    using var builder = new CanvasPathBuilder(ds);
                    builder.BeginFigure(At(e.Points[0]));
                    for (int i = 1; i < e.Points.Count; i++) builder.AddLine(At(e.Points[i]));
                    builder.EndFigure(CanvasFigureLoop.Open);
                    using var g = CanvasGeometry.CreatePath(builder);
                    ds.DrawGeometry(g, color, (float)Math.Max(e.LineWidthMM * k, minWidthM), style);
                    break;
                }
            }
        }
    }

    // MARK: Sketch strokes (StrokeRenderer.swift)

    private static readonly CanvasStrokeStyle StrokeStyleRound = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };

    private static void DrawStroke(CanvasDrawingSession ds, MapScene.SketchStroke s, double minWidthM)
    {
        double w = Math.Max(s.WidthM, minWidthM);
        var color = ToColor(s.ColorHex);
        var pts = s.Stroke.Points;
        if (pts.Count == 1) { ds.FillCircle(V(pts[0]), (float)(w / 2), color); return; }
        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(V(pts[0]));
        for (int i = 1; i < pts.Count; i++) builder.AddLine(V(pts[i]));
        builder.EndFigure(CanvasFigureLoop.Open);
        using var g = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(g, color, (float)w, StrokeStyleRound);
    }

    // MARK: Frame and north lines

    /// <summary>The A4 frame outline (locked area) and, when enabled, the north lines inside it.</summary>
    public static void DrawFrame(CanvasDrawingSession ds, MapTransform view, MapFrame frame, Core.Project.NorthGrid? grid, SheetSpec sheet)
    {
        var saved = ds.Transform;
        ds.Transform = view.ToPixels * saved;
        double ppm = view.PixelsPerMeter;
        if (grid is { IsEnabled: true })
        {
            float w = (float)Math.Max(sheet.GroundMeters(grid.LineWidthMM), 0.75 / ppm);
            var color = ToColor(grid.ColorHex, grid.Opacity);
            foreach (var (a, b) in grid.Lines(frame)) ds.DrawLine(V(a), V(b), color, w);
        }
        var e = frame.Extent;
        ds.DrawRectangle((float)e.MinX, (float)e.MinY, (float)e.Width, (float)e.Height, Color.FromArgb(200, 0x2B, 0x2A, 0x28), (float)(1.2 / ppm));
        ds.Transform = saved;
    }

    /// <summary>A paper-coloured veil over the basemap whose alpha = 1 − basemap opacity (FR-2.5), as the Mac's VeilOverlay.</summary>
    public static void DrawVeil(CanvasDrawingSession ds, MapTransform view, double basemapOpacity)
    {
        double a = 1 - Math.Clamp(basemapOpacity, 0, 1);
        if (a <= 0) return;
        ds.FillRectangle(0, 0, (float)view.ViewWidth, (float)view.ViewHeight, Color.FromArgb((byte)Math.Round(a * 255), PaperColor.R, PaperColor.G, PaperColor.B));
    }
}

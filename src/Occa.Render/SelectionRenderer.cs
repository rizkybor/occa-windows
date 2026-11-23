using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Occa.Core.Geometry;
using Occa.Core.Vector;
using Windows.UI;

namespace Occa.Render;

/// <summary>
/// Selection feedback for the editing tools, drawn in pixel space on top of the map: the selected object's outline
/// (blue), its nodes (squares; the active one filled) and bezier handles in the Node tool, the path being drawn, a
/// rubber-band box, and the Select Area frame. Colours follow the Mac's selection blue (#315E85 / sky).
/// </summary>
public static class SelectionRenderer
{
    public static readonly Color Accent = Color.FromArgb(255, 0x31, 0x5E, 0x85);
    public static readonly Color AccentFill = Color.FromArgb(60, 0x31, 0x5E, 0x85);
    private static readonly CanvasStrokeStyle Dashed = new() { CustomDashStyle = new float[] { 4, 3 }, DashCap = CanvasCapStyle.Flat };

    /// <summary>Outline of a selected object (flattened, 1.5 px) plus its node squares.</summary>
    public static void DrawFeature(CanvasDrawingSession ds, Feature f, MapTransform view, bool showNodes, int? activeNode = null, bool showHandles = false)
    {
        foreach (var ring in f.RingFeatures)
        {
            var pts = ring.Flattened(0.5 / view.PixelsPerMeter).Select(view.Project).ToArray();
            if (pts.Length == 0) continue;
            if (pts.Length == 1) { ds.DrawCircle(pts[0], 8, Accent, 1.5f); continue; }
            using var builder = new CanvasPathBuilder(ds);
            builder.BeginFigure(pts[0]);
            for (int i = 1; i < pts.Length; i++) builder.AddLine(pts[i]);
            builder.EndFigure(f.ClosesPath ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(builder);
            ds.DrawGeometry(g, Color.FromArgb(255, 255, 255, 255), 3.5f);
            ds.DrawGeometry(g, Accent, 1.5f);
        }
        if (!showNodes) return;
        var all = f.AllNodes();
        for (int i = 0; i < all.Count; i++)
        {
            var n = all[i];
            var p = view.Project(n.Point);
            if (showHandles && (i == activeNode || activeNode is null))
            {
                if (n.InHandle is { } hin) DrawHandle(ds, p, view.Project(hin));
                if (n.OutHandle is { } hout) DrawHandle(ds, p, view.Project(hout));
            }
            const float s = 4.5f;
            var rect = new Windows.Foundation.Rect(p.X - s, p.Y - s, 2 * s, 2 * s);
            ds.FillRectangle(rect, i == activeNode ? Accent : Color.FromArgb(255, 255, 255, 255));
            ds.DrawRectangle(rect, Accent, 1.5f);
        }
    }

    private static void DrawHandle(CanvasDrawingSession ds, Vector2 node, Vector2 handle)
    {
        ds.DrawLine(node, handle, Accent, 1);
        ds.FillCircle(handle, 3.5f, Color.FromArgb(255, 255, 255, 255));
        ds.DrawCircle(handle, 3.5f, Accent, 1.5f);
    }

    /// <summary>The path under construction (Pen / Freehand): the points so far plus a preview segment to the pointer.</summary>
    public static void DrawDraft(CanvasDrawingSession ds, IReadOnlyList<Point2D> points, Point2D? cursor, MapTransform view, Color color, float widthPx, bool closed)
    {
        if (points.Count == 0) return;
        var pts = points.Select(view.Project).ToList();
        if (cursor is { } c) pts.Add(view.Project(c));
        if (pts.Count >= 2)
        {
            using var builder = new CanvasPathBuilder(ds);
            builder.BeginFigure(pts[0]);
            for (int i = 1; i < pts.Count; i++) builder.AddLine(pts[i]);
            builder.EndFigure(closed && pts.Count > 2 ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(builder);
            ds.DrawGeometry(g, color, Math.Max(widthPx, 1.5f));
        }
        foreach (var p in points.Select(view.Project)) { ds.FillCircle(p, 3.5f, Color.FromArgb(255, 255, 255, 255)); ds.DrawCircle(p, 3.5f, Accent, 1.5f); }
    }

    /// <summary>Rubber band (rectangle/ellipse tools and the selection box).</summary>
    public static void DrawBox(CanvasDrawingSession ds, Point2D a, Point2D b, MapTransform view, bool ellipse = false)
    {
        var pa = view.Project(a); var pb = view.Project(b);
        var rect = new Windows.Foundation.Rect(Math.Min(pa.X, pb.X), Math.Min(pa.Y, pb.Y), Math.Abs(pb.X - pa.X), Math.Abs(pb.Y - pa.Y));
        if (ellipse)
        {
            var c = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
            ds.FillEllipse(c, (float)rect.Width / 2, (float)rect.Height / 2, AccentFill);
            ds.DrawEllipse(c, (float)rect.Width / 2, (float)rect.Height / 2, Accent, 1.5f, Dashed);
        }
        else
        {
            ds.FillRectangle(rect, AccentFill);
            ds.DrawRectangle(rect, Accent, 1.5f, Dashed);
        }
    }

    /// <summary>Select Area mode: the A4 frame fixed at the view centre with a dimmed outside (the Mac's frame overlay).</summary>
    public static void DrawAreaFrame(CanvasDrawingSession ds, MapTransform view, Core.Sheet.MapFrame frame)
    {
        var e = frame.Extent;
        var tl = view.Project(new Point2D(e.MinX, e.MaxY)); var br = view.Project(new Point2D(e.MaxX, e.MinY));
        var rect = new Windows.Foundation.Rect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
        using var outside = CanvasGeometry.CreateRectangle(ds, 0, 0, (float)view.ViewWidth, (float)view.ViewHeight);
        using var inside = CanvasGeometry.CreateRectangle(ds, rect);
        using var dim = outside.CombineWith(inside, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        ds.FillGeometry(dim, Color.FromArgb(90, 0, 0, 0));
        ds.DrawRectangle(rect, Color.FromArgb(255, 255, 255, 255), 3);
        ds.DrawRectangle(rect, Accent, 1.5f);
    }
}

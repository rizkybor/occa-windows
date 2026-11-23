using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Occa.Core.Contour;
using Occa.Core.Geometry;
using Occa.Core.Sheet;

namespace Occa.Render;

/// <summary>
/// Draws generated or imported contour lines (brown, ISOM 101/102/103 widths from <see cref="ContourSettings"/>) under
/// the map objects, like the Mac's <c>ContourOverlay</c>. Geometry is cached per line list so panning stays smooth.
/// </summary>
public sealed class ContourRenderer
{
    private static readonly CanvasStrokeStyle RoundStyle = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };

    private IReadOnlyList<ContourLine>? _cachedLines;
    private ICanvasResourceCreator? _device;
    private readonly Dictionary<ContourLine.Kind, CanvasGeometry> _geometry = new();

    public void InvalidateCache()
    {
        foreach (var g in _geometry.Values) g.Dispose();
        _geometry.Clear();
        _cachedLines = null;
    }

    /// <summary>Draws the lines in project metres under the view transform. The session's transform must be identity on entry.</summary>
    public void Draw(CanvasDrawingSession ds, IReadOnlyList<ContourLine> lines, ContourSettings s, SheetSpec sheet, MapTransform view, double minLinePx = 0.5)
    {
        if (lines.Count == 0 || s.Opacity <= 0) return;
        if (!ReferenceEquals(_device, ds.Device) || !ReferenceEquals(_cachedLines, lines))
        {
            InvalidateCache();
            _device = ds.Device;
            _cachedLines = lines;
            foreach (var kind in new[] { ContourLine.Kind.Contour, ContourLine.Kind.Index, ContourLine.Kind.Form })
            {
                var group = lines.Where(l => l.LineKind == kind && l.Points.Count >= 2).ToList();
                if (group.Count == 0) continue;
                using var builder = new CanvasPathBuilder(ds);
                foreach (var line in group)
                {
                    builder.BeginFigure(V(line.Points[0]));
                    for (int i = 1; i < line.Points.Count; i++) builder.AddLine(V(line.Points[i]));
                    builder.EndFigure(CanvasFigureLoop.Open);
                }
                _geometry[kind] = CanvasGeometry.CreatePath(builder);
            }
        }
        var saved = ds.Transform;
        ds.Transform = view.ToPixels * saved;
        double ppm = view.PixelsPerMeter;
        double minWidthM = minLinePx / ppm;
        var color = MapRenderer.ToColor(s.ColorHex, s.Opacity);
        var form = ContourSettings.FormLine(s.ContourWidthMM);
        foreach (var (kind, geometry) in _geometry)
        {
            double widthMM = kind switch { ContourLine.Kind.Index => s.IndexWidthMM, ContourLine.Kind.Form => form.Width, _ => s.ContourWidthMM };
            double w = Math.Max(sheet.GroundMeters(widthMM), minWidthM);
            if (kind == ContourLine.Kind.Form)
            {
                var style = new CanvasStrokeStyle
                {
                    StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, DashCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round,
                    CustomDashStyle = form.Dash.Select(d => (float)(sheet.GroundMeters(d) / w)).ToArray(),
                };
                ds.DrawGeometry(geometry, color, (float)w, style);
            }
            else ds.DrawGeometry(geometry, color, (float)w, RoundStyle);
        }
        ds.Transform = saved;
    }

    private static Vector2 V(Point2D p) => new((float)p.X, (float)p.Y);
}

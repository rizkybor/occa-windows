using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Occa.Core.Courses;
using Occa.Core.Geometry;
using Windows.UI;

namespace Occa.Render;

/// <summary>
/// Draws a <see cref="CourseOverprint"/> with Win2D (port of <c>CourseOverprintRenderer</c>, which draws into a CGContext).
/// Everything is drawn in pixel space (points projected through the view), so text stays upright and line widths can
/// have a minimum in pixels. Exports place the lower and upper purple parts in the colour order (<see cref="CourseOverprint.Levels"/>);
/// the on-screen preview draws the whole overprint on top of the map with a little transparency so black, brown and
/// blue detail stays readable while planning (Win2D has no multiply blend on a plain drawing session).
/// </summary>
public static class CourseOverprintRenderer
{
    private static readonly CanvasStrokeStyle RoundStyle = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };

    /// <param name="images">Course object images by file name (missing ones are skipped).</param>
    public static void Draw(CanvasDrawingSession ds, CourseOverprint o, MapTransform view, double minLinePx = 0, double alpha = 1,
                            IReadOnlyDictionary<string, CanvasBitmap>? images = null)
    {
        if (o.IsEmpty) return;
        double ppm = view.PixelsPerMeter;
        Vector2 V(Point2D p) => view.Project(p);
        var baseColor = MapRenderer.ToColor(o.ColorHex, alpha);
        var color = baseColor;
        float width = (float)Math.Max(o.LineWidthM * ppm, minLinePx);
        bool bold = false, italic = false;
        var visible = view.Visible;
        foreach (var mark in o.Marks)
        {
            switch (mark)
            {
                case CourseMark.Font f:
                    bold = f.Bold; italic = f.Italic;
                    break;
                case CourseMark.Color c:
                    color = c.Hex is null ? baseColor : MapRenderer.ToColor(c.Hex, alpha);
                    break;
                case CourseMark.Width w:
                    width = (float)Math.Max((w.WidthM ?? o.LineWidthM) * ppm, minLinePx);
                    break;
                case CourseMark.Level:
                    break;
                case CourseMark.Circle ci:
                    if (!visible.Intersects(new Rect2D(ci.Center.X - ci.RadiusM, ci.Center.Y - ci.RadiusM, ci.Center.X + ci.RadiusM, ci.Center.Y + ci.RadiusM))) continue;
                    ds.DrawCircle(V(ci.Center), (float)(ci.RadiusM * ppm), color, width, RoundStyle);
                    break;
                case CourseMark.Polyline pl when pl.Points.Count >= 2:
                {
                    if (Rect2D.FromPoints(pl.Points) is { } b && !visible.Intersects(b.InsetBy(-width / ppm))) continue;
                    using var builder = new CanvasPathBuilder(ds);
                    builder.BeginFigure(V(pl.Points[0]));
                    for (int i = 1; i < pl.Points.Count; i++) builder.AddLine(V(pl.Points[i]));
                    builder.EndFigure(pl.Closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
                    using var g = CanvasGeometry.CreatePath(builder);
                    ds.DrawGeometry(g, color, width, RoundStyle);
                    break;
                }
                case CourseMark.Text t:
                {
                    // Digit height = HeightM; Arial/Helvetica digits are ≈ 0.72 em.
                    float size = (float)(t.HeightM * ppm / 0.72);
                    if (size < 3) continue;
                    using var format = new CanvasTextFormat
                    {
                        FontFamily = "Arial", FontSize = size, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
                        FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)(bold ? 700 : 400) },
                        FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                    };
                    var c = V(t.Center);
                    using var layout = new CanvasTextLayout(ds, t.Value, format, 0, 0);
                    // Centre on the digit box (cap height), not on the full line box with descender space.
                    var bounds = layout.DrawBounds;
                    ds.DrawTextLayout(layout, (float)(c.X - bounds.X - bounds.Width / 2), (float)(c.Y - bounds.Y - bounds.Height / 2), color);
                    break;
                }
                case CourseMark.Fill fi when fi.Points.Count >= 3:
                {
                    using var g = CanvasGeometry.CreatePolygon(ds, fi.Points.Select(V).ToArray());
                    ds.FillGeometry(g, Color.FromArgb((byte)Math.Round(color.A * fi.Opacity), color.R, color.G, color.B));
                    break;
                }
                case CourseMark.Image im:
                {
                    if (images is null || !images.TryGetValue(im.Name, out var bitmap)) continue;
                    var c = V(im.Center);
                    float pw = (float)(im.WidthM * ppm), ph = (float)(im.HeightM * ppm);
                    ds.DrawImage(bitmap, new Windows.Foundation.Rect(c.X - pw / 2, c.Y - ph / 2, pw, ph));
                    break;
                }
            }
        }
    }
}

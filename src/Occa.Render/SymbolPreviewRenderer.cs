using Microsoft.Graphics.Canvas;
using Occa.Core.Geometry;
using Occa.Core.Render;
using Occa.Core.Sheet;
using Occa.Core.Vector;
using Windows.UI;

namespace Occa.Render;

/// <summary>
/// Small symbol samples for the Symbols panel (the Mac's <c>SymbolSwatch</c>): a line with its ISOM decoration, a
/// filled box with its pattern, or the point symbol's own shape, drawn with <see cref="MapRenderer"/> itself so the
/// sample is the symbol as it prints. Always on light map paper, also in dark mode (black symbols would vanish).
/// </summary>
public static class SymbolPreviewRenderer
{
    public static readonly Color Paper = Color.FromArgb(255, 0xFB, 0xF9, 0xF4);

    /// <summary>Renders the sample at <paramref name="widthPx"/> × <paramref name="heightPx"/> device pixels (BGRA8 premultiplied).</summary>
    public static byte[] Render(MapSymbol symbol, SheetSpec sheet, int widthPx, int heightPx)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, widthPx, heightPx, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Paper);
            Draw(ds, symbol, sheet, widthPx, heightPx);
        }
        return target.GetPixelBytes();
    }

    public static void Draw(CanvasDrawingSession ds, MapSymbol s, SheetSpec sheet, double w, double h)
    {
        // Sample geometry in map millimetres: the swatch shows about 18 × 14 "mm" enlarged (8 px per mm at 26 × 22).
        double k = Math.Min(w / 26, h / 22);
        double pxPerMM = 8 * k;
        double mpm = sheet.GroundMeters(1);   // ground metres per map mm
        var view = new MapTransform { ViewWidth = w, ViewHeight = h, PixelsPerMeter = pxPerMM / mpm, Center = new Point2D(0, 0) };
        Feature? f = null;
        switch (s.SymbolKind)
        {
            case MapSymbol.Kind.Line:
            {
                double half = (w / 2 - 1 * k) / pxPerMM * mpm;
                f = SymbolFeatures.Line(s, new List<BezierNode> { new(new Point2D(-half, 0)), new(new Point2D(half, 0)) });
                // Thin lines are enlarged to stay visible at swatch size (the Mac clamps to 1.5–4 pt).
                double minMM = 1.5 / pxPerMM, maxMM = 4 / pxPerMM;
                if (f.WidthMM < minMM) f.SetWidth(minMM); else if (f.WidthMM > maxMM) f.SetWidth(maxMM);
                break;
            }
            case MapSymbol.Kind.Area:
            {
                double hw = 9 * k / pxPerMM * mpm, hh = 7 * k / pxPerMM * mpm;
                f = SymbolFeatures.Area(s, new List<BezierNode> { new(new Point2D(-hw, -hh)), new(new Point2D(hw, -hh)), new(new Point2D(hw, hh)), new(new Point2D(-hw, hh)) });
                break;
            }
            case MapSymbol.Kind.Point:
            {
                if (s.PointElements is { Count: > 0 } elements)
                {
                    // The original shape, scaled to fit an 18 × 18 sample.
                    double diameterMM = Math.Max(elements.DiameterMM(), 0.1);
                    view.PixelsPerMeter = (Math.Min(w, h) - 4 * k) / diameterMM / mpm;
                    f = SymbolFeatures.Point(s, new Point2D(0, 0));
                }
                break;
            }
        }
        if (f is null)
        {
            // Text symbol: a bold "T" in the symbol colour.
            using var format = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontFamily = "Segoe UI", FontSize = (float)(12 * k), FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center, VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center };
            ds.DrawText("T", new Windows.Foundation.Rect(0, 0, w, h), MapRenderer.ToColor(s.ColorHex), format);
            return;
        }
        var scene = MapScene.Build(new[] { f }, Array.Empty<Stroke>(), sheet);
        new MapRenderer().Draw(ds, scene, view, sheet, minLinePx: 0.8);
        if (s.SymbolKind == MapSymbol.Kind.Area)
        {
            // Thin edge so white / very light areas (401 open land, 405 forest) still show on the paper.
            var a = view.Project(new Point2D(-9 * k / pxPerMM * mpm, 7 * k / pxPerMM * mpm));
            var b = view.Project(new Point2D(9 * k / pxPerMM * mpm, -7 * k / pxPerMM * mpm));
            ds.DrawRoundedRectangle(new Windows.Foundation.Rect(a.X, a.Y, b.X - a.X, b.Y - a.Y), 2, 2, Color.FromArgb(30, 0, 0, 0), 1);
        }
    }
}

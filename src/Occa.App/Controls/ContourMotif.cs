using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Occa.App.Controls;

/// <summary>
/// Decorative contour lines, the same geometry as <c>ContourMotif</c> (Tokens.swift): seven nested wobbly rings
/// around the centre, every fifth one an index contour, in the muted ink colour. XAML polylines, no Win2D needed.
/// </summary>
public sealed class ContourMotif : Canvas
{
    public ContourMotif()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    private void Redraw()
    {
        Children.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || Application.Current.Resources["CpInkMuted"] is not SolidColorBrush ink) return;
        double cx = w / 2, cy = h / 2;
        for (int i = 1; i <= 7; i++)
        {
            double r = i * Math.Min(w, h) / 16;
            var line = new Polyline { Stroke = ink, StrokeThickness = i % 5 == 0 ? 1.2 : 0.7, Opacity = i % 5 == 0 ? 0.5 : 0.25 };
            for (int step = 0; step <= 72; step++)
            {
                double t = step / 72.0 * 2 * Math.PI;
                double wobble = 1 + 0.08 * Math.Sin(3 * t + i) + 0.05 * Math.Cos(5 * t);
                line.Points.Add(new Point(cx + Math.Cos(t) * r * 1.3 * wobble, cy + Math.Sin(t) * r * wobble));
            }
            Children.Add(line);
        }
    }
}

/// <summary>
/// Splash background, the same as <c>SplashContours</c> (SplashView.swift): two hills of nested rings in the
/// top-right and bottom-left corners, every fifth an index contour.
/// </summary>
public sealed class SplashContours : Canvas
{
    public SplashContours()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    private void Redraw()
    {
        Children.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || Application.Current.Resources["CpSplashContour"] is not SolidColorBrush color) return;
        double u = Math.Max(w, h);
        foreach (var (cx, cy, rings, baseR, seed) in new[] { (0.90, 0.10, 9, 0.032, 1.0), (0.08, 0.92, 8, 0.034, 2.3) })
        {
            double ox = w * cx, oy = h * cy;
            for (int i = 1; i <= rings; i++)
            {
                double r = i * u * baseR;
                bool index = i % 5 == 0;
                var line = new Polygon { Stroke = color, StrokeThickness = index ? 1.4 : 0.8, Opacity = index ? 1 : 0.55 };
                for (int step = 0; step <= 120; step++)
                {
                    double t = step / 120.0 * 2 * Math.PI;
                    double wobble = 1 + 0.09 * Math.Sin(3 * t + seed) + 0.05 * Math.Cos(5 * t + seed * 2) + 0.03 * Math.Sin(2 * t);
                    line.Points.Add(new Point(ox + Math.Cos(t) * r * 1.2 * wobble, oy + Math.Sin(t) * r * wobble));
                }
                Children.Add(line);
            }
        }
    }
}

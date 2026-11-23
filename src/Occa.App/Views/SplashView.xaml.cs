using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Occa.Core;
using Windows.Foundation;

namespace Occa.App.Views;

/// <summary>
/// Splash shown when OCCA opens, the same as <c>SplashView</c> on Mac/iPad: paper background with contour lines
/// that fade in, the logo settling in, an orange pen stroke drawing itself under the name, the tagline and the
/// supporter credit. About 4 s, then it fades out to the project list. Sizes scale with the window.
/// </summary>
public sealed partial class SplashView : UserControl
{
    public const double DurationSeconds = 4;

    /// <summary>Raised when the fade-out has finished; the host removes the view.</summary>
    public event EventHandler? Finished;

    public SplashView()
    {
        InitializeComponent();
        VersionText.Text = AppInfo.VersionText;
        Loaded += async (_, _) =>
        {
            Intro.Begin();
            await Task.Delay(TimeSpan.FromSeconds(DurationSeconds));
            Outro.Begin();
        };
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double unit = Math.Min(e.NewSize.Width, e.NewSize.Height);
        if (unit <= 0) return;
        double logo = unit * 0.30;
        LogoFrame.Width = LogoFrame.Height = logo;
        LogoFrame.CornerRadius = new CornerRadius(logo * 0.2237);
        Centre.Spacing = unit * 0.035;
        Centre.Margin = new Thickness(0, 0, 0, unit * 0.06);
        Title.FontSize = unit * 0.075;
        Tagline.FontSize = Math.Max(12, unit * 0.024);
        Tagline.Margin = new Thickness(0, unit * 0.008, 0, 0);
        VersionText.FontSize = Math.Max(11, unit * 0.016);
        Credits.Margin = new Thickness(0, 0, 0, Math.Max(28, unit * 0.05));
        JcdLogo.Height = Math.Max(18, unit * 0.032);

        // The pen stroke: a gentle arc like the curve on the logo's flag, 30% of the unit wide.
        double w = unit * 0.30, h = unit * 0.025;
        PenStroke.StrokeThickness = Math.Max(2, unit * 0.006);
        PenStroke.Width = w;
        PenStroke.Height = h;
        var figure = new PathFigure { StartPoint = new Point(0, h * 0.75) };
        figure.Segments.Add(new QuadraticBezierSegment { Point1 = new Point(w / 2, -h * 0.2), Point2 = new Point(w, h * 0.75) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        PenStroke.Data = geometry;
        StrokeClip.Rect = new Rect(-4, -h, w + 8, h * 3);
    }

    private void Outro_Completed(object? sender, object e) => Finished?.Invoke(this, EventArgs.Empty);
}

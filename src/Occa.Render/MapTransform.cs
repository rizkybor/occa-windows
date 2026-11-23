using System.Numerics;
using Occa.Core.Geometry;

namespace Occa.Render;

/// <summary>
/// View transform between project metres (x east, y north) and device-independent pixels (x right, y down):
/// a uniform scale about the view centre with the y axis flipped. Rotation is not needed in M1.
/// </summary>
public sealed class MapTransform
{
    /// <summary>Project point shown at the centre of the view.</summary>
    public Point2D Center { get; set; }
    /// <summary>Pixels per metre.</summary>
    public double PixelsPerMeter { get; set; } = 1;
    public double ViewWidth { get; set; }
    public double ViewHeight { get; set; }

    public Matrix3x2 ToPixels => Matrix3x2.CreateTranslation((float)-Center.X, (float)-Center.Y)
                                 * Matrix3x2.CreateScale((float)PixelsPerMeter, (float)-PixelsPerMeter)
                                 * Matrix3x2.CreateTranslation((float)(ViewWidth / 2), (float)(ViewHeight / 2));

    public Vector2 Project(Point2D p) => new((float)((p.X - Center.X) * PixelsPerMeter + ViewWidth / 2), (float)((Center.Y - p.Y) * PixelsPerMeter + ViewHeight / 2));

    public Point2D Unproject(double px, double py) => new(Center.X + (px - ViewWidth / 2) / PixelsPerMeter, Center.Y - (py - ViewHeight / 2) / PixelsPerMeter);

    /// <summary>Metres covered by the view.</summary>
    public Rect2D Visible
    {
        get
        {
            var a = Unproject(0, 0);
            var b = Unproject(ViewWidth, ViewHeight);
            return new Rect2D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }
    }

    /// <summary>Zoom about a pixel position so that the point under it stays put.</summary>
    public void ZoomAt(double px, double py, double factor, double minPpm, double maxPpm)
    {
        var anchor = Unproject(px, py);
        PixelsPerMeter = Math.Clamp(PixelsPerMeter * factor, minPpm, maxPpm);
        var after = Unproject(px, py);
        Center = new Point2D(Center.X + anchor.X - after.X, Center.Y + anchor.Y - after.Y);
    }

    public void PanByPixels(double dx, double dy) => Center = new Point2D(Center.X - dx / PixelsPerMeter, Center.Y + dy / PixelsPerMeter);

    /// <summary>Fit a rectangle (metres) into the view with a margin.</summary>
    public void Fit(Rect2D rect, double marginPx = 24)
    {
        if (ViewWidth <= 0 || ViewHeight <= 0 || rect.Width <= 0 || rect.Height <= 0) return;
        PixelsPerMeter = Math.Min((ViewWidth - 2 * marginPx) / rect.Width, (ViewHeight - 2 * marginPx) / rect.Height);
        Center = rect.Center;
    }
}

using System.Text.Json.Serialization;

namespace Occa.Core.Geometry;

/// <summary>Point in a projected metric CRS (UTM). x = easting, y = northing. Mirrors <c>Point2D</c> in CakraCore.</summary>
public readonly record struct Point2D(double X, double Y)
{
    [JsonIgnore] public static readonly Point2D Zero = new(0, 0);

    public static Point2D operator +(Point2D a, Point2D b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2D operator *(Point2D a, double k) => new(a.X * k, a.Y * k);

    public double DistanceTo(Point2D other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));

    /// <summary>Shortest distance from this point to segment a–b.</summary>
    public double DistanceToSegment(Point2D a, Point2D b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 <= 0) return DistanceTo(a);
        double t = Math.Clamp(((X - a.X) * dx + (Y - a.Y) * dy) / len2, 0, 1);
        return DistanceTo(new Point2D(a.X + t * dx, a.Y + t * dy));
    }

    [JsonIgnore] public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>Axis-aligned bounding box in metres.</summary>
public readonly record struct Rect2D(double MinX, double MinY, double MaxX, double MaxY)
{
    public static Rect2D? FromPoints(IEnumerable<Point2D> points)
    {
        bool any = false;
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var p in points)
        {
            if (!any) { minX = maxX = p.X; minY = maxY = p.Y; any = true; continue; }
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        return any ? new Rect2D(minX, minY, maxX, maxY) : null;
    }

    [JsonIgnore] public double Width => MaxX - MinX;
    [JsonIgnore] public double Height => MaxY - MinY;
    [JsonIgnore] public Point2D Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);

    public bool Intersects(Rect2D o) => MinX <= o.MaxX && MaxX >= o.MinX && MinY <= o.MaxY && MaxY >= o.MinY;
    public bool Contains(Rect2D o) => o.MinX >= MinX && o.MaxX <= MaxX && o.MinY >= MinY && o.MaxY <= MaxY;
    public bool Contains(Point2D p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
    public Rect2D InsetBy(double d) => new(MinX + d, MinY + d, MaxX - d, MaxY - d);
    public Rect2D Union(Rect2D o) => new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
    public Rect2D Intersection(Rect2D o) => new(Math.Max(MinX, o.MinX), Math.Max(MinY, o.MinY), Math.Min(MaxX, o.MaxX), Math.Min(MaxY, o.MaxY));
}

/// <summary>WGS84 geographic coordinate in degrees.</summary>
public readonly record struct LatLon(double Lat, double Lon);

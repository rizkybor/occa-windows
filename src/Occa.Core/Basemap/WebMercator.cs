using Occa.Core.Geometry;

namespace Occa.Core.Basemap;

/// <summary>Web Mercator (EPSG:3857) tile arithmetic for XYZ tile services such as Esri World Imagery.</summary>
public static class WebMercator
{
    public const int TileSize = 256;
    public const double MaxLatitude = 85.05112878;

    /// <summary>Tile column and row that contain a WGS84 position at a zoom level (Google/OSM/Esri "XYZ" scheme).</summary>
    public static (int X, int Y) TileFor(double latitude, double longitude, int zoom)
    {
        if (zoom is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(zoom));
        latitude = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
        double n = 1 << zoom;
        double latRad = latitude * Math.PI / 180;
        int x = (int)Math.Floor((longitude + 180) / 360 * n);
        int y = (int)Math.Floor((1 - Math.Log(Math.Tan(latRad) + 1 / Math.Cos(latRad)) / Math.PI) / 2 * n);
        return (Math.Clamp(x, 0, (int)n - 1), Math.Clamp(y, 0, (int)n - 1));
    }

    /// <summary>WGS84 position of the north-west corner of a tile.</summary>
    public static (double Latitude, double Longitude) TileOrigin(int x, int y, int zoom)
    {
        double n = 1 << zoom;
        double longitude = x / n * 360 - 180;
        double latRad = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n)));
        return (latRad * 180 / Math.PI, longitude);
    }

    /// <summary>Ground resolution in metres per pixel at a latitude and zoom level.</summary>
    public static double MetersPerPixel(double latitude, int zoom)
        => 156543.03392 * Math.Cos(latitude * Math.PI / 180) / (1 << zoom);
}

/// <summary>Web Mercator in metres (EPSG:3857), for placing tiles in the project's UTM frame.</summary>
public static class WebMercatorMeters
{
    public const double EarthRadius = 6_378_137.0;
    public const double HalfWorld = Math.PI * EarthRadius;

    public static Point2D FromLatLon(LatLon c)
    {
        double lat = Math.Clamp(c.Lat, -WebMercator.MaxLatitude, WebMercator.MaxLatitude) * Math.PI / 180;
        return new Point2D(EarthRadius * c.Lon * Math.PI / 180, EarthRadius * Math.Log(Math.Tan(Math.PI / 4 + lat / 2)));
    }

    public static LatLon ToLatLon(Point2D m)
        => new(180 / Math.PI * (2 * Math.Atan(Math.Exp(m.Y / EarthRadius)) - Math.PI / 2), m.X / EarthRadius * 180 / Math.PI);

    /// <summary>Mercator metres of the north-west corner of a tile.</summary>
    public static Point2D TileOriginMeters(int x, int y, int zoom)
    {
        double n = 1 << zoom;
        double size = 2 * HalfWorld / n;
        return new Point2D(-HalfWorld + x * size, HalfWorld - y * size);
    }

    /// <summary>Mercator metres per tile side at a zoom level.</summary>
    public static double TileSizeMeters(int zoom) => 2 * HalfWorld / (1 << zoom);

    /// <summary>The zoom level whose tiles have about <paramref name="metersPerPixel"/> ground metres per pixel at the latitude.</summary>
    public static int ZoomFor(double metersPerPixel, double latitude, int maxZoom)
    {
        double scaleCos = Math.Cos(latitude * Math.PI / 180);
        double z = Math.Log2(156543.03392 * scaleCos / Math.Max(metersPerPixel, 1e-6));
        return Math.Clamp((int)Math.Round(z), 0, maxZoom);
    }
}

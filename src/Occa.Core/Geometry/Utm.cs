namespace Occa.Core.Geometry;

/// <summary>
/// UTM zone on the WGS84 datum (EPSG 326xx north, 327xx south). The Norway/Svalbard zone exceptions are not handled;
/// Indonesia lies in zones 46–54. Mirrors <c>UTMZone</c> in CakraCore.
/// </summary>
public readonly record struct UtmZone(int Number, bool IsNorth)
{
    public static UtmZone? Create(int number, bool isNorth) => number is >= 1 and <= 60 ? new UtmZone(number, isNorth) : null;

    /// <summary>Zone containing this coordinate.</summary>
    public static UtmZone Containing(LatLon c)
    {
        double lon = c.Lon % 360;
        if (lon >= 180) lon -= 360;
        if (lon < -180) lon += 360;
        int n = (int)Math.Floor((lon + 180) / 6) + 1;
        return new UtmZone(Math.Clamp(n, 1, 60), c.Lat >= 0);
    }

    /// <summary>Only EPSG UTM WGS84 (32601–32660, 32701–32760) is supported.</summary>
    /// <summary>Detect the WGS84 UTM zone from .prj WKT (name "UTM_Zone_48S" or AUTHORITY EPSG 326xx/327xx).</summary>
    public static UtmZone? FromWkt(string wkt) => Contour.UtmZoneWkt.Parse(wkt);

    public static UtmZone? FromEpsg(int epsg) => epsg switch
    {
        >= 32601 and <= 32660 => new UtmZone(epsg - 32600, true),
        >= 32701 and <= 32760 => new UtmZone(epsg - 32700, false),
        _ => null,
    };

    public int Epsg => (IsNorth ? 32600 : 32700) + Number;
    public double CentralMeridian => (Number - 1) * 6 - 180 + 3;
    public double FalseNorthing => IsNorth ? 0 : 10_000_000;

    /// <summary>Short label for the top bar, e.g. "UTM 48S".</summary>
    public string Label => $"UTM {Number}{(IsNorth ? "N" : "S")}";

    /// <summary>ESRI-style WKT1 for .prj files; read by OCAD, OOM and QGIS.</summary>
    public string PrjWkt
    {
        get
        {
            var name = $"WGS_1984_UTM_Zone_{Number}{(IsNorth ? "N" : "S")}";
            return $"PROJCS[\"{name}\",GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Transverse_Mercator\"],PARAMETER[\"False_Easting\",500000.0],PARAMETER[\"False_Northing\",{(int)FalseNorthing}.0],PARAMETER[\"Central_Meridian\",{(int)CentralMeridian}.0],PARAMETER[\"Scale_Factor\",0.9996],PARAMETER[\"Latitude_Of_Origin\",0.0],UNIT[\"Meter\",1.0]]";
        }
    }

    public Point2D Project(LatLon c) => Utm.Forward(c, this);
    public LatLon Unproject(Point2D p) => Utm.Inverse(p, this);
}

/// <summary>Transverse Mercator using the Krüger series to order n⁴ (sub-millimetre accuracy within the zone).</summary>
public static class Utm
{
    private const double A = 6_378_137.0;
    private const double F = 1 / 298.257223563;
    private const double K0 = 0.9996;
    private const double FalseEasting = 500_000.0;

    private static readonly double N = F / (2 - F);
    private static readonly double BigA = A / (1 + N) * (1 + N * N / 4 + N * N * N * N / 64);
    private static readonly double[] Alpha = Series(n => new[]
    {
        n / 2 - 2 * n * n / 3 + 5 * n * n * n / 16 + 41 * n * n * n * n / 180,
        13 * n * n / 48 - 3 * n * n * n / 5 + 557 * n * n * n * n / 1440,
        61 * n * n * n / 240 - 103 * n * n * n * n / 140,
        49561 * n * n * n * n / 161280,
    });
    private static readonly double[] Beta = Series(n => new[]
    {
        n / 2 - 2 * n * n / 3 + 37 * n * n * n / 96 - n * n * n * n / 360,
        n * n / 48 + n * n * n / 15 - 437 * n * n * n * n / 1440,
        17 * n * n * n / 480 - 37 * n * n * n * n / 840,
        4397 * n * n * n * n / 161280,
    });
    private static readonly double[] Delta = Series(n => new[]
    {
        2 * n - 2 * n * n / 3 - 2 * n * n * n + 116 * n * n * n * n / 45,
        7 * n * n / 3 - 8 * n * n * n / 5 - 227 * n * n * n * n / 45,
        56 * n * n * n / 15 - 136 * n * n * n * n / 35,
        4279 * n * n * n * n / 630,
    });

    private static double[] Series(Func<double, double[]> f) => f(N);

    public static Point2D Forward(LatLon c, UtmZone zone)
    {
        double phi = c.Lat * Math.PI / 180;
        double dLambda = (c.Lon - zone.CentralMeridian) * Math.PI / 180;
        double k = 2 * Math.Sqrt(N) / (1 + N);
        double t = Math.Sinh(Math.Atanh(Math.Sin(phi)) - k * Math.Atanh(k * Math.Sin(phi)));
        double xiP = Math.Atan2(t, Math.Cos(dLambda));
        double etaP = Math.Atanh(Math.Sin(dLambda) / Math.Sqrt(1 + t * t));
        double xi = xiP, eta = etaP;
        for (int j = 1; j <= 4; j++)
        {
            double jj = 2 * j;
            xi += Alpha[j - 1] * Math.Sin(jj * xiP) * Math.Cosh(jj * etaP);
            eta += Alpha[j - 1] * Math.Cos(jj * xiP) * Math.Sinh(jj * etaP);
        }
        return new Point2D(FalseEasting + K0 * BigA * eta, zone.FalseNorthing + K0 * BigA * xi);
    }

    public static LatLon Inverse(Point2D p, UtmZone zone)
    {
        double xi = (p.Y - zone.FalseNorthing) / (K0 * BigA);
        double eta = (p.X - FalseEasting) / (K0 * BigA);
        double xiP = xi, etaP = eta;
        for (int j = 1; j <= 4; j++)
        {
            double jj = 2 * j;
            xiP -= Beta[j - 1] * Math.Sin(jj * xi) * Math.Cosh(jj * eta);
            etaP -= Beta[j - 1] * Math.Cos(jj * xi) * Math.Sinh(jj * eta);
        }
        double chi = Math.Asin(Math.Sin(xiP) / Math.Cosh(etaP));
        double phi = chi;
        for (int j = 1; j <= 4; j++) phi += Delta[j - 1] * Math.Sin(2 * j * chi);
        double lambda = Math.Atan2(Math.Sinh(etaP), Math.Cos(xiP));
        return new LatLon(phi * 180 / Math.PI, zone.CentralMeridian + lambda * 180 / Math.PI);
    }
}

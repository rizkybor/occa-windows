using System.Text.Json.Serialization;

namespace Occa.Core.Sheet;

/// <summary>
/// The IOF map specification a map follows, chosen from its scale, with the standard sizes OCCA applies
/// (<c>MapStandard</c> in CakraCore). ISOM 2017-2 base 1:15000 (1:10000 enlarged 150 %); ISSprOM 2019-2 base 1:4000
/// (1:3000 enlarged 133 %); ISInOM 6.7.3 base 1:700, never chosen from the scale alone.
/// </summary>
[JsonConverter(typeof(Occa.Core.Json.CamelEnumConverter<MapStandard>))]
public enum MapStandard
{
    Isom,
    Issprom,
    Isinom,
}

public static class MapStandardExtensions
{
    /// <summary>Sprint maps (1:5000 and larger scales) follow ISSprOM; forest maps (1:7500–1:15000) ISOM.</summary>
    public static MapStandard ForScale(int scale) => scale <= 5000 ? MapStandard.Issprom : MapStandard.Isom;

    public static string Label(this MapStandard s) => s switch
    {
        MapStandard.Isom => "ISOM 2017-2",
        MapStandard.Issprom => "ISSprOM 2019-2",
        _ => "ISInOM 6.7.3",
    };

    public static int BaseScale(this MapStandard s) => s switch { MapStandard.Isom => 15000, MapStandard.Issprom => 4000, _ => 700 };

    /// <summary>When a map is enlarged, all lines, symbols and screens are enlarged proportionally (never reduced).</summary>
    public static double Enlargement(this MapStandard s, int scale) => scale <= 0 ? 1 : Math.Max(1, (double)s.BaseScale() / scale);

    public static double ContourWidthMM(this MapStandard s) => s == MapStandard.Isom ? 0.14 : 0.21;
    public static double IndexContourWidthMM(this MapStandard s) => s == MapStandard.Isom ? 0.25 : 0.30;
    public const double FormLineWidthRatio = 0.1 / 0.14;
    public const double FormLineDashPerWidth = 20.0;
    public const double FormLineGapPerWidth = 2.0;

    public static double NorthLineSpacingM(this MapStandard s) => s switch { MapStandard.Isom => 300, MapStandard.Issprom => 120, _ => 20 };
    public static double NorthLineBlueWidthMM(this MapStandard s) => s == MapStandard.Isom ? 0.12 : 0.18;

    /// <summary>Factor for symbol sizes when a map changes scale within the same standard; 1 when the standard changes.</summary>
    public static double SymbolSizeFactor(int oldScale, int newScale)
    {
        var a = ForScale(oldScale);
        var b = ForScale(newScale);
        return a != b ? 1 : b.Enlargement(newScale) / a.Enlargement(oldScale);
    }
}

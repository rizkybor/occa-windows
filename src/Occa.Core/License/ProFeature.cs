namespace Occa.Core.License;

/// <summary>
/// What a license key unlocks (Trial, Professional, Lifetime, Organization); Starter has <see cref="FreeLimits"/>.
/// Starter keeps everything else: drawing with all ISOM/ISSprOM symbols, course planning, PDF export (with a small
/// "Made with OCCA" credit), up to <see cref="FreeLimits.Projects"/> projects at the <see cref="FreeLimits.Scales"/> scales,
/// and up to <see cref="FreeLimits.Courses"/> courses. No fieldwork, contours or live basemap. Port of <c>ProFeature</c>.
/// </summary>
public enum ProFeature
{
    UnlimitedProjects, AllScales, UnlimitedCourses, Fieldwork,
    HdImagery, LiveBasemap, MyLocation, BasemapImport, Contours, ImportMaps, CustomSymbols,
    CourseExport, CourseImages,
    TrackRecording, Gpx, FieldToMap, FieldTransfer, FieldReport,
    RasterExport, VectorExport, NoWatermark,
}

public static class ProFeatureExtensions
{
    public static string Title(this ProFeature f) => f switch
    {
        ProFeature.UnlimitedProjects => "Unlimited projects",
        ProFeature.AllScales => "Every map scale (1:1000–1:15000, indoor 1:250–1:2000)",
        ProFeature.UnlimitedCourses => $"More than {FreeLimits.Courses} courses",
        ProFeature.Fieldwork => "Fieldwork: field mode, notes, and photos",
        ProFeature.HdImagery => "HD satellite imagery",
        ProFeature.LiveBasemap => "Live basemap (real-time satellite imagery)",
        ProFeature.MyLocation => "Show my location on the map",
        ProFeature.BasemapImport => "GeoTIFF basemaps",
        ProFeature.Contours => "Contours from elevation data",
        ProFeature.ImportMaps => "Import OCAD / OOM maps and courses",
        ProFeature.CustomSymbols => "Custom symbol sets",
        ProFeature.CourseExport => "IOF XML 3.0 and course PNG export",
        ProFeature.CourseImages => "Images on course maps",
        ProFeature.TrackRecording => "Track recording and survey coverage",
        ProFeature.Gpx => "GPX import and export",
        ProFeature.FieldToMap => "Turn field notes and tracks into map objects",
        ProFeature.FieldTransfer => "Send field data between devices",
        ProFeature.FieldReport => "Field report PDF",
        ProFeature.RasterExport => "Raster export (TIFF, PNG, JPEG)",
        ProFeature.VectorExport => "Vector export (OOM .omap, DXF)",
        _ => "PDF without the OCCA credit",
    };

    /// <summary>Segoe Fluent Icons glyph (the Mac uses SF Symbols).</summary>
    public static string Glyph(this ProFeature f) => f switch
    {
        ProFeature.UnlimitedProjects => "",
        ProFeature.AllScales => "",
        ProFeature.UnlimitedCourses => "",
        ProFeature.Fieldwork => "",
        ProFeature.HdImagery => "",
        ProFeature.LiveBasemap => "",
        ProFeature.MyLocation => "",
        ProFeature.BasemapImport => "",
        ProFeature.Contours => "",
        ProFeature.ImportMaps => "",
        ProFeature.CustomSymbols => "",
        ProFeature.CourseExport => "",
        ProFeature.CourseImages => "",
        ProFeature.TrackRecording => "",
        ProFeature.Gpx => "",
        ProFeature.FieldToMap => "",
        ProFeature.FieldTransfer => "",
        ProFeature.FieldReport => "",
        ProFeature.RasterExport => "",
        ProFeature.VectorExport => "",
        _ => "",
    };

    /// <summary>The short list shown on the plans screen.</summary>
    public static readonly ProFeature[] Highlights =
    {
        ProFeature.UnlimitedProjects, ProFeature.AllScales, ProFeature.UnlimitedCourses, ProFeature.Fieldwork, ProFeature.Contours,
        ProFeature.HdImagery, ProFeature.CourseExport, ProFeature.TrackRecording, ProFeature.ImportMaps, ProFeature.VectorExport,
    };
}

/// <summary>
/// Starter (free) plan limits. Existing projects and data over a limit are never locked: they open, show, and export to PDF;
/// only adding more needs a license key.
/// </summary>
public static class FreeLimits
{
    public const int Projects = 3;
    /// <summary>Sprint (ISSprOM base 1:4000) and forest (ISOM 1:10000); one A4 sheet sets the area.</summary>
    public static readonly int[] Scales = { 4000, 10000 };
    public const int Courses = 4;

    public static string ScalesLabel => string.Join(" · ", Scales.Select(s => $"1:{s}"));

    /// <summary>Scales that can be picked: every scale on a paid plan, Starter's two otherwise (plus <paramref name="current"/>).</summary>
    public static List<int> PickableScales(bool isPro, int? current = null, bool indoor = false)
    {
        var all = indoor ? Sheet.MapPresets.IndoorScales : Sheet.MapPresets.Scales;
        var list = isPro ? all.ToList() : all.Where(s => Scales.Contains(s) || s == current).ToList();
        if (current is int c && !list.Contains(c)) list.Add(c);
        list.Sort();
        return list;
    }

    public static bool AllowsScale(bool isPro, int scale) => isPro || Scales.Contains(scale);
}

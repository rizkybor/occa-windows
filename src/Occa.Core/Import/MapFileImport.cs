using Occa.Core.Geometry;
using Occa.Core.Project;
using Occa.Core.Sheet;
using Occa.Core.Vector;

namespace Occa.Core.Import;

/// <summary>A new project from an OCAD (.ocd) or OOM (.omap/.xmap) map file: its objects and a locked map frame centred on them. Courses (OCAD course setting files) arrive with M4.</summary>
public sealed class MapFileContent
{
    public enum SourceKind { Ocad, Omap }

    public required SourceKind Source { get; init; }
    public required int Scale { get; init; }
    public List<Feature> Features { get; init; } = new();
    /// <summary>UTM zone stated by the file (OOM); null for OCAD, whose zone the user chooses.</summary>
    public UtmZone? KnownZone { get; init; }
    public required OcadImportReport Report { get; init; }

    public Rect2D? Extent => Rect2D.FromPoints(Features.Select(f => f.Bounds).Where(b => b.HasValue).SelectMany(b => new[] { new Point2D(b!.Value.MinX, b.Value.MinY), new Point2D(b.Value.MaxX, b.Value.MaxY) }));
    public bool IsEmpty => Features.Count == 0;
}

public sealed class MapImportException : Exception
{
    public enum Kind { TooLarge, Empty, UnsupportedExtension }
    public Kind Reason { get; }
    public MapImportException(Kind reason, string message) : base(message) => Reason = reason;
}

public static class MapFileImport
{
    public static readonly string[] FileExtensions = { "ocd", "omap", "xmap" };
    private const int MaxBytes = 200_000_000;

    public static MapFileContent Read(byte[] data, string fileExtension)
    {
        if (data.Length > MaxBytes) throw new MapImportException(MapImportException.Kind.TooLarge, "The map file is larger than 200 MB.");
        MapFileContent content;
        switch (fileExtension.TrimStart('.').ToLowerInvariant())
        {
            case "ocd":
            {
                var map = OcadReader.Read(data);
                var (features, report) = map.Features();
                content = new MapFileContent { Source = MapFileContent.SourceKind.Ocad, Scale = (int)Math.Round(map.Scale), Features = features, KnownZone = null, Report = report };
                break;
            }
            case "omap":
            case "xmap":
            {
                var geo = OmapReader.Georeference(data) ?? throw OmapReadException.NotGeo();
                var zone = UtmZone.Containing(geo.Reference);
                var (features, report) = OmapReader.Read(data, zone);
                content = new MapFileContent { Source = MapFileContent.SourceKind.Omap, Scale = geo.Scale, Features = features, KnownZone = zone, Report = report };
                break;
            }
            default:
                throw new MapImportException(MapImportException.Kind.UnsupportedExtension, $"Unsupported map file (.{fileExtension.TrimStart('.')}). Use .ocd, .omap, or .xmap.");
        }
        if (content.IsEmpty) throw new MapImportException(MapImportException.Kind.Empty, "No map objects in this file.");
        return content;
    }

    /// <summary>Zones to try for an OCAD map (which stores no zone): the neighbours of zone first, same hemisphere, then the other hemisphere.</summary>
    public static List<UtmZone> CandidateZones(UtmZone zone, int maxSteps = 8)
    {
        var result = new List<UtmZone>();
        foreach (var north in new[] { zone.IsNorth, !zone.IsNorth })
        {
            if (north != zone.IsNorth && UtmZone.Create(zone.Number, north) is { } same) result.Add(same);
            for (int step = 1; step <= Math.Max(maxSteps, 1); step++)
                foreach (var n in new[] { zone.Number + step, zone.Number - step })
                {
                    int wrapped = (n - 1 + 60) % 60 + 1;
                    if (UtmZone.Create(wrapped, north) is { } z && !result.Contains(z) && z != zone) result.Add(z);
                }
        }
        return result;
    }

    /// <summary>Scale for the project sheet: the map's own scale when it is a preset, otherwise the nearest preset.</summary>
    public static int SheetScale(int scale) => MapPresets.Scales.MinBy(s => Math.Abs(s - scale));

    /// <summary>Project document: A4 in the orientation that fits the content, frame centred on it and locked.</summary>
    public static ProjectDocument Document(string name, MapFileContent content, UtmZone zone, int snapStepM = 10)
    {
        var e = content.Extent;
        var sheet = new SheetSpec { Orientation = e is { } ex && ex.Width > ex.Height ? SheetOrientation.Landscape : SheetOrientation.Portrait, ScaleDenominator = SheetScale(content.Scale) };
        var project = new Project.Project { Name = name, Sheet = sheet, CrsEpsg = zone.Epsg, AreaLocked = true, SnapStepM = snapStepM };
        if (e is { } ext) project.FrameOrigin = MapFrame.Centered(ext.Center, sheet).SnappedToStep(snapStepM).Origin;
        return new ProjectDocument { Project = project, Features = content.Features };
    }
}

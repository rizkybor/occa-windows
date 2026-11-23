using Occa.Core.Courses;
using Occa.Core.Geometry;
using Occa.Core.Project;

namespace Occa.Core.Import;

public sealed class CourseImportException : Exception
{
    public CourseImportException(string message) : base(message) { }

    public static CourseImportException UnsupportedExtension(string ext) => new($"Unsupported course file (.{ext}). Use .occa, .ocd, .omap, or .xmap.");
}

/// <summary>
/// "Import course" on the Course page: controls and courses from another file, placed in the current project's zone
/// (port of <c>CourseFileImport</c>).
///
/// - <c>.occa</c> (and older <c>.coca</c> / <c>.cpencil</c>): the courses of that project, with control descriptions; positions are
///   converted when that project uses another UTM zone. Course objects (text, shapes, images) are not copied.
/// - <c>.ocd</c>: an OCAD course setting file (<see cref="OcadCourseReader"/>).
/// - <c>.omap</c> / <c>.xmap</c>: OOM has no course data, so start (701), control (702), and finish (706) symbols drawn on the map
///   become controls (without codes or course order).
/// </summary>
public static class CourseFileImport
{
    public static IReadOnlyList<string> FileExtensions { get; } = new[] { ".occa" }.Concat(ProjectArchive.LegacyFileExtensions).Concat(new[] { ".ocd", ".omap", ".xmap" }).ToList();

    public static OcadCourseImport Read(byte[] data, string fileExtension, UtmZone zone)
    {
        var ext = fileExtension.TrimStart('.').ToLowerInvariant();
        if (ext == "occa" || ProjectArchive.LegacyFileExtensions.Contains("." + ext)) return FromProjectArchive(data, zone);
        if (ext == "ocd")
        {
            var result = OcadCourseReader.Read(data);
            if (result.Controls.Count == 0) throw new CourseImportException("This OCAD file has no course setting data (it is a map). Use Import map for maps.");
            return result;
        }
        if (ext is "omap" or "xmap")
        {
            var result = OmapReader.CourseControls(data, zone);
            if (result.Controls.Count == 0) throw new CourseImportException("No course symbols (701 start, 702 control, 706 finish) in this OOM map.");
            return result;
        }
        throw CourseImportException.UnsupportedExtension(ext);
    }

    internal static OcadCourseImport FromProjectArchive(byte[] data, UtmZone zone)
    {
        var entries = ProjectArchive.ReadEntries(data);
        if (!entries.TryGetValue(ProjectPackage.ProjectFile, out var projectData)) throw new ProjectArchiveException(ProjectArchiveError.MissingProject, "The archive has no project.json.");
        var project = ProjectPackage.ReadProject(projectData);
        var doc = ProjectPackage.Read(entries);
        var set = doc.Courses;
        if (set.Controls.Count == 0) throw new CourseImportException($"“{project.Name}” has no controls or courses.");
        // Same place on the ground in the current project's zone.
        var source = project.Zone ?? zone;
        Point2D Convert(Point2D p) => source == zone ? p : zone.Project(source.Unproject(p));
        var ids = new Dictionary<Guid, Guid>();
        var controls = set.Controls.Select(c =>
        {
            var copy = c.Clone();
            copy.Id = Guid.NewGuid();
            copy.Position = Convert(c.Position);
            ids[c.Id] = copy.Id;
            return copy;
        }).ToList();
        var courses = set.Courses.Select(c =>
        {
            var copy = c.Clone();
            copy.Id = Guid.NewGuid();
            copy.ControlIds = c.ControlIds.Where(ids.ContainsKey).Select(id => ids[id]).ToList();
            return copy;
        }).ToList();
        return new OcadCourseImport { Controls = controls, Courses = courses };
    }
}

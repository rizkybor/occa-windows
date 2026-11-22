using System.Text.Json;
using System.Text.Json.Nodes;

namespace Occa.Core.Project;

/// <summary>
/// What is inside a project package, read from its JSON files without a full model (M0 spike). Unknown fields are not
/// touched, so this is safe for files written by any OCCA version. The package layout mirrors <c>CakraPackage</c>
/// on Mac: <c>project.json</c>, <c>strokes.json</c>, <c>features.json</c>, <c>courses.json</c>, <c>field.json</c>.
/// </summary>
public sealed record ProjectSummary(
    Guid Id,
    string Name,
    int ScaleDenominator,
    int? CrsEpsg,
    bool AreaLocked,
    int FeatureCount,
    int StrokeCount,
    int ControlCount,
    int CourseCount,
    string EventTitle,
    IReadOnlyList<string> Files)
{
    public const string StrokesFileName = "strokes.json";
    public const string FeaturesFileName = "features.json";
    public const string CoursesFileName = "courses.json";
    public const string FieldFileName = "field.json";

    /// <summary>Summary of a <c>.occa</c> archive's entries (see <see cref="ProjectArchive.ReadEntries(ReadOnlySpan{byte})"/>).</summary>
    public static ProjectSummary FromEntries(IReadOnlyDictionary<string, byte[]> entries)
    {
        var project = Parse(entries[ProjectArchive.ProjectFileName]) ?? throw new JsonException("project.json is not a JSON object.");
        var sheet = project["sheet"] as JsonObject;
        var features = Envelope(entries, FeaturesFileName, "features");
        var strokes = Envelope(entries, StrokesFileName, "strokes");
        var courses = Envelope(entries, CoursesFileName, "courses") as JsonObject;

        return new ProjectSummary(
            Id: Guid.TryParse(project["id"]?.GetValue<string>(), out var id) ? id : Guid.Empty,
            Name: project["name"]?.GetValue<string>() ?? "",
            ScaleDenominator: sheet?["scaleDenominator"]?.GetValue<int>() ?? 0,
            CrsEpsg: project["crsEPSG"]?.GetValue<int>(),
            AreaLocked: project["areaLocked"]?.GetValue<bool>() ?? false,
            FeatureCount: (features as JsonArray)?.Count ?? 0,
            StrokeCount: (strokes as JsonArray)?.Count ?? 0,
            ControlCount: (courses?["controls"] as JsonArray)?.Count ?? 0,
            CourseCount: (courses?["courses"] as JsonArray)?.Count ?? 0,
            EventTitle: courses?["eventTitle"]?.GetValue<string>() ?? "",
            Files: entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Summary of a <c>.occa</c> file on disk.</summary>
    public static ProjectSummary FromArchiveFile(string path) => FromEntries(ProjectArchive.ReadEntries(path));

    /// <summary>Total objects on the map: drawn features plus sketch strokes.</summary>
    public int ObjectCount => FeatureCount + StrokeCount;

    private static JsonObject? Parse(byte[] json) => JsonNode.Parse(json) as JsonObject;

    /// <summary>The payload of a <c>{ "version": 1, "&lt;key&gt;": … }</c> envelope file, or null when the file is absent.</summary>
    private static JsonNode? Envelope(IReadOnlyDictionary<string, byte[]> entries, string file, string key)
        => entries.TryGetValue(file, out var bytes) ? Parse(bytes)?[key] : null;
}

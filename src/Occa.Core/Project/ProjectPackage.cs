using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Occa.Core.Json;
using Occa.Core.Courses;
using Occa.Core.Vector;

namespace Occa.Core.Project;

/// <summary>Full contents of one project loaded into memory (<c>ProjectDocument</c> on Mac).</summary>
public sealed class ProjectDocument
{
    public Project Project { get; set; } = new();
    public List<Stroke> Strokes { get; set; } = new();
    public List<Feature> Features { get; set; } = new();
    /// <summary>Course planner: controls + courses (courses.json; absent in older packages).</summary>
    public CourseSet Courses { get; set; } = new();
    /// <summary>Field data (M4); kept as JSON until ported. null = no field.json.</summary>
    public JsonObject? Field { get; set; }

    public int ObjectCount => Features.Count + Strokes.Count;
}

/// <summary>
/// The <c>.cakra</c> package layout shared with Mac: <c>project.json</c>, <c>strokes.json</c>, <c>features.json</c>,
/// <c>courses.json</c>, <c>field.json</c> (envelopes <c>{version, key}</c> except project.json). Reads from a folder
/// or from the entries of a <c>.occa</c> archive; writes atomically with sorted keys so files compare equal to Mac's.
/// </summary>
public static class ProjectPackage
{
    public const string FileExtension = ".cakra";
    public const string ProjectFile = "project.json";
    public const string StrokesFile = "strokes.json";
    public const string FeaturesFile = "features.json";
    public const string CoursesFile = "courses.json";
    public const string FieldFile = "field.json";
    public const string ThumbnailFile = "thumbnail.jpg";
    public const string FieldPhotosFolder = "field-photos";
    public const int FormatVersion = 1;

    private sealed class Envelope<T>
    {
        public int Version { get; set; } = FormatVersion;
        public T? Payload { get; set; }
    }

    public static Project ReadProject(ReadOnlySpan<byte> json) => OccaJson.Deserialize<Project>(json);

    public static ProjectDocument Read(IReadOnlyDictionary<string, byte[]> entries)
    {
        var doc = new ProjectDocument { Project = ReadProject(entries[ProjectFile]) };
        if (entries.TryGetValue(StrokesFile, out var strokes)) doc.Strokes = EnvelopePayload<List<Stroke>>(strokes, "strokes") ?? new();
        if (entries.TryGetValue(FeaturesFile, out var features)) doc.Features = EnvelopePayload<List<Feature>>(features, "features") ?? new();
        if (entries.TryGetValue(CoursesFile, out var courses)) doc.Courses = EnvelopePayload<CourseSet>(courses, "courses") ?? new CourseSet();
        if (entries.TryGetValue(FieldFile, out var field))
        {
            try { doc.Field = JsonNode.Parse(field)?["field"] as JsonObject; } catch (JsonException) { doc.Field = null; }
        }
        return doc;
    }

    /// <summary>Reads a package folder (the JSON files only).</summary>
    public static ProjectDocument Read(string folder)
    {
        var entries = new Dictionary<string, byte[]>();
        foreach (var name in new[] { ProjectFile, StrokesFile, FeaturesFile, CoursesFile, FieldFile })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) entries[name] = File.ReadAllBytes(path);
        }
        return Read(entries);
    }

    private static T? EnvelopePayload<T>(byte[] json, string key) where T : class
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException($"{key} envelope is not an object.");
        var payload = node[key];
        return payload is null ? null : payload.Deserialize<T>(OccaJson.Options);
    }

    /// <summary>The package files of a document as JSON text (sorted keys, no indentation, like Mac's encoder).</summary>
    public static Dictionary<string, string> Serialize(ProjectDocument doc)
    {
        var files = new Dictionary<string, string>
        {
            [ProjectFile] = OccaJson.SerializeSorted(doc.Project),
            [StrokesFile] = EnvelopeJson("strokes", JsonSerializer.SerializeToNode(doc.Strokes, OccaJson.Options)),
            [FeaturesFile] = EnvelopeJson("features", JsonSerializer.SerializeToNode(doc.Features, OccaJson.Options)),
        };
        // Like Mac: courses.json exists only when the set has content.
        if (!(doc.Courses.IsEmpty && doc.Courses.EventTitle.Length == 0)) files[CoursesFile] = EnvelopeJson("courses", JsonSerializer.SerializeToNode(doc.Courses, OccaJson.Options));
        if (doc.Field is not null) files[FieldFile] = EnvelopeJson("field", doc.Field.DeepClone());
        return files;
    }

    private static string EnvelopeJson(string key, JsonNode? payload)
    {
        var o = new JsonObject { ["version"] = FormatVersion, [key] = payload };
        return OccaJson.Sorted(o)!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Writes the document into a package folder atomically (temp file, then move; NFR-5).</summary>
    public static void Write(ProjectDocument doc, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var (name, json) in Serialize(doc)) WriteAtomic(Path.Combine(folder, name), Encoding.UTF8.GetBytes(json));
        if (doc.Courses.IsEmpty && doc.Courses.EventTitle.Length == 0) File.Delete(Path.Combine(folder, CoursesFile));
        if (doc.Field is null) File.Delete(Path.Combine(folder, FieldFile));
    }

    public static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Unpacks an archive's entries into a folder, skipping the per-device imagery cache (as Mac does).</summary>
    public static void Unpack(IReadOnlyDictionary<string, byte[]> entries, string folder)
    {
        foreach (var (path, data) in entries)
        {
            if (path.StartsWith(ImagerySnapshot.Folder + "/", StringComparison.Ordinal)) continue;
            var target = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            WriteAtomic(target, data);
        }
    }
}

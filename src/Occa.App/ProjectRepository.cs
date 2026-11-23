using Occa.Core.Project;

namespace Occa.App;

/// <summary>
/// The projects folder (<c>%LOCALAPPDATA%\OCCA\Projects\&lt;id&gt;.cakra</c>, FR-W9), the same package layout as Mac's
/// <c>ProjectRepository</c>: lists packages, imports <c>.occa</c> archives (a project whose id already exists gets a
/// new id and the suffix " (imported)", nothing is overwritten).
/// </summary>
public sealed class ProjectRepository
{
    public sealed record Entry(Project Project, string Folder)
    {
        public string ThumbnailPath => Path.Combine(Folder, ProjectPackage.ThumbnailFile);
        public bool HasThumbnail => File.Exists(ThumbnailPath);
    }

    public string Root { get; }

    public ProjectRepository(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public List<Entry> Load()
    {
        var entries = new List<Entry>();
        foreach (var dir in Directory.EnumerateDirectories(Root, "*" + ProjectPackage.FileExtension))
        {
            var projectFile = Path.Combine(dir, ProjectPackage.ProjectFile);
            if (!File.Exists(projectFile)) continue;
            try { entries.Add(new Entry(ProjectPackage.ReadProject(File.ReadAllBytes(projectFile)), dir)); }
            catch (Exception) { /* a damaged package is skipped, not shown */ }
        }
        return entries.OrderByDescending(e => e.Project.UpdatedAt).ToList();
    }

    public string FolderFor(Guid id) => Path.Combine(Root, id.ToString().ToUpperInvariant() + ProjectPackage.FileExtension);

    /// <summary>Writes a new project package (e.g. from an imported OCAD/OOM map).</summary>
    public Entry Create(ProjectDocument doc)
    {
        var folder = FolderFor(doc.Project.Id);
        ProjectPackage.Write(doc, folder);
        return new Entry(doc.Project, folder);
    }

    /// <summary>Imports a <c>.occa</c> archive; returns the new package folder.</summary>
    public Entry Import(byte[] archive)
    {
        var entries = ProjectArchive.ReadEntries(archive);
        var project = ProjectPackage.ReadProject(entries[ProjectPackage.ProjectFile]);
        var existing = new HashSet<Guid>(Load().Select(e => e.Project.Id));
        var staging = Path.Combine(Root, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProjectPackage.Unpack(entries, staging);
            if (existing.Contains(project.Id))
            {
                project.Id = Guid.NewGuid();
                project.Name += " (imported)";
                ProjectPackage.WriteAtomic(Path.Combine(staging, ProjectPackage.ProjectFile), System.Text.Encoding.UTF8.GetBytes(Core.Json.OccaJson.SerializeSorted(project)));
            }
            // Make sure the contents read completely before it enters the project list.
            _ = ProjectPackage.Read(staging);
            var target = FolderFor(project.Id);
            Directory.Move(staging, target);
            return new Entry(project, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}

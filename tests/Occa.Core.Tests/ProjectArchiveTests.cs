using Occa.Core.Project;

namespace Occa.Core.Tests;

/// <summary>
/// <c>sample-park-sprint.occa</c> and <c>large-forest-map.occa</c> were produced by CakraCore itself
/// (<c>CakraPackage.write</c> + <c>ProjectArchive.pack</c>) on a Mac, so they are exactly what the Mac/iPad app
/// shares. The expected JSON files next to the small one are the package contents before packing.
/// </summary>
public class ProjectArchiveTests
{
    [Fact]
    public void ReadsEveryEntryOfAMacArchive()
    {
        var entries = ProjectArchive.ReadEntries(Fixtures.Path("sample-park-sprint.occa"));

        Assert.Equal(new[] { "courses.json", "features.json", "project.json", "strokes.json", "thumbnail.jpg" },
            entries.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var file in new[] { "project.json", "features.json", "courses.json", "strokes.json" })
            Assert.Equal(Fixtures.Bytes($"sample-park-sprint/{file}"), entries[file]);
        Assert.DoesNotContain(entries.Keys, k => k.StartsWith("imagery/"));
    }

    [Fact]
    public void SummarisesObjectsAndCourses()
    {
        var summary = ProjectSummary.FromArchiveFile(Fixtures.Path("sample-park-sprint.occa"));

        Assert.Equal("Sample Park Sprint", summary.Name);
        Assert.Equal(Guid.Parse("6F1D2C3B-4A5E-4F60-9B71-8C2D3E4F5A6B"), summary.Id);
        Assert.Equal(4000, summary.ScaleDenominator);
        Assert.Equal(32748, summary.CrsEpsg);
        Assert.True(summary.AreaLocked);
        Assert.Equal(7, summary.FeatureCount);
        Assert.Equal(1, summary.StrokeCount);
        Assert.Equal(8, summary.ObjectCount);
        Assert.Equal(5, summary.ControlCount);
        Assert.Equal(2, summary.CourseCount);
        Assert.Equal("Sample Sprint", summary.EventTitle);
    }

    [Fact]
    public void ReadsALargeArchiveWithLzfseBlocks()
    {
        var summary = ProjectSummary.FromArchiveFile(Fixtures.Path("large-forest-map.occa"));

        Assert.Equal("Large Forest Map", summary.Name);
        Assert.Equal(600, summary.FeatureCount);
        Assert.Equal(0, summary.StrokeCount);
        Assert.Equal(5, summary.ControlCount);
        Assert.Equal(2, summary.CourseCount);
        var entries = ProjectArchive.ReadEntries(Fixtures.Path("large-forest-map.occa"));
        Assert.Equal(24_000, entries["dem.bin"].Length);
    }

    [Fact]
    public void RejectsNonArchive()
    {
        var e = Assert.Throws<ProjectArchiveException>(() => ProjectArchive.ReadEntries("hello"u8));
        Assert.Equal(ProjectArchiveError.NotArchive, e.Error);
    }

    [Fact]
    public void RejectsTruncatedArchive()
    {
        var bytes = Fixtures.Bytes("large-forest-map.occa");
        var e = Assert.Throws<ProjectArchiveException>(() => ProjectArchive.ReadEntries(bytes.AsSpan(0, bytes.Length / 2)));
        Assert.Equal(ProjectArchiveError.Corrupt, e.Error);
    }

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b")]
    [InlineData(".hidden")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    public void RejectsUnsafePaths(string path)
    {
        var e = Assert.Throws<ProjectArchiveException>(() => ProjectArchive.ValidatePath(path));
        Assert.Equal(ProjectArchiveError.UnsafePath, e.Error);
    }

    [Fact]
    public void AcceptsNestedPaths()
    {
        ProjectArchive.ValidatePath("course-images/logo.jpg");
    }
}

using Occa.Core.Compression;
using Occa.Core.Project;

namespace Occa.Core.Tests;

public class ProjectArchiveWriteTests
{
    [Fact]
    public void EncoderRoundTripsThroughTheDecoder()
    {
        var data = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 31 % 251)).ToArray();
        var encoded = LzfseEncoder.Encode(data);
        Assert.Equal("bvx-", System.Text.Encoding.ASCII.GetString(encoded, 0, 4));
        Assert.Equal(data, LzfseDecoder.Decode(encoded));
        Assert.Empty(LzfseDecoder.Decode(LzfseEncoder.Encode(ReadOnlySpan<byte>.Empty)));
    }

    [Fact]
    public void PackedArchiveReadsBackIdentically()
    {
        var original = ProjectArchive.ReadEntries(Fixtures.Path("symbol-display.occa"));
        var packed = ProjectArchive.Pack(original);
        Assert.True(ProjectArchive.IsArchive(packed));
        var back = ProjectArchive.ReadEntries(packed);
        Assert.Equal(original.Keys.OrderBy(k => k, StringComparer.Ordinal), back.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (k, v) in original) Assert.Equal(v, back[k]);
        // Kept for the Mac-side check (tests/Occa.Core.Tests/Fixtures/README): the file Apple's decoder must open.
        File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "windows-packed.occa"), packed);
    }

    [Fact]
    public void PackSkipsImageryAndRejectsUnsafePaths()
    {
        var entries = new Dictionary<string, byte[]> { ["project.json"] = "{}"u8.ToArray(), ["imagery/satellite.jpg"] = new byte[] { 1 } };
        var back = ProjectArchive.ReadEntries(ProjectArchive.Pack(entries));
        Assert.Single(back);
        var evil = new Dictionary<string, byte[]> { ["project.json"] = "{}"u8.ToArray(), ["../x"] = new byte[] { 1 } };
        Assert.Throws<ProjectArchiveException>(() => ProjectArchive.Pack(evil));
        Assert.Throws<ProjectArchiveException>(() => ProjectArchive.Pack(new Dictionary<string, byte[]> { ["a.json"] = new byte[] { 1 } }));
    }
}

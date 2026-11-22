using System.Buffers.Binary;
using System.Text;
using Occa.Core.Compression;

namespace Occa.Core.Project;

/// <summary>
/// The <c>.occa</c> project file written by OCCA on Mac/iPad (<c>ProjectArchive.swift</c> in CakraCore): the whole
/// project package (<c>project.json</c>, <c>features.json</c>, <c>courses.json</c>, …) in one file.
///
/// Layout: the 8-byte magic <c>CPENCIL1</c> followed by an LZFSE stream. The decompressed payload is: entry count
/// (UInt32), then for each entry the path length (UInt32), the UTF-8 relative path, the data length (UInt64) and the
/// data. All integers are little-endian. Entries are sorted by path. The same container, with another magic, is used by
/// <c>.occafield</c> files.
/// </summary>
public static class ProjectArchive
{
    public const string FileExtension = ".occa";
    public static readonly IReadOnlyList<string> LegacyFileExtensions = new[] { ".coca", ".cpencil" };
    public const string ProjectFileName = "project.json";

    /// <summary>Per-device cache of captured basemap imagery; never read from someone else's file (same rule as Mac).</summary>
    public const string ImageryFolder = "imagery";

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CPENCIL1");
    private const int MaxEntries = 100_000;
    private const long MaxTotalBytes = 4L << 30;
    private const int MaxPathLength = 1024;

    /// <summary>True when the bytes start with the <c>.occa</c> magic.</summary>
    public static bool IsArchive(ReadOnlySpan<byte> data) => data.Length >= Magic.Length && data.Slice(0, Magic.Length).SequenceEqual(Magic);

    /// <summary>
    /// All entries of the archive, keyed by relative path. Throws <see cref="ProjectArchiveException"/> when the data is
    /// not an archive, is corrupt, contains an unsafe path, or has no <c>project.json</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> ReadEntries(ReadOnlySpan<byte> archive)
    {
        var entries = Decode(archive, Magic);
        if (!entries.ContainsKey(ProjectFileName)) throw new ProjectArchiveException(ProjectArchiveError.MissingProject, "Project file does not contain project.json.");
        return entries;
    }

    public static IReadOnlyDictionary<string, byte[]> ReadEntries(string path) => ReadEntries(File.ReadAllBytes(path));

    internal static Dictionary<string, byte[]> Decode(ReadOnlySpan<byte> archive, ReadOnlySpan<byte> magic)
    {
        if (archive.Length < magic.Length || !archive.Slice(0, magic.Length).SequenceEqual(magic))
            throw new ProjectArchiveException(ProjectArchiveError.NotArchive, "Not an OCCA project file (.occa).");
        byte[] payload;
        try
        {
            payload = LzfseDecoder.Decode(archive.Slice(magic.Length), MaxTotalBytes);
        }
        catch (InvalidDataException e)
        {
            throw new ProjectArchiveException(ProjectArchiveError.Corrupt, "Corrupt project file: decompression failed.", e);
        }

        var reader = new Reader(payload);
        int count = checked((int)reader.UInt32());
        if (count > MaxEntries) throw Corrupt("too many files");
        var entries = new Dictionary<string, byte[]>(count, StringComparer.Ordinal);
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            int pathLength = checked((int)reader.UInt32());
            if (pathLength <= 0 || pathLength > MaxPathLength) throw Corrupt("path length");
            string path;
            try
            {
                path = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(reader.Bytes(pathLength));
            }
            catch (DecoderFallbackException)
            {
                throw Corrupt("path");
            }
            ValidatePath(path);
            ulong size = reader.UInt64();
            total += (long)Math.Min(size, long.MaxValue);
            if (size > int.MaxValue || total > MaxTotalBytes) throw Corrupt("file size");
            entries[path] = reader.Bytes((int)size).ToArray();
        }
        if (!reader.IsAtEnd) throw Corrupt("trailing data at end");
        return entries;
    }

    /// <summary>Relative, forward-slash paths only: no absolute paths, no "..", no hidden files (same rules as Mac).</summary>
    public static void ValidatePath(string path)
    {
        var parts = path.Split('/');
        bool bad = path.StartsWith('/') || path.Contains('\\') || parts.Length == 0
                   || parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.StartsWith('.'));
        if (bad) throw new ProjectArchiveException(ProjectArchiveError.UnsafePath, $"Project file contains an unsafe path: {path}.");
    }

    private static ProjectArchiveException Corrupt(string what) => new(ProjectArchiveError.Corrupt, $"Corrupt project file: {what}.");

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public Reader(ReadOnlySpan<byte> data) => _data = data;

        public bool IsAtEnd => _offset == _data.Length;

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || _offset + n > _data.Length) throw Corrupt("truncated");
            var s = _data.Slice(_offset, n);
            _offset += n;
            return s;
        }

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));
    }
}

public enum ProjectArchiveError { NotArchive, Corrupt, UnsafePath, MissingProject }

public sealed class ProjectArchiveException : Exception
{
    public ProjectArchiveError Error { get; }

    public ProjectArchiveException(ProjectArchiveError error, string message, Exception? inner = null) : base(message, inner) => Error = error;
}

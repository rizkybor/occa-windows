using System.Text.Json;
using System.Text.Json.Serialization;
using Occa.Core.Geometry;
using Occa.Core.Json;

namespace Occa.Core.Contour;

/// <summary>Regular elevation grid in the project CRS (UTM, metres). Row 0 = northernmost. NaN value = no data. Port of <c>DEMGrid</c>.</summary>
public sealed class DemGrid : IEquatable<DemGrid>
{
    public sealed record Header
    {
        /// <summary>Top-left corner of the grid (cell edge, not centre).</summary>
        [JsonPropertyName("minX")] public double MinX { get; set; }
        [JsonPropertyName("maxY")] public double MaxY { get; set; }
        [JsonPropertyName("cellSize")] public double CellSize { get; set; }
        [JsonPropertyName("cols")] public int Cols { get; set; }
        [JsonPropertyName("rows")] public int Rows { get; set; }
        /// <summary>Data source, e.g. "Terrain Tiles (AWS) z14" or the user's DEM file name.</summary>
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        /// <summary>Native data resolution (m), for the interval-too-fine warning (FR-8.11).</summary>
        [JsonPropertyName("nativeResolutionM")] public double NativeResolutionM { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    public Header Head { get; }
    public float[] Values { get; }

    public DemGrid(Header header, float[] values)
    {
        if (values.Length != header.Cols * header.Rows) throw new ArgumentException("Value count does not match the grid size.");
        Head = header;
        Values = values;
    }

    public int Cols => Head.Cols;
    public int Rows => Head.Rows;
    public double CellSize => Head.CellSize;

    /// <summary>Centre coordinate of cell (col, row).</summary>
    public Point2D Center(int col, int row) => new(Head.MinX + (col + 0.5) * CellSize, Head.MaxY - (row + 0.5) * CellSize);

    public float Value(int col, int row) => col < 0 || row < 0 || col >= Cols || row >= Rows ? float.NaN : Values[row * Cols + col];

    /// <summary>Bilinear interpolation at a UTM point; NaN outside the grid or when neighbours have no data.</summary>
    public float Sample(Point2D p)
    {
        double fx = (p.X - Head.MinX) / CellSize - 0.5;
        double fy = (Head.MaxY - p.Y) / CellSize - 0.5;
        int c0 = (int)Math.Floor(fx), r0 = (int)Math.Floor(fy);
        float tx = (float)(fx - c0), ty = (float)(fy - r0);
        return Bilinear.Sample(Value(c0, r0), Value(c0 + 1, r0), Value(c0, r0 + 1), Value(c0 + 1, r0 + 1), tx, ty);
    }

    /// <summary>Minimum and maximum elevation (ignoring NaN).</summary>
    public (float Min, float Max)? Range
    {
        get
        {
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (var v in Values)
            {
                if (float.IsNaN(v)) continue;
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
            return lo <= hi ? (lo, hi) : null;
        }
    }

    /// <summary>Extent covered by the grid (UTM).</summary>
    public Rect2D Extent => new(Head.MinX, Head.MaxY - Rows * CellSize, Head.MinX + Cols * CellSize, Head.MaxY);

    /// <summary>
    /// Build a UTM grid for <paramref name="extent"/> by sampling an elevation function (e.g. a lat/lon DEM).
    /// The cell count is capped at <paramref name="maxCells"/> by enlarging the cell size when needed.
    /// </summary>
    public static DemGrid Resample(Rect2D extent, double cellSize, string source, double nativeResolutionM, Func<Point2D, float> elevation, int maxCells = 4_000_000)
    {
        double cell = Math.Max(cellSize, 0.25);
        while (Math.Ceiling(extent.Width / cell) * Math.Ceiling(extent.Height / cell) > maxCells) cell *= 1.25;
        int cols = Math.Max(2, (int)Math.Ceiling(extent.Width / cell));
        int rows = Math.Max(2, (int)Math.Ceiling(extent.Height / cell));
        var header = new Header { MinX = extent.MinX, MaxY = extent.MaxY, CellSize = cell, Cols = cols, Rows = rows, Source = source, NativeResolutionM = nativeResolutionM };
        var values = new float[cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                values[r * cols + c] = elevation(new Point2D(extent.MinX + (c + 0.5) * cell, extent.MaxY - (r + 0.5) * cell));
        return new DemGrid(header, values);
    }

    // MARK: Store in the project package (dem.json + dem.bin Float32 little-endian)

    public const string HeaderFile = "dem.json";
    public const string DataFile = "dem.bin";

    public void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        Project.ProjectPackage.WriteAtomic(Path.Combine(directory, HeaderFile), System.Text.Encoding.UTF8.GetBytes(OccaJson.SerializeSorted(Head)));
        var data = new byte[Values.Length * 4];
        for (int i = 0; i < Values.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), Values[i]);
        Project.ProjectPackage.WriteAtomic(Path.Combine(directory, DataFile), data);
    }

    public static DemGrid? Read(string directory)
    {
        var headerPath = Path.Combine(directory, HeaderFile);
        if (!File.Exists(headerPath)) return null;
        var header = OccaJson.Deserialize<Header>(File.ReadAllBytes(headerPath));
        var data = File.ReadAllBytes(Path.Combine(directory, DataFile));
        if (header.Cols <= 0 || header.Rows <= 0 || data.Length != header.Cols * header.Rows * 4) throw new InvalidDataException("DEM data does not match the header");
        var values = new float[header.Cols * header.Rows];
        for (int i = 0; i < values.Length; i++) values[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(i * 4));
        return new DemGrid(header, values);
    }

    public static void Remove(string directory)
    {
        foreach (var name in new[] { HeaderFile, DataFile })
        {
            var p = Path.Combine(directory, name);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    public bool Equals(DemGrid? other)
        => other is not null && Head with { Extra = null } == other.Head with { Extra = null } && Values.Length == other.Values.Length
           && Values.Zip(other.Values).All(p => p.First.Equals(p.Second));

    public override bool Equals(object? obj) => Equals(obj as DemGrid);
    public override int GetHashCode() => HashCode.Combine(Head.Cols, Head.Rows, Head.CellSize, Values.Length);
}

/// <summary>NaN-aware bilinear interpolation shared by the DEM grid, elevation rasters and terrain tiles.</summary>
public static class Bilinear
{
    /// <summary>
    /// Neighbours without data are ignored and the weights renormalised. Result is NaN when the valid-data weight is
    /// &lt; 0.5 (the point is closer to empty area than to data).
    /// </summary>
    public static float Sample(float a, float b, float c, float d, float tx, float ty)
    {
        Span<float> w = stackalloc float[] { (1 - tx) * (1 - ty), tx * (1 - ty), (1 - tx) * ty, tx * ty };
        Span<float> v = stackalloc float[] { a, b, c, d };
        float sum = 0, weight = 0;
        for (int i = 0; i < 4; i++)
        {
            if (w[i] <= 0 || float.IsNaN(v[i])) continue;
            sum += v[i] * w[i];
            weight += w[i];
        }
        return weight >= 0.5f ? sum / weight : float.NaN;
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Occa.Core.Geometry;

namespace Occa.Core.Contour;

/// <summary>Elevation source with its own georeference, before resampling to the project UTM grid (<c>ElevationRaster</c> on Mac).</summary>
public sealed class ElevationRaster
{
    /// <summary>EPSG:4326 (x = lon, y = lat) when <c>Zone</c> is null, otherwise a WGS84 UTM zone.</summary>
    public readonly record struct Crs(UtmZone? Zone)
    {
        public static readonly Crs Geographic = new((UtmZone?)null);
        public static Crs Utm(UtmZone z) => new(z);
        public bool IsGeographic => Zone is null;
    }

    public Crs CrsValue { get; }
    /// <summary>Coordinate of the top-left edge of cell (0,0) and cell size in CRS units (degrees or metres).</summary>
    public double OriginX { get; }
    public double OriginY { get; }
    public double CellX { get; }
    public double CellY { get; }
    public int Cols { get; }
    public int Rows { get; }
    public float[] Values { get; }

    public ElevationRaster(Crs crs, double originX, double originY, double cellX, double cellY, int cols, int rows, float[] values)
    {
        CrsValue = crs;
        OriginX = originX;
        OriginY = originY;
        CellX = cellX;
        CellY = cellY;
        Cols = cols;
        Rows = rows;
        Values = values;
    }

    /// <summary>Approximate resolution in metres (geographic: row spacing in latitude; 1 arc-second ≈ 30.9 m).</summary>
    public double ResolutionM => CrsValue.IsGeographic ? CellY * 111_320 : CellX;

    /// <summary>Bilinear elevation at a project UTM point (converted to the raster CRS first).</summary>
    public float Sample(Point2D p, UtmZone projectZone)
    {
        double x, y;
        if (CrsValue.Zone is not { } zone)
        {
            var ll = projectZone.Unproject(p);
            (x, y) = (ll.Lon, ll.Lat);
        }
        else if (zone == projectZone) (x, y) = (p.X, p.Y);
        else
        {
            var q = zone.Project(projectZone.Unproject(p));
            (x, y) = (q.X, q.Y);
        }
        double fx = (x - OriginX) / CellX - 0.5, fy = (OriginY - y) / CellY - 0.5;
        int c0 = (int)Math.Floor(fx), r0 = (int)Math.Floor(fy);
        float V(int c, int r) => c < 0 || r < 0 || c >= Cols || r >= Rows ? float.NaN : Values[r * Cols + c];
        float tx = (float)(fx - c0), ty = (float)(fy - r0);
        return Bilinear.Sample(V(c0, r0), V(c0 + 1, r0), V(c0, r0 + 1), V(c0 + 1, r0 + 1), tx, ty);
    }

    /// <summary>Resample to the project UTM grid. Cell size = native resolution (at least 0.5 m), capped by cell count.</summary>
    public DemGrid Grid(Rect2D extent, UtmZone zone, string source)
    {
        double res = ResolutionM;
        return DemGrid.Resample(extent, Math.Max(res, 0.5), source, res, p => Sample(p, zone));
    }
}

public enum DemReadErrorKind { Unsupported, Corrupt }

public sealed class DemReadException : Exception
{
    public DemReadErrorKind Kind { get; }
    public DemReadException(DemReadErrorKind kind, string detail) : base(kind == DemReadErrorKind.Unsupported ? $"DEM format not supported yet: {detail}." : $"DEM file is corrupt: {detail}.") => Kind = kind;

    public static DemReadException Unsupported(string s) => new(DemReadErrorKind.Unsupported, s);
    public static DemReadException Corrupt(string s) => new(DemReadErrorKind.Corrupt, s);
}

/// <summary>ESRI ASCII Grid (.asc) reader (port of <c>AsciiGridReader</c>).</summary>
public static class AsciiGridReader
{
    /// <summary>
    /// Header: ncols, nrows, xllcorner|xllcenter, yllcorner|yllcenter, cellsize, [NODATA_value]. CRS from .prj if present;
    /// without .prj, degree-like coordinates are taken as EPSG:4326, otherwise project UTM.
    /// </summary>
    public static ElevationRaster Read(byte[] data, string? prj, UtmZone projectZone)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(data); }
        var header = new Dictionary<string, double>();
        int pos = 0;
        // Read header lines as long as the line starts with a letter.
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            if (nl < 0) break;
            var line = text[pos..nl].Trim();
            if (line.Length == 0 || !char.IsLetter(line[0])) break;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) header[parts[0].ToLowerInvariant()] = v;
            pos = nl + 1;
        }
        if (!header.TryGetValue("ncols", out var nc) || !header.TryGetValue("nrows", out var nr) || !header.TryGetValue("cellsize", out var cell)
            || nc <= 1 || nr <= 1 || nc * nr > 100_000_000 || cell <= 0)
            throw DemReadException.Corrupt("incomplete ncols/nrows/cellsize header");
        int ncols = (int)nc, nrows = (int)nr;
        bool isCenter = header.ContainsKey("xllcenter");
        double xll = (header.TryGetValue("xllcorner", out var xc) ? xc : header.TryGetValue("xllcenter", out var xce) ? xce : 0) - (isCenter ? cell / 2 : 0);
        double yll = (header.TryGetValue("yllcorner", out var yc) ? yc : header.TryGetValue("yllcenter", out var yce) ? yce : 0) - (isCenter ? cell / 2 : 0);
        float noData = (float)(header.TryGetValue("nodata_value", out var nd) ? nd : -9999);
        var values = new List<float>(ncols * nrows);
        foreach (var token in text[pos..].Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw DemReadException.Corrupt("value is not a number");
            values.Add(v == noData ? float.NaN : v);
            if (values.Count == ncols * nrows) break;
        }
        if (values.Count != ncols * nrows) throw DemReadException.Corrupt("fewer values than ncols × nrows");
        var crs = CrsFrom(prj, xll, cell, projectZone);
        return new ElevationRaster(crs, xll, yll + nrows * cell, cell, cell, ncols, nrows, values.ToArray());
    }

    internal static ElevationRaster.Crs CrsFrom(string? prj, double sampleX, double cell, UtmZone projectZone)
    {
        if (prj is not null && UtmZone.FromWkt(prj) is { } zone) return ElevationRaster.Crs.Utm(zone);
        if (prj is not null && prj.ToUpperInvariant().StartsWith("GEOGCS", StringComparison.Ordinal)) return ElevationRaster.Crs.Geographic;
        return Math.Abs(sampleX) <= 180 && cell < 0.1 ? ElevationRaster.Crs.Geographic : ElevationRaster.Crs.Utm(projectZone);
    }
}

/// <summary>
/// Minimal GeoTIFF reader for single-band DEMs: uncompressed, Deflate (8/32946), LZW (5); predictor 1/2/3;
/// int16/uint16/int32/float32/float64; strips or tiles; little/big endian. Georeference from ModelTiepoint +
/// ModelPixelScale, CRS from GeoKey ProjectedCSType (UTM WGS84) or GeographicType (4326). Port of <c>GeoTIFFReader</c>.
/// </summary>
public static class GeoTiffReader
{
    public sealed record Decoded(int Width, int Height, float[] Values, double[] PixelScale, double[] Tiepoint, ElevationRaster.Crs? Crs);

    public static ElevationRaster Read(byte[] data, UtmZone projectZone)
    {
        var d = Decode(data);
        var scale = d.PixelScale; var tie = d.Tiepoint;
        if (scale.Length < 2 || tie.Length < 6 || scale[0] <= 0 || scale[1] <= 0)
            throw DemReadException.Unsupported("GeoTIFF without ModelPixelScale/ModelTiepoint (a world file may be needed)");
        double originX = tie[3] - tie[0] * scale[0], originY = tie[4] + tie[1] * scale[1];
        var resolved = d.Crs ?? (Math.Abs(originX) <= 180 && scale[0] < 0.1 ? ElevationRaster.Crs.Geographic : ElevationRaster.Crs.Utm(projectZone));
        return new ElevationRaster(resolved, originX, originY, scale[0], scale[1], d.Width, d.Height, d.Values);
    }

    /// <summary>Decode pixels + raw georeference tags (without validating the georeference).</summary>
    public static Decoded Decode(byte[] b)
    {
        if (b.Length <= 8) throw DemReadException.Corrupt("too short");
        bool little = (b[0], b[1]) switch { (0x49, 0x49) => true, (0x4D, 0x4D) => false, _ => throw DemReadException.Unsupported("not a TIFF") };
        int U16(int o) { if (o < 0 || o + 2 > b.Length) throw DemReadException.Corrupt("offset outside the file"); return little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)); }
        int U32(int o) { if (o < 0 || o + 4 > b.Length) throw DemReadException.Corrupt("offset outside the file"); return (int)(little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o))); }
        double F64(int o) { if (o < 0 || o + 8 > b.Length) throw DemReadException.Corrupt("offset outside the file"); return little ? BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(o)); }
        if (U16(2) != 42) throw DemReadException.Unsupported("BigTIFF or another TIFF version");

        // First IFD.
        int ifd = U32(4);
        int count = U16(ifd);
        if (count >= 1000) throw DemReadException.Corrupt("IFD");
        var tags = new Dictionary<int, (int Type, int Count, int ValueOffset)>();
        for (int i = 0; i < count; i++)
        {
            int e = ifd + 2 + i * 12;
            tags[U16(e)] = (U16(e + 2), U32(e + 4), e + 8);
        }
        var typeSize = new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 2, [4] = 4, [5] = 8, [6] = 1, [7] = 1, [8] = 2, [9] = 4, [10] = 8, [11] = 4, [12] = 8, [16] = 8 };
        /// Tag values as a number array (from inside the entry if they fit in 4 bytes, otherwise from the offset).
        double[] Numbers(int tag)
        {
            if (!tags.TryGetValue(tag, out var t) || !typeSize.TryGetValue(t.Type, out var size) || t.Count >= 50_000_000) return Array.Empty<double>();
            int baseOffset = size * t.Count <= 4 ? t.ValueOffset : U32(t.ValueOffset);
            var result = new double[t.Count];
            for (int i = 0; i < t.Count; i++)
            {
                int o = baseOffset + i * size;
                result[i] = t.Type switch
                {
                    3 => U16(o),
                    4 => (uint)U32(o),
                    8 => (short)U16(o),
                    9 => U32(o),
                    11 => BitConverter.Int32BitsToSingle(U32(o)),
                    12 => F64(o),
                    _ => o >= 0 && o < b.Length ? b[o] : throw DemReadException.Corrupt("offset outside the file"),
                };
            }
            return result;
        }
        int? One(int tag) { var n = Numbers(tag); return n.Length > 0 ? (int)n[0] : null; }

        int width = One(256) ?? 0, height = One(257) ?? 0;
        if (width <= 1 || height <= 1 || (long)width * height > 100_000_000) throw DemReadException.Corrupt("image size");
        int bits = One(258) ?? 1;
        int compression = One(259) ?? 1;
        int spp = One(277) ?? 1;
        int predictor = One(317) ?? 1;
        int format = One(339) ?? 1;   // 1 uint, 2 int, 3 float
        if (spp != 1) throw DemReadException.Unsupported("more than 1 band");
        if (compression is not (1 or 5 or 8 or 32946)) throw DemReadException.Unsupported($"compression {compression}");
        if (bits is not (16 or 32 or 64)) throw DemReadException.Unsupported($"{bits} bits per sample");
        int bps = bits / 8;

        // Data blocks: tiles (322/323/324/325) or strips (278/273/279).
        bool tiled = tags.ContainsKey(322);
        int blockW = tiled ? (One(322) ?? width) : width;
        int blockH = tiled ? (One(323) ?? height) : (One(278) ?? height);
        var offsets = Numbers(tiled ? 324 : 273).Select(v => (long)v).ToArray();
        var lengths = Numbers(tiled ? 325 : 279).Select(v => (long)v).ToArray();
        if (offsets.Length == 0 || offsets.Length != lengths.Length || blockW <= 0 || blockH <= 0) throw DemReadException.Corrupt("strip/tile table");
        int across = tiled ? (width + blockW - 1) / blockW : 1;

        float? noData = null;
        if (tags.TryGetValue(42113, out var nd))
        {
            int baseOffset = nd.Count <= 4 ? nd.ValueOffset : U32(nd.ValueOffset);
            int end = Math.Min(baseOffset + nd.Count, b.Length);
            var raw = b.AsSpan(baseOffset, Math.Max(0, end - baseOffset));
            int z = raw.IndexOf((byte)0);
            if (z >= 0) raw = raw[..z];
            if (float.TryParse(Encoding.UTF8.GetString(raw).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) noData = v;
        }

        var values = new float[width * height];
        Array.Fill(values, float.NaN);
        for (int i = 0; i < offsets.Length; i++)
        {
            long off = offsets[i], len = lengths[i];
            if (off < 0 || len < 0 || off + len > b.Length) throw DemReadException.Corrupt("block outside the file");
            var block = b.AsSpan((int)off, (int)len).ToArray();
            int expected = blockW * blockH * bps;
            block = compression switch { 5 => LzwDecode(block, expected), 8 or 32946 => Inflate(block, expected), _ => block };
            if (block.Length < blockW * Math.Min(blockH, height) * bps && compression != 1) throw DemReadException.Corrupt("block too short");
            int rowsInBlock = Math.Min(blockH, block.Length / (blockW * bps));
            if (predictor == 2) UndoHorizontal(block, blockW, rowsInBlock, bps, little);
            if (predictor == 3) UndoFloatingPoint(block, blockW, rowsInBlock, bps);
            int x0 = tiled ? (i % across) * blockW : 0;
            int y0 = tiled ? (i / across) * blockH : i * blockH;
            for (int r = 0; r < rowsInBlock; r++)
            {
                int y = y0 + r;
                if (y >= height) break;
                for (int c = 0; c < blockW; c++)
                {
                    int x = x0 + c;
                    if (x >= width) break;
                    int o = (r * blockW + c) * bps;
                    // Predictor 3 reorders bytes to big-endian; otherwise follow the file's byte order.
                    bool le = predictor != 3 && little;
                    ulong raw = 0;
                    for (int k = 0; k < bps; k++) raw |= (ulong)block[o + (le ? k : bps - 1 - k)] << (8 * k);
                    float v = (format, bits) switch
                    {
                        (3, 32) => BitConverter.Int32BitsToSingle((int)(uint)raw),
                        (3, 64) => (float)BitConverter.Int64BitsToDouble((long)raw),
                        (2, 16) => (short)(ushort)raw,
                        (2, 32) => (int)(uint)raw,
                        (_, 16) => (ushort)raw,
                        (_, 32) => (uint)raw,
                        _ => float.NaN,
                    };
                    values[y * width + x] = (noData is { } n && v == n) || v < -10_000 || !float.IsFinite(v) ? float.NaN : v;
                }
            }
        }

        // Georeference.
        var scale = Numbers(33550); var tie = Numbers(33922);
        ElevationRaster.Crs? crs = null;
        var keys = Numbers(34735).Select(v => (int)v).ToArray();
        if (keys.Length >= 4)
        {
            for (int k = 0; k < keys[3]; k++)
            {
                int e = 4 + k * 4;
                if (e + 3 >= keys.Length || keys[e + 1] != 0) continue;
                if (keys[e] == 3072 && UtmZone.FromEpsg(keys[e + 3]) is { } z) crs = ElevationRaster.Crs.Utm(z);
                if (keys[e] == 2048 && (keys[e + 3] == 4326 || keys[e + 3] == 4979)) crs ??= ElevationRaster.Crs.Geographic;
                if (keys[e] == 1024 && keys[e + 3] == 2) crs ??= ElevationRaster.Crs.Geographic;
            }
        }
        return new Decoded(width, height, values, scale, tie, crs);
    }

    /// <summary>TIFF Deflate = zlib (2-byte header + data + adler32).</summary>
    internal static byte[] Inflate(byte[] src, int expected)
    {
        if (src.Length <= 2) throw DemReadException.Corrupt("empty deflate block");
        try
        {
            using var z = new ZLibStream(new MemoryStream(src), CompressionMode.Decompress);
            var result = new byte[Math.Max(expected, 1)];
            int read = 0;
            while (read < result.Length)
            {
                int n = z.Read(result, read, result.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) throw DemReadException.Corrupt("deflate failed");
            return read == result.Length ? result : result[..read];
        }
        catch (InvalidDataException) { throw DemReadException.Corrupt("deflate failed"); }
    }

    /// <summary>TIFF LZW (MSB-first, 9–12-bit codes, Clear 256, EOI 257).</summary>
    public static byte[] LzwDecode(byte[] src, int expected)
    {
        var result = new List<byte>(expected);
        var table = new List<byte[]>(4096);
        void Reset() { table.Clear(); for (int i = 0; i < 256; i++) table.Add(new[] { (byte)i }); table.Add(Array.Empty<byte>()); table.Add(Array.Empty<byte>()); }
        Reset();
        int codeLen = 9, bitPos = 0;
        byte[]? prev = null;
        int? Next()
        {
            if (bitPos + codeLen > src.Length * 8) return null;
            int v = 0;
            for (int i = 0; i < codeLen; i++)
            {
                v = (v << 1) | ((src[bitPos >> 3] >> (7 - (bitPos & 7))) & 1);
                bitPos++;
            }
            return v;
        }
        while (Next() is int code)
        {
            if (code == 257) break;
            if (code == 256) { Reset(); codeLen = 9; prev = null; continue; }
            byte[] entry;
            if (code < table.Count)
            {
                entry = table[code];
                if (prev is not null) table.Add(prev.Append(entry[0]).ToArray());
            }
            else if (prev is not null && code == table.Count)
            {
                entry = prev.Append(prev[0]).ToArray();
                table.Add(entry);
            }
            else throw DemReadException.Corrupt("LZW code");
            result.AddRange(entry);
            prev = entry;
            // TIFF "early change": the code width grows one code early.
            if (table.Count + 1 >= (1 << codeLen) && codeLen < 12) codeLen++;
            if (result.Count >= expected) break;
        }
        return result.ToArray();
    }

    /// <summary>Predictor 2: horizontal differencing per sample.</summary>
    internal static void UndoHorizontal(byte[] b, int width, int rows, int bps, bool little)
    {
        for (int r = 0; r < rows; r++)
        {
            ulong prev = 0;
            for (int c = 0; c < width; c++)
            {
                int o = (r * width + c) * bps;
                if (o + bps > b.Length) return;
                ulong v = 0;
                for (int k = 0; k < bps; k++) v |= (ulong)b[o + (little ? k : bps - 1 - k)] << (8 * k);
                ulong mask = bps == 8 ? ulong.MaxValue : (1UL << (8 * bps)) - 1;
                v = (v + prev) & mask;
                prev = v;
                for (int k = 0; k < bps; k++) b[o + (little ? k : bps - 1 - k)] = (byte)((v >> (8 * k)) & 0xFF);
            }
        }
    }

    /// <summary>Predictor 3 (floating point): per-byte differencing, then each row's bytes are regrouped by byte plane (MSB first).</summary>
    internal static void UndoFloatingPoint(byte[] b, int width, int rows, int bps)
    {
        int rowBytes = width * bps;
        for (int r = 0; r < rows; r++)
        {
            int start = r * rowBytes;
            if (start + rowBytes > b.Length) return;
            for (int i = 1; i < rowBytes; i++) b[start + i] = (byte)(b[start + i] + b[start + i - 1]);
            var row = b.AsSpan(start, rowBytes).ToArray();
            for (int c = 0; c < width; c++)
                for (int k = 0; k < bps; k++) b[start + c * bps + k] = row[k * width + c];
        }
    }
}

/// <summary>Terrain Tiles (AWS, Terrarium encoding): tile maths and the elevation formula (port of <c>TerrainTiles</c>).</summary>
public static class TerrainTiles
{
    /// <summary>Terrarium elevation: (R × 256 + G + B / 256) − 32768 metres.</summary>
    public static float Elevation(byte r, byte g, byte b) => r * 256f + g + b / 256f - 32768;

    public static Uri Url(int z, int x, int y) => new($"https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png");

    /// <summary>Web Mercator tile position (fractional) for lat/lon at zoom z.</summary>
    public static (double X, double Y) TileXY(LatLon c, int z)
    {
        double n = Math.Pow(2, z);
        double lat = c.Lat * Math.PI / 180;
        return ((c.Lon + 180) / 360 * n, (1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2 * n);
    }

    /// <summary>Tiles covering a UTM box (in the project zone).</summary>
    public static List<(int X, int Y)> Tiles(Rect2D extent, UtmZone zone, int z)
    {
        var corners = new[] { new Point2D(extent.MinX, extent.MinY), new Point2D(extent.MaxX, extent.MaxY), new Point2D(extent.MinX, extent.MaxY), new Point2D(extent.MaxX, extent.MinY) };
        var t = corners.Select(p => TileXY(zone.Unproject(p), z)).ToList();
        int x0 = (int)Math.Floor(t.Min(v => v.X)), x1 = (int)Math.Floor(t.Max(v => v.X));
        int y0 = (int)Math.Floor(t.Min(v => v.Y)), y1 = (int)Math.Floor(t.Max(v => v.Y));
        var result = new List<(int, int)>();
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) result.Add((x, y));
        return result;
    }

    /// <summary>Tile resolution at a given latitude (m/pixel; 256 px tiles).</summary>
    public static double ResolutionM(int z, double lat) => 40_075_016.686 * Math.Cos(lat * Math.PI / 180) / (256 * Math.Pow(2, z));
}

/// <summary>Mosaic of decoded Terrarium tiles (elevation per pixel), to be resampled to the UTM grid.</summary>
public sealed class TerrainMosaic
{
    public int Z { get; }
    /// <summary>"x/y" → 256×256 elevations.</summary>
    public Dictionary<string, float[]> Tiles { get; }

    public TerrainMosaic(int z, Dictionary<string, float[]>? tiles = null)
    {
        Z = z;
        Tiles = tiles ?? new Dictionary<string, float[]>();
    }

    public static string Key(int x, int y) => $"{x}/{y}";

    public float Sample(LatLon c)
    {
        var t = TerrainTiles.TileXY(c, Z);
        double px = t.X * 256 - 0.5, py = t.Y * 256 - 0.5;
        int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
        float V(int x, int y) => Tiles.TryGetValue(Key(x >> 8, y >> 8), out var tile) ? tile[(y & 255) * 256 + (x & 255)] : float.NaN;
        float tx = (float)(px - x0), ty = (float)(py - y0);
        return Bilinear.Sample(V(x0, y0), V(x0 + 1, y0), V(x0, y0 + 1), V(x0 + 1, y0 + 1), tx, ty);
    }

    /// <summary>Decodes a Terrarium PNG tile (256×256 RGB) into elevations; null when the PNG cannot be decoded.</summary>
    public static float[]? DecodeTile(byte[] png)
    {
        var rgba = Courses.PngDecoder.Decode(png);
        if (rgba is null || rgba.Length != 256 * 256 * 4) return null;
        var values = new float[256 * 256];
        for (int i = 0; i < values.Length; i++) values[i] = TerrainTiles.Elevation(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
        return values;
    }
}

/// <summary>GeoJSON contour lines (port of <c>ContourGeoJSON</c>).</summary>
public static class ContourGeoJson
{
    private static readonly string[] ElevationKeys = { "elev", "ELEV", "elevation", "ELEVATION", "Elevation", "contour", "CONTOUR", "height", "HEIGHT", "z", "Z" };

    /// <summary>
    /// LineString/MultiLineString with an elevation property. Coordinates are lon/lat (RFC 7946); if the values are clearly
    /// metres (&gt; 180), they are taken as UTM in the project zone.
    /// </summary>
    public static List<(double Elevation, List<Point2D> Points)> Read(byte[] data, UtmZone zone)
    {
        JsonObject root;
        try { root = JsonNode.Parse(data) as JsonObject ?? throw DemReadException.Corrupt("GeoJSON"); }
        catch (System.Text.Json.JsonException) { throw DemReadException.Corrupt("GeoJSON"); }
        var features = root["features"] as JsonArray ?? (root["type"]?.GetValue<string>() == "Feature" ? new JsonArray(root.DeepClone()) : new JsonArray());
        var result = new List<(double, List<Point2D>)>();
        foreach (var f in features.OfType<JsonObject>())
        {
            var props = f["properties"] as JsonObject;
            double? elev = null;
            foreach (var key in ElevationKeys)
            {
                if (props?[key] is not JsonValue v) continue;
                if (v.TryGetValue<double>(out var d)) { elev = d; break; }
                if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ds)) { elev = ds; break; }
            }
            if (elev is null || f["geometry"] is not JsonObject geom || geom["type"]?.GetValue<string>() is not { } type) continue;
            IEnumerable<JsonArray> lines = type switch
            {
                "LineString" => new[] { geom["coordinates"] as JsonArray ?? new JsonArray() },
                "MultiLineString" => (geom["coordinates"] as JsonArray)?.OfType<JsonArray>() ?? Enumerable.Empty<JsonArray>(),
                _ => Enumerable.Empty<JsonArray>(),
            };
            foreach (var line in lines)
            {
                if (line.Count < 2) continue;
                var pts = new List<Point2D>();
                foreach (var c in line.OfType<JsonArray>())
                {
                    if (c.Count < 2) continue;
                    double x = c[0]!.GetValue<double>(), y = c[1]!.GetValue<double>();
                    pts.Add(Math.Abs(x) > 180 ? new Point2D(x, y) : zone.Project(new LatLon(y, x)));
                }
                result.Add((elev.Value, pts));
            }
        }
        if (result.Count == 0) throw DemReadException.Corrupt("no LineString with an elevation property (elev/elevation/contour)");
        return result;
    }
}

public static class UtmZoneWkt
{
    private static readonly Regex Epsg = new(@"326\d\d|327\d\d", RegexOptions.Compiled);
    private static readonly Regex ZoneName = new(@"UTM[ _]ZONE[ _](\d{1,2})([NS])", RegexOptions.Compiled);

    /// <summary>Detect the WGS84 UTM zone from .prj WKT (name "UTM_Zone_48S", "UTM zone 48S", or AUTHORITY EPSG 327xx).</summary>
    public static UtmZone? Parse(string wkt)
    {
        var s = wkt.ToUpperInvariant();
        if (Epsg.Match(s) is { Success: true } m && int.TryParse(m.Value, out var code) && UtmZone.FromEpsg(code) is { } z) return z;
        var n = ZoneName.Match(s);
        if (!n.Success) return null;
        return int.TryParse(n.Groups[1].Value, out var num) ? UtmZone.Create(num, n.Groups[2].Value == "N") : null;
    }
}

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Occa.Core.Contour;
using Occa.Core.Courses;
using Occa.Core.Geometry;
using Occa.Core.Sheet;
using Occa.Core.Vector;
using static Occa.Core.Tests.E;

namespace Occa.Core.Tests;

/// <summary>Port of CakraCore's ContourTests: marching squares, classification, short-line removal, package round trip.</summary>
public class ContourTests
{
    private static readonly SheetSpec Sheet = new() { ScaleDenominator = 4000 };

    private static DemGrid Grid(Func<Point2D, float> f, int cols = 60, int rows = 60, double cell = 5)
        => DemGrid.Resample(new Rect2D(0, 0, cols * cell, rows * cell), cell, "uji", cell, f);

    [Fact]
    public void PlaneGivesStraightLinesAtExpectedX()
    {
        // z = x / 10 → 5 m contours at x = 50, 100, ...
        var g = Grid(p => (float)(p.X / 10));
        var s = new ContourSettings { IsEnabled = true, IntervalM = 5, Smoothing = 0, MinLengthMM = 0, IndexEvery = 5 };
        var result = ContourGenerator.Generate(g, s, Sheet);
        var xs = result.Lines.Select(l => Math.Round(l.Points.Average(p => p.X))).ToHashSet();
        Assert.Equal(new HashSet<double> { 50, 100, 150, 200, 250 }, xs);
        foreach (var line in result.Lines)
            foreach (var p in line.Points) Assert.Equal(line.Elevation * 10, p.X, 6);
        Assert.Equal(ContourLine.Kind.Index, result.Lines.First(l => l.Elevation == 25).LineKind);
        Assert.Equal(ContourLine.Kind.Contour, result.Lines.First(l => l.Elevation == 10).LineKind);
    }

    [Fact]
    public void ConeGivesClosedCirclesOfCorrectRadius()
    {
        var c = P(150, 150);
        var g = Grid(p => (float)(100 - p.DistanceTo(c) / 2));   // r = (100 − z) × 2
        var s = new ContourSettings { IsEnabled = true, IntervalM = 10, Smoothing = 0, MinLengthMM = 0 };
        var result = ContourGenerator.Generate(g, s, Sheet);
        var ring = result.Lines.FirstOrDefault(l => l.Elevation == 50);
        Assert.NotNull(ring);
        Assert.True(ring!.IsClosed);
        foreach (var p in ring.Points) Assert.Equal(100, p.DistanceTo(c), 0);
        Assert.InRange(result.MaxElevation, 98, 102);
    }

    [Fact]
    public void FormLinesAndBaseOffset()
    {
        var s = new ContourSettings { IntervalM = 5, IndexEvery = 5, BaseOffsetM = 2.5 };
        Assert.Equal(ContourLine.Kind.Index, ContourLine.KindOf(27.5, s));
        Assert.Equal(ContourLine.Kind.Contour, ContourLine.KindOf(7.5, s));
        s.ShowFormLines = true;
        Assert.Equal(ContourLine.Kind.Form, ContourLine.KindOf(5, s));
        Assert.Equal(2, ContourSettings.DefaultInterval(4000));
        Assert.Equal(5, ContourSettings.DefaultInterval(10000));
        Assert.NotNull(new ContourSettings { IntervalM = 2 }.IntervalWarning(30));
        Assert.Null(new ContourSettings { IntervalM = 10 }.IntervalWarning(30));
        Assert.Equal((0.21, 0.375), ContourSettings.StandardWidths(10000));
        Assert.Equal(new[] { 5.0, 2.5 }, ContourSettings.IntervalPresets(MapStandard.Isom));
    }

    [Fact]
    public void ShortLinesRemovedAndFeaturesCarryIsomCodes()
    {
        var c = P(150, 150);
        var g = Grid(p => (float)(100 - p.DistanceTo(c) / 2));
        // 1:4000 → 30 mm paper = 120 m: small circles (perimeter < 120 m) are dropped.
        var s = new ContourSettings { IntervalM = 10, Smoothing = 0, MinLengthMM = 30 };
        var lines = ContourGenerator.Generate(g, s, Sheet).Lines;
        Assert.DoesNotContain(lines, l => l.Length < 120);
        var f = lines.First(l => l.IsClosed).ToFeature(s);
        Assert.Equal(FeatureKind.Line, f.Kind);
        Assert.Contains(f.SymbolCode ?? "", new[] { "101.0", "102.0" });
        Assert.Equal(f.Nodes[0].Point, f.Nodes[^1].Point);
    }

    [Fact]
    public void PackageRoundTrip()
    {
        var g = Grid(p => (float)(p.X + p.Y), cols: 4, rows: 3);
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            g.Write(dir);
            Assert.Equal(g, DemGrid.Read(dir));
            var lines = new List<ContourLine> { new(125, ContourLine.Kind.Index, new List<Point2D> { P(1, 2), P(3, 4) }, false) };
            ContourLine.WriteImported(lines, dir);
            var back = ContourLine.ReadImported(dir)!;
            Assert.Single(back);
            Assert.Equal(125, back[0].Elevation);
            Assert.Equal(ContourLine.Kind.Index, back[0].LineKind);
            ContourLine.RemoveImported(dir);
            Assert.Null(ContourLine.ReadImported(dir));
            DemGrid.Remove(dir);
            Assert.Null(DemGrid.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ContourSettingsInProjectJsonAreTyped()
    {
        var p = new Core.Project.Project { Name = "Uji", ContourSettings = new ContourSettings { IsEnabled = true, IntervalM = 2.5 } };
        var json = Core.Json.OccaJson.SerializeSorted(p);
        Assert.Contains("\"contourSettings\":{\"baseOffsetM\":0,\"colorHex\":\"#D15C00\",\"contourWidthMM\":0.14,\"includeInExport\":true,\"indexEvery\":5,\"indexWidthMM\":0.25,\"intervalM\":2.5,\"isEnabled\":true,\"minLengthMM\":1.5,\"opacity\":1,\"showFormLines\":false,\"smoothing\":1}", json);
        var back = Core.Json.OccaJson.Deserialize<Core.Project.Project>(json);
        Assert.Equal(2.5, back.Contours.IntervalM);
        Assert.Equal(2, new Core.Project.Project().Contours.IntervalM);   // default sheet 1:4000 → ISSprOM 2 m
    }
}

/// <summary>Port of DEMReaderTests: ESRI ASCII grid, GeoTIFF (deflate, predictors, LZW), Terrarium, GeoJSON contours.</summary>
public class DemReaderTests
{
    private static readonly UtmZone Zone = UtmZone.FromEpsg(32748)!.Value;

    [Fact]
    public void AsciiGrid()
    {
        const string text = "ncols 3\nnrows 2\nxllcorner 680000\nyllcorner 9304000\ncellsize 10\nNODATA_value -9999\n1 2 3\n4 -9999 6\n";
        var r = AsciiGridReader.Read(Encoding.UTF8.GetBytes(text), null, Zone);
        Assert.Equal(ElevationRaster.Crs.Utm(Zone), r.CrsValue);
        Assert.Equal(9_304_020, r.OriginY);
        Assert.Equal(1, r.Values[0]);
        Assert.True(float.IsNaN(r.Values[4]));
        Assert.Equal(1, r.Sample(P(680_005, 9_304_015), Zone));
        // Between cell 1 and an empty cell: still has a value from valid neighbours (not NaN).
        Assert.False(float.IsNaN(r.Sample(P(680_008, 9_304_012), Zone)));
        Assert.Equal(Zone, UtmZone.FromWkt(Zone.PrjWkt));
        Assert.Equal(UtmZone.FromEpsg(32633), UtmZone.FromWkt("PROJCS[\"WGS 84 / UTM zone 33N\",...]"));
    }

    /// <summary>Single-strip float32 TIFF with GeoTIFF tags (UTM 48S).</summary>
    internal static byte[] MakeGeoTiff(int width, int height, float[] values, int compression = 1, int predictor = 1)
    {
        var pixels = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(pixels.AsSpan(i * 4), values[i]);
        if (predictor == 3)
        {
            // Floating-point predictor: arrange bytes per plane (MSB first), then per-byte differences.
            int rowBytes = width * 4;
            for (int r = 0; r < height; r++)
            {
                var row = pixels.AsSpan(r * rowBytes, rowBytes).ToArray();
                var planes = new byte[rowBytes];
                for (int c = 0; c < width; c++) for (int k = 0; k < 4; k++) planes[k * width + c] = row[c * 4 + (3 - k)];
                for (int i = rowBytes - 1; i > 0; i--) planes[i] = (byte)(planes[i] - planes[i - 1]);
                planes.CopyTo(pixels, r * rowBytes);
            }
        }
        if (compression == 8) pixels = CourseImages.Zlib(pixels);
        if (compression == 5) pixels = LzwEncode(pixels);
        var d = new List<byte> { 0x49, 0x49, 42, 0, 8, 0, 0, 0 };
        byte[] Le16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }
        byte[] Le32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
        byte[] Dbl(params double[] v) => v.SelectMany(x => { var b = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, x); return b; }).ToArray();
        var entries = new List<(int Tag, int Type, int Count, byte[] Value)>
        {
            (256, 4, 1, Le32((uint)width)), (257, 4, 1, Le32((uint)height)), (258, 3, 1, Le16(32).Concat(new byte[2]).ToArray()),
            (259, 3, 1, Le16((ushort)compression).Concat(new byte[2]).ToArray()), (262, 3, 1, Le16(1).Concat(new byte[2]).ToArray()),
            (273, 4, 1, Array.Empty<byte>()), (277, 3, 1, Le16(1).Concat(new byte[2]).ToArray()), (278, 4, 1, Le32((uint)height)),
            (279, 4, 1, Le32((uint)pixels.Length)), (317, 3, 1, Le16((ushort)predictor).Concat(new byte[2]).ToArray()), (339, 3, 1, Le16(3).Concat(new byte[2]).ToArray()),
            (33550, 12, 3, Dbl(10, 10, 0)), (33922, 12, 6, Dbl(0, 0, 0, 680_000, 9_304_100, 0)),
            (34735, 3, 12, new[] { 1, 1, 0, 2, 1024, 0, 1, 1, 3072, 0, 1, 32748 }.SelectMany(v => Le16((ushort)v)).ToArray()),
        };
        int ifdSize = 2 + entries.Count * 12 + 4;
        var extra = new List<byte>();
        int extraBase = 8 + ifdSize;
        var ifd = new List<byte>(Le16((ushort)entries.Count));
        int pixelOffset = extraBase + entries.Where(e => e.Value.Length > 4).Sum(e => e.Value.Length);
        foreach (var (tag, type, count, value) in entries)
        {
            ifd.AddRange(Le16((ushort)tag)); ifd.AddRange(Le16((ushort)type)); ifd.AddRange(Le32((uint)count));
            if (tag == 273) ifd.AddRange(Le32((uint)pixelOffset));
            else if (value.Length > 4) { ifd.AddRange(Le32((uint)(extraBase + extra.Count))); extra.AddRange(value); }
            else { ifd.AddRange(value); ifd.AddRange(new byte[4 - value.Length]); }
        }
        ifd.AddRange(Le32(0));
        d.AddRange(ifd); d.AddRange(extra); d.AddRange(pixels);
        return d.ToArray();
    }

    /// <summary>TIFF LZW encoder (MSB-first, early change) so the decoder is tested against an independent implementation.</summary>
    internal static byte[] LzwEncode(byte[] data)
    {
        var bits = new List<byte>();
        int acc = 0, nAcc = 0;
        void Emit(int code, int len)
        {
            acc = (acc << len) | code; nAcc += len;
            while (nAcc >= 8) { bits.Add((byte)(acc >> (nAcc - 8))); nAcc -= 8; acc &= (1 << nAcc) - 1; }
        }
        var table = new Dictionary<string, int>();
        void Reset() { table.Clear(); for (int i = 0; i < 256; i++) table[((char)i).ToString()] = i; }
        Reset();
        int next = 258, codeLen = 9;
        Emit(256, codeLen);
        var w = "";
        foreach (var b in data)
        {
            var wc = w + (char)b;
            if (table.ContainsKey(wc)) { w = wc; continue; }
            Emit(table[w], codeLen);
            table[wc] = next++;
            if (next + 1 > (1 << codeLen) && codeLen < 12) codeLen++;
            if (next >= 4094) { Emit(256, codeLen); Reset(); next = 258; codeLen = 9; }
            w = ((char)b).ToString();
        }
        if (w.Length > 0) Emit(table[w], codeLen);
        Emit(257, codeLen);
        if (nAcc > 0) bits.Add((byte)(acc << (8 - nAcc)));
        return bits.ToArray();
    }

    [Fact]
    public void GeoTiffUncompressedDeflateAndLzw()
    {
        var values = Enumerable.Range(0, 20).Select(i => i * 1.5f + 100).ToArray();
        foreach (var (comp, pred) in new[] { (1, 1), (8, 1), (8, 3), (5, 1), (5, 3) })
        {
            var r = GeoTiffReader.Read(MakeGeoTiff(5, 4, values, comp, pred), Zone);
            Assert.Equal(values, r.Values);
            Assert.Equal(ElevationRaster.Crs.Utm(Zone), r.CrsValue);
            Assert.Equal(680_000, r.OriginX);
            Assert.Equal(10, r.CellX);
        }
    }

    [Fact]
    public void LzwRoundTripOnNoisyData()
    {
        var pixels = Enumerable.Range(0, 37 * 23 * 2).Select(i => (byte)((i * 977) % 251)).ToArray();
        Assert.Equal(pixels, GeoTiffReader.LzwDecode(LzwEncode(pixels), pixels.Length));
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.Throws<DemReadException>(() => GeoTiffReader.Read(Encoding.UTF8.GetBytes("bukan tiff"), Zone));
        Assert.Throws<DemReadException>(() => AsciiGridReader.Read(Encoding.UTF8.GetBytes("ncols 3\n"), null, Zone));
        var good = MakeGeoTiff(5, 4, Enumerable.Repeat(1f, 20).ToArray());
        for (int n = 0; n < good.Length; n += 13)
        {
            try { GeoTiffReader.Read(good[..n], Zone); } catch (DemReadException) { }
        }
    }

    [Fact]
    public void TerrariumAndTiles()
    {
        Assert.Equal(0, TerrainTiles.Elevation(128, 0, 0));
        Assert.Equal(100.5f, TerrainTiles.Elevation(128, 100, 128));
        var t = TerrainTiles.TileXY(new LatLon(0, 0), 1);
        Assert.Equal(1, t.X, 9);
        Assert.Equal(1, t.Y, 9);
        var extent = new Rect2D(679_000, 9_303_800, 681_500, 9_306_800);
        Assert.NotEmpty(TerrainTiles.Tiles(extent, Zone, 14));
        Assert.Equal(9.55, TerrainTiles.ResolutionM(14, 0), 2);
        Assert.Equal("https://s3.amazonaws.com/elevation-tiles-prod/terrarium/14/13052/8300.png", TerrainTiles.Url(14, 13052, 8300).ToString());
        // Mosaic sampling: a flat tile of 100 m.
        var tile = Enumerable.Repeat(100f, 256 * 256).ToArray();
        var (tx, ty) = TerrainTiles.TileXY(new LatLon(-6.3, 106.8), 14);
        var mosaic = new TerrainMosaic(14, new Dictionary<string, float[]> { [TerrainMosaic.Key((int)tx, (int)ty)] = tile });
        Assert.Equal(100f, mosaic.Sample(new LatLon(-6.3, 106.8)), 3);
        // Terrarium PNG decode: 256×256 pixels encoding 0 m.
        var png = TestPng.Rgba(256, 256, Enumerable.Repeat(new byte[] { 128, 0, 0, 255 }, 256 * 256).SelectMany(x => x).ToArray());
        var decoded = TerrainMosaic.DecodeTile(png)!;
        Assert.Equal(256 * 256, decoded.Length);
        Assert.Equal(0, decoded[1000]);
    }

    [Fact]
    public void GeoJsonContours()
    {
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"elev":125},"geometry":{"type":"LineString","coordinates":[[106.62,-6.28],[106.63,-6.281]]}},
              {"type":"Feature","properties":{"name":"tanpa elevasi"},"geometry":{"type":"LineString","coordinates":[[106.62,-6.28],[106.63,-6.28]]}}
            ]}
            """;
        var lines = ContourGeoJson.Read(Encoding.UTF8.GetBytes(json), Zone);
        Assert.Single(lines);
        Assert.Equal(125, lines[0].Elevation);
        Assert.Equal(Zone.Project(new LatLon(-6.28, 106.62)).X, lines[0].Points[0].X, 6);
    }
}

/// <summary>Port of ContourExportTests: Shapefile structure, KML content, KMZ zip validity.</summary>
public class ContourExportTests
{
    private static readonly UtmZone Zone = UtmZone.FromEpsg(32748)!.Value;

    private static List<ContourLine> Lines => new()
    {
        new(125, ContourLine.Kind.Index, new List<Point2D> { P(680_000, 9_304_000), P(680_100, 9_304_050), P(680_200, 9_304_000) }, false),
        new(127.5, ContourLine.Kind.Form, new List<Point2D> { P(680_010, 9_304_200), P(680_300, 9_304_210) }, false),
    };

    [Fact]
    public void ShapefileStructure()
    {
        var f = Shapefile.Polylines(Lines);
        int Be32(byte[] d, int o) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(o));
        int Le32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
        double Led(byte[] d, int o) => BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(o));
        // Header .shp
        Assert.Equal(9994, Be32(f.Shp, 0));
        Assert.Equal(f.Shp.Length, Be32(f.Shp, 24) * 2);
        Assert.Equal(1000, Le32(f.Shp, 28));
        Assert.Equal(3, Le32(f.Shp, 32));
        Assert.Equal(680_000, Led(f.Shp, 36));
        Assert.Equal(9_304_210, Led(f.Shp, 60));
        // Record 1: number, type, 1 part, 3 points, first point.
        Assert.Equal(1, Be32(f.Shp, 100));
        Assert.Equal(3, Le32(f.Shp, 108));
        Assert.Equal(1, Le32(f.Shp, 108 + 36));
        Assert.Equal(3, Le32(f.Shp, 108 + 40));
        Assert.Equal(680_000, Led(f.Shp, 108 + 48));
        Assert.Equal(9_304_000, Led(f.Shp, 108 + 56));
        // .shx: 2 records, record 2 offset matches .shp.
        Assert.Equal(100 + 2 * 8, f.Shx.Length);
        int off2 = Be32(f.Shx, 108) * 2;
        Assert.Equal(2, Be32(f.Shp, off2));
        // .dbf: 2 records, ELEV field contains 125.00.
        Assert.Equal(0x03, f.Dbf[0]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(f.Dbf.AsSpan(4)));
        int headerLen = f.Dbf[8] | f.Dbf[9] << 8;
        var record = Encoding.UTF8.GetString(f.Dbf, headerLen, 29);
        Assert.Equal("       125.00index     102.0 ", record);
        Assert.Equal(0x1A, f.Dbf[^1]);
    }

    [Fact]
    public void KmlIsWgs84AndWellFormed()
    {
        var kml = Kml.Document(Lines, Zone, new ContourSettings(), "Uji & kontur");
        XDocument.Parse(kml);
        var ll = Zone.Unproject(P(680_000, 9_304_000));
        Assert.Contains($"{ll.Lon.ToString("F8", System.Globalization.CultureInfo.InvariantCulture)},{ll.Lat.ToString("F8", System.Globalization.CultureInfo.InvariantCulture)},125.00", kml);
        Assert.Contains("<color>ff005cd1</color>", kml);
        Assert.Contains("Uji &amp; kontur", kml);
        Assert.Contains("<name>Index contour (1)</name>", kml);
    }

    /// <summary>KMZ checked with .NET's ZipArchive: ZIP structure and CRC32 must be valid for other tools.</summary>
    [Fact]
    public void KmzOpensWithZipArchive()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var paths = ContourExporter.Export(Lines, new HashSet<ContourExportFormat> { ContourExportFormat.Kmz, ContourExportFormat.Shp }, Zone, new ContourSettings(), "kontur", "Uji", dir);
            Assert.Equal(new HashSet<string> { ".kmz", ".shp", ".shx", ".dbf", ".prj", ".cpg" }, paths.Select(p => Path.GetExtension(p)!).ToHashSet());
            var kmz = paths.First(p => p.EndsWith(".kmz"));
            using var zip = ZipFile.OpenRead(kmz);
            var entry = Assert.Single(zip.Entries);
            Assert.Equal("doc.kml", entry.FullName);
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            Assert.Equal(Kml.Document(Lines, Zone, new ContourSettings(), "Uji"), reader.ReadToEnd());
            Assert.Equal(0xCBF4_3926u, ZipWriter.Crc32(Encoding.ASCII.GetBytes("123456789")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Occa.Core.Geometry;
using Occa.Core.Vector;

namespace Occa.Core.Contour;

/// <summary>Export of contour data only: Shapefile (project UTM), KML and KMZ (WGS84, for Google Earth and map apps).</summary>
public enum ContourExportFormat { Shp, Kml, Kmz }

public static class ContourExportFormatExtensions
{
    public static string Label(this ContourExportFormat f) => f switch { ContourExportFormat.Shp => "Shapefile (.shp)", ContourExportFormat.Kml => "KML (.kml)", _ => "KMZ (.kmz)" };
    public static string Extension(this ContourExportFormat f) => f.ToString().ToLowerInvariant();
}

/// <summary>Port of <c>ContourExporter</c>.</summary>
public static class ContourExporter
{
    /// <summary>Write the selected formats to <paramref name="directory"/>; return all files created (Shapefile = 5 files).</summary>
    public static List<string> Export(IReadOnlyList<ContourLine> lines, ISet<ContourExportFormat> formats, UtmZone zone, ContourSettings settings,
                                      string baseName, string title, string directory)
    {
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        var usable = lines.Where(l => l.Points.Count >= 2).ToList();
        if (formats.Contains(ContourExportFormat.Shp))
        {
            var shp = Shapefile.Polylines(usable);
            foreach (var (ext, data) in new[] { ("shp", shp.Shp), ("shx", shp.Shx), ("dbf", shp.Dbf), ("prj", Encoding.UTF8.GetBytes(zone.PrjWkt)), ("cpg", Encoding.UTF8.GetBytes("UTF-8")) })
            {
                var path = Path.Combine(directory, baseName + "." + ext);
                Project.ProjectPackage.WriteAtomic(path, data);
                paths.Add(path);
            }
        }
        var kml = formats.Contains(ContourExportFormat.Kml) || formats.Contains(ContourExportFormat.Kmz) ? Kml.Document(usable, zone, settings, title) : "";
        if (formats.Contains(ContourExportFormat.Kml))
        {
            var path = Path.Combine(directory, baseName + ".kml");
            Project.ProjectPackage.WriteAtomic(path, Encoding.UTF8.GetBytes(kml));
            paths.Add(path);
        }
        if (formats.Contains(ContourExportFormat.Kmz))
        {
            var path = Path.Combine(directory, baseName + ".kmz");
            Project.ProjectPackage.WriteAtomic(path, ZipWriter.Archive(new[] { ("doc.kml", Encoding.UTF8.GetBytes(kml)) }));
            paths.Add(path);
        }
        return paths;
    }
}

/// <summary>ESRI Shapefile (PolyLine) + DBF writer (port of <c>Shapefile</c>).</summary>
public static class Shapefile
{
    public sealed record Files(byte[] Shp, byte[] Shx, byte[] Dbf);

    private const int PolyLineType = 3;

    /// <summary>One PolyLine record per contour line; attributes ELEV (N 12.2), TYPE (C 10), ISOM (C 6).</summary>
    public static Files Polylines(IReadOnlyList<ContourLine> lines)
    {
        var records = new List<byte[]>();
        var all = new List<Point2D>();
        foreach (var line in lines)
        {
            var c = new ByteWriter();
            var box = Rect2D.FromPoints(line.Points) ?? new Rect2D(0, 0, 0, 0);
            c.Le(PolyLineType);
            foreach (var v in new[] { box.MinX, box.MinY, box.MaxX, box.MaxY }) c.Le(v);
            c.Le(1);                    // NumParts
            c.Le(line.Points.Count);    // NumPoints
            c.Le(0);                    // Parts[0]
            foreach (var p in line.Points) { c.Le(p.X); c.Le(p.Y); }
            records.Add(c.ToArray());
            all.AddRange(line.Points);
        }
        var bounds = Rect2D.FromPoints(all) ?? new Rect2D(0, 0, 0, 0);

        var shp = new ByteWriter(Header(100 + records.Sum(r => 8 + r.Length), bounds));
        var shx = new ByteWriter(Header(100 + records.Count * 8, bounds));
        int offset = 100;
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            shp.Be(i + 1);
            shp.Be(r.Length / 2);
            shp.Append(r);
            shx.Be(offset / 2);
            shx.Be(r.Length / 2);
            offset += 8 + r.Length;
        }
        return new Files(shp.ToArray(), shx.ToArray(), Dbf(lines));
    }

    /// <summary>100-byte .shp/.shx header: file code + length (big-endian, 16-bit units), version + type + bbox (little-endian).</summary>
    private static byte[] Header(int fileLengthBytes, Rect2D box)
    {
        var d = new ByteWriter();
        d.Be(9994);
        for (int i = 0; i < 5; i++) d.Be(0);
        d.Be(fileLengthBytes / 2);
        d.Le(1000);
        d.Le(PolyLineType);
        foreach (var v in new[] { box.MinX, box.MinY, box.MaxX, box.MaxY, 0, 0, 0, 0 }) d.Le(v);
        return d.ToArray();
    }

    /// <summary>dBase III: 32-byte header, 32-byte field descriptors, 0x0D, records (flag + fixed-width fields), 0x1A.</summary>
    private static byte[] Dbf(IReadOnlyList<ContourLine> lines)
    {
        var fields = new[] { ("ELEV", 'N', 12, 2), ("TYPE", 'C', 10, 0), ("ISOM", 'C', 6, 0) };
        int recordLength = 1 + fields.Sum(f => f.Item3);
        int headerLength = 32 + 32 * fields.Length + 1;
        var d = new ByteWriter();
        d.Byte(0x03);
        var now = DateTime.Now;
        d.Byte((byte)(now.Year - 1900)); d.Byte((byte)now.Month); d.Byte((byte)now.Day);
        d.Le((uint)lines.Count);
        d.Le((ushort)headerLength);
        d.Le((ushort)recordLength);
        d.Zeros(20);
        foreach (var (name, type, length, decimals) in fields)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name.Length > 10 ? name[..10] : name);
            d.Append(nameBytes);
            d.Zeros(11 - nameBytes.Length);
            d.Byte((byte)type);
            d.Zeros(4);
            d.Byte((byte)length);
            d.Byte((byte)decimals);
            d.Zeros(14);
        }
        d.Byte(0x0D);
        foreach (var line in lines)
        {
            d.Byte((byte)' ');
            var elev = line.Elevation.ToString("0.00", CultureInfo.InvariantCulture);
            var padded = new string(' ', Math.Max(0, 12 - elev.Length)) + elev;
            d.Append(Encoding.UTF8.GetBytes(padded.Length > 12 ? padded[..12] : padded));
            foreach (var (value, length) in new[] { (line.LineKind.ToString().ToLowerInvariant(), 10), (line.SymbolCode, 6) })
            {
                var v = value.Length > length ? value[..length] : value;
                d.Append(Encoding.UTF8.GetBytes(v + new string(' ', length - v.Length)));
            }
        }
        d.Byte(0x1A);
        return d.ToArray();
    }
}

/// <summary>KML 2.2 writer (port of <c>KML</c>).</summary>
public static class Kml
{
    /// <summary>One folder per line kind, one style per kind (project contour colour), elevation in the name and ExtendedData.</summary>
    public static string Document(IReadOnlyList<ContourLine> lines, UtmZone zone, ContourSettings s, string title)
    {
        // KML: colour is aabbggrr; width in screen pixels.
        var rgb = ColorHex.Rgb(s.ColorHex) ?? (0.82, 0.36, 0);
        int alpha = (int)Math.Round(s.Opacity * 255, MidpointRounding.AwayFromZero);
        var color = $"{alpha:x2}{(int)(rgb.B * 255):x2}{(int)(rgb.G * 255):x2}{(int)(rgb.R * 255):x2}";
        string Num(double v, int d) => v.ToString("F" + d, CultureInfo.InvariantCulture);
        var x = new StringBuilder();
        x.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<kml xmlns=\"http://www.opengis.net/kml/2.2\">\n<Document>\n");
        x.Append($"<name>{Escape(title)}</name>\n");
        x.Append($"<description>Contour interval {Num(s.IntervalM, 2)} m, index every {s.IndexEvery} lines. Made with {AppInfo.Name}.</description>\n");
        x.Append($"<Style id=\"contour\"><LineStyle><color>{color}</color><width>1.5</width></LineStyle></Style>\n");
        x.Append($"<Style id=\"index\"><LineStyle><color>{color}</color><width>3</width></LineStyle></Style>\n");
        x.Append($"<Style id=\"form\"><LineStyle><color>{color}</color><width>1</width></LineStyle></Style>\n\n");
        foreach (var kind in new[] { ContourLine.Kind.Index, ContourLine.Kind.Contour, ContourLine.Kind.Form })
        {
            var group = lines.Where(l => l.LineKind == kind).ToList();
            if (group.Count == 0) continue;
            x.Append($"<Folder><name>{group[0].SymbolName} ({group.Count})</name>\n");
            foreach (var line in group)
            {
                var coords = string.Join(" ", line.Points.Select(p =>
                {
                    var ll = zone.Unproject(p);
                    return $"{Num(ll.Lon, 8)},{Num(ll.Lat, 8)},{Num(line.Elevation, 2)}";
                }));
                var kindName = kind.ToString().ToLowerInvariant();
                x.Append($"<Placemark><name>{Num(line.Elevation, line.Elevation % 1 == 0 ? 0 : 2)} m</name><styleUrl>#{kindName}</styleUrl>");
                x.Append($"<ExtendedData><Data name=\"elev\"><value>{Num(line.Elevation, 2)}</value></Data><Data name=\"isom\"><value>{line.SymbolCode}</value></Data></ExtendedData>");
                x.Append($"<LineString><tessellate>1</tessellate><altitudeMode>clampToGround</altitudeMode><coordinates>{coords}</coordinates></LineString></Placemark>\n");
            }
            x.Append("</Folder>\n");
        }
        x.Append("</Document>\n</kml>\n");
        return x.ToString();
    }

    internal static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>Minimal ZIP archive (Store method, no compression): local header + data + central directory + end record.</summary>
public static class ZipWriter
{
    public static byte[] Archive(IEnumerable<(string Name, byte[] Data)> entries)
    {
        var list = entries.ToList();
        var result = new ByteWriter();
        var central = new ByteWriter();
        foreach (var (name, data) in list)
        {
            uint crc = Crc32(data);
            var nameData = Encoding.UTF8.GetBytes(name);
            uint offset = (uint)result.Length;
            result.Le(0x0403_4B50u);
            result.Le((ushort)20); result.Le((ushort)0); result.Le((ushort)0);   // version, flags, store method
            result.Le((ushort)0); result.Le((ushort)0x21);                        // time, date (1980-01-01)
            result.Le(crc); result.Le((uint)data.Length); result.Le((uint)data.Length);
            result.Le((ushort)nameData.Length); result.Le((ushort)0);
            result.Append(nameData);
            result.Append(data);

            central.Le(0x0201_4B50u);
            central.Le((ushort)20); central.Le((ushort)20); central.Le((ushort)0); central.Le((ushort)0);
            central.Le((ushort)0); central.Le((ushort)0x21);
            central.Le(crc); central.Le((uint)data.Length); central.Le((uint)data.Length);
            central.Le((ushort)nameData.Length); central.Le((ushort)0); central.Le((ushort)0);
            central.Le((ushort)0); central.Le((ushort)0); central.Le(0u);
            central.Le(offset);
            central.Append(nameData);
        }
        uint centralOffset = (uint)result.Length;
        var centralBytes = central.ToArray();
        result.Append(centralBytes);
        result.Le(0x0605_4B50u);
        result.Le((ushort)0); result.Le((ushort)0);
        result.Le((ushort)list.Count); result.Le((ushort)list.Count);
        result.Le((uint)centralBytes.Length); result.Le(centralOffset);
        result.Le((ushort)0);
        return result.ToArray();
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32(byte[] data)
    {
        uint c = 0xFFFF_FFFF;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFF_FFFF;
    }
}

/// <summary>Little/big-endian byte buffer for the binary writers.</summary>
internal sealed class ByteWriter
{
    private readonly MemoryStream _ms = new();

    public ByteWriter() { }
    public ByteWriter(byte[] initial) => _ms.Write(initial, 0, initial.Length);

    public int Length => (int)_ms.Length;
    public void Byte(byte b) => _ms.WriteByte(b);
    public void Zeros(int n) { for (int i = 0; i < n; i++) _ms.WriteByte(0); }
    public void Append(byte[] data) => _ms.Write(data, 0, data.Length);

    public void Le(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); _ms.Write(b); }
    public void Le(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _ms.Write(b); }
    public void Le(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _ms.Write(b); }
    public void Le(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, v); _ms.Write(b); }
    public void Be(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); _ms.Write(b); }

    public byte[] ToArray() => _ms.ToArray();
}

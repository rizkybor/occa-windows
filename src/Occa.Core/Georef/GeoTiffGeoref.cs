using System.Buffers.Binary;
using System.Text;
using Occa.Core.Geometry;
using Occa.Core.Project;

namespace Occa.Core.Georef;

/// <summary>
/// GeoTIFF georeferencing from tags (without decoding pixels): ModelPixelScale (33550), ModelTiepoint (33922),
/// GeoKeyDirectory (34735) → UTM WGS84 or WGS84 lat/lon. Port of <c>GeoTIFFGeoref</c> in CakraCore.
/// </summary>
public static class GeoTiffGeoref
{
    public enum Error { NotTiff, NotGeoreferenced, UnsupportedCrs, Rotated }

    public sealed class GeorefException : Exception
    {
        public Error Error { get; }
        public int? Epsg { get; }

        public GeorefException(Error error, int? epsg = null) : base(Describe(error, epsg))
        {
            Error = error;
            Epsg = epsg;
        }

        private static string Describe(Error e, int? epsg) => e switch
        {
            Error.NotTiff => "Not a TIFF file.",
            Error.NotGeoreferenced => "This TIFF is not georeferenced (no GeoTIFF pixel scale/tiepoint tags).",
            Error.UnsupportedCrs => $"The {(epsg is int v ? $"EPSG:{v}" : "image")} coordinate system is not supported yet. Re-save it in UTM WGS84 or WGS84 lat/lon.",
            _ => "Rotated GeoTIFFs (ModelTransformation) are not supported yet.",
        };
    }

    public sealed record Info(int Width, int Height, BasemapImage.Crs Crs, double OriginX, double OriginY, double PixelSizeX, double PixelSizeY, string? DateTime);

    public static Info Read(byte[] d)
    {
        if (d.Length < 8) throw new GeorefException(Error.NotTiff);
        bool little = d[0] == 0x49 && d[1] == 0x49;
        if (!little && !(d[0] == 0x4D && d[1] == 0x4D)) throw new GeorefException(Error.NotTiff);
        int U16(int o) => o >= 0 && o + 2 <= d.Length ? (little ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o))) : throw new GeorefException(Error.NotTiff);
        int U32(int o) => o >= 0 && o + 4 <= d.Length ? (int)(little ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o))) : throw new GeorefException(Error.NotTiff);
        double F64(int o) => o >= 0 && o + 8 <= d.Length ? (little ? BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(o)) : BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(o))) : throw new GeorefException(Error.NotTiff);

        if (U16(2) != 42) throw new GeorefException(Error.NotTiff);
        int ifd = U32(4);
        int count = U16(ifd);
        if (count >= 1000) throw new GeorefException(Error.NotTiff);
        var tags = new Dictionary<int, (int Type, int Count, int ValueOffset)>();
        for (int i = 0; i < count; i++)
        {
            int e = ifd + 2 + i * 12;
            tags[U16(e)] = (U16(e + 2), U32(e + 4), e + 8);
        }
        var size = new Dictionary<int, int> { [3] = 2, [4] = 4, [12] = 8 };
        List<double> Numbers(int tag)
        {
            if (!tags.TryGetValue(tag, out var t) || !size.TryGetValue(t.Type, out var s) || t.Count >= 10_000) return new List<double>();
            int b = s * t.Count <= 4 ? t.ValueOffset : U32(t.ValueOffset);
            var list = new List<double>(t.Count);
            for (int i = 0; i < t.Count; i++)
            {
                int o = b + i * s;
                list.Add(t.Type switch { 3 => U16(o), 4 => U32(o), _ => F64(o) });
            }
            return list;
        }
        var wl = Numbers(256);
        var hl = Numbers(257);
        if (wl.Count == 0 || hl.Count == 0 || wl[0] <= 0 || hl[0] <= 0) throw new GeorefException(Error.NotTiff);
        int width = (int)wl[0], height = (int)hl[0];
        if (tags.ContainsKey(34264) && !tags.ContainsKey(33550)) throw new GeorefException(Error.Rotated);
        var scale = Numbers(33550);
        var tie = Numbers(33922);
        if (scale.Count < 2 || tie.Count < 6 || !(scale[0] > 0) || !(scale[1] > 0)) throw new GeorefException(Error.NotGeoreferenced);

        int? projected = null, geographic = null, model = null;
        var keys = Numbers(34735).Select(v => (int)v).ToList();
        if (keys.Count >= 4)
        {
            for (int k = 0; k < keys[3]; k++)
            {
                int e = 4 + k * 4;
                if (e + 3 >= keys.Count || keys[e + 1] != 0) continue;
                switch (keys[e])
                {
                    case 1024: model = keys[e + 3]; break;
                    case 2048: geographic = keys[e + 3]; break;
                    case 3072: projected = keys[e + 3]; break;
                }
            }
        }
        BasemapImage.Crs crs;
        if (projected is int p)
        {
            if (UtmZone.FromEpsg(p) is null) throw new GeorefException(Error.UnsupportedCrs, p);
            crs = BasemapImage.Crs.Utm(p);
        }
        else if (model == 2 || geographic == 4326 || geographic == 4979) crs = BasemapImage.Crs.Geographic;
        else throw new GeorefException(Error.UnsupportedCrs, geographic);

        double originX = tie[3] - tie[0] * scale[0];
        double originY = tie[4] + tie[1] * scale[1];
        string? dateTime = null;
        if (tags.TryGetValue(306, out var dt) && dt.Type == 2 && dt.Count > 1 && dt.Count < 64)
        {
            int b = dt.Count <= 4 ? dt.ValueOffset : U32(dt.ValueOffset);
            if (b >= 0 && b + dt.Count <= d.Length)
            {
                var bytes = d.AsSpan(b, dt.Count);
                int end = bytes.IndexOf((byte)0);
                dateTime = Encoding.UTF8.GetString(end < 0 ? bytes : bytes.Slice(0, end)).Trim();
                if (dateTime.Length == 0) dateTime = null;
            }
        }
        return new Info(width, height, crs, originX, originY, scale[0], scale[1], dateTime);
    }
}

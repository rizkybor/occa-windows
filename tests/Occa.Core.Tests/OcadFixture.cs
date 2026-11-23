using System.Buffers.Binary;
using System.Text;

namespace Occa.Core.Tests;

/// <summary>Minimal OCAD 12 file builder (port of OcadReaderTests.makeOcad): 1 scale string, 1 colour, line/point/double-line symbols, objects.</summary>
internal static class OcadFixture
{
    public static byte[] Make(string scalePar = "\tm10000.000000\tg150\tr1\tx680000\ty9304000\ta0.00000000\ti1000",
                              List<(int Sym, byte Kind, List<(int X, int Y, byte XF, byte YF)> Coords)>? objects = null)
    {
        var d = new List<byte>(new byte[64]);
        void Ensure(int n) { while (d.Count < n) d.Add(0); }
        void Put16(ushort v, int o) { Ensure(o + 2); BinaryPrimitives.WriteUInt16LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(d).Slice(o), v); }
        void PutI16(short v, int o) { Ensure(o + 2); BinaryPrimitives.WriteInt16LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(d).Slice(o), v); }
        void Put32(uint v, int o) { Ensure(o + 4); BinaryPrimitives.WriteUInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(d).Slice(o), v); }
        void PutI32(int v, int o) { Ensure(o + 4); BinaryPrimitives.WriteInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(d).Slice(o), v); }
        void Put8(byte v, int o) { Ensure(o + 1); d[o] = v; }
        int Append(byte[] bytes) { int o = d.Count; d.AddRange(bytes); return o; }

        Put16(0x0CAD, 0);
        PutI16(12, 4);
        int strBlock = Append(new byte[4 + 256 * 16]);
        var scaleBytes = Encoding.UTF8.GetBytes(scalePar).Concat(new byte[] { 0 }).ToArray();
        int scale = Append(scaleBytes);
        var colorBytes = Encoding.UTF8.GetBytes("Brown\tn5\tc0\tm56\ty100\tk18").Concat(new byte[] { 0 }).ToArray();
        int color = Append(colorBytes);
        var strings = new[] { (scale, scaleBytes.Length, 1039), (color, 26, 9) };
        for (int i = 0; i < strings.Length; i++)
        {
            Put32((uint)strings[i].Item1, strBlock + 4 + i * 16);
            Put32((uint)strings[i].Item2, strBlock + 8 + i * 16);
            PutI32(strings[i].Item3, strBlock + 12 + i * 16);
        }
        Put32((uint)strBlock, 32);

        int symBlock = Append(new byte[4 + 256 * 4]);
        int sym = Append(new byte[872]);
        PutI32(872, sym); PutI32(101_000, sym + 4); Put8(2, sym + 8); PutI16(5, sym + 28);
        var contour = "Contour"; for (int i = 0; i < contour.Length; i++) Put16(contour[i], sym + 56 + i * 2);
        PutI16(5, sym + 796); PutI16(21, sym + 798);
        Put32((uint)sym, symBlock + 4);
        int point = Append(new byte[824]);
        PutI32(824, point); PutI32(204_000, point + 4); Put8(1, point + 8); PutI16(5, point + 28);
        var boulder = "Boulder"; for (int i = 0; i < boulder.Length; i++) Put16(boulder[i], point + 56 + i * 2);
        Put16(3, point + 796); PutI16(4, point + 800); PutI16(5, point + 804); PutI16(60, point + 808); PutI16(1, point + 810);
        Put32((uint)point, symBlock + 8);
        int dbl = Append(new byte[872]);
        PutI32(872, dbl); PutI32(505_000, dbl + 4); Put8(2, dbl + 8);
        var footpath = "Footpath"; for (int i = 0; i < footpath.Length; i++) Put16(footpath[i], dbl + 56 + i * 2);
        PutI16(5, dbl + 796); PutI16(0, dbl + 798); PutI16(3, dbl + 822); PutI16(1, dbl + 824); PutI16(5, dbl + 826); PutI16(5, dbl + 828);
        PutI16(35, dbl + 832); PutI16(10, dbl + 834); PutI16(10, dbl + 836);
        Put32((uint)dbl, symBlock + 12);
        Put32((uint)symBlock, 8);

        int objBlock = Append(new byte[4 + 256 * 40]);
        var objs = objects ?? new() { (101_000, 2, new() { (0, 0, 0, 0), (1000, 0, 1, 0), (2000, 1000, 2, 0), (2000, 2000, 0, 0) }) };
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            int pos = Append(new byte[56 + o.Coords.Count * 8]);
            PutI32(o.Sym, pos); Put8(o.Kind, pos + 4); Put32((uint)o.Coords.Count, pos + 44);
            for (int k = 0; k < o.Coords.Count; k++)
            {
                var c = o.Coords[k];
                PutI32(c.X << 8 | c.XF, pos + 56 + k * 8);
                PutI32(c.Y << 8 | c.YF, pos + 60 + k * 8);
            }
            Put32((uint)pos, objBlock + 4 + i * 40 + 16);
            Put8(1, objBlock + 4 + i * 40 + 30);
        }
        Put32((uint)objBlock, 12);
        return d.ToArray();
    }
}

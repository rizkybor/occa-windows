namespace Occa.Core.Compression;

/// <summary>
/// Decoder for an LZVN block (<c>bvxn</c>) inside an LZFSE stream. Apple's encoder uses LZVN for small inputs
/// (under about 4 KB), which covers most <c>.occa</c> files of small projects. Port of <c>lzvn_decode_base.c</c>.
/// </summary>
internal static class LzvnDecoder
{
    private enum Op : byte { SmallD, MediumD, LargeD, PreviousD, SmallL, LargeL, SmallM, LargeM, Nop, EndOfStream, Undefined }

    private static readonly Op[] Table = BuildTable();

    private static Op[] BuildTable()
    {
        var t = new Op[256];
        for (int opc = 0; opc < 256; opc++)
        {
            Op op;
            if (opc >= 0xE0)
                op = opc == 0xE0 ? Op.LargeL : opc < 0xF0 ? Op.SmallL : opc == 0xF0 ? Op.LargeM : Op.SmallM;
            else if (opc >= 0xD8)
                op = Op.Undefined;
            else if (opc >= 0xA0 && opc <= 0xBF)
                op = Op.MediumD;
            else if (opc >= 0x70 && opc <= 0x7F)
                op = Op.Undefined;
            else
            {
                int low = opc & 7;
                if (low == 7) op = Op.LargeD;
                else if (low != 6) op = Op.SmallD;
                else if (opc == 0x06) op = Op.EndOfStream;
                else if (opc == 0x0E || opc == 0x16) op = Op.Nop;
                else if (opc < 0x40) op = Op.Undefined;
                else op = Op.PreviousD;
            }
            t[opc] = op;
        }
        return t;
    }

    public static void DecodeBlock(ReadOnlySpan<byte> src, OutputBuffer output, int expectedRawBytes)
    {
        int startLength = output.Length;
        int pos = 0;
        int d = 0;
        while (true)
        {
            if (pos >= src.Length) throw new InvalidDataException("LZVN block ends without an end-of-stream marker.");
            int opc = src[pos];
            int l, m;
            switch (Table[opc])
            {
                case Op.SmallD:
                    Need(src, pos, 2);
                    l = opc >> 6;
                    m = ((opc >> 3) & 7) + 3;
                    d = ((opc & 7) << 8) | src[pos + 1];
                    pos += 2;
                    break;
                case Op.MediumD:
                    Need(src, pos, 3);
                    l = (opc >> 3) & 3;
                    m = (((opc & 7) << 2) | (src[pos + 1] & 3)) + 3;
                    d = (src[pos + 1] >> 2) | (src[pos + 2] << 6);
                    pos += 3;
                    break;
                case Op.LargeD:
                    Need(src, pos, 3);
                    l = opc >> 6;
                    m = ((opc >> 3) & 7) + 3;
                    d = src[pos + 1] | (src[pos + 2] << 8);
                    pos += 3;
                    break;
                case Op.PreviousD:
                    l = opc >> 6;
                    m = ((opc >> 3) & 7) + 3;
                    pos += 1;
                    break;
                case Op.SmallL:
                    l = opc & 0xF;
                    m = 0;
                    pos += 1;
                    break;
                case Op.LargeL:
                    Need(src, pos, 2);
                    l = src[pos + 1] + 16;
                    m = 0;
                    pos += 2;
                    break;
                case Op.SmallM:
                    l = 0;
                    m = opc & 0xF;
                    pos += 1;
                    break;
                case Op.LargeM:
                    Need(src, pos, 2);
                    l = 0;
                    m = src[pos + 1] + 16;
                    pos += 2;
                    break;
                case Op.Nop:
                    pos += 1;
                    continue;
                case Op.EndOfStream:
                    if (output.Length - startLength != expectedRawBytes)
                        throw new InvalidDataException("LZVN block size does not match its header.");
                    return;
                default:
                    throw new InvalidDataException($"Undefined LZVN opcode 0x{opc:x2}.");
            }
            if (l > 0)
            {
                Need(src, pos, l);
                output.Append(src.Slice(pos, l));
                pos += l;
            }
            if (m > 0) output.CopyMatch(d, m);
            if (output.Length - startLength > expectedRawBytes) throw new InvalidDataException("LZVN block produces more bytes than its header declares.");
        }
    }

    private static void Need(ReadOnlySpan<byte> src, int pos, int n)
    {
        if (src.Length - pos < n) throw new InvalidDataException("LZVN block is truncated.");
    }
}

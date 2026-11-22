using System.Buffers.Binary;

namespace Occa.Core.Compression;

/// <summary>
/// Decoder for Apple's LZFSE stream format, the compression used by <c>NSData.compressed(using: .lzfse)</c> in the Mac/iPad
/// app for <c>.occa</c> and <c>.occafield</c> files. Pure C# port of the decoding side of the open-source reference
/// implementation (https://github.com/lzfse/lzfse, BSD-3). Decompression only: the encoder is a later milestone.
///
/// A stream is a sequence of blocks, each starting with a 4-byte magic: <c>bvx-</c> (uncompressed), <c>bvxn</c> (LZVN),
/// <c>bvx1</c> / <c>bvx2</c> (LZFSE with a full or packed header) and <c>bvx$</c> (end of stream).
/// </summary>
public static class LzfseDecoder
{
    private const uint EndOfStreamMagic = 0x24787662;      // "bvx$"
    private const uint UncompressedMagic = 0x2d787662;     // "bvx-"
    private const uint LzvnMagic = 0x6e787662;             // "bvxn"
    private const uint CompressedV1Magic = 0x31787662;     // "bvx1"
    private const uint CompressedV2Magic = 0x32787662;     // "bvx2"

    private const int LSymbols = 20, MSymbols = 20, DSymbols = 64, LiteralSymbols = 256;
    private const int LStates = 64, MStates = 64, DStates = 256, LiteralStates = 1024;
    private const int MatchesPerBlock = 10_000;
    private const int LiteralsPerBlock = 4 * MatchesPerBlock;
    private const int V1HeaderSize = 770;
    private const int V2HeaderFixedSize = 32;

    /// <summary>Decompresses a complete LZFSE stream. The output size is the sum of the blocks' raw sizes.</summary>
    /// <exception cref="InvalidDataException">The stream is truncated or malformed.</exception>
    /// <param name="maxOutputBytes">Upper bound on the decoded size, so a crafted file cannot exhaust memory.</param>
    public static byte[] Decode(ReadOnlySpan<byte> input, long maxOutputBytes = 4L << 30)
    {
        var output = new OutputBuffer(maxOutputBytes);
        int pos = 0;
        while (true)
        {
            if (input.Length - pos < 4) throw new InvalidDataException("LZFSE stream ends without an end-of-stream block.");
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(pos));
            switch (magic)
            {
                case EndOfStreamMagic:
                    return output.ToArray();
                case UncompressedMagic:
                {
                    if (input.Length - pos < 8) throw Truncated();
                    int n = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(pos + 4)));
                    pos += 8;
                    if (input.Length - pos < n) throw Truncated();
                    output.Append(input.Slice(pos, n));
                    pos += n;
                    break;
                }
                case LzvnMagic:
                {
                    if (input.Length - pos < 12) throw Truncated();
                    int nRaw = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(pos + 4)));
                    int nPayload = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(pos + 8)));
                    pos += 12;
                    if (input.Length - pos < nPayload) throw Truncated();
                    output.Reserve(nRaw);
                    LzvnDecoder.DecodeBlock(input.Slice(pos, nPayload), output, nRaw);
                    pos += nPayload;
                    break;
                }
                case CompressedV1Magic:
                case CompressedV2Magic:
                {
                    var header = magic == CompressedV1Magic ? ReadV1Header(input.Slice(pos)) : ReadV2Header(input.Slice(pos));
                    pos += header.HeaderSize;
                    if (input.Length - pos < header.PayloadBytes) throw Truncated();
                    output.Reserve(header.RawBytes);
                    DecodeCompressedBlock(input.Slice(pos, header.PayloadBytes), header, output);
                    pos += header.PayloadBytes;
                    break;
                }
                default:
                    throw new InvalidDataException($"Unknown LZFSE block magic 0x{magic:x8} at offset {pos}.");
            }
        }
    }

    private static InvalidDataException Truncated() => new("LZFSE stream is truncated.");

    // MARK: Block headers

    private sealed class BlockHeader
    {
        public int HeaderSize;
        public int RawBytes;
        public int PayloadBytes;
        public int Literals;
        public int Matches;
        public int LiteralPayloadBytes;
        public int LmdPayloadBytes;
        public int LiteralBits;
        public ushort[] LiteralState = new ushort[4];
        public int LmdBits;
        public ushort LState, MState, DState;
        /// <summary>L (20), M (20), D (64) and literal (256) symbol frequencies, in that order.</summary>
        public ushort[] Freq = new ushort[LSymbols + MSymbols + DSymbols + LiteralSymbols];
    }

    private static BlockHeader ReadV1Header(ReadOnlySpan<byte> s)
    {
        if (s.Length < V1HeaderSize) throw Truncated();
        var h = new BlockHeader { HeaderSize = V1HeaderSize };
        int o = 4;
        h.RawBytes = ReadI32(s, ref o);
        h.PayloadBytes = ReadI32(s, ref o);
        h.Literals = ReadI32(s, ref o);
        h.Matches = ReadI32(s, ref o);
        h.LiteralPayloadBytes = ReadI32(s, ref o);
        h.LmdPayloadBytes = ReadI32(s, ref o);
        h.LiteralBits = ReadI32(s, ref o);
        for (int i = 0; i < 4; i++) h.LiteralState[i] = ReadU16(s, ref o);
        h.LmdBits = ReadI32(s, ref o);
        h.LState = ReadU16(s, ref o);
        h.MState = ReadU16(s, ref o);
        h.DState = ReadU16(s, ref o);
        for (int i = 0; i < h.Freq.Length; i++) h.Freq[i] = ReadU16(s, ref o);
        Validate(h);
        return h;
    }

    private static BlockHeader ReadV2Header(ReadOnlySpan<byte> s)
    {
        if (s.Length < V2HeaderFixedSize) throw Truncated();
        var h = new BlockHeader { RawBytes = ReadI32At(s, 4) };
        ulong v0 = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(8));
        ulong v1 = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(16));
        ulong v2 = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(24));
        h.Literals = (int)Field(v0, 0, 20);
        h.LiteralPayloadBytes = (int)Field(v0, 20, 20);
        h.Matches = (int)Field(v0, 40, 20);
        h.LiteralBits = (int)Field(v0, 60, 3) - 7;
        for (int i = 0; i < 4; i++) h.LiteralState[i] = (ushort)Field(v1, 10 * i, 10);
        h.LmdPayloadBytes = (int)Field(v1, 40, 20);
        h.LmdBits = (int)Field(v1, 60, 3) - 7;
        h.HeaderSize = (int)Field(v2, 0, 32);
        h.LState = (ushort)Field(v2, 32, 10);
        h.MState = (ushort)Field(v2, 42, 10);
        h.DState = (ushort)Field(v2, 52, 10);
        h.PayloadBytes = h.LiteralPayloadBytes + h.LmdPayloadBytes;
        if (h.HeaderSize < V2HeaderFixedSize || h.HeaderSize > s.Length) throw new InvalidDataException("Bad LZFSE v2 header size.");
        DecodeFreqTables(s.Slice(V2HeaderFixedSize, h.HeaderSize - V2HeaderFixedSize), h.Freq);
        Validate(h);
        return h;
    }

    private static void Validate(BlockHeader h)
    {
        if (h.Literals > LiteralsPerBlock || h.Matches > MatchesPerBlock || h.RawBytes < 0 || h.PayloadBytes < 0
            || h.LiteralPayloadBytes < 0 || h.LmdPayloadBytes < 0
            || h.LiteralPayloadBytes + h.LmdPayloadBytes != h.PayloadBytes
            || h.LiteralBits < -7 || h.LiteralBits > 0 || h.LmdBits < -7 || h.LmdBits > 0)
            throw new InvalidDataException("Bad LZFSE block header.");
        foreach (var st in h.LiteralState) if (st >= LiteralStates) throw new InvalidDataException("Bad LZFSE literal state.");
        if (h.LState >= LStates || h.MState >= MStates || h.DState >= DStates) throw new InvalidDataException("Bad LZFSE LMD state.");
    }

    private static readonly byte[] FreqNBits =
    {
        2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14,
        2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14,
    };

    private static readonly sbyte[] FreqValue =
    {
        0, 2, 1, 4, 0, 3, 1, -1, 0, 2, 1, 5, 0, 3, 1, -1,
        0, 2, 1, 6, 0, 3, 1, -1, 0, 2, 1, 7, 0, 3, 1, -1,
    };

    /// <summary>Variable-length frequency table of a v2 header, LSB-first bit stream.</summary>
    private static void DecodeFreqTables(ReadOnlySpan<byte> src, ushort[] freq)
    {
        int pos = 0;
        uint accum = 0;
        int accumBits = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            while (pos < src.Length && accumBits + 8 <= 32)
            {
                accum |= (uint)src[pos++] << accumBits;
                accumBits += 8;
            }
            int b = (int)(accum & 31);
            int nbits = FreqNBits[b];
            int value = nbits switch
            {
                8 => 8 + (int)((accum >> 4) & 0xf),
                14 => 24 + (int)((accum >> 4) & 0x3ff),
                _ => FreqValue[b],
            };
            if (nbits > accumBits) throw new InvalidDataException("Truncated LZFSE frequency table.");
            accum >>= nbits;
            accumBits -= nbits;
            freq[i] = (ushort)value;
        }
        if (accumBits >= 8 || pos != src.Length) throw new InvalidDataException("Malformed LZFSE frequency table.");
    }

    // MARK: FSE (tANS) decoding

    private struct FseInStream
    {
        public ulong Accum;
        public int AccumBits;
    }

    /// <summary>
    /// Bit reader over a payload read backwards from <paramref name="pos"/> towards <paramref name="start"/>.
    /// Mirrors <c>fse_in_checked_init</c> / <c>fse_in_checked_flush</c> in the reference implementation. A refill may
    /// reach up to 8 bytes before <paramref name="start"/>: the reference reads those bytes from whatever precedes the
    /// payload (header or previous block) and never consumes them in a valid stream; here they read as zero.
    /// </summary>
    private static void FseInit(ref FseInStream s, int n, ReadOnlySpan<byte> buf, ref int pos, int start)
    {
        int lowest = start - OverReadBytes;
        if (n != 0)
        {
            if (pos < lowest + 8) throw Truncated();
            pos -= 8;
            s.Accum = Load8(buf, pos);
            s.AccumBits = n + 64;
        }
        else
        {
            if (pos < lowest + 7) throw Truncated();
            pos -= 7;
            s.Accum = Load8(buf, pos - 1) >> 8;
            s.AccumBits = n + 56;
        }
        if (s.AccumBits < 56 || s.AccumBits >= 64 || (s.Accum >> s.AccumBits) != 0)
            throw new InvalidDataException("Bad LZFSE bit stream start.");
    }

    private const int OverReadBytes = 8;

    private static void FseFlush(ref FseInStream s, ReadOnlySpan<byte> buf, ref int pos, int start)
    {
        int nbits = (63 - s.AccumBits) & -8;
        int next = pos - (nbits >> 3);
        if (next < start - OverReadBytes) throw Truncated();
        pos = next;
        ulong incoming = Load8(buf, next);
        s.Accum = (s.Accum << nbits) | (incoming & MaskLsb64(nbits));
        s.AccumBits += nbits;
        if (s.AccumBits < 56 || s.AccumBits >= 64) throw new InvalidDataException("Bad LZFSE bit stream.");
    }

    private static ulong FsePull(ref FseInStream s, int n)
    {
        s.AccumBits -= n;
        if (s.AccumBits < 0) throw new InvalidDataException("LZFSE bit stream underflow.");
        ulong result = s.Accum >> s.AccumBits;
        s.Accum &= MaskLsb64(s.AccumBits);
        return result;
    }

    private static ulong MaskLsb64(int n) => n >= 64 ? ulong.MaxValue : (1UL << n) - 1;

    /// <summary>Little-endian 8-byte load. Bytes outside the buffer read as zero (the reference reads them but masks them away).</summary>
    private static ulong Load8(ReadOnlySpan<byte> buf, int pos)
    {
        if (pos >= 0 && pos + 8 <= buf.Length) return BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(pos));
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            int p = pos + i;
            if (p >= 0 && p < buf.Length) v |= (ulong)buf[p] << (8 * i);
        }
        return v;
    }

    private struct DecoderEntry
    {
        public byte K;
        public byte Symbol;
        public short Delta;
    }

    private struct ValueDecoderEntry
    {
        public byte TotalBits;
        public byte ValueBits;
        public short Delta;
        public int VBase;
    }

    private static DecoderEntry[] BuildDecoderTable(int nStates, ReadOnlySpan<ushort> freq)
    {
        var t = new DecoderEntry[nStates];
        int idx = 0, nClz = LeadingZeros(nStates), sum = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            int f = freq[i];
            if (f == 0) continue;
            sum += f;
            if (sum > nStates) throw new InvalidDataException("LZFSE frequency table exceeds the state count.");
            int k = LeadingZeros(f) - nClz;
            int j0 = ((2 * nStates) >> k) - f;
            for (int j = 0; j < f; j++)
            {
                DecoderEntry e;
                e.Symbol = (byte)i;
                if (j < j0) { e.K = (byte)k; e.Delta = (short)(((f + j) << k) - nStates); }
                else { e.K = (byte)(k - 1); e.Delta = (short)((j - j0) << (k - 1)); }
                t[idx++] = e;
            }
        }
        return t;
    }

    private static ValueDecoderEntry[] BuildValueDecoderTable(int nStates, ReadOnlySpan<ushort> freq, byte[] vbits, int[] vbase)
    {
        var t = new ValueDecoderEntry[nStates];
        int idx = 0, nClz = LeadingZeros(nStates), sum = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            int f = freq[i];
            if (f == 0) continue;
            sum += f;
            if (sum > nStates) throw new InvalidDataException("LZFSE frequency table exceeds the state count.");
            int k = LeadingZeros(f) - nClz;
            int j0 = ((2 * nStates) >> k) - f;
            for (int j = 0; j < f; j++)
            {
                ValueDecoderEntry e;
                e.ValueBits = vbits[i];
                e.VBase = vbase[i];
                if (j < j0) { e.TotalBits = (byte)(k + e.ValueBits); e.Delta = (short)(((f + j) << k) - nStates); }
                else { e.TotalBits = (byte)(k - 1 + e.ValueBits); e.Delta = (short)((j - j0) << (k - 1)); }
                t[idx++] = e;
            }
        }
        return t;
    }

    private static int LeadingZeros(int v) => System.Numerics.BitOperations.LeadingZeroCount((uint)v);

    private static byte FseDecode(ref int state, DecoderEntry[] table, ref FseInStream s)
    {
        var e = table[state];
        state = e.Delta + (int)FsePull(ref s, e.K);
        return e.Symbol;
    }

    private static int FseValueDecode(ref int state, ValueDecoderEntry[] table, ref FseInStream s)
    {
        var e = table[state];
        uint bits = (uint)FsePull(ref s, e.TotalBits);
        state = e.Delta + (int)(bits >> e.ValueBits);
        return e.VBase + (int)(bits & (e.ValueBits >= 32 ? uint.MaxValue : (1u << e.ValueBits) - 1));
    }

    private static readonly byte[] LExtraBits = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 5, 8 };
    private static readonly int[] LBaseValue = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 28, 60 };
    private static readonly byte[] MExtraBits = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 5, 8, 11 };
    private static readonly int[] MBaseValue = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 24, 56, 312 };
    private static readonly byte[] DExtraBits =
    {
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
        4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7,
        8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11,
        12, 12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 15, 15, 15, 15,
    };
    private static readonly int[] DBaseValue =
    {
        0, 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 28, 36, 44, 52,
        60, 76, 92, 108, 124, 156, 188, 220, 252, 316, 380, 444, 508, 636, 764, 892,
        1020, 1276, 1532, 1788, 2044, 2556, 3068, 3580, 4092, 5116, 6140, 7164, 8188, 10236, 12284, 14332,
        16380, 20476, 24572, 28668, 32764, 40956, 49148, 57340, 65532, 81916, 98300, 114684, 131068, 163836, 196604, 229372,
    };

    // MARK: Compressed block

    private static void DecodeCompressedBlock(ReadOnlySpan<byte> payload, BlockHeader h, OutputBuffer output)
    {
        var freq = h.Freq.AsSpan();
        var lTable = BuildValueDecoderTable(LStates, freq.Slice(0, LSymbols), LExtraBits, LBaseValue);
        var mTable = BuildValueDecoderTable(MStates, freq.Slice(LSymbols, MSymbols), MExtraBits, MBaseValue);
        var dTable = BuildValueDecoderTable(DStates, freq.Slice(LSymbols + MSymbols, DSymbols), DExtraBits, DBaseValue);
        var literalTable = BuildDecoderTable(LiteralStates, freq.Slice(LSymbols + MSymbols + DSymbols, LiteralSymbols));

        // Literals: decoded four at a time, from a stream read backwards from the end of the literal payload.
        var literals = new byte[h.Literals + 4];
        {
            var stream = new FseInStream();
            int pos = h.LiteralPayloadBytes;
            FseInit(ref stream, h.LiteralBits, payload, ref pos, 0);
            int s0 = h.LiteralState[0], s1 = h.LiteralState[1], s2 = h.LiteralState[2], s3 = h.LiteralState[3];
            for (int i = 0; i < h.Literals; i += 4)
            {
                FseFlush(ref stream, payload, ref pos, 0);
                literals[i] = FseDecode(ref s0, literalTable, ref stream);
                literals[i + 1] = FseDecode(ref s1, literalTable, ref stream);
                literals[i + 2] = FseDecode(ref s2, literalTable, ref stream);
                literals[i + 3] = FseDecode(ref s3, literalTable, ref stream);
            }
        }

        // L/M/D triples: literal count, match length, match distance.
        {
            var stream = new FseInStream();
            int start = h.LiteralPayloadBytes;
            int pos = start + h.LmdPayloadBytes;
            FseInit(ref stream, h.LmdBits, payload, ref pos, start);
            int lState = h.LState, mState = h.MState, dState = h.DState;
            int lit = 0, d = 0;
            long produced = 0;
            for (int remaining = h.Matches; remaining > 0; remaining--)
            {
                FseFlush(ref stream, payload, ref pos, start);
                int l = FseValueDecode(ref lState, lTable, ref stream);
                int m = FseValueDecode(ref mState, mTable, ref stream);
                int newD = FseValueDecode(ref dState, dTable, ref stream);
                if (newD != 0) d = newD;
                if (lit + l > h.Literals) throw new InvalidDataException("LZFSE match refers to literals beyond the block.");
                if (produced + l + m > h.RawBytes) throw new InvalidDataException("LZFSE block produces more bytes than its header declares.");
                output.Append(literals.AsSpan(lit, l));
                lit += l;
                if (m > 0) output.CopyMatch(d, m);
                produced += l + m;
            }
            if (produced != h.RawBytes) throw new InvalidDataException("LZFSE block produced fewer bytes than its header declares.");
        }
    }

    // MARK: Helpers

    private static ulong Field(ulong v, int offset, int nbits) => (v >> offset) & ((1UL << nbits) - 1);

    private static int ReadI32(ReadOnlySpan<byte> s, ref int o)
    {
        int v = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(o));
        o += 4;
        return v;
    }

    private static int ReadI32At(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt32LittleEndian(s.Slice(o));

    private static ushort ReadU16(ReadOnlySpan<byte> s, ref int o)
    {
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o));
        o += 2;
        return v;
    }
}

/// <summary>Growable output buffer shared by the LZFSE and LZVN block decoders (matches may copy from any earlier block).</summary>
internal sealed class OutputBuffer
{
    private byte[] _data = new byte[64 * 1024];
    private readonly long _max;

    public OutputBuffer(long max) => _max = max;

    public int Length { get; private set; }

    public void Reserve(int more)
    {
        if (more < 0 || Length + (long)more > _max) throw new InvalidDataException("LZFSE output exceeds the allowed size.");
        long needed = Length + (long)more;
        if (needed > _data.Length)
        {
            long size = Math.Max(needed, Math.Min(_max, (long)_data.Length * 2));
            Array.Resize(ref _data, checked((int)size));
        }
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        Reserve(bytes.Length);
        bytes.CopyTo(_data.AsSpan(Length));
        Length += bytes.Length;
    }

    public void AppendByte(byte b)
    {
        Reserve(1);
        _data[Length++] = b;
    }

    /// <summary>LZ77 copy of <paramref name="length"/> bytes starting <paramref name="distance"/> bytes back; may overlap.</summary>
    public void CopyMatch(int distance, int length)
    {
        if (distance <= 0 || distance > Length) throw new InvalidDataException("LZFSE match distance points before the start of the output.");
        Reserve(length);
        int src = Length - distance;
        if (distance >= length)
        {
            Array.Copy(_data, src, _data, Length, length);
        }
        else
        {
            for (int i = 0; i < length; i++) _data[Length + i] = _data[src + i];
        }
        Length += length;
    }

    public byte[] ToArray() => _data.AsSpan(0, Length).ToArray();
}

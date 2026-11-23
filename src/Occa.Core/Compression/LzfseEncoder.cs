using System.Buffers.Binary;

namespace Occa.Core.Compression;

/// <summary>
/// Writes an LZFSE stream that Apple's decoder (<c>NSData.decompressed(using: .lzfse)</c>) reads, so files saved on
/// Windows open on Mac/iPad. M2 emits uncompressed blocks (<c>bvx-</c>), which the format allows; a real LZFSE/LZVN
/// encoder can replace this later without changing the file format. Project files are small JSON, so the size cost
/// is acceptable for now.
/// </summary>
public static class LzfseEncoder
{
    private const uint EndOfStreamMagic = 0x24787662;      // "bvx$"
    private const uint UncompressedMagic = 0x2d787662;     // "bvx-"
    /// <summary>Largest raw block written (the reference encoder also works in blocks).</summary>
    public const int BlockSize = 1 << 20;

    public static byte[] Encode(ReadOnlySpan<byte> input)
    {
        int blocks = Math.Max(1, (input.Length + BlockSize - 1) / BlockSize);
        var output = new byte[input.Length + blocks * 8 + 4];
        int pos = 0;
        for (int offset = 0; offset < input.Length; offset += BlockSize)
        {
            int n = Math.Min(BlockSize, input.Length - offset);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), UncompressedMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos + 4), (uint)n);
            input.Slice(offset, n).CopyTo(output.AsSpan(pos + 8));
            pos += 8 + n;
        }
        if (input.Length == 0)
        {
            // Apple writes an empty raw block for empty input.
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), UncompressedMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos + 4), 0);
            pos += 8;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), EndOfStreamMagic);
        return output;
    }
}

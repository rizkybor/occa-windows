using Occa.Core.Compression;

namespace Occa.Core.Tests;

/// <summary>
/// Streams in <c>Fixtures/lzfse</c> were written on a Mac with <c>NSData.compressed(using: .lzfse)</c>; each
/// <c>.raw</c> file is the original. They cover every block type Apple's encoder emits: bvx- (empty), bvxn (LZVN,
/// small input) and bvx2 (compressible and incompressible input).
/// </summary>
public class LzfseDecoderTests
{
    [Theory]
    [InlineData("empty", "bvx-")]
    [InlineData("text-small", "bvxn")]
    [InlineData("mixed-30k", "bvx2")]
    [InlineData("random-20k", "bvx2")]
    public void DecodesAppleStreams(string name, string expectedFirstBlock)
    {
        var compressed = Fixtures.Bytes($"lzfse/{name}.lzfse");
        Assert.Equal(expectedFirstBlock, System.Text.Encoding.ASCII.GetString(compressed, 0, 4));

        var decoded = LzfseDecoder.Decode(compressed);

        Assert.Equal(Fixtures.Bytes($"lzfse/{name}.raw"), decoded);
    }

    [Fact]
    public void RejectsTruncatedStream()
    {
        var compressed = Fixtures.Bytes("lzfse/mixed-30k.lzfse");
        Assert.Throws<InvalidDataException>(() => LzfseDecoder.Decode(compressed.AsSpan(0, compressed.Length / 2)));
    }

    [Fact]
    public void RejectsUnknownMagic()
    {
        Assert.Throws<InvalidDataException>(() => LzfseDecoder.Decode("hello world!"u8));
    }

    [Fact]
    public void EnforcesOutputLimit()
    {
        var compressed = Fixtures.Bytes("lzfse/mixed-30k.lzfse");
        Assert.Throws<InvalidDataException>(() => LzfseDecoder.Decode(compressed, maxOutputBytes: 1000));
    }
}

using System.Numerics;
using System.Security.Cryptography;

namespace Occa.Core.License;

/// <summary>
/// Ed25519 (RFC 8032) signature verification in plain C#, since .NET 8 has no built-in Ed25519. Only the public-key
/// side is needed by the app (the back office signs tokens with libsodium); signing is implemented for the tests.
/// BigInteger arithmetic is slow compared with a native library but a token is verified a handful of times per launch.
/// </summary>
public static class Ed25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger I = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point Base = new(Mod(RecoverX(ModY(4 * Inverse(5)), 0)), ModY(4 * Inverse(5)), 1, Mod(RecoverX(ModY(4 * Inverse(5)), 0) * ModY(4 * Inverse(5))));

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static BigInteger Mod(BigInteger a) { var r = a % P; return r.Sign < 0 ? r + P : r; }
    private static BigInteger ModY(BigInteger a) => Mod(a);
    private static BigInteger Inverse(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

    private static BigInteger RecoverX(BigInteger y, int sign)
    {
        var y2 = Mod(y * y);
        var u = Mod(y2 - 1);
        var v = Mod(D * y2 + 1);
        var x2 = Mod(u * Inverse(v));
        // x = (u/v)^((p+3)/8), corrected by sqrt(-1) when needed (RFC 8032 §5.1.3).
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x) != x2) x = Mod(x * I);
        if (Mod(x * x) != x2) return -1;
        if ((int)(x & 1) != sign) x = P - x;
        return x;
    }

    private static Point Add(Point a, Point b)
    {
        // Extended coordinates, "add-2008-hwcd-3" (a = -1).
        var A = Mod((a.Y - a.X) * (b.Y - b.X));
        var B = Mod((a.Y + a.X) * (b.Y + b.X));
        var C = Mod(2 * a.T * b.T * D);
        var Dd = Mod(2 * a.Z * b.Z);
        var E = Mod(B - A);
        var F = Mod(Dd - C);
        var G = Mod(Dd + C);
        var H = Mod(B + A);
        return new Point(Mod(E * F), Mod(G * H), Mod(F * G), Mod(E * H));
    }

    private static Point Multiply(Point p, BigInteger s)
    {
        var result = new Point(0, 1, 1, 0);
        var q = p;
        while (s > 0)
        {
            if (!s.IsEven) result = Add(result, q);
            q = Add(q, q);
            s >>= 1;
        }
        return result;
    }

    private static byte[] Encode(Point p)
    {
        var zi = Inverse(p.Z);
        var x = Mod(p.X * zi); var y = Mod(p.Y * zi);
        var bytes = ToLittleEndian(y, 32);
        if (!x.IsEven) bytes[31] |= 0x80;
        return bytes;
    }

    private static Point? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) return null;
        var copy = bytes.ToArray();
        int sign = copy[31] >> 7;
        copy[31] &= 0x7F;
        var y = FromLittleEndian(copy);
        if (y >= P) return null;
        var x = RecoverX(y, sign);
        if (x.Sign < 0) return null;
        return new Point(x, y, 1, Mod(x * y));
    }

    private static byte[] ToLittleEndian(BigInteger v, int length)
    {
        var bytes = v.ToByteArray(isUnsigned: true, isBigEndian: false);
        var result = new byte[length];
        Array.Copy(bytes, result, Math.Min(bytes.Length, length));
        return result;
    }

    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    /// <summary>True when <paramref name="signature"/> (64 bytes) is a valid Ed25519 signature of <paramref name="message"/> under the 32-byte public key.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        var a = Decode(publicKey);
        var r = Decode(signature[..32]);
        if (a is null || r is null) return false;
        var s = FromLittleEndian(signature[32..]);
        if (s >= L) return false;
        var h = Hash(signature[..32], publicKey, message);
        var k = BigInteger.Remainder(FromLittleEndian(h), L);
        // Check S·B == R + k·A (cofactor-less, like libsodium's strict verification for small inputs).
        var left = Multiply(Base, s);
        var right = Add(r.Value, Multiply(a.Value, k));
        return Encode(left).AsSpan().SequenceEqual(Encode(right));
    }

    private static byte[] Hash(ReadOnlySpan<byte> r, ReadOnlySpan<byte> a, ReadOnlySpan<byte> m)
    {
        var buffer = new byte[64 + m.Length];
        r.CopyTo(buffer);
        a.CopyTo(buffer.AsSpan(32));
        m.CopyTo(buffer.AsSpan(64));
        return SHA512.HashData(buffer);
    }

    /// <summary>Public key for a 32-byte seed (RFC 8032 §5.1.5); for tests and tools.</summary>
    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        var (s, _) = Expand(seed);
        return Encode(Multiply(Base, s));
    }

    /// <summary>Detached signature (64 bytes) of <paramref name="message"/> with a 32-byte seed; for tests and tools.</summary>
    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var (s, prefix) = Expand(seed);
        var publicKey = Encode(Multiply(Base, s));
        var rInput = new byte[32 + message.Length];
        prefix.CopyTo(rInput, 0);
        message.CopyTo(rInput.AsSpan(32));
        var r = BigInteger.Remainder(FromLittleEndian(SHA512.HashData(rInput)), L);
        var rEncoded = Encode(Multiply(Base, r));
        var k = BigInteger.Remainder(FromLittleEndian(Hash(rEncoded, publicKey, message)), L);
        var sig = new byte[64];
        rEncoded.CopyTo(sig, 0);
        ToLittleEndian(BigInteger.Remainder(r + k * s, L), 32).CopyTo(sig, 32);
        return sig;
    }

    private static (BigInteger Scalar, byte[] Prefix) Expand(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32) throw new ArgumentException("Ed25519 seed must be 32 bytes.", nameof(seed));
        var h = SHA512.HashData(seed);
        h[0] &= 0xF8;
        h[31] &= 0x7F;
        h[31] |= 0x40;
        return (FromLittleEndian(h.AsSpan(0, 32)), h[32..]);
    }
}

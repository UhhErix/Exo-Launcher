using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace Exo.Identity;

/// <summary>
/// The one curve Exo identity uses (plan §2.1): P-256, the curve of DAVE's MLS ciphersuite 2, which Exo already runs.
/// Public keys cross the wire as SEC1 uncompressed points (65 bytes: 04, X, Y) and signatures as IEEE P1363 r || s
/// (64 bytes, s at most n / 2): the forms CNG and WebCrypto both use natively, so the mailbox Worker checks a login with crypto.subtle
/// and BCrypt with no conversion anywhere.
/// </summary>
public static class P256
{
    public const int PublicKeyBytes = 65, SignatureBytes = 64, ScalarBytes = 32;
    public static readonly ECCurve Curve = ECCurve.NamedCurves.nistP256;

    // The group order: a private scalar is 1 to n - 1.
    private static readonly BigInteger Order = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", NumberStyles.HexNumber);

    // A signature's s is at most n / 2 (rounded down) (plan §2.13): (r, n - s) verifies for the same key and message, so
    // without this rule anyone holding a signature could make a second valid one, and a hash or a byte compare over it
    // would see two different records.
    private static readonly BigInteger HalfOrder = Order >> 1;

    public static byte[] PublicKey(ECAlgorithm key)
    {
        var q = key.ExportParameters(false).Q;
        return [0x04, .. q.X!, .. q.Y!];
    }

    /// <summary>A signer or agreement key from a private scalar; .NET computes the public point.</summary>
    public static ECDsa Signer(ReadOnlySpan<byte> scalar) => ECDsa.Create(new ECParameters { Curve = Curve, D = scalar.ToArray() });

    public static ECDiffieHellman Agreement(ReadOnlySpan<byte> scalar) => ECDiffieHellman.Create(new ECParameters { Curve = Curve, D = scalar.ToArray() });

    /// <summary>False for a bad key or signature of any shape, a high-s signature included; a point off the curve never
    /// verifies.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureBytes || !IsLowS(signature) || Point(publicKey) is not { } point) return false;
        try
        {
            using var key = ECDsa.Create(point);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException) { return false; }
    }

    /// <summary>A P1363 signature with s at most n / 2: s becomes n - s when it's higher. CNG and the TPM pick either.</summary>
    public static byte[] LowS(byte[] signature)
    {
        if (signature.Length != SignatureBytes || IsLowS(signature)) return signature;
        var s = (Order - new BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true)).ToByteArray(isUnsigned: true, isBigEndian: true);
        var low = new byte[SignatureBytes];
        signature.AsSpan(0, 32).CopyTo(low);
        s.CopyTo(low.AsSpan(SignatureBytes - s.Length));
        return low;
    }

    public static bool IsLowS(ReadOnlySpan<byte> signature) =>
        new BigInteger(signature[32..], isUnsigned: true, isBigEndian: true) <= HalfOrder;

    /// <summary>
    /// ECDH: the shared point's X coordinate (32 bytes), the "DH" of RFC 9180. Null when the other key isn't a point on
    /// the curve, which CNG checks on import; a sender can't steer the result with a bad point.
    /// </summary>
    public static byte[]? Agree(ECDiffieHellman mine, ReadOnlySpan<byte> otherPublicKey)
    {
        if (Point(otherPublicKey) is not { } point) return null;
        try
        {
            using var other = ECDiffieHellman.Create(point);
            return mine.DeriveRawSecretAgreement(other.PublicKey);
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException) { return null; }
    }

    /// <summary>
    /// A private scalar drawn from key material: HKDF-Expand(prk, info || counter, 32) for counter 0, 1, … until the
    /// number is 1 to n - 1 (a miss is about 1 in 2^32, so the counter is nearly always 0; the vectors hold a sync key
    /// whose box needs counter 1). The recovery code's keys and the sync box's key come from here, so every app derives
    /// the same key from the same secret. prk is used as HKDF's PRK as it is, with no Extract (CNG:
    /// BCRYPT_HKDF_PRK_AND_FINALIZE): the sync key or an app's sub-key, both 32 random bytes, or the recovery code's
    /// HKDF-Extract output.
    /// </summary>
    public static byte[] DeriveScalar(ReadOnlySpan<byte> prk, ReadOnlySpan<byte> info)
    {
        var input = new byte[info.Length + 1];
        info.CopyTo(input);
        var scalar = new byte[ScalarBytes];
        for (var counter = 0; counter < 256; counter++)
        {
            input[^1] = (byte)counter;
            HKDF.Expand(HashAlgorithmName.SHA256, prk, scalar, input);
            if (InRange(scalar)) return scalar;
        }
        throw new CryptographicException("No scalar in range in 256 tries.");
    }

    public static bool InRange(ReadOnlySpan<byte> scalar)
    {
        var value = new BigInteger(scalar, isUnsigned: true, isBigEndian: true);
        return scalar.Length == ScalarBytes && !value.IsZero && value < Order;
    }

    private static ECParameters? Point(ReadOnlySpan<byte> publicKey) =>
        publicKey.Length == PublicKeyBytes && publicKey[0] == 0x04
            ? new ECParameters { Curve = Curve, Q = new ECPoint { X = publicKey[1..33].ToArray(), Y = publicKey[33..].ToArray() } }
            : null;
}

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace ExoLauncher.Helpers;

/// <summary>
/// P-256 signature checks for Exo's signed feeds (<see cref="Services.DriverAdvisories"/>). Public keys are SEC1 uncompressed
/// points (65 bytes: 04, X, Y) and signatures IEEE P1363 r || s (64 bytes, s at most n / 2): the forms CNG and WebCrypto
/// both use natively, so a Worker signs with crypto.subtle and Exo checks with no conversion.
/// </summary>
internal static class P256
{
    private const int PublicKeyBytes = 65, SignatureBytes = 64;

    // The group order. A signature's s is at most n / 2 (rounded down): (r, n - s) verifies for the same key and message,
    // so without this rule anyone holding a signature could make a second valid one.
    private static readonly BigInteger HalfOrder =
        BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", NumberStyles.HexNumber) >> 1;

    /// <summary>False for a bad key or signature of any shape, a high-s signature included; a point off the curve never
    /// verifies.</summary>
    internal static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureBytes || !IsLowS(signature) || publicKey.Length != PublicKeyBytes || publicKey[0] != 0x04) return false;
        try
        {
            using var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = publicKey[1..33].ToArray(), Y = publicKey[33..].ToArray() },
            });
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException) { return false; }
    }

    internal static bool IsLowS(ReadOnlySpan<byte> signature) =>
        new BigInteger(signature[32..], isUnsigned: true, isBigEndian: true) <= HalfOrder;
}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Exo.Identity;

/// <summary>
/// HPKE (RFC 9180) base mode in the one suite Exo uses (plan §2.1): KEM 0x0010 DHKEM(P-256, HKDF-SHA256), KDF 0x0001
/// HKDF-SHA256, AEAD 0x0001 AES-128-GCM, the suite of MLS ciphersuite 2. .NET 10 has no HPKE, so it is written here
/// from .NET's own ECDH, HKDF and AES-GCM and checked against RFC 9180's A.3.1 vectors. The DH value is the shared
/// point's X coordinate big-endian: .NET's DeriveRawSecretAgreement already turns CNG's little-endian
/// BCRYPT_KDF_RAW_SECRET output around (C++ callers reverse it themselves; §2.11).
/// </summary>
public static class Hpke
{
    public const int EncBytes = P256.PublicKeyBytes, TagBytes = 16;
    private const int KeyBytes = 16, NonceBytes = 12, HashBytes = 32;
    private static readonly byte[] KemSuite = [.. "KEM"u8, 0x00, 0x10];
    private static readonly byte[] HpkeSuite = [.. "HPKE"u8, 0x00, 0x10, 0x00, 0x01, 0x00, 0x01];

    /// <summary>One sender's or recipient's context: its messages are numbered, so each nonce is used once.</summary>
    public sealed class Context(byte[] key, byte[] baseNonce, byte[] exporterSecret)
    {
        private ulong _seq;

        public byte[] Key => key;
        public byte[] BaseNonce => baseNonce;
        public byte[] ExporterSecret => exporterSecret;

        public byte[] Seal(ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext)
        {
            var output = new byte[plaintext.Length + TagBytes];
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(NextNonce(), plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), aad);
            return output;
        }

        /// <summary>The plaintext, or null when the ciphertext or its additional data changed.</summary>
        public byte[]? Open(ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ciphertext)
        {
            if (ciphertext.Length < TagBytes) return null;
            var plain = new byte[ciphertext.Length - TagBytes];
            using var aes = new AesGcm(key, TagBytes);
            try { aes.Decrypt(Nonce(_seq), ciphertext[..plain.Length], ciphertext[plain.Length..], plain, aad); }
            catch (AuthenticationTagMismatchException) { return null; }
            _seq++;
            return plain;
        }

        public byte[] Export(ReadOnlySpan<byte> exporterContext, int length) =>
            LabeledExpand(exporterSecret, HpkeSuite, "sec"u8, exporterContext, length);

        private byte[] NextNonce() => Nonce(_seq++);

        private byte[] Nonce(ulong seq)
        {
            var nonce = (byte[])baseNonce.Clone();
            Span<byte> counter = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(counter, seq);
            for (var i = 0; i < 8; i++) nonce[NonceBytes - 8 + i] ^= counter[i];
            return nonce;
        }
    }

    /// <summary>A sender's context to <paramref name="recipient"/>, and the enc it sends along. Null when the
    /// recipient isn't a point on the curve.</summary>
    public static (byte[] Enc, Context Context)? SetupSender(ReadOnlySpan<byte> recipient, ReadOnlySpan<byte> info, ECDiffieHellman? ephemeral = null)
    {
        using var own = ephemeral is null ? ECDiffieHellman.Create(P256.Curve) : null;
        var key = ephemeral ?? own!;
        if (P256.Agree(key, recipient) is not { } dh) return null;
        var enc = P256.PublicKey(key);
        var shared = ExtractAndExpand(dh, [.. enc, .. recipient]);
        return (enc, KeySchedule(shared, info));
    }

    /// <summary>The recipient's context for <paramref name="enc"/>, or null when enc isn't a point on the curve.</summary>
    public static Context? SetupRecipient(IDeviceDecrypter recipient, ReadOnlySpan<byte> enc, ReadOnlySpan<byte> info)
    {
        if (enc.Length != EncBytes || recipient.Agree(enc) is not { } dh) return null;
        var shared = ExtractAndExpand(dh, [.. enc, .. recipient.PublicKey]);
        return KeySchedule(shared, info);
    }

    /// <summary>RFC 9180 DeriveKeyPair for P-256: the private scalar drawn from ikm (the vectors' ikmE and ikmR).</summary>
    public static byte[] DeriveKeyPair(ReadOnlySpan<byte> ikm)
    {
        var prk = LabeledExtract([], KemSuite, "dkp_prk"u8, ikm);
        for (var counter = 0; counter < 256; counter++)
        {
            var candidate = LabeledExpand(prk, KemSuite, "candidate"u8, [(byte)counter], P256.ScalarBytes);
            if (P256.InRange(candidate)) return candidate;
        }
        throw new CryptographicException("DeriveKeyPairError");
    }

    private static byte[] ExtractAndExpand(ReadOnlySpan<byte> dh, ReadOnlySpan<byte> kemContext)
    {
        var prk = LabeledExtract([], KemSuite, "eae_prk"u8, dh);
        return LabeledExpand(prk, KemSuite, "shared_secret"u8, kemContext, HashBytes);
    }

    private static Context KeySchedule(ReadOnlySpan<byte> shared, ReadOnlySpan<byte> info)
    {
        var pskIdHash = LabeledExtract([], HpkeSuite, "psk_id_hash"u8, []);
        var infoHash = LabeledExtract([], HpkeSuite, "info_hash"u8, info);
        byte[] context = [0x00, .. pskIdHash, .. infoHash];
        var secret = LabeledExtract(shared, HpkeSuite, "secret"u8, []);
        return new(
            LabeledExpand(secret, HpkeSuite, "key"u8, context, KeyBytes),
            LabeledExpand(secret, HpkeSuite, "base_nonce"u8, context, NonceBytes),
            LabeledExpand(secret, HpkeSuite, "exp"u8, context, HashBytes));
    }

    private static byte[] LabeledExtract(ReadOnlySpan<byte> salt, byte[] suite, ReadOnlySpan<byte> label, ReadOnlySpan<byte> ikm) =>
        HKDF.Extract(HashAlgorithmName.SHA256, [.. "HPKE-v1"u8, .. suite, .. label, .. ikm], salt.ToArray());

    private static byte[] LabeledExpand(ReadOnlySpan<byte> prk, byte[] suite, ReadOnlySpan<byte> label, ReadOnlySpan<byte> info, int length)
    {
        var output = new byte[length];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, output, [(byte)(length >> 8), (byte)length, .. "HPKE-v1"u8, .. suite, .. label, .. info]);
        return output;
    }
}

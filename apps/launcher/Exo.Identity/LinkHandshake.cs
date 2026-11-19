using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Exo.Identity;

/// <summary>
/// Linking a new PC to the ones already linked (plan §2.4). Most PCs have no camera, so a typed 8-digit nameplate is
/// the main path and the QR the shortcut. The nameplate is a meeting place on the mailbox Worker, not a secret:
/// <list type="number">
/// <item>The new PC (N) makes an ephemeral key eN and gets a nameplate.</item>
/// <item>The existing PC (E) makes eE and sends the commitment C = SHA-256("exo-link-commit" || eE_pub).</item>
/// <item>Only then N sends eN_pub; E reveals eE_pub and N checks it against C.</item>
/// <item>Both take ECDH(eN, eE), HKDF-Extract it with the nameplate's 8 ASCII digits as salt, and expand the link key
/// (info "exo link v1" || eN_pub || eE_pub) and 8 bytes for the 6-digit match code (info "exo link sas" || eN_pub ||
/// eE_pub, big-endian, mod 1,000,000).</item>
/// </list>
/// Someone in the middle has to commit to a key before seeing N's, so they match the 6 digits by chance once in a
/// million (the ZRTP and Bluetooth numeric-comparison model). With the QR, E also checks eN_pub against the QR's hash.
/// </summary>
public static class LinkHandshake
{
    public const int NameplateDigits = 8, MatchDigits = 6, CommitmentBytes = 32;

    /// <summary>What N and E send each other through the rendezvous: the first byte of each frame.</summary>
    public const byte CommitFrame = 0x01, KeyFrame = 0x02, RevealFrame = 0x03, SealedFrame = 0x04;

    public sealed record Keys(byte[] Key, string MatchCode);

    public static bool IsNameplate(string text) => text.Length == NameplateDigits && text.All(char.IsAsciiDigit);

    public static byte[] Commitment(ReadOnlySpan<byte> existingPcKey) => SHA256.HashData([.. "exo-link-commit"u8, .. existingPcKey]);

    /// <summary><c>exo-link:1:&lt;nameplate&gt;:&lt;base32 SHA-256(eN_pub)&gt;</c>.</summary>
    public static string QrText(string nameplate, ReadOnlySpan<byte> newPcKey) =>
        $"exo-link:1:{nameplate}:{Base32.Encode(SHA256.HashData(newPcKey))}";

    public static (string Nameplate, byte[] KeyHash)? ReadQr(string text)
    {
        var parts = text.Split(':');
        if (parts is not ["exo-link", "1", var nameplate, var hash] || !IsNameplate(nameplate)) return null;
        return Base32.Decode(hash) is { Length: 32 } keyHash ? (nameplate, keyHash) : null;
    }

    public static Keys Derive(ReadOnlySpan<byte> shared, string nameplate, ReadOnlySpan<byte> newPcKey, ReadOnlySpan<byte> existingPcKey)
    {
        if (!IsNameplate(nameplate)) throw new ArgumentException("A nameplate is 8 digits.", nameof(nameplate));
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, shared.ToArray(), Encoding.ASCII.GetBytes(nameplate));
        var key = new byte[32];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, key, [.. "exo link v1"u8, .. newPcKey, .. existingPcKey]);
        Span<byte> match = stackalloc byte[8];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, match, [.. "exo link sas"u8, .. newPcKey, .. existingPcKey]);
        var code = BinaryPrimitives.ReadUInt64BigEndian(match) % 1_000_000;
        return new(key, code.ToString("D6", CultureInfo.InvariantCulture));
    }

    /// <summary>The new PC's side: it sends its key only after holding E's commitment, then checks E's reveal.</summary>
    public sealed class NewPc(ECDiffieHellman? key = null) : IDisposable
    {
        private readonly ECDiffieHellman _key = key ?? ECDiffieHellman.Create(P256.Curve);
        private byte[]? _commitment;

        public byte[] PublicKey => P256.PublicKey(_key);

        /// <summary>E's commitment, taken once; the answer is N's key.</summary>
        public byte[] TakeCommitment(ReadOnlySpan<byte> commitment)
        {
            if (_commitment is not null) throw new InvalidOperationException("The commitment came already.");
            if (commitment.Length != CommitmentBytes) throw new ArgumentException("A commitment is 32 bytes.", nameof(commitment));
            _commitment = commitment.ToArray();
            return PublicKey;
        }

        /// <summary>The keys, or null when E's key isn't the one it committed to, or isn't a point.</summary>
        public Keys? TakeReveal(ReadOnlySpan<byte> existingPcKey, string nameplate)
        {
            if (_commitment is null) throw new InvalidOperationException("No commitment yet: N's key must not have gone out.");
            if (!CryptographicOperations.FixedTimeEquals(Commitment(existingPcKey), _commitment)) return null;
            return P256.Agree(_key, existingPcKey) is { } shared ? Derive(shared, nameplate, PublicKey, existingPcKey) : null;
        }

        public void Dispose() => _key.Dispose();
    }

    /// <summary>The existing PC's side: it commits first and reveals only after N's key arrived.</summary>
    public sealed class ExistingPc(ECDiffieHellman? key = null) : IDisposable
    {
        private readonly ECDiffieHellman _key = key ?? ECDiffieHellman.Create(P256.Curve);

        public byte[] PublicKey => P256.PublicKey(_key);
        public byte[] Commitment => LinkHandshake.Commitment(PublicKey);

        /// <summary>E's key to reveal and the keys; null when N's key isn't a point, or isn't the one in the QR.</summary>
        public (byte[] Reveal, Keys Keys)? TakeNewPcKey(ReadOnlySpan<byte> newPcKey, string nameplate, byte[]? qrKeyHash = null)
        {
            if (qrKeyHash is not null && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(newPcKey), qrKeyHash)) return null;
            return P256.Agree(_key, newPcKey) is { } shared ? (PublicKey, Derive(shared, nameplate, newPcKey, PublicKey)) : null;
        }

        public void Dispose() => _key.Dispose();
    }
}

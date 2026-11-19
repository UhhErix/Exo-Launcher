using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Exo.Identity;

/// <summary>
/// ECDH with a CNG key inside its own provider (plan §2.19): the other side's public key is
/// imported into the same provider handle (NCryptImportKey, ECCPUBLICBLOB), then NCryptSecretAgreement and
/// NCryptDeriveKey with BCRYPT_KDF_RAW_SECRET, whose little-endian X is reversed. .NET's
/// ECDiffieHellmanCng.DeriveRawSecretAgreement imports the other key into the software provider, and a TPM key won't
/// agree across providers (NTE_INVALID_HANDLE), which once read as the TPM refusing ECDH outright.
/// </summary>
public static partial class CngAgreement
{
    private const uint EcdhPublicP256Magic = 0x314B4345; // BCRYPT_ECDH_PUBLIC_P256_MAGIC, "ECK1"

    /// <summary>The shared X coordinate (32 bytes, big-endian), or null when the key or the point is refused.</summary>
    public static byte[]? Agree(CngKey key, ReadOnlySpan<byte> otherPublicKey)
    {
        if (otherPublicKey.Length != P256.PublicKeyBytes || otherPublicKey[0] != 0x04) return null;
        var blob = new byte[8 + 64];
        BitConverter.TryWriteBytes(blob.AsSpan(0, 4), EcdhPublicP256Magic);
        BitConverter.TryWriteBytes(blob.AsSpan(4, 4), 32u);
        otherPublicKey[1..].CopyTo(blob.AsSpan(8));

        using var provider = key.ProviderHandle;
        using var mine = key.Handle;
        if (NCryptImportKey(provider, IntPtr.Zero, "ECCPUBLICBLOB", IntPtr.Zero, out var theirs, blob, blob.Length, 0) != 0) return null;
        using (theirs)
        {
            if (NCryptSecretAgreement(mine, theirs, out var secret, 0) != 0) return null;
            using (secret)
            {
                var raw = new byte[32];
                if (NCryptDeriveKey(secret, "TRUNCATE", IntPtr.Zero, raw, raw.Length, out var written, 0) != 0 || written != 32) return null;
                Array.Reverse(raw);
                return raw;
            }
        }
    }

    /// <summary>Whether <paramref name="key"/> agrees with a fresh software key both ways: the probe a TPM key must pass.</summary>
    public static bool Works(CngKey key)
    {
        try
        {
            using var mine = new ECDiffieHellmanCng(key);
            using var other = ECDiffieHellman.Create(P256.Curve);
            var ours = Agree(key, P256.PublicKey(other));
            return ours is not null && ours.AsSpan().SequenceEqual(other.DeriveRawSecretAgreement(mine.PublicKey));
        }
        catch (CryptographicException) { return false; }
    }

    [LibraryImport("ncrypt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NCryptImportKey(SafeNCryptProviderHandle provider, IntPtr importKey, string blobType, IntPtr parameters,
        out SafeNCryptKeyHandle key, byte[] data, int dataBytes, int flags);

    [LibraryImport("ncrypt.dll")]
    private static partial int NCryptSecretAgreement(SafeNCryptKeyHandle privateKey, SafeNCryptKeyHandle publicKey, out SafeNCryptSecretHandle secret, int flags);

    [LibraryImport("ncrypt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NCryptDeriveKey(SafeNCryptSecretHandle secret, string kdf, IntPtr parameters, byte[] derived, int derivedBytes,
        out int written, int flags);
}

/// <summary>This PC's CNG encryption key as HPKE's recipient, agreeing inside its own provider (<see cref="CngAgreement"/>).</summary>
public sealed class CngEcdhDecrypter(CngKey key) : IDeviceDecrypter, IDisposable
{
    public byte[] PublicKey { get; } = PublicOf(key);

    public byte[]? Agree(ReadOnlySpan<byte> otherPublicKey) => CngAgreement.Agree(key, otherPublicKey);

    public void Dispose() => key.Dispose();

    private static byte[] PublicOf(CngKey key)
    {
        using var ecdh = new ECDiffieHellmanCng(key);
        return P256.PublicKey(ecdh);
    }
}

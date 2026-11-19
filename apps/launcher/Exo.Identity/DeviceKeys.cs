using System.Security.Cryptography;
using System.Text;

namespace Exo.Identity;

/// <summary>Signs with a device's identity key (or the recovery code's key): P1363 ECDSA P-256 with SHA-256, s at most
/// n / 2 (<see cref="P256.LowS"/>).</summary>
public interface IDeviceSigner
{
    /// <summary>SEC1 uncompressed, 65 bytes.</summary>
    byte[] PublicKey { get; }

    byte[] Sign(ReadOnlySpan<byte> data);
}

/// <summary>The device encryption key's one operation: ECDH, as HPKE's recipient.</summary>
public interface IDeviceDecrypter
{
    byte[] PublicKey { get; }

    /// <summary>The shared X coordinate, or null when the other key isn't a point on the curve.</summary>
    byte[]? Agree(ReadOnlySpan<byte> otherPublicKey);
}

/// <summary>Where this PC's keys live. The tests put an in-memory store here; the app has <see cref="CngDeviceKeys"/>.</summary>
public interface IDeviceKeyStore
{
    /// <summary>Both keys, or null when either is missing. Never creates anything.</summary>
    DeviceKeys? Open();

    /// <summary>Both keys, made first when they're missing. Only the setup and link flows call this, so a lost key
    /// is never silently replaced by a fresh identity (<see cref="ExoIdentity"/>).</summary>
    DeviceKeys OpenOrCreate();
}

public sealed class DeviceKeys(IDeviceSigner identity, IDeviceDecrypter encryption, bool hardware = false) : IDisposable
{
    public IDeviceSigner Identity => identity;
    public IDeviceDecrypter Encryption => encryption;

    /// <summary>The identity key is in this PC's TPM rather than in software.</summary>
    public bool Hardware => hardware;

    public string DeviceId => Device.IdOf(identity.PublicKey);

    public void Dispose()
    {
        (identity as IDisposable)?.Dispose();
        (encryption as IDisposable)?.Dispose();
    }
}

/// <summary>A .NET ECDSA key as a signer: a CNG key of this PC, the recovery code's key, or a test's key.</summary>
public sealed class EcdsaSigner(ECDsa key) : IDeviceSigner, IDisposable
{
    public byte[] PublicKey { get; } = P256.PublicKey(key);

    public byte[] Sign(ReadOnlySpan<byte> data) =>
        P256.LowS(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose() => key.Dispose();
}

/// <summary>A .NET ECDH key as HPKE's recipient: this PC's CNG encryption key, or a test's key.</summary>
public sealed class EcdhDecrypter(ECDiffieHellman key) : IDeviceDecrypter, IDisposable
{
    public byte[] PublicKey { get; } = P256.PublicKey(key);

    public byte[]? Agree(ReadOnlySpan<byte> otherPublicKey) => P256.Agree(key, otherPublicKey);

    public void Dispose() => key.Dispose();
}

/// <summary>
/// This PC's identity keys in Windows' CNG (plan §2.2): non-exportable P-256 keys made with NCryptCreatePersistedKey
/// (through <see cref="CngKey.Create(CngAlgorithm, string?, CngKeyCreationParameters?)"/>) under fixed per-user names
/// that every Exo program of that user opens, so they are one device. Each key goes in the TPM (Microsoft
/// Platform Crypto Provider) when there is one and it does the job, else in the Microsoft Software Key Storage
/// Provider. The encryption key is kept on the TPM only when an ECDH round trip works there, agreeing inside the TPM's own
/// provider (<see cref="CngAgreement"/>; agreeing across providers fails with NTE_INVALID_HANDLE, which once read as the
/// TPM refusing ECDH). A key is never moved between providers later.
///
/// Any Exo app may create the keys. Creation is serialized across
/// apps by the named mutex <see cref="CreateMutexName"/>, and NTE_EXISTS (another app got there first) opens the key it
/// made. Any other program of the same Windows user can ask CNG to use these keys; the TPM keeps them on this PC, not
/// away from that user's programs (the same boundary as DPAPI CurrentUser).
/// </summary>
public sealed class CngDeviceKeys(string identityName, string encryptionName) : IDeviceKeyStore
{
    public const string IdentityKeyName = "Exo Device Identity v1", EncryptionKeyName = "Exo Device Encryption v1";
    public const string CreateMutexName = @"Local\Exo.Identity.Create";
    private const int NteExists = unchecked((int)0x8009000F);
    private static readonly CngProvider Platform = new("Microsoft Platform Crypto Provider");
    private static readonly CngProvider Software = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    public string IdentityName => identityName;
    public string EncryptionName => encryptionName;

    /// <summary>
    /// The keys for the identity kept in <paramref name="folder"/> (<see cref="IdentityStore.Folder"/> unless an app runs
    /// as a test copy): the real ones in the real folder, else a test copy's own (plan §2.9).
    /// </summary>
    public static CngDeviceKeys For(string folder)
    {
        var suffix = TestSuffix(folder);
        return new(IdentityKeyName + suffix, EncryptionKeyName + suffix);
    }

    /// <summary>
    /// "" for the real folder (<see cref="IdentityStore.RealFolder"/>); for any other (a test copy's, the packaged smoke
    /// run's), " test-" and the first 12 hex digits of SHA-256 over the folder's full path in lowercase UTF-8 without a
    /// trailing separator. A test copy then never signs, decrypts or links as the user's real device, and two test
    /// folders never share keys.
    /// </summary>
    public static string TestSuffix(string folder)
    {
        if (!IdentityStore.IsTest(folder)) return "";
        return " test-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(IdentityStore.Normalize(folder))))[..12];
    }

    public DeviceKeys? Open()
    {
        var identity = OpenEither(identityName);
        var encryption = OpenEither(encryptionName);
        if (identity is not null && encryption is not null) return Wrap(identity, encryption);
        identity?.Dispose();
        encryption?.Dispose();
        return null;
    }

    public DeviceKeys OpenOrCreate()
    {
        using var mutex = new Mutex(false, CreateMutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("Another Exo app is still making the identity keys.");
        }
        catch (AbandonedMutexException) { /* its holder exited mid-way; what it left is opened or replaced below */ }
        try
        {
            var identity = OpenEither(identityName);
            var encryption = OpenEither(encryptionName);
            if (encryption is not null && Equals(encryption.Provider, Platform) && !Agrees(encryption))
            {
                encryption.Delete();
                encryption = null;
            }
            if (identity is null || encryption is null)
            {
                // Either key gone (a TPM clear, a reset profile, a creation cut short) makes a new device, both keys
                // new: the PC never reappears under its old record with half its keys replaced.
                identity?.Delete();
                encryption?.Delete();
                identity = Create(CngAlgorithm.ECDsaP256, identityName, works: null);
                encryption = Create(CngAlgorithm.ECDiffieHellmanP256, encryptionName, works: Agrees);
            }
            return Wrap(identity, encryption);
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>Deletes a test copy's keys from both providers. Exo never deletes the real ones.</summary>
    public void DeleteTestKeys()
    {
        if (!identityName.StartsWith(IdentityKeyName + " test-", StringComparison.Ordinal) ||
            !encryptionName.StartsWith(EncryptionKeyName + " test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Only a test copy's keys are ever deleted.");
        foreach (var name in new[] { identityName, encryptionName })
            while (OpenEither(name) is { } key) key.Delete();
    }

    private static CngKey? OpenEither(string name)
    {
        foreach (var provider in new[] { Platform, Software })
        {
            try
            {
                if (CngKey.Exists(name, provider)) return CngKey.Open(name, provider);
            }
            catch (CryptographicException) { /* no TPM: that provider doesn't open */ }
        }
        return null;
    }

    /// <summary>On the TPM when it takes the key and <paramref name="works"/> agrees, else in software.</summary>
    private static CngKey Create(CngAlgorithm algorithm, string name, Func<CngKey, bool>? works)
    {
        foreach (var provider in new[] { Platform, Software })
        {
            var parameters = new CngKeyCreationParameters { Provider = provider };
            // The TPM never exports a private key; the software provider is told so outright.
            if (Equals(provider, Software)) parameters.ExportPolicy = CngExportPolicies.None;
            CngKey key;
            try { key = CngKey.Create(algorithm, name, parameters); }
            catch (CryptographicException e) when (e.HResult == NteExists) { key = CngKey.Open(name, provider); }
            catch (CryptographicException) when (Equals(provider, Platform)) { continue; }
            if (works is null || works(key)) return key;
            key.Delete();
        }
        throw new CryptographicException("Neither key provider made a working " + algorithm.Algorithm + " key.");
    }

    private static bool Agrees(CngKey key) => CngAgreement.Works(key);

    private static DeviceKeys Wrap(CngKey identity, CngKey encryption)
    {
        // The signer's algorithm object holds its own handle to the identity key; the decrypter keeps the encryption key
        // itself, to agree inside its provider.
        using (identity)
            return new(new EcdsaSigner(new ECDsaCng(identity)), new CngEcdhDecrypter(encryption), Equals(identity.Provider, Platform));
    }
}

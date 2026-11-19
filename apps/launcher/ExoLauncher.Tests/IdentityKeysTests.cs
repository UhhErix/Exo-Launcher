using System.Security.Cryptography;
using ExoLauncher.Helpers;
using Exo.Identity;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways this PC's identity keys could go wrong where nobody would see it until a link or a sync failed:
/// - a test run, the packaged smoke run or an E2E copy on a test data folder signs with the user's real keys (or two
///   test folders share keys), or the real data folder gets a test suffix;
/// - checking whether the PC is set up creates a key, so a lost key silently becomes a fresh identity instead of
///   "link this PC again" (plan §2.9), or a replaced encryption key goes unnoticed;
/// - two Exo apps creating at once end with two different keys (the mutex, NTE_EXISTS);
/// - a TPM that makes an ECDH key but can't agree on it (this PC's) ends up holding the encryption key;
/// - a key that comes back from CNG doesn't sign, verify or open HPKE as the in-memory ones do;
/// - a test copy reaches the real mailbox.
/// The CNG test makes real keys under this run's test suffix only and deletes them before it ends.
/// </summary>
public sealed class IdentityKeysTests
{
    private static string TestIdentityFolder => Path.Combine(TestDataDirectory.Root, "identity");

    [Fact]
    public void ATestDataFolderGetsItsOwnKeyNames()
    {
        // The real folder is the shared one; a test copy's is anything else.
        var real = IdentityStore.RealFolder;
        Assert.Equal("", CngDeviceKeys.TestSuffix(real));
        Assert.Equal("", CngDeviceKeys.TestSuffix(real.ToUpperInvariant() + "\\"));

        var suffix = CngDeviceKeys.TestSuffix(@"C:\Temp\exo-tests-1");
        Assert.Matches("^ test-[0-9a-f]{12}$", suffix);
        var expected = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(@"c:\temp\exo-tests-1")))[..12];
        Assert.Equal(" test-" + expected, suffix);
        Assert.Equal(suffix, CngDeviceKeys.TestSuffix(@"c:\TEMP\exo-tests-1\"));
        Assert.NotEqual(suffix, CngDeviceKeys.TestSuffix(@"C:\Temp\exo-tests-2"));

        // A folder under the test data directory is a test copy's identity, never the user's.
        var keys = CngDeviceKeys.For(TestIdentityFolder);
        Assert.True(IdentityStore.IsTest(TestIdentityFolder));
        Assert.StartsWith(CngDeviceKeys.IdentityKeyName + " test-", keys.IdentityName, StringComparison.Ordinal);
        Assert.StartsWith(CngDeviceKeys.EncryptionKeyName + " test-", keys.EncryptionName, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new CngDeviceKeys(CngDeviceKeys.IdentityKeyName, CngDeviceKeys.EncryptionKeyName).DeleteTestKeys());
    }

    [Fact]
    public void ALostKeyMeansLinkThisPcAgainNeverAFreshIdentity()
    {
        var store = new TestKeys.Store();
        var state = Path.Combine(TestDataDirectory.Root, "identity-" + Guid.NewGuid().ToString("N") + ".json");
        var identity = new ExoIdentity(store, state);

        Assert.Equal(ExoIdentity.Status.NotSetUp, identity.Check().Status);
        Assert.Equal(0, store.Creates);

        using (var keys = identity.KeysForSetup()) identity.SaveLinked("AAISEM2EKVTHPCEZVK54ZXPO74", keys);
        var (status, linked) = identity.Check();
        Assert.Equal(ExoIdentity.Status.Ready, status);
        Assert.Equal("AAISEM2EKVTHPCEZVK54ZXPO74", linked!.ExoId);
        var oldDevice = linked.DeviceId;

        store.LoseIdentity();
        (status, linked) = identity.Check();
        Assert.Equal(ExoIdentity.Status.LinkAgain, status);
        Assert.Equal(oldDevice, linked!.DeviceId);
        Assert.Equal(1, store.Creates);

        // Linking again makes a new device; the old record is the one another PC removes.
        using (var keys = identity.KeysForSetup())
        {
            Assert.NotEqual(oldDevice, keys.DeviceId);
            Assert.Equal(ExoIdentity.Status.LinkAgain, identity.Check().Status);
            identity.SaveLinked("AAISEM2EKVTHPCEZVK54ZXPO74", keys);
        }
        Assert.Equal(ExoIdentity.Status.Ready, identity.Check().Status);

        store.ReplaceEncryption();
        Assert.Equal(ExoIdentity.Status.LinkAgain, identity.Check().Status);

        // A state file that no longer reads still says this PC was linked once.
        File.WriteAllText(state, "{not json");
        Assert.Equal(ExoIdentity.Status.LinkAgain, identity.Check().Status);
        DeletionGate.DeleteFile(state);
        Assert.Equal(ExoIdentity.Status.NotSetUp, identity.Check().Status);
    }

    [Fact]
    public void ATestCopyReachesOnlyAMailboxOnThisPc()
    {
        Assert.True(Mailbox.EndpointAllowed(new Uri("ws://127.0.0.1:8787"), testCopy: true));
        Assert.False(Mailbox.EndpointAllowed(new Uri("wss://exo-mailbox.example.workers.dev"), testCopy: true));
        Assert.True(Mailbox.EndpointAllowed(new Uri("wss://exo-mailbox.example.workers.dev"), testCopy: false));
        Assert.False(Mailbox.EndpointAllowed(new Uri("ws://exo-mailbox.example.workers.dev"), testCopy: false));
        Assert.False(Mailbox.EndpointAllowed(new Uri("http://127.0.0.1:8787"), testCopy: false));
    }

    [Fact]
    public async Task RealCngKeysOnThisPcUnderTheTestSuffix()
    {
        var store = CngDeviceKeys.For(TestIdentityFolder);
        Assert.Contains(" test-", store.IdentityName, StringComparison.Ordinal);
        try
        {
            store.DeleteTestKeys();
            Assert.Null(store.Open());

            // Several apps at once: one pair of keys comes out of it.
            var made = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                using var keys = store.OpenOrCreate();
                return (Convert.ToHexString(keys.Identity.PublicKey), Convert.ToHexString(keys.Encryption.PublicKey));
            })).ToArray();
            Assert.Single((await Task.WhenAll(made)).Distinct());

            using var opened = store.Open()!;
            Assert.Equal((await made[0]).Item1, Convert.ToHexString(opened.Identity.PublicKey));
            TestContext.Current.SendDiagnosticMessage(opened.Hardware ? "Identity key on the TPM." : "Identity key in the software provider.");
            // On a PC whose TPM agrees inside its own provider (this one does), the encryption key is
            // there too, and it agrees as a software key would.
            using (var encryption = CngKey.Open(store.EncryptionName, new CngProvider(opened.Hardware ? "Microsoft Platform Crypto Provider" : "Microsoft Software Key Storage Provider")))
            {
                Assert.True(CngAgreement.Works(encryption));
                bool tpmAgrees;
                try
                {
                    using var probe = CngKey.Create(CngAlgorithm.ECDiffieHellmanP256, null, new CngKeyCreationParameters { Provider = new CngProvider("Microsoft Platform Crypto Provider") });
                    tpmAgrees = CngAgreement.Works(probe);
                }
                catch (CryptographicException) { tpmAgrees = false; }
                if (opened.Hardware && tpmAgrees) Assert.Equal("Microsoft Platform Crypto Provider", encryption.Provider!.Provider);
                TestContext.Current.SendDiagnosticMessage("Encryption key in " + encryption.Provider!.Provider + ".");
            }

            // It signs as P1363 that verifies, and opens HPKE sealed to it, as the in-memory keys do.
            var signature = opened.Identity.Sign("exo"u8);
            Assert.True(P256.Verify(opened.Identity.PublicKey, "exo"u8, signature));
            using var writer = TestKeys.Signer();
            var envelope = Envelope.SealToDevice(new byte[16], opened.Encryption.PublicKey, "to this PC"u8, writer)!;
            Assert.Equal("to this PC"u8.ToArray(), Envelope.OpenToDevice(envelope, new byte[16], opened.Encryption, [writer.PublicKey])!.Data);

            // Keys already there (made by another Exo app) are opened, never replaced.
            using (var again = store.OpenOrCreate()) Assert.Equal(opened.DeviceId, again.DeviceId);

            // The encryption key alone gone: a new device, both keys new.
            DeleteOne(store.EncryptionName);
            Assert.Null(store.Open());
            using var fresh = store.OpenOrCreate();
            Assert.NotEqual(opened.DeviceId, fresh.DeviceId);
        }
        finally
        {
            store.DeleteTestKeys();
            Assert.Null(store.Open());
        }
    }

    private static void DeleteOne(string name)
    {
        foreach (var provider in new[] { new CngProvider("Microsoft Platform Crypto Provider"), CngProvider.MicrosoftSoftwareKeyStorageProvider })
        {
            try { if (CngKey.Exists(name, provider)) CngKey.Open(name, provider).Delete(); }
            catch (CryptographicException) { /* no TPM */ }
        }
    }
}

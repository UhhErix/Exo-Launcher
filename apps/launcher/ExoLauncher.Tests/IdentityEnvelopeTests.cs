using System.Security.Cryptography;
using Exo.Identity;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the envelope (plan §2.5) could fail without any error the user sees:
/// - a sync item, a to-device message or a link message doesn't come back out as it went in, or comes out under the
///   wrong mailbox, epoch, key or recipient (it would merge into the wrong place);
/// - a changed byte anywhere (header, nonce, enc, ciphertext, tag, signature) still opens;
/// - a device not in the list (one removed in an earlier epoch, still holding the old key) gets its items taken, or the
///   wrong device is credited as the writer;
/// - the padded size tells the mailbox the length, or a size class lets an envelope outgrow the mailbox's 64 KiB
///   message, or an over-long batch is cut short instead of refused;
/// - a padding with junk after the data, a length past the end, or a larger size than the data needs is accepted (two
///   spellings of one message);
/// - a sync batch that isn't canonical, or belongs to another app, or names an item twice, merges anyway.
/// </summary>
public sealed class IdentityEnvelopeTests
{
    private static readonly byte[] Box = Convert.FromHexString("00112233445566778899aabbccddeeff");
    private static readonly byte[] SyncKey = SHA256.HashData("sync key"u8);

    [Fact]
    public void ASyncItemOpensForTheGroupAndNamesItsWriter()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        var key = Envelope.SyncSubKey(SyncKey, "launcher");
        var envelope = Envelope.SealSync(Box, 7, key, "{\"wishlist\":[1,2,3]}"u8, b);
        Assert.Equal((Envelope.SyncKind, 7u), Envelope.Header(envelope));
        var opened = Envelope.OpenSync(envelope, Box, key, [a.PublicKey, b.PublicKey]);
        Assert.NotNull(opened);
        Assert.Equal(1, opened.Sender);
        Assert.Equal("{\"wishlist\":[1,2,3]}"u8.ToArray(), opened.Data);

        // Another app's sub-key, another box, or a writer who isn't in the list: nothing.
        Assert.Null(Envelope.OpenSync(envelope, Box, Envelope.SyncSubKey(SyncKey, "other"), [a.PublicKey, b.PublicKey]));
        Assert.Null(Envelope.OpenSync(envelope, Box.Reverse().ToArray(), key, [a.PublicKey, b.PublicKey]));
        Assert.Null(Envelope.OpenSync(envelope, Box, key, [a.PublicKey]));
        Assert.Null(Envelope.OpenSync(envelope, Box, key, []));
        Assert.NotEqual(Envelope.SyncSubKey(SyncKey, "launcher"), Envelope.SyncSubKey(SyncKey, "browser-history"));
    }

    [Fact]
    public void EveryChangedByteIsRefused()
    {
        using var writer = TestKeys.Signer();
        using var device = TestKeys.Decrypter();
        var key = Envelope.SyncSubKey(SyncKey, "launcher");
        var sync = Envelope.SealSync(Box, 1, key, "data"u8, writer);
        var toDevice = Envelope.SealToDevice(Box, device.PublicKey, "sync key"u8, writer)!;
        var link = Envelope.SealLink(key, "offer"u8);
        for (var i = 0; i < sync.Length; i++)
        {
            var changed = (byte[])sync.Clone();
            changed[i] ^= 0x01;
            Assert.Null(Envelope.OpenSync(changed, Box, key, [writer.PublicKey]));
        }
        for (var i = 0; i < toDevice.Length; i++)
        {
            var changed = (byte[])toDevice.Clone();
            changed[i] ^= 0x01;
            Assert.Null(Envelope.OpenToDevice(changed, Box, device, [writer.PublicKey]));
        }
        for (var i = 0; i < link.Length; i++)
        {
            var changed = (byte[])link.Clone();
            changed[i] ^= 0x01;
            Assert.Null(Envelope.OpenLink(changed, key));
        }
        Assert.NotNull(Envelope.OpenSync(sync, Box, key, [writer.PublicKey]));
        Assert.NotNull(Envelope.OpenToDevice(toDevice, Box, device, [writer.PublicKey]));
        Assert.Equal("offer"u8.ToArray(), Envelope.OpenLink(link, key));
        // Cut short or grown: refused too.
        Assert.Null(Envelope.OpenSync(sync[..^1], Box, key, [writer.PublicKey]));
        Assert.Null(Envelope.OpenSync([.. sync, 0], Box, key, [writer.PublicKey]));
        Assert.Null(Envelope.OpenLink(link[..10], key));
    }

    [Fact]
    public void AToDeviceMessageOpensOnlyOnThatDevice()
    {
        using var writer = TestKeys.Signer();
        using var device = TestKeys.Decrypter();
        using var otherDevice = TestKeys.Decrypter();
        var envelope = Envelope.SealToDevice(Box, device.PublicKey, "the new sync key"u8, writer)!;
        Assert.Equal((Envelope.ToDeviceKind, 0u), Envelope.Header(envelope));
        Assert.Equal("the new sync key"u8.ToArray(), Envelope.OpenToDevice(envelope, Box, device, [writer.PublicKey])!.Data);
        Assert.Null(Envelope.OpenToDevice(envelope, Box, otherDevice, [writer.PublicKey]));
        Assert.Null(Envelope.OpenToDevice(envelope, Box.Reverse().ToArray(), device, [writer.PublicKey]));
        // A sync envelope isn't a to-device one, and the other way round; nor is a recovery kit (its epoch is its code's number).
        Assert.Null(Envelope.OpenSync(envelope, Box, new byte[32], [writer.PublicKey]));
        Assert.Null(Envelope.OpenToDevice(Envelope.SealToDevice(Box, device.PublicKey, "a kit"u8, writer, epoch: 1)!, Box, device, [writer.PublicKey]));
        var offCurve = (byte[])device.PublicKey.Clone();
        offCurve[10] ^= 1;
        Assert.Null(Envelope.SealToDevice(Box, offCurve, "x"u8, writer));
    }

    [Theory]
    [InlineData(0, 256)]
    [InlineData(252, 256)]
    [InlineData(253, 512)]
    [InlineData(508, 512)]
    [InlineData(509, 1024)]
    [InlineData(4092, 4096)]
    [InlineData(32764, 32768)]
    [InlineData(32765, 65385)]
    [InlineData(65381, 65385)]
    public void PaddingRoundsUpToTheNextClass(int dataBytes, int paddedBytes)
    {
        var data = RandomNumberGenerator.GetBytes(dataBytes);
        var padded = Envelope.Pad(data);
        Assert.Equal(paddedBytes, padded.Length);
        Assert.Equal(data, Envelope.Unpad(padded));
    }

    [Fact]
    public void TheLargestEnvelopeFitsTheMailboxAndBiggerBatchesAreRefused()
    {
        Assert.Equal(new[] { 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65385 }, Envelope.PadSizes);
        using var writer = TestKeys.Signer();
        using var device = TestKeys.Decrypter();
        var key = new byte[32];
        var most = new byte[Envelope.MaxData];
        Assert.Equal(65536, Envelope.SealToDevice(Box, device.PublicKey, most, writer)!.Length);
        Assert.True(Envelope.SealSync(Box, 0, key, most, writer).Length <= Envelope.MaxBytes);
        Assert.Throws<ArgumentException>(() => Envelope.Pad(new byte[Envelope.MaxData + 1]));
        Assert.Throws<ArgumentException>(() => Envelope.SealSync(Box, 0, key, new byte[Envelope.MaxData + 1], writer));
    }

    [Fact]
    public void APaddingWithJunkOrAWrongLengthIsRefused()
    {
        var padded = Envelope.Pad("abc"u8);
        padded[200] = 1;
        Assert.Null(Envelope.Unpad(padded));
        padded = Envelope.Pad("abc"u8);
        padded[3] = 253;
        Assert.Null(Envelope.Unpad(padded));
        Assert.Null(Envelope.Unpad(new byte[300]));
        Assert.Equal(Array.Empty<byte>(), Envelope.Unpad(new byte[256]));
        // The length is big-endian.
        Assert.Equal(new byte[] { 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c', 0 }, Envelope.Pad("abc"u8)[..8]);
        // Over-padded: 3 bytes fit 256, so 512 is a second spelling of the same message.
        var over = new byte[512];
        Envelope.Pad("abc"u8).CopyTo(over, 0);
        Assert.Null(Envelope.Unpad(over));
        Assert.Null(Envelope.Unpad(new byte[512]));
        over = new byte[512];
        over[2] = 1; // 256 bytes of data: 512 is the smallest that holds them with the length.
        Assert.Equal(new byte[256], Envelope.Unpad(over));
    }

    [Fact]
    public void ASyncBatchIsCanonicalAndOneAppsItemsEachNamedOnce()
    {
        var device = new string('a', 32);
        var batch = SyncBatch.Write("launcher",
        [
            new("wishlist", "steam:570", 1_761_000_000_000, device, new System.Text.Json.Nodes.JsonObject { ["store"] = "steam", ["appId"] = 570 }),
            new("wishlist", "steam:730", 1_761_000_000_500, device, null),
        ]);
        Assert.Equal(
            "{\"app\":\"launcher\",\"items\":[{\"body\":{\"appId\":570,\"store\":\"steam\"},\"device\":\"" + device + "\",\"id\":\"steam:570\",\"modified\":1761000000000,\"type\":\"wishlist\"}," +
            "{\"deleted\":true,\"device\":\"" + device + "\",\"id\":\"steam:730\",\"modified\":1761000000500,\"type\":\"wishlist\"}],\"v\":1}",
            System.Text.Encoding.UTF8.GetString(batch));
        var items = SyncBatch.Read(batch, "launcher")!;
        Assert.Equal(2, items.Count);
        Assert.Equal(570, items[0].Body!["appId"]!.GetValue<int>());
        Assert.Null(items[1].Body);

        // Another app's sub-key opened it, or it isn't byte for byte canonical: refused whole.
        Assert.Null(SyncBatch.Read(batch, "other"));
        Assert.Null(SyncBatch.Read([.. batch, (byte)' '], "launcher"));
        Assert.Null(SyncBatch.Read(System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(batch).Replace("\"v\":1", "\"v\": 1")), "launcher"));
        // Writing what a reader would refuse throws instead.
        Assert.Throws<ArgumentException>(() => SyncBatch.Write("launcher", []));
        Assert.Throws<ArgumentException>(() => SyncBatch.Write("launcher", [new("wishlist", "x", 1, device, 1), new("wishlist", "x", 2, device, 2)]));
        Assert.Throws<ArgumentException>(() => SyncBatch.Write("launcher", [new("Wishlist", "x", 1, device, 1)]));
        Assert.Throws<ArgumentException>(() => SyncBatch.Write("launcher", [new("wishlist", "a b", 1, device, 1)]));
        Assert.Throws<ArgumentException>(() => SyncBatch.Write("launcher", [new("wishlist", "x", -1, device, 1)]));
        Assert.Throws<ArgumentException>(() => SyncBatch.Write("launcher", [new("wishlist", "x", 1, device.ToUpperInvariant().Replace('A', 'F'), 1)]));
        Assert.Throws<ArgumentException>(() => SyncBatch.Write("launcher", [new("wishlist", "x", 1, device, 1.5)]));
    }

    [Fact]
    public void TheSizeOnTheWireSaysOnlyTheClass()
    {
        using var writer = TestKeys.Signer();
        var key = new byte[32];
        var short1 = Envelope.SealSync(Box, 0, key, "a"u8, writer);
        var long1 = Envelope.SealSync(Box, 0, key, new byte[250], writer);
        Assert.Equal(short1.Length, long1.Length);
        Assert.Equal(6 + 12 + 256 + 16 + 64, short1.Length);
    }
}

/// <summary>Keys held in memory for the identity tests, and a key store that keeps them there.</summary>
internal static class TestKeys
{
    internal static EcdsaSigner Signer() => new(ECDsa.Create(P256.Curve));

    internal static EcdhDecrypter Decrypter() => new(ECDiffieHellman.Create(P256.Curve));

    /// <summary>A PC's two keys in memory, counting how often anything asked for them to be made.</summary>
    internal sealed class Store : IDeviceKeyStore
    {
        private ECParameters? _identity, _encryption;

        internal int Creates { get; private set; }

        public DeviceKeys? Open() => _identity is { } i && _encryption is { } e
            ? new(new EcdsaSigner(ECDsa.Create(i)), new EcdhDecrypter(ECDiffieHellman.Create(e)))
            : null;

        public DeviceKeys OpenOrCreate()
        {
            if (_identity is null || _encryption is null)
            {
                Creates++;
                using var identity = ECDsa.Create(P256.Curve);
                using var encryption = ECDiffieHellman.Create(P256.Curve);
                (_identity, _encryption) = (identity.ExportParameters(true), encryption.ExportParameters(true));
            }
            return Open()!;
        }

        /// <summary>What a TPM clear does to the identity key.</summary>
        internal void LoseIdentity() => _identity = null;

        internal void ReplaceEncryption()
        {
            using var encryption = ECDiffieHellman.Create(P256.Curve);
            _encryption = encryption.ExportParameters(true);
        }
    }
}

using Exo.Identity;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the recovery code (plan §2.12) could fail the one time it's needed, after every PC is gone or one was stolen:
/// - the code as shown can't be read back (grouping, case, 0/1/8 typed for O/I/B), or a different code reads as it;
/// - the same code doesn't give the same box, signing and seal keys on another PC or app;
/// - a typo opens something, or the kit opens under another code, from another box, or after a byte changed;
/// - the kit loses or bends the Exo ID, the epoch, the sync key or who posted it, or takes a field it doesn't know;
/// - the kit's header carries the sync-key epoch (or nothing) instead of its code's number, so a kit for one code reads
///   as one for another;
/// - a PC can't post a kit for a new sync key without the code (then every removal would need a new code, and a stolen
///   PC could replace it);
/// - the box, signing and seal keys aren't independent of each other.
/// </summary>
public sealed class IdentityRecoveryTests
{
    private static readonly RecoveryCode.Kit Kit = new("AAISEM2EKVTHPCEZVK54ZXPO74", 3, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void TheCodeAsShownReadsBackToTheSameKeys()
    {
        var (text, keys) = RecoveryCode.New();
        Assert.Matches("^[A-Z2-7]{5}-[A-Z2-7]{5}-[A-Z2-7]{5}-[A-Z2-7]{5}-[A-Z2-7]{6}$", text);
        foreach (var typed in new[] { text, text.ToLowerInvariant(), text.Replace("-", " "), text.Replace("-", ""), text.Replace('O', '0').Replace('I', '1').Replace('B', '8') })
        {
            var again = RecoveryCode.Read(typed)!;
            Assert.Equal(keys.BoxId, again.BoxId);
            Assert.Equal(keys.SignScalar, again.SignScalar);
            Assert.Equal(keys.SealPublicKey, again.SealPublicKey);
        }
        Assert.Null(RecoveryCode.Read(text[..^1]));
        Assert.Null(RecoveryCode.Read(text + "A"));
        Assert.Null(RecoveryCode.Read(""));
    }

    [Fact]
    public void APcSealsTheKitWithoutTheCodeAndOnlyTheCodeOpensIt()
    {
        var (text, keys) = RecoveryCode.New();
        using var pc = TestKeys.Signer();
        // What a linked PC has: the entry's encPub from the device list and the box id. No code.
        var sealedKit = RecoveryCode.SealKit(Kit, 2, keys.SealPublicKey, keys.BoxId, pc)!;
        var opened = RecoveryCode.Read(text)!.OpenKit(sealedKit)!;
        // The header's epoch is the code's number, apart from the sync-key epoch the kit carries.
        Assert.Equal((Envelope.ToDeviceKind, 2u), Envelope.Header(sealedKit));
        Assert.Equal(2u, opened.CodeNumber);
        Assert.Equal(Kit.ExoId, opened.Kit.ExoId);
        Assert.Equal(Kit.Epoch, opened.Kit.Epoch);
        Assert.Equal(Kit.SyncKey, opened.Kit.SyncKey);
        Assert.Equal(Device.IdOf(pc.PublicKey), opened.By);
        // Who posted it is checked once the list is read: that PC's key, and no other.
        Assert.True(Envelope.SignedBy(sealedKit, pc.PublicKey));
        using var other = TestKeys.Signer();
        Assert.False(Envelope.SignedBy(sealedKit, other.PublicKey));

        Assert.Null(RecoveryCode.New().Keys.OpenKit(sealedKit));
        var typo = text[..^1] + (text[^1] == 'A' ? 'Q' : 'A');
        Assert.Null(RecoveryCode.Read(typo)?.OpenKit(sealedKit));
        for (var i = 0; i < sealedKit.Length - 64; i += 9)
        {
            var changed = (byte[])sealedKit.Clone();
            changed[i] ^= 1;
            Assert.Null(keys.OpenKit(changed));
        }
        Assert.Null(keys.OpenKit(sealedKit[..^1]));
        // Sealed for another box: the box id is in the additional data.
        var (_, elsewhere) = RecoveryCode.New();
        Assert.Null(keys.OpenKit(RecoveryCode.SealKit(Kit, 2, keys.SealPublicKey, elsewhere.BoxId, pc)!));
        // A header without a code number (0, the old form) is refused, and no code is number 0.
        Assert.Null(keys.OpenKit(Envelope.SealToDevice(Convert.FromHexString(keys.BoxId), keys.SealPublicKey, RecoveryCode.KitJson(Kit, Device.IdOf(pc.PublicKey)), pc)!));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecoveryCode.SealKit(Kit, 0, keys.SealPublicKey, keys.BoxId, pc));
        // The size says only the padding class.
        Assert.Equal(6 + 65 + 256 + 16 + 64, sealedKit.Length);
    }

    [Fact]
    public void AKitWithAFieldItDoesntKnowIsRefused()
    {
        var (_, keys) = RecoveryCode.New();
        using var pc = TestKeys.Signer();
        var json = System.Text.Encoding.UTF8.GetString(RecoveryCode.KitJson(Kit, Device.IdOf(pc.PublicKey)));
        foreach (var bad in new[] { json.Replace("\"v\":1", "\"more\":1,\"v\":1"), json.Replace("\"v\":1", "\"v\":2"), json.Replace("\"epoch\":3", "\"epoch\": 3") })
            Assert.Null(keys.OpenKit(Envelope.SealToDevice(Convert.FromHexString(keys.BoxId), keys.SealPublicKey, System.Text.Encoding.UTF8.GetBytes(bad), pc, epoch: 1)!));
        Assert.NotNull(keys.OpenKit(Envelope.SealToDevice(Convert.FromHexString(keys.BoxId), keys.SealPublicKey, System.Text.Encoding.UTF8.GetBytes(json), pc, epoch: 1)!));
    }

    [Fact]
    public void TheCodesKeysAreApartAndBoxesDontCollide()
    {
        var (_, keys) = RecoveryCode.New();
        Assert.NotEqual(keys.BoxScalar, keys.SignScalar);
        Assert.NotEqual(keys.BoxScalar, keys.SealScalar);
        Assert.NotEqual(keys.SignScalar, keys.SealScalar);
        Assert.Matches("^[0-9a-f]{32}$", keys.BoxId);
        using var box = keys.BoxKey();
        using var entry = keys.EntryKey();
        Assert.Equal(keys.BoxId, Mailbox.BoxId(box.PublicKey));
        Assert.NotEqual(keys.BoxId, Mailbox.BoxId(entry.PublicKey));
        Assert.NotEqual(keys.BoxId, RecoveryCode.New().Keys.BoxId);
        var record = Device.Read(keys.Entry(1))!;
        Assert.True(record.Recovery);
        Assert.Equal(keys.SealPublicKey, record.EncPub);
        Assert.Equal(entry.PublicKey, record.SigPub);
    }
}

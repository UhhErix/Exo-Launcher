using System.Security.Cryptography;
using Exo.Identity;
using L = Exo.Identity.LinkHandshake;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways linking a PC (plan §2.4) could go wrong without either screen showing it:
/// - the two PCs derive different link keys or match codes, so a real link always fails, or the same ones whatever
///   happens in between, so a man in the middle passes the comparison;
/// - the new PC sends its key before holding the existing PC's commitment, or takes a reveal that isn't what was
///   committed, so the middle can pick its key after seeing N's and grind the 6 digits;
/// - the QR's key hash isn't checked, or a malformed QR or nameplate is taken;
/// - the match code isn't 6 digits (leading zeros lost), or doesn't depend on the nameplate and both keys.
/// </summary>
public sealed class IdentityLinkTests
{
    private const string Nameplate = "48210937";

    [Fact]
    public void BothPcsDeriveTheSameKeyAndMatchCode()
    {
        using var n = new L.NewPc();
        using var e = new L.ExistingPc();
        var nKey = n.TakeCommitment(e.Commitment);
        var (reveal, eKeys) = e.TakeNewPcKey(nKey, Nameplate)!.Value;
        var nKeys = n.TakeReveal(reveal, Nameplate);
        Assert.NotNull(nKeys);
        Assert.Equal(eKeys.Key, nKeys.Key);
        Assert.Equal(eKeys.MatchCode, nKeys.MatchCode);
        Assert.Matches("^[0-9]{6}$", nKeys.MatchCode);
        Assert.Equal(32, nKeys.Key.Length);

        // What E sends under the key opens on N, and the other way.
        Assert.Equal("chain and sync key"u8.ToArray(), Envelope.OpenLink(Envelope.SealLink(eKeys.Key, "chain and sync key"u8), nKeys.Key));
        Assert.Equal("my record"u8.ToArray(), Envelope.OpenLink(Envelope.SealLink(nKeys.Key, "my record"u8), eKeys.Key));
    }

    [Fact]
    public void SomeoneInTheMiddleEndsUpWithOtherCodes()
    {
        // M talks to N as E and to E as N. It must commit to N before it sees N's key.
        using var n = new L.NewPc();
        using var e = new L.ExistingPc();
        using var mToN = new L.ExistingPc();
        using var mToE = new L.NewPc();
        var nKey = n.TakeCommitment(mToN.Commitment);
        var (mReveal, mnKeys) = mToN.TakeNewPcKey(nKey, Nameplate)!.Value;
        var nKeys = n.TakeReveal(mReveal, Nameplate)!;
        var meKey = mToE.TakeCommitment(e.Commitment);
        var (eReveal, eKeys) = e.TakeNewPcKey(meKey, Nameplate)!.Value;
        var meKeys = mToE.TakeReveal(eReveal, Nameplate)!;
        Assert.Equal(nKeys.MatchCode, mnKeys.MatchCode);
        Assert.Equal(eKeys.MatchCode, meKeys.MatchCode);
        // Random 6 digits each side: the same by chance once in a million.
        Assert.NotEqual(nKeys.Key, eKeys.Key);

        // And M can't swap in another key after committing.
        using var late = new L.ExistingPc();
        using var n2 = new L.NewPc();
        var n2Key = n2.TakeCommitment(mToN.Commitment);
        var (lateReveal, _) = late.TakeNewPcKey(n2Key, Nameplate)!.Value;
        Assert.Null(n2.TakeReveal(lateReveal, Nameplate));
    }

    [Fact]
    public void TheNewPcRevealsNothingOutOfTurn()
    {
        using var n = new L.NewPc();
        using var e = new L.ExistingPc();
        Assert.Throws<InvalidOperationException>(() => n.TakeReveal(e.PublicKey, Nameplate));
        n.TakeCommitment(e.Commitment);
        Assert.Throws<InvalidOperationException>(() => n.TakeCommitment(e.Commitment));
        Assert.Throws<ArgumentException>(() => new L.NewPc().TakeCommitment(new byte[31]));
    }

    [Fact]
    public void TheQrPinsTheNewPcsKey()
    {
        using var n = new L.NewPc();
        using var e = new L.ExistingPc();
        using var stranger = new L.NewPc();
        var qr = L.QrText(Nameplate, n.PublicKey);
        Assert.StartsWith("exo-link:1:48210937:", qr, StringComparison.Ordinal);
        var (nameplate, keyHash) = L.ReadQr(qr)!.Value;
        Assert.Equal(Nameplate, nameplate);
        Assert.Equal(SHA256.HashData(n.PublicKey), keyHash);
        Assert.Null(e.TakeNewPcKey(stranger.PublicKey, Nameplate, keyHash));
        Assert.NotNull(e.TakeNewPcKey(n.PublicKey, Nameplate, keyHash));
        foreach (var bad in new[] { "exo-link:2:48210937:" + qr[20..], "exo-link:1:4821093:" + qr[20..], "exo-link:1:4821093x:" + qr[20..], qr[..^2], "exo-link:1:48210937", qr + ":x" })
            Assert.Null(L.ReadQr(bad));
    }

    [Fact]
    public void TheTranscriptIsTheSpecs()
    {
        // Fixed keys, each value re-derived here from the spec's own words with .NET's HKDF.
        using var eN = P256.Agreement(SHA256.HashData("vectors eN"u8));
        using var eE = P256.Agreement(SHA256.HashData("vectors eE"u8));
        var nPub = P256.PublicKey(eN);
        var ePub = P256.PublicKey(eE);
        Assert.Equal(SHA256.HashData([.. "exo-link-commit"u8, .. ePub]), L.Commitment(ePub));
        var shared = P256.Agree(eN, ePub)!;
        Assert.Equal(shared, P256.Agree(eE, nPub));
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, shared, "48210937"u8.ToArray());
        var key = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, [.. "exo link v1"u8, .. nPub, .. ePub]);
        var sas = HKDF.Expand(HashAlgorithmName.SHA256, prk, 8, [.. "exo link sas"u8, .. nPub, .. ePub]);
        var keys = L.Derive(shared, Nameplate, nPub, ePub);
        Assert.Equal(key, keys.Key);
        Assert.Equal((System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(sas) % 1_000_000).ToString("D6"), keys.MatchCode);
        // Each input moves the code: the nameplate, and the order of the keys.
        Assert.NotEqual(keys.Key, L.Derive(shared, "48210938", nPub, ePub).Key);
        Assert.NotEqual(keys.Key, L.Derive(shared, Nameplate, ePub, nPub).Key);
        Assert.Throws<ArgumentException>(() => L.Derive(shared, "4821093", nPub, ePub));
    }

    [Fact]
    public void AMatchCodeKeepsItsLeadingZeros()
    {
        // Over many links some codes start with 0; every one is still 6 characters.
        var shared = new byte[32];
        for (var i = 0; i < 2000; i++)
        {
            shared[0] = (byte)i;
            shared[1] = (byte)(i >> 8);
            Assert.Equal(6, L.Derive(shared, Nameplate, [1], [2]).MatchCode.Length);
        }
    }
}

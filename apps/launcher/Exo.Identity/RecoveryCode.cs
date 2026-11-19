using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// The recovery code (plan §2.12): 128 random bits shown once as 26 base32 characters, stored nowhere. It is the
/// identity's root: its entry in the device list removes any device and no PC can remove or replace it, so a person who
/// loses every PC, or has one stolen, takes the identity back with the code. Everything comes from the code through
/// HKDF-SHA256 (Extract with salt "exo recovery v1", then Expand), each key a P-256 scalar (<see cref="P256.DeriveScalar"/>):
/// <list type="bullet">
/// <item>"box": the recovery box's key; it logs in to the mailbox box that keeps the kits;</item>
/// <item>"sign": the recovery entry's signing key (its <c>sigPub</c> in the device list);</item>
/// <item>"seal": the recovery entry's encryption key (its <c>encPub</c>): every new sync key is sealed to it.</item>
/// </list>
/// A kit is a kind 0x02 envelope (to one device) to the entry's <c>encPub</c>, mailbox id the recovery box's, signed by
/// the PC that posts it, holding the canonical JSON <c>{"by","epoch","exoId","syncKey","v":1}</c> (the sync key in
/// base64url without padding, <c>by</c> the posting PC's id). The envelope's header epoch is the box's own key counter,
/// the code's number (<see cref="DeviceList.CodeNumber"/>: 1 for the identity's first code), never the sync-key epoch the
/// kit carries. Any PC in the list can make one without the code; only the code opens it. Linked PCs keep the box key
/// (never the code), post a kit for every new sync key, and post it again before the mailbox's 30 days run out.
/// </summary>
public sealed class RecoveryCode
{
    public const int CodeBytes = 16, CodeChars = 26;
    private static readonly byte[] Salt = "exo recovery v1"u8.ToArray();

    /// <summary>What a kit carries: the identity and its sync key with that key's epoch.</summary>
    public sealed record Kit(string ExoId, uint Epoch, byte[] SyncKey);

    /// <summary>A kit opened: what it carries, the id of the PC that says it posted it (check with
    /// <see cref="Envelope.SignedBy"/> once the device list is read), and the code number in its header (check against
    /// <see cref="DeviceList.CodeNumber"/>).</summary>
    public sealed record OpenedKit(Kit Kit, string By, uint CodeNumber);

    private RecoveryCode(ReadOnlySpan<byte> code)
    {
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, code.ToArray(), Salt);
        BoxScalar = P256.DeriveScalar(prk, "box"u8);
        SignScalar = P256.DeriveScalar(prk, "sign"u8);
        SealScalar = P256.DeriveScalar(prk, "seal"u8);
        using var box = P256.Signer(BoxScalar);
        BoxId = Mailbox.BoxId(P256.PublicKey(box));
        using var seal = P256.Agreement(SealScalar);
        SealPublicKey = P256.PublicKey(seal);
    }

    public byte[] BoxScalar { get; }
    public byte[] SignScalar { get; }
    public byte[] SealScalar { get; }

    /// <summary>The recovery entry's <c>encPub</c>.</summary>
    public byte[] SealPublicKey { get; }

    /// <summary>The recovery box: 32 lowercase hex, as the mailbox names it.</summary>
    public string BoxId { get; }

    /// <summary>A new code: the text to show once, and its keys.</summary>
    public static (string Text, RecoveryCode Keys) New()
    {
        var code = RandomNumberGenerator.GetBytes(CodeBytes);
        try { return (Format(code), new(code)); }
        finally { CryptographicOperations.ZeroMemory(code); }
    }

    /// <summary>The keys of a typed code, or null when it isn't 26 base32 characters (spaces, hyphens and case aside).</summary>
    public static RecoveryCode? Read(string typed) =>
        Base32.Decode(typed) is { Length: CodeBytes } code ? new(code) : null;

    /// <summary>Five groups to copy by hand: ABCDE-FGHIJ-KLMNO-PQRST-UVWXYZ.</summary>
    public static string Format(ReadOnlySpan<byte> code)
    {
        var text = Base32.Encode(code);
        return string.Join('-', text[..5], text[5..10], text[10..15], text[15..20], text[20..]);
    }

    public EcdsaSigner BoxKey() => new(P256.Signer(BoxScalar));

    public EcdsaSigner EntryKey() => new(P256.Signer(SignScalar));

    public EcdhDecrypter SealKey() => new(P256.Agreement(SealScalar));

    /// <summary>This code's entry for the device list, signed by its own key: the recovery entry, or with
    /// <paramref name="account"/> the Exo account's (the same keys, from a secret the account keeps, plan §2.17).</summary>
    public JsonObject Entry(long created, string? name = null, bool account = false)
    {
        using var key = EntryKey();
        return Device.Create(key, SealPublicKey, name ?? (account ? "Exo account" : "Recovery code"), [], created, recovery: !account, account: account);
    }

    /// <summary>
    /// A kit, sealed by a PC of the list to the recovery entry's <c>encPub</c> for the box <paramref name="boxId"/>, its
    /// header epoch the entry's <paramref name="codeNumber"/> (<see cref="DeviceList.CodeNumber"/>); no code needed. Null
    /// when <paramref name="entryEncPub"/> isn't a key.
    /// </summary>
    public static byte[]? SealKit(Kit kit, uint codeNumber, ReadOnlySpan<byte> entryEncPub, string boxId, IDeviceSigner by, ECDiffieHellman? ephemeral = null)
    {
        if (codeNumber == 0) throw new ArgumentOutOfRangeException(nameof(codeNumber), "Codes are numbered from 1.");
        return Envelope.SealToDevice(Convert.FromHexString(boxId), entryEncPub, KitJson(kit, Device.IdOf(by.PublicKey)), by, ephemeral, codeNumber);
    }

    public static byte[] KitJson(Kit kit, string by) => CanonicalJson.Serialize(new JsonObject
    {
        ["v"] = 1,
        ["exoId"] = kit.ExoId,
        ["epoch"] = (long)kit.Epoch,
        ["syncKey"] = Base64Url.EncodeToString(kit.SyncKey),
        ["by"] = by,
    });

    /// <summary>The kit, or null when it wasn't sealed to this code's entry for its box (a typo reads as another code),
    /// was changed, has no code number in its header (0), or isn't exactly the kit's five fields in canonical form (an
    /// unknown field comes with a new v).</summary>
    public OpenedKit? OpenKit(ReadOnlySpan<byte> sealedKit)
    {
        using var seal = SealKey();
        if (Envelope.Header(sealedKit) is not (Envelope.ToDeviceKind, var codeNumber and > 0) ||
            Envelope.OpenToDeviceBeforeSender(sealedKit, Convert.FromHexString(BoxId), seal) is not { } json ||
            ReadKitJson(json) is not var (kit, by))
            return null;
        return new(kit, by, codeNumber);
    }

    /// <summary>
    /// A kit's canonical JSON read back: what it carries and the id of the PC that says it posted it; null unless it is
    /// exactly the five fields in canonical form. A new sync key sent to each remaining device after a removal carries
    /// the same JSON (<see cref="SyncKeyHandoff"/>).
    /// </summary>
    public static (Kit Kit, string By)? ReadKitJson(ReadOnlySpan<byte> json)
    {
        if (CanonicalJson.ParseObject(json) is not { Count: 5 } kit || Device.Int(kit, "v") != 1 ||
            Device.Text(kit, "exoId") is not { Length: CodeChars } exoId || Base32.Decode(exoId) is not { Length: 16 } exoIdBytes || Base32.Encode(exoIdBytes) != exoId ||
            Device.Int(kit, "epoch") is not { } epoch || epoch is < 0 or > uint.MaxValue ||
            Device.Text(kit, "by") is not { Length: 32 } by || !by.All(char.IsAsciiHexDigitLower) ||
            Device.Text(kit, "syncKey") is not { } syncKey || CanonicalJson.Bytes(syncKey) is not { Length: 32 } key ||
            !json.SequenceEqual(KitJson(new(exoId, (uint)epoch, key), by)))
            return null;
        return (new(exoId, (uint)epoch, key), by);
    }
}

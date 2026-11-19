using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// The files of <see cref="IdentityStore.Folder"/>, which every Exo app reads and writes
/// (plan §2.18), so a PC set up or linked in one app is set up in any other at once:
/// <list type="bullet">
/// <item><c>identity.json</c>: the Exo ID, this PC's device id and a hash of its encryption key (<see cref="ExoIdentity"/>); no secret.</item>
/// <item><c>sync-key.sealed</c>: the sync key and its epoch, sealed to this PC's encryption key (<see cref="SealSyncKey"/>).</item>
/// <item><c>device-list.json</c>: the device list's signed changes as they travel (<see cref="DeviceListFile"/>), checked again on every read.</item>
/// <item><c>recovery.sealed</c>: the recovery box's key a linked PC keeps (never the code), sealed the same way.</item>
/// <item><c>account.sealed</c>: this PC's Exo account session (<see cref="SealAccount"/>), so signing in in one app signs in any other.</item>
/// <item><c>profile-card.json</c>: what the profile shows anyone, plain, for an app to draw before its own sync lands:
/// {"v":1,"exoId","name","picture" (the still's data URL),"avatar" (the look's avatar media)}; written by whichever app
/// holds the profile, each part checked by the app reading it.</item>
/// <item><c>account-box.sealed</c>: the account box's key every PC keeps while account sign-in is on, sealed as the recovery box's.</item>
/// </list>
/// Each is written whole under <see cref="StateMutex"/>: to a temporary file, then moved over the old one. What each app
/// has read from the mailboxes (its cursors) stays in its own data, since each app reads its own items.
/// </summary>
public static class IdentityFiles
{
    public const string State = "identity.json", SyncKey = "sync-key.sealed", DeviceList = "device-list.json", Recovery = "recovery.sealed",
        Account = "account.sealed", AccountBox = "account-box.sealed", Card = "profile-card.json";

    /// <summary>Held while any app writes one of these files (and while it reads several that must agree).</summary>
    public const string StateMutex = @"Local\Exo.Identity.State";

    private static readonly byte[] SyncKeyInfo = "exo identity v1/sync key at rest"u8.ToArray();
    private static readonly byte[] RecoveryInfo = "exo identity v1/recovery box at rest"u8.ToArray();
    private static readonly byte[] AccountInfo = "exo identity v1/account at rest"u8.ToArray();
    private static readonly byte[] AccountBoxInfo = "exo identity v1/account box at rest"u8.ToArray();

    /// <summary>
    /// <c>sync-key.sealed</c>: HPKE (§2.1's suite) to this PC's encryption key, info "exo identity v1/sync key at rest", aad
    /// the Exo ID in UTF-8, plaintext the canonical JSON {"epoch":n,"key":"&lt;64 hex&gt;"}; the file is enc (65 bytes) then
    /// the ciphertext. Only this PC can open it, and only for this identity.
    /// </summary>
    public static byte[] SealSyncKey(ReadOnlySpan<byte> encPub, string exoId, long epoch, ReadOnlySpan<byte> key, ECDiffieHellman? ephemeral = null)
    {
        if (key.Length != 32) throw new ArgumentException("A sync key is 32 bytes.", nameof(key));
        if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        var plain = CanonicalJson.Serialize(new JsonObject { ["epoch"] = epoch, ["key"] = Convert.ToHexStringLower(key) });
        return Seal(encPub, SyncKeyInfo, exoId, plain, ephemeral);
    }

    /// <summary>The sync key and its epoch, or null when the file isn't this PC's sealed key for this identity.</summary>
    public static (long Epoch, byte[] Key)? OpenSyncKey(IDeviceDecrypter decrypter, string exoId, ReadOnlySpan<byte> file)
    {
        if (Open(decrypter, SyncKeyInfo, exoId, file) is not { } plain || CanonicalJson.ParseObject(plain) is not { Count: 2 } json) return null;
        if (Device.Int(json, "epoch") is not { } epoch || epoch < 0 || Device.Text(json, "key") is not { Length: 64 } hex) return null;
        byte[] key;
        try { key = Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
        // Only the canonical form: lowercase hex, the fields as written.
        return CanonicalJson.Serialize(json).AsSpan().SequenceEqual(plain) && hex == Convert.ToHexStringLower(key) ? (epoch, key) : null;
    }

    /// <summary><c>recovery.sealed</c>: the recovery box's 32-byte key, sealed as <see cref="SealSyncKey"/> seals the sync key (its own info).</summary>
    public static byte[] SealRecoveryKey(ReadOnlySpan<byte> encPub, string exoId, ReadOnlySpan<byte> boxKey, ECDiffieHellman? ephemeral = null)
    {
        if (boxKey.Length != 32) throw new ArgumentException("A recovery box key is 32 bytes.", nameof(boxKey));
        return Seal(encPub, RecoveryInfo, exoId, boxKey, ephemeral);
    }

    public static byte[]? OpenRecoveryKey(IDeviceDecrypter decrypter, string exoId, ReadOnlySpan<byte> file) =>
        Open(decrypter, RecoveryInfo, exoId, file) is { Length: 32 } key ? key : null;

    /// <summary><c>account-box.sealed</c>: the account box's 32-byte key, sealed as the recovery box's (its own info).</summary>
    public static byte[] SealAccountBoxKey(ReadOnlySpan<byte> encPub, string exoId, ReadOnlySpan<byte> boxKey, ECDiffieHellman? ephemeral = null)
    {
        if (boxKey.Length != 32) throw new ArgumentException("An account box key is 32 bytes.", nameof(boxKey));
        return Seal(encPub, AccountBoxInfo, exoId, boxKey, ephemeral);
    }

    public static byte[]? OpenAccountBoxKey(IDeviceDecrypter decrypter, string exoId, ReadOnlySpan<byte> file) =>
        Open(decrypter, AccountBoxInfo, exoId, file) is { Length: 32 } key ? key : null;

    /// <summary>
    /// <c>account.sealed</c>: the session as <see cref="AccountClient.SessionJson"/> writes it, sealed as the sync key is
    /// (its own info), its aad this PC's device id: a PC can be signed in before it has an identity.
    /// </summary>
    public static byte[] SealAccount(ReadOnlySpan<byte> encPub, string deviceId, AccountClient.Session session, ECDiffieHellman? ephemeral = null) =>
        Seal(encPub, AccountInfo, deviceId, CanonicalJson.Serialize(AccountClient.SessionJson(session)), ephemeral);

    public static AccountClient.Session? OpenAccount(IDeviceDecrypter decrypter, string deviceId, ReadOnlySpan<byte> file) =>
        Open(decrypter, AccountInfo, deviceId, file) is { } plain && CanonicalJson.ParseObject(plain) is { } json ? AccountClient.ReadSession(json) : null;

    /// <summary>
    /// <c>device-list.json</c>: the canonical JSON {"changes":[…],"exoId":"…","v":1}, the changes as they travel, oldest
    /// first. Its reader loads them through <see cref="DeviceList.Load"/>, so a file edited by hand is refused, not believed.
    /// </summary>
    public static byte[] DeviceListFile(DeviceList list) =>
        CanonicalJson.Serialize(new JsonObject
        {
            ["v"] = 1,
            ["exoId"] = list.ExoId,
            ["changes"] = new JsonArray([.. list.Changes.Select(change => (JsonNode)change.DeepClone())]),
        });

    public static DeviceList? ReadDeviceList(ReadOnlySpan<byte> file)
    {
        if (CanonicalJson.ParseObject(file) is not { Count: 3 } json || Device.Int(json, "v") != 1 || Device.Text(json, "exoId") is not { } exoId ||
            json["changes"] is not JsonArray changes || changes.Any(change => change is not JsonObject))
            return null;
        var list = Exo.Identity.DeviceList.Load([.. changes.Select(change => (JsonObject)change!.DeepClone())]);
        return list is not null && list.ExoId == exoId ? list : null;
    }

    private static byte[] Seal(ReadOnlySpan<byte> encPub, byte[] info, string exoId, ReadOnlySpan<byte> plain, ECDiffieHellman? ephemeral)
    {
        var (enc, context) = Hpke.SetupSender(encPub, info, ephemeral) ?? throw new ArgumentException("Not a P-256 public key.", nameof(encPub));
        return [.. enc, .. context.Seal(Encoding.UTF8.GetBytes(exoId), plain)];
    }

    private static byte[]? Open(IDeviceDecrypter decrypter, byte[] info, string exoId, ReadOnlySpan<byte> file)
    {
        if (file.Length <= Hpke.EncBytes + Hpke.TagBytes) return null;
        var context = Hpke.SetupRecipient(decrypter, file[..Hpke.EncBytes], info);
        return context?.Open(Encoding.UTF8.GetBytes(exoId), file[Hpke.EncBytes..]);
    }
}

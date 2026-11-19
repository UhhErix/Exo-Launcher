using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// A device in an Exo identity (plan §2.3): <c>{v:1, id, name, created, sigPub, encPub, apps, sig}</c>, signed by its own
/// identity key, so a record can't name keys its device doesn't hold. <c>id</c> is the first 16 bytes of SHA-256 of
/// <c>sigPub</c> in lowercase hex; keys are base64url SEC1 points. The recovery code's entry has <c>recovery: true</c>;
/// its <c>encPub</c> is the code's seal key, which every new sync key is sealed to (the kit, plan §2.12). The Exo account's
/// entry (plan §2.17) has <c>account: true</c> and is made the same way from a secret the account keeps; never both.
/// </summary>
public sealed record Device(string Id, string Name, long Created, byte[] SigPub, byte[] EncPub, IReadOnlyList<string> Apps, bool Recovery, bool Account = false)
{
    /// <summary>The recovery entry or the account entry: not a PC.</summary>
    public bool Entry => Recovery || Account;

    public const string Label = "exo-device/1";
    public const int NameChars = 64, MaxApps = 8;

    public static string IdOf(ReadOnlySpan<byte> sigPub) => Convert.ToHexStringLower(SHA256.HashData(sigPub).AsSpan(0, 16));

    /// <summary>The Exo ID: the first 16 bytes of SHA-256 of the first device's identity key, as 26 base32 characters.</summary>
    public static string ExoIdOf(ReadOnlySpan<byte> sigPub) => Base32.Encode(SHA256.HashData(sigPub).AsSpan(0, 16));

    public static JsonObject Create(IDeviceSigner key, byte[]? encPub, string name, IEnumerable<string> apps, long created, bool recovery = false, bool account = false)
    {
        if (recovery && account) throw new ArgumentException("An entry is the recovery entry or the account entry, not both.", nameof(account));
        if (!NameOk(name)) throw new ArgumentException("A device name is 1 to 64 characters of visible text, with no space at either end.", nameof(name));
        if (encPub is not { Length: P256.PublicKeyBytes }) throw new ArgumentException("Every entry has an encryption key, the recovery entry too.", nameof(encPub));
        var record = new JsonObject
        {
            ["v"] = 1,
            ["id"] = IdOf(key.PublicKey),
            ["name"] = name,
            ["created"] = created,
            ["sigPub"] = Base64Url.EncodeToString(key.PublicKey),
            ["encPub"] = Base64Url.EncodeToString(encPub),
            ["apps"] = new JsonArray([.. apps.Select(app => (JsonNode)app)]),
        };
        if (recovery) record["recovery"] = true;
        if (account) record["account"] = true;
        return CanonicalJson.Sign(record, Label, key);
    }

    /// <summary>The device, or null unless every field is well-formed and (when <paramref name="check"/>) its own key
    /// signed it. Unknown fields are refused: a new one comes with a new <c>v</c>.</summary>
    public static Device? Read(JsonNode? node, bool check = true)
    {
        if (node is not JsonObject record || Int(record, "v") != 1 || Text(record, "id") is not { } id || Text(record, "name") is not { } name ||
            !NameOk(name) || Int(record, "created") is not { } created || created < 0 || Key(record, "sigPub") is not { } sigPub ||
            IdOf(sigPub) != id || Key(record, "encPub") is not { } encPub || record["apps"] is not JsonArray list || list.Count > MaxApps)
            return null;
        // Only true is written; a false flag would be a second spelling of the same record.
        var recovery = record["recovery"] is JsonValue flag && flag.GetValueKind() == System.Text.Json.JsonValueKind.True;
        if (record.ContainsKey("recovery") && !recovery) return null;
        var account = record["account"] is JsonValue accountFlag && accountFlag.GetValueKind() == System.Text.Json.JsonValueKind.True;
        if (record.ContainsKey("account") && !account || recovery && account) return null;
        List<string> apps = [];
        foreach (var app in list)
        {
            if (app is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length is 0 or > 16 || !text.All(c => c is >= 'a' and <= 'z' or '-')) return null;
            apps.Add(text);
        }
        var known = new HashSet<string> { "v", "id", "name", "created", "sigPub", "encPub", "apps", "recovery", "account", "sig" };
        if (record.Any(p => !known.Contains(p.Key))) return null;
        return !check || CanonicalJson.Verify(record, Label, sigPub) ? new(id, name, created, sigPub, encPub, apps, recovery, account) : null;
    }

    /// <summary>
    /// A device name (plan §2.13): 1 to 64 Unicode scalar values (an emoji is one, whatever its UTF-16 or UTF-8 length),
    /// no white space first or last, and nothing invisible: Unicode's default-ignorable code points and the controls are
    /// refused, except the three that emoji are made of, each only where an emoji needs it (a zero-width joiner between
    /// two pictographs, a variation selector after one, tag characters inside a subdivision flag). Lists, not a runtime's
    /// Unicode tables, so every app draws the line in the same place.
    /// </summary>
    public static bool NameOk(string name)
    {
        var scalars = new List<int>(name.Length);
        for (var rest = name.AsSpan(); !rest.IsEmpty;)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != OperationStatus.Done) return false;
            scalars.Add(rune.Value);
            rest = rest[used..];
        }
        if (scalars.Count is 0 or > NameChars || Space(scalars[0]) || Space(scalars[^1])) return false;
        for (var i = 0; i < scalars.Count; i++)
        {
            var before = i > 0 ? scalars[i - 1] : -1;
            var after = i + 1 < scalars.Count ? scalars[i + 1] : -1;
            var ok = scalars[i] switch
            {
                0x200D => (Pictograph(before) || before == 0xFE0F) && Pictograph(after),
                0xFE0E or 0xFE0F => Pictograph(before) || Keycap(before),
                >= 0xE0020 and <= 0xE007E => (before == 0x1F3F4 || Tag(before)) && (Tag(after) || after == 0xE007F),
                0xE007F => Tag(before),
                var c => !Invisible(c),
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Refused anywhere in a name: the controls, the line and paragraph separators, and Unicode's
    /// Default_Ignorable_Code_Point (bidi controls, zero-width characters, fillers, variation selectors, tags). U+200D,
    /// U+FE0E, U+FE0F and U+E0020 to U+E007F are among them but go through <see cref="NameOk"/>'s emoji rules first.</summary>
    public static bool Invisible(int c) => c is <= 0x1F or (>= 0x7F and <= 0x9F) or 0xAD or 0x34F or 0x61C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or
        (>= 0x180B and <= 0x180F) or (>= 0x200B and <= 0x200F) or 0x2028 or 0x2029 or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F) or
        0x3164 or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF or 0xFFA0 or (>= 0xFFF0 and <= 0xFFF8) or (>= 0x1BCA0 and <= 0x1BCA3) or
        (>= 0x1D173 and <= 0x1D17A) or (>= 0xE0000 and <= 0xE0FFF);

    // Unicode's White_Space less what Invisible already takes.
    private static bool Space(int c) => c is 0x20 or 0xA0 or 0x1680 or (>= 0x2000 and <= 0x200A) or 0x202F or 0x205F or 0x3000;

    // Where emoji live: what a zero-width joiner may join, and what a variation selector may follow.
    private static bool Pictograph(int c) => c is 0x203C or 0x2049 or (>= 0x2100 and <= 0x2BFF) or 0x3030 or 0x303D or 0x3297 or 0x3299 or (>= 0x1F000 and <= 0x1FAFF);

    // A keycap's first character (#, *, 0 to 9) and the two signs with an emoji form.
    private static bool Keycap(int c) => c is 0x23 or 0x2A or (>= 0x30 and <= 0x39) or 0xA9 or 0xAE;

    private static bool Tag(int c) => c is >= 0xE0020 and <= 0xE007E;

    public static string? Text(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? Int(JsonObject obj, string key) => obj[key] is JsonValue v ? CanonicalJson.Integer(v) : null;

    private static byte[]? Key(JsonObject obj, string key) =>
        Text(obj, key) is { } text && CanonicalJson.Bytes(text) is { Length: P256.PublicKeyBytes } bytes && bytes[0] == 0x04 ? bytes : null;
}

/// <summary>
/// Who an Exo identity is: the append-only chain of changes to its device list (plan §2.3),
/// <c>{v:1, exoId, seq, prev, op, device, by, at, sig}</c>, each signed by a device already in the list (the first by
/// itself) and naming the change before it by <see cref="CanonicalJson.Hash"/> (its canonical form without its own
/// <c>sig</c>; <c>""</c> for the first). <c>op</c> is <c>add</c> (<c>device</c> = the new device's signed record),
/// <c>remove</c> (<c>device</c> = its id) or <c>rename</c> (<c>device</c> = <c>{id, name}</c>).
///
/// What each may sign (plan §2.12, §2.13, §2.16, §2.17): a PC adds a PC, adds the recovery entry or the account entry
/// when there is none, removes a PC (itself included: it leaves the identity) or the account entry (account sign-in off),
/// and renames a PC; the recovery entry adds a PC, removes any device (itself included: recovery off), and adds a new
/// code's entry, which takes its place. Only the recovery entry removes itself, and no one renames it, so the code can
/// always take the list back from a PC that was stolen. The account entry only adds PCs, and a PC it adds is restricted
/// (<see cref="IsRestricted"/>), as is any PC a restricted PC adds: it removes and renames only itself and restricted PCs
/// added after it, never a PC the owner brought in, and adds no entry. So whoever takes over the account's Google, Apple
/// or email can join, never lock the owner out. One exception: with no unrestricted PC and no recovery entry listed (the
/// owner signed out of their last PC), the account entry's add is the owner coming back and isn't restricted.
/// Restricted is read from the chain as merged, never from what a PC signed: a fork can change it.
///
/// The chain is a function of the set of changes a device has seen, whatever order they came in: from the first
/// change on, of the changes made after the head, <see cref="Winner"/> picks the one that stays. Every change seen is
/// kept (a fork's losers and the branches behind them too), so a later arrival that changes a fork's outcome replays
/// the chain from there. A losing branch's author signs it again on top (<see cref="Resign"/>), in rank order. An
/// author that signed two different changes after the same change (an equivocation) has both dropped and is marked
/// (<see cref="Equivocators"/>); it stays in the list.
/// </summary>
public sealed class DeviceList
{
    public const string ChangeLabel = "exo-change/1";
    private readonly List<JsonObject> _changes = [];
    private readonly List<string> _hashes = [];
    private readonly Dictionary<string, Device> _devices = [];
    // PCs the account entry brought in, or a PC it brought in did, however far down.
    private readonly HashSet<string> _restricted = [];
    // Every change taken and checked, on the chain or not, by hash; and the hashes of the changes made after each.
    private readonly Dictionary<string, JsonObject> _seen = [];
    private readonly Dictionary<string, List<string>> _after = [];

    private DeviceList(string exoId) => ExoId = exoId;

    public enum Result { Appended, Known, Kept, Replaced, Gap, Invalid }

    /// <param name="Dropped">With <see cref="Result.Replaced"/>: the changes no longer on the chain, in order, for their
    /// authors to sign again (<see cref="Resign"/>, which passes over equivocations).</param>
    public sealed record Taken(Result Result, IReadOnlyList<JsonObject> Dropped);

    public string ExoId { get; }
    public IReadOnlyList<JsonObject> Changes => _changes;
    public IReadOnlyCollection<Device> Devices => _devices.Values;
    public string Head => _hashes.Count == 0 ? "" : _hashes[^1];

    public Device? Find(string id) => _devices.GetValueOrDefault(id);

    /// <summary>
    /// The devices that signed two or more different changes after the same change (so with the same seq), anywhere in
    /// what this device has seen: every such change is out (plan §2.16). A function of the set, never of arrival order.
    /// The device stays listed, for the person to remove (the recovery entry or another PC can), and its next change
    /// after a later head counts as any other.
    /// </summary>
    public IReadOnlyList<string> Equivocators =>
        [.. _after.Values.SelectMany(next => Twice(next.Select(hash => _seen[hash]))).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>
    /// The recovery code's number (plan §2.12): 1 for the identity's first code, one more for each code after it (one
    /// replacing itself, or a PC adding one after recovery was turned off). A kit's envelope carries it as its header
    /// epoch, apart from the sync-key epoch inside the kit. Null when recovery is off.
    /// </summary>
    public uint? CodeNumber => _devices.Values.Any(d => d.Recovery)
        ? (uint)_changes.Count(c => Device.Text(c, "op") == "add" && c["device"] is JsonObject record && record["recovery"] is not null)
        : null;

    /// <summary>The account entry's number, as <see cref="CodeNumber"/> is the recovery code's: one more for each account
    /// entry ever added; its kits carry it. Null while account sign-in is off.</summary>
    public uint? AccountNumber => _devices.Values.Any(d => d.Account)
        ? (uint)_changes.Count(c => Device.Text(c, "op") == "add" && c["device"] is JsonObject record && record["account"] is not null)
        : null;

    /// <summary>A PC that came in through the account entry, or through a PC that did: it may remove and rename only
    /// itself and restricted PCs added after it, and add no entry.</summary>
    public bool IsRestricted(string id) => _restricted.Contains(id);

    /// <summary>A new identity: the first PC adds itself; its key names the Exo ID.</summary>
    public static DeviceList Start(IDeviceSigner first, JsonObject record, long at)
    {
        var list = new DeviceList(Device.ExoIdOf(first.PublicKey));
        if (!list.Append(list.Change(first, "add", record, at))) throw new ArgumentException("The first record isn't this key's own PC record.", nameof(record));
        return list;
    }

    /// <summary>The list from its whole chain, each change checked in order; null when any fails.</summary>
    public static DeviceList? Load(IReadOnlyList<JsonObject> changes)
    {
        if (changes.Count == 0 || Device.Text(changes[0], "exoId") is not { } exoId) return null;
        var list = new DeviceList(exoId);
        return changes.All(change => list.Append((JsonObject)change.DeepClone())) ? list : null;
    }

    /// <summary>
    /// <paramref name="changes"/> in an order <paramref name="list"/> can take them in (they arrive as profile items, in
    /// any order): by <c>seq</c>, and one that comes after a change still to come tried again once that one is in. One
    /// whose <c>prev</c> never turns up is left out. Every Exo app takes the profile's device changes in this order.
    /// </summary>
    public static IEnumerable<JsonObject> InTakeOrder(DeviceList list, IEnumerable<JsonObject> changes)
    {
        var copy = Load(list.Changes)!;
        var pending = changes.OrderBy(c => Device.Int(c, "seq")).ToList();
        var progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            foreach (var change in pending.ToList())
            {
                if (copy.Take(change).Result == Result.Gap) continue;
                pending.Remove(change);
                progress = true;
                yield return change;
            }
        }
    }

    public JsonObject Add(IDeviceSigner by, JsonObject record, long at) => Make(by, "add", record, at);

    public JsonObject Remove(IDeviceSigner by, string id, long at) => Make(by, "remove", id, at);

    public JsonObject Rename(IDeviceSigner by, string id, string name, long at) => Make(by, "rename", new JsonObject { ["id"] = id, ["name"] = name }, at);

    /// <summary>A change from another device. Checked against the list as it stood after its <c>prev</c>, kept, and the
    /// chain decided again from that point.</summary>
    public Taken Take(JsonObject change)
    {
        if (Device.Text(change, "exoId") != ExoId || Device.Text(change, "prev") is not { } prev || HashOf(change) is not { } hash) return new(Result.Invalid, []);
        if (_seen.ContainsKey(hash)) return new(Result.Known, []);
        // After a change this device hasn't seen: the sender has changes this chain is missing.
        if (prev.Length > 0 && !_seen.ContainsKey(prev)) return new(Result.Gap, []);
        change = (JsonObject)change.DeepClone();
        // Nothing else after the head yet. (What waits there is only ever an equivocation, which the head stayed before.)
        if (prev == Head && Candidates(Head).Count == 0) return new(Append(change) ? Result.Appended : Result.Invalid, []);
        if (!StateAfter(prev).Append(change)) return new(Result.Invalid, []);
        Record(change, hash);
        // After a change off the chain: a branch that already lost, so nothing here moves.
        var at = prev.Length == 0 ? 0 : _hashes.IndexOf(prev) + 1;
        if (at == 0 && prev.Length > 0) return new(Result.Kept, []);
        var old = _changes.ToList();
        var oldHashes = _hashes.ToList();
        Replay(at);
        Extend();
        var same = at;
        while (same < oldHashes.Count && same < _hashes.Count && _hashes[same] == oldHashes[same]) same++;
        if (same == oldHashes.Count) return new(_hashes.Count > same ? Result.Appended : Result.Kept, []);
        return new(Result.Replaced, old.Skip(same).ToList());
    }

    /// <summary>
    /// Plan §2.13's fork rule: of changes made at once after the same head (this list as it is), the one that stays, or
    /// null when none does. It depends only on the set, not on the order the changes came in. First (pass 0, §2.16), an
    /// author with two or more different changes in the set signed them at once, an equivocation: all of them are out.
    /// Then:
    /// <list type="number">
    /// <item>The removes left are walked from the oldest author: the recovery entry first, then each PC by the seq of its
    /// own add (each author has one change here now). A remove whose author is already struck is passed over; any other
    /// strikes the device it removes (a device removing itself strikes nothing).</item>
    /// <item>Every change by a struck device is out. So a remove beats its target's concurrent changes, and when two
    /// devices remove each other the one added earlier wins (the recovery entry before any PC).</item>
    /// <item>The lowest <see cref="CanonicalJson.Hash"/> of what's left wins.</item>
    /// </list>
    /// Past pass 0 something is always left: the author of the last remove that struck was never struck after it, and
    /// with no strike nothing is out. Only when every change is an equivocation does none win, and the head stays. Only
    /// step 3 can an author steer, by changing its own content until its hash is low, and there neither outcome takes
    /// anything from anyone.
    /// </summary>
    public JsonObject? Winner(IReadOnlyList<JsonObject> candidates)
    {
        var twice = Twice(candidates).ToHashSet();
        var fair = candidates.Where(c => !twice.Contains(Device.Text(c, "by")!)).ToList();
        var struck = new HashSet<string>();
        foreach (var remove in fair.Where(c => Device.Text(c, "op") == "remove")
                     .OrderBy(c => Rank(Device.Text(c, "by")!)).ThenBy(CanonicalJson.Hash, StringComparer.Ordinal))
        {
            var (by, target) = (Device.Text(remove, "by")!, Device.Text(remove, "device")!);
            if (!struck.Contains(by) && target != by) struck.Add(target);
        }
        var left = fair.Where(c => !struck.Contains(Device.Text(c, "by")!)).ToList();
        return left.Count == 0 ? null : left.MinBy(CanonicalJson.Hash, StringComparer.Ordinal);
    }

    /// <summary>The authors with two or more different changes among <paramref name="changes"/> (all after one change).</summary>
    private static IEnumerable<string> Twice(IEnumerable<JsonObject> changes) =>
        changes.DistinctBy(CanonicalJson.Hash).GroupBy(c => Device.Text(c, "by")!).Where(g => g.Count() > 1).Select(g => g.Key);

    /// <summary>
    /// Changes that lost forks, in the order they are signed again (plan §2.13, §2.16): a fork's losers by their
    /// author's rank at the fork (the recovery entry first, then each PC by the seq of its own add), each followed by the
    /// branch behind it in order; forks further back first. Equivocations are left out: they are never signed again.
    /// </summary>
    public IReadOnlyList<JsonObject> ResignOrder(IEnumerable<JsonObject> lost)
    {
        var all = lost.DistinctBy(CanonicalJson.Hash).ToList();
        var byHash = all.ToDictionary(CanonicalJson.Hash);
        string RootOf(JsonObject change)
        {
            var hash = CanonicalJson.Hash(change);
            while (byHash.TryGetValue(Device.Text(byHash[hash], "prev")!, out var before)) hash = CanonicalJson.Hash(before);
            return hash;
        }
        var branches = all.Where(change => !Equivocated(change, all)).GroupBy(RootOf).ToDictionary(g => g.Key, g => g.ToList());
        var roots = branches.Keys.Select(hash => byHash[hash])
            .OrderBy(root => Device.Int(root, "seq"))
            .ThenBy(root => Device.Text(root, "prev") is { } prev && (prev.Length == 0 || _seen.ContainsKey(prev)) ? StateAfter(prev).Rank(Device.Text(root, "by")!) : int.MaxValue)
            .ThenBy(CanonicalJson.Hash, StringComparer.Ordinal);
        return [.. roots.SelectMany(root => branches[CanonicalJson.Hash(root)].OrderBy(change => Device.Int(change, "seq")).ThenBy(CanonicalJson.Hash, StringComparer.Ordinal))];
    }

    /// <summary>Whether another change by the same author after the same change is among <paramref name="also"/> or
    /// what this device has seen.</summary>
    private bool Equivocated(JsonObject change, IEnumerable<JsonObject> also)
    {
        var (hash, by, prev) = (CanonicalJson.Hash(change), Device.Text(change, "by"), Device.Text(change, "prev"));
        bool Other(JsonObject x) => Device.Text(x, "by") == by && Device.Text(x, "prev") == prev && CanonicalJson.Hash(x) != hash;
        return also.Any(Other) || prev is not null && _after.TryGetValue(prev, out var next) && next.Any(h => Other(_seen[h]));
    }

    /// <summary>
    /// What <paramref name="by"/> signs again of changes that lost forks, on top of the chain, walking them in
    /// <see cref="ResignOrder"/>: each of its own with the same <c>op</c>, <c>device</c> and <c>at</c> (the time the
    /// person acted), the next <c>seq</c> and the head as <c>prev</c>. One after another: at another author's change that
    /// still applies it stops, since that author goes first; call again once that change is in. A change that no longer
    /// applies is passed over: its author was removed, the device it adds is in, the one it removes or renames is gone,
    /// its author may no longer sign it, or the same action (by, op, device, at) is already in the chain. An equivocation
    /// is never signed again.
    /// </summary>
    public IReadOnlyList<JsonObject> Resign(IEnumerable<JsonObject> lost, IDeviceSigner by)
    {
        var id = Device.IdOf(by.PublicKey);
        var made = new List<JsonObject>();
        foreach (var change in ResignOrder(lost))
        {
            if (Device.Text(change, "by") is not { } author || Device.Text(change, "op") is not { } op || change["device"] is not { } device ||
                Device.Int(change, "at") is not { } at || _changes.Any(x => SameAction(x, change)))
                continue;
            // Tried unsigned first, so one that no longer applies costs no signature (tens of ms on a TPM).
            var draft = Draft(author, op, device, at);
            draft["sig"] = "";
            if (!StateAfter(Head).Apply(draft, check: false)) continue;
            if (author != id || Candidates(Head).Any(x => Device.Text(x, "by") == id)) break;
            var again = Change(by, op, device, at);
            if (Take(again).Result == Result.Invalid) break;
            made.Add(again);
            if (Head != CanonicalJson.Hash(again)) break;
        }
        return made;
    }

    private static bool SameAction(JsonObject x, JsonObject y) =>
        Device.Text(x, "by") == Device.Text(y, "by") && Device.Text(x, "op") == Device.Text(y, "op") && Device.Int(x, "at") == Device.Int(y, "at") &&
        CanonicalJson.Serialize(x["device"]!).AsSpan().SequenceEqual(CanonicalJson.Serialize(y["device"]!));

    /// <summary>How early a device came in: the recovery entry before every PC, then each PC by the seq of its own add.</summary>
    public int Rank(string id)
    {
        if (!_devices.TryGetValue(id, out var device)) return int.MaxValue;
        if (device.Recovery) return -1;
        for (var seq = _changes.Count - 1; seq >= 0; seq--)
            if (Device.Text(_changes[seq], "op") == "add" && _changes[seq]["device"] is JsonObject record && Device.Text(record, "id") == id) return seq;
        return int.MaxValue;
    }

    private JsonObject Make(IDeviceSigner by, string op, JsonNode device, long at)
    {
        // A second change of this device's after the same head would be an equivocation: both would be dropped.
        if (Candidates(Head).Any(x => Device.Text(x, "by") == Device.IdOf(by.PublicKey)))
            throw new InvalidOperationException("This device already signed a change after the head.");
        var change = Change(by, op, device, at);
        if (Take(change).Result != Result.Appended) throw new InvalidOperationException($"This {op} doesn't apply to the list as it is.");
        return change;
    }

    private JsonObject Change(IDeviceSigner by, string op, JsonNode device, long at) =>
        CanonicalJson.Sign(Draft(Device.IdOf(by.PublicKey), op, device, at), ChangeLabel, by);

    private JsonObject Draft(string by, string op, JsonNode device, long at) => new()
    {
        ["v"] = 1,
        ["exoId"] = ExoId,
        ["seq"] = _changes.Count,
        ["prev"] = Head,
        ["op"] = op,
        ["device"] = device.DeepClone(),
        ["by"] = by,
        ["at"] = at,
    };

    private static string? HashOf(JsonObject change)
    {
        try { return CanonicalJson.Hash(change); }
        catch (FormatException) { return null; }
    }

    private IReadOnlyList<JsonObject> Candidates(string head) => _after.TryGetValue(head, out var next) ? [.. next.Select(h => _seen[h])] : [];

    /// <summary>A copy of the list as it stood right after <paramref name="hash"/>, which may be off the chain.</summary>
    private DeviceList StateAfter(string hash)
    {
        var path = new List<JsonObject>();
        for (var at = hash; at.Length > 0; at = Device.Text(_seen[at], "prev")!) path.Add(_seen[at]);
        path.Reverse();
        var state = new DeviceList(ExoId);
        state.Replay(path);
        return state;
    }

    /// <summary>The chain cut back to its first <paramref name="count"/> changes.</summary>
    private void Replay(int count) => Replay(_changes.Take(count).ToList());

    /// <summary>The chain rebuilt from changes already checked.</summary>
    private void Replay(IReadOnlyList<JsonObject> changes)
    {
        _changes.Clear();
        _hashes.Clear();
        _devices.Clear();
        _restricted.Clear();
        foreach (var change in changes)
            if (!Apply(change, check: false)) throw new InvalidOperationException("A checked change no longer applies.");
    }

    /// <summary>From the head on: of the changes seen after it, the winner, until none is left (or none wins).</summary>
    private void Extend()
    {
        while (Candidates(Head) is { Count: > 0 } next && Winner(next) is { } winner)
            if (!Apply(winner, check: false)) throw new InvalidOperationException("A checked change no longer applies.");
    }

    /// <summary>A change after the head, checked, applied and remembered; false leaves everything as it was.</summary>
    private bool Append(JsonObject change)
    {
        if (!Apply(change, check: true)) return false;
        Record(change, _hashes[^1]);
        return true;
    }

    private void Record(JsonObject change, string hash)
    {
        _seen[hash] = change;
        var prev = Device.Text(change, "prev")!;
        if (!_after.TryGetValue(prev, out var next)) _after[prev] = next = [];
        next.Add(hash);
    }

    /// <summary>Checks the change against the list as it is (its signatures too, when <paramref name="check"/>) and
    /// applies it; false leaves the list unchanged.</summary>
    private bool Apply(JsonObject change, bool check)
    {
        var seq = _changes.Count;
        if (Device.Int(change, "v") != 1 || Device.Text(change, "exoId") != ExoId || Device.Int(change, "seq") != seq ||
            Device.Text(change, "prev") != Head || Device.Int(change, "at") is not >= 0 ||
            Device.Text(change, "by") is not { } by || Device.Text(change, "op") is not { } op || change.Count != 9)
            return false;
        var added = op == "add" ? Device.Read(change["device"], check) : null;
        var signer = seq == 0 ? added : _devices.GetValueOrDefault(by);
        if (signer is null || signer.Id != by || check && !CanonicalJson.Verify(change, ChangeLabel, signer.SigPub)) return false;
        if (seq == 0 && (added!.Entry || Device.ExoIdOf(added.SigPub) != ExoId)) return false;
        var restricted = _restricted.Contains(signer.Id);
        // A restricted PC reaches only itself and restricted PCs added after it: never a PC the owner brings in, even a
        // later one, or a takeover's PC would beat the owner's new PCs at every fork (it ranks before them).
        bool Reaches(string id) => !restricted || id == signer.Id || _restricted.Contains(id) && Rank(id) > Rank(signer.Id);
        switch (op)
        {
            // Anyone in the list brings in a PC (restricted when the account entry or a restricted PC does). The recovery
            // entry: also a new code's entry, which takes its place. A PC that isn't restricted: the recovery entry or
            // the account entry when there is none.
            case "add" when added is not null && !_devices.ContainsKey(added.Id) &&
                            (!added.Entry || added.Recovery && signer.Recovery || !signer.Entry && !restricted && !_devices.Values.Any(e => added.Recovery ? e.Recovery : e.Account && added.Account)):
                if (added.Recovery && signer.Recovery) _devices.Remove(signer.Id);
                // The account entry's add is restricted while a PC that isn't is listed, or the recovery entry is (the
                // code is the owner's way back). With neither, it is the owner signing in again after leaving their
                // last PC, and it starts unrestricted, as a first PC does. Neither entry counts as a PC.
                if (!added.Entry && (restricted || signer.Account &&
                    _devices.Values.Any(e => e.Recovery || !e.Entry && !_restricted.Contains(e.Id))))
                    _restricted.Add(added.Id);
                _devices[added.Id] = added;
                break;
            // The recovery entry removes any device, itself included (recovery off). A PC removes a PC, itself included
            // (it leaves the identity), or the account entry (account sign-in off), never the recovery entry; a restricted
            // PC removes only PCs it reaches. The account entry removes nothing.
            case "remove" when Device.Text(change, "device") is { } id && _devices.TryGetValue(id, out var gone) &&
                               (signer.Recovery || !signer.Account && !gone.Recovery && (gone.Account ? !restricted : Reaches(id))):
                _devices.Remove(id);
                _restricted.Remove(id);
                break;
            // A PC renames a PC it reaches. No one renames an entry, and an entry renames nothing.
            case "rename" when !signer.Entry && change["device"] is JsonObject d && d.Count == 2 && Device.Text(d, "id") is { } id &&
                               _devices.TryGetValue(id, out var renamed) && !renamed.Entry && Reaches(id) && Device.Text(d, "name") is { } name && Device.NameOk(name):
                _devices[id] = renamed with { Name = name };
                break;
            default:
                return false;
        }
        _changes.Add(change);
        _hashes.Add(CanonicalJson.Hash(change));
        return true;
    }
}

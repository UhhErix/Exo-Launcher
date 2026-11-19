using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// What every Exo app does with the recovery code, the same way (plan §2.12, §2.17), so no app carries its own copy of
/// the steps that touch keys:
/// <list type="bullet">
/// <item><see cref="OpenAsync"/>: what the code opens, for joining with it (the Exo account) or taking the profile back.</item>
/// <item><see cref="JoinAsync"/>: this PC in, added by the code's entry, as a linked PC is; the other PCs, the sync
/// key and the code stay. Signing in on a PC with no profile does this with the code the account keeps.</item>
/// <item><see cref="Setup"/>: a new profile with this PC its first device.</item>
/// <item><see cref="RecoveryOn"/>: a code's entry on the list, the code shown once to the person.</item>
/// <item><see cref="AccountOn"/>: the Exo account's entry (§2.17), made from a secret the account keeps: the account gets
/// it first (<see cref="AccountClient.PutRecoveryAsync"/>), and only then does this run, so the account never misses
/// the entry's secret. The entry only adds PCs, restricted ones (<see cref="DeviceList.IsRestricted"/>).</item>
/// <item><see cref="PostKitAsync"/>: the kit for a sync key in the recovery box and the account box.</item>
/// </list>
/// Each answers the device-list changes it signed; the app puts them in the profile box (ProfileItems.DeviceChange) and
/// takes its own items from <see cref="Opened.Batches"/>.
/// </summary>
public static class CodeFlows
{
    /// <summary>Another PC signed in to the account at the same moment and its add took the head: open again and join on top.</summary>
    public sealed class HeadTakenException() : InvalidOperationException("Another PC signed in to this account at the same moment. Try again.");

    /// <summary>What the code opens: the newest kit in its box, the device list from the sync box as of that kit's key
    /// (checked against the kit's sender and the code's own entry, the recovery entry or the account entry), and every row
    /// of that box.</summary>
    public sealed record Opened(RecoveryCode.OpenedKit Kit, byte[] KitEnvelope, DeviceList List, byte[] BoxId, IReadOnlyList<byte[]> Rows, Device Entry)
    {
        public byte[] SyncKey => Kit.Kit.SyncKey;
        public uint Epoch => Kit.Kit.Epoch;

        /// <summary>One app's batches (its sub-key's, <see cref="Envelope.SyncSubKey"/>), as they were put, from
        /// senders on the list only.</summary>
        public IReadOnlyList<IReadOnlyList<SyncBatch.Item>> Batches(string app)
        {
            var key = Envelope.SyncSubKey(SyncKey, app);
            var senders = List.Devices.Select(d => d.SigPub).ToList();
            var batches = new List<IReadOnlyList<SyncBatch.Item>>();
            foreach (var row in Rows)
                if (Envelope.Header(row) is (Envelope.SyncKind, var epoch) && epoch == Epoch && Envelope.OpenSyncBeforeSender(row, BoxId, key) is { } data &&
                    SyncBatch.Read(data, app) is { } items && senders.Any(s => Envelope.SignedBy(row, s)))
                    batches.Add(items);
            return batches;
        }
    }

    public static async Task<Opened> OpenAsync(RecoveryCode code, MailboxConnect connect, MailboxPace? pace, CancellationToken ct)
    {
        // The kits: the newest that opens.
        var kits = new List<(RecoveryCode.OpenedKit Kit, byte[] Envelope)>();
        using (var boxKey = code.BoxKey())
        await using (var box = await MailboxBox.OpenAsync(connect, boxKey, ct, pace).ConfigureAwait(false))
            foreach (var message in await box.SinceAsync(0, ct).ConfigureAwait(false))
                if (code.OpenKit(message.Blob) is { } opened) kits.Add((opened, message.Blob));
        if (kits.Count == 0) throw new InvalidOperationException("Nothing was found for that code. Check it and try again.");
        var (kit, kitEnvelope) = kits.OrderByDescending(k => k.Kit.Kit.Epoch).First();

        // The sync box as of that key, and the device list from the profile's items in it.
        var boxId = Convert.FromHexString(SyncSession.BoxIdOf(kit.Kit.SyncKey));
        var rows = new List<byte[]>();
        using (var syncBoxKey = new EcdsaSigner(P256.Signer(Mailbox.BoxScalar(kit.Kit.SyncKey))))
        await using (var box = await MailboxBox.OpenAsync(connect, syncBoxKey, ct, pace).ConfigureAwait(false))
            rows.AddRange((await box.SinceAsync(0, ct).ConfigureAwait(false)).Select(m => m.Blob));
        var profileKey = Envelope.SyncSubKey(kit.Kit.SyncKey, ProfileItems.App);
        var changes = new List<JsonObject>();
        foreach (var row in rows)
            if (Envelope.Header(row) is (Envelope.SyncKind, var epoch) && epoch == kit.Kit.Epoch && Envelope.OpenSyncBeforeSender(row, boxId, profileKey) is { } data &&
                SyncBatch.Read(data, ProfileItems.App) is { } items)
                changes.AddRange(items.Select(ProfileItems.Change).OfType<JsonObject>());
        var list = Chain(changes) ?? throw new InvalidOperationException("The profile's device list couldn't be read.");
        Device? entry;
        using (var entryKey = code.EntryKey()) entry = list.Devices.FirstOrDefault(d => d.Entry && d.SigPub.AsSpan().SequenceEqual(entryKey.PublicKey));
        if (entry is null || list.ExoId != kit.Kit.ExoId || list.Find(kit.By) is not { Entry: false } by || !Envelope.SignedBy(kitEnvelope, by.SigPub) ||
            (entry.Recovery ? list.CodeNumber : list.AccountNumber) != kit.CodeNumber)
            throw new InvalidOperationException("That code isn't this profile's current recovery code.");
        return new Opened(kit, kitEnvelope, list, boxId, rows, entry);
    }

    /// <summary>
    /// This PC joins with the code: the code's entry adds <paramref name="record"/> (this PC's, by
    /// <see cref="Device.Create"/>), the folder takes the identity with the kit's sync key and the code's box key, and
    /// the add goes in the sync box signed by the entry (<see cref="AnnounceAsync"/>), or the other PCs would drop it, and
    /// every row after it, as a stranger's. <paramref name="put"/> makes the app's profile items of the add
    /// (ProfileItems.DeviceChange); answers them, already in the box, for the app to mark sent.
    /// </summary>
    /// <param name="claim">With the account entry: claims the head for the add at the account service
    /// (<see cref="AccountClient.ClaimEntryAsync"/>) before anything is saved; refused, this throws
    /// <see cref="HeadTakenException"/>, and the app opens again (the other PC's add is in by then) and joins on top.</param>
    public static async Task<IReadOnlyList<SyncBatch.Item>> JoinAsync(Opened opened, RecoveryCode code, IdentityFolder folder, DeviceKeys keys, JsonObject record,
        long now, Func<JsonObject, IReadOnlyList<SyncBatch.Item>> put, MailboxConnect connect, MailboxPace? pace, CancellationToken ct,
        Func<string, string, Task<bool>>? claim = null)
    {
        JsonObject add;
        var head = opened.List.Head;
        using (var entryKey = code.EntryKey()) add = opened.List.Add(entryKey, record, now);
        // Two PCs signing in at once would both add after this head, by the one entry: an equivocation every PC drops.
        if (claim is not null && opened.Entry.Account && !await claim(head, CanonicalJson.Hash(add)).ConfigureAwait(false)) throw new HeadTakenException();
        folder.Save(keys, opened.List.ExoId, opened.List, opened.Epoch, opened.SyncKey, opened.Entry.Recovery ? code.BoxScalar : null, linked: true,
            accountBox: opened.Entry.Account ? code.BoxScalar : null);
        var items = put(add);
        await AnnounceAsync(opened, code, items, connect, pace, ct).ConfigureAwait(false);
        return items;
    }

    /// <summary>
    /// The changes the code's entry signed (a join's add; a restore's removes and adds), as the profile items the app
    /// made of them (ProfileItems.DeviceChange), in the sync box as of the kit's key, signed by the entry: every PC of
    /// the list knows it, so they take this PC in before its own rows come, which they'd otherwise drop as a stranger's.
    /// </summary>
    public static async Task AnnounceAsync(Opened opened, RecoveryCode code, IReadOnlyList<SyncBatch.Item> items, MailboxConnect connect, MailboxPace? pace, CancellationToken ct)
    {
        var profileKey = Envelope.SyncSubKey(opened.SyncKey, ProfileItems.App);
        using var entryKey = code.EntryKey();
        using var boxKey = new EcdsaSigner(P256.Signer(Mailbox.BoxScalar(opened.SyncKey)));
        await using var box = await MailboxBox.OpenAsync(connect, boxKey, ct, pace).ConfigureAwait(false);
        foreach (var (data, _) in SyncBatch.Split(ProfileItems.App, items).Batches)
            await box.PutAsync(Envelope.SealSync(opened.BoxId, opened.Epoch, profileKey, data, entryKey), ct).ConfigureAwait(false);
    }

    /// <summary>A new identity with this PC (<paramref name="record"/>) its first device and a new sync key, saved.
    /// Answers the list; its changes go in the profile box.</summary>
    public static DeviceList Setup(IdentityFolder folder, DeviceKeys keys, JsonObject record, long now)
    {
        var list = DeviceList.Start(keys.Identity, record, now);
        folder.Save(keys, list.ExoId, list, 1, SyncKeyHandoff.NewKey(), linked: true);
        return list;
    }

    /// <summary>
    /// <paramref name="code"/>'s entry on the list (<see cref="RecoveryCode.New"/>; with the Exo account, given to the
    /// account first), signed by this PC in one hold of the mutex, and its box key kept in the folder. The app puts the
    /// changes, the recovery box item (ProfileItems.RecoveryBoxBody) and the kit (<see cref="PostKitAsync"/>).
    /// </summary>
    public static IdentityFolder.Made RecoveryOn(IdentityFolder folder, IdentityFolder.State state, RecoveryCode code, long now)
    {
        if (state.List.CodeNumber is not null) throw new InvalidOperationException("Recovery is already on.");
        var made = folder.Make(state, list => list.Add(state.Keys.Identity, code.Entry(now), now));
        folder.Save(state.Keys, state.ExoId, state.List, state.Epoch, state.SyncKey, code.BoxScalar, accountBox: state.AccountBox);
        return made;
    }

    /// <summary>
    /// The Exo account's entry on the list, made from <paramref name="secret"/> (<see cref="RecoveryCode.New"/>; the
    /// account already has it), signed by this PC (one that isn't restricted) in one hold of the mutex, and its box key
    /// kept in the folder. The app puts the changes, the account box item (ProfileItems.RecoveryBoxBody under
    /// <see cref="ProfileItems.AccountBoxType"/>) and the kit (<see cref="PostKitAsync"/>).
    /// </summary>
    public static IdentityFolder.Made AccountOn(IdentityFolder folder, IdentityFolder.State state, RecoveryCode secret, long now)
    {
        if (state.List.AccountNumber is not null) throw new InvalidOperationException("Account sign-in is already on.");
        var made = folder.Make(state, list => list.Add(state.Keys.Identity, secret.Entry(now, account: true), now));
        folder.Save(state.Keys, state.ExoId, state.List, state.Epoch, state.SyncKey, state.RecoveryBox, accountBox: secret.BoxScalar);
        return made;
    }

    /// <summary>The kit for <paramref name="kit"/>'s sync key in the recovery box and in the account box, each while its
    /// entry is on and this PC holds its box's key; false when there was nothing to post it to.</summary>
    public static async Task<bool> PostKitAsync(MailboxConnect connect, MailboxPace? pace, IdentityFolder.State state, DeviceList list, RecoveryCode.Kit kit, CancellationToken ct)
    {
        var posted = false;
        if (state.RecoveryBox is { } recoveryBox && list.CodeNumber is { } code && list.Devices.FirstOrDefault(d => d.Recovery) is { } recovery)
            posted |= await PostOneAsync(connect, pace, state, recoveryBox, code, recovery, kit, ct).ConfigureAwait(false);
        if (state.AccountBox is { } accountBox && list.AccountNumber is { } number && list.Devices.FirstOrDefault(d => d.Account) is { } account)
            posted |= await PostOneAsync(connect, pace, state, accountBox, number, account, kit, ct).ConfigureAwait(false);
        return posted;
    }

    private static async Task<bool> PostOneAsync(MailboxConnect connect, MailboxPace? pace, IdentityFolder.State state, byte[] scalar, uint number, Device entry, RecoveryCode.Kit kit, CancellationToken ct)
    {
        using var boxKey = new EcdsaSigner(P256.Signer(scalar));
        var sealedKit = RecoveryCode.SealKit(kit, number, entry.EncPub, Mailbox.BoxId(boxKey.PublicKey), state.Keys.Identity) ?? throw new InvalidOperationException("The entry has no key.");
        await using var box = await MailboxBox.OpenAsync(connect, boxKey, ct, pace).ConfigureAwait(false);
        await box.PutAsync(sealedKit, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The device list from a set of changes, whatever order they came in; null when none starts a chain.</summary>
    public static DeviceList? Chain(IReadOnlyList<JsonObject> changes)
    {
        if (changes.FirstOrDefault(c => Device.Int(c, "seq") == 0 && Device.Text(c, "prev") == "") is not { } first || DeviceList.Load([first]) is not { } list) return null;
        foreach (var change in DeviceList.InTakeOrder(list, changes)) list.Take(change);
        return list;
    }
}

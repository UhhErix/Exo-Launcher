using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// What every Exo app does to a profile it is in, or comes into, the same way (plan §2.3, §2.12, §2.17): the steps that
/// touch keys, the device list and the boxes, in one place beside <see cref="CodeFlows"/>, so no app carries its own
/// copy of them. The app keeps what is its own: its UI, Windows Hello before a step that asks for it (a remove, a
/// link's add, the recovery code shown), its locks, its sessions (stopped around a step that moves the box) and where it
/// keeps its items; it hands each flow the profile's items (<paramref name="profile"/> in each call) as it holds them now,
/// after starting them over where a step needs that (a setup, a link, a restore).
/// <list type="bullet">
/// <item><see cref="Setup"/>: a new profile, this PC its first device, with its name.</item>
/// <item><see cref="LinkJoinAsync"/> and <see cref="LinkAddAsync"/>: the two sides of Link a PC (<see cref="LinkFlow"/>).</item>
/// <item><see cref="Rename"/>, <see cref="RemoveAsync"/> and <see cref="LeaveAsync"/>: a PC's name, another PC out
/// (with a new sync key for the PCs that stay), this PC out.</item>
/// <item><see cref="RecoveryOnAsync"/>, <see cref="AccountOnAsync"/> and <see cref="RestoreAsync"/>: the recovery code's
/// entry (the code shown once), the Exo account's entry, and the profile taken back with the code.</item>
/// </list>
/// Each puts what it signed in <paramref name="profile"/> as profile items (<see cref="ProfileItems.DeviceChange"/>), and,
/// where the PCs must hear it before this PC's sessions run again, in the sync box too.
/// </summary>
public static class ProfileFlows
{
    /// <summary>A new profile with this PC (<paramref name="record"/>) its first device, a new sync key and
    /// <paramref name="name"/>, saved. Answers the list.</summary>
    public static DeviceList Setup(IdentityFolder folder, DeviceKeys keys, JsonObject record, string name, SyncedItems profile, long now)
    {
        var body = ProfileItems.NameBody(name);
        var list = CodeFlows.Setup(folder, keys, record, now);
        profile.Put(ProfileItems.ProfileType, ProfileItems.NameId, body, keys.DeviceId);
        Put(profile, keys.DeviceId, list.Changes);
        return list;
    }

    /// <summary>
    /// Link a PC, on the PC joining, once the person said the 6 digits match: it hands over this PC's record, takes the
    /// identity the PC already in sends back (its list with this PC added, the sync key) and saves it. The app starts
    /// its items over before (<paramref name="profile"/> is the new holder), as a PC new to the profile.
    /// </summary>
    public static async Task<LinkFlow.Joined> LinkJoinAsync(LinkFlow.NewPc pc, IdentityFolder folder, DeviceKeys keys, JsonObject record, SyncedItems profile, CancellationToken ct)
    {
        var joined = await pc.JoinAsync(record, ct).ConfigureAwait(false);
        Put(profile, keys.DeviceId, joined.List.Changes);
        folder.Save(keys, joined.ExoId, joined.List, joined.Epoch, joined.SyncKey, linked: true);
        return joined;
    }

    /// <summary>
    /// Link a PC, on the PC already in, once the person said the 6 digits match (and Windows Hello, app-side): it offers the
    /// identity, adds the record the new PC sends, puts the add in the sync box before the new PC hears it's in (so its
    /// first rows come after its add) and tells it. A restricted PC can't add: the list refuses it, and this throws.
    /// </summary>
    public static async Task LinkAddAsync(LinkFlow.ExistingPc pc, IdentityFolder folder, IdentityFolder.State state, SyncedItems profile, MailboxConnect connect,
        MailboxPace? pace, long now, CancellationToken ct)
    {
        var record = await pc.OfferAsync(new(state.ExoId, state.Epoch, state.SyncKey, state.List.Changes), ct).ConfigureAwait(false);
        var made = folder.Make(state, list => list.Add(state.Keys.Identity, record, now));
        var items = PutMade(profile, state.DeviceId, made);
        await PutRowAsync(state, items, connect, pace, ct).ConfigureAwait(false);
        profile.Sent(items);
        Keep(folder, state);
        await pc.FinishAsync(made.Change!, ct).ConfigureAwait(false);
    }

    /// <summary>A PC's name (this PC's, or one this PC may reach: a restricted PC renames only itself and the restricted
    /// PCs added after it), for the sessions to carry.</summary>
    public static IReadOnlyList<SyncBatch.Item> Rename(IdentityFolder folder, IdentityFolder.State state, SyncedItems profile, string id, string name, long now)
    {
        var items = PutMade(profile, state.DeviceId, folder.Make(state, list => list.Rename(state.Keys.Identity, id, name, now)));
        Keep(folder, state);
        return items;
    }

    /// <summary>
    /// Another PC out (plan §2.3; Windows Hello app-side, the app's sessions stopped): this PC signs the remove, makes a
    /// new sync key and leaves it, sealed to each PC that stays, in the old box with the remove, posts its kit to the
    /// recovery and account boxes, and saves it. The removed PC can't read anything written after. Answers the new kit:
    /// the app moves its sessions to the new box, and ends the removed PC's account sessions.
    /// </summary>
    public static async Task<RecoveryCode.Kit> RemoveAsync(IdentityFolder folder, IdentityFolder.State state, SyncedItems profile, string id, MailboxConnect connect,
        MailboxPace? pace, long now, CancellationToken ct)
    {
        if (id == state.DeviceId) throw new InvalidOperationException("Remove this PC from another one.");
        var items = PutMade(profile, state.DeviceId, folder.Make(state, list => list.Remove(state.Keys.Identity, id, now)));
        var kit = new RecoveryCode.Kit(state.ExoId, state.Epoch + 1, SyncKeyHandoff.NewKey());
        var oldBox = Convert.FromHexString(SyncSession.BoxIdOf(state.SyncKey));
        using (var boxKey = new EcdsaSigner(P256.Signer(Mailbox.BoxScalar(state.SyncKey))))
        await using (var box = await MailboxBox.OpenAsync(connect, boxKey, ct, pace).ConfigureAwait(false))
        {
            await box.PutAsync(SealProfile(state, items), ct).ConfigureAwait(false);
            foreach (var device in state.List.Devices.Where(d => !d.Entry && d.Id != state.DeviceId))
                await box.PutAsync(SyncKeyHandoff.Seal(kit, oldBox, device.EncPub, state.Keys.Identity), ct).ConfigureAwait(false);
        }
        await CodeFlows.PostKitAsync(connect, pace, state, state.List, kit, ct).ConfigureAwait(false);
        folder.Save(state.Keys, state.ExoId, state.List, kit.Epoch, kit.SyncKey, state.RecoveryBox, accountBox: state.AccountBox);
        return kit;
    }

    /// <summary>
    /// This PC out of the profile (signing out of the Exo account; the app's sessions stopped, after one last pass so the
    /// leave goes after every change this PC can see): its own remove, straight into the sync box, since its sessions
    /// won't run again to carry it, and its identity files gone (<see cref="IdentityFolder.Forget"/>). Its keys stay.
    /// </summary>
    public static async Task LeaveAsync(IdentityFolder folder, IdentityFolder.State state, SyncedItems profile, MailboxConnect connect, MailboxPace? pace, long now,
        CancellationToken ct)
    {
        var items = PutMade(profile, state.DeviceId, folder.Make(state, list => list.Remove(state.Keys.Identity, state.DeviceId, now)));
        await PutRowAsync(state, items, connect, pace, ct).ConfigureAwait(false);
        folder.Forget();
    }

    /// <summary>
    /// Recovery on (plan §2.12; Windows Hello app-side): a new code's entry on the list, its box's key for every PC as an
    /// item, and the kit for the sync key in its box. Answers the code, to show once: it is kept nowhere.
    /// </summary>
    public static async Task<string> RecoveryOnAsync(IdentityFolder folder, IdentityFolder.State state, SyncedItems profile, MailboxConnect connect, MailboxPace? pace,
        long now, CancellationToken ct)
    {
        if (state.List.CodeNumber is not null) throw new InvalidOperationException("Recovery is already on.");
        var (text, code) = RecoveryCode.New();
        PutMade(profile, state.DeviceId, CodeFlows.RecoveryOn(folder, state, code, now));
        profile.Put(ProfileItems.RecoveryBoxType, ProfileItems.RecoveryBoxId, ProfileItems.RecoveryBoxBody(state.List.CodeNumber!.Value, code.BoxScalar), state.DeviceId);
        await CodeFlows.PostKitAsync(connect, pace, state with { RecoveryBox = code.BoxScalar }, state.List, new(state.ExoId, state.Epoch, state.SyncKey), ct).ConfigureAwait(false);
        return text;
    }

    /// <summary>
    /// The Exo account's entry for this profile (plan §2.17), signed by this PC: a new secret goes to the account first
    /// (<paramref name="keep"/>, with the exoId and the secret's text: <see cref="AccountClient.PutRecoveryAsync"/> of
    /// <see cref="AccountClient.RecoveryCodeText"/>), so the account never misses the entry the profile uses; then an
    /// entry the account no longer holds is removed and the new one added, with its box's key for every PC as an item and
    /// a kit for the sync key in its box.
    /// </summary>
    public static async Task AccountOnAsync(IdentityFolder folder, IdentityFolder.State state, SyncedItems profile, Func<string, string, Task> keep,
        MailboxConnect connect, MailboxPace? pace, long now, CancellationToken ct)
    {
        var (text, secret) = RecoveryCode.New();
        await keep(state.ExoId, text).ConfigureAwait(false);
        if (state.List.Devices.FirstOrDefault(d => d.Account) is { } old)
            PutMade(profile, state.DeviceId, folder.Make(state, list => list.Remove(state.Keys.Identity, old.Id, now)));
        PutMade(profile, state.DeviceId, CodeFlows.AccountOn(folder, state, secret, now));
        profile.Put(ProfileItems.AccountBoxType, ProfileItems.RecoveryBoxId, ProfileItems.RecoveryBoxBody(state.List.AccountNumber!.Value, secret.BoxScalar), state.DeviceId);
        await CodeFlows.PostKitAsync(connect, pace, state with { AccountBox = secret.BoxScalar }, state.List, new(state.ExoId, state.Epoch, state.SyncKey), ct).ConfigureAwait(false);
    }

    /// <param name="Code">The new recovery code, to show once.</param>
    public sealed record Restored(string Code, DeviceList List, uint Epoch, byte[] SyncKey);

    /// <summary>
    /// The profile taken back with its recovery code on this PC (plan §2.12), after <see cref="CodeFlows.OpenAsync"/>
    /// and once the app took what the box had (<see cref="CodeFlows.Opened.Batches"/>, into holders started over): the
    /// code's entry removes every old PC (the account's entry with them: whoever holds the account signs in again for a
    /// new one), adds this one and a new code in its own place, announced in the old box so a stolen PC finds itself
    /// removed; then a new sync key, saved, with its kit in the new code's box. Answers the new code, to show once.
    /// </summary>
    public static async Task<Restored> RestoreAsync(CodeFlows.Opened opened, RecoveryCode code, IdentityFolder folder, DeviceKeys keys, JsonObject record,
        SyncedItems profile, MailboxConnect connect, MailboxPace? pace, long now, CancellationToken ct)
    {
        if (!opened.Entry.Recovery) throw new InvalidOperationException("Only the recovery code takes a profile back.");
        var list = opened.List;
        var (text, next) = RecoveryCode.New();
        var made = new List<JsonObject>();
        using (var entryKey = code.EntryKey())
        {
            foreach (var old in list.Devices.Where(d => !d.Recovery).ToList()) made.Add(list.Remove(entryKey, old.Id, now));
            made.Add(list.Add(entryKey, record, now));
            made.Add(list.Add(entryKey, next.Entry(now), now));
        }
        await CodeFlows.AnnounceAsync(opened, code, Put(profile, keys.DeviceId, made), connect, pace, ct).ConfigureAwait(false);
        var (syncKey, epoch) = (SyncKeyHandoff.NewKey(), opened.Epoch + 1);
        profile.Put(ProfileItems.RecoveryBoxType, ProfileItems.RecoveryBoxId, ProfileItems.RecoveryBoxBody(list.CodeNumber!.Value, next.BoxScalar), keys.DeviceId);
        profile.Put(ProfileItems.AccountBoxType, ProfileItems.RecoveryBoxId, null, keys.DeviceId);
        folder.Save(keys, list.ExoId, list, epoch, syncKey, next.BoxScalar, linked: true);
        var restored = new IdentityFolder.State(list.ExoId, keys, list, epoch, syncKey, next.BoxScalar);
        await CodeFlows.PostKitAsync(connect, pace, restored, list, new(list.ExoId, epoch, syncKey), ct).ConfigureAwait(false);
        return new Restored(text, list, epoch, syncKey);
    }

    /// <summary>Device-list changes as profile items, for the sessions to carry.</summary>
    public static IReadOnlyList<SyncBatch.Item> Put(SyncedItems profile, string deviceId, IEnumerable<JsonObject> changes) =>
        [.. changes.Select(ProfileItems.DeviceChange).Select(c => profile.Put(ProfileItems.DeviceChangeType, c.Id, c.Body, deviceId))];

    /// <summary>A new change's items, the merge's re-signs first and even when the list refused the change; then the
    /// refusal, as the error.</summary>
    public static IReadOnlyList<SyncBatch.Item> PutMade(SyncedItems profile, string deviceId, IdentityFolder.Made made)
    {
        var items = Put(profile, deviceId, made.ToPut);
        if (made.Refused is { } why) throw new InvalidOperationException(why);
        return items;
    }

    /// <summary>Profile items as one row of the sync box, signed by this PC, outside the sessions.</summary>
    public static async Task PutRowAsync(IdentityFolder.State state, IReadOnlyList<SyncBatch.Item> items, MailboxConnect connect, MailboxPace? pace, CancellationToken ct)
    {
        using var boxKey = new EcdsaSigner(P256.Signer(Mailbox.BoxScalar(state.SyncKey)));
        await using var box = await MailboxBox.OpenAsync(connect, boxKey, ct, pace).ConfigureAwait(false);
        await box.PutAsync(SealProfile(state, items), ct).ConfigureAwait(false);
    }

    private static byte[] SealProfile(IdentityFolder.State state, IReadOnlyList<SyncBatch.Item> items) =>
        Envelope.SealSync(Convert.FromHexString(SyncSession.BoxIdOf(state.SyncKey)), state.Epoch, Envelope.SyncSubKey(state.SyncKey, ProfileItems.App),
            SyncBatch.Write(ProfileItems.App, items), state.Keys.Identity);

    /// <summary>The list as the change left it, with the boxes' keys this PC holds.</summary>
    private static void Keep(IdentityFolder folder, IdentityFolder.State state) =>
        folder.Save(state.Keys, state.ExoId, state.List, state.Epoch, state.SyncKey, state.RecoveryBox, accountBox: state.AccountBox);
}

using System.Security.Cryptography;

namespace Exo.Identity;

/// <summary>
/// One app's conversation with the shared sync box (plan §2.6, §2.7). <see cref="RunAsync"/>:
/// <list type="number">
/// <item>logs in to the box the sync key names (<see cref="Mailbox.BoxScalar"/>);</item>
/// <item>reads every row after the cursor in seq order: a kind 0x01 envelope is opened with each holder's sub-key and
/// signed by a device of the list, its batch given to that holder's <see cref="SyncedItems.Take"/> (the winners go to
/// <see cref="Options.Taken"/> before the next row, so a device the profile's changes just added can sign the rows
/// after); anything else goes to <see cref="Options.Other"/> (a new sync key, <see cref="SyncKeyHandoff"/>);</item>
/// <item>pushes each holder's <see cref="SyncedItems.Outgoing"/> (or its <see cref="SyncedItems.Snapshot"/> when asked,
/// or when this PC missed rows), split into envelopes by <see cref="SyncBatch.Split"/>, and calls
/// <see cref="SyncedItems.Sent"/> with what the box stored;</item>
/// <item>with <c>live</c>, stays: takes what other PCs store as it comes and pushes this PC's changes a moment after they
/// happen, until it is cancelled or the connection ends.</item>
/// </list>
/// The cursor is the highest seq read, never one this PC put (an earlier row may still be on its way); reading one's
/// own rows again takes nothing. Each envelope carries a whole batch that reads on its own, so nothing is reassembled.
/// </summary>
public sealed class SyncSession(MailboxConnect connect, SyncSession.Options options)
{
    /// <param name="Epoch">The sync key's epoch: every 0x01 envelope's header.</param>
    /// <param name="Signer">This PC's identity key, which signs every envelope it puts.</param>
    /// <param name="DeviceId">This PC's device id, stamped on its changes.</param>
    /// <param name="Senders">The identity keys of the devices in the list, asked again for every row.</param>
    /// <param name="Holders">The sub-keys this app reads and writes (its own, and <c>profile</c>).</param>
    /// <param name="Cursor">The highest seq this app read from this box (0: from the start).</param>
    /// <param name="Snapshot">Push every holder's snapshot rather than only what changed (a new box, a weekly one).</param>
    /// <param name="Other">A row no holder opened (a new sync key, someone else's app); true stops the session (this box is done).</param>
    /// <param name="BoxKey">The key the box's own key comes from (<see cref="Mailbox.BoxScalar"/>): null for the shared
    /// box (the sync key); for a box of its own (§2.7) that sub-key,
    /// <c>Envelope.SyncSubKey(syncKey, app)</c>. Items are sealed under each holder's sub-key of <paramref name="SyncKey"/>
    /// either way, never one derived again from this. In a box of its own, a snapshot's writer trims the rows below it
    /// (<see cref="RunAsync"/>); the shared box is never trimmed.</param>
    /// <param name="Skipped">An item that can't go (too big for an envelope alone, or outside the format): it stays on
    /// this PC unsent, for the app to log; the rest of its batch goes.</param>
    /// <param name="Pace">The Worker's buckets, kept on this side (<see cref="MailboxPace.Worker"/> unless a test runs its own).</param>
    public sealed record Options(
        uint Epoch,
        byte[] SyncKey,
        IDeviceSigner Signer,
        string DeviceId,
        Func<IReadOnlyList<byte[]>> Senders,
        IReadOnlyList<SyncedItems> Holders,
        uint Cursor,
        bool Snapshot,
        Action<SyncedItems, IReadOnlyList<SyncBatch.Item>> Taken,
        Action<uint> CursorMoved,
        Func<MailboxMessage, bool>? Other = null,
        byte[]? BoxKey = null,
        Action<SyncedItems, SyncBatch.Item>? Skipped = null,
        MailboxPace? Pace = null);

    /// <summary>How long a change waits before it goes, so a run of changes (a slider dragged) goes as one put.</summary>
    public static readonly TimeSpan PushDelay = TimeSpan.FromSeconds(2);

    private readonly byte[] _boxKey = options.BoxKey ?? options.SyncKey;
    private byte[] BoxIdBytes => _boxIdBytes ??= Convert.FromHexString(Mailbox.BoxId(BoxPublicKey(_boxKey)));
    private byte[]? _boxIdBytes;
    private readonly Dictionary<string, byte[]> _subKeys = options.Holders.ToDictionary(h => h.App, h => Envelope.SyncSubKey(options.SyncKey, h.App));
    private uint _cursor = options.Cursor;
    private bool _done;

    /// <summary>The box's id (32 lowercase hex), from the sync key.</summary>
    public string BoxId => Convert.ToHexStringLower(BoxIdBytes);

    /// <summary>The sync box's public key for a sync key: its id names the box.</summary>
    public static byte[] BoxPublicKey(ReadOnlySpan<byte> syncKey)
    {
        using var key = P256.Signer(Mailbox.BoxScalar(syncKey));
        return P256.PublicKey(key);
    }

    /// <summary>The box this sync key names, as the mailbox's URL has it.</summary>
    public static string BoxIdOf(ReadOnlySpan<byte> syncKey) => Mailbox.BoxId(BoxPublicKey(syncKey));

    /// <summary>
    /// Whether a cursor missed rows, by the box's ready frame: past its last (the box emptied and started again) or
    /// below its first less one (rows expired or a cap took them before this PC read them). Then the reader starts from
    /// 0 and pushes its whole snapshot.
    /// </summary>
    public static bool CursorMissed(uint cursor, uint first, uint last) => cursor > last || (ulong)cursor + 1 < first;

    /// <summary>Whether <see cref="Options.Other"/> ended it: the box is behind this PC now.</summary>
    public bool Done => _done;

    /// <summary>The highest seq read so far.</summary>
    public uint Cursor => _cursor;

    public async Task RunAsync(bool live, CancellationToken ct)
    {
        using var boxKey = new EcdsaSigner(P256.Signer(Mailbox.BoxScalar(_boxKey)));
        await using var box = await MailboxBox.OpenAsync(connect, boxKey, ct, options.Pace).ConfigureAwait(false);
        // A cursor past the box's last row: the box emptied (30 days with nothing) and started again. Below its first:
        // rows went before this PC read them. Either way it reads what's there, and puts its whole snapshot back.
        var missed = CursorMissed(_cursor, box.First, box.Last);
        if (missed) _cursor = 0;
        foreach (var message in await box.SinceAsync(_cursor, ct).ConfigureAwait(false))
            if (Read(message) && _done) return;
        await PushAsync(box, options.Snapshot || missed, ct).ConfigureAwait(false);
        if (!live) return;

        using var changed = new SemaphoreSlim(0);
        void Signal() => changed.Release();
        foreach (var holder in options.Holders) holder.Changed += Signal;
        try
        {
            // Changes made while the catch-up ran go now.
            if (options.Holders.Any(h => h.Pending)) changed.Release();
            var next = box.NextAsync(ct);
            var wake = changed.WaitAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                if (await Task.WhenAny(next, wake).ConfigureAwait(false) == next)
                {
                    if (Read(await next.ConfigureAwait(false)) && _done) return;
                    next = box.NextAsync(ct);
                    continue;
                }
                await wake.ConfigureAwait(false);
                await Task.Delay(PushDelay, ct).ConfigureAwait(false);
                while (changed.CurrentCount > 0) await changed.WaitAsync(ct).ConfigureAwait(false);
                await PushAsync(box, snapshot: false, ct).ConfigureAwait(false);
                wake = changed.WaitAsync(ct);
            }
        }
        finally
        {
            foreach (var holder in options.Holders) holder.Changed -= Signal;
        }
    }

    /// <summary>One row: true when it was this box's to read (its seq moved the cursor).</summary>
    private bool Read(MailboxMessage message)
    {
        if (message.Seq <= _cursor) return false;
        var opened = false;
        if (Envelope.Header(message.Blob) is (Envelope.SyncKind, var epoch) && epoch == options.Epoch)
        {
            var senders = options.Senders();
            foreach (var holder in options.Holders)
            {
                if (Envelope.OpenSync(message.Blob, BoxIdBytes, _subKeys[holder.App], senders) is not { } batch) continue;
                opened = true;
                if (SyncBatch.Read(batch.Data, holder.App) is { } items && holder.Take(items) is { Count: > 0 } taken) options.Taken(holder, taken);
                break;
            }
        }
        if (!opened && options.Other?.Invoke(message) == true) _done = true;
        _cursor = message.Seq;
        options.CursorMoved(_cursor);
        return true;
    }

    /// <summary>
    /// Pushes what changed here, or every holder's snapshot. A snapshot in a box of its own then trims what is below it,
    /// only once all of its envelopes are stored and only when this PC had read every row before its first (the cursor
    /// just before it), so everything trimmed is in the snapshot; a PC whose cursor falls below the box's first row reads
    /// from 0 and gets the snapshot. The shared box holds other apps' rows and is never trimmed.
    /// </summary>
    private async Task PushAsync(MailboxBox box, bool snapshot, CancellationToken ct)
    {
        uint? first = null;
        foreach (var holder in options.Holders)
        {
            var items = snapshot ? holder.Snapshot(options.DeviceId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : holder.Outgoing(options.DeviceId);
            if (items.Count == 0) continue;
            // Oldest first: past the burst the rest goes over time, and what has waited longest goes first.
            var (batches, skipped) = SyncBatch.Split(holder.App, [.. items.OrderBy(i => i.Modified).ThenBy(i => i.Type, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal)]);
            foreach (var item in skipped) options.Skipped?.Invoke(holder, item);
            foreach (var (data, batch) in batches)
            {
                var seq = await box.PutAsync(Envelope.SealSync(BoxIdBytes, options.Epoch, _subKeys[holder.App], data, options.Signer), ct).ConfigureAwait(false);
                first ??= seq;
                holder.Sent(batch);
            }
        }
        if (snapshot && options.BoxKey is not null && first is > 1 and var start && _cursor + 1 == start)
            await box.TrimAsync(start - 1, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// A new sync key for each device that stays, after a removal (plan §2.3): a kind 0x02 envelope to the device's
/// <c>encPub</c>, put in the old sync box (the one every device still reads; the removed PC can't open it), signed by
/// the PC that removed, holding the same canonical JSON as the recovery kit
/// (<c>{"by","epoch","exoId","syncKey","v":1}</c>, <see cref="RecoveryCode.KitJson"/>). Its header epoch is 0.
/// </summary>
public static class SyncKeyHandoff
{
    public static byte[] Seal(RecoveryCode.Kit kit, ReadOnlySpan<byte> oldBoxId, ReadOnlySpan<byte> recipientEncPub, IDeviceSigner by, ECDiffieHellman? ephemeral = null) =>
        Envelope.SealToDevice(oldBoxId, recipientEncPub, RecoveryCode.KitJson(kit, Device.IdOf(by.PublicKey)), by, ephemeral)
        ?? throw new ArgumentException("Not a P-256 public key.", nameof(recipientEncPub));

    /// <summary>The new key, or null unless the envelope is for this device, from a device of the list, for this
    /// identity, and newer than <paramref name="epoch"/>.</summary>
    public static RecoveryCode.Kit? Open(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> boxId, IDeviceDecrypter me, DeviceList list, string exoId, uint epoch)
    {
        var senders = list.Devices.Where(d => !d.Entry).ToList();
        if (Envelope.OpenToDevice(envelope, boxId, me, [.. senders.Select(d => d.SigPub)]) is not { } opened ||
            RecoveryCode.ReadKitJson(opened.Data) is not var (kit, by) || by != senders[opened.Sender].Id ||
            kit.ExoId != exoId || kit.Epoch <= epoch || kit.SyncKey.Length != 32)
            return null;
        return kit;
    }

    /// <summary>A new sync key: 32 random bytes.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);
}

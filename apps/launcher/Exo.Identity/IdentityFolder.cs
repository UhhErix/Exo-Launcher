using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// The shared identity folder's files read and written together (plan §2.18), under the mutex every Exo app takes
/// (<see cref="IdentityFiles.StateMutex"/>), so a PC set up, linked or changed in one app is the same in any other at once.
/// Writes merge with what is on disk: the device list takes every change another app saved meanwhile, and a sync key
/// never goes back to an older epoch.
/// </summary>
public sealed class IdentityFolder(string folder, IDeviceKeyStore keys)
{
    public string Folder => folder;
    public IDeviceKeyStore Keys => keys;
    public ExoIdentity Identity { get; } = new(keys, Path.Combine(folder, IdentityFiles.State));

    /// <summary>This PC's part of the identity: its keys (the caller disposes them), the device list, the sync key and,
    /// while recovery or account sign-in is on, that box's key.</summary>
    public sealed record State(string ExoId, DeviceKeys Keys, DeviceList List, uint Epoch, byte[] SyncKey, byte[]? RecoveryBox, byte[]? AccountBox = null) : IDisposable
    {
        public string DeviceId => Keys.DeviceId;
        public void Dispose() => Keys.Dispose();
    }

    /// <summary>The identity as the folder holds it, or null when this PC isn't set up, has to be linked again, or a file
    /// doesn't check out.</summary>
    public State? Read() => Locked(() =>
    {
        var (status, linked) = Identity.Check();
        if (status != ExoIdentity.Status.Ready || linked is null || keys.Open() is not { } opened) return null;
        var list = ReadList();
        var syncKey = ReadBytes(IdentityFiles.SyncKey) is { } sealedKey ? IdentityFiles.OpenSyncKey(opened.Encryption, linked.ExoId, sealedKey) : null;
        if (list is null || list.ExoId != linked.ExoId || list.Find(opened.DeviceId) is null || syncKey is not var (epoch, key) || epoch > uint.MaxValue)
        {
            opened.Dispose();
            return null;
        }
        var recovery = ReadBytes(IdentityFiles.Recovery) is { } sealedBox ? IdentityFiles.OpenRecoveryKey(opened.Encryption, linked.ExoId, sealedBox) : null;
        var account = ReadBytes(IdentityFiles.AccountBox) is { } sealedAccount ? IdentityFiles.OpenAccountBoxKey(opened.Encryption, linked.ExoId, sealedAccount) : null;
        return new State(linked.ExoId, opened, list, (uint)epoch, key, recovery, account);
    });

    /// <summary>
    /// Saves the identity: <paramref name="list"/> merged with the file's (each change saved there meanwhile is taken),
    /// the sync key unless the file already holds a newer epoch, the recovery box's and the account box's keys when given
    /// (each file goes when the list has no such entry), and identity.json when <paramref name="linked"/> (a setup or a
    /// link). Answers what the folder holds now.
    /// </summary>
    public (DeviceList List, uint Epoch, byte[] SyncKey) Save(DeviceKeys mine, string exoId, DeviceList list, uint epoch, byte[] syncKey, byte[]? recoveryBox = null, bool linked = false,
        byte[]? accountBox = null) => Locked(() =>
    {
        Directory.CreateDirectory(folder);
        if (ReadList() is { } onDisk && onDisk.ExoId == exoId)
            foreach (var change in onDisk.Changes) list.Take(change);
        Write(IdentityFiles.DeviceList, IdentityFiles.DeviceListFile(list));
        if (ReadBytes(IdentityFiles.SyncKey) is { } sealedKey && IdentityFiles.OpenSyncKey(mine.Encryption, exoId, sealedKey) is var (kept, keptKey) && kept > epoch)
            (epoch, syncKey) = ((uint)kept, keptKey);
        else Write(IdentityFiles.SyncKey, IdentityFiles.SealSyncKey(mine.Encryption.PublicKey, exoId, epoch, syncKey));
        if (list.CodeNumber is null) DeletionGate.DeleteFile(Path.Combine(folder, IdentityFiles.Recovery));
        else if (recoveryBox is not null) Write(IdentityFiles.Recovery, IdentityFiles.SealRecoveryKey(mine.Encryption.PublicKey, exoId, recoveryBox));
        if (list.AccountNumber is null) DeletionGate.DeleteFile(Path.Combine(folder, IdentityFiles.AccountBox));
        else if (accountBox is not null) Write(IdentityFiles.AccountBox, IdentityFiles.SealAccountBoxKey(mine.Encryption.PublicKey, exoId, accountBox));
        if (linked) Identity.SaveLinked(exoId, mine);
        return (list, epoch, syncKey);
    });

    /// <param name="List">The device list as the folder holds it now.</param>
    /// <param name="Resigned">This PC's own changes signed again on top of the chain, to put in the profile box.</param>
    /// <param name="Removed">This PC is no longer on the list: it leaves the profile (<see cref="Forget"/>).</param>
    public sealed record Taken(DeviceList List, IReadOnlyList<JsonObject> Resigned, bool Removed);

    /// <summary>
    /// Device-list changes from the profile box (another PC's, or this PC's from another app), taken into the folder's
    /// list in <see cref="DeviceList.InTakeOrder"/>, and this PC's own changes that lost a fork signed again on top
    /// (<see cref="DeviceList.Resign"/>), all in one hold of the mutex every Exo app takes. Two apps on this PC taking
    /// the same fork would otherwise both sign the same action again: the same change (its hash leaves out the sig), but
    /// a second TPM signature, a second put and two files a sig apart. Under the mutex the second merges the first's
    /// re-sign from disk first, and Resign passes over an action already on the chain. Every app takes the profile's
    /// device changes through this, and signs new ones through <see cref="Make"/>, never on the list it read alone.
    /// </summary>
    public Taken TakeChanges(State state, IEnumerable<JsonObject> changes) => Locked(() =>
    {
        var list = state.List;
        var lost = MergeFile(state);
        foreach (var change in DeviceList.InTakeOrder(list, changes)) Into(list, change, lost);
        var resigned = lost.Count > 0 ? list.Resign(lost, state.Keys.Identity) : [];
        var removed = list.Find(state.DeviceId) is null;
        if (!removed)
        {
            Directory.CreateDirectory(folder);
            Write(IdentityFiles.DeviceList, IdentityFiles.DeviceListFile(list));
        }
        return new Taken(list, resigned, removed);
    });

    /// <param name="Change">The new change, after the head every app on this PC has; null when the list refused it.</param>
    /// <param name="Resigned">This PC's own changes that the merge found had lost a fork, signed again first: they go in
    /// the profile box even when the change was refused, or this PC's chain drifts from everyone else's.</param>
    /// <param name="Refused">Why the list refused the change (say, a rename of a PC the merge just removed).</param>
    public sealed record Made(JsonObject? Change, IReadOnlyList<JsonObject> Resigned, string? Refused = null)
    {
        /// <summary>What goes in the profile box, in this order: the re-signs, then the change.</summary>
        public IReadOnlyList<JsonObject> ToPut => Change is null ? Resigned : [.. Resigned, Change];
    }

    /// <summary>
    /// A new device-list change by this PC (a rename, a remove, an add; <paramref name="make"/> signs it on the list it
    /// is given), in one hold of the mutex: device-list.json is merged in first, so the change goes after the head every
    /// app on this PC has, and the list is written with it before the hold ends. Signed at a head another app had already
    /// moved past, it would be a second change after one head: an equivocation, which every PC drops along with the
    /// first. The caller puts <see cref="Made.ToPut"/> in the profile box after this returns, a refused change's
    /// re-signs too, then reports <see cref="Made.Refused"/>.
    /// </summary>
    public Made Make(State state, Func<DeviceList, JsonObject> make) => Locked(() =>
    {
        var list = state.List;
        var lost = MergeFile(state);
        var resigned = lost.Count > 0 ? list.Resign(lost, state.Keys.Identity) : [];
        JsonObject? change = null;
        string? refused = null;
        try { change = make(list); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { refused = e.Message; }
        Directory.CreateDirectory(folder);
        Write(IdentityFiles.DeviceList, IdentityFiles.DeviceListFile(list));
        return new Made(change, resigned, refused);
    });

    /// <summary>device-list.json's changes taken into the state's list; answers this PC's changes they replaced.</summary>
    private List<JsonObject> MergeFile(State state)
    {
        var lost = new List<JsonObject>();
        if (ReadList() is { } onDisk && onDisk.ExoId == state.ExoId)
            foreach (var change in onDisk.Changes) Into(state.List, change, lost);
        return lost;
    }

    private static void Into(DeviceList list, JsonObject change, List<JsonObject> lost)
    {
        if (list.Take(change) is { Result: DeviceList.Result.Replaced, Dropped: var dropped }) lost.AddRange(dropped);
    }

    /// <summary>This PC left the identity (another PC or the recovery code removed it): its files go, so every app reads
    /// it as not set up, and its account session with them, so a removed PC can't sign straight back in. Its keys stay;
    /// they're this PC's, for its next setup or link.</summary>
    public void Forget() => Locked(() =>
    {
        foreach (var file in new[] { IdentityFiles.State, IdentityFiles.SyncKey, IdentityFiles.DeviceList, IdentityFiles.Recovery, IdentityFiles.Account, IdentityFiles.AccountBox })
            DeletionGate.DeleteFile(Path.Combine(folder, file));
        return true;
    });

    /// <summary>This PC's Exo account session, which every Exo app on it shares; <paramref name="mine"/> are this PC's
    /// keys (<see cref="ExoIdentity.KeysForSetup"/>), since a PC signs in before it has an identity.</summary>
    public void SaveAccount(DeviceKeys mine, AccountClient.Session session) => Locked(() =>
    {
        Directory.CreateDirectory(folder);
        Write(IdentityFiles.Account, IdentityFiles.SealAccount(mine.Encryption.PublicKey, mine.DeviceId, session));
        return true;
    });

    /// <summary>The session, or null when this PC isn't signed in (or its keys or the file don't check out).</summary>
    public AccountClient.Session? ReadAccount() => Locked(() =>
    {
        if (ReadBytes(IdentityFiles.Account) is not { } file || keys.Open() is not { } opened) return null;
        using (opened) return IdentityFiles.OpenAccount(opened.Encryption, opened.DeviceId, file);
    });

    /// <summary>Signed out: the session goes; the identity stays as it is.</summary>
    public void ForgetAccount() => Locked(() =>
    {
        DeletionGate.DeleteFile(Path.Combine(folder, IdentityFiles.Account));
        return true;
    });

    /// <summary><c>profile-card.json</c> written whole (<see cref="IdentityFiles.Card"/>), or taken away with null.</summary>
    public void SaveCard(byte[]? json) => Locked(() =>
    {
        if (json is null) DeletionGate.DeleteFile(Path.Combine(folder, IdentityFiles.Card));
        else
        {
            Directory.CreateDirectory(folder);
            Write(IdentityFiles.Card, json);
        }
        return true;
    });

    private DeviceList? ReadList() => ReadBytes(IdentityFiles.DeviceList) is { } bytes ? IdentityFiles.ReadDeviceList(bytes) : null;

    private byte[]? ReadBytes(string file)
    {
        try { return File.ReadAllBytes(Path.Combine(folder, file)); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private void Write(string file, byte[] bytes)
    {
        var path = Path.Combine(folder, file);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static T Locked<T>(Func<T> work)
    {
        using var mutex = new Mutex(false, IdentityFiles.StateMutex);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("Another Exo app is still writing the identity.");
        }
        catch (AbandonedMutexException) { /* its holder exited mid-write; every file is written whole, so what's there reads */ }
        try { return work(); }
        finally { mutex.ReleaseMutex(); }
    }
}

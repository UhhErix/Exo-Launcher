using System.Text;
using System.Text.Json.Nodes;
using Exo.Identity;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the device list (plan §2.3) could let the wrong PC in or keep a removed one, with nothing on screen:
/// - a record naming keys its device doesn't hold, or a change signed by a device that isn't (or is no longer) in the
///   list, or by nobody, is taken;
/// - a change is replayed, reordered, or moved to another identity (another Exo ID, another place in the chain);
/// - the recovery code loses its place as the root: a PC removes, renames or replaces its entry, the entry renames, there
///   are two entries, a record says "recovery": false, or a stolen PC wins a race against it;
/// - two devices that changed the list at once end up with different lists: plan §2.13's rule must pick the same winner
///   from the same set of changes whatever order they arrive in, three- and four-way forks included (a pairwise rule
///   can go round in a circle there);
/// - a PC being removed wins its race by grinding a low hash, or two devices removing each other are decided by hash;
/// - a losing branch comes back changed (another time, out of order) or brings back a change that no longer applies, or
///   two losers by different authors are signed again in an order that depends on who came first;
/// - a device that signs two different changes after the same one (an equivocation) gets one of them in, or gets them
///   dropped in one arrival order and not the other, or is locked out for good;
/// - two Exo apps on one PC (one identity key) take the same fork at once and both sign the lost change again (the same
///   change twice: a wasted signature and put), or one signs a new change at a head the other has already moved past:
///   two different changes after one head, an equivocation of their own making;
/// - one change counted as two because its signature differs (signed again, or the high-s twin anyone can make);
/// - a chain that loads differs from the one that was written (the same canonical bytes, the same Exo ID);
/// - a device name hides text (zero-width and other invisible characters) or refuses an ordinary emoji.
/// </summary>
public sealed class IdentityDeviceListTests
{
    private static JsonObject Pc(IDeviceSigner key, string name = "Desk PC")
    {
        using var enc = TestKeys.Decrypter();
        return Device.Create(key, enc.PublicKey, name, ["launcher"], 1_761_000_000);
    }

    private static string Id(IDeviceSigner key) => Device.IdOf(key.PublicKey);

    [Fact]
    public void TheFirstPcStartsTheIdentityAndNamesIt()
    {
        using var first = TestKeys.Signer();
        var list = DeviceList.Start(first, Pc(first), 1_761_000_000);
        Assert.Equal(Device.ExoIdOf(first.PublicKey), list.ExoId);
        Assert.Equal(26, list.ExoId.Length);
        Assert.Single(list.Devices);
        Assert.Equal("Desk PC", list.Find(Id(first))!.Name);

        using var other = TestKeys.Signer();
        Assert.Throws<ArgumentException>(() => DeviceList.Start(first, Pc(other), 1));
    }

    [Fact]
    public void AddRenameAndRemoveTravelAndLoadTheSame()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        var list = DeviceList.Start(a, Pc(a), 100);
        list.Add(a, Pc(b, "Laptop"), 101);
        list.Rename(b, Id(b), "Travel laptop", 102);
        list.Remove(b, Id(a), 103);
        Assert.Equal(new[] { "Travel laptop" }, list.Devices.Select(d => d.Name));

        // Through the wire as text and back.
        var text = list.Changes.Select(c => Encoding.UTF8.GetString(CanonicalJson.Serialize(c))).ToList();
        var loaded = DeviceList.Load([.. text.Select(t => CanonicalJson.ParseObject(Encoding.UTF8.GetBytes(t))!)])!;
        Assert.Equal(list.Head, loaded.Head);
        Assert.Equal(list.ExoId, loaded.ExoId);
        Assert.Equal(new[] { "Travel laptop" }, loaded.Devices.Select(d => d.Name));

        // A removed PC signs nothing more.
        Assert.Throws<InvalidOperationException>(() => list.Rename(a, Id(b), "x", 104));
    }

    [Fact]
    public void ARecordMustBeSignedByItsOwnKey()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        var record = Pc(b);
        Assert.NotNull(Device.Read(record));
        var forged = (JsonObject)record.DeepClone();
        forged["name"] = "Someone else";
        Assert.Null(Device.Read(forged));
        var swapped = (JsonObject)record.DeepClone();
        swapped["sigPub"] = Pc(a)["sigPub"]!.DeepClone();
        Assert.Null(Device.Read(swapped));
        var extra = (JsonObject)record.DeepClone();
        extra["admin"] = true;
        Assert.Null(Device.Read(extra));
        var keyless = (JsonObject)record.DeepClone();
        keyless.Remove("encPub");
        Assert.Null(Device.Read(keyless));
        // The recovery flag is there only as true: false, even signed by the record's own key, is a second spelling.
        var notRecovery = (JsonObject)record.DeepClone();
        notRecovery.Remove("sig");
        notRecovery["recovery"] = false;
        Assert.Null(Device.Read(CanonicalJson.Sign(notRecovery, Device.Label, b)));
        var list = DeviceList.Start(a, Pc(a), 1);
        Assert.Throws<InvalidOperationException>(() => list.Add(a, forged, 2));
    }

    [Fact]
    public void AChangeFromOutsideTheListOrOutOfPlaceIsRefused()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        using var outsider = TestKeys.Signer();
        var list = DeviceList.Start(a, Pc(a), 1);
        var mirror = DeviceList.Load(list.Changes)!;
        var add = list.Add(a, Pc(b), 2);

        // Signed by a key that isn't in the list: refused, the list unchanged.
        var outsiders = DeviceList.Start(outsider, Pc(outsider), 1);
        var foreign = outsiders.Add(outsider, Pc(b), 2);
        Assert.Equal(DeviceList.Result.Invalid, mirror.Take(foreign).Result);
        // A changed byte of a real change.
        var changed = (JsonObject)add.DeepClone();
        changed["at"] = 3;
        Assert.Equal(DeviceList.Result.Invalid, mirror.Take(changed).Result);
        Assert.Equal(DeviceList.Result.Appended, mirror.Take(add).Result);
        Assert.Equal(DeviceList.Result.Known, mirror.Take(add).Result);
        Assert.Equal(list.Head, mirror.Head);
        // Ahead of what this chain has: the sender must send the ones in between.
        var later = list.Rename(a, Id(b), "B", 4);
        var latest = list.Rename(a, Id(b), "B2", 5);
        Assert.Equal(DeviceList.Result.Gap, mirror.Take(latest).Result);
        Assert.Equal(DeviceList.Result.Appended, mirror.Take(later).Result);
        Assert.Equal(DeviceList.Result.Appended, mirror.Take(latest).Result);
        Assert.Null(DeviceList.Load([list.Changes[0], list.Changes[2]]));
    }

    [Fact]
    public void TheRecoveryCodeIsTheRootAndNoPcCanRemoveOrReplaceIt()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        using var c = TestKeys.Signer();
        var (_, code) = RecoveryCode.New();
        using var recovery = code.EntryKey();
        var recoveryId = Id(recovery);
        var list = DeviceList.Start(a, Pc(a), 1);
        // A PC sets the first code up.
        list.Add(a, code.Entry(2), 2);
        list.Add(a, Pc(b), 3);

        Assert.Equal(1u, list.CodeNumber);

        // No PC removes, renames or replaces it, or adds a second; the entry renames nothing, itself included.
        var (_, other) = RecoveryCode.New();
        Assert.Throws<InvalidOperationException>(() => list.Add(a, other.Entry(4), 4));
        Assert.Throws<InvalidOperationException>(() => list.Remove(b, recoveryId, 4));
        Assert.Throws<InvalidOperationException>(() => list.Rename(b, recoveryId, "Old code", 4));
        Assert.Throws<InvalidOperationException>(() => list.Rename(recovery, Id(a), "x", 4));
        Assert.Throws<InvalidOperationException>(() => list.Rename(recovery, recoveryId, "x", 4));

        // The code removes any PC and brings one in.
        list.Remove(recovery, Id(a), 5);
        list.Add(recovery, Pc(c), 6);
        Assert.Equal(new[] { Id(b), Id(c), recoveryId }.Order(), list.Devices.Select(d => d.Id).Order());

        // It replaces itself with a new code's entry, the second code; the old one then signs nothing.
        using var next = other.EntryKey();
        list.Add(recovery, other.Entry(7), 7);
        Assert.Null(list.Find(recoveryId));
        Assert.True(list.Find(Id(next))!.Recovery);
        Assert.Equal(2u, list.CodeNumber);
        Assert.Throws<InvalidOperationException>(() => list.Remove(recovery, Id(b), 8));
        // And recovery off is the code's own call: its entry removes itself.
        list.Remove(next, Id(next), 9);
        Assert.DoesNotContain(list.Devices, d => d.Recovery);
        Assert.Null(list.CodeNumber);
        // A PC may then set up a third code.
        var (_, third) = RecoveryCode.New();
        list.Add(b, third.Entry(10), 10);
        Assert.Equal(3u, list.CodeNumber);
        // A PC removing itself leaves the identity.
        list.Remove(c, Id(c), 11);
        Assert.Null(list.Find(Id(c)));
        Assert.Throws<InvalidOperationException>(() => list.Rename(c, Id(b), "x", 12));
        Assert.NotNull(DeviceList.Load(list.Changes));
    }

    [Fact]
    public void AStolenOldPcWinsAMutualRaceAndTheCodeTakesTheListBack()
    {
        using var stolen = TestKeys.Signer();
        using var owner = TestKeys.Signer();
        var (_, code) = RecoveryCode.New();
        using var recovery = code.EntryKey();
        var start = DeviceList.Start(stolen, Pc(stolen), 1);
        start.Add(stolen, Pc(owner), 2);
        start.Add(owner, code.Entry(3), 3);
        DeviceList Copy() => DeviceList.Load(start.Changes)!;

        // Rule 2's policy: the stolen PC was added first, so it wins when the two remove each other.
        var thief = Copy().Remove(stolen, Id(owner), 10);
        var mine = Copy().Remove(owner, Id(stolen), 10);
        Assert.Equal(CanonicalJson.Hash(thief), CanonicalJson.Hash(Copy().Winner([mine, thief])!));

        // With the code typed at the same time, the code wins: the thief's change is struck, whatever the hashes.
        var taken = HashAbove(start, (l, at) => l.Remove(recovery, Id(stolen), at), CanonicalJson.Hash(thief), CanonicalJson.Hash(mine));
        foreach (var order in IdentityVectorsTests.Orders([thief, mine, taken]))
        {
            var list = Copy();
            foreach (var change in order) list.Take(change);
            Assert.Null(list.Find(Id(stolen)));
            Assert.NotNull(list.Find(Id(owner)));
        }

        // After the race was lost: the owner's PC is out, the stolen one in. The code removes it then too, and a PC it
        // brings in (the owner's again) makes the new sync key; nothing the stolen PC signs gets past the code.
        var lost = Copy();
        lost.Take(thief);
        Assert.Null(lost.Find(Id(owner)));
        Assert.Throws<InvalidOperationException>(() => DeviceList.Load(lost.Changes)!.Remove(stolen, Id(recovery), 11));
        var back = DeviceList.Load(lost.Changes)!;
        var removeStolen = back.Remove(recovery, Id(stolen), 12);
        var thiefAgain = DeviceList.Load(lost.Changes)!.Add(stolen, Pc(TestKeys.Signer(), "Thief's PC"), 12);
        Assert.Equal(CanonicalJson.Hash(removeStolen), CanonicalJson.Hash(DeviceList.Load(lost.Changes)!.Winner([thiefAgain, removeStolen])!));
        back.Add(recovery, Pc(owner), 13);
        Assert.Equal(new[] { Id(owner), Id(recovery) }.Order(), back.Devices.Select(d => d.Id).Order());
    }

    [Fact]
    public void TwoChangesAtOnceEndTheSameEverywhere()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        var onA = DeviceList.Start(a, Pc(a), 1);
        onA.Add(a, Pc(b), 2);
        var onB = DeviceList.Load(onA.Changes)!;

        // Both rename at once: two changes with seq 2 after the same head.
        var fromA = onA.Rename(a, Id(a), "A's name", 3);
        var fromB = onB.Rename(b, Id(b), "B's name", 3);
        var aWins = string.CompareOrdinal(CanonicalJson.Hash(fromA), CanonicalJson.Hash(fromB)) < 0;
        var (winner, loser, winnerList, loserList, loserKey) = aWins ? (fromA, fromB, onA, onB, b) : (fromB, fromA, onB, onA, a);

        Assert.Equal(DeviceList.Result.Kept, winnerList.Take(loser).Result);
        var taken = loserList.Take(winner);
        Assert.Equal(DeviceList.Result.Replaced, taken.Result);
        Assert.Equal(CanonicalJson.Hash(loser), CanonicalJson.Hash(taken.Dropped.Single()));
        Assert.Equal(winnerList.Head, loserList.Head);

        // The loser's author signs it again on top, keeping its time; both lists then agree, with both renames.
        var again = loserList.Resign(taken.Dropped, loserKey).Single();
        Assert.Equal(3, Device.Int(again, "at"));
        Assert.Equal(3, Device.Int(again, "seq"));
        Assert.Equal(DeviceList.Result.Appended, winnerList.Take(again).Result);
        Assert.Equal(winnerList.Head, loserList.Head);
        Assert.Equal(new[] { "A's name", "B's name" }, onA.Devices.Select(d => d.Name).Order());
        Assert.Equal(new[] { "A's name", "B's name" }, onB.Devices.Select(d => d.Name).Order());
        // Signing the same action again does nothing: it's in the chain.
        Assert.Empty(loserList.Resign(taken.Dropped, loserKey));
    }

    [Fact]
    public async Task TwoAppsOnOnePcTakingOneForkSignItAgainOnce()
    {
        // PCs A and B rename themselves at once.
        var (storeA, storeB) = (new TestKeys.Store(), new TestKeys.Store());
        using var a = storeA.OpenOrCreate();
        using var b = storeB.OpenOrCreate();
        var list = DeviceList.Start(a.Identity, Pc(a.Identity), 1);
        list.Add(a.Identity, Pc(b.Identity), 2);
        var fromA = DeviceList.Load(list.Changes)!.Rename(a.Identity, Id(a.Identity), "A's name", 3);
        var fromB = DeviceList.Load(list.Changes)!.Rename(b.Identity, Id(b.Identity), "B's name", 3);

        // The PC whose change loses the fork runs two Exo apps on one identity folder.
        var aLoses = CanonicalJson.Hash(DeviceList.Load(list.Changes)!.Winner([fromA, fromB])!) == CanonicalJson.Hash(fromB);
        var (store, mine, lost, won) = aLoses ? (storeA, a, fromA, fromB) : (storeB, b, fromB, fromA);
        var path = Path.Combine(TestDataDirectory.Root, "identity-" + Guid.NewGuid().ToString("N"));
        var launcher = new IdentityFolder(path, store);
        var other = new IdentityFolder(path, store);
        list.Take(lost);
        launcher.Save(mine, list.ExoId, list, 1, new byte[32], linked: true);

        try
        {
            // Both apps take the winning change at the same moment: only one of them signs the lost rename again.
            using var inLauncher = launcher.Read()!;
            using var inOther = other.Read()!;
            var taken = await Task.WhenAll(
                Task.Run(() => launcher.TakeChanges(inLauncher, [won])),
                Task.Run(() => other.TakeChanges(inOther, [won])));
            Assert.Single(taken.SelectMany(t => t.Resigned));
            Assert.All(taken, t => Assert.False(t.Removed));

            // On disk: both renames, and never two of this PC's changes after the same head (an equivocation).
            using var after = launcher.Read()!;
            foreach (var sameHead in after.List.Changes.Where(c => Device.Text(c, "by") == Id(mine.Identity)).GroupBy(c => Device.Text(c, "prev")))
                Assert.Single(sameHead);
            Assert.Equal(new[] { "A's name", "B's name" }, after.List.Devices.Select(d => d.Name).Order());
            Assert.All(taken, t => Assert.Equal(after.List.Head, t.List.Head));

            // Taking it again re-signs nothing: the action is on the chain.
            using var later = other.Read()!;
            Assert.Empty(other.TakeChanges(later, [won, lost]).Resigned);
        }
        finally { DeletionGate.DeleteDirectory(path, recursive: true); }
    }

    [Fact]
    public void TwoAppsOnOnePcSigningNewChangesNeverForkThemselves()
    {
        var store = new TestKeys.Store();
        using var mine = store.OpenOrCreate();
        using var b = TestKeys.Signer();
        var path = Path.Combine(TestDataDirectory.Root, "identity-" + Guid.NewGuid().ToString("N"));
        var launcher = new IdentityFolder(path, store);
        var other = new IdentityFolder(path, store);
        var list = DeviceList.Start(mine.Identity, Pc(mine.Identity), 1);
        list.Add(mine.Identity, Pc(b), 2);
        launcher.Save(mine, list.ExoId, list, 1, new byte[32], linked: true);

        try
        {
            // Both apps read the list, then each signs a rename: the other's comes second, on the list it read before
            // Launcher's was written, and still goes after it.
            using var inLauncher = launcher.Read()!;
            using var inOther = other.Read()!;
            var first = launcher.Make(inLauncher, l => l.Rename(mine.Identity, Id(mine.Identity), "Desk", 3));
            var second = other.Make(inOther, l => l.Rename(mine.Identity, Id(b), "Laptop", 4));
            Assert.Empty(first.Resigned);
            Assert.Empty(second.Resigned);
            Assert.Equal(CanonicalJson.Hash(first.Change!), Device.Text(second.Change!, "prev"));

            using var after = launcher.Read()!;
            Assert.Equal(CanonicalJson.Hash(second.Change!), after.List.Head);
            Assert.Equal(new[] { "Desk", "Laptop" }, after.List.Devices.Select(d => d.Name).Order());
        }
        finally { DeletionGate.DeleteDirectory(path, recursive: true); }
    }

    [Fact]
    public void ARefusedChangeStillHandsBackTheReSignItsMergeMade()
    {
        // A and B rename themselves at once; the PC whose rename loses has it on its list and on disk.
        var (storeA, storeB) = (new TestKeys.Store(), new TestKeys.Store());
        using var a = storeA.OpenOrCreate();
        using var b = storeB.OpenOrCreate();
        var list = DeviceList.Start(a.Identity, Pc(a.Identity), 1);
        list.Add(a.Identity, Pc(b.Identity), 2);
        var fromA = DeviceList.Load(list.Changes)!.Rename(a.Identity, Id(a.Identity), "A's name", 3);
        var fromB = DeviceList.Load(list.Changes)!.Rename(b.Identity, Id(b.Identity), "B's name", 3);
        var aLoses = CanonicalJson.Hash(DeviceList.Load(list.Changes)!.Winner([fromA, fromB])!) == CanonicalJson.Hash(fromB);
        var (store, mine, lost, won) = aLoses ? (storeA, a, fromA, fromB) : (storeB, b, fromB, fromA);
        var path = Path.Combine(TestDataDirectory.Root, "identity-" + Guid.NewGuid().ToString("N"));
        var folder = new IdentityFolder(path, store);
        var mineList = DeviceList.Load(list.Changes)!;
        mineList.Take(lost);
        folder.Save(mine, list.ExoId, mineList, 1, new byte[32], linked: true);

        try
        {
            using var state = folder.Read()!;
            // Another app on this PC took the winner meanwhile.
            var theirs = DeviceList.Load(list.Changes)!;
            theirs.Take(won);
            File.WriteAllBytes(Path.Combine(path, IdentityFiles.DeviceList), IdentityFiles.DeviceListFile(theirs));

            // This app renames a PC that isn't on the list: refused, and the merge's re-sign comes back all the same.
            var made = folder.Make(state, l => l.Rename(mine.Identity, new string('0', 32), "Nobody", 4));
            Assert.Null(made.Change);
            Assert.NotNull(made.Refused);
            var again = Assert.Single(made.Resigned);
            Assert.Equal(made.Resigned, made.ToPut);
            using var after = folder.Read()!;
            Assert.Equal(CanonicalJson.Hash(again), after.List.Head);
        }
        finally { DeletionGate.DeleteDirectory(path, recursive: true); }
    }

    [Fact]
    public void ARemoveBeatsAConcurrentChangeByTheDeviceItRemovesWhateverTheHashes()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        var start = DeviceList.Start(a, Pc(a), 1);
        start.Add(a, Pc(b), 2);
        var bId = Id(b);
        var onA = DeviceList.Load(start.Changes)!;
        var onB = DeviceList.Load(start.Changes)!;
        var remove = onA.Remove(a, bId, 3);
        // B, about to be removed, makes its own change with a lower hash (as grinding would): it still loses.
        var race = HashBelow(start, (l, at) => l.Rename(b, bId, "Still here", at), CanonicalJson.Hash(remove));
        Assert.Equal(DeviceList.Result.Appended, onB.Take(race).Result);

        Assert.Equal(DeviceList.Result.Kept, onA.Take(race).Result);
        var taken = onB.Take(remove);
        Assert.Equal(DeviceList.Result.Replaced, taken.Result);
        Assert.Equal(onA.Head, onB.Head);
        Assert.Null(onB.Find(bId));
        // Its author is gone, so the lost change can't come back.
        Assert.Empty(onB.Resign(taken.Dropped, b));
    }

    [Fact]
    public void TwoDevicesRemovingEachOtherTheOneAddedEarlierWins()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        using var c = TestKeys.Signer();
        var start = DeviceList.Start(a, Pc(a), 1);
        start.Add(a, Pc(b), 2);
        start.Add(a, Pc(c), 3);
        var (bId, cId) = (Id(b), Id(c));
        var onB = DeviceList.Load(start.Changes)!;
        var onC = DeviceList.Load(start.Changes)!;
        var bRemovesC = onB.Remove(b, cId, 4);
        // C (added at seq 2, after B at seq 1) removes B with the lower hash: B's removal still wins.
        var cRemovesB = HashBelow(start, (l, at) => l.Remove(c, bId, at), CanonicalJson.Hash(bRemovesC));
        Assert.Equal(DeviceList.Result.Appended, onC.Take(cRemovesB).Result);

        Assert.Equal(DeviceList.Result.Kept, onB.Take(cRemovesB).Result);
        Assert.Equal(DeviceList.Result.Replaced, onC.Take(bRemovesC).Result);
        Assert.Equal(onB.Head, onC.Head);
        Assert.NotNull(onC.Find(bId));
        Assert.Null(onC.Find(cId));
    }

    [Fact]
    public void AThreeWayCycleSettlesTheSameInEveryArrivalOrder()
    {
        // A's remove beats B's change, B's beats C's by hash, C's beats A's by hash. Pairwise, the head
        // would be whichever came last of the cycle; as a set, B is struck and the lower hash of A's and C's wins.
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        using var c = TestKeys.Signer();
        var start = DeviceList.Start(a, Pc(a), 1);
        start.Add(a, Pc(b), 2);
        start.Add(a, Pc(c), 3);
        var removeB = DeviceList.Load(start.Changes)!.Remove(a, Id(b), 10);
        var cRenames = HashBelow(start, (l, at) => l.Rename(c, Id(c), "C", at), CanonicalJson.Hash(removeB));
        var bRenames = HashBelow(start, (l, at) => l.Rename(b, Id(b), "B", at), CanonicalJson.Hash(cRenames));

        var heads = IdentityVectorsTests.Orders([removeB, bRenames, cRenames]).Select(order =>
        {
            var list = DeviceList.Load(start.Changes)!;
            foreach (var change in order) list.Take(change);
            return list.Head;
        }).Distinct().ToList();
        Assert.Equal(CanonicalJson.Hash(cRenames), Assert.Single(heads));
    }

    [Fact]
    public void FourWayForksAndRandomForksSettleTheSameInEveryArrivalOrder()
    {
        var random = new Random(2026_10_28);
        for (var round = 0; round < 40; round++)
        {
            var keys = Enumerable.Range(0, 5).Select(_ => TestKeys.Signer()).ToList();
            var (_, code) = RecoveryCode.New();
            using var recovery = code.EntryKey();
            var start = DeviceList.Start(keys[0], Pc(keys[0]), 1);
            for (var i = 1; i < keys.Count; i++) start.Add(keys[0], Pc(keys[i]), 1 + i);
            start.Add(keys[0], code.Entry(9), 9);
            IDeviceSigner[] authors = [.. keys, recovery];

            // 3 or 4 changes after the same head, each by a random author: removes (any target, mutual and cyclic ones
            // included), renames and adds; an author picked twice equivocates.
            var fork = new List<JsonObject>();
            while (fork.Count < 3 + round % 2)
            {
                var by = authors[random.Next(authors.Length)];
                var on = DeviceList.Load(start.Changes)!;
                var at = 100L + random.Next(1000);
                try
                {
                    var change = random.Next(3) switch
                    {
                        0 => on.Remove(by, Id(keys[random.Next(keys.Count)]), at),
                        1 => on.Rename(by, Id(keys[random.Next(keys.Count)]), "PC " + at, at),
                        _ => on.Add(by, Pc(TestKeys.Signer(), "New " + at), at),
                    };
                    if (fork.All(f => CanonicalJson.Hash(f) != CanonicalJson.Hash(change))) fork.Add(change);
                }
                catch (InvalidOperationException) { /* not one this author may sign */ }
            }

            var results = IdentityVectorsTests.Orders(fork).Select(order =>
            {
                var list = DeviceList.Load(start.Changes)!;
                foreach (var change in order) Assert.NotEqual(DeviceList.Result.Invalid, list.Take(change).Result);
                return (list.Head, Devices: string.Join(",", list.Devices.Select(d => d.Id).Order()));
            }).Distinct().ToList();
            Assert.Single(results);
            var winner = DeviceList.Load(start.Changes)!.Winner(fork);
            Assert.Equal(winner is null ? start.Head : CanonicalJson.Hash(winner), results[0].Head);
            foreach (var key in keys) key.Dispose();
        }
    }

    [Fact]
    public void ALosingBranchIsSignedAgainInOrderAndWhatNoLongerAppliesIsDropped()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        using var c = TestKeys.Signer();
        using var d = TestKeys.Signer();
        var start = DeviceList.Start(a, Pc(a), 1);
        start.Add(a, Pc(b), 2);
        start.Add(a, Pc(c), 3);
        var onA = DeviceList.Load(start.Changes)!;
        var onB = DeviceList.Load(start.Changes)!;
        // B, offline, renames C, renames itself and adds D; A removes C at the same time.
        var removeC = onA.Remove(a, Id(c), 10);
        var renameC = HashAbove(start, (l, at) => l.Rename(b, Id(c), "Old tower", at), CanonicalJson.Hash(removeC), "");
        onB.Take(renameC);
        var renameB = onB.Rename(b, Id(b), "Travel laptop", 21);
        var addD = onB.Add(b, Pc(d, "Den PC"), 22);

        foreach (var order in IdentityVectorsTests.Orders([removeC, renameC, renameB, addD]))
        {
            var list = DeviceList.Load(start.Changes)!;
            var pending = new Queue<JsonObject>(order);
            for (var tries = 0; pending.Count > 0 && tries < 20; tries++)
                if (list.Take(pending.Peek()).Result == DeviceList.Result.Gap) pending.Enqueue(pending.Dequeue());
                else pending.Dequeue();
            Assert.Equal(CanonicalJson.Hash(removeC), list.Head);
        }

        var taken = onB.Take(removeC);
        Assert.Equal(DeviceList.Result.Replaced, taken.Result);
        Assert.Equal(new[] { renameC, renameB, addD }.Select(CanonicalJson.Hash), taken.Dropped.Select(CanonicalJson.Hash));
        var again = onB.Resign(taken.Dropped, b);
        // The rename of C no longer applies (C is gone): dropped. The rest keep their content and time, in order.
        Assert.Equal(2, again.Count);
        Assert.Equal(new[] { "rename", "add" }, again.Select(x => Device.Text(x, "op")));
        Assert.Equal(new[] { 21L, 22L }, again.Select(x => Device.Int(x, "at")!.Value));
        Assert.Equal(new[] { 4L, 5L }, again.Select(x => Device.Int(x, "seq")!.Value));
        Assert.Equal(CanonicalJson.Hash(removeC), Device.Text(again[0], "prev"));
        foreach (var change in again) Assert.Equal(DeviceList.Result.Appended, onA.Take(change).Result);
        Assert.Equal(onA.Head, onB.Head);
        Assert.Equal(new[] { "Den PC", "Desk PC", "Travel laptop" }, onA.Devices.Select(x => x.Name).Order());
    }

    [Fact]
    public void TwoLosersByDifferentAuthorsAreSignedAgainInRankOrderOneAfterAnother()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        using var c = TestKeys.Signer();
        var start = DeviceList.Start(a, Pc(a), 1);
        start.Add(a, Pc(b), 2);
        start.Add(a, Pc(c), 3);
        DeviceList Copy() => DeviceList.Load(start.Changes)!;
        // All three rename themselves at once; A's wins on the lowest hash, and C's (added after B) has a lower hash
        // than B's, so an order by hash would put C's first.
        var bRenames = Copy().Rename(b, Id(b), "B", 10);
        var cRenames = HashBelow(start, (l, at) => l.Rename(c, Id(c), "C", at), CanonicalJson.Hash(bRenames));
        var aRenames = HashBelow(start, (l, at) => l.Rename(a, Id(a), "A", at), CanonicalJson.Hash(cRenames));
        JsonObject[] fork = [cRenames, aRenames, bRenames];

        var onB = Copy();
        var onC = Copy();
        foreach (var change in fork)
        {
            onB.Take(change);
            onC.Take(change);
        }
        Assert.Equal(CanonicalJson.Hash(aRenames), onB.Head);
        Assert.Equal(new[] { bRenames, cRenames }.Select(CanonicalJson.Hash), onC.ResignOrder([cRenames, bRenames]).Select(CanonicalJson.Hash));
        // C's turn comes after B's: nothing yet.
        Assert.Empty(onC.Resign(fork, c));
        var bAgain = Assert.Single(onB.Resign(fork, b));
        Assert.Equal(DeviceList.Result.Appended, onC.Take(bAgain).Result);
        var cAgain = Assert.Single(onC.Resign(fork, c));
        Assert.Equal(CanonicalJson.Hash(bAgain), Device.Text(cAgain, "prev"));
        Assert.Equal(new long[] { 4, 5 }, new[] { bAgain, cAgain }.Select(x => Device.Int(x, "seq")!.Value));

        // The head is a function of the set: every arrival order of the five ends on C's re-sign.
        foreach (var order in IdentityVectorsTests.Orders([.. fork, bAgain, cAgain]))
        {
            var list = Copy();
            var pending = new Queue<JsonObject>(order);
            for (var tries = 0; pending.Count > 0 && tries < 50; tries++)
                if (list.Take(pending.Peek()).Result == DeviceList.Result.Gap) pending.Enqueue(pending.Dequeue());
                else pending.Dequeue();
            Assert.Equal(CanonicalJson.Hash(cAgain), list.Head);
        }
    }

    [Fact]
    public void TwoDifferentChangesByOneAuthorAfterTheSameChangeAreBothDroppedInEitherOrder()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        using var c = TestKeys.Signer();
        var start = DeviceList.Start(a, Pc(a), 1);
        start.Add(a, Pc(b), 2);
        start.Add(a, Pc(c), 3);
        DeviceList Copy() => DeviceList.Load(start.Changes)!;
        // B signs two changes at seq 3 (as a PC restored from an old image might), both with lower hashes than A's
        // change at the same time; one of them removes A, which would strike A's change if it counted.
        var aRenames = Copy().Rename(a, Id(a), "Desk", 10);
        var bRenames = HashBelow(start, (l, at) => l.Rename(b, Id(b), "Laptop", at), CanonicalJson.Hash(aRenames));
        var bRemovesA = HashBelow(start, (l, at) => l.Remove(b, Id(a), at), CanonicalJson.Hash(aRenames));

        foreach (var (first, second) in new[] { (bRenames, bRemovesA), (bRemovesA, bRenames) })
        {
            var list = Copy();
            Assert.Equal(DeviceList.Result.Appended, list.Take(first).Result);
            var taken = list.Take(second);
            // Both out: the head goes back, nothing else being there, and B is marked but stays listed.
            Assert.Equal(DeviceList.Result.Replaced, taken.Result);
            Assert.Equal(start.Head, list.Head);
            Assert.Equal([Id(b)], list.Equivocators);
            Assert.NotNull(list.Find(Id(b)));
            Assert.Empty(list.Resign(taken.Dropped, b));
            // B can't sign a third change there, and a third that arrives is out as well.
            Assert.Throws<InvalidOperationException>(() => list.Rename(b, Id(b), "Again", 11));
            Assert.Equal(DeviceList.Result.Kept, list.Take(Copy().Rename(b, Id(b), "Again", 12)).Result);
            Assert.Equal(start.Head, list.Head);
            // A's change moves the head on; B's next change after it counts again.
            Assert.Equal(DeviceList.Result.Appended, list.Take(aRenames).Result);
            Assert.Equal(CanonicalJson.Hash(aRenames), list.Head);
            var honest = list.Rename(b, Id(b), "Laptop 2", 13);
            Assert.Equal(CanonicalJson.Hash(honest), list.Head);
            Assert.NotNull(list.Find(Id(a)));
        }

        // Any order of the three ends on A's change, and the head never depended on which of B's came first.
        foreach (var order in IdentityVectorsTests.Orders([bRenames, bRemovesA, aRenames]))
        {
            var list = Copy();
            foreach (var change in order) Assert.NotEqual(DeviceList.Result.Invalid, list.Take(change).Result);
            Assert.Equal(CanonicalJson.Hash(aRenames), list.Head);
            Assert.Equal([Id(b)], list.Equivocators);
        }
        Assert.Equal(CanonicalJson.Hash(aRenames), CanonicalJson.Hash(Copy().Winner([bRenames, bRemovesA, aRenames])!));
        Assert.Null(Copy().Winner([bRenames, bRemovesA]));
    }

    [Fact]
    public void TheSameChangeSignedAgainIsTheSameChangeAndAHighSCopyIsRefused()
    {
        using var a = TestKeys.Signer();
        using var b = TestKeys.Signer();
        var list = DeviceList.Start(a, Pc(a), 1);
        var mirror = DeviceList.Load(list.Changes)!;
        var add = list.Add(a, Pc(b), 2);

        // Signed again by its author: another sig, the same content, the same hash.
        var again = CanonicalJson.Sign(add, DeviceList.ChangeLabel, a);
        Assert.NotEqual(add["sig"]!.GetValue<string>(), again["sig"]!.GetValue<string>());
        Assert.Equal(CanonicalJson.Hash(add), CanonicalJson.Hash(again));
        Assert.Equal(DeviceList.Result.Appended, mirror.Take(again).Result);
        Assert.Equal(DeviceList.Result.Known, mirror.Take(add).Result);
        Assert.Equal(list.Head, mirror.Head);

        // The (r, n - s) twin anyone could make from the signature: refused, never a second copy.
        var twin = (JsonObject)add.DeepClone();
        twin["sig"] = System.Buffers.Text.Base64Url.EncodeToString(IdentityVectorsTests.Twin(CanonicalJson.Bytes(add["sig"]!.GetValue<string>())!));
        Assert.Equal(DeviceList.Result.Invalid, DeviceList.Load([list.Changes[0]])!.Take(twin).Result);
        Assert.Null(DeviceList.Load([list.Changes[0], twin]));
    }

    [Fact]
    public void ADeviceNameIsVisibleText()
    {
        using var a = TestKeys.Signer();
        using var enc = TestKeys.Decrypter();
        var emoji64 = string.Concat(Enumerable.Repeat(Cp(0x1F600), 64));
        // 64 scalar values, whatever their UTF-16 (128 here) or UTF-8 (256) length; emoji with their joiners,
        // variation selectors and flag tags.
        foreach (var good in new[]
                 {
                     "Eric's PC " + Cp(0x1F600), emoji64, new string('x', 64),
                     Cp(0x1F9D1, 0x200D, 0x1F4BB) + " Desk", Cp(0x2764, 0xFE0F) + " PC", Cp(0x1F3F3, 0xFE0F, 0x200D, 0x1F308), Cp(0x23, 0xFE0F, 0x20E3) + "1",
                     Cp(0x1F3F4, 0xE0067, 0xE0062, 0xE0073, 0xE0063, 0xE0074, 0xE007F) + " PC", Cp(0x1F642, 0x200D, 0x2194, 0xFE0F),
                 })
            Assert.NotNull(Device.Read(Device.Create(a, enc.PublicKey, good, ["launcher", "other"], 1)));
        var bad = new List<string>
        {
            "", " lead", "trail ", Cp(0x3000) + "lead", "trail" + Cp(0xA0), "line" + Cp(0x0A) + "break", "next" + Cp(0x85) + "line", "del" + Cp(0x7F), "line" + Cp(0x2028) + "sep",
            "bidi" + Cp(0x202E) + "override", "bidi" + Cp(0x2066) + "isolate", new string('x', 65), emoji64 + Cp(0x1F600), "lone" + (char)0xD800,
            // A joiner, a selector or a tag where no emoji needs it.
            "a" + Cp(0x200D) + "b", Cp(0x1F600, 0x200D), Cp(0x200D, 0x1F600), "A" + Cp(0xFE0F), Cp(0xFE0F) + "A",
            "a" + Cp(0xE0067, 0xE007F), Cp(0x1F3F4, 0xE0067, 0xE0062) + " PC", Cp(0x1F3F4, 0xE007F),
        };
        // Every other invisible code point, anywhere.
        foreach (var c in new[]
                 {
                     0xAD, 0x34F, 0x61C, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x180B, 0x180E, 0x180F, 0x200B, 0x200C, 0x200E, 0x200F, 0x2060, 0x2064, 0x2065,
                     0x206A, 0x206F, 0x3164, 0xFE00, 0xFE0D, 0xFEFF, 0xFFA0, 0xFFF0, 0xFFF8, 0x1BCA0, 0x1BCA3, 0x1D173, 0x1D17A, 0xE0000, 0xE0001,
                     0xE001F, 0xE0080, 0xE0100, 0xE01EF, 0xE0FFF,
                 })
        {
            Assert.True(Device.Invisible(c));
            bad.Add("x" + Cp(c) + "y");
        }
        foreach (var name in bad) Assert.Throws<ArgumentException>(() => Device.Create(a, enc.PublicKey, name, [], 1));
        Assert.Throws<ArgumentException>(() => Device.Create(a, null, "No key", [], 1));
    }

    private static string Cp(params int[] scalars) => string.Concat(scalars.Select(char.ConvertFromUtf32));

    /// <summary>A change made on a fresh copy of <paramref name="start"/>, its time moved until its hash is below
    /// <paramref name="than"/>, so only the priority rule can make it lose.</summary>
    private static JsonObject HashBelow(DeviceList start, Func<DeviceList, long, JsonObject> make, string than)
    {
        for (var at = 1000L; ; at++)
        {
            var change = make(DeviceList.Load(start.Changes)!, at);
            if (string.CompareOrdinal(CanonicalJson.Hash(change), than) < 0) return change;
        }
    }

    /// <summary>The same, with its hash above both <paramref name="than"/> and <paramref name="andThan"/>.</summary>
    private static JsonObject HashAbove(DeviceList start, Func<DeviceList, long, JsonObject> make, string than, string andThan)
    {
        for (var at = 1000L; ; at++)
        {
            var change = make(DeviceList.Load(start.Changes)!, at);
            if (string.CompareOrdinal(CanonicalJson.Hash(change), than) > 0 && string.CompareOrdinal(CanonicalJson.Hash(change), andThan) > 0) return change;
        }
    }
}

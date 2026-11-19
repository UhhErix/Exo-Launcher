using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the store-helper grouping can fail:
/// - the first call thinks an empty cache is fresh (the long.MinValue age overflow) and trimming never runs;
/// - a cache older than 30 s is reused, or a fresh one rebuilt every call;
/// - a process whose parent pid was reused by the client root joins the group;
/// - a helper-named process under an unrelated parent joins the group;
/// - a grandchild helper (helper started by a helper) is missed;
/// - Battle.net's root (same file name as its renderers) is grouped as its own helper;
/// - an exited process (no identity) is grouped;
/// - a client with no helpers yields an empty group.
/// </summary>
public sealed class StoreClientHelpersTests
{
    private static ManagedProcessIdentity? Identity(int pid) => new ManagedProcessIdentity(pid, pid * 10L);

    private static List<ManagedProcessGroup> Build(
        (int Pid, int Parent, string Name)[] table,
        Func<int, int, bool>? startedBefore = null,
        Func<int, bool>? hasTypeSwitch = null,
        Func<int, ManagedProcessIdentity?>? identify = null) =>
        StoreClientHelpers.Build(table, startedBefore ?? ((_, _) => false), hasTypeSwitch ?? (_ => false), identify ?? Identity);

    [Fact]
    public void NeverBuiltIsStaleAtAnyClock()
    {
        foreach (var now in new[] { 0L, 1L, 29_999L, Environment.TickCount64, long.MaxValue / 2 })
            Assert.False(StoreClientHelpers.IsFresh(now, StoreClientHelpers.NeverBuilt));
    }

    [Fact]
    public void LongMinValueWouldHaveOverflowedIntoFresh()
    {
        // The regression: with long.MinValue the age "now - at" wraps negative, so the empty cache looked fresh forever.
        Assert.True(unchecked(Environment.TickCount64 - long.MinValue) < 0);
        Assert.NotEqual(long.MinValue, StoreClientHelpers.NeverBuilt);
    }

    [Theory]
    [InlineData(1_000, 1_000, true)]
    [InlineData(30_999, 1_000, true)]
    [InlineData(31_000, 1_000, false)]
    [InlineData(100_000, 1_000, false)]
    public void CacheLivesThirtySeconds(long now, long builtAt, bool fresh) =>
        Assert.Equal(fresh, StoreClientHelpers.IsFresh(now, builtAt));

    [Fact]
    public void GroupsSteamHelpersAndGrandchildren()
    {
        var groups = Build([
            (100, 4, "steam.exe"),
            (101, 100, "steamwebhelper.exe"),
            (102, 101, "steamwebhelper.exe"),
            (103, 100, "game.exe"),
            (104, 103, "steamwebhelper.exe"),
            (200, 4, "explorer.exe"),
            (201, 200, "steamwebhelper.exe"),
        ]);
        var steam = Assert.Single(groups);
        Assert.Equal("store-steam", steam.Key);
        Assert.Equal([101, 102], steam.Processes.Select(p => p.Pid).Order());
    }

    [Fact]
    public void ReusedParentPidDoesNotAdoptAnOlderProcess()
    {
        // 300 is a helper-named process whose real parent exited; Steam later started with that pid.
        var groups = Build(
            [(100, 4, "steam.exe"), (300, 100, "steamwebhelper.exe"), (301, 100, "steamwebhelper.exe")],
            startedBefore: (child, parent) => child == 300 && parent == 100);
        Assert.Equal([301], Assert.Single(groups).Processes.Select(p => p.Pid));
    }

    [Fact]
    public void ExitedHelperIsSkipped()
    {
        var groups = Build(
            [(100, 4, "EpicGamesLauncher.exe"), (101, 100, "EpicWebHelper.exe"), (102, 100, "msedgewebview2.exe")],
            identify: pid => pid == 101 ? null : Identity(pid));
        var epic = Assert.Single(groups);
        Assert.Equal("store-epic", epic.Key);
        Assert.Equal([102], epic.Processes.Select(p => p.Pid));
    }

    [Fact]
    public void BattleNetRenderersAreToldApartByTypeSwitch()
    {
        var groups = Build(
            [(100, 4, "Battle.net.exe"), (101, 100, "Battle.net.exe"), (102, 100, "Battle.net.exe")],
            hasTypeSwitch: pid => pid == 101);
        var bnet = Assert.Single(groups);
        Assert.Equal("store-battlenet", bnet.Key);
        Assert.Equal([101], bnet.Processes.Select(p => p.Pid));
    }

    [Fact]
    public void ClientWithoutHelpersHasNoGroup() =>
        Assert.Empty(Build([(100, 4, "GalaxyClient.exe")]));

    [Fact]
    public void NamesMatchCaseInsensitively() =>
        Assert.Single(Build([(100, 4, "STEAM.EXE"), (101, 100, "SteamWebHelper.exe")]));

    private static readonly long Grace = (long)StoreClientHelpers.BehindGrace.TotalMilliseconds;

    [Fact]
    public void AnOpenWindowLeftBehindIsTrimmedOnlyAfterTheGrace()
    {
        // Steam opened and left behind a game: in use from the moment it is first seen, quiet once the grace runs out.
        var usedAt = new Dictionary<StoreKind, long>();
        Assert.True(StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: 1_000));
        Assert.True(StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: 1_000 + Grace - 1));
        Assert.False(StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: 1_000 + Grace));
    }

    [Fact]
    public void BringingTheWindowToTheFrontStartsTheGraceOver()
    {
        var usedAt = new Dictionary<StoreKind, long>();
        StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: 0);
        Assert.False(StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: Grace));
        Assert.True(StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: true, now: Grace + 5_000));
        Assert.True(StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: 2 * Grace));
        Assert.False(StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: 2 * Grace + 5_000));
    }

    [Fact]
    public void ClosingTheWindowForgetsItSoOpeningAgainWaitsAFullGrace()
    {
        var usedAt = new Dictionary<StoreKind, long>();
        StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: 0);
        Assert.False(StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: false, inFront: false, now: 10_000));
        Assert.Empty(usedAt);
        Assert.True(StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: 10 * Grace));
    }

    [Fact]
    public void ADialogInFrontCountsEvenWithTheMainWindowClosed() =>
        // A game's Properties or a cloud-save question from Steam, with its library window in the tray.
        Assert.True(StoreClientHelpers.InUse([], StoreKind.Steam, open: false, inFront: true, now: 0));

    [Fact]
    public void EachClientKeepsItsOwnClock()
    {
        var usedAt = new Dictionary<StoreKind, long>();
        StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: 0);
        Assert.True(StoreClientHelpers.InUse(usedAt, StoreKind.Ea, open: true, inFront: false, now: Grace));
        Assert.False(StoreClientHelpers.InUse(usedAt, StoreKind.Steam, open: true, inFront: false, now: Grace));
    }
}

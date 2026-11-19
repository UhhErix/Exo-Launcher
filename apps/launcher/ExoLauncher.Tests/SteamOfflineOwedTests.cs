using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Steam could be left in offline mode by a run of Exo that is over:
/// - an outage or family switch Exo made outlives the run and Steam starts offline for days, or the next start or a clean
///   exit doesn't put it back;
/// - a running Steam is closed (or waited on) to put it back, Exo taking Steam from the player again;
/// - the player's own Settings switch is put back under them, or a record written before reasons (which is theirs) is
///   treated as Exo's;
/// - the bytes on the way back differ from the ones on the way in.
/// Runs with SteamOfflineTests and SteamOfflineModeTests, one at a time: all write Exo's offline record in the run's data
/// folder.
/// </summary>
[Collection("SteamOfflineRecord")]
public sealed class SteamOfflineOwedTests
{
    private const string Id = "76561197960265729";

    private static string RealFile() =>
        File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "Steam", "loginusers.vdf")).ReplaceLineEndings("\n");

    [Fact]
    public void SteamPutOfflineForAnOutageIsBackOnlineAtTheNextStart()
    {
        // The outage ended with the run that waited it out: nothing asks for offline any more.
        var outage = new SteamLoginUsers.OfflineSwitch(SteamLoginUsers.KeyFor(Id), "0", "0", SteamLoginUsers.ForOutage);
        Assert.Equal(SteamOfflineOwed.Plan.WriteNow, SteamOfflineOwed.Decide(outage, steamRunning: false, steamGameRunning: false));
        Assert.Equal(SteamOfflineOwed.Plan.WaitForSteamToClose, SteamOfflineOwed.Decide(outage, steamRunning: true, steamGameRunning: false));

        // A family switch asks while its game runs, and stops asking once it closed.
        var family = new SteamLoginUsers.OfflineSwitch(SteamLoginUsers.KeyFor(Id), "0", "0", SteamLoginUsers.ForFamily);
        Assert.Equal(SteamOfflineOwed.Plan.Leave, SteamOfflineOwed.Decide(family, steamRunning: false, steamGameRunning: true));
        Assert.Equal(SteamOfflineOwed.Plan.WriteNow, SteamOfflineOwed.Decide(family, steamRunning: false, steamGameRunning: false));
        Assert.True(SteamOfflineOwed.EndsWithExo(outage));
        Assert.True(SteamOfflineOwed.EndsWithExo(family));
        Assert.False(SteamOfflineOwed.EndsWithExo(null));
    }

    [Fact]
    public void ThePlayersOwnOfflineSwitchIsLeftAlone()
    {
        var chosen = new SteamLoginUsers.OfflineSwitch(SteamLoginUsers.KeyFor(Id), "0", "0", SteamLoginUsers.Chosen);
        Assert.Equal(SteamOfflineOwed.Plan.Leave, SteamOfflineOwed.Decide(chosen, steamRunning: false, steamGameRunning: false));
        Assert.Equal(SteamOfflineOwed.Plan.Leave, SteamOfflineOwed.Decide(chosen, steamRunning: true, steamGameRunning: true));
        // Even while Steam runs and something is asked for, the player's switch is not Exo's to settle.
        Assert.Equal(SteamOfflineOwed.Plan.Leave, SteamOfflineOwed.Decide(chosen, steamRunning: false, steamGameRunning: true));
        Assert.False(SteamOfflineOwed.EndsWithExo(chosen));
        // Nothing standing is nothing to settle.
        Assert.Equal(SteamOfflineOwed.Plan.Leave, SteamOfflineOwed.Decide(null, steamRunning: false, steamGameRunning: false));
    }

    [Fact]
    public void ARunningSteamIsNotClosedToPutItOnline()
    {
        var outage = new SteamLoginUsers.OfflineSwitch(SteamLoginUsers.KeyFor(Id), "0", "0", SteamLoginUsers.ForOutage);
        // Running: the record waits, nothing is written and Steam is not closed.
        Assert.Equal(SteamOfflineOwed.Plan.WaitForSteamToClose, SteamOfflineOwed.Decide(outage, steamRunning: true, steamGameRunning: false));

        // Neither entry point holds a way to close Steam or to wait on it.
        var source = File.ReadAllText(Repo.PathOf("ExoLauncher", "Adapters", "SteamOfflineOwed.cs"));
        Assert.DoesNotContain("-shutdown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseSteamAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForExit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", source, StringComparison.Ordinal);
        // The decision is the only place that sees whether Steam runs, and it never writes while it does.
        Assert.Contains("steamRunning ? Plan.WaitForSteamToClose : Plan.WriteNow", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderRecordWithNoReasonIsThePlayers()
    {
        // Written before reasons were kept: its reason reads as an outage, but Exo can't tell whose switch it is, so it is
        // the player's and stays.
        var old = System.Text.Json.JsonSerializer.Deserialize<SteamLoginUsers.OfflineSwitch>(
            """{"Account":"abc","WantsOfflineMode":"0","SkipOfflineModeWarning":"0"}""")!;
        Assert.Null(old.Reason);
        Assert.Equal(SteamLoginUsers.ForOutage, old.Why);
        Assert.False(SteamOfflineOwed.EndsWithExo(old));
        Assert.Equal(SteamOfflineOwed.Plan.Leave, SteamOfflineOwed.Decide(old, steamRunning: false, steamGameRunning: false));
    }

    [Fact]
    public void ACleanExitWithSteamClosedPutsItOnlineAtOnce()
    {
        // Closed at a clean exit: written now; running: left, and the record waits for the next start.
        var outage = new SteamLoginUsers.OfflineSwitch(SteamLoginUsers.KeyFor(Id), "0", "0", SteamLoginUsers.ForOutage);
        Assert.Equal(SteamOfflineOwed.Plan.WriteNow, SteamOfflineOwed.Decide(outage, steamRunning: false, steamGameRunning: false));
        Assert.Equal(SteamOfflineOwed.Plan.WaitForSteamToClose, SteamOfflineOwed.Decide(outage, steamRunning: true, steamGameRunning: false));
        // On a real loginusers.vdf the values go back exactly as they were.
        var original = RealFile();
        var (offlineText, before) = SteamLoginUsers.SetOffline(original, Id)!.Value;
        Assert.Equal(original, SteamLoginUsers.RestoreOffline(offlineText, Id, before));
        var family = new SteamLoginUsers.OfflineSwitch(SteamLoginUsers.KeyFor(Id), before.WantsOfflineMode, before.SkipOfflineModeWarning, SteamLoginUsers.ForFamily);
        Assert.True(SteamOfflineOwed.EndsWithExo(family));
    }
}
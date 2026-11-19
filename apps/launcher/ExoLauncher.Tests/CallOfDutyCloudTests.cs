using System.Globalization;
using ExoLauncher.Services;
using static ExoLauncher.Tests.KeepKit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Call of Duty's own cloud, and its content facts, could be read wrong:
/// - a cloud restore between two looks (the flag on, a .b file written since, no session here) is fought instead of yielded to;
/// - a player's change with no .b touched is taken for a cloud restore and left;
/// - a file added or removed under the install's main folder reads as an update, when ordinary play does that.
/// </summary>
public sealed class CallOfDutyCloudTests
{
    private const string Flag = "ConfigCloudStorageEnabled@0;1;1 = true";

    /// <summary>The game's file as season 1 left it, with Activision's cloud flag on, and an account's; optimized.</summary>
    private static Game CloudGame()
    {
        var game = Fresh();
        // The flag goes in before the press, so the press reads it as the file's own and the shape starts from it.
        File.WriteAllText(game.Main, File.ReadAllText(game.Main).Replace("SkipSeasonIntroVideo@0;57752;48403 = false",
            "SkipSeasonIntroVideo@0;57752;48403 = false\r\n\r\n" + Flag, StringComparison.Ordinal));
        Assert.True(GameSettings.Set(game.Profile, null, true, game.Program));
        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(game.Main), StringComparison.Ordinal);
        return game;
    }

    /// <summary>
    /// A .b file written after the last look and before this one: Activision's cloud copying the PC's settings back. Its time
    /// is just past that look's (or past <paramref name="since"/>), so a signal read against this look's own time (its own
    /// reading of the values) misses it.
    /// </summary>
    private static void CloudWrote(Game game, DateTime? since = null)
    {
        var lastLook = since ?? Keep.Load().Games[game.Profile.Id].CheckedAt!.Value;
        var file = Path.Combine(game.Folder, "players", "s.1.0.cod26.b");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [1, 2, 3]);
        File.SetLastWriteTimeUtc(file, lastLook.AddMilliseconds(10));
        // This look reads the values back at a later moment than the .b file's, as a second look always does.
        Thread.Sleep(60);
    }

    private static void TwoLinesChanged(string main) => Rewrite(main,
        ("VSync@0;9203;8071 = disabled", "VSync@0;9203;8071 = 100%"),
        ("MotionBlur@0;11212;9876 = Off", "MotionBlur@0;11212;9876 = On"));

    [Fact]
    public void ACloudRestoreBetweenTwoLooksIsYieldedNotFought()
    {
        var game = CloudGame();
        CloudWrote(game);
        TwoLinesChanged(game.Main);

        // The look as Exo starts, with no session on this PC: nothing here explains the change but the cloud.
        Assert.False(GameSettings.KeepOn(game.Profile, game.Program, afterSession: false));

        var text = File.ReadAllText(game.Main);
        Assert.Contains("VSync@0;9203;8071 = 100%", text, StringComparison.Ordinal);
        Assert.Contains("MotionBlur@0;11212;9876 = On", text, StringComparison.Ordinal);
        var record = Keep.Load().Games[game.Profile.Id];
        Assert.Equal(KeepState.Yielded, record.Entries["VSync@0;9203;8071"].State);
        Assert.Equal(KeepState.Yielded, record.Entries["MotionBlur@0;11212;9876"].State);
    }

    /// <summary>The profile's cloud check as it stands, for a test that tells what a look read it against.</summary>
    private static GameSettings.Profile CloudNoted(Game game, Action<DateTime?> noted) =>
        game.Profile with
        {
            CloudChanged = (files, since, afterSession) => { noted(since); return GameSettings.CallOfDutyCloudChanged(files, since, afterSession); },
        };

    [Fact]
    public void ALookThatStopsBecauseAHelperStartedLeavesTheRecordAsItWas()
    {
        var game = CloudGame();
        CloudWrote(game);
        var path = Keep.Path;
        var before = File.ReadAllBytes(path);
        var previous = GameSettings.HelperPaths;
        try
        {
            // The helper starts as the look reads the values back (the second reading of the game's facts is inside it), not before.
            var started = false;
            var reads = 0;
            GameSettings.HelperPaths = _ => started ? ["C:\\anywhere\\bootstrapper.exe"] : [];
            var starting = game.Profile with
            {
                Helpers = ["bootstrapper"],
                Facts = () => { if (++reads > 1) started = true; return new Dictionary<string, string>(); },
            };
            Assert.False(GameSettings.KeepOn(starting, game.Program, afterSession: false));
        }
        finally { GameSettings.HelperPaths = previous; }

        // Nothing was judged, so the record is byte for byte as it was: the look did not use up its own moment.
        Assert.Equal([.. before], File.ReadAllBytes(path));
    }

    [Fact]
    public void ACloudRestoreIsStillSeenAfterALookThatStopped()
    {
        var game = CloudGame();
        CloudWrote(game);
        var previous = GameSettings.HelperPaths;
        try
        {
            // The helper starts as the look reads the values back (the second reading of the game's facts is inside it).
            var started = false;
            var reads = 0;
            GameSettings.HelperPaths = _ => started ? ["C:\\anywhere\\bootstrapper.exe"] : [];
            var starting = game.Profile with
            {
                Helpers = ["bootstrapper"],
                Facts = () => { if (++reads > 1) started = true; return new Dictionary<string, string>(); },
            };
            Assert.False(GameSettings.KeepOn(starting, game.Program, afterSession: false));
        }
        finally { GameSettings.HelperPaths = previous; }

        // The stop left the look's own moment where it was, so the next look still reads the cloud's files against the last
        // look, sees them, and yields to the cloud instead of fighting it.
        DateTime? noted = null;
        TwoLinesChanged(game.Main);
        Assert.False(GameSettings.KeepOn(CloudNoted(game, since => noted = since), game.Program, afterSession: false));
        var record = Keep.Load().Games[game.Profile.Id];
        Assert.Equal(KeepState.Yielded, record.Entries["VSync@0;9203;8071"].State);
        Assert.Equal(KeepState.Yielded, record.Entries["MotionBlur@0;11212;9876"].State);
        Assert.Contains("MotionBlur@0;11212;9876 = On", File.ReadAllText(game.Main), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameChangeWithNoCloudFileTouchedIsNotACloudRestore()
    {
        var game = CloudGame();
        TwoLinesChanged(game.Main);

        Assert.True(GameSettings.KeepOn(game.Profile, game.Program, afterSession: false));

        var text = File.ReadAllText(game.Main);
        Assert.Contains("VSync@0;9203;8071 = disabled", text, StringComparison.Ordinal);
        Assert.Contains("MotionBlur@0;11212;9876 = Off", text, StringComparison.Ordinal);
        var record = Keep.Load().Games[game.Profile.Id];
        Assert.Equal(KeepState.PutBack, record.Entries["VSync@0;9203;8071"].State);
        Assert.Equal(KeepState.PutBack, record.Entries["MotionBlur@0;11212;9876"].State);
    }

    [Fact]
    public void AFileAddedUnderMainIsNotAnUpdate()
    {
        // The install's main folder is there as the press is made, so a record made then holds its time. (The fixture's own
        // profile has no build facts: For() binds them, so here they are bound to the install's, as the game's are.)
        var fresh = Fresh();
        var game = fresh with { Profile = fresh.Profile with { Facts = () => GameSettings.CallOfDutyFacts(fresh.Folder) } };
        var main = Directory.CreateDirectory(Path.Combine(game.Folder, "main")).FullName;
        var ccs = Path.Combine(game.Folder, "ccs_history.json");
        File.WriteAllText(ccs, "{}");
        File.SetLastWriteTimeUtc(ccs, DateTime.UtcNow.AddDays(-1));
        Assert.True(GameSettings.Set(game.Profile, null, true, game.Program));

        // A record from an earlier Exo still holds the main folder as a fact; it is not evidence of anything now.
        Keep.Update(doc => doc.Games[game.Profile.Id].Keeping!.Facts["main"] = DateTime.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture));

        // Between two looks the folder gains a file, as ordinary play does (a new map's data), and Windows changes its time.
        var added = Path.Combine(main, "ww_mp_exo_test.ff");
        File.WriteAllBytes(added, [1]);
        File.SetLastWriteTimeUtc(added, DateTime.UtcNow.AddMinutes(5));

        // One line the player changed, with no evidence of a reset around it: a drift, not a reset.
        Rewrite(game.Main, ("VSync@0;9203;8071 = disabled", "VSync@0;9203;8071 = 100%"));
        Assert.True(GameSettings.KeepOn(game.Profile, game.Program, afterSession: true));

        var entry = Keep.Load().Games[game.Profile.Id].Entries["VSync@0;9203;8071"];
        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(game.Main), StringComparison.Ordinal);
        Assert.Equal(KeepState.PutBack, entry.State);
        Assert.Equal("WriteAndRemember", entry.History[^1].Action);
        Assert.Contains("no evidence of a reset", entry.History[^1].Cause, StringComparison.Ordinal);
    }
}

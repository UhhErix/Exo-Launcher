using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Tests.KeepKit;

namespace ExoLauncher.Tests;

/// <summary>
/// The safe-mode guard: Call of Duty leaves __cod in its install folder while it runs, and one left behind makes the next
/// start offer safe mode, which resets its settings. Ways it could go wrong, each with a test:
/// - a marker whose game exits slowly is given up on before 5 seconds have passed;
/// - a marker is cleared while its game still runs;
/// - a refused clear (the anti-cheat still closing) is never tried again;
/// - a clear that is always refused is kept up past the 2 minute cap;
/// - there is nothing to look at (no marker, no install folder) and the wait still happens;
/// - a cleared marker is not noted for Keep, is noted for a game with no Optimize, or is noted twice;
/// - a session that followed a cleared marker has its one moved line put back as the player's drift instead of a reset.
/// </summary>
public sealed class CallOfDutyMarkerTests
{
    /// <summary>A folder standing in for Call of Duty's install, with its running marker (pid 4242) in it.</summary>
    private static (GameEntry Game, string Folder) MarkerGame()
    {
        var folder = Path.Combine(TestDataDirectory.Root, "codmarker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "__cod"), BitConverter.GetBytes(4242));
        return (Folder(folder), folder);
    }

    private static GameEntry Folder(string? path) => new()
    {
        Id = "local:cod-marker-" + Guid.NewGuid().ToString("N"), Title = "Call of Duty", Store = StoreKind.Local, Installed = true, Path = path,
    };

    /// <summary>
    /// Runs the loop with one fake second per delay call: <paramref name="alive"/> is told how many delay calls have
    /// happened, and <paramref name="clear"/> which clear attempt this is (1 for the first). <paramref name="read"/> stands
    /// in for the marker's reader. Returns what the loop returned, how many delay calls happened, the delay count at each
    /// clear call, and the process ids it asked about.
    /// </summary>
    private static async Task<(bool Cleared, int Delays, List<int> ClearsAt, List<int> Pids)> Run(GameEntry game,
        Func<int, bool> alive, Func<int, bool>? clear = null, Func<string, (bool Read, int? Pid)>? read = null)
    {
        var delays = 0;
        var clearsAt = new List<int>();
        var pids = new List<int>();
        var cleared = await GameSettings.ClearMarkerWhenGoneAsync(game,
            _ => { delays++; return Task.CompletedTask; },
            pid => { pids.Add(pid); return alive(delays); },
            _ => "exo-test-profile",
            _ => { clearsAt.Add(delays); return (clear ?? (attempt => true))(clearsAt.Count); },
            read);
        return (cleared, delays, clearsAt, pids);
    }

    [Fact]
    public async Task AMarkerWhoseGameExitsSlowlyIsStillCleared()
    {
        var (game, _) = MarkerGame();
        // Its process is still there for 30 looks (30 fake seconds), then gone.
        var (cleared, _, clearsAt, pids) = await Run(game, alive: delays => delays < 30);

        Assert.True(cleared);
        Assert.All(pids, pid => Assert.Equal(4242, pid));
        // Cleared once, at 30 fake seconds of the process being there and 5 more of it gone.
        Assert.Equal(35, Assert.Single(clearsAt));
    }

    [Fact]
    public async Task AGameThatComesBackStartsTheFiveSecondWaitAgain()
    {
        var (game, _) = MarkerGame();
        // Gone for 3 looks, back from 3 to 12 (ten looks), then gone from 13 on: the wait starts over, never counts the first 3.
        var (cleared, _, clearsAt, _) = await Run(game, alive: delays => delays >= 3 && delays < 13);

        Assert.True(cleared);
        Assert.Equal(18, Assert.Single(clearsAt));
    }

    [Fact]
    public async Task AMarkerIsNeverClearedWhileItsGameRuns()
    {
        var (game, _) = MarkerGame();
        var (cleared, delays, clearsAt, _) = await Run(game, alive: _ => true);

        Assert.False(cleared);
        Assert.Empty(clearsAt);
        Assert.InRange(delays, 120, 122);
    }

    [Fact]
    public async Task AClearRefusedIsTriedAgainFiveSecondsLater()
    {
        var (game, _) = MarkerGame();
        var (cleared, _, clearsAt, _) = await Run(game, alive: _ => false, clear: attempt => attempt >= 2);

        Assert.True(cleared);
        Assert.Equal(new[] { 5, 10 }, clearsAt);
    }

    [Fact]
    public async Task AClearThatIsAlwaysRefusedStopsAtTheCap()
    {
        var (game, _) = MarkerGame();
        var (cleared, delays, clearsAt, _) = await Run(game, alive: _ => false, clear: _ => false);

        Assert.False(cleared);
        Assert.True(clearsAt.Count > 1);
        Assert.InRange(delays, 120, 122);
    }

    [Fact]
    public async Task NoMarkerIsNothingToDo()
    {
        var folder = Path.Combine(TestDataDirectory.Root, "codmarker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var (cleared, delays, clearsAt, _) = await Run(Folder(folder), alive: _ => false);

        Assert.False(cleared);
        Assert.Equal(0, delays);
        Assert.Empty(clearsAt);
    }

    [Fact]
    public async Task AGameWithNoInstallPathIsNothingToDo()
    {
        var (cleared, delays, clearsAt, _) = await Run(Folder(""), alive: _ => false);

        Assert.False(cleared);
        Assert.Equal(0, delays);
        Assert.Empty(clearsAt);
    }

    [Fact]
    public async Task AMarkerThatCannotBeReadIsNeverCleared()
    {
        var (game, _) = MarkerGame();
        // The game or its anti-cheat holds the file: an unreadable marker says nothing about its process, so it is never "gone".
        var (cleared, delays, clearsAt, _) = await Run(game, alive: _ => false, read: _ => (false, null));

        Assert.False(cleared);
        Assert.Empty(clearsAt);
        Assert.InRange(delays, 120, 122);
    }

    [Fact]
    public async Task AMarkerThatBecomesReadableIsClearedFiveSecondsAfterItsGameIsGone()
    {
        var (game, _) = MarkerGame();
        // Unreadable for 20 looks (the anti-cheat has it open as it closes), then readable with a process that is gone: the five
        // seconds run from the first read that says so, and never from the unreadable ones.
        var looks = 0;
        var (cleared, _, clearsAt, _) = await Run(game, alive: _ => false, read: _ => ++looks <= 20 ? (false, null) : (true, 4242));

        Assert.True(cleared);
        Assert.Equal(25, Assert.Single(clearsAt));
    }

    [Fact]
    public async Task AMarkerWithNoIdInItIsTreatedAsLeftBehind()
    {
        var (game, _) = MarkerGame();
        // Read fine, and holds no usable id (a zero-length or short file a crash left): one left behind.
        var (cleared, _, clearsAt, pids) = await Run(game, alive: _ => false, read: _ => (true, null));

        Assert.True(cleared);
        Assert.Equal(5, Assert.Single(clearsAt));
        Assert.Empty(pids);
    }

    [Fact]
    public void AClearedMarkerIsNotedForKeepOnlyWhenTheGameHasAnOptimize()
    {
        var game = Optimized();
        GameSettings.NoteMarker(game.Profile.Id);
        Assert.Equal(1, Keep.Load().Games[game.Profile.Id].Signals.Count(signal => signal == "marker"));
        // Noted twice is still one signal.
        GameSettings.NoteMarker(game.Profile.Id);
        Assert.Equal(1, Keep.Load().Games[game.Profile.Id].Signals.Count(signal => signal == "marker"));

        // A profile Keep holds no game for, and none at all: neither throws, and neither adds a game.
        GameSettings.NoteMarker("no-such-profile");
        GameSettings.NoteMarker(null);
        Assert.False(Keep.Load().Games.ContainsKey("no-such-profile"));
    }

    /// <summary>One of Exo's lines changed in the game's file, to another of the values the line allows.</summary>
    private static void OneLineChanged(string main) => Rewrite(main, ("VSync@0;9203;8071 = disabled", "VSync@0;9203;8071 = 100%"));

    [Fact]
    public void AMarkerAtSessionStartIsAReset()
    {
        var game = Optimized();
        GameSettings.NoteMarker(game.Profile.Id);
        OneLineChanged(game.Main);

        // The look as the session that followed the marker closes.
        Assert.True(GameSettings.KeepOn(game.Profile, game.Program, afterSession: true));

        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(game.Main), StringComparison.Ordinal);
        var entry = Keep.Load().Games[game.Profile.Id].Entries["VSync@0;9203;8071"];
        Assert.Equal(KeepState.PutBack, entry.State);
        var last = entry.History[^1];
        Assert.Contains("marker", last.Cause, StringComparison.Ordinal);
        Assert.Equal("Write", last.Action);
        Assert.DoesNotContain("marker", Keep.Load().Games[game.Profile.Id].Signals);
    }

    [Fact]
    public void AMarkerIsKeptWhenThePutBackCouldNotBeWritten()
    {
        var game = Optimized();
        GameSettings.NoteMarker(game.Profile.Id);
        OneLineChanged(game.Main);

        // The game still holds the file as the session closes: the put back cannot be written, so the marker is not used up.
        using (GameSettings.ForceWriterForTest((_, _) => throw new IOException("locked")))
            Assert.False(GameSettings.KeepOn(game.Profile, game.Program, afterSession: true));
        Assert.Contains("marker", Keep.Load().Games[game.Profile.Id].Signals);
        Assert.Contains("VSync@0;9203;8071 = 100%", File.ReadAllText(game.Main), StringComparison.Ordinal);
        Assert.Equal(KeepState.Undone, Keep.Load().Games[game.Profile.Id].Entries["VSync@0;9203;8071"].State);

        // Free again, still as the session closes: the next look still has the marker, and puts the line back as the reset it was.
        Assert.True(GameSettings.KeepOn(game.Profile, game.Program, afterSession: true));
        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(game.Main), StringComparison.Ordinal);
        var entry = Keep.Load().Games[game.Profile.Id].Entries["VSync@0;9203;8071"];
        Assert.Equal(KeepState.PutBack, entry.State);
        Assert.Contains("marker", entry.History[^1].Cause, StringComparison.Ordinal);
        Assert.Equal("Write", entry.History[^1].Action);
        Assert.DoesNotContain("marker", Keep.Load().Games[game.Profile.Id].Signals);
    }

    [Fact]
    public void AFirstRunWriteClearsTheMarker()
    {
        // The game's file is not there as Optimize is pressed: the write waits for its first run.
        var folder = NewFolder();
        var main = Path.Combine(folder, "s.1.0.cod26.txt");
        var program = Path.Combine(folder, "cod.exe");
        File.WriteAllBytes(program, []);
        File.SetLastWriteTimeUtc(program, Season1);
        var profile = GameSettings.ModernWarfare4(new("nvidia", 5, 8)) with
        {
            Id = "mw4-" + Guid.NewGuid().ToString("N"), Title = "MW4 " + Guid.NewGuid().ToString("N"), File = main, PlayerFiles = null, Processes = [NoGame],
        };
        Assert.True(GameSettings.Set(profile, null, true, program));
        Assert.True(GameSettings.Waiting(profile));
        GameSettings.NoteMarker(profile.Id);
        Assert.Contains("marker", Keep.Load().Games[profile.Id].Signals);

        // The game's first run writes its file and closes: the waiting Optimize is written then, and the marker is used up.
        File.WriteAllText(main, Main.ReplaceLineEndings("\r\n"));
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(main), StringComparison.Ordinal);
        Assert.DoesNotContain("marker", Keep.Load().Games[profile.Id].Signals);
    }

    [Fact]
    public void AMarkerIsKeptWhileItsGroupWaits()
    {
        var game = Optimized();
        GameSettings.NoteMarker(game.Profile.Id);
        // An update, and a line Exo wrote in the audio group is gone from the file: that group fails closed and waits, so
        // nothing is written at all.
        File.SetLastWriteTimeUtc(game.Program, Season2);
        File.WriteAllText(game.Main, File.ReadAllText(game.Main)
            .Replace("CinematicVolume@0;14317;21372 = 0.000000", "", StringComparison.Ordinal));

        // Nothing was written, and the marker the session needs is still there for the look that re-opens the group.
        Assert.False(GameSettings.KeepOn(game.Profile, game.Program, afterSession: true));
        Assert.Contains("marker", Keep.Load().Games[game.Profile.Id].Signals);
        Assert.Equal(KeepState.Waiting, Keep.Load().Games[game.Profile.Id].Entries["CinematicVolume@0;14317;21372"].State);
    }

    [Fact]
    public void AMarkerIsUsedUpByASessionInWhichNothingMoved()
    {
        var game = Optimized();
        GameSettings.NoteMarker(game.Profile.Id);

        // The session the marker explained moved nothing: the marker has been used, and the next session is nobody's reset.
        Assert.False(GameSettings.KeepOn(game.Profile, game.Program, afterSession: true));
        Assert.DoesNotContain("marker", Keep.Load().Games[game.Profile.Id].Signals);
    }

    [Fact]
    public void APendingOptimizeWrittenAsExoStartsUsesUpTheMarker()
    {
        // Optimize is pressed before the game has a file, so its write waits for the first run.
        var folder = NewFolder();
        var main = Path.Combine(folder, "s.1.0.cod26.txt");
        var program = Path.Combine(folder, "cod.exe");
        File.WriteAllBytes(program, []);
        File.SetLastWriteTimeUtc(program, Season1);
        var profile = GameSettings.ModernWarfare4(new("nvidia", 5, 8)) with
        {
            Id = "mw4-" + Guid.NewGuid().ToString("N"), Title = "MW4 " + Guid.NewGuid().ToString("N"), File = main, PlayerFiles = null, Processes = [NoGame],
        };
        Assert.True(GameSettings.Set(profile, null, true, program));
        Assert.True(GameSettings.Waiting(profile));
        GameSettings.NoteMarker(profile.Id);
        Assert.Contains("marker", Keep.Load().Games[profile.Id].Signals);

        // The game ran and Exo was closed when it did: the start-up look writes the waiting Optimize, and the marker it wrote
        // against is used up.
        File.WriteAllText(main, Main.ReplaceLineEndings("\r\n"));
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: false));
        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(main), StringComparison.Ordinal);
        Assert.True(GameSettings.Waiting(profile) is false);
        Assert.DoesNotContain("marker", Keep.Load().Games[profile.Id].Signals);
    }

    [Fact]
    public void BeforeALaunchAMarkerThatCannotBeReadIsTriedAgainAndThenRemoved()
    {
        var (game, folder) = MarkerGame();
        var marker = Path.Combine(folder, "__cod");
        var tries = 0;
        var waits = 0;

        // An antivirus has the marker open for a moment: the read fails three times, and the delete before the launch goes ahead
        // anyway, because a marker left there makes the game ask for safe mode, which resets its settings.
        Assert.True(GameSettings.ClearLeftoverMarker(game, _ => { tries++; return (false, null); }, _ => { waits++; return Task.CompletedTask; }));

        Assert.Equal(3, tries);
        Assert.Equal(2, waits);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void BeforeALaunchAMarkerThatBecomesReadableAndIsAliveIsLeft()
    {
        var (game, folder) = MarkerGame();
        var marker = Path.Combine(folder, "__cod");
        var previous = GameSettings.ProcessIdsNamed;
        try
        {
            // A cod.exe is running, with this id (a test's own stand-in for the process lookup). The marker is unreadable on the
            // first try (an antivirus has it open) and read on the second: the game is there, so the marker is left alone.
            GameSettings.ProcessIdsNamed = _ => [4242];
            var tries = 0;
            Assert.False(GameSettings.ClearLeftoverMarker(game, _ => ++tries < 2 ? (false, null) : (true, 4242), _ => Task.CompletedTask));
            Assert.Equal(2, tries);
        }
        finally { GameSettings.ProcessIdsNamed = previous; }
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void AProtectedProcessThatReusedTheMarkersIdIsNotTheGame()
    {
        var (game, folder) = MarkerGame();
        var previous = GameSettings.ProcessIdsNamed;
        try
        {
            // A protected process (an anti-cheat service, Defender) has taken the id the marker names, but no process named cod
            // has it: it is not the game, so the marker is one left behind and goes before the launch.
            GameSettings.ProcessIdsNamed = name => name == "cod" ? [] : [4242];
            Assert.True(GameSettings.ClearLeftoverMarker(game));
        }
        finally { GameSettings.ProcessIdsNamed = previous; }
        Assert.False(File.Exists(Path.Combine(folder, "__cod")));
    }

    [Fact]
    public void AMarkerWhoseProcessRunsIsLeftBeforeALaunch()
    {
        var (game, folder) = MarkerGame();
        var previous = GameSettings.ProcessIdsNamed;
        try
        {
            // A process named cod has the marker's id: the game is there, and nothing is deleted.
            GameSettings.ProcessIdsNamed = name => name == "cod" ? [4242] : [];
            Assert.False(GameSettings.ClearLeftoverMarker(game));
        }
        finally { GameSettings.ProcessIdsNamed = previous; }
        Assert.True(File.Exists(Path.Combine(folder, "__cod")));
    }

    [Fact]
    public void WithoutAMarkerTheSameOneLineChangeIsADrift()
    {
        var game = Optimized();
        OneLineChanged(game.Main);

        Assert.True(GameSettings.KeepOn(game.Profile, game.Program, afterSession: true));

        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(game.Main), StringComparison.Ordinal);
        var entry = Keep.Load().Games[game.Profile.Id].Entries["VSync@0;9203;8071"];
        Assert.Equal(KeepState.PutBack, entry.State);
        var last = entry.History[^1];
        Assert.DoesNotContain("marker", last.Cause, StringComparison.Ordinal);
        Assert.Contains("no evidence of a reset", last.Cause, StringComparison.Ordinal);
        Assert.Equal("WriteAndRemember", last.Action);
    }
}

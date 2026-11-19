using ExoLauncher.Helpers;
using ExoLauncher.Services;
using static ExoLauncher.Tests.KeepKit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the evidence for what moves can go wrong: a value Exo wrote is not kept, so which one moved can never be told; a
/// moved value is not named in the log, or is named without its file or with an account's id; what else changed since the value
/// was last held (driver, game build, screens) is not said; Exo's own target, worked out from the PC, is taken for a drift in
/// the file; a game still running after a minute is never looked at; a torn or empty record drops every game's undo data;
/// reading back writes something.
/// </summary>
public sealed class KeepEvidenceTests
{
    private static readonly KeepContext.PcFacts Before = new("617.42", 26300, 2, "Headphones");

    [Fact]
    public void WhatExoWroteIsKeptWithAFreshReadOfIt()
    {
        var game = Optimized();
        var entry = Keep.Load().Games[game.Profile.Id].Entries["VSync@0;9203;8071"];
        Assert.Equal("disabled", entry.Intended);
        Assert.Equal("100%", entry.Before);
        Assert.Equal("disabled", entry.ReadBack);
        Assert.NotNull(entry.ReadBackAt);
        Assert.Equal(game.Profile.Id + "/latency", entry.Owner);
        // The player's own file keeps its line under its own key.
        Assert.Equal("120.000000", Keep.Load().Games[game.Profile.Id].Entries["76561198000000001/g.cod26.1.0.l.txt|Fov@1;65095;45350"].Intended);
    }

    [Fact]
    public void TheLogNamesEveryLineThatMoved()
    {
        var game = Optimized();
        Rewrite(game.Main, ("VSync@0;9203;8071 = disabled", "VSync@0;9203;8071 = 100%"), ("MotionBlur@0;11212;9876 = Off", "MotionBlur@0;11212;9876 = On"));
        Rewrite(game.Player, ("Fov@1;65095;45350 = 120.000000", "Fov@1;65095;45350 = 105.000000"));
        using var heard = new Heard(game.Profile.Title);

        var moved = KeepEvidence.ReadBack(game.Profile, GameSettings.ReadAll(game.Profile), game.Program, "after the game closed").Moved;

        Assert.Equal(3, moved.Count);
        Assert.Equal(3, heard.Lines.Count(line => line.Contains("-> now", StringComparison.Ordinal)));
        Assert.Contains(heard.Lines, line => line.Contains("VSync@0;9203;8071: Exo's disabled -> now 100%", StringComparison.Ordinal));
        Assert.Contains(heard.Lines, line => line.Contains("MotionBlur@0;11212;9876: Exo's Off -> now On", StringComparison.Ordinal));
        // The account's file is named by its name, never by the account's folder.
        Assert.Contains(heard.Lines, line => line.Contains("g.cod26.1.0.l.txt: Fov@1;65095;45350: Exo's 120.000000 -> now 105.000000", StringComparison.Ordinal));
        Assert.DoesNotContain(heard.Lines, line => line.Contains("76561198000000001", StringComparison.Ordinal));
        Assert.All(heard.Lines, line => Assert.Contains("after the game closed", line, StringComparison.Ordinal));
        // A line that did not move is not named.
        Assert.DoesNotContain(heard.Lines, line => line.Contains("MusicVolume", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadingBackWritesNothingToTheGamesFiles()
    {
        var game = Optimized();
        Rewrite(game.Main, ("VSync@0;9203;8071 = disabled", "VSync@0;9203;8071 = 100%"));
        var main = File.ReadAllBytes(game.Main);
        var player = File.ReadAllBytes(game.Player);

        KeepEvidence.ReadBack(game.Profile, GameSettings.ReadAll(game.Profile), game.Program, "before launch");

        Assert.Equal(main, File.ReadAllBytes(game.Main));
        Assert.Equal(player, File.ReadAllBytes(game.Player));
    }

    [Fact]
    public void TheLogSaysWhatElseChangedSinceTheValueWasLastHeld()
    {
        var game = Fresh();
        using (KeepContext.ForceForTest(() => Before))
            Assert.True(GameSettings.Set(game.Profile, null, true, game.Program));
        Rewrite(game.Main, ("VSync@0;9203;8071 = disabled", "VSync@0;9203;8071 = 100%"));
        using var heard = new Heard(game.Profile.Title);

        using (KeepContext.ForceForTest(() => Before with { Driver = "621.14", Screens = 1 }))
            KeepEvidence.ReadBack(game.Profile, GameSettings.ReadAll(game.Profile), game.Program, "as Exo started");

        var line = Assert.Single(heard.Lines);
        Assert.Contains("driver 617.42 -> 621.14", line, StringComparison.Ordinal);
        Assert.Contains("screens 2 -> 1", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows build", line, StringComparison.Ordinal);
        Assert.Contains("as Exo started", line, StringComparison.Ordinal);
        Assert.Contains("written ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenOnlyTheFilesTimeChangedTheLogNamesThatAndNothingElse()
    {
        var game = Fresh();
        using var held = KeepContext.ForceForTest(() => Before);
        Assert.True(GameSettings.Set(game.Profile, null, true, game.Program));
        Rewrite(game.Main, ("VSync@0;9203;8071 = disabled", "VSync@0;9203;8071 = 100%"));
        using var heard = new Heard(game.Profile.Title);

        KeepEvidence.ReadBack(game.Profile, GameSettings.ReadAll(game.Profile), game.Program, "before launch");

        var line = Assert.Single(heard.Lines);
        Assert.Contains("file time ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("driver", line, StringComparison.Ordinal);
        Assert.DoesNotContain("screens", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AFactNoEarlierLookHadIsNotAChange()
    {
        var then = new Dictionary<string, string> { ["windows"] = "26300" };
        var now = new Dictionary<string, string> { ["windows"] = "26300", ["driver"] = "621.14", ["screens"] = "2" };
        Assert.Equal("nothing else changed", KeepContext.Diff(then, now));
        Assert.Equal("Windows build 26300 -> 26301", KeepContext.Diff(then, new Dictionary<string, string> { ["windows"] = "26301" }));
    }

    [Fact]
    public void ATargetComputedFromTheScreenCountIsNotADrift()
    {
        var game = Optimized(new("nvidia", 5, 8, Screens: 2));
        // Written with two screens: the mouse is kept in the game, and the entry says which inputs that was worked out from.
        Assert.Contains("ConstrainMouse@0;100;3 = true", File.ReadAllText(game.Main), StringComparison.Ordinal);
        Assert.Contains("screens=2", Keep.Load().Games[game.Profile.Id].Entries["ConstrainMouse@0;100;3"].Inputs!, StringComparison.Ordinal);
        // The second screen is switched off: Exo no longer asks for it, and the file did not move.
        var oneScreen = GameSettings.WithInputs(GameSettings.ModernWarfare4(new("nvidia", 5, 8, Screens: 1)) with
        {
            Id = game.Profile.Id, Title = game.Profile.Title, File = game.Main, PlayerFiles = game.Profile.PlayerFiles, Processes = game.Profile.Processes,
        }, new("nvidia", 5, 8, Screens: 1));
        using var heard = new Heard(game.Profile.Title);

        var moved = KeepEvidence.ReadBack(oneScreen, GameSettings.ReadAll(oneScreen), game.Program, "before launch").Moved;

        Assert.Empty(moved);
        var line = Assert.Single(heard.Lines);
        Assert.Contains("ConstrainMouse@0;100;3: Exo's own target moved (true -> no longer asked for), the file did not", line, StringComparison.Ordinal);
        Assert.Contains("screens 2 -> 1", line, StringComparison.Ordinal);
        Assert.DoesNotContain("-> now", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetComputedFromTheFilesOwnResolutionIsNotADrift()
    {
        var game = Optimized();
        Assert.Contains("Fov@1;65095;45350 = 120.000000", File.ReadAllText(game.Player), StringComparison.Ordinal);
        // The game now outputs an ultrawide picture: its own line says so, Exo's field of view target follows it, the Fov line did not move.
        Rewrite(game.Main, ("Resolution@0;100;4 = 1920x1080", "Resolution@0;100;4 = 3440x1440"));
        using var heard = new Heard(game.Profile.Title);

        var moved = KeepEvidence.ReadBack(game.Profile, GameSettings.ReadAll(game.Profile), game.Program, "after the game closed").Moved;

        Assert.Empty(moved);
        var line = Assert.Single(heard.Lines, l => l.Contains("Fov@1;65095;45350", StringComparison.Ordinal));
        Assert.Contains("Exo's own target moved (120.000000 -> 110.000000), the file did not", line, StringComparison.Ordinal);
        Assert.Contains("Resolution 1920x1080 -> 3440x1440", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AMovedLineWhoseTargetAlsoMovedSaysBoth()
    {
        var game = Optimized();
        Rewrite(game.Main, ("Resolution@0;100;4 = 1920x1080", "Resolution@0;100;4 = 3440x1440"));
        Rewrite(game.Player, ("Fov@1;65095;45350 = 120.000000", "Fov@1;65095;45350 = 100.000000"));
        using var heard = new Heard(game.Profile.Title);

        var moved = Assert.Single(KeepEvidence.ReadBack(game.Profile, GameSettings.ReadAll(game.Profile), game.Program, "after the game closed").Moved);

        Assert.Equal("Fov@1;65095;45350", moved.Line);
        var line = Assert.Single(heard.Lines);
        Assert.Contains("Exo's 120.000000 -> now 100.000000", line, StringComparison.Ordinal);
        Assert.Contains("Exo's own target is now 110.000000", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGameStillRunningAfterSixtySecondsIsStillChecked()
    {
        var polls = 0;
        var waited = TimeSpan.Zero;
        // The game and its launcher run for 100 polls of five seconds: eight minutes and more, far past a minute.
        var free = await KeepEvidence.UntilFreeAsync(() => ++polls <= 100, TimeSpan.FromSeconds(5), (span, _) => { waited += span; return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(free);
        Assert.Equal(101, polls);
        Assert.True(waited > TimeSpan.FromMinutes(8));
    }

    [Fact]
    public async Task TheGameStartingAgainEndsTheWaitWithoutALook()
    {
        using var cancel = new CancellationTokenSource();
        var polls = 0;
        var free = await KeepEvidence.UntilFreeAsync(() => { polls++; cancel.Cancel(); return true; }, TimeSpan.FromSeconds(5), (_, _) => Task.CompletedTask, cancel.Token);
        Assert.False(free);
        Assert.Equal(1, polls);
    }

    [Fact]
    public void AnEmptyRecordIsSetAsideAndTheLastGoodOneIsUsed()
    {
        var path = Path.Combine(NewFolder(), "keep.json");
        using var _ = Keep.ForcePathForTest(path);
        Assert.True(Keep.Update(doc => doc.Games["a"] = new Keep.KeepGame { Entries = { ["k"] = new Keep.KeepEntry { Intended = "1", Before = "0" } } }));
        Assert.True(Keep.Update(doc => doc.Games["b"] = new Keep.KeepGame { Entries = { ["k"] = new Keep.KeepEntry { Intended = "2", Before = "0" } } }));

        // A crash left the file empty (a zero-length file is what a half-finished replace leaves).
        File.WriteAllBytes(path, []);
        var doc = Keep.Load();

        Assert.Equal("1", doc.Games["a"].Entries["k"].Intended);
        Assert.True(File.Exists(path + ".unreadable"));
        // The next write does not drop what the last good record held for other games.
        Assert.True(Keep.Update(d => d.Games["c"] = new Keep.KeepGame()));
        Assert.Equal(["a", "c"], Keep.Load().Games.Keys.Order().ToArray());
    }

    [Fact]
    public void ARecordAnotherProgramHoldsIsLeftAloneAndNothingIsSavedOverIt()
    {
        var game = Optimized();
        var path = Keep.Path;
        var before = File.ReadAllBytes(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(Keep.Update(doc => doc.Games.Clear()));

        Assert.False(File.Exists(path + ".unreadable"));
        Assert.Equal(before, File.ReadAllBytes(path));
        var record = Keep.Load().Games.GetValueOrDefault(game.Profile.Id);
        Assert.NotNull(record);
        Assert.NotEmpty(record.Entries);
    }

    [Fact]
    public void ARecordThatCanOnlyBeReadByItsHolderIsNeverReplacedByTheOlderCopy()
    {
        var game = Optimized();
        var path = Keep.Path;
        var before = File.ReadAllBytes(path);
        // Another program holds the record so that it can be renamed but not read: what the next save would write over is hidden.
        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Delete))
            Assert.False(Keep.Update(doc => doc.Games.Clear()));

        Assert.False(File.Exists(path + ".unreadable"));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.NotNull(Keep.Load().Games.GetValueOrDefault(game.Profile.Id));
    }

    [Fact]
    public void ARecordFromANewerExoIsNeverSavedOver()
    {
        var path = Path.Combine(NewFolder(), "keep.json");
        using var _ = Keep.ForcePathForTest(path);
        // A newer Exo's record: a version this one does not know, and a property it does not read.
        File.WriteAllText(path, """{"version":""" + (Keep.Version + 1) + ""","games":{"g":{"entries":{}}},"fromANewerExo":true}""");
        var before = File.ReadAllBytes(path);

        Assert.False(Keep.Update(doc => doc.Games.Clear()));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(Keep.Load().Games.ContainsKey("g"));
    }

    [Fact]
    public void ASaveLeavesNoHalfWrittenFileAndKeepsTheLastGoodOne()
    {
        var path = Path.Combine(NewFolder(), "keep.json");
        using var _ = Keep.ForcePathForTest(path);
        for (var i = 0; i < 5; i++) Assert.True(Keep.Update(doc => doc.Games["g" + doc.Games.Count] = new Keep.KeepGame()));
        Assert.False(File.Exists(path + "." + Environment.ProcessId + ".tmp"));
        Assert.Equal(5, Keep.Load().Games.Count);
        Assert.Equal(4, System.Text.Json.JsonDocument.Parse(File.ReadAllText(path + ".bak")).RootElement.GetProperty("games").EnumerateObject().Count());
    }
}

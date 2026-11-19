using System.Text.Json;
using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Tests.KeepKit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the plain status the page is told could fail:
/// - a game never optimized shows a status (or one with nothing held does);
/// - a refused write, or a value not yet put back, is shown as Optimized;
/// - a value the player owns, or one waiting for the first run, is hidden by another;
/// - Put back after the update is said once the game has run again, or not said before it has;
/// - the PC is not Optimized when a game is, or asks for an OK that nothing needs;
/// - the report carries an id, a value or a count, or a game with no status;
/// - a word is not the enum's own name with a lower-case first letter.
/// </summary>
public sealed class KeepStatusTests
{
    /// <summary>A game with one group, not pending and with no put back, and one entry per state given.</summary>
    private static Keep.KeepGame Game(params KeepState[] states)
    {
        var game = new Keep.KeepGame
        {
            Keeping = new Keep.Keeping { Groups = ["g"], Pending = false, Again = null },
        };
        for (var index = 0; index < states.Length; index++)
            game.Entries["e" + index] = new Keep.KeepEntry { State = states[index] };
        return game;
    }

    [Fact]
    public void AGameNeverOptimizedHasNoStatus()
    {
        Assert.Equal(KeepStatus.Game.None, KeepStatus.Of((Keep.KeepGame?)null));
        var off = Game(KeepState.Held);
        off.Keeping = null;
        Assert.Equal(KeepStatus.Game.None, KeepStatus.Of(off));
    }

    [Fact]
    public void EveryValueHeldIsOptimized() =>
        Assert.Equal(KeepStatus.Game.Optimized, KeepStatus.Of(Game(KeepState.Held, KeepState.Held)));

    [Fact]
    public void ARefusedWriteNeverShowsOptimized() =>
        Assert.Equal(KeepStatus.Game.WontKeepSome, KeepStatus.Of(Game(KeepState.Held, KeepState.Refused)));

    [Fact]
    public void RefusedIsSaidBeforeEverythingElse() =>
        Assert.Equal(KeepStatus.Game.WontKeepSome,
            KeepStatus.Of(Game(KeepState.Refused, KeepState.Waiting, KeepState.Yielded, KeepState.Held)));

    [Fact]
    public void AWaitingValueOrAPendingOptimizeWaitsForTheFirstRun()
    {
        Assert.Equal(KeepStatus.Game.WaitsForFirstRun, KeepStatus.Of(Game(KeepState.Held, KeepState.Waiting)));
        var pending = new Keep.KeepGame { Keeping = new Keep.Keeping { Groups = ["g"], Pending = true } };
        Assert.Empty(pending.Entries);
        Assert.Equal(KeepStatus.Game.WaitsForFirstRun, KeepStatus.Of(pending));
    }

    [Fact]
    public void AYieldedValueSaysYourChangesKept() =>
        Assert.Equal(KeepStatus.Game.YourChangesKept, KeepStatus.Of(Game(KeepState.Held, KeepState.Yielded)));

    [Fact]
    public void APutBackAfterAnUpdateSaysSoUntilTheGameRunsAgain()
    {
        var again = Game(KeepState.Held, KeepState.PutBack);
        again.Keeping!.Again = new DateTime(2026, 11, 18, 17, 0, 0, DateTimeKind.Utc);
        Assert.Equal(KeepStatus.Game.PutBackAfterUpdate, KeepStatus.Of(again));
        var ran = Game(KeepState.Held, KeepState.PutBack);
        Assert.Null(ran.Keeping!.Again);
        Assert.Equal(KeepStatus.Game.Optimized, KeepStatus.Of(ran));
    }

    [Fact]
    public void AValueThatCouldNotBePutBackIsNotOptimized() =>
        Assert.NotEqual(KeepStatus.Game.Optimized, KeepStatus.Of(Game(KeepState.Held, KeepState.Undone)));

    [Fact]
    public void ASkippedLineDoesNotHideOptimized() =>
        Assert.Equal(KeepStatus.Game.Optimized, KeepStatus.Of(Game(KeepState.Held, KeepState.Skipped)));

    [Fact]
    public void ThePcIsOptimizedWhenAnyGameIs()
    {
        Assert.Equal(KeepStatus.Pc.None, KeepStatus.ForPc([KeepStatus.Game.None]));
        Assert.Equal(KeepStatus.Pc.None, KeepStatus.ForPc([KeepStatus.Game.WontKeepSome]));
        Assert.Equal(KeepStatus.Pc.Optimized, KeepStatus.ForPc([KeepStatus.Game.None, KeepStatus.Game.Optimized]));
        Assert.Equal(KeepStatus.Pc.Optimized, KeepStatus.ForPc([KeepStatus.Game.YourChangesKept]));
        Assert.Equal(KeepStatus.Pc.PutBackAfterDriverUpdate, KeepStatus.ForPc([KeepStatus.Game.Optimized], driverPutBack: true));
        Assert.Equal(KeepStatus.Pc.NeedsYourOk, KeepStatus.ForPc([KeepStatus.Game.Optimized], driverPutBack: true, needsOk: true));
    }

    [Fact]
    public void TheReportComesFromTheRecordAloneUnderEachGameIdItWasPressedFor()
    {
        // The report is made from the record it is given: no library, no game's install folder is read.
        var first = Game(KeepState.Held);
        first.GameIds = ["g1", "g2"];
        var second = Game(KeepState.Held);
        var doc = new Keep.Doc();
        doc.Games["p1"] = first;
        doc.Games["p2"] = second;

        var report = KeepStatus.For(doc);

        Assert.Equal("optimized", report.Pc);
        Assert.Equal(["g1", "g2"], report.Games.Keys.Order().ToArray());
    }

    [Fact]
    public void NotingAGameTwiceWritesTheRecordOnce()
    {
        var folder = Path.Combine(Path.GetTempPath(), "keepnoting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "keep.json");
        using var forced = Keep.ForcePathForTest(path);
        Assert.True(Keep.Update(doc => doc.Games["p1"] = new Keep.KeepGame { Keeping = new Keep.Keeping { Groups = ["g"] } }));

        GameSettings.NoteGame("p1", "g1");
        var once = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(20);
        GameSettings.NoteGame("p1", "g1");

        Assert.Equal(once, File.GetLastWriteTimeUtc(path));
        Assert.Equal(["g1"], Keep.Load().Games["p1"].GameIds);
        // A profile with no record at all: nothing is made for it.
        GameSettings.NoteGame("no-such-profile", "g9");
        Assert.False(Keep.Load().Games.ContainsKey("no-such-profile"));
    }

    [Fact]
    public void TheReportLeavesOutGamesWithNoStatusAndNamesNothingButGamesAndWords()
    {
        var first = new Keep.KeepGame { Keeping = new Keep.Keeping { Groups = ["g"] }, GameIds = ["g1"] };
        first.Entries["a"] = new Keep.KeepEntry
            { State = KeepState.Held, Target = "TARGET-XYZ", Intended = "INTENDED-XYZ", Before = "BEFORE-XYZ" };
        var second = new Keep.KeepGame { Keeping = new Keep.Keeping { Groups = ["g"] }, GameIds = ["g2"] };
        second.Entries["a"] = new Keep.KeepEntry
            { State = KeepState.Held, Target = "TARGET-XYZ", Intended = "INTENDED-XYZ", Before = "BEFORE-XYZ" };
        second.Entries["b"] = new Keep.KeepEntry
            { State = KeepState.Yielded, Target = "TARGET-XYZ", Intended = "INTENDED-XYZ", Before = "BEFORE-XYZ" };
        // A record with nothing to say, and one never pressed for a library game: neither is told.
        var none = new Keep.KeepGame { GameIds = ["g4"] };
        var unpressed = new Keep.KeepGame { Keeping = new Keep.Keeping { Groups = ["g"] } };
        var doc = new Keep.Doc();
        doc.Games["p1"] = first;
        doc.Games["p2"] = second;
        doc.Games["p3"] = none;
        doc.Games["p4"] = unpressed;

        var report = KeepStatus.For(doc);

        Assert.Equal("optimized", report.Pc);
        Assert.Equal(["g1", "g2"], report.Games.Keys.Order().ToArray());
        Assert.Equal("optimized", report.Games["g1"]);
        Assert.Equal("yourChangesKept", report.Games["g2"]);
        var text = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("TARGET-XYZ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("INTENDED-XYZ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEFORE-XYZ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BothLibraryEntriesOfOneGameGetAStatus()
    {
        using var record = Keep.RecordForTest(out var path);
        Assert.True(Keep.Update(doc => doc.Games[GameSettings.ModernWarfare4(Owner).Id] = OneGroup()));

        // The library lists the same game twice: two cards, one record. The start-up look notes both ids, so the page's status is
        // told under both rather than under the one that happened to come first.
        GameSettings.NoteLibrary([Entry("steam:1"), Entry("battlenet:1")]);

        Assert.Equal(["battlenet:1", "steam:1"], Keep.Load().Games[GameSettings.ModernWarfare4(Owner).Id].GameIds.Order(StringComparer.Ordinal));
        var report = KeepStatus.For(Keep.Load());
        Assert.Equal(KeepStatus.Word(KeepStatus.Game.Optimized), report.Games["steam:1"]);
        Assert.Equal(KeepStatus.Word(KeepStatus.Game.Optimized), report.Games["battlenet:1"]);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AnEmptyOrPartialLibraryAtStartRemovesNoGamesStatus()
    {
        using var record = Keep.RecordForTest(out _);
        var id = GameSettings.ModernWarfare4(Owner).Id;
        Assert.True(Keep.Update(doc => doc.Games[id] = new Keep.KeepGame
            { Keeping = new Keep.Keeping { Groups = ["latency"] }, GameIds = ["g1", "g2"] }));

        // A first run, a games drive not connected, or a store that did not answer: the library at start is empty. The
        // record keeps every id it had, and the page is still told the status under each.
        GameSettings.NoteLibrary([]);
        Assert.Equal(["g1", "g2"], Keep.Load().Games[id].GameIds.Order(StringComparer.Ordinal));
        Assert.Equal(["g1", "g2"], KeepStatus.For(Keep.Load()).Games.Keys.Order(StringComparer.Ordinal));

        // A partial library: only g1's entry is listed. g2's is not dropped for being absent.
        GameSettings.NoteLibrary([Entry("g1")]);
        Assert.Equal(["g1", "g2"], Keep.Load().Games[id].GameIds.Order(StringComparer.Ordinal));
        Assert.Equal(["g1", "g2"], KeepStatus.For(Keep.Load()).Games.Keys.Order(StringComparer.Ordinal));
    }

    private static readonly GameSettings.Pc Owner = new("nvidia", 5, 8, Screens: 2);

    /// <summary>Once Optimized, with one group on and no value written yet: the record's status is Optimized.</summary>
    private static Keep.KeepGame OneGroup() => new() { Keeping = new Keep.Keeping { Groups = ["latency"] } };

    /// <summary>A library entry whose profile is Modern Warfare 4's, found by its install folder's cod26 subfolder.</summary>
    private static GameEntry Entry(string id)
    {
        var folder = Path.Combine(Path.GetTempPath(), "keeplibrary-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "cod26"));
        return new GameEntry { Id = id, Title = "Call of Duty", Store = StoreKind.Steam, Installed = true, Path = folder };
    }

    [Fact]
    public void EveryStatusWordIsCamelCase()
    {
        foreach (var status in Enum.GetValues<KeepStatus.Game>())
        {
            var word = KeepStatus.Word(status);
            Assert.True(char.IsLower(word[0]), $"{status} is said as {word}");
            Assert.Equal(status.ToString(), word, ignoreCase: true);
        }
        foreach (var status in Enum.GetValues<KeepStatus.Pc>())
        {
            var word = KeepStatus.Word(status);
            Assert.True(char.IsLower(word[0]), $"{status} is said as {word}");
            Assert.Equal(status.ToString(), word, ignoreCase: true);
        }
    }
}

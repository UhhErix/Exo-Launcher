using System.Text.Json;
using ExoLauncher.Services;
using static ExoLauncher.Tests.KeepKit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways committing a write could lie:
/// - the game's file is written before the undo record is on disk, so a crash between the two leaves a change with no undo;
/// - a write the file never took (it is locked) is answered with success, or leaves the record half changed;
/// - a write the file did not take is shown as Optimized.
/// </summary>
public sealed class KeepCommitTests
{
    /// <summary>Every line of the profile's files by its record key (file key and line id), as they read now.</summary>
    private static Dictionary<string, string> Lines(GameSettings.Profile profile) =>
        GameSettings.ReadAll(profile).SelectMany(open => open.File.Settings.Select(line => (Key: open.Key + line.Id, line.Value)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    [Fact]
    public void TheUndoRecordIsWrittenBeforeTheGamesFile()
    {
        var game = Fresh();
        var before = Lines(game.Profile);
        var calls = 0;
        Dictionary<string, (string Before, string Intended)>? atFirstCall = null;
        using (GameSettings.ForceWriterForTest((path, bytes) =>
        {
            if (++calls == 1)
                atFirstCall = (Keep.Load().Games.GetValueOrDefault(game.Profile.Id)?.Entries ?? [])
                    .ToDictionary(pair => pair.Key, pair => (pair.Value.Before, pair.Value.Intended), StringComparer.Ordinal);
            File.WriteAllBytes(path, bytes);
        }))
        {
            Assert.True(GameSettings.Set(game.Profile, null, true, game.Program));
        }

        Assert.True(calls > 0);
        Assert.NotNull(atFirstCall);
        var after = Lines(game.Profile);
        var changed = after.Where(pair => before.TryGetValue(pair.Key, out var was) && was != pair.Value).ToList();
        Assert.NotEmpty(changed);
        foreach (var (key, value) in changed)
        {
            Assert.True(atFirstCall!.TryGetValue(key, out var entry), $"{key} changed, but the record written before the file had no entry for it");
            Assert.Equal(before[key], entry.Before);
            Assert.Equal(value, entry.Intended);
        }
    }

    [Fact]
    public void OptimizeOnALockedFileReportsFailureAndLeavesNoRecord()
    {
        var game = Fresh();
        var bytes = File.ReadAllBytes(game.Main);
        using (GameSettings.ForceWriterForTest((_, _) => throw new IOException("locked")))
            Assert.False(GameSettings.Set(game.Profile, null, true, game.Program));

        Assert.Equal(bytes, File.ReadAllBytes(game.Main));
        var record = Keep.Load().Games.GetValueOrDefault(game.Profile.Id);
        Assert.True(record is null || record.Entries.Count == 0);
        Assert.Empty(GameSettings.KeptOn(game.Profile));
    }

    [Fact]
    public void PutBackOnALockedFileReportsFailure()
    {
        var game = Fresh();
        Assert.True(GameSettings.Set(game.Profile, null, true, game.Program));
        var recordBefore = JsonSerializer.Serialize(Keep.Load().Games[game.Profile.Id]);
        var fileBefore = File.ReadAllBytes(game.Main);
        using (GameSettings.ForceWriterForTest((_, _) => throw new IOException("locked")))
            Assert.False(GameSettings.Set(game.Profile, null, false, game.Program));

        Assert.Equal(fileBefore, File.ReadAllBytes(game.Main));
        Assert.Equal(recordBefore, JsonSerializer.Serialize(Keep.Load().Games[game.Profile.Id]));
        Assert.NotEmpty(GameSettings.KeptOn(game.Profile));
    }

    [Fact]
    public void AFailedGroupChangeLeavesTheWholeRecordAsItWas()
    {
        var game = Fresh();
        Assert.True(GameSettings.Set(game.Profile, null, true, game.Program));
        var recordBefore = JsonSerializer.Serialize(Keep.Load().Games[game.Profile.Id]);
        var fileBefore = File.ReadAllBytes(game.Main);
        using (GameSettings.ForceWriterForTest((_, _) => throw new IOException("locked")))
            Assert.False(GameSettings.Set(game.Profile, "audio", false, game.Program));

        Assert.Equal(fileBefore, File.ReadAllBytes(game.Main));
        Assert.Equal(recordBefore, JsonSerializer.Serialize(Keep.Load().Games[game.Profile.Id]));
    }

    [Fact]
    public void AFailureOnTheSecondFilePutsTheFirstFileBack()
    {
        var game = Fresh();
        var before = GameSettings.ReadAll(game.Profile).ToDictionary(open => open.Path, open => File.ReadAllBytes(open.Path), StringComparer.OrdinalIgnoreCase);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (GameSettings.ForceWriterForTest((path, bytes) =>
        {
            // The first file written takes Exo's values; the second is held by the game, and only the first can be written again.
            if (written.Count > 0 && !written.Contains(path)) throw new IOException("the second file is locked");
            File.WriteAllBytes(path, bytes);
            written.Add(path);
        }))
            Assert.False(GameSettings.Set(game.Profile, null, true, game.Program));

        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        var record = Keep.Load().Games.GetValueOrDefault(game.Profile.Id);
        Assert.True(record is null || record.Entries.Count == 0);
    }

    [Fact]
    public void AFirstFileThatCannotBePutBackKeepsItsUndo()
    {
        var game = Fresh();
        var mainBefore = File.ReadAllBytes(game.Main);
        var playerBefore = File.ReadAllBytes(game.Player);
        var before = Lines(game.Profile);
        var calls = 0;
        using (GameSettings.ForceWriterForTest((path, bytes) =>
        {
            // The first file is written, then the second's write fails and the first cannot be put back either.
            if (++calls == 1) File.WriteAllBytes(path, bytes);
            else throw new IOException("locked");
        }))
            Assert.False(GameSettings.Set(game.Profile, null, true, game.Program));

        Assert.NotEqual(mainBefore, File.ReadAllBytes(game.Main));
        Assert.Equal(playerBefore, File.ReadAllBytes(game.Player));
        var record = Keep.Load().Games[game.Profile.Id];
        var changed = Lines(game.Profile).Where(pair => pair.Value != before[pair.Key]).ToList();
        Assert.NotEmpty(changed);
        foreach (var (key, _) in changed)
        {
            Assert.True(record.Entries.TryGetValue(key, out var entry), $"{key} changed in a file that could not be put back, but its undo was dropped");
            Assert.Equal(before[key], entry.Before);
        }
        Assert.DoesNotContain(record.Entries.Keys, key => key.Contains('|', StringComparison.Ordinal));

        // With the file free again, Put back restores what it held before Exo.
        Assert.True(GameSettings.Set(game.Profile, null, false, game.Program));
        Assert.Equal(mainBefore, File.ReadAllBytes(game.Main));
    }

    [Fact]
    public void PutBackThatCannotSaveTheRecordReportsFailure()
    {
        var game = Optimized();
        // The game put its own values back, so Put back has nothing to write: only the record is left to save ("Optimize is off").
        Reset(game.Main, game.Player);
        using (Keep.FailSavesForTest())
            Assert.False(GameSettings.Set(game.Profile, null, false, game.Program));

        // The record still says Optimize is on, which is what the next look will act on.
        Assert.NotEmpty(GameSettings.KeptOn(game.Profile));
    }

    [Fact]
    public void OptimizeThatCannotSaveTheRecordReportsFailureAndWritesNothing()
    {
        var game = Fresh();
        var before = GameSettings.ReadAll(game.Profile).ToDictionary(open => open.Path, open => File.ReadAllBytes(open.Path), StringComparer.OrdinalIgnoreCase);
        using (Keep.FailSavesForTest())
            Assert.False(GameSettings.Set(game.Profile, null, true, game.Program));

        // The undo record comes first: without it, nothing is written.
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ARefusedWriteNeverShowsOptimized()
    {
        var game = Fresh();
        var originals = GameSettings.ReadAll(game.Profile).ToDictionary(open => open.Path, open => File.ReadAllBytes(open.Path), StringComparer.OrdinalIgnoreCase);
        using (GameSettings.ForceWriterForTest((path, _) => File.WriteAllBytes(path, originals[path])))
            Assert.True(GameSettings.Set(game.Profile, null, true, game.Program));

        var record = Keep.Load().Games[game.Profile.Id];
        Assert.DoesNotContain(record.Entries.Values, entry => entry.State == KeepState.Held);
        Assert.NotEqual(KeepStatus.Game.Optimized, KeepStatus.Of(game.Profile.Id));
    }
}

using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// A restore or import writes files the running process also owns, and this run closes straight after. Its close-time saves
/// (playtime's flush of open sessions, the Riot ledger's, notes', window placement's debounced save) would write what this run
/// held back over the restored file. Ways this could fail: a session still open when the restore lands is credited on exit and
/// its file overwrites the restored hours; a ledger line is appended after the restore; a note saved after the restore
/// replaces the restored notes.json; a window move after the restore writes the old placement over the restored one.
/// Fixtures match no real player or account.
/// </summary>
public sealed class RestoreKeepsOwnedFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-restore-owned-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private const string Game = "steam:999999941";

    public RestoreKeepsOwnedFilesTests()
    {
        Directory.CreateDirectory(_folder);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _folder);
        PlaytimeService.ForgetLoaded();
        RiotLedger.ForgetLoaded();
    }

    public void Dispose()
    {
        PlaytimeService.ForgetLoaded();
        RiotLedger.ForgetLoaded();
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch { /* temp */ }
    }

    private string In(string name) => System.IO.Path.Combine(_folder, name);

    [Fact]
    public void AFlushAfterARestoreKeepsTheRestoredPlaytime()
    {
        // A game is open with 30 minutes counted in memory (its process started half an hour ago).
        PlaytimeService.AdoptSession(Game, DateTimeOffset.UtcNow.AddMinutes(-30));
        // The restore writes the backup's playtime.json (5 minutes); this run still holds its open session.
        File.WriteAllText(In("playtime.json"), "{\"minutes\":{\"" + Game + "\":5}}");
        PlaytimeService.KeepRestoredFile();

        PlaytimeService.FlushActiveSessions();

        Assert.Equal(5, MinutesOnDisk(Game));
    }

    [Fact]
    public void WithoutTheRestoreFlagTheSameFlushWritesItsMinutes()
    {
        // Control: the same setup without the restore keeps the flush's minutes, so the test above can fail.
        PlaytimeService.AdoptSession(Game, DateTimeOffset.UtcNow.AddMinutes(-30));
        File.WriteAllText(In("playtime.json"), "{\"minutes\":{\"" + Game + "\":5}}");

        PlaytimeService.FlushActiveSessions();

        Assert.Equal(30, MinutesOnDisk(Game));
    }

    [Fact]
    public void ARiotLedgerCheckpointAfterARestoreAppendsNothing()
    {
        var ledger = In("riot-ledger");
        const string restored = "{\"schema\":1,\"kind\":\"seed\",\"key\":\"seed|riot:valorant|restored\",\"game\":\"riot:valorant\",\"minutes\":7}\n";
        RiotLedger.Begin(RiotLedger.Unassigned, "riot:valorant", DateTimeOffset.UtcNow.AddMinutes(-30));
        Directory.CreateDirectory(ledger);
        File.WriteAllText(System.IO.Path.Combine(ledger, RiotLedger.Unassigned + ".jsonl"), restored);
        RiotLedger.KeepRestoredFile();

        RiotLedger.Checkpoint(DateTimeOffset.UtcNow);
        RiotLedger.End("riot:valorant", DateTimeOffset.UtcNow);

        Assert.Equal(restored, File.ReadAllText(System.IO.Path.Combine(ledger, RiotLedger.Unassigned + ".jsonl")));
    }

    [Fact]
    public void ANoteSavedAfterARestoreDoesNotReplaceTheRestoredNotes()
    {
        var statePath = In("notes-state.json");
        var store = new NoteStore(statePath, In("notes"));
        var note = store.Create("first line", markdown: false, gameId: null, out _);
        Assert.NotNull(note);
        const string restored = "{\"folder\":null,\"notes\":{}}";
        File.WriteAllText(statePath, restored);
        store.KeepRestoredFile();

        store.Pin(note!.Id, true, out _);

        Assert.Equal(restored, File.ReadAllText(statePath));
    }

    [Fact]
    public void AWindowMoveAfterARestoreDoesNotReplaceTheRestoredPlacement()
    {
        WindowPlacementStore.Save(new WindowPlacement(10, 20, 1600, 900, false, 1));
        var restored = new WindowPlacement(30, 40, 1280, 720, false, 1);
        WindowPlacementStore.Save(restored);
        // Save restores its own file; the restore itself writes the backup's placement last.
        File.WriteAllText(WindowPlacementStore.Path, System.Text.Json.JsonSerializer.Serialize(restored));
        WindowPlacementStore.KeepRestoredFile();

        WindowPlacementStore.Save(new WindowPlacement(50, 60, 1000, 700, false, 1));

        Assert.Equal(restored, WindowPlacementStore.Load());
    }

    private int MinutesOnDisk(string game)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(In("playtime.json")))!;
        return root["minutes"]?[game]?.GetValue<int>() ?? 0;
    }
}

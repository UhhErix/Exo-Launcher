using System.Text.Json.Nodes;
using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Epic's quiet is made only when it is wanted, and its undo is never a lie (Fixtures/Epic/app-settings.json, copied to a
/// temp folder each time: never this PC's Epic). Ways it could fail: Epic's settings changed, or a marker made, when quiet
/// is not wanted; an undo record written after a change (a crash between leaves a change that cannot be undone) or one that
/// changes something when the change never happened; Off that leaves what Exo wrote or overwrites what the player changed
/// since; quiet written again while an Off waits for Epic to close.
/// </summary>
public sealed class EpicQuietKeepTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-epic-quiet-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(_folder, "app-settings.json");
    private string Record => Path.Combine(_folder, "epic-quiet.json");

    public EpicQuietKeepTests()
    {
        Directory.CreateDirectory(_folder);
        File.Copy(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "Epic", "app-settings.json"), Settings);
    }

    public void Dispose() => ExoLauncher.Helpers.DeletionGate.DeleteDirectory(_folder, recursive: true);

    private static void Write(string path, string text) => File.WriteAllText(path, text);

    private JsonNode Now() => JsonNode.Parse(File.ReadAllText(Settings))!;

    private JsonNode Original() => JsonNode.Parse(File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "Epic", "app-settings.json")))!;

    [Fact]
    public void EpicIsNotTouchedUnlessQuietIsWanted()
    {
        var bytes = File.ReadAllBytes(Settings);
        EpicQuietSettings.HandAt(wanted: false, Record, Settings, epicRunning: false, Write);
        Assert.Equal(bytes, File.ReadAllBytes(Settings));
        Assert.False(File.Exists(Record));

        // Wanted: the marker is made and the settings change.
        EpicQuietSettings.HandAt(wanted: true, Record, Settings, epicRunning: false, Write);
        Assert.True(File.Exists(Record));
        Assert.False(Now()["enableNotifications"]!.GetValue<bool>());
        Assert.Equal("lowResources", Now()["performanceProfile"]!["preset"]!.GetValue<string>());
        Assert.Equal(EpicQuietSettings.Done, EpicQuietSettings.ReadRecord(Record).State);
    }

    [Fact]
    public void AFailedWriteLeavesAnUndoThatChangesNothing()
    {
        var bytes = File.ReadAllBytes(Settings);
        // The record is written (as "intended", first); the write of Epic's settings fails.
        Assert.Throws<IOException>(() => EpicQuietSettings.HandAt(wanted: true, Record, Settings, epicRunning: false, (path, text) =>
        {
            if (path == Settings) throw new IOException("locked");
            File.WriteAllText(path, text);
        }));
        Assert.Equal(bytes, File.ReadAllBytes(Settings));
        var record = EpicQuietSettings.ReadRecord(Record);
        Assert.Equal(EpicQuietSettings.Intended, record.State);
        Assert.True(record.Before["enableNotifications"]!.GetValue<bool>());
        // The undo touches no key that does not hold what Exo wrote: the settings stay byte for byte.
        Assert.True(EpicQuietSettings.UndoAt(Record, Settings, epicRunning: false, Write));
        Assert.Equal(bytes, File.ReadAllBytes(Settings));
        Assert.False(File.Exists(Record));
    }

    [Fact]
    public void EpicOffPutsBackExactlyWhatWasThere()
    {
        EpicQuietSettings.HandAt(wanted: true, Record, Settings, epicRunning: false, Write);
        Assert.False(JsonNode.DeepEquals(Original(), Now()));
        EpicQuietSettings.HandAt(wanted: false, Record, Settings, epicRunning: false, Write);
        Assert.True(JsonNode.DeepEquals(Original(), Now()));
        Assert.False(File.Exists(Record));
    }

    [Fact]
    public void EpicOffWaitsForEpicToCloseAndNothingIsWrittenMeanwhile()
    {
        EpicQuietSettings.HandAt(wanted: true, Record, Settings, epicRunning: false, Write);
        var quiet = File.ReadAllText(Settings);
        // Epic runs: the undo waits, marked in the record.
        EpicQuietSettings.HandAt(wanted: false, Record, Settings, epicRunning: true, Write);
        Assert.Equal(quiet, File.ReadAllText(Settings));
        Assert.True(EpicQuietSettings.ReadRecord(Record).Off);
        // Epic comes back as the player has it (notifications on again): the pass before Exo starts it puts back, never writes quiet.
        var loud = JsonNode.Parse(quiet)!;
        loud["enableNotifications"] = true;
        File.WriteAllText(Settings, loud.ToJsonString());
        EpicQuietSettings.ApplyAt(Record, Settings, Write);
        Assert.True(Now()["enableNotifications"]!.GetValue<bool>());
        Assert.True(Now()["verboseLogs"]!.GetValue<bool>());
        Assert.False(File.Exists(Record));
    }

    [Fact]
    public void AKeyThePlayerChangedSinceIsLeftAlone()
    {
        EpicQuietSettings.HandAt(wanted: true, Record, Settings, epicRunning: false, Write);
        var edited = Now().AsObject();
        edited["enableNotifications"] = true;                       // the player turned it back on in Epic
        edited["performanceProfile"]!["preset"] = "custom";         // and chose a preset of their own
        File.WriteAllText(Settings, edited.ToJsonString());
        EpicQuietSettings.HandAt(wanted: false, Record, Settings, epicRunning: false, Write);
        Assert.True(Now()["enableNotifications"]!.GetValue<bool>());
        Assert.Equal("custom", Now()["performanceProfile"]!["preset"]!.GetValue<string>());
        // What Exo changed and the player did not is put back.
        Assert.True(Now()["verboseLogs"]!.GetValue<bool>());
        Assert.True(Now()["showWhatsNewOnStartup"]!.GetValue<bool>());
    }

    [Fact]
    public void ARecordFromBeforeIsReadAsDone()
    {
        File.WriteAllText(Record, """{ "enableNotifications": true }""");
        var record = EpicQuietSettings.ReadRecord(Record);
        Assert.Equal(EpicQuietSettings.Done, record.State);
        Assert.False(record.Off);
        Assert.True(record.Before["enableNotifications"]!.GetValue<bool>());
    }
}

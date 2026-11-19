using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// A restore or import writes settings.json, then Exo restarts. The run that is closing still holds the settings it loaded
/// before the restore, and its exit save (Flush) or any save before the restart would write those back over the restored
/// file. Ways this could fail: Load clears the last written text, so the first save after a restore is not recognised as
/// stale; a save on the way out writes the old settings; a save after the restore is not refused.
/// </summary>
public sealed class SettingsRestoreKeepsFileTests : IDisposable
{
    private readonly string? _prior = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-settings-restore-" + Guid.NewGuid().ToString("N"));

    public SettingsRestoreKeepsFileTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _prior);
        try { DeletionGate.DeleteDirectory(_dir, true); } catch { }
    }

    private static string SortModeOnDisk() =>
        JsonNode.Parse(File.ReadAllText(PathHelper.SettingsPath))!["sortMode"]!.GetValue<string>();

    [Fact]
    public void AFlushAfterARestoreLeavesTheRestoredFileAlone()
    {
        var service = new SettingsService();
        service.Load();
        service.UpdateProfile(settings => settings.SortMode = "played");

        // The restore writes the backup's settings over settings.json; this run still holds "played".
        File.WriteAllText(PathHelper.SettingsPath, "{\"sortMode\":\"name\"}");
        service.KeepRestoredFile();

        service.Flush();

        Assert.Equal("name", SortModeOnDisk());
    }

    [Fact]
    public void ASaveAfterARestoreIsNotWrittenOverTheRestoredFile()
    {
        var service = new SettingsService();
        service.Load();
        service.UpdateProfile(settings => settings.SortMode = "played");

        File.WriteAllText(PathHelper.SettingsPath, "{\"sortMode\":\"name\"}");
        service.KeepRestoredFile();

        service.UpdateProfile(settings => settings.SortMode = "played");

        Assert.Equal("name", SortModeOnDisk());
    }

    [Fact]
    public void ALoadAfterARestoreReadsTheRestoredSettings()
    {
        var service = new SettingsService();
        service.Load();
        File.WriteAllText(PathHelper.SettingsPath, "{\"sortMode\":\"name\"}");
        service.KeepRestoredFile();

        var next = new SettingsService();
        next.Load();

        Assert.Equal("name", next.Current.SortMode);
    }
}

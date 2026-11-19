using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Settings files written before crash reports became on by default still hold "sendCrashReports". The key has no
/// property any more, so it must be ignored: the file still loads, the other settings are kept, and the key is gone
/// after the next save. A failure here would move a user's settings.json aside as corrupt.
/// </summary>
public sealed class LegacySendCrashReportsSettingTests : IDisposable
{
    private readonly string? _prior = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-legacy-crash-" + Guid.NewGuid().ToString("N"));

    public LegacySendCrashReportsSettingTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _prior);
        try { DeletionGate.DeleteDirectory(_dir, true); } catch { }
    }

    [Fact]
    public void Stored_sendCrashReports_is_ignored_and_dropped_on_save()
    {
        File.WriteAllText(PathHelper.SettingsPath, "{\"sendCrashReports\": false, \"startMaximized\": true}");

        var service = new SettingsService();
        service.Load();
        Assert.True(service.Current.StartMaximized);

        service.UpdateProfile(settings => settings.StartMaximized = false);
        var saved = File.ReadAllText(PathHelper.SettingsPath);
        Assert.DoesNotContain("sendCrashReports", saved, StringComparison.OrdinalIgnoreCase);
    }
}

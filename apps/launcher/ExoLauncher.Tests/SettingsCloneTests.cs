using System.Collections;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Saving settings copies them field by field. A setting added to <see cref="AppSettings"/> but not to that copy is
/// lost on every save (Start Lucid with Exo turned itself back off this way), so every simple setting must survive it.
/// </summary>
public sealed class SettingsCloneTests
{
    [Fact]
    public void Every_simple_setting_survives_a_save()
    {
        var source = new AppSettings();
        var changed = new List<string>();
        foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value = type == typeof(bool) ? !(bool)(property.GetValue(source) ?? false)
                : type == typeof(string) ? "C:\\Exo\\Lucid.exe"
                : null;
            if (value is null) continue;
            property.SetValue(source, value);
            changed.Add(property.Name);
        }

        var copy = SettingsService.Clone(source);

        // Settings the copy deliberately normalizes to a fixed set of values or rewrites.
        string[] normalized = ["TextColour", "TextSize", "Background", "SortMode", "DiscordInputProfile", "MouseBridge", "DefaultInstallRoot",
            "DiscordInputDevice", "DiscordOutputDevice", "AppVersion", "NvidiaDriverBranch"];
        var lost = changed.Except(normalized).Where(name =>
        {
            var property = typeof(AppSettings).GetProperty(name)!;
            return !Equals(property.GetValue(source), property.GetValue(copy));
        }).ToList();
        Assert.Empty(lost);
    }

    [Fact]
    public void Load_ignores_legacy_compressSpeed_and_speed_properties()
    {
        var folder = Path.Combine(Path.GetTempPath(), "exo-settings-legacy-" + Guid.NewGuid().ToString("N"));
        var prior = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "settings.json"), """
                {
                  "compressWhileIdle": true,
                  "recompressAfterUpdates": true,
                  "compressSpeed": "fast",
                  "speed": "gentle"
                }
                """);
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, folder);
            var service = new Services.SettingsService();
            service.Load();
            Assert.True(service.Current.CompressWhileIdle);
            Assert.True(service.Current.RecompressAfterUpdates);

            service.SetSmoothMotion("steam:3513350", true);
            Assert.True(service.SmoothMotionEnabled("steam:3513350"));
            Assert.False(service.SmoothMotionEnabled("steam:730"));
            var cloned = Services.SettingsService.Clone(service.Current);
            Assert.True(cloned.SmoothMotion["steam:3513350"]);
            service.SetSmoothMotion("steam:3513350", false);
            Assert.False(service.SmoothMotionEnabled("steam:3513350"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, prior);
            try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch { /* temp */ }
        }
    }
}

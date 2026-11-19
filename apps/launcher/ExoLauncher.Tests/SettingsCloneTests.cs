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
            "DiscordInputDevice", "DiscordOutputDevice", "AppVersion", "NvidiaDriverBranch", "CompressSpeed"];
        var lost = changed.Except(normalized).Where(name =>
        {
            var property = typeof(AppSettings).GetProperty(name)!;
            return !Equals(property.GetValue(source), property.GetValue(copy));
        }).ToList();
        Assert.Empty(lost);
    }

    /// <summary>
    /// Pins to Discord's top bar could fail silently by being lost on save, by a hand-edited or stale file carrying something
    /// that isn't a server or DM id, by repeats drawing one chip twice, or by growing without end.
    /// </summary>
    [Fact]
    public void Discord_band_pins_survive_a_save_checked_and_bounded()
    {
        var source = new AppSettings
        {
            DiscordBandPins = new()
            {
                ["111"] = ["g:1", "d:2", "g:1", "x:3", "g:", "g:12a", "d:" + new string('9', 40)],
                ["222"] = [.. Enumerable.Range(0, 100).Select(i => $"g:{i}")],
                ["333"] = ["nonsense"],
            },
        };

        var copy = SettingsService.Clone(source).DiscordBandPins;

        Assert.Equal(["g:1", "d:2"], copy["111"]);
        Assert.Equal(SettingsService.DiscordBandPinsMost, copy["222"].Count);
        Assert.False(copy.ContainsKey("333"));
    }
}

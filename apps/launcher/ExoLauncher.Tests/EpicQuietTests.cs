using ExoLauncher.Adapters;
using System.Text.Json.Nodes;

namespace ExoLauncher.Tests;

/// <summary>
/// Starting the Epic Games Launcher in its tray and quieting its own toasts.
/// Ways it could fail: the new launcher gets the old -silent, which it does
/// not know, so its window opens on every Exo start; the old one gets
/// --autostart; quieting drops or reorders Epic's other settings; a value the
/// user already had off is recorded as on.
/// </summary>
public sealed class EpicQuietTests
{
    // From this PC's settings\app-settings.json (new launcher 0.97.0), trimmed.
    private const string Settings = """
        {
          "defaultInstallDir": "C:\\Users\\a\\Epic Games",
          "minimizeToTray": true,
          "startOnBoot": false,
          "enableNotifications": true,
          "showWhatsNewOnStartup": false,
          "performanceProfile": { "preset": "default", "customOverrides": null },
          "verboseLogs": true,
          "topBar": { "hideLabels": true }
        }
        """;

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Epic Games\Launcher\EpicGamesLauncher.exe", "--autostart")]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", "-silent")]
    public void Each_launcher_gets_its_own_tray_start_flag(string exe, string flag) =>
        Assert.Equal(flag, EpicAdapter.QuietStartFlag(exe));

    [Fact]
    public void Quiet_turns_off_only_what_was_on_and_keeps_the_rest()
    {
        var (json, before) = EpicQuietSettings.Quiet(Settings)!.Value;
        var settings = JsonNode.Parse(json)!.AsObject();
        Assert.False(settings["enableNotifications"]!.GetValue<bool>());
        Assert.False(settings["showWhatsNewOnStartup"]!.GetValue<bool>());
        Assert.True(settings["minimizeToTray"]!.GetValue<bool>());
        Assert.True(settings["topBar"]!["hideLabels"]!.GetValue<bool>());
        Assert.Equal(["enableNotifications", "verboseLogs", "performanceProfile.preset"], before.Keys);
    }

    [Fact]
    public void Quiet_lowers_the_preset_and_the_logs_and_nothing_beside_them()
    {
        var (json, _) = EpicQuietSettings.Quiet(Settings)!.Value;
        var settings = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("lowResources", settings["performanceProfile"]!["preset"]!.GetValue<string>());
        Assert.Null(settings["performanceProfile"]!["customOverrides"]);
        Assert.False(settings["verboseLogs"]!.GetValue<bool>());
        Assert.False(settings["startOnBoot"]!.GetValue<bool>());
    }

    [Fact]
    public void A_preset_the_user_chose_is_left_alone()
    {
        Assert.Null(EpicQuietSettings.Quiet("""{ "performanceProfile": { "preset": "custom" }, "verboseLogs": false }"""));
        Assert.Null(EpicQuietSettings.Quiet("""{ "performanceProfile": { "preset": "lowResources" } }"""));
    }

    [Fact]
    public void A_preset_and_logs_put_back_by_the_user_are_not_set_again()
    {
        var tuned = new HashSet<string> { EpicQuietSettings.PresetKey, EpicQuietSettings.VerboseKey };
        Assert.Null(EpicQuietSettings.Quiet("""{ "performanceProfile": { "preset": "default" }, "verboseLogs": true }""", tuned));
        // The notifications are held at every start.
        Assert.NotNull(EpicQuietSettings.Quiet("""{ "enableNotifications": true }""", tuned));
    }

    [Fact]
    public void Already_quiet_changes_nothing() =>
        Assert.Null(EpicQuietSettings.Quiet("""{ "enableNotifications": false, "showWhatsNewOnStartup": false }"""));

    [Fact]
    public void Restore_puts_back_what_Exo_changed_and_keeps_the_rest()
    {
        var (json, before) = EpicQuietSettings.Quiet(Settings)!.Value;
        var back = JsonNode.Parse(EpicQuietSettings.Restore(json, before)!)!.AsObject();
        Assert.True(back["enableNotifications"]!.GetValue<bool>());
        Assert.True(back["verboseLogs"]!.GetValue<bool>());
        Assert.False(back["showWhatsNewOnStartup"]!.GetValue<bool>());
        Assert.Equal("default", back["performanceProfile"]!["preset"]!.GetValue<string>());
        Assert.True(back["topBar"]!["hideLabels"]!.GetValue<bool>());
    }

    [Fact]
    public void Restore_leaves_a_value_the_user_changed_since()
    {
        var (json, before) = EpicQuietSettings.Quiet(Settings)!.Value;
        var edited = JsonNode.Parse(json)!.AsObject();
        edited["performanceProfile"]!["preset"] = "custom";
        edited["verboseLogs"] = true;
        var back = JsonNode.Parse(EpicQuietSettings.Restore(edited.ToJsonString(), before)!)!.AsObject();
        Assert.Equal("custom", back["performanceProfile"]!["preset"]!.GetValue<string>());
        Assert.True(back["enableNotifications"]!.GetValue<bool>());
        Assert.Null(EpicQuietSettings.Restore("""{ "performanceProfile": { "preset": "custom" } }""", before));
    }
}

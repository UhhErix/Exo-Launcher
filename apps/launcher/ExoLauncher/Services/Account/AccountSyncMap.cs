using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Models;

namespace ExoLauncher.Services.Account;

/// <summary>
/// What the Launcher syncs and how each piece maps to items (CLIENT.md 8). Every AppSettings property is listed in exactly
/// one of the two sets: a new property with no class fails the test, so nothing slips in unreviewed.
/// </summary>
internal static class AccountSyncMap
{
    /// <summary>The "settings" item: user preferences that mean the same on every PC.</summary>
    internal const string SettingsId = "app";

    /// <summary>Preferences the account carries (CLIENT.md 8, settings allowlist).</summary>
    internal static readonly string[] SyncedSettings =
    [
        "AchievementBanners", "AchievementVolume", "AntiCheatSafeMode", "AutoInstallRedistributables", "AutoUpdateGames",
        "Background", "CloseStoreClientsAfterLaunch", "ControllerSound", "DiscordLayout", "DownloadHoursEnd", "DownloadHoursStart", "FpsCounter",
        "FpsCounterHideOverBuiltIn", "FpsCounterItems", "HiddenGames", "KeepAwakeDownloads", "LibraryShowInstalled", "LibraryShowOwned",
        "MinimizeWhilePlaying", "PauseDownloadsWhilePlaying", "ShowLibraryTools", "SortMode",
        "StoreClientsQuiet", "TextColour", "TextSize", "UiPrefs",
    ];

    /// <summary>
    /// UiPrefs travels one key at a time ("UiPrefs.discord", ...): the settings item merges by top-level key, so two PCs
    /// changing two different page choices keep both, and the newest change of one choice wins.
    /// </summary>
    private const string UiPrefPrefix = "UiPrefs.";

    /// <summary>Stays on this PC: paths, window and display, GPU, Discord and voice, consents, and first-run flags.</summary>
    internal static readonly string[] DeviceSettings =
    [
        "AllowResize", "AppVersion", "AutoUpdateChipset", "CompressWhileIdle", "RecompressAfterUpdates", "AutoUpdateUpscalers", "CheckForUpdates", "CopyPortableIntoLibrary", "DefaultInstallRoot",
        "DiscordAttenuateOthers", "DiscordAttenuateSelf", "DiscordAttenuation", "DiscordAutoSensitivity",
        "DiscordDesktopNotifications", "DiscordDisableAllSounds", "DiscordDisabledSounds", "DiscordEchoCancellation",
        "DiscordEvenVoices", "DiscordVoiceTargetLufs", "DiscordMicAutoGain", "DiscordInputDevice", "DiscordInputProfile", "DiscordInputVolume",
        "DiscordKeybinds", "DiscordNoiseSuppression", "DiscordNoiseSuppressionKind", "DiscordVoiceTier", "DiscordVoiceIsolation", "DiscordLoopbackEchoCancellation", "DiscordOnlyMyVoice",
        "DiscordOthersNoiseSuppression", "DiscordOutputDevice", "DiscordOutputVolume", "DiscordPttDelay",
        "DiscordPushToTalk", "DiscordQuietWhilePlaying", "DiscordSelectedChannelSound",
        "DiscordSensitivity", "DiscordSoundVolume", "DiscordSoundboardMuted", "DiscordSoundboardVolume",
        "DiscordGoLiveFps", "DiscordGoLiveHeight", "DiscordGoLiveHevc",
        "DiscordTaskbarFlash", "DiscordUnreadBadge", "DiscordVoiceFocus", "DiscordVoiceTunes",
        "AutoUpdateOff", "Autopilot", "ExoAi", "FastGpuForGames", "FpsCounterPosition", "FrameCap", "FrameGenEverywhere", "FrameGenKeptOff", "FrameGenRoute", "FramelessMode",
        "FramelessPinSide", "FramelessPinTop", "HdrCursorFixGames", "HdrForGames", "HdrGameModes", "HdrGameScreens", "HdrSdrGamma",
        "HdrSwitchedOn", "HdrSwitchedRamp", "LastPlayed", "LaunchOverrides", "LibraryFolders", "StartLucidWithExo",
        "MouseBridge", "MouseBridgeAppAutostart", "MouseBridgeAppPath", "NvidiaColor", "NvidiaDriverBranch", "NvidiaGsync", "NvidiaLowLatency",
        "AccountAsked", "OnboardingComplete", "PlayStationPadSeen", "PreferredGameSources", "Recent", "ReduceMotion", "RtxVideoDriver", "RtxVideoHdr",
        "RtxVideoSuperResolution", "SdrGammaBefore", "SmoothMotion", "StartMaximized", "StartWithWindows", "StartupHandedToExo", "StartupMigrationVersion", "SteamBetaJoined",
        "SteamInput", "SteamOverlay", "SteamOverlayPrior", "SteamShortcuts", "SteamWithoutAntiCheat", "ThroughSteam", "UpscalerAntiCheatConsent", "UpscalerAntiCheatEverywhere", "UpscalerAutoUpdateDefaulted", "GraphicsModAntiCheatConsent", "GraphicsFeaturesBestSettings", "CustomCoverImages",
        "WhatsNewSeen", "UiPrefsMoved",
    ];

    /// <summary>The account's value for each synced page choice in a "settings" item's value, by key.</summary>
    internal static Dictionary<string, JsonElement> UiPrefsIn(string? value)
    {
        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (value is null || JsonNode.Parse(value) is not JsonObject node) return found;
        foreach (var key in PlayerData.UiPrefKeys.Where(PlayerData.UiPrefSyncs))
            if (node[UiPrefPrefix + key] is { } pref) found[key] = JsonSerializer.Deserialize<JsonElement>(pref.ToJsonString());
        return found;
    }

    /// <summary>The synced part of the settings, as the "settings" item's value.</summary>
    internal static JsonObject SettingsValue(AppSettings settings)
    {
        var all = JsonSerializer.SerializeToNode(settings)?.AsObject() ?? new JsonObject();
        var synced = new JsonObject();
        foreach (var name in SyncedSettings)
        {
            if (name == nameof(AppSettings.UiPrefs)) continue;
            if (all[name] is { } value) synced[name] = JsonNode.Parse(value.ToJsonString());
        }
        // Only the choices about the player, and none moved in from the page's old storage until the account had its say.
        foreach (var (key, value) in settings.UiPrefs)
            if (PlayerData.UiPrefSyncs(key) && !settings.UiPrefsMoved.Contains(key)) synced[UiPrefPrefix + key] = JsonNode.Parse(value.GetRawText());
        return synced;
    }

    /// <summary>Lays a pulled "settings" value over this PC's settings. Only synced properties are touched.</summary>
    internal static void ApplySettings(AppSettings target, string value)
    {
        var node = JsonNode.Parse(value)?.AsObject() ?? new JsonObject();
        foreach (var name in SyncedSettings)
        {
            if (name == nameof(AppSettings.UiPrefs)) continue;
            var property = typeof(AppSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || node[name] is null) continue;
            property.SetValue(target, node[name]!.Deserialize(property.PropertyType));
        }
        foreach (var key in PlayerData.UiPrefKeys.Where(PlayerData.UiPrefSyncs))
            if (node[UiPrefPrefix + key] is { } pref && pref.ToJsonString().Length <= PlayerData.MaxUiPrefBytes)
                target.UiPrefs[key] = JsonSerializer.Deserialize<JsonElement>(pref.ToJsonString());
    }

    /// <summary>The playtime entry for one play session. Its id is the device and the session, so a resent entry is the same item.</summary>
    internal static string PlaytimeValue(string gameId, DateTimeOffset start, DateTimeOffset end, int minutes) =>
        new JsonObject
        {
            ["game"] = gameId,
            ["minutes"] = minutes,
            ["start"] = start.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["end"] = end.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }.ToJsonString();

    /// <summary>The settings properties that are neither synced nor device-only (for the test that keeps the lists complete).</summary>
    internal static IEnumerable<string> Unclassified() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !SyncedSettings.Contains(name) && !DeviceSettings.Contains(name));
}

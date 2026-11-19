using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's own switches for its pop-ups and sounds, kept off for every account on this PC. Where Steam keeps them (its steamui settings pages and SteamUI.dll's names):
/// - userdata/&lt;id&gt;/7/remote/sharedconfig.vdf › UserRoamingConfigStore: Settings > Notifications (toasts never
///   shown, no sound with one, each client toast and sound: achievements, controllers, other users signed in), and
///   Software/Valve/Steam/FriendsUI › FriendsUIJSON, the friends list's settings as JSON;
/// - userdata/&lt;id&gt;/config/localconfig.vdf › UserLocalConfigStore: friends (the same friends switches as Steam
///   reads them before its friends list loads; ChatFlashMode 2 never flashes the taskbar), system (a screenshot's
///   toast and sound), news › NotifyAvailableGames (new releases and changes to your games) and WebStorage, where
///   Steam's own pages keep what they remember (SteamClient.Storage): the dialogs Steam puts before a game starts
///   (<see cref="LaunchDialogs"/>).
/// Steam holds both files in memory and writes them as it exits, so they are written while it is closed. The rest of
/// each file is kept byte for byte.
/// </summary>
internal static class StoreQuiet
{
    private static readonly (string[] Path, (string Key, string Value)[] Values)[] Shared =
    [
        (["UserRoamingConfigStore"],
        [
            ("DisableAllToasts", "1"), ("DisableToastsInGame", "1"), ("PlaySoundOnToast", "0"),
            ("AchievementNotificationToast", "0"), ("AchievementNotificationSound", "0"),
            ("ControllerConnectNotificationToast", "0"), ("ControllerConnectNotificationSound", "0"),
            ("ControllerLowBatteryNotificationToast", "0"), ("ControllerLowBatteryNotificationSound", "0"),
            ("LoggedInAsUserNotificationToast", "0"), ("LoggedInAsUserNotificationSound", "0"),
        ]),
    ];

    private static readonly (string[] Path, (string Key, string Value)[] Values)[] Local =
    [
        (["UserLocalConfigStore", "friends"],
        [
            ("Notifications_ShowIngame", "0"), ("Notifications_ShowOnline", "0"), ("Notifications_ShowMessage", "0"),
            ("Notifications_EventsAndAnnouncements", "0"), ("Sounds_PlayIngame", "0"), ("Sounds_PlayOnline", "0"),
            ("Sounds_PlayMessage", "0"), ("Sounds_EventsAndAnnouncements", "0"), ("ChatFlashMode", "2"),
        ]),
        (["UserLocalConfigStore", "system"], [("InGameOverlayScreenshotNotification", "0"), ("InGameOverlayScreenshotPlaySound", "0")]),
        (["UserLocalConfigStore", "news"], [("NotifyAvailableGames", "0")]),
    ];

    private static readonly string[] FriendsUi = ["UserRoamingConfigStore", "Software", "Valve", "Steam", "FriendsUI"];

    private static readonly string[] WebStorage = ["UserLocalConfigStore", "WebStorage"];

    /// <summary>
    /// The dialogs Steam puts between Play and the game on a PC (steamui's ConfiguratorInterstitials, from its launch
    /// action), each kept as Steam keeps it once its "Don't show this on future games" is ticked and OK pressed:
    /// <c>Deck_ConfiguratorInterstitialsCheckbox_&lt;name&gt;</c> "1", read only beside a JSON list in
    /// <c>Deck_ConfiguratorInterstitialApps_&lt;name&gt;</c>, with <c>Deck_ConfiguratorInterstitialsVersionSeen_&lt;name&gt;</c>
    /// at the dialog's version. GamepadRequired is "Grab your controller!" (no controller connected), GamepadRecommended
    /// "Grab a controller and kick back!", CurrentGamepadUnsupported and CurrentGamepadSteamInputOptIn what Steam does
    /// with a PlayStation controller in this game (Exo sets Steam Input per game itself); IntroToSteamInputGames shows
    /// once, with no box, and is seen. Each holds the game until OK. Unclaimed content and the VR headset notice stay:
    /// only the player can act on them.
    /// </summary>
    internal static readonly (string Name, int Version, bool Box)[] LaunchDialogs =
    [
        ("GamepadRequired", 0, true), ("GamepadRecommended", 1, true),
        ("CurrentGamepadUnsupported", 1, true), ("CurrentGamepadSteamInputOptIn", 1, true),
        ("IntroToSteamInputGames", 1, false),
    ];

    /// <summary>One value Exo's quiet writes: where, and the text it writes (as the file holds it, escapes kept).</summary>
    internal sealed record Change(string[] Path, string Key, string Value);

    /// <summary>One account's file that isn't quiet yet: what it holds now, the quiet text, and each value that text changes.</summary>
    internal sealed record Edit(string Account, bool Local, string Path, string Original, string Text, IReadOnlyList<Change> Changes);

    /// <summary>Each account's files that aren't quiet yet.</summary>
    internal static List<Edit> SteamEdits(string root)
    {
        var edits = new List<Edit>();
        foreach (var account in Accounts(root))
        {
            Add(account, false, Path.Combine(account, "7", "remote", "sharedconfig.vdf"));
            Add(account, true, Path.Combine(account, "config", "localconfig.vdf"));
        }
        return edits;

        void Add(string account, bool local, string path)
        {
            if (SteamPlaytime.ReadSharedText(path) is not { Length: > 0 } text) return;
            try
            {
                var log = new List<Change>();
                var next = local ? QuietLocal(text, log) : QuietShared(text, log);
                if (next != text) edits.Add(new Edit(Path.GetFileName(account), local, path, text, next, log));
            }
            catch (InvalidDataException) { /* a file Steam left half-written: Steam writes it whole as it next exits */ }
        }
    }

    /// <summary>
    /// Whether an account's localconfig.vdf still lets a dialog stop a game's start: Steam would hold the game at one.
    /// </summary>
    internal static bool LaunchDialogsPending(string root) =>
        Accounts(root).Any(account => Quieter(Path.Combine(account, "config", "localconfig.vdf"), text => QuietDialogs(text)) is not null);

    internal static IEnumerable<string> Accounts(string root)
    {
        try { return Directory.GetDirectories(Path.Combine(root, "userdata")).Where(dir => Path.GetFileName(dir).All(char.IsAsciiDigit)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The file's quieter text; null when it is already quiet, missing or half-written.</summary>
    private static string? Quieter(string path, Func<string, string> quiet)
    {
        if (SteamPlaytime.ReadSharedText(path) is not { Length: > 0 } text) return null;
        try
        {
            var next = quiet(text);
            return next != text ? next : null;
        }
        catch (InvalidDataException) { return null; /* a file Steam left half-written: Steam writes it whole as it next exits */ }
    }

    private static string Set(string text, string[] path, string key, string value, List<Change>? log)
    {
        log?.Add(new Change(path, key, value));
        return SteamOverlayConfig.SetValue(text, path, key, value);
    }

    internal static string QuietShared(string text, List<Change>? log = null)
    {
        text = Apply(text, Shared, log);
        if (SteamKeyValues.ParseText(text).Section(string.Join('/', FriendsUi)).Value("FriendsUIJSON") is { Length: > 0 } json &&
            QuietFriendsJson(json) is { } quiet)
            text = Set(text, FriendsUi, "FriendsUIJSON", quiet.Replace("\\", "\\\\").Replace("\"", "\\\""), log);
        return text;
    }

    internal static string QuietLocal(string text, List<Change>? log = null) => QuietDialogs(Apply(text, Local, log), log);

    /// <summary>The file with every <see cref="LaunchDialogs"/> entry off; a list of games Steam already keeps stays.</summary>
    internal static string QuietDialogs(string text, List<Change>? log = null)
    {
        var storage = SteamKeyValues.ParseText(text).Section(string.Join('/', WebStorage));
        foreach (var (name, version, box) in LaunchDialogs)
        {
            var seen = "Deck_ConfiguratorInterstitialsVersionSeen_" + name;
            if (!int.TryParse(storage.Value(seen), NumberStyles.None, CultureInfo.InvariantCulture, out var was) || was < version)
                text = Set(text, WebStorage, seen, version.ToString(CultureInfo.InvariantCulture), log);
            if (!box) continue;
            var apps = "Deck_ConfiguratorInterstitialApps_" + name;
            if (!IsJsonList(storage.Value(apps))) text = Set(text, WebStorage, apps, "[]", log);
            var checkbox = "Deck_ConfiguratorInterstitialsCheckbox_" + name;
            if (storage.Value(checkbox) != "1") text = Set(text, WebStorage, checkbox, "1", log);
        }
        return text;
    }

    private static bool IsJsonList(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try { return JsonNode.Parse(value) is JsonArray; }
        catch (JsonException) { return false; }
    }

    private static string Apply(string text, (string[] Path, (string Key, string Value)[] Values)[] sections, List<Change>? log)
    {
        var parsed = SteamKeyValues.ParseText(text);
        foreach (var (path, values) in sections)
        {
            var section = parsed.Section(string.Join('/', path));
            foreach (var (key, value) in values)
                if (section.Value(key) != value) text = Set(text, path, key, value, log);
        }
        return text;
    }

    /// <summary>The friends list's settings with every notification and sound off and no taskbar flashing; null when they already are.</summary>
    internal static string? QuietFriendsJson(string json)
    {
        JsonObject settings;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed) return null;
            settings = parsed;
        }
        catch (JsonException) { return null; }
        var changed = false;
        foreach (var key in settings.Select(entry => entry.Key).ToList())
        {
            if (!key.StartsWith("bNotifications_", StringComparison.Ordinal) && !key.StartsWith("bSounds_", StringComparison.Ordinal)) continue;
            if (settings[key] is JsonValue value && value.TryGetValue<bool>(out var on) && !on) continue;
            settings[key] = false;
            changed = true;
        }
        if (settings["nChatFlashMode"] is not JsonValue mode || !mode.TryGetValue<int>(out var flash) || flash != 2)
        {
            settings["nChatFlashMode"] = 2;
            changed = true;
        }
        return changed ? settings.ToJsonString() : null;
    }
}

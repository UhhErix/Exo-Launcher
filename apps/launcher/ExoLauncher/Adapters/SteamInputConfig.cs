namespace ExoLauncher.Adapters;

/// <summary>
/// Steam Input, per game, where Steam keeps it: the account's localconfig.vdf › UserLocalConfigStore › apps › &lt;app&gt; ›
/// <c>UseSteamControllerConfig</c> ("0" off, "1" or absent Steam's default, "2" on). Off wins over Steam's global setting
/// and over a developer's opt-in; app 413080 is Steam's desktop layout (the controller as mouse and keyboard outside
/// Steam games). Steam rewrites the file as it exits, so it is written with the overlay switch, while Steam is closed.
///
/// Exo decides it per game from the game's store entry (<see cref="Desired"/>), with no choice in Properties: off where
/// the game reads every controller itself, on where it needs Steam Input to be played with one, Steam's own default
/// where only some controllers reach the game by themselves; the desktop layout is off. Exo records each value it
/// replaced (none: the line wasn't there) and puts it back exactly when it no longer owns the key, the line taken out
/// again where there was none; a value changed in Steam after Exo set it is the player's from then on
/// (<see cref="Players"/>), so Exo never fights them. Covered by SteamInputConfigTests.
/// </summary>
internal static class SteamInputConfig
{
    internal const string Key = "UseSteamControllerConfig";
    internal const string DesktopLayout = "413080";
    /// <summary>Steam's store category for "PlayStation controller support" over USB (58 is over Bluetooth).</summary>
    internal const string PlayStationCategory = "category_57";
    /// <summary>A record entry for a game whose Steam Input the player changed in Steam: Exo never writes it again.</summary>
    internal const string Players = "!";

    internal static string? Read(SteamKeyValues apps, string appId) => apps.Section(appId).Value(Key);

    /// <summary>Whether a game's appinfo lists PlayStation controller support.</summary>
    internal static bool ReadsPlayStation(SteamKeyValues app) => app.Section("common").Section("category").Value(PlayStationCategory) is not null;

    /// <summary>
    /// A game, not a tool or an app: Steam Input on for something that runs beside your games (Wallpaper Engine) would
    /// put its layout on the controller in every game.
    /// </summary>
    internal static bool IsGame(SteamKeyValues app) => string.Equals(app.Section("common").Value("type"), "game", StringComparison.OrdinalIgnoreCase);

    /// <summary>Controller support as Steam lists it: "full", "partial" or "none" (appinfo's own word, else categories 28 and 18).</summary>
    internal static string Support(SteamKeyValues app)
    {
        var common = app.Section("common");
        if (common.Value("controller_support") is { } said && (said.Equals("full", StringComparison.OrdinalIgnoreCase) || said.Equals("partial", StringComparison.OrdinalIgnoreCase)))
            return said.ToLowerInvariant();
        var categories = common.Section("category");
        return categories.Value("category_28") is not null ? "full" : categories.Value("category_18") is not null ? "partial" : "none";
    }

    /// <summary>
    /// Exo's Steam Input for a game: "0" (off) with full controller support and PlayStation support listed, so the game
    /// reads an Xbox or a PlayStation controller itself; "2" (on) with partial or no controller support, so Steam Input
    /// maps the controller. Null (Steam's own default) with full support for Xbox controllers only: Steam's default leaves
    /// an Xbox controller to the game and maps a PlayStation controller, which the game can't read itself, where off
    /// would leave a DualSense doing nothing. Null too for what isn't a game.
    /// </summary>
    internal static string? Desired(SteamKeyValues app) =>
        !IsGame(app) ? null : Support(app) != "full" ? "2" : ReadsPlayStation(app) ? "0" : null;

    /// <summary>
    /// Whether Steam Input stands between the controller and the game with this value: on, or Steam's default with a
    /// PlayStation controller on this PC in a game that doesn't read one (asked only then).
    /// </summary>
    internal static bool InUse(string? value, SteamKeyValues app, Func<bool> playStationPad) =>
        value == "2" || (value is null or "1" && IsGame(app) && !ReadsPlayStation(app) && playStationPad());

    /// <summary>
    /// The values to write and Exo's record once they land. <paramref name="rule"/>: each installed game's
    /// <see cref="Desired"/> value. <paramref name="choices"/>: "steam" for Exo's own Steam shortcuts (games that aren't
    /// Steam's, sent through Steam Input); it comes back without the ones whose value the player changed in Steam.
    /// </summary>
    internal static (Dictionary<string, string> Writes, Dictionary<string, string> Record, Dictionary<string, string> Choices) Plan(
        SteamKeyValues apps,
        IReadOnlyDictionary<string, string?> rule,
        IReadOnlyDictionary<string, string> choices,
        IReadOnlyDictionary<string, string> record)
    {
        var writes = new Dictionary<string, string>(StringComparer.Ordinal);
        var nextRecord = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nextChoices = new Dictionary<string, string>(choices, StringComparer.OrdinalIgnoreCase);
        var keys = record.Keys.Concat(choices.Keys).Concat(rule.Keys).Append(DesktopLayout).Distinct(StringComparer.Ordinal);
        foreach (var appId in keys)
        {
            if (appId.Length is 0 or > 10 || !appId.All(char.IsAsciiDigit)) continue;
            // An empty prior is a line that wasn't there; Steam reads that as 1.
            var current = Read(apps, appId);
            var entry = record.GetValueOrDefault(appId);
            var owned = entry?.Split('>') is [var p, var s] ? (Prior: p, Set: s) : ((string Prior, string Set)?)null;
            // Changed in Steam since Exo set it: the player's from now on, and Exo's pick for it is dropped.
            if (entry == Players || owned is { } o && (current ?? "1") != o.Set)
            {
                nextRecord[appId] = Players;
                nextChoices.Remove(appId);
                continue;
            }
            var desired = nextChoices.GetValueOrDefault(appId) == "steam" ? "2" : appId == DesktopLayout ? "0" : rule.GetValueOrDefault(appId);
            if (desired is null)
            {
                // Exo no longer owns it: its value goes back to what it was.
                if (owned is { } given && current != (given.Prior.Length == 0 ? null : given.Prior)) writes[appId] = given.Prior;
                continue;
            }
            var prior = owned?.Prior ?? current ?? "";
            if ((current ?? "1") != desired) writes[appId] = desired;
            if (desired != (prior.Length == 0 ? "1" : prior)) nextRecord[appId] = $"{prior}>{desired}";
        }
        return (writes, nextRecord, nextChoices);
    }

    /// <summary>The file with each game's Steam Input value set (an empty value takes the line out); nothing else changes.</summary>
    internal static string Write(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (appId, value) in values)
        {
            if (appId.Length is 0 or > 10 || !appId.All(char.IsAsciiDigit)) throw new ArgumentException("Not a Steam app id.", nameof(values));
            if (value is not ("" or "0" or "1" or "2")) throw new ArgumentException("Steam Input is 0, 1 or 2.", nameof(values));
            string[] path = ["UserLocalConfigStore", "apps", appId];
            text = value.Length == 0 ? SteamOverlayConfig.RemoveValue(text, path, Key) : SteamOverlayConfig.SetValue(text, path, Key, value);
        }
        return text;
    }
}

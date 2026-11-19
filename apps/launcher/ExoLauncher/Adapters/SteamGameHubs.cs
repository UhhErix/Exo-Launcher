using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// One Steam app that holds other games. Steam has one Call of Duty app, "Call of Duty®" (1938090, the Call of Duty HQ
/// folder, cod.exe), however many of the games you buy: each is a paid DLC of it. Steam's owned-games answer and the app's
/// manifest name only the app, so the library's rule that DLC is never a row hid a bought Modern Warfare 4 behind the name
/// "Call of Duty®". It stays one card, since it is one install, one launch and one set of hours: it takes the name and the
/// art of the newest game the account holds, and says which others it holds, so each is found by its own name and the
/// Store's page for any of them leads here. Nothing but the name, the art and that list changes.
/// </summary>
internal static class SteamGameHubs
{
    internal const string CallOfDuty = "1938090";

    /// <summary>
    /// Newest first, each by the app its Steam store page sells: Steam's own proof the game was bought. The packs beside it
    /// (Multiplayer, DMZ) aren't read, since a free trial may carry them. Add each new game when it goes on sale.
    /// </summary>
    private static readonly IncludedGame[] CallOfDutyGames =
    [
        new("4435490", "Call of Duty®: Modern Warfare® 4"),
        new("3606480", "Call of Duty®: Black Ops 7"),
        new("2933620", "Call of Duty®: Black Ops 6"),
        new("2519060", "Call of Duty®: Modern Warfare® III"),
        new("1962660", "Call of Duty®: Modern Warfare® II"),
    ];

    /// <summary>What the Steam client is asked about along with the owned-games candidates.</summary>
    internal static IEnumerable<string> GameAppIds => CallOfDutyGames.Select(game => game.AppId);

    internal static bool IsHeldGame(string appId) => CallOfDutyGames.Any(game => game.AppId == appId);

    /// <summary>
    /// The list for a card, newest game first (it names the card and lends it its art), then the app itself under Steam's
    /// name; null for any other app, or when the account holds none of its games. <paramref name="held"/> is Steam's answer
    /// about their licences; with Steam closed it is null and an installed app's manifest speaks instead: a DLC with depots
    /// installed, or one switched off in its Properties, which Steam offers only for what the account holds.
    /// </summary>
    internal static IReadOnlyList<IncludedGame>? Includes(string appId, string steamName, IReadOnlySet<string>? held, string? manifest)
    {
        if (appId != CallOfDuty) return null;
        IReadOnlySet<string>? onDisk = null;
        if (held is null && manifest is not null)
        {
            var config = SteamGameProperties.ReadManifest(manifest);
            onDisk = config.DisabledDlc.Concat(config.InstalledDlc).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        }
        var games = CallOfDutyGames.Where(game => (held ?? onDisk)?.Contains(game.AppId) == true).ToList();
        if (games.Count == 0) return null;
        games.Add(new IncludedGame(appId, steamName));
        return games;
    }
}

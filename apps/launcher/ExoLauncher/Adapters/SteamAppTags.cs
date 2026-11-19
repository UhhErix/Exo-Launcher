using System.Collections.Concurrent;

namespace ExoLauncher.Adapters;

/// <summary>
/// A Steam app's genres and modes, as the Library's search and Filter menu word them (ui/src/lib/librarySearch.ts ›
/// LIBRARY_TAGS): from appinfo's <c>common/genres</c> (Steam's genre ids) and <c>common/category</c> (its feature ids).
/// Steam's ids have not changed in years; one Exo doesn't know is left out, never guessed.
/// </summary>
internal static class SteamAppTags
{
    private static readonly Dictionary<int, string> GenreWords = new()
    {
        [1] = "action", [2] = "strategy", [3] = "rpg", [4] = "casual", [9] = "racing", [18] = "sports", [23] = "indie",
        [25] = "adventure", [28] = "simulation", [29] = "mmo", [37] = "free to play", [70] = "early access",
    };

    /// <summary>Steam's categories: multi-player, single-player, co-op, MMO, shared or split screen, cross-platform
    /// multiplayer, full and partial controller support, online PvP, split-screen PvP, online co-op, split-screen co-op,
    /// LAN PvP, LAN co-op and PvP.</summary>
    private static readonly Dictionary<int, string[]> CategoryWords = new()
    {
        [1] = ["multiplayer"], [2] = ["single-player"], [9] = ["co-op"], [20] = ["mmo", "multiplayer"],
        [24] = ["local co-op"], [27] = ["multiplayer"], [28] = ["controller"], [18] = ["controller"],
        [36] = ["multiplayer"], [37] = ["multiplayer"], [38] = ["co-op"], [39] = ["co-op", "local co-op"],
        [47] = ["multiplayer"], [48] = ["co-op"], [49] = ["multiplayer"],
    };

    /// <summary>The page's order (genres, then modes), so the same app always gives the same string.</summary>
    private static readonly string[] Order =
    [
        "action", "adventure", "rpg", "strategy", "simulation", "racing", "sports", "casual", "indie", "mmo",
        "free to play", "early access", "single-player", "multiplayer", "co-op", "local co-op", "controller",
    ];

    // Tens of thousands of apps share a few hundred distinct sets: one string each.
    private static readonly ConcurrentDictionary<string, string> Interned = new(StringComparer.Ordinal);

    /// <summary>The words for these ids, comma-separated in the page's order; "" when none is known.</summary>
    internal static string Words(IReadOnlyCollection<int>? genres, IReadOnlyCollection<int>? categories)
    {
        if (genres is not { Count: > 0 } && categories is not { Count: > 0 }) return "";
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in genres ?? [])
            if (GenreWords.TryGetValue(id, out var word)) words.Add(word);
        foreach (var id in categories ?? [])
            if (CategoryWords.TryGetValue(id, out var some)) words.UnionWith(some);
        if (words.Count == 0) return "";
        var joined = string.Join(',', Order.Where(words.Contains));
        return Interned.Count < 4096 ? Interned.GetOrAdd(joined, joined) : joined;
    }

    internal static string[] Split(string tags) =>
        string.IsNullOrEmpty(tags) ? [] : tags.Split(',');

    /// <summary>A library card's tags: its Steam app's, or none for a game from another store.</summary>
    internal static string[] For(Models.GameEntry game) =>
        game.Store == Models.StoreKind.Steam && game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)
            ? SteamAppInfoNames.TagsOf(game.Id["steam:".Length..])
            : [];
}

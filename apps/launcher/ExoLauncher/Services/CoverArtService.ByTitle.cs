using System.Collections.Concurrent;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public static partial class CoverArtService
{
    /// <summary>Each name asked this session, by its title key: the one lookup, shared by every card that asks.</summary>
    private static readonly ConcurrentDictionary<string, Task<string?>> TitleWideArt = new(StringComparer.Ordinal);

    /// <summary>
    /// Wide art for a game known only by its name (Tonight: a game friends are in that isn't in the library), kept in
    /// the cover cache under that name: the file when it is there, else Steam's (the name resolved to an app id the way
    /// a library game's is, which the title map keeps on disk), else Epic's catalog; the Microsoft Store's for the games
    /// only it has art for. Only the name leaves this PC. Null when none is found; a miss is asked again next session.
    /// </summary>
    public static Task<string?> WideArtByTitleAsync(string title, CancellationToken cancellationToken)
    {
        title = title.Trim();
        var key = NormalizeTitleKey(title);
        if (key.Length < 2 || title.Length > 200) return Task.FromResult<string?>(null);
        // Epic as the store, so a name Steam doesn't sell still gets Epic's catalog (Riot's titles are there too).
        var game = new GameEntry { Id = "title:" + key, Title = title, Store = StoreOfTitle(title), Owned = false };
        if (ResolveWideArtUrl(game) is { } cached) return Task.FromResult<string?>(cached);
        return TitleWideArt.GetOrAdd(key, _ => FetchWideArtByTitleAsync(game)).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The store whose art a name gets: Minecraft and Roblox are on neither Steam nor Epic (MicrosoftStoreArt has theirs).
    /// Any name with the word minecraft in it is Minecraft, as a modpack's or launcher's is ("Medieval Minecraft [FABRIC]"),
    /// but for Minecraft's spin-offs, games of their own that Steam sells.
    /// </summary>
    internal static StoreKind StoreOfTitle(string title)
    {
        var key = NormalizeTitleKey(title);
        if (key == "roblox") return StoreKind.Roblox;
        if (!key.Split(' ').Contains("minecraft")) return StoreKind.Epic;
        return key.StartsWith("minecraft dungeons", StringComparison.Ordinal)
            || key.StartsWith("minecraft legends", StringComparison.Ordinal)
            || key.StartsWith("minecraft story mode", StringComparison.Ordinal)
            ? StoreKind.Epic : StoreKind.Minecraft;
    }

    private static async Task<string?> FetchWideArtByTitleAsync(GameEntry game)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            // Binds the name's app id to this entry, which WarmWideArtAsync then reads back (MappedSteamAppId).
            // A Steam title search for these binds junk.
            if (game.Store == StoreKind.Epic)
                _ = MappedSteamAppId(game) ?? await ResolveSteamAppIdByTitleAsync(CoverHttp, game, limit.Token).ConfigureAwait(false);
            if (!await WarmWideArtAsync(CoverHttp, game, requested: true, limit.Token).ConfigureAwait(false)) return null;
            NotifyOwnedArtworkWrite();
            return ResolveWideArtUrl(game);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"Wide art by title failed for '{game.Title}': {ex.Message}");
            return null;
        }
    }
}

using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// What Exo knows about a game for Fluid Motion Frames (<see cref="FluidMotionFrames"/>), gathered when the game's page
/// asks (gpu.game) and as it launches: Steam's tags (a game from another store found on Steam by its title, a big one
/// off Steam from <see cref="StoreService.OffSteamTagsFor"/>) and how this PC's graphics card meets its Steam
/// requirements, kept 30 days in frame-gen-games.json (7 when Steam had nothing); PCGamingWiki's page
/// (<see cref="GameWiki"/>); and whether its install ships a frame generation library (the upscaler scan).
/// </summary>
internal static class FrameGenFacts
{
    private sealed record SteamFacts(IReadOnlyList<int> Tags, string? Graphics, DateTimeOffset At);

    /// <summary>The upscaler scan's frame generation libraries: DLSS's, FSR's and XeSS's.</summary>
    private static readonly HashSet<string> FrameGenLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvngx_dlssg.dll", "amd_fidelityfx_framegeneration_dx12.dll", "libxess_fg.dll",
    };

    /// <summary>How long a launch waits for a game not looked up yet; past it, the launch goes with what is known.</summary>
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(2);

    private static readonly DlssSwapService Upscalers = new();
    private static readonly object Gate = new();
    private static Dictionary<string, SteamFacts>? _cache;

    private static string FilePath => Path.Combine(PathHelper.AppDataDir, "frame-gen-games.json");

    /// <summary>Everything about the game, asking Steam and the wiki for what isn't kept; a part that fails is left out.</summary>
    internal static async Task<FluidMotionFrames.Game> GatherAsync(GameEntry game, CancellationToken ct)
    {
        var steam = Quietly(() => SteamAsync(game, ct));
        var wiki = Quietly(() => GameWiki.FactsAsync(game, ct));
        var files = Quietly(() => Task.Run(() => (bool?)HasFrameGenFiles(game, ct), ct));
        await Task.WhenAll(steam, wiki, files).ConfigureAwait(false);
        return new FluidMotionFrames.Game(game.Title)
        {
            Tags = (await steam.ConfigureAwait(false))?.Tags ?? [],
            Graphics = (await steam.ConfigureAwait(false))?.Graphics,
            Wiki = await wiki.ConfigureAwait(false),
            FrameGenFiles = await files.ConfigureAwait(false) == true,
        };
    }

    /// <summary>
    /// <see cref="GatherAsync"/> for at most <paramref name="wait"/>, which only a game not looked up yet waits for; past
    /// it, what is kept, while the rest arrives for next time.
    /// </summary>
    internal static async Task<FluidMotionFrames.Game> WithinAsync(GameEntry game, TimeSpan wait, CancellationToken ct)
    {
        var gathering = GatherAsync(game, CancellationToken.None);
        if (await Task.WhenAny(gathering, Task.Delay(wait, ct)).ConfigureAwait(false) == gathering) return await gathering.ConfigureAwait(false);
        AppLog.Info($"Fluid Motion Frames for {game.Title}: still looking the game up, so it goes with what is known.");
        return new FluidMotionFrames.Game(game.Title)
        {
            Tags = Kept(game.Id)?.Tags ?? StoreService.OffSteamTagsFor(game.Title) ?? [],
            Graphics = Kept(game.Id)?.Graphics,
            Wiki = GameWiki.CachedFacts(game.Id),
        };
    }

    /// <summary>For a launch, on the launch's own thread before the game starts: <see cref="WithinAsync"/> for two seconds.</summary>
    internal static FluidMotionFrames.Game ForLaunch(GameEntry game) =>
        WithinAsync(game, LaunchWait, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<T?> Quietly<T>(Func<Task<T?>> work)
    {
        try { return await work().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Debug("Fluid Motion Frames facts: " + ex.GetType().Name);
            return default;
        }
    }

    /// <summary>Steam's tags and the requirements' verdict for this PC's graphics card: kept while fresh, else asked for.</summary>
    private static async Task<SteamFacts?> SteamAsync(GameEntry game, CancellationToken ct)
    {
        if (Kept(game.Id) is { } kept && DateTimeOffset.UtcNow - kept.At < TimeSpan.FromDays(kept.Tags.Count == 0 ? 7 : 30)) return kept;
        if (StoreService.OffSteamTagsFor(game.Title) is { } off) return new SteamFacts(off, null, DateTimeOffset.UtcNow);
        try
        {
            var store = App.Services.Store;
            var appId = CoverArtService.SteamAppId(game) ?? await store.SteamIdForTitleAsync(game.Title, ct).ConfigureAwait(false);
            SteamFacts facts;
            if (appId is null) facts = new SteamFacts([], null, DateTimeOffset.UtcNow);
            else
            {
                // The page reads the card facts too, so the tags after it come from the cache.
                var detail = await store.GameAsync(appId, ct).ConfigureAwait(false);
                var items = await store.ItemsAsync([appId], ct, staleOk: true).ConfigureAwait(false);
                var graphics = detail is null ? null : FluidMotionFrames.Graphics(detail.Specs, SystemSpecs.Read());
                facts = new SteamFacts(items.TryGetValue(appId, out var item) ? item.Tags.Take(20).ToList() : [], graphics, DateTimeOffset.UtcNow);
            }
            Remember(game.Id, facts);
            return facts;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or IOException)
        {
            AppLog.Debug("Fluid Motion Frames facts from Steam: " + ex.GetType().Name);
            return Kept(game.Id);
        }
    }

    private static bool HasFrameGenFiles(GameEntry game, CancellationToken ct) =>
        Upscalers.GetStatus(game, refresh: false, consented: null, ct).Rows.Any(row => FrameGenLibraries.Contains(row.Dll.FileName));

    private static SteamFacts? Kept(string gameId)
    {
        lock (Gate) return Load().GetValueOrDefault(gameId);
    }

    private static void Remember(string gameId, SteamFacts facts)
    {
        lock (Gate)
        {
            Load()[gameId] = facts;
            try
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_cache));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug("Fluid Motion Frames facts not saved: " + ex.GetType().Name);
            }
        }
    }

    private static Dictionary<string, SteamFacts> Load()
    {
        if (_cache is not null) return _cache;
        _cache = new Dictionary<string, SteamFacts>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length < 4 * 1024 * 1024 &&
                JsonSerializer.Deserialize<Dictionary<string, SteamFacts>>(File.ReadAllText(FilePath)) is { } read)
                foreach (var pair in read) _cache[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Debug("Fluid Motion Frames facts unreadable: " + ex.GetType().Name);
        }
        return _cache;
    }
}

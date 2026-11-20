using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services.Achievements;

/// <summary>
/// An achievement just unlocked in a game that is running: what it is, how rare, how far along it puts you (Total 0 when
/// unknown), whether it finished the game. By the time
/// <see cref="AchievementWatch.Unlocked"/> raises it, Icon is its picture as a data: address.
/// </summary>
internal sealed record Unlock(string GameId, string Game, string ApiName, string Name, string Description, string? Icon,
    double? Percent, string Rarity, int Done, int Total, bool Completed, DateTimeOffset At);

/// <summary>
/// Achievements as they unlock, while a Steam game runs: Steam writes the game's stats file (appcache/stats,
/// <see cref="SteamStatsCache"/>) the moment one unlocks, so a watcher on that one file sees it within a second, with no
/// network and nothing touching the game. At the game's start the file is read once as the baseline and the game's
/// global unlock percentages are fetched once from Steam's public stats (no key, the app id alone), for rarity; each
/// write after that is read again and what is new becomes an <see cref="Unlock"/> (<see cref="Unlocked"/>). Stops with
/// the game. Epic and GOG write no local unlock file Exo can read (.claude research: achievements-overlay.md), so a game
/// from either is followed through its library provider instead: the account's achievements are read when the game
/// starts (the baseline) and again every <see cref="PollEvery"/> while it runs, never otherwise, and what turned
/// unlocked in between is an <see cref="Unlock"/> the same way. Either way, a game's start also keeps the icons of what it
/// could still unlock (<see cref="AchievementIconCache.FillAsync"/>), so a banner never waits on the network.
/// </summary>
internal static class AchievementWatch
{
    internal static event Action<Unlock>? Unlocked;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Watch> Watching = new(StringComparer.Ordinal);
    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromSeconds(10));
    private static readonly Dictionary<string, CancellationTokenSource> Polling = new(StringComparer.Ordinal);
    private static readonly IAchievementProvider Epic = new EpicLegendaryAchievementProvider(), Gog = new GogGameplayAchievementProvider();
    private static readonly SemaphoreSlim Raising = new(1, 1);

    /// <summary>
    /// Between reads of a running game's achievements: Epic's starts Legendary each time (a cold start of a packed Python
    /// tool, at idle priority behind the game), so it waits 90 s; GOG's is one request.
    /// </summary>
    internal static TimeSpan PollEvery(StoreKind store) => store == StoreKind.Epic ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30);

    private sealed class Watch : IDisposable
    {
        internal required GameEntry Game { get; init; }
        internal required string AppId { get; init; }
        internal required string StatsPath { get; init; }
        internal required Dictionary<(int Stat, int Bit), SteamStatsCache.Entry> Schema { get; init; }
        internal Dictionary<string, DateTimeOffset> Known { get; set; } = new(StringComparer.Ordinal);
        internal Dictionary<string, double> Percents { get; set; } = new(StringComparer.Ordinal);
        internal FileSystemWatcher? Watcher { get; set; }
        internal Timer? Settle { get; set; }
        internal CancellationTokenSource Icons { get; } = new();
        public void Dispose() { Icons.Cancel(); Icons.Dispose(); Watcher?.Dispose(); Settle?.Dispose(); }
    }

    internal static void Start() => App.Services.Launcher.GameRunStateChanged += (gameId, state) =>
    {
        if (state.IsRunning) { if (App.Services.Library.Find(gameId) is { } game) _ = Task.Run(() => BeginAsync(game)); }
        else Stop(gameId);
    };

    private static async Task BeginAsync(GameEntry game)
    {
        try
        {
            if (game.Store is StoreKind.Epic or StoreKind.Gog) { await PollAsync(game).ConfigureAwait(false); return; }
            if (game.Store != StoreKind.Steam || SteamLibraryCacheAchievementProvider.SourceGameId(game) is not { } appId) return;
            if (SteamInstallation.ResolveRoot() is not { } root || SteamClientReady.ReadActiveUser() is var account && account == 0) return;
            var folder = Path.Combine(root, "appcache", "stats");
            var schemaPath = Path.Combine(folder, $"UserGameStatsSchema_{appId}.bin");
            if (!File.Exists(schemaPath)) return;
            var language = SteamStatsCache.Language(System.Globalization.CultureInfo.CurrentUICulture.Name);
            if (SteamStatsCache.Schema(await File.ReadAllBytesAsync(schemaPath).ConfigureAwait(false), appId, language) is not { Count: > 0 } schema) return;
            var watch = new Watch { Game = game, AppId = appId, StatsPath = Path.Combine(folder, $"UserGameStats_{account}_{appId}.bin"), Schema = schema };
            watch.Known = Read(watch) ?? new(StringComparer.Ordinal);
            watch.Percents = await PercentsAsync(appId).ConfigureAwait(false);
            watch.Settle = new Timer(_ => _ = CheckAsync(game.Id), null, Timeout.Infinite, Timeout.Infinite);
            watch.Watcher = new FileSystemWatcher(folder, Path.GetFileName(watch.StatsPath)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            watch.Watcher.Changed += (_, _) => watch.Settle?.Change(300, Timeout.Infinite);
            watch.Watcher.Created += (_, _) => watch.Settle?.Change(300, Timeout.Infinite);
            watch.Watcher.Renamed += (_, _) => watch.Settle?.Change(300, Timeout.Infinite);
            watch.Watcher.EnableRaisingEvents = true;
            lock (Gate)
            {
                if (Watching.Remove(game.Id, out var old)) old.Dispose();
                Watching[game.Id] = watch;
            }
            AppLog.Info($"Achievements: watching {game.Title} ({watch.Known.Count} of {schema.Count} unlocked).");
            await AchievementIconCache.FillAsync(schema.Values.Where(e => !watch.Known.ContainsKey(e.ApiName)).Select(e => e.Icon), Http, watch.Icons.Token).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { /* The game stopped while its icons were being kept. */ }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Warn("Achievements: not watched: " + ex.Message);
        }
    }

    private static void Stop(string gameId)
    {
        lock (Gate)
        {
            if (Watching.Remove(gameId, out var watch)) watch.Dispose();
            if (Polling.Remove(gameId, out var poll)) { poll.Cancel(); poll.Dispose(); }
        }
    }

    /// <summary>
    /// An Epic or GOG game while it runs: its achievements now (the baseline), then again every <see cref="PollEvery"/>;
    /// each read's new unlocks (<see cref="NewlyUnlocked"/>) become banners. Ends when the game stops.
    /// </summary>
    private static async Task PollAsync(GameEntry game)
    {
        var provider = game.Store == StoreKind.Epic ? Epic : Gog;
        if (!provider.Supports(game) || provider.GetCurrentCoverageKey(game) is null) return;
        var stop = new CancellationTokenSource();
        lock (Gate)
        {
            if (Polling.Remove(game.Id, out var old)) { old.Cancel(); old.Dispose(); }
            Polling[game.Id] = stop;
        }
        var token = stop.Token;
        AchievementSnapshot? known = null;
        try
        {
            using var every = new PeriodicTimer(PollEvery(game.Store));
            do
            {
                // A stop that came before this watch was registered would leave it reading forever.
                if (!App.Services.Launcher.GetGameRunState(game).IsRunning) break;
                var now = await provider.GetSnapshotAsync(game, token).ConfigureAwait(false);
                if (now.Coverage is not (AchievementCoverageStatus.Complete or AchievementCoverageStatus.Partial)) continue;
                if (known is null)
                {
                    AppLog.Info($"Achievements: following {game.Title} on {provider.Id} ({now.ReportedUnlocked} of {now.ReportedTotal} unlocked).");
                    _ = AchievementIconCache.FillAsync(now.Entries.Where(e => !e.State.Unlocked).Select(e => e.Definition.IconUnlockedUrl), Http, token);
                }
                var total = now.ReportedTotal ?? now.Entries.Count;
                var unlocked = now.ReportedUnlocked ?? now.Entries.Count(e => e.State.Unlocked);
                var fresh = NewlyUnlocked(known, now);
                var done = unlocked - fresh.Count;
                foreach (var entry in fresh)
                {
                    var d = entry.Definition;
                    await RaiseAsync(new Unlock(game.Id, game.Title, d.ExternalId, d.Name, d.Description, d.IconUnlockedUrl, d.GlobalUnlockPercent,
                        Rarity(d.GlobalUnlockPercent), ++done, total, entry == fresh[^1] && total > 0 && unlocked >= total, entry.State.UnlockedAtUtc ?? DateTimeOffset.UtcNow), game).ConfigureAwait(false);
                }
                known = now;
            }
            while (await every.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (Gate) if (Polling.TryGetValue(game.Id, out var current) && current == stop) Polling.Remove(game.Id);
            stop.Dispose();
        }
    }

    /// <summary>
    /// What turned unlocked between two reads of one account's achievements: only rows the earlier read had as locked
    /// (a row missing from it, as in a partial catalog, is unknown, not new), oldest first. Nothing on the first read,
    /// an unusable read, or when the account changed between them.
    /// </summary>
    internal static IReadOnlyList<AchievementEntry> NewlyUnlocked(AchievementSnapshot? before, AchievementSnapshot now)
    {
        static bool Usable(AchievementSnapshot s) => s.Coverage is AchievementCoverageStatus.Complete or AchievementCoverageStatus.Partial;
        if (before is null || !Usable(before) || !Usable(now) || !string.Equals(before.CoverageKey, now.CoverageKey, StringComparison.Ordinal)) return [];
        var locked = before.Entries.Where(e => !e.State.Unlocked).Select(e => e.Definition.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return now.Entries.Where(e => e.State.Unlocked && locked.Contains(e.Definition.ExternalId))
            .OrderBy(e => e.State.UnlockedAtUtc ?? DateTimeOffset.MaxValue).ThenBy(e => e.Definition.ExternalId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Out to the banners, in order, with its picture in hand.</summary>
    private static async Task RaiseAsync(Unlock unlock, GameEntry game)
    {
        AppLog.Info($"Achievements: {unlock.Game} unlocked {unlock.Name} ({unlock.Rarity}{(unlock.Completed ? ", completed the game" : "")}).");
        await Raising.WaitAsync().ConfigureAwait(false);
        try { Unlocked?.Invoke(unlock with { Icon = await PictureAsync(unlock.Icon, game).ConfigureAwait(false) }); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { AppLog.Warn("Achievements: banner not shown: " + ex.Message); }
        finally { Raising.Release(); }
    }

    /// <summary>
    /// The picture a banner always has: the achievement's icon (kept since the game started; if it was missed, asked for
    /// once, for at most 2 s), else the game's own art, else the one that ships.
    /// </summary>
    private static async Task<string?> PictureAsync(string? icon, GameEntry game)
    {
        using var soon = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        if (await AchievementIconCache.PictureAsync(icon, Http, soon.Token).ConfigureAwait(false) is { } picture) return picture;
        if (CoverArtService.TryResolveLocalFile(CoverArtService.WithCover(game).CoverUrl) is { } art
            && await CoverArtService.EncodePosterAsync(art, 256, 256, 0.88f).ConfigureAwait(false) is { } square
            && AchievementIconCache.DataUri(square) is { } poster) return poster;
        return AchievementIconCache.Bundled();
    }

    /// <summary>The file's unlocks now; null while Steam is mid-write (read again on its next write).</summary>
    private static Dictionary<string, DateTimeOffset>? Read(Watch watch)
    {
        try { return File.Exists(watch.StatsPath) ? SteamStatsCache.Unlocked(File.ReadAllBytes(watch.StatsPath), watch.Schema) : new(StringComparer.Ordinal); }
        catch (IOException) { return null; }
    }

    private static async Task CheckAsync(string gameId)
    {
        Watch? watch;
        lock (Gate) Watching.TryGetValue(gameId, out watch);
        if (watch is null || Read(watch) is not { } now) return;
        var fresh = now.Where(pair => !watch.Known.ContainsKey(pair.Key)).OrderBy(pair => pair.Value).ToList();
        watch.Known = now;
        var completed = now.Count >= watch.Schema.Count;
        var done = now.Count - fresh.Count;
        foreach (var (api, at) in fresh)
        {
            var entry = watch.Schema.Values.First(e => e.ApiName == api);
            double? percent = watch.Percents.TryGetValue(api, out var p) ? p : null;
            await RaiseAsync(new Unlock(watch.Game.Id, watch.Game.Title, api, entry.Name, entry.Description, entry.Icon, percent,
                Rarity(percent), ++done, watch.Schema.Count, completed && api == fresh[^1].Key, at), watch.Game).ConfigureAwait(false);
        }
    }

    /// <summary>What banner.html shows for an unlock, as JSON: the in-game overlay and the OBS browser source post the same. Only a data: picture passes, the one kind its CSP allows.</summary>
    internal static string Card(Unlock unlock, int volumePercent, bool reduceMotion) => JsonSerializer.Serialize(new
    {
        game = unlock.Game, name = unlock.Name, description = unlock.Description,
        icon = unlock.Icon?.StartsWith("data:image/", StringComparison.Ordinal) == true ? unlock.Icon : null,
        fallback = unlock.GameId == "preview" ? AchievementIconCache.Bundled() : null,
        percent = unlock.Percent, rarity = unlock.Rarity, unlocked = unlock.Done, total = unlock.Total, completed = unlock.Completed,
        volume = Math.Clamp(volumePercent, 0, 100) / 100.0, reduce = reduceMotion,
    });

    /// <summary>A sample banner (the Preview in Settings: common, rare, epic, legendary or completion), as a real unlock shows it.</summary>
    internal static void Preview(string tier)
    {
        AppLog.Info($"Achievements: preview {tier}, {Unlocked?.GetInvocationList().Length ?? 0} listening.");
        double? percent = tier switch { "legendary" or "completion" => 0.6, "epic" => 3.2, "rare" => 12.5, _ => 48 };
        var (name, description, done) = tier switch
        {
            "completion" => ("Every Last One", "Unlock every achievement.", 66),
            "legendary" => ("Without a Scratch", "Finish the final chapter without taking a hit.", 58),
            "epic" => ("Unbroken", "Win ten fights in a row.", 41),
            "rare" => ("The Long Road Home", "Walk back from the far side of the valley.", 23),
            _ => ("First Light", "Reach the summit at dawn.", 8),
        };
        Unlocked?.Invoke(new Unlock("preview", "this game", "preview", name, description, AchievementIconCache.Sample(), percent, Rarity(percent), done, 66, tier == "completion", DateTimeOffset.UtcNow));
    }

    /// <summary>Steam's public global unlock percentages for a game, by api name (empty when Steam doesn't answer).</summary>
    private static async Task<Dictionary<string, double>> PercentsAsync(string appId)
    {
        var percents = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            var json = await Http.GetStringAsync($"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v0002/?gameid={appId}").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("achievementpercentages", out var root) && root.TryGetProperty("achievements", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.TryGetProperty("name", out var name) && name.GetString() is { } api && item.TryGetProperty("percent", out var value))
                        percents[api] = value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed
                            : value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 100;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { AppLog.Debug("Achievements: no global percentages: " + ex.Message); }
        return percents;
    }

    /// <summary>How rare by the share of players who have it: legendary under 1%, epic under 5%, rare under 20%, else common.</summary>
    internal static string Rarity(double? percent) => percent switch
    {
        null => "common",
        < 1 => "legendary",
        < 5 => "epic",
        < 20 => "rare",
        _ => "common",
    };
}

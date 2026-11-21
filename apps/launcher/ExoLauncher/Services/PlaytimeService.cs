using ExoLauncher.Services.Account;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Playtime for every library title: native lifetime readings (Steam, Epic,
/// and GOG) or Exo's own measured sessions, whichever is larger. A Riot game's lifetime is its
/// Riot ID's ledger (<see cref="RiotLedger"/>): tracker.gg's total once, then Exo's sessions.
/// </summary>
public static class PlaytimeService
{
    private sealed record LocalPlaytimeRow(
        GameEntry Game,
        int? StoreMinutes,
        DateTimeOffset? StoreLastPlayed,
        int ExoMinutes,
        DateTimeOffset? ExoLastPlayed);

    private static readonly object FileGate = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ActiveSessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> SessionCheckpoints = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _checkpointTimer;
    // Set by a restore or import: this run never writes playtime.json again (the restored file is the copy), so the close-time flush cannot undo it.
    private static string? _restoredFileKeptFor;
    // Only ever runs while a game does: each checkpoint rewrites and flushes playtime.json. A crash loses at most this
    // much, and none of it while the game is still running when Exo is back (AdoptSession counts from the checkpoint).
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromMinutes(5);
    private static Dictionary<string, int>? _exoMinutes;
    private static Dictionary<string, string>? _exoLastPlayed;
    // The Xbox hours seed (converted once from reported-hours.json), and per Xbox game the Exo minutes it already includes.
    private static Dictionary<string, ReportedHours.TitleHours>? _xboxSeeds;
    private static Dictionary<string, int>? _xboxBase;
    // Until the initial document is read successfully, these dictionaries hold
    // only this process's pending contributions and must never replace the file.
    private static bool _exoLoaded;
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private static string StorePath => Path.Combine(PathHelper.AppDataDir, "playtime.json");

    // What keeps Steam's log from adding a session twice (BackfillSteam): each Steam session Exo counted itself, and the
    // last time in Steam's log already read. Spans older than any session still to be read are dropped.
    internal readonly record struct Span(DateTimeOffset Start, DateTimeOffset End);
    private static Dictionary<string, List<Span>>? _countedSteam;
    private static DateTimeOffset? _steamLogReadTo;
    private static int _steamBackfilling;
    private static readonly ConcurrentDictionary<string, byte> AutoSeededRiotAccounts = new(StringComparer.Ordinal);

    internal static async Task<bool> TryAutoSeedRiotHoursAsync(Func<Task>? onSeeded = null, CancellationToken ct = default)
    {
        if (HostMode.TestRun || HostMode.IsDisposable) return false;
        var riotId = RiotIdStore.TryRead();
        if (riotId is null) return false;
        var account = RiotLedger.AccountKey(riotId);
        if (account is null || RiotLedger.HasSeed(account)) return false;
        if (!AutoSeededRiotAccounts.TryAdd(account, 0)) return false;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RiotHoursClient.RequestTimeout);
            var result = await RiotHoursClient.LoadAsync(riotId, timeout.Token).ConfigureAwait(false);
            if (!result.Ok) return false;
            var now = DateTimeOffset.UtcNow;
            var importedAt = result.LatestMatchUtc() is { } latest && latest <= now ? latest : now;
            if (!RiotLedger.SeedIfHigher(account, result.ToSeeds(), importedAt, RiotLedger.CloudSource))
                return false;
            AppLog.Info("Riot hours synced automatically for " + result.Readings.Count + " game(s).");
            if (onSeeded is not null)
                await onSeeded().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug("Automatic Riot hours sync skipped: " + ex.GetType().Name);
            return false;
        }
    }

    // The play sessions this PC counted or was credited for (SessionKey), saved in playtime.json: a pulled entry for one is never added again.
    private static HashSet<string>? _syncedSessions;

    public static void BeginSession(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            Begin_NoLock(gameId, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Opens a session for a game Exo found running without its own Play: started from its store or a shortcut, or
    /// still running when Exo started again. It counts from the process's own start, but never from before what is
    /// already counted (Exo's last-played stamp is the end of every counted session), so a restart mid-game neither
    /// loses nor counts twice. A session already open is kept.
    /// </summary>
    public static void AdoptSession(string gameId, DateTimeOffset? processStartedAt)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        lock (FileGate)
        {
            if (ActiveSessions.ContainsKey(gameId)) return;
            // First: a session a crash left open is counted through its checkpoint, which moves last played up to it.
            EnsureExoLoaded_NoLock();
            var now = DateTimeOffset.UtcNow;
            var start = processStartedAt ?? now;
            if (ExoLastPlayed_NoLock(gameId) is { } counted && counted > start)
                start = counted;
            Begin_NoLock(gameId, start > now ? now : start);
        }
    }

    private static void Begin_NoLock(string gameId, DateTimeOffset start)
    {
        ActiveSessions[gameId] = start;
        // Seen running now: an adopted session's time since its process started is already real if Exo then crashes.
        SessionCheckpoints[gameId] = DateTimeOffset.UtcNow;
        // Last played is the instant play starts. Waiting for EndSession left
        // a just-launched card showing an older reading — its own store's
        // previous session, or a sibling store's, whichever was newer.
        _exoLastPlayed![gameId] = start.ToString("O", CultureInfo.InvariantCulture);
        if (gameId.StartsWith("riot:", StringComparison.OrdinalIgnoreCase))
        {
            // Without a Riot ID the session goes to the unassigned ledger, which the ID's first set moves into its own.
            var account = RiotLedger.AccountKey(RiotIdStore.TryRead());
            RiotLedger.Begin(account ?? RiotLedger.Unassigned, gameId, start);
            if (account is not null) RiotModes.Refresh(gameId);
        }
        _checkpointTimer ??= new Timer(
            _ => CheckpointActiveSessions(),
            null,
            CheckpointEvery,
            CheckpointEvery);
        SaveExo_NoLock();
    }

    /// <summary>Credits the session through <paramref name="endedAt"/> (now when omitted).</summary>
    public static void EndSession(string gameId, DateTimeOffset? endedAt = null)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        lock (FileGate)
        {
            if (!ActiveSessions.TryRemove(gameId, out var start)) return;
            SessionCheckpoints.TryRemove(gameId, out _);
            StopCheckpointTimerWhenIdle_NoLock();
            EnsureExoLoaded_NoLock();
            var end = endedAt ?? DateTimeOffset.UtcNow;
            CreditElapsed_NoLock(gameId, start, end);
            // Counted here: its entry, once the account has it, is never credited again on this PC (CreditSynced).
            _syncedSessions!.Add(SessionKey(gameId, start));
            AccountSyncHooks.PlaySessionEnded(gameId, start, end, MinutesOf(end - start));
            RiotLedger.End(gameId, end);
            SaveExo_NoLock();
        }
    }

    // The timer ticks every minute for the life of the process otherwise.
    private static void StopCheckpointTimerWhenIdle_NoLock()
    {
        if (!ActiveSessions.IsEmpty) return;
        _checkpointTimer?.Dispose();
        _checkpointTimer = null;
    }

    /// <summary>Drop an open session without crediting (helper / protocol PIDs that exit immediately).</summary>
    public static void CancelSession(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return;
        lock (FileGate)
        {
            if (!ActiveSessions.TryRemove(gameId, out _)) return;
            SessionCheckpoints.TryRemove(gameId, out _);
            RiotLedger.Cancel(gameId);
            StopCheckpointTimerWhenIdle_NoLock();
            EnsureExoLoaded_NoLock();
            SaveExo_NoLock();
        }
    }

    /// <summary>
    /// Drops everything held in memory (open sessions, the loaded document, the Steam read-through mark), as a restart
    /// would. The next use reads the file again, so a test can start from a folder of its own.
    /// </summary>
    internal static void ForgetLoaded()
    {
        lock (FileGate)
        {
            _checkpointTimer?.Dispose();
            _checkpointTimer = null;
            ActiveSessions.Clear();
            SessionCheckpoints.Clear();
            AutoSeededRiotAccounts.Clear();
            _exoMinutes = null;
            _exoLastPlayed = null;
            _xboxSeeds = null;
            _xboxBase = null;
            _countedSteam = null;
            _syncedSessions = null;
            _steamLogReadTo = null;
            _exoLoaded = false;
            _restoredFileKeptFor = null;
        }
    }

    /// <summary>A restore or import has written playtime.json: this run keeps its own saves off it (see SaveExo_NoLock).</summary>
    public static void KeepRestoredFile()
    {
        lock (FileGate) _restoredFileKeptFor = PathHelper.AppDataDir;
    }

    /// <summary>Credits active sessions through shutdown and stops the checkpoint timer.</summary>
    public static void FlushActiveSessions()
    {
        foreach (var gameId in ActiveSessions.Keys.ToArray())
            EndSession(gameId);
        lock (FileGate)
        {
            _checkpointTimer?.Dispose();
            _checkpointTimer = null;
            EnsureExoLoaded_NoLock();
            SaveExo_NoLock();
        }
    }

    private static void CheckpointActiveSessions()
    {
        lock (FileGate)
        {
            if (ActiveSessions.IsEmpty)
            {
                StopCheckpointTimerWhenIdle_NoLock();
                return;
            }
            var now = DateTimeOffset.UtcNow;
            foreach (var gameId in ActiveSessions.Keys)
                SessionCheckpoints[gameId] = now;
            foreach (var gameId in ActiveSessions.Keys)
                if (gameId.StartsWith("riot:", StringComparison.OrdinalIgnoreCase)) RiotModes.Refresh(gameId);
            RiotLedger.Checkpoint(now);
            EnsureExoLoaded_NoLock();
            SaveExo_NoLock();
        }
    }

    /// <summary>
    /// Adds the Steam play Exo didn't count itself, from Steam's own log of every game process it starts and stops:
    /// played while Exo was closed, or a game Exo couldn't see. Runs as Exo starts and as a Steam game exits.
    /// </summary>
    public static void BackfillSteam()
    {
        if (Interlocked.Exchange(ref _steamBackfilling, 1) == 1) return;
        try
        {
            if (ResolveSteamRoot() is not { } root) return;
            // From Exo's first install (its data folder's birth): play before it is in Steam's own play time.
            BackfillSteam(SteamGameProcessLog.ReadWithPrevious(root),
                new DateTimeOffset(Directory.GetCreationTimeUtc(PathHelper.AppDataDir), TimeSpan.Zero), TimeZoneInfo.Local);
        }
        catch (Exception ex) { AppLog.Warn("Play time from Steam's log failed: " + ex.GetType().Name); }
        finally { Volatile.Write(ref _steamBackfilling, 0); }
    }

    internal static void BackfillSteam(string log, DateTimeOffset installedAt, TimeZoneInfo zone)
    {
        var (sessions, readTo) = SteamGameProcessLog.Sessions(log, zone);
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            // Unread, what Exo already counted is unknown.
            if (!_exoLoaded) return;
            var now = DateTimeOffset.UtcNow;
            var counted = _countedSteam!.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var (gameId, start) in ActiveSessions)
            {
                if (!counted.TryGetValue(gameId, out var spans)) counted[gameId] = spans = [];
                spans.Add(new Span(start, now));
            }
            // The first read: what Exo counted before it kept its sessions is somewhere in Steam's log as well.
            var earlier = _steamLogReadTo is not null ? null : _exoMinutes!
                .Where(pair => pair.Key.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value -
                    (int)_countedSteam!.GetValueOrDefault(pair.Key, []).Sum(span => (span.End - span.Start).TotalMinutes),
                    StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var (gameId, minutes, lastEnd) in PlanSteamBackfill(sessions, installedAt, _steamLogReadTo ?? installedAt, counted, earlier))
            {
                _exoMinutes![gameId] = AddMinutes(_exoMinutes.GetValueOrDefault(gameId), minutes);
                if (!(ExoLastPlayed_NoLock(gameId) >= lastEnd))
                    _exoLastPlayed![gameId] = lastEnd.ToString("O", CultureInfo.InvariantCulture);
                AppLog.Info($"Play time: {minutes} min of {gameId} from Steam's log, played while Exo wasn't counting.");
                changed = true;
            }
            if (readTo is { } read && !(_steamLogReadTo >= read))
            {
                _steamLogReadTo = read;
                foreach (var (gameId, spans) in _countedSteam!.ToArray())
                {
                    spans.RemoveAll(span => span.End < read - SteamGameProcessLog.LongestSession);
                    if (spans.Count == 0) _countedSteam!.Remove(gameId);
                }
                changed = true;
            }
            if (changed) SaveExo_NoLock();
        }
    }

    /// <summary>
    /// What Steam's closed sessions add per game: each one ending after <paramref name="readTo"/> (one ending earlier
    /// was read before), from Exo's first install on, less every span Exo counted itself. <paramref name="earlier"/> is
    /// minutes Exo counted before it kept its sessions (the first read only): they are in Steam's log too, so come off.
    /// </summary>
    internal static List<(string GameId, int Minutes, DateTimeOffset LastEnd)> PlanSteamBackfill(
        IEnumerable<SteamGameProcessLog.Session> sessions, DateTimeOffset installedAt, DateTimeOffset readTo,
        IReadOnlyDictionary<string, List<Span>> counted, IReadOnlyDictionary<string, int>? earlier)
    {
        var found = new Dictionary<string, (double Seconds, DateTimeOffset LastEnd)>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            var start = session.StartUtc < installedAt ? installedAt : session.StartUtc;
            if (session.EndUtc <= readTo || session.EndUtc <= start) continue;
            var gameId = "steam:" + session.AppId;
            var seconds = UncountedSeconds(start, session.EndUtc, counted.GetValueOrDefault(gameId));
            if (seconds <= 0) continue;
            var sum = found.GetValueOrDefault(gameId);
            found[gameId] = (sum.Seconds + seconds, session.EndUtc > sum.LastEnd ? session.EndUtc : sum.LastEnd);
        }
        return found
            .Select(pair => (GameId: pair.Key,
                Minutes: (int)Math.Floor((pair.Value.Seconds - 60.0 * Math.Max(0, earlier?.GetValueOrDefault(pair.Key) ?? 0)) / 60),
                pair.Value.LastEnd))
            .Where(credit => credit.Minutes > 0)
            .ToList();
    }

    // The part of a Steam session outside every span Exo counted. A piece under a minute is the seconds between Exo's
    // and Steam's own start or exit, not play.
    private static double UncountedSeconds(DateTimeOffset start, DateTimeOffset end, List<Span>? counted)
    {
        var seconds = 0.0;
        var from = start;
        foreach (var span in (counted ?? []).Where(span => span.End > start && span.Start < end).OrderBy(span => span.Start))
        {
            Add(span.Start);
            if (span.End > from) from = span.End;
        }
        Add(end);
        return seconds;

        void Add(DateTimeOffset to)
        {
            if ((to - from).TotalSeconds >= 60) seconds += (to - from).TotalSeconds;
        }
    }

    private static DateTimeOffset? ExoLastPlayed_NoLock(string gameId) =>
        _exoLastPlayed!.TryGetValue(gameId, out var raw) &&
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    /// <summary>
    /// When a library game's session on this PC last ended: the newest of the per-game last-played stamps a session's end
    /// writes (a game on another PC has none here, so its store's time is never taken for one). Null when none is known.
    /// </summary>
    internal static DateTime? LastExoSessionEnd()
    {
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            DateTime? newest = null;
            foreach (var raw in _exoLastPlayed!.Values)
            {
                if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
                var utc = at.UtcDateTime;
                if (newest is null || utc > newest) newest = utc;
            }
            return newest;
        }
    }

    /// <summary>
    /// Attach the best known playtime / last-played to each game.
    /// <paramref name="steamAccountMinutes"/> is Steam Web API playtime_forever by app id.
    /// </summary>
    public static IReadOnlyList<GameEntry> Enrich(
        IReadOnlyList<GameEntry> games,
        IReadOnlyDictionary<string, int>? steamAccountMinutes = null,
        IReadOnlyDictionary<string, int>? epicMinutesOverride = null)
    {
        if (games.Count == 0) return games;

        var steamRoot = ResolveSteamRoot();
        IReadOnlyDictionary<string, SteamPlaytime.Entry> steam = steamRoot is null
            ? new Dictionary<string, SteamPlaytime.Entry>()
            : SteamPlaytime.LoadHours(steamRoot, allAccounts: true);
        var (gogSnapshot, galaxyRows) = LoadGalaxyPlaytimeTogether();
        var gog = gogSnapshot.Minutes;
        var gogLast = gogSnapshot.LastPlayed;
        var riotLast = RiotLastPlayed.LoadAll();
        var epicLast = EpicEglLastPlayed.LoadAll();
        var riotAccount = RiotLedger.AccountKey(RiotIdStore.TryRead());
        Dictionary<string, int> exo;
        Dictionary<string, string> exoLast;
        List<ReportedHours.TitleHours> xboxSeeds;
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            exo = new Dictionary<string, int>(_exoMinutes!, StringComparer.OrdinalIgnoreCase);
            exoLast = new Dictionary<string, string>(_exoLastPlayed!, StringComparer.OrdinalIgnoreCase);
            xboxSeeds = _xboxSeeds!.Values.ToList();
        }

        var epicMinutes = epicMinutesOverride ?? EpicPlaytime.GetCachedMinutes();
        var now = DateTimeOffset.UtcNow;
        var rows = games.Select(g =>
        {
            // The store figure is only what this method reads itself: an incoming value may be
            // an earlier pass's Galaxy, tracker or Exo figure (painted or cached rows), never
            // trusted as the store's.
            int? storeMins = null;
            DateTimeOffset? storeLast = g.LastPlayedUtc;

            if (g.Store == StoreKind.Steam)
            {
                if (!string.IsNullOrWhiteSpace(g.LaunchTarget) && steam.TryGetValue(g.LaunchTarget, out var se))
                {
                    if (se.Minutes > 0) storeMins = se.Minutes;
                    if (se.LastPlayedUtc is not null) storeLast = se.LastPlayedUtc;
                }
                var account = SteamOwnedPlaytime.MinutesFor(g, steamAccountMinutes);
                if (account > (storeMins ?? 0)) storeMins = account;
            }
            else if (g.Store == StoreKind.Gog)
            {
                var gogId = ExtractGogId(g);
                if (gogId is not null && gog.TryGetValue(gogId, out var gm) && gm > 0)
                    storeMins = gm;
                if (gogId is not null && gogLast.TryGetValue(gogId, out var gl) &&
                    (storeLast is null || gl > storeLast))
                    storeLast = gl;
            }
            else if (g.Store == StoreKind.Riot)
            {
                foreach (var product in RiotProductKeys(g))
                {
                    if (riotLast.TryGetValue(product, out var rl) &&
                        (storeLast is null || rl > storeLast))
                        storeLast = rl;
                }
            }
            else if (g.Store == StoreKind.Epic &&
                     g.EntitlementState is not EntitlementState.Unverified and not EntitlementState.NotOwned)
            {
                var reading = EpicPlaytime.MinutesFor(g, epicMinutes);
                storeMins = reading > 0 ? reading : null;
                foreach (var key in EpicPlaytime.ArtifactKeys(g))
                {
                    if (epicLast.TryGetValue(key, out var el) &&
                        (storeLast is null || el > storeLast))
                        storeLast = el;
                }
            }

            // Epic's launcher writes local wall-clock with a Z suffix, so east
            // of UTC last-played can arrive a few hours in the future. Drop only
            // impossible stamps; clamp a small skew to now so the UI is not blank.
            if (storeLast > now.AddHours(36)) storeLast = null;
            else if (storeLast > now) storeLast = now;

            exo.TryGetValue(g.Id, out var exoMins);
            DateTimeOffset? exoPlayed = null;
            if (exoLast.TryGetValue(g.Id, out var raw) &&
                DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                exoPlayed = parsed;

            return new LocalPlaytimeRow(g, storeMins, storeLast, exoMins, exoPlayed);
        }).ToList();

        return rows.Select(row =>
        {
            // Every row stays store-local: one store's counter is never copied
            // onto another store's entry. Combining the same game across stores
            // is the grouped card's job (LibraryService.GroupVariants), which
            // reads these store-local values and never feeds its total back in.
            // A Riot game's lifetime is its Riot ID's ledger: tracker.gg's seed plus Exo's sessions since (every mode).
            // Before a Riot ID is set, the minutes are in the unassigned ledger, so the row shows them rather than 0.
            var importedMinutes = row.Game.Store == StoreKind.Riot
                ? RiotLedger.Totals(riotAccount ?? RiotLedger.Unassigned, row.Game.Id)
                : 0;
            // The store's own figure, else GOG Galaxy's for that store, else Riot's tracker.gg (ReportedPlaytime).
            var xboxTitle = ReportedPlaytime.XboxFor(row.Game, xboxSeeds,
                row.Game.Store == StoreKind.Xbox && xboxSeeds.Count > 0 ? XboxGameConfig.Read(row.Game.Path)?.IdentityName : null);
            var xbox = xboxTitle is null ? null : XboxSeedReading(row.Game.Id, xboxTitle);
            var galaxy = GalaxyPlatformTimes.Find(row.Game, galaxyRows);
            var pick = ReportedPlaytime.Pick(
                row.StoreMinutes is > 0 ? new(row.StoreMinutes.Value, ReportedPlaytime.Source.Store)
                    : xbox is { } seeded ? new(seeded.Minutes, ReportedPlaytime.Source.Xbox) : null,
                galaxy is null ? null : new(galaxy.Minutes, ReportedPlaytime.Source.Galaxy),
                importedMinutes > 0 ? new(importedMinutes, ReportedPlaytime.Source.Tracker) : null);
            // Exo's own measured minutes for this entry when they are more: play the store never saw (Steam offline,
            // a game started while its store was closed) is not hidden. The larger, never the sum: the same session is
            // in both. A larger store figure (play on other PCs) still wins. The Xbox seed already holds Exo's earlier
            // minutes and adds only the later ones, so it is the figure as it is.
            var most = pick is { From: ReportedPlaytime.Source.Xbox } xboxPick
                ? xboxPick.Minutes
                : Math.Max(pick?.Minutes ?? 0, ExoMinutesForRow(row.Game.Store, row.ExoMinutes));
            int? best = most > 0 ? most : null;
            var effectiveLast = new[] { row.StoreLastPlayed, row.ExoLastPlayed, xbox?.LastPlayedUtc, galaxy?.LastPlayedUtc }
                .Where(value => value is not null && value <= now.AddHours(36))
                .Select(value => value!.Value)
                .DefaultIfEmpty()
                .Max();
            DateTimeOffset? effectiveLastPlayed = effectiveLast == default ? null : effectiveLast;

            if (best == row.Game.PlaytimeMinutes && effectiveLastPlayed == row.Game.LastPlayedUtc)
                return row.Game;

            return row.Game.WithPlaytime(best, effectiveLastPlayed);
        }).ToList();
    }

    private static (GogPlaytime.Snapshot Gog, IReadOnlyList<GalaxyPlatformTimes.Row> Platform) LoadGalaxyPlaytimeTogether()
    {
        return GogPlaytime.LoadWithPlatformTimes();
    }

    /// <summary>
    /// The per-game Exo counter a row may be maxed with. A Riot game's Exo minutes are its Riot ID's ledger, already in the
    /// row's pick; the per-game counter holds every Riot ID's sessions and other PCs' play, so a Riot row never takes it.
    /// </summary>
    internal static int ExoMinutesForRow(StoreKind store, int exoMinutes) => store == StoreKind.Riot ? 0 : exoMinutes;

    private static string? ExtractGogId(GameEntry g)
    {
        if (g.Id.StartsWith("gog:", StringComparison.OrdinalIgnoreCase))
            return g.Id["gog:".Length..];
        return null;
    }

    private static string? ExtractRiotProduct(GameEntry g)
    {
        if (!string.IsNullOrWhiteSpace(g.LaunchTarget))
            return g.LaunchTarget.Trim().ToLowerInvariant();
        if (g.Id.StartsWith("riot:", StringComparison.OrdinalIgnoreCase))
            return g.Id["riot:".Length..].Trim().ToLowerInvariant();
        return null;
    }

    private static IEnumerable<string> RiotProductKeys(GameEntry g)
    {
        var product = ExtractRiotProduct(g);
        if (string.IsNullOrWhiteSpace(product)) yield break;
        yield return product;
        if (product is "league_of_legends")
        {
            yield return "leagueoflegends";
            yield return "lol";
        }
    }

    private static string? ResolveSteamRoot()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return path.Replace('/', Path.DirectorySeparatorChar);
        }
        catch { /* */ }
        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        }.FirstOrDefault(Directory.Exists);
    }

    private static void EnsureExoLoaded_NoLock()
    {
        if (_exoLoaded) return;
        _exoMinutes ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _exoLastPlayed ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _xboxSeeds ??= new Dictionary<string, ReportedHours.TitleHours>(StringComparer.OrdinalIgnoreCase);
        _xboxBase ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _countedSteam ??= new Dictionary<string, List<Span>>(StringComparer.OrdinalIgnoreCase);
        _syncedSessions ??= new HashSet<string>(StringComparer.Ordinal);
        try
        {
            string json;
            try { json = File.ReadAllText(StorePath); }
            catch (FileNotFoundException) { _exoLoaded = true; ConvertReportedHours_NoLock(); return; }
            catch (DirectoryNotFoundException) { _exoLoaded = true; ConvertReportedHours_NoLock(); return; }
            using var doc = JsonDocument.Parse(json);
            var loadedMinutes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var loadedLast = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var recoveredSessions = new List<(string Id, DateTimeOffset Start, DateTimeOffset End)>();
            if (doc.RootElement.TryGetProperty("minutes", out var mins) &&
                mins.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in mins.EnumerateObject())
                {
                    if (p.Value.TryGetInt32(out var n) && n > 0)
                        loadedMinutes[p.Name] = n;
                }
            }
            if (doc.RootElement.TryGetProperty("lastPlayed", out var last) &&
                last.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in last.EnumerateObject())
                {
                    var s = p.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        loadedLast[p.Name] = s;
                }
            }
            if (doc.RootElement.TryGetProperty("activeSessions", out var active) &&
                active.ValueKind == JsonValueKind.Object)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var session in active.EnumerateObject())
                {
                    if (!TryReadStamp(session.Value, "startUtc", out var start) ||
                        !TryReadStamp(session.Value, "checkpointUtc", out var checkpoint))
                        continue;
                    // A checkpoint is the last time Exo actually observed the
                    // session. Never credit the unknown period after a crash.
                    if (checkpoint < start || checkpoint > now.AddMinutes(5) || checkpoint - start > TimeSpan.FromDays(7))
                        continue;
                    recoveredSessions.Add((session.Name, start, checkpoint));
                }
            }
            var loadedCounted = new Dictionary<string, List<Span>>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("countedSteamSessions", out var counted) &&
                counted.ValueKind == JsonValueKind.Object)
            {
                foreach (var game in counted.EnumerateObject())
                {
                    if (game.Value.ValueKind != JsonValueKind.Array) continue;
                    var spans = new List<Span>();
                    foreach (var span in game.Value.EnumerateArray())
                        if (TryReadStamp(span, "startUtc", out var start) && TryReadStamp(span, "endUtc", out var end) && end > start)
                            spans.Add(new Span(start, end));
                    if (spans.Count > 0) loadedCounted[game.Name] = spans;
                }
            }
            var loadedSeeds = new Dictionary<string, ReportedHours.TitleHours>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("xboxSeeds", out var seeds) && seeds.ValueKind == JsonValueKind.Array)
            {
                foreach (var seed in seeds.EnumerateArray())
                {
                    if (seed.ValueKind != JsonValueKind.Object) continue;
                    var titleId = ReadText(seed, "titleId");
                    var name = ReadText(seed, "name");
                    if (string.IsNullOrWhiteSpace(titleId) || string.IsNullOrWhiteSpace(name) ||
                        !seed.TryGetProperty("minutes", out var minutesNode) || !minutesNode.TryGetInt32(out var seedMinutes) || seedMinutes <= 0)
                        continue;
                    DateTimeOffset? lastPlayed = TryReadStamp(seed, "lastPlayedUtc", out var at) ? at : null;
                    loadedSeeds[titleId] = new ReportedHours.TitleHours(titleId, name, ReadText(seed, "pfn"), seedMinutes, lastPlayed);
                }
            }
            var loadedBase = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("xboxBase", out var bases) && bases.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in bases.EnumerateObject())
                    if (p.Value.TryGetInt32(out var n) && n >= 0) loadedBase[p.Name] = n;
            }
            var loadedSynced = new HashSet<string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("syncedSessions", out var synced) && synced.ValueKind == JsonValueKind.Array)
                foreach (var key in synced.EnumerateArray())
                    if (key.ValueKind == JsonValueKind.String && key.GetString() is { Length: > 0 } text) loadedSynced.Add(text);
            // Publish only a fully parsed baseline, then add contributions made
            // while the original was temporarily unavailable exactly once.
            var pendingMinutes = _exoMinutes;
            var pendingLast = _exoLastPlayed;
            var pendingCounted = _countedSteam;
            var pendingSynced = _syncedSessions!;
            _exoMinutes = loadedMinutes;
            _exoLastPlayed = loadedLast;
            _countedSteam = loadedCounted;
            _syncedSessions = loadedSynced;
            _syncedSessions.UnionWith(pendingSynced);
            _xboxSeeds = loadedSeeds;
            _xboxBase = loadedBase;
            _steamLogReadTo = TryReadStamp(doc.RootElement, "steamLogReadTo", out var readTo) ? readTo : null;
            foreach (var session in recoveredSessions)
            {
                // Counted and synced as a session that ended is: the account gets its entry, and its copy coming back is never credited again.
                CreditElapsed_NoLock(session.Id, session.Start, session.End);
                _syncedSessions.Add(SessionKey(session.Id, session.Start));
                AccountSyncHooks.PlaySessionEnded(session.Id, session.Start, session.End, MinutesOf(session.End - session.Start));
            }
            foreach (var (gameId, spans) in pendingCounted)
            {
                if (!_countedSteam.TryGetValue(gameId, out var loaded)) _countedSteam[gameId] = loaded = [];
                loaded.AddRange(spans);
            }
            foreach (var pair in pendingMinutes)
                _exoMinutes[pair.Key] = AddMinutes(_exoMinutes.GetValueOrDefault(pair.Key), pair.Value);
            foreach (var pair in pendingLast)
                if (!_exoLastPlayed.TryGetValue(pair.Key, out var previous) ||
                    !DateTimeOffset.TryParse(previous, out var previousAt) ||
                    DateTimeOffset.TryParse(pair.Value, out var pendingAt) && pendingAt > previousAt)
                    _exoLastPlayed[pair.Key] = pair.Value;
            // A session counted before minutes were rounded (1 min 56 s stored as 1) is still that long in its span.
            var lifted = false;
            foreach (var (gameId, spans) in _countedSteam)
            {
                var kept = MinutesKept(_exoMinutes.GetValueOrDefault(gameId), spans);
                if (kept <= _exoMinutes.GetValueOrDefault(gameId)) continue;
                _exoMinutes[gameId] = kept;
                lifted = true;
            }
            _exoLoaded = true;
            ConvertReportedHours_NoLock();
            if (lifted || recoveredSessions.Count > 0 || pendingMinutes.Count > 0 || pendingLast.Count > 0 || pendingCounted.Count > 0)
                SaveExo_NoLock();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Content that can never parse. Move it aside and keep saving from
            // what this session has; staying unloaded silently dropped every
            // later playtime save. A locked or busy file still retries.
            AppLog.Warn("playtime.json could not be read: " + ex.GetType().Name);
            if (PathHelper.MoveAsideUnreadable(StorePath))
            {
                _exoLoaded = true;
                SaveExo_NoLock();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("playtime.json load failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Moves the Xbox hours in reported-hours.json into the seed, once. A title already in the seed keeps its figure, so a
    /// second conversion adds nothing. The file is removed only after the seed is saved; if the save fails it stays for the next try.
    /// </summary>
    private static void ConvertReportedHours_NoLock()
    {
        var document = ReportedHours.TryRead();
        if (document is null) return;
        foreach (var title in document.Xbox)
        {
            if (!_xboxSeeds!.TryAdd(title.TitleId, title)) continue;
            AccountSyncHooks.XboxSeedAdded(title);
        }
        if (SaveExo_NoLock()) ReportedHours.Remove();
    }

    private static string RequiredText(System.Text.Json.Nodes.JsonObject node, string name) =>
        node[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text) ? text : throw new FormatException(name + " is missing.");

    private static int RequiredMinutes(System.Text.Json.Nodes.JsonObject node) =>
        node["minutes"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out int minutes) && minutes >= 0 && minutes <= RiotLedger.MaxSeedMinutes ? minutes : throw new FormatException("minutes is missing or too large.");

    /// <summary>A Xbox hours seed another PC of this account converted: added once, and a title already seeded here keeps its figure.</summary>
    internal static void AddSyncedSeed(string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(value)?.AsObject() ?? throw new FormatException("Not a seed.");
        var last = node["lastPlayed"]?.GetValue<string>();
        var title = new ReportedHours.TitleHours(
            RequiredText(node, "title"),
            RequiredText(node, "name"),
            node["pfn"]?.GetValue<string>(),
            RequiredMinutes(node),
            last is null ? null : DateTimeOffset.Parse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            if (_xboxSeeds!.TryAdd(title.TitleId, title)) SaveExo_NoLock();
        }
    }

    /// <summary>
    /// A Xbox game's lifetime: the seed's minutes for its title plus the Exo minutes measured since the seed was first
    /// matched to this game. The seed already counts the Exo minutes before that, so nothing is counted twice.
    /// </summary>
    private static (int Minutes, DateTimeOffset? LastPlayedUtc)? XboxSeedReading(string gameId, ReportedHours.TitleHours title)
    {
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            if (!_xboxBase!.ContainsKey(gameId))
            {
                _xboxBase[gameId] = _exoMinutes!.GetValueOrDefault(gameId);
                SaveExo_NoLock();
            }
            var since = Math.Max(0, _exoMinutes!.GetValueOrDefault(gameId) - _xboxBase[gameId]);
            return (title.Minutes + since, title.LastPlayedUtc);
        }
    }

    private static string? ReadText(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool SaveExo_NoLock()
    {
        if (!_exoLoaded) return false;
        if (_restoredFileKeptFor == PathHelper.AppDataDir)
        {
            AppLog.Info("Play time: a change after the restore is not written; the restored playtime.json stays.");
            return false;
        }
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var payload = new
            {
                minutes = _exoMinutes,
                lastPlayed = _exoLastPlayed,
                xboxSeeds = _xboxSeeds!.Values.Select(seed => new
                {
                    titleId = seed.TitleId,
                    name = seed.Name,
                    pfn = seed.Pfn,
                    minutes = seed.Minutes,
                    lastPlayedUtc = seed.LastPlayedUtc?.ToString("O", CultureInfo.InvariantCulture),
                }).ToArray(),
                xboxBase = _xboxBase,
                syncedSessions = _syncedSessions!.Order(StringComparer.Ordinal).ToArray(),
                activeSessions = ActiveSessions.ToDictionary(
                    pair => pair.Key,
                    pair => new
                    {
                        startUtc = pair.Value.ToString("O", CultureInfo.InvariantCulture),
                        checkpointUtc = SessionCheckpoints
                            .GetValueOrDefault(pair.Key, pair.Value)
                            .ToString("O", CultureInfo.InvariantCulture),
                    },
                    StringComparer.OrdinalIgnoreCase),
                countedSteamSessions = _countedSteam!.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(span => new
                    {
                        startUtc = span.Start.ToString("O", CultureInfo.InvariantCulture),
                        endUtc = span.End.ToString("O", CultureInfo.InvariantCulture),
                    }).ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                steamLogReadTo = _steamLogReadTo?.ToString("O", CultureInfo.InvariantCulture),
            };
            var json = JsonSerializer.Serialize(payload, IndentedJsonOptions);
            var tmp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tmp, StorePath, overwrite: true);
            }
            finally
            {
                try { DeletionGate.DeleteFile(tmp); } catch { /* preserve the original if cleanup also fails */ }
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("playtime.json save failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Identifies one play session by game and start. This PC's own entry and another PC's copy of it carry the same
    /// start, so the key matches on both sides.
    /// </summary>
    internal static string SessionKey(string gameId, DateTimeOffset start) =>
        gameId + "|" + start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Minutes of a synced playtime entry (another PC's, or this PC's own session the account gave back), credited once on
    /// this PC. The session key is kept in playtime.json, which is backed up, so a restore cannot credit it again.
    /// </summary>
    internal static void CreditSynced(string gameId, DateTimeOffset start, int minutes)
    {
        if (string.IsNullOrWhiteSpace(gameId) || minutes <= 0) return;
        lock (FileGate)
        {
            EnsureExoLoaded_NoLock();
            if (!_syncedSessions!.Add(SessionKey(gameId, start))) return;
            _exoMinutes![gameId] = AddMinutes(_exoMinutes.GetValueOrDefault(gameId), minutes);
            SaveExo_NoLock();
        }
    }

    private static void CreditElapsed_NoLock(
        string gameId,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var minutes = MinutesOf(end - start);
        if (minutes <= 0) return;
        _exoMinutes![gameId] = AddMinutes(_exoMinutes.GetValueOrDefault(gameId), minutes);
        _exoLastPlayed![gameId] = end.ToString("O", CultureInfo.InvariantCulture);
        if (!gameId.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return;
        if (!_countedSteam!.TryGetValue(gameId, out var spans)) _countedSteam[gameId] = spans = [];
        spans.Add(new Span(start, end));
    }

    private static bool TryReadStamp(JsonElement node, string name, out DateTimeOffset value)
    {
        value = default;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var raw) &&
               raw.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(raw.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    /// <summary>Rounded, not cut: 1 min 56 s is 2 minutes, as Steam counts it, and from 30 s it is a minute.</summary>
    internal static int MinutesOf(TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero ? 0 : (int)Math.Round(elapsed.TotalMinutes, MidpointRounding.AwayFromZero);

    /// <summary>
    /// What to keep for a Steam game Exo counted: the stored total, or the rounded length of those spans when a
    /// session was cut to the minute below. A larger total (Steam's log for time Exo wasn't counting) stays.
    /// </summary>
    internal static int MinutesKept(int stored, IReadOnlyList<Span> counted)
    {
        var fromSpans = 0;
        foreach (var span in counted)
            fromSpans = AddMinutes(fromSpans, MinutesOf(span.End - span.Start));
        return Math.Max(stored, fromSpans);
    }

    private static int AddMinutes(int current, int added) =>
        (int)Math.Clamp((long)current + added, 0L, int.MaxValue);
}

/// <summary>
/// Riot Client only publishes last-session timestamps locally (no lifetime minutes).
/// </summary>
internal static class RiotLastPlayed
{
    public static Dictionary<string, DateTimeOffset> LoadAll()
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in CandidateSettings())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                // Only the last-session-timestamp block (not patch-notes / affinity).
                ReadLastSessionBlock(text, map);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Riot last-played parse failed: " + ex.Message);
            }
        }
        return map;
    }

    private static IEnumerable<string> CandidateSettings()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Riot Games", "Riot Client", "Config", "RiotClientSettings.yaml");
        yield return Path.Combine(local, "Riot Games", "VALORANT", "Config", "RiotClientSettings.yaml");
        yield return Path.Combine(local, "Riot Games", "League of Legends", "Config", "RiotClientSettings.yaml");
        yield return Path.Combine(local, "Riot Games", "League of Legends", "Config", "LeagueClientSettings.yaml");
    }

    /// <summary>Public for tests — any friend's Riot YAML shape, quoted or not.</summary>
    internal static void ReadLastSessionBlock(string text, IDictionary<string, DateTimeOffset> map)
    {
        var blockIdx = text.IndexOf("last-session-timestamp:", StringComparison.OrdinalIgnoreCase);
        if (blockIdx < 0) return;

        var lineStart = blockIdx;
        while (lineStart > 0 && text[lineStart - 1] is not '\n' and not '\r')
            lineStart--;
        var keyIndent = LeadingIndent(text.AsSpan(lineStart, blockIdx - lineStart));

        var afterKey = text.IndexOf('\n', blockIdx);
        if (afterKey < 0) return;
        var cursor = afterKey + 1;
        var body = new System.Text.StringBuilder();
        while (cursor < text.Length)
        {
            var end = text.IndexOf('\n', cursor);
            if (end < 0) end = text.Length;
            var line = text[cursor..end];
            var trimmed = line.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(trimmed) && LeadingIndent(trimmed) <= keyIndent)
                break;
            body.Append(trimmed);
            body.Append('\n');
            cursor = end + 1;
        }

        var block = body.ToString();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     block,
                     @"(?im)^\s*([a-z0-9_]+)\.(?:live|pbe)\s*:\s*[""']?(\d{9,})[""']?"))
            AddLastSession(map, m.Groups[1].Value, m.Groups[2].Value);

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     block,
                     @"(?im)^\s*([a-z0-9_]+)\s*:\s*(?:#.*)?\r?\n[ \t]+(?:live|pbe)\s*:\s*[""']?(\d{9,})[""']?"))
            AddLastSession(map, m.Groups[1].Value, m.Groups[2].Value);
    }

    private static void AddLastSession(
        IDictionary<string, DateTimeOffset> map, string productRaw, string digits)
    {
        var product = productRaw.Trim().ToLowerInvariant();
        if (!TryUnixTimestamp(digits, out var when)) return;
        if (!map.TryGetValue(product, out var cur) || when > cur)
            map[product] = when;
    }

    private static bool TryUnixTimestamp(string digits, out DateTimeOffset when)
    {
        when = default;
        if (!long.TryParse(digits, out var unix) || unix <= 0) return false;
        try
        {
            when = unix > 9_999_999_999L
                ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                : DateTimeOffset.FromUnixTimeSeconds(unix);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int LeadingIndent(ReadOnlySpan<char> line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 2;
            else break;
        }
        return n;
    }
}

/// <summary>
/// Last-launched stamp from Epic Games Launcher's GameUserSettings.ini.
/// Lifetime hours still come from Epic's playtime API once a session exists.
/// </summary>
internal static class EpicEglLastPlayed
{
    public static Dictionary<string, DateTimeOffset> LoadAll()
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        // testhost must not inherit this machine's EGL last-played into fixtures.
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (entry is not null &&
            entry.Contains("testhost", StringComparison.OrdinalIgnoreCase))
            return map;
        foreach (var path in EpicPlaytime.EglRememberMeIniCandidates())
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("LastPlayedGame=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var value = trimmed["LastPlayedGame=".Length..].Trim().Trim('"');
                    if (!TryParseLastPlayedGame(value, out var app, out var when)) continue;
                    foreach (var key in EpicLastPlayedKeys(value, app))
                    {
                        if (!map.TryGetValue(key, out var cur) || when > cur)
                            map[key] = when;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Epic EGL last-played parse failed: " + ex.Message);
            }
        }
        return map;
    }

    internal static bool TryParseLastPlayedGame(string? value, out string app, out DateTimeOffset when)
    {
        app = "";
        when = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var comma = value.LastIndexOf(',');
        if (comma <= 0 || comma >= value.Length - 1) return false;
        var left = value[..comma].Trim();
        var stamp = value[(comma + 1)..].Trim();
        var colon = left.LastIndexOf(':');
        app = (colon >= 0 ? left[(colon + 1)..] : left).Trim();
        if (string.IsNullOrWhiteSpace(app) || app.Length > 128) return false;
        return DateTimeOffset.TryParse(
            stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when);
    }

    internal static IEnumerable<string> EpicLastPlayedKeys(string raw, string app)
    {
        yield return app;
        var comma = raw.LastIndexOf(',');
        var left = comma > 0 ? raw[..comma].Trim() : raw.Trim();
        foreach (var part in left.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0 && part.Length <= 128) yield return part;
        }
    }
}

/// <summary>
/// One shared Galaxy snapshot for adapter discovery and playtime enrichment.
/// SQLite is preferred; bounded legacy JSON is a compatibility fallback.
/// </summary>
internal static class GogPlaytime
{
    internal sealed record Snapshot(IReadOnlyDictionary<string, int> Minutes, IReadOnlyDictionary<string, DateTimeOffset> LastPlayed);
    private static readonly object CacheGate = new();
    private static Snapshot? _cached;
    private static string? _cacheKey;
    private static long _cacheUntil;
    internal const int JsonEntryLimit = 2048, JsonDepthLimit = 3, JsonFileLimit = 256;
    internal const long JsonByteLimit = 16 * 1024 * 1024;

    public static Dictionary<string, int> LoadAll() => new(LoadWithPlatformTimes().Gog.Minutes, StringComparer.Ordinal);

    internal static Snapshot LoadSnapshot() => LoadWithPlatformTimes().Gog;

    internal static (Snapshot Gog, IReadOnlyList<GalaxyPlatformTimes.Row> Platform) LoadWithPlatformTimes()
    {
        var databases = GogGalaxySqlite.CandidateDatabasePaths().ToArray();
        var roots = CandidateRoots().ToArray();
        var dbKey = GalaxyPlatformTimes.DatabaseCacheKey(databases);
        var gogKey = dbKey + "|" + string.Join('|', roots);
        var now = Environment.TickCount64;
        lock (CacheGate)
        {
            if (_cached is not null && _cacheKey == gogKey && now < _cacheUntil &&
                GalaxyPlatformTimes.TryGetCached(dbKey, now, out var cachedPlatform))
                return (_cached, cachedPlatform);

            var gogMap = new Dictionary<string, int>(StringComparer.Ordinal);
            var gogLastPlayed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            var platformRows = new List<GalaxyPlatformTimes.Row>();
            var sqliteAvailable = false;
            foreach (var path in databases)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var combined = GogGalaxySqlite.ReadCombined(path);
                    sqliteAvailable = true;
                    foreach (var row in combined.GogTimes)
                    {
                        if (!gogMap.TryGetValue(row.ProductId, out var current) || row.Minutes > current)
                            gogMap[row.ProductId] = row.Minutes;
                        if (row.LastPlayedUtc is { } last && (!gogLastPlayed.TryGetValue(row.ProductId, out var currentLast) || last > currentLast))
                            gogLastPlayed[row.ProductId] = last;
                    }
                    platformRows.AddRange(combined.PlatformRows);
                }
                catch (Exception ex)
                {
                    AppLog.Debug("Galaxy hours unreadable: " + ex.GetType().Name);
                }
            }

            var gogSnapshot = sqliteAvailable
                ? new Snapshot(gogMap, gogLastPlayed)
                : ReadSnapshot(Array.Empty<string>(), roots);
            _cacheKey = gogKey;
            _cacheUntil = now + 30_000;
            _cached = gogSnapshot;
            GalaxyPlatformTimes.SeedCache(dbKey, now, platformRows);
            return (gogSnapshot, platformRows);
        }
    }

    internal static Snapshot LoadSnapshot(IEnumerable<string> databasePaths, IEnumerable<string> jsonRoots, long now)
    {
        var databases = databasePaths.ToArray();
        var roots = jsonRoots.ToArray();
        static string Stamp(string path)
        {
            try { var file = new FileInfo(path); return file.Exists ? $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : path + ":missing"; }
            catch { return path + ":unavailable"; }
        }
        var key = string.Join('|', databases.SelectMany(path => new[] { Stamp(path), Stamp(path + "-wal") }).Concat(roots));
        lock (CacheGate)
        {
            if (_cached is not null && _cacheKey == key && now < _cacheUntil) return _cached;
            var next = ReadSnapshot(databases, roots);
            _cacheKey = key;
            _cacheUntil = now + 30_000;
            return _cached = next;
        }
    }

    internal static Snapshot ReadSnapshot(IEnumerable<string> databases, IEnumerable<string> roots)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastPlayed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var sqliteAvailable = false;
        foreach (var path in databases)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var rows = GogGalaxySqlite.ReadGameTimes(path);
                sqliteAvailable = true;
                foreach (var row in rows)
                {
                    if (!map.TryGetValue(row.ProductId, out var current) || row.Minutes > current) map[row.ProductId] = row.Minutes;
                    if (row.LastPlayedUtc is { } last && (!lastPlayed.TryGetValue(row.ProductId, out var currentLast) || last > currentLast))
                        lastPlayed[row.ProductId] = last;
                }
            }
            catch { /* Missing/corrupt/older Galaxy database: bounded compatibility fallback. */ }
        }
        if (sqliteAvailable) return new(map, lastPlayed);
        var files = 0;
        long bytes = 0;
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Adapters.BoundedFileTree.EnumerateFiles(root, JsonEntryLimit, JsonDepthLimit))
                {
                    if (!Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (++files > JsonFileLimit) return new(map, lastPlayed);
                    try
                    {
                        using var stream = File.OpenRead(file);
                        if (stream.Length is < 2 or > 2_000_000) continue;
                        bytes += stream.Length;
                        if (bytes > JsonByteLimit) return new(map, lastPlayed);
                        var buffer = new byte[(int)stream.Length];
                        stream.ReadExactly(buffer);
                        var text = System.Text.Encoding.UTF8.GetString(buffer);
                        if (!text.Contains("timeSpentInGame", StringComparison.OrdinalIgnoreCase) &&
                            !text.Contains("playtime", StringComparison.OrdinalIgnoreCase))
                            continue;
                        TryParseFile(map, text, file);
                    }
                    catch { /* skip */ }
                }
            }
            catch { /* skip root */ }
        }
        return new(map, lastPlayed);
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var prog = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(prog, "GOG.com", "Galaxy", "storage");
        yield return Path.Combine(local, "GOG.com", "Galaxy", "storage");
        yield return Path.Combine(local, "GOG.com", "Galaxy", "webcache");
    }

    private static void TryParseFile(Dictionary<string, int> map, string text, string path)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        // Heroic / Galaxy shapes: { "productId": "123", "playtime": 3600 } (seconds)
        // or nested timeSpentInGame in seconds.
        var id = GuessProductId(root, path);
        var seconds = ReadSeconds(root);
        if (id is null || seconds is null or <= 0) return;
        var mins = (int)Math.Clamp(seconds.Value / 60, 1, int.MaxValue);
        if (!map.TryGetValue(id, out var cur) || mins > cur)
            map[id] = mins;
    }

    private static string? GuessProductId(JsonElement root, string path)
    {
        foreach (var key in new[] { "productId", "product_id", "gameId", "game_id", "id" })
        {
            if (root.TryGetProperty(key, out var el))
            {
                var s = el.ValueKind == JsonValueKind.Number
                    ? el.GetRawText()
                    : el.GetString();
                if (!string.IsNullOrWhiteSpace(s) && s.All(char.IsDigit))
                    return s;
            }
        }
        var name = Path.GetFileNameWithoutExtension(path);
        return name.All(char.IsDigit) ? name : null;
    }

    private static long? ReadSeconds(JsonElement root)
    {
        foreach (var key in new[] { "timeSpentInGame", "playtime", "playTime", "totalPlayTime" })
        {
            if (!root.TryGetProperty(key, out var el)) continue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n) && n > 0)
                return n;
            if (el.ValueKind == JsonValueKind.String &&
                long.TryParse(el.GetString(), out n) && n > 0)
                return n;
        }
        return null;
    }
}

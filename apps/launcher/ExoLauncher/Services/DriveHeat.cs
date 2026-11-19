using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// A game's drive in a session: while a library game runs (however it started), the drive it is installed on is asked
/// its temperature every 10 seconds, its SMART thermal counters at the start and end, and its PCIe link. Nothing is
/// asked while no game runs, so a laptop's drive can rest. When the game closes, its session is kept only if the drive
/// came near its warning point or slowed itself (<see cref="Drives.HeatWorthSaying"/>), for that game's page; the
/// fastest link seen under play is kept per drive, so PC › Drives can tell a link that stays slow from one resting.
/// </summary>
internal static class DriveHeat
{
    private sealed class Session(int disk, string? instance)
    {
        internal int Disk { get; } = disk;
        internal string? Instance { get; } = instance;
        internal int Peak { get; set; } = int.MinValue;
        internal int? Warn { get; set; }
        internal long? SlowedSeconds { get; set; }
        internal long? SlowedEvents { get; set; }
        internal int FastestGen { get; set; }
    }

    /// <summary>What is kept: each game's last session worth saying, and each drive's fastest link seen under play.</summary>
    internal sealed record Store(Dictionary<string, Drives.Heat> Games, Dictionary<string, int> Links);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Session> Sessions = new(StringComparer.Ordinal);
    private static CancellationTokenSource? _loop;
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);
    private static string StorePath => Path.Combine(PathHelper.AppDataDir, "drive-heat.json");

    internal static void Start() => App.Services.Launcher.GameRunStateChanged += (_, _) => Task.Run(Sync);

    /// <summary>The last session of a game worth saying, or null.</summary>
    internal static Drives.Heat? Last(string gameId) => Load().Games.GetValueOrDefault(gameId);

    /// <summary>A link that never ran at its full speed while a game played on this drive (null: no session seen yet).</summary>
    internal static bool SlowSeen(string? instance, Drives.Link? link) =>
        instance is not null && link is not null && Load().Links.TryGetValue(instance, out var fastest) && fastest < link.MaxGen;

    private static void Sync()
    {
        try
        {
            var running = MainWindow.RunningLibraryGames().Keys.ToHashSet(StringComparer.Ordinal);
            List<(string Id, Session Session)> ended;
            lock (Gate)
            {
                foreach (var id in running.Where(id => !Sessions.ContainsKey(id)))
                    if (Begin(id) is { } session) Sessions[id] = session;
                ended = Sessions.Where(s => !running.Contains(s.Key)).Select(s => (s.Key, s.Value)).ToList();
                foreach (var (id, _) in ended) Sessions.Remove(id);
                if (Sessions.Count > 0 && _loop is null)
                {
                    _loop = new CancellationTokenSource();
                    _ = LoopAsync(_loop.Token);
                }
                else if (Sessions.Count == 0 && _loop is { } loop)
                {
                    loop.Cancel();
                    _loop = null;
                }
            }
            foreach (var (id, session) in ended) End(id, session);
        }
        catch (Exception ex) { AppLog.Debug("Drive heat: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>The game's drive and its first reading; null for a game Exo can't place on a drive it can read.</summary>
    private static Session? Begin(string gameId)
    {
        if (App.Services.Library.Find(gameId)?.Path is not { } path || DriveSpace.LetterOf(path) is not { Length: > 0 } letter
            || PcAbout.DiskNumber(letter) is not { } disk) return null;
        var instance = Drives.ReadDisks().FirstOrDefault(d => d.Number == disk)?.InstanceId;
        var session = new Session(disk, instance);
        Take(session);
        return session.Peak == int.MinValue ? null : session;
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Every, ct).ConfigureAwait(false);
                List<Session> now;
                lock (Gate) now = [.. Sessions.Values];
                foreach (var session in now) Take(session);
            }
        }
        catch (OperationCanceledException) { /* no game runs */ }
    }

    /// <summary>One reading: the peak, the drive's warning point, the thermal counters at the first reading, the fastest link.</summary>
    private static void Take(Session session)
    {
        var (temperature, health, link) = Drives.Sample(session.Disk, session.Instance);
        lock (Gate)
        {
            if (temperature is not null)
            {
                session.Peak = Math.Max(session.Peak, temperature.NowC);
                session.Warn ??= temperature.WarnC;
            }
            session.SlowedSeconds ??= health?.ThrottleSeconds;
            session.SlowedEvents ??= health?.ThrottleEvents;
            if (link is not null) session.FastestGen = Math.Max(session.FastestGen, link.Gen);
        }
    }

    private static void End(string gameId, Session session)
    {
        var (temperature, health, _) = Drives.Sample(session.Disk, session.Instance);
        if (temperature is not null) session.Peak = Math.Max(session.Peak, temperature.NowC);
        var seconds = health?.ThrottleSeconds - session.SlowedSeconds ?? 0;
        var events = health?.ThrottleEvents - session.SlowedEvents ?? 0;
        var heat = Drives.HeatWorthSaying(session.Peak, session.Warn, seconds, events, DateTimeOffset.UtcNow);
        AppLog.Info($"Drive heat: {gameId}: peak {session.Peak} °C (warns at {session.Warn?.ToString() ?? "-"}), slowed {seconds} s in {events} step(s){(session.FastestGen > 0 ? $", link up to Gen{session.FastestGen}" : "")}.");
        lock (Gate)
        {
            var store = Load();
            if (heat is null) store.Games.Remove(gameId);
            else store.Games[gameId] = heat;
            if (session.Instance is { } instance && session.FastestGen > 0) store.Links[instance] = session.FastestGen;
            Save(store);
        }
    }

    private static Store Load()
    {
        try
        {
            if (File.Exists(StorePath) && JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath)) is { Games: not null, Links: not null } store) return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { /* starts again */ }
        return new Store(new Dictionary<string, Drives.Heat>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
    }

    private static void Save(Store store)
    {
        try
        {
            var temp = StorePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(store));
            File.Move(temp, StorePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Drive heat: not saved: " + ex.Message); }
    }
}

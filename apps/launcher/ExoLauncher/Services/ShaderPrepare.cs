using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// When a Prepare run has seen enough. Unreal compiles a game's shaders itself on its first launch and logs nothing Exo
/// could read, but the work shows from outside: the driver's shader cache grows and the game keeps the processor busy.
/// Done is the cache having grown and then both going quiet.
/// </summary>
internal sealed class ShaderPrepareWatch
{
    internal enum Verdict { Working, Settled, NothingCompiled, TooLong }

    /// A driver writes its cache in bursts and a compile can pause between batches: this long quiet, it has ended.
    internal const double SettleSeconds = 120;
    /// Past this with no growth and a calm game, the game compiles later or nothing at all (a menu it waits at).
    internal const double NothingSeconds = 300;
    internal const double MaxSeconds = 90 * 60;
    /// Growth smaller than this is the driver's housekeeping, not a compile.
    internal const long MinGrowthBytes = 1L << 20;
    /// A write this small does not count as the cache still growing.
    internal const long QuietBytes = 64L << 10;
    /// Of all logical processors, the least that counts as busy. A compile runs on most of them; a menu's share varies by
    /// game, so busy is also well under the run's own peak (PBZ's menu took 11% after a compile at nearly all of them).
    internal const double BusyShare = 0.2;
    internal const double BusyOfPeak = 0.6;

    private readonly long _startBytes;
    private long _lastBytes;
    private double _lastActive;
    private double _peak;

    internal ShaderPrepareWatch(long startBytes)
    {
        _startBytes = startBytes;
        _lastBytes = startBytes;
    }

    /// <param name="seconds">Since the game started.</param>
    /// <param name="cacheBytes">The driver caches' size now.</param>
    /// <param name="cpuShare">The game's share of all processors since the last look; null when it can't be read.</param>
    internal Verdict Next(double seconds, long cacheBytes, double? cpuShare)
    {
        if (cpuShare is { } share) _peak = Math.Max(_peak, share);
        if (Math.Abs(cacheBytes - _lastBytes) >= QuietBytes || cpuShare >= Math.Max(BusyShare, _peak * BusyOfPeak)) _lastActive = seconds;
        _lastBytes = cacheBytes;
        if (seconds >= MaxSeconds) return Verdict.TooLong;
        var quiet = seconds - _lastActive;
        if (cacheBytes - _startBytes >= MinGrowthBytes) return quiet >= SettleSeconds ? Verdict.Settled : Verdict.Working;
        return seconds >= NothingSeconds && quiet >= SettleSeconds ? Verdict.NothingCompiled : Verdict.Working;
    }
}

/// <summary>
/// The Shaders page's Prepare: the wait on an Unreal game's first launch, done when you press it instead of when
/// you sit down to play. Exo starts the game the way Play does, watches <see cref="ShaderPrepareWatch"/> and closes it
/// the way Stop does. Only for games that run no anti-cheat: Exo never starts or closes a protected game on its own, and
/// Riot's games are out by <see cref="AntiCheatCheck"/>. It reads no game file and changes none. One
/// run at a time. Automatic is on until turned off on the page: while <see cref="PcIdle"/> says nobody is using the PC, it
/// runs a game that has never been played, or one prepared for an earlier driver (<see cref="WantsAuto"/>).
/// </summary>
internal sealed class ShaderPrepare(Func<LaunchOrchestrator> launcherOf)
{
    private static readonly TimeSpan Look = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartWait = TimeSpan.FromMinutes(3);
    /// Input within this long means someone is at the PC, and the game stays open.
    private static readonly TimeSpan IdleBeforeClose = TimeSpan.FromMinutes(3);
    private static readonly Lock Gate = new();

    private string? _running;
    private CancellationTokenSource? _cancel;
    private readonly Dictionary<string, string> _ended = new(StringComparer.Ordinal);

    internal event Action? Changed;

    // Built on first use: the bridge subscribes to Changed at startup, before the launcher is needed.
    private LaunchOrchestrator Launcher => launcherOf();

    private static string RecordsPath => Path.Combine(PathHelper.AppDataDir, "shaders-prepared.json");

    /// "running", "prepared", "stale" (prepared for another driver), "none" (nothing compiled at its start on this driver)
    /// or "ready"; message: how a run that failed ended.
    internal (string State, string? Message) StateOf(GameEntry game)
    {
        lock (Gate)
        {
            if (_running == game.Id) return ("running", null);
            if (_ended.TryGetValue(game.Id, out var ended)) return ("failed", ended);
        }
        if (ShaderWipeMarks.IsMarked(game.Id, null)) return ("stale", "Shaders need preparing again");
        if (!Saved().Games.TryGetValue(game.Id, out var record)) return ("ready", null);
        var current = DriverVersions();
        if (record == NothingFor(current)) return ("none", null);
        return (record == current ? "prepared" : "stale", null);
    }

    /// Whether automatic runs are on (until turned off on the page).
    internal bool Auto => Saved().Auto;

    internal void SetAuto(bool on)
    {
        lock (Gate) Save(Saved() with { Auto = on });
        Changed?.Invoke();
    }

    internal bool Busy { get { lock (Gate) return _running is not null; } }

    /// The game a run is building the shaders of now, or null.
    internal string? Running { get { lock (Gate) return _running; } }

    /// Exo started this game only to compile its shaders: nobody is playing it.
    internal bool Preparing(string gameId) { lock (Gate) return _running == gameId; }

    /// A run that never started (the game needs something first): shown, and not tried again this session.
    internal void Failed(string gameId, string message)
    {
        lock (Gate) _ended[gameId] = message;
        Changed?.Invoke();
    }

    /// Automatic runs only what it can help: a game never played, or one prepared for an earlier driver. A game you have
    /// played already went through its first launch, and one that failed stays until you press it.
    internal static bool WantsAuto(string state, int? playtimeMinutes) =>
        state == "stale" || state == "ready" && playtimeMinutes is null or 0;

    private static string NothingFor(string driver) => "none|" + driver;

    /// The compile's own check, once per game per driver: run when the PC is idle, and its answer stays in the engine.
    internal bool VerifiedOnThisDriver(string gameId) => Saved().Verified.TryGetValue(gameId, out var driver) && driver == DriverVersions();

    internal void NoteVerified(string gameId)
    {
        lock (Gate)
        {
            var store = Saved();
            store.Verified[gameId] = DriverVersions();
            Save(store);
        }
    }

    /// Why this game can't be prepared at all: the sheet shows no line.
    internal static bool Eligible(GameEntry game, AntiCheatCheck.Answer antiCheat, string? engineFamily = null) =>
        AntiCheatCheck.Allows(game, antiCheat, AntiCheatCheck.Change.ShaderPrepare)
        && GameProcessRegistry.SupportsGameProcessControl(game.Store)
        && ScsKillerStatus.SupportsFirstLaunchPrepare(game.Path, engineFamily);

    /// After the game was started: watches it to the end, off the caller. False when a run is already going.
    internal bool Start(GameEntry game, Action? onEnd = null)
    {
        var cancel = new CancellationTokenSource();
        lock (Gate)
        {
            if (_running is not null) { cancel.Dispose(); return false; }
            _running = game.Id;
            _cancel = cancel;
            _ended.Remove(game.Id);
        }
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            using var awake = KeepAwake.Hold("shader warming " + game.Title);
            using var compiling = ShaderCacheLock.Hold();
            string? message = null;
            var completed = false;
            try { message = await RunAsync(game, cancel.Token).ConfigureAwait(false); completed = message is null; }
            catch (OperationCanceledException) { AppLog.Info($"Prepare shaders: {game.Title} stopped by you; the game stays open."); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppLog.Warn($"Prepare shaders: {game.Title}: {error.Message}");
                message = "Could not finish";
            }
            lock (Gate)
            {
                _running = null;
                _cancel = null;
                if (message is not null) _ended[game.Id] = message;
            }
            if (completed) ShaderWipeMarks.Clear(game.Id);
            cancel.Dispose();
            Changed?.Invoke();
            onEnd?.Invoke();
        });
        return true;
    }

    internal void Cancel(string gameId)
    {
        lock (Gate)
            if (_running == gameId) _cancel?.Cancel();
    }

    /// Null when prepared; else the sheet's word for how it ended.
    private async Task<string?> RunAsync(GameEntry game, CancellationToken cancel)
    {
        var startBytes = await Task.Run(DriveSpace.DriverCacheBytes, cancel).ConfigureAwait(false);
        var started = Stopwatch.StartNew();
        while (!Launcher.GetGameRunState(game).IsRunning)
        {
            if (started.Elapsed > StartWait) return "Did not start";
            await Task.Delay(TimeSpan.FromSeconds(2), cancel).ConfigureAwait(false);
        }
        AppLog.Info($"Prepare shaders: {game.Title} started, driver caches at {startBytes / (1 << 20)} MB.");
        started.Restart();
        var watch = new ShaderPrepareWatch(startBytes);
        var cpu = new GameCpu(Launcher, game);
        while (true)
        {
            await Task.Delay(Look, cancel).ConfigureAwait(false);
            if (!Launcher.GetGameRunState(game).IsRunning)
            {
                AppLog.Info($"Prepare shaders: {game.Title} closed before its shaders settled.");
                return "Game closed early";
            }
            var bytes = DriveSpace.DriverCacheBytes();
            var verdict = watch.Next(started.Elapsed.TotalSeconds, bytes, cpu.Share());
            if (verdict == ShaderPrepareWatch.Verdict.Working) continue;
            AppLog.Info($"Prepare shaders: {game.Title} {verdict} after {started.Elapsed.TotalMinutes:0.0} min, +{(bytes - startBytes) / (1 << 20)} MB.");
            if (verdict == ShaderPrepareWatch.Verdict.Settled) Remember(game.Id, DriverVersions());
            if (verdict == ShaderPrepareWatch.Verdict.NothingCompiled) Remember(game.Id, NothingFor(DriverVersions()));
            // Someone at the PC may have started playing: a game in front with input just now is never closed under them. Used
            // for something else, the game they didn't ask to stay goes.
            if (PcIdle.InputIdle() < IdleBeforeClose && GameInFront(game))
            {
                AppLog.Info($"Prepare shaders: {game.Title} left open, the PC is in use.");
                return verdict == ShaderPrepareWatch.Verdict.TooLong ? "Took too long" : null;
            }
            var stop = await Launcher.StopGameAsync(game, cancel).ConfigureAwait(false);
            if (verdict == ShaderPrepareWatch.Verdict.TooLong) return "Took too long";
            return stop.Ok || stop.NothingRunning ? null : "Close the game";
        }
    }

    private bool GameInFront(GameEntry game)
    {
        try
        {
            using var front = Process.GetProcessById((int)ForegroundWatcher.ForegroundProcess());
            return Launcher.GameExecutable(game.Id) is not { } program ||
                front.ProcessName.Equals(Path.GetFileNameWithoutExtension(program), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return true; }
    }

    /// Every display driver's version: a new driver throws the cache away, whichever card it is for.
    private static string DriverVersions()
    {
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display is null) return "";
            var versions = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var name in display.GetSubKeyNames().Where(name => name.Length == 4 && name.All(char.IsAsciiDigit)))
            {
                using var adapter = display.OpenSubKey(name);
                if (adapter?.GetValue("DriverVersion") is string version) versions.Add(version.Trim());
            }
            return string.Join('|', versions);
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return ""; }
    }

    /// Automatic's choice and each game's record: the drivers it was prepared for, or "none|driver".
    private sealed record Store(bool Auto = true)
    {
        public Dictionary<string, string> Games { get; init; } = [];
        public Dictionary<string, string> Verified { get; init; } = [];
    }

    private static Store Saved()
    {
        try { return JsonSerializer.Deserialize<Store>(File.ReadAllText(RecordsPath)) ?? new Store(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new Store(); }
    }

    private static void Save(Store store)
    {
        Directory.CreateDirectory(PathHelper.AppDataDir);
        File.WriteAllText(RecordsPath, JsonSerializer.Serialize(store));
    }

    private static void Remember(string gameId, string record)
    {
        lock (Gate)
        {
            var store = Saved();
            store.Games[gameId] = record;
            Save(store);
        }
    }

    /// The game's share of all processors between looks, from its verified program's processes.
    private sealed class GameCpu(LaunchOrchestrator launcher, GameEntry game)
    {
        private TimeSpan? _used;
        private long _at;

        internal double? Share()
        {
            if (launcher.GameExecutable(game.Id) is not { } program) return null;
            var used = TimeSpan.Zero;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program)))
            {
                using (process)
                {
                    try { used += process.TotalProcessorTime; }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
                }
            }
            var now = Stopwatch.GetTimestamp();
            var share = _used is { } last && now > _at
                ? (used - last).TotalSeconds / Stopwatch.GetElapsedTime(_at, now).TotalSeconds / Environment.ProcessorCount
                : (double?)null;
            (_used, _at) = (used, now);
            return share;
        }
    }
}

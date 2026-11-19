using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Riot Client closing and visible relaunch could fail:
/// - RiotClientServices staying open in tray after ignoring /quit or soft-close, timing out account switch;
/// - closing or killing RiotClientServices while VALORANT/League starts mid-close;
/// - targeting Vanguard (vgc/vgk/vgtray) or a same-named non-Riot executable;
/// - relaunching RiotClientServices visibly without closing a running --headless or tray-only instance first;
/// - waiting a full 22.5s when a spawned store client crashes/exits early before opening a UI window.
/// </summary>
public sealed class RiotClientCloseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRiotCloseOps : WebHostBridge.IRiotCloseOps
    {
        public Func<string?> OnRunningGame { get; init; } = () => null;
        public Func<bool> OnRequestQuit { get; init; } = () => false;
        public Action<IReadOnlyList<string>>? OnSoftClose { get; init; }
        public Action<IReadOnlyList<string>>? OnKillProcesses { get; init; }
        public Func<bool> OnTryDeleteStaleLockfile { get; init; } = () => true;

        public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Lockfile { get; set; }
        public DateTimeOffset Now { get; set; } = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        public List<string> Steps { get; } = [];
        public TimeSpan TotalDelayed { get; private set; }
        public int QuitPollCount { get; private set; }

        public string? RunningGame() => OnRunningGame();

        public IReadOnlyList<string> RunningProcesses(IReadOnlyList<string> names) =>
            names.Where(Running.Contains).ToArray();

        public bool LockfileExists() => Lockfile;

        public Task<bool> RequestQuitAsync(CancellationToken ct)
        {
            Steps.Add("quit");
            return Task.FromResult(OnRequestQuit());
        }

        public void SoftClose(IReadOnlyList<string> names)
        {
            Steps.Add("soft:" + string.Join(",", names));
            OnSoftClose?.Invoke(names);
        }

        public void KillProcesses(IReadOnlyList<string> names)
        {
            Steps.Add("kill:" + string.Join(",", names));
            OnKillProcesses?.Invoke(names);
        }

        public bool TryDeleteStaleLockfile()
        {
            Steps.Add("delete-lockfile");
            if (!OnTryDeleteStaleLockfile()) return false;
            Lockfile = false;
            return true;
        }

        public DateTimeOffset UtcNow() => Now;

        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            QuitPollCount++;
            TotalDelayed += duration;
            Now += duration;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStoreClientOpenRuntime : IStoreClientOpenRuntime
    {
        public bool ExistsResult { get; set; } = true;
        public Func<int, bool>? RevealAt { get; set; }
        public Func<int, bool>? EarlyExitAt { get; set; }
        public int Checks { get; private set; }
        public int Delays { get; private set; }

        public bool Exists(StoreClientLaunchCommand command) => ExistsResult;
        public void AllowUserOpen(StoreKind store) { }
        public void Dispatch(StoreKind store, StoreClientLaunchCommand command) { }

        public bool RevealAndCheck(StoreClientLaunchCommand command, IReadOnlyList<string> processNames) =>
            RevealAt?.Invoke(Checks++) ?? false;

        public bool SpawnedExitedEarly(StoreClientLaunchCommand command, IReadOnlyList<string> processNames) =>
            EarlyExitAt?.Invoke(Checks - 1) ?? false;

        public Task Delay(CancellationToken ct)
        {
            Delays++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Close_succeeds_cleanly_on_request_quit_and_waits_for_lockfile_removal()
    {
        var ops = new FakeRiotCloseOps
        {
            OnRequestQuit = () => true,
        };
        ops.Running.Add("RiotClientServices");
        ops.Running.Add("Riot Client");
        ops.Lockfile = true;

        var polls = 0;
        var baseOps = new PollingRiotCloseOps(ops, () =>
        {
            polls++;
            if (polls >= 2) ops.Running.Clear();
            if (polls >= 3) ops.Lockfile = false;
        });

        string? progress = null;
        var error = await WebHostBridge.CloseRiotClientAsync(baseOps, m => progress = m, Ct);

        Assert.Null(error);
        Assert.NotNull(progress);
        Assert.Equal(["quit"], ops.Steps);
        Assert.False(ops.Lockfile);
    }

    [Fact]
    public async Task Unanswered_background_quit_waits_1s_soft_closes_including_services_then_kills_path_verified_riot_processes()
    {
        FakeRiotCloseOps? ops = null;
        ops = new FakeRiotCloseOps
        {
            OnRequestQuit = () => false,
            OnSoftClose = _ => { /* tray-only RiotClientServices ignores soft close */ },
            OnKillProcesses = names =>
            {
                Assert.Contains("RiotClientServices", names);
                Assert.Contains("Riot Client", names);
                Assert.Contains("RiotClientUx", names);
                Assert.Contains("RiotClientUxRender", names);
                Assert.Contains("RiotClientCrashHandler", names);
                ops!.Running.Clear();
            },
        };
        ops.Running.Add("RiotClientServices");
        ops.Lockfile = true;

        var error = await WebHostBridge.CloseRiotClientAsync(ops, null, Ct);

        Assert.Null(error);
        Assert.Equal("quit", ops.Steps[0]);
        Assert.StartsWith("soft:", ops.Steps[1], StringComparison.Ordinal);
        Assert.StartsWith("kill:", ops.Steps[2], StringComparison.Ordinal);
        Assert.Equal("delete-lockfile", ops.Steps[3]);
        Assert.InRange(ops.TotalDelayed, TimeSpan.FromSeconds(2.9), TimeSpan.FromSeconds(3.5));
        Assert.False(ops.Lockfile);
    }

    [Fact]
    public async Task Active_game_aborts_up_front_and_aborts_immediately_before_process_termination()
    {
        var upFrontOps = new FakeRiotCloseOps { OnRunningGame = () => "VALORANT" };
        upFrontOps.Running.Add("RiotClientServices");

        var upFrontError = await WebHostBridge.CloseRiotClientAsync(upFrontOps, null, Ct);
        Assert.Contains("VALORANT", upFrontError);
        Assert.Empty(upFrontOps.Steps);

        var gameStartedLate = false;
        var midCloseOps = new FakeRiotCloseOps
        {
            OnRunningGame = () => gameStartedLate ? "League of Legends" : null,
            OnRequestQuit = () => false,
            OnSoftClose = _ => gameStartedLate = true,
        };
        midCloseOps.Running.Add("RiotClientServices");

        var midCloseError = await WebHostBridge.CloseRiotClientAsync(midCloseOps, null, Ct);
        Assert.Contains("League of Legends", midCloseError);
        Assert.DoesNotContain(midCloseOps.Steps, s => s.StartsWith("kill:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refuses_if_processes_remain_alive_past_5s_post_kill_timeout()
    {
        var ops = new FakeRiotCloseOps
        {
            OnRequestQuit = () => false,
            // RiotClientServices ignores everything and stays alive.
        };
        ops.Running.Add("RiotClientServices");

        var error = await WebHostBridge.CloseRiotClientAsync(ops, null, Ct);

        Assert.NotNull(error);
        Assert.Contains("tray icon", error, StringComparison.OrdinalIgnoreCase);
        // 1s quit + 2s soft-close + 5s post-kill wait = 8s (never 25.9s)
        Assert.Equal(TimeSpan.FromSeconds(8), ops.TotalDelayed);
    }

    [Theory]
    [InlineData("vgc", @"C:\Program Files\Riot Vanguard\vgc.exe", true, false)]
    [InlineData("vgk", @"C:\Program Files\Riot Vanguard\vgk.sys", true, false)]
    [InlineData("vgtray", @"C:\Program Files\Riot Vanguard\vgtray.exe", true, false)]
    [InlineData("VALORANT-Win64-Shipping", @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", true, false)]
    [InlineData("RiotClientServices", @"C:\Temp\Spoof\RiotClientServices.exe", false, false)]
    [InlineData("RiotClientServices", @"C:\Riot Games\Riot Client\RiotClientServices.exe", true, true)]
    [InlineData("Riot Client", @"C:\Riot Games\Riot Client\Riot Client.exe", true, true)]
    [InlineData("RiotClientUx", @"C:\Riot Games\Riot Client\RiotClientUx.exe", true, true)]
    [InlineData("RiotClientUxRender", @"C:\Riot Games\Riot Client\RiotClientUxRender.exe", true, true)]
    [InlineData("RiotClientCrashHandler", @"C:\Riot Games\Riot Client\RiotClientCrashHandler.exe", true, true)]
    public void Termination_guard_never_touches_vanguard_games_or_unverified_paths(
        string name, string exePath, bool belongsToRiot, bool expected)
    {
        Assert.Equal(expected, WebHostBridge.CanTerminateRiotClientProcess(name, exePath, _ => belongsToRiot));
    }

    [Theory]
    [InlineData(true, true, true, true, false, false)]   // Active Riot game => never close
    [InlineData(false, false, false, false, false, false)] // Not running => cold start visibly
    [InlineData(false, true, true, false, false, true)]  // Tracked headless => close cleanly first
    [InlineData(false, false, true, true, false, true)]  // --headless cmdline => close cleanly first
    [InlineData(false, false, true, false, false, true)] // Tray-only RiotClientServices (no UI) => close cleanly first
    [InlineData(false, false, true, false, true, false)] // Visible Electron UI already up => reveal directly
    public void Headless_or_tray_only_decision_closes_before_visible_launch(
        bool runningGame, bool trackedHeadless, bool servicesRunning, bool hasHeadlessArg, bool anyUiRunning, bool expected)
    {
        Assert.Equal(expected, WebHostBridge.NeedsRestartForVisibleLaunch(
            runningGame, trackedHeadless, servicesRunning, hasHeadlessArg, anyUiRunning));
    }

    [Fact]
    public async Task StoreClientOpener_waits_past_15s_for_cold_electron_and_fails_fast_on_early_exit()
    {
        var cold = new FakeStoreClientOpenRuntime { RevealAt = attempt => attempt >= 35 };
        var coldResult = await StoreClientOpener.OpenAsync(
            StoreKind.Riot, "Riot Client", new StoreClientLaunchCommand(@"C:\Riot Games\Riot Client\RiotClientServices.exe"),
            StoreWindowHider.RiotUiProcessNames, cold, Ct);
        Assert.True(coldResult.Ok);
        Assert.Equal(36, cold.Checks);
        Assert.Equal(45, StoreClientOpener.DefaultAttempts);

        var dead = new FakeStoreClientOpenRuntime { EarlyExitAt = attempt => attempt >= 1 };
        var deadResult = await StoreClientOpener.OpenAsync(
            StoreKind.Riot, "Riot Client", new StoreClientLaunchCommand(@"C:\Riot Games\Riot Client\RiotClientServices.exe"),
            StoreWindowHider.RiotUiProcessNames, dead, Ct);
        Assert.False(deadResult.Ok);
        Assert.Equal(2, dead.Checks);
        Assert.Equal(1, dead.Delays);
    }

    private sealed class PollingRiotCloseOps(FakeRiotCloseOps inner, Action onDelay) : WebHostBridge.IRiotCloseOps
    {
        public string? RunningGame() => inner.RunningGame();
        public IReadOnlyList<string> RunningProcesses(IReadOnlyList<string> names) => inner.RunningProcesses(names);
        public bool LockfileExists() => inner.LockfileExists();
        public Task<bool> RequestQuitAsync(CancellationToken ct) => inner.RequestQuitAsync(ct);
        public void SoftClose(IReadOnlyList<string> names) => inner.SoftClose(names);
        public void KillProcesses(IReadOnlyList<string> names) => inner.KillProcesses(names);
        public bool TryDeleteStaleLockfile() => inner.TryDeleteStaleLockfile();
        public DateTimeOffset UtcNow() => inner.UtcNow();
        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            onDelay();
            return inner.Delay(duration, ct);
        }
    }
}

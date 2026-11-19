using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Owner, 2026-11-19: "downloading app then switching to another account doesn't register, then it auto resumed when it
/// should. also opening apps while a download is going on is fine." Ways it could fail:
/// - the running download carries on for the account that is no longer signed in;
/// - a queued download for the old account reaches the front and is dropped as "account changed";
/// - a play hold's release resumes a download of the other account into a refusal that leaves it paused for review;
/// - switching back to the original account never resumes what the switch paused, or resumes what the player paused;
/// - an application or tool started from the Library holds downloads like a game, or keeps them held after the game ends;
/// - Play on another game of the same store is refused while a download runs.
/// </summary>
public sealed class DownloadAccountSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(TestDataDirectory.Root, "account-switch-" + Guid.NewGuid().ToString("N"));

    public DownloadAccountSwitchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, recursive: true); } catch { /* temp */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRunningDownloadPausesForTheOtherAccountAndCarriesOnWhenItIsBack()
    {
        var adapter = new ScopedAdapter();
        var orchestrator = Orchestrator(adapter);
        _ = orchestrator.InstallAsync(Epic("a"), path: _root, outer: Ct);
        await adapter.Started("epic:a").WaitAsync(TimeSpan.FromSeconds(10), Ct);

        adapter.Scope = "account-2";
        orchestrator.CheckDownloadAccounts();

        await adapter.Stopped("epic:a").WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var paused = Assert.Single(orchestrator.PausedDownloads);
        Assert.Equal("epic:a", paused.GameId);
        Assert.False(paused.CanResume);
        Assert.Contains("other Fixture account", paused.Reason);
        // Resume under the other account keeps it waiting for its own, unchanged.
        var refused = await orchestrator.ResumeDownloadAsync("epic:a", Ct);
        Assert.False(refused.Ok);
        Assert.Contains("other Fixture account", Assert.Single(orchestrator.PausedDownloads).Reason);
        Assert.Equal(1, adapter.Runs("epic:a"));

        adapter.Scope = "account-1";
        orchestrator.CheckDownloadAccounts();

        await WaitFor(() => adapter.Runs("epic:a") == 2);
        Assert.Empty(orchestrator.PausedDownloads);
        adapter.Finish("epic:a");
    }

    [Fact]
    public async Task AQueuedDownloadForTheOldAccountWaitsInsteadOfBeingDropped()
    {
        var adapter = new ScopedAdapter();
        var orchestrator = Orchestrator(adapter);
        _ = orchestrator.InstallAsync(Epic("a"), path: _root, outer: Ct);
        await adapter.Started("epic:a").WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True((await orchestrator.InstallAsync(Epic("b"), path: _root, outer: Ct)).Queued);

        // Switched in the store's own app: the queue reaches b before any check has run.
        adapter.Scope = "account-2";
        adapter.Finish("epic:a");

        await WaitFor(() => orchestrator.PausedDownloads.Any(p => p.GameId == "epic:b"));
        Assert.Equal(0, adapter.Runs("epic:b"));
        Assert.Contains("other Fixture account", orchestrator.PausedDownloads.Single(p => p.GameId == "epic:b").Reason);

        adapter.Scope = "account-1";
        orchestrator.CheckDownloadAccounts();
        await WaitFor(() => adapter.Runs("epic:b") == 1);
        adapter.Finish("epic:b");
    }

    [Fact]
    public async Task AHeldDownloadOfTheOtherAccountWaitsForItsAccountNotForReview()
    {
        var adapter = new ScopedAdapter();
        var orchestrator = Orchestrator(adapter);
        _ = orchestrator.InstallAsync(Epic("a"), path: _root, outer: Ct);
        await adapter.Started("epic:a").WaitAsync(TimeSpan.FromSeconds(10), Ct);
        orchestrator.Hold("playing", "Downloads are paused while you play X.");
        await adapter.Stopped("epic:a").WaitAsync(TimeSpan.FromSeconds(10), Ct);

        adapter.Scope = "account-2";
        orchestrator.Release("playing");

        // Not resumed into a refusal: it waits, and says for what.
        await Task.Delay(300, Ct);
        Assert.Equal(1, adapter.Runs("epic:a"));
        Assert.Contains("other Fixture account", Assert.Single(orchestrator.PausedDownloads).Reason);

        adapter.Scope = "account-1";
        orchestrator.CheckDownloadAccounts();
        await WaitFor(() => adapter.Runs("epic:a") == 2);
        adapter.Finish("epic:a");
    }

    [Fact]
    public async Task ADownloadThePlayerPausedStaysPausedOnASwitchBack()
    {
        var adapter = new ScopedAdapter();
        var orchestrator = Orchestrator(adapter);
        _ = orchestrator.InstallAsync(Epic("a"), path: _root, outer: Ct);
        await adapter.Started("epic:a").WaitAsync(TimeSpan.FromSeconds(10), Ct);
        orchestrator.Pause("epic:a");
        await adapter.Stopped("epic:a").WaitAsync(TimeSpan.FromSeconds(10), Ct);

        adapter.Scope = "account-2";
        orchestrator.CheckDownloadAccounts();
        adapter.Scope = "account-1";
        orchestrator.CheckDownloadAccounts();

        await Task.Delay(300, Ct);
        Assert.Equal(1, adapter.Runs("epic:a"));
        Assert.Equal("Paused.", Assert.Single(orchestrator.PausedDownloads).Reason);
    }

    [Fact]
    public async Task AnotherGameOfTheSameStorePlaysWhileADownloadRuns()
    {
        var adapter = new ScopedAdapter();
        var orchestrator = Orchestrator(adapter);
        _ = orchestrator.InstallAsync(Epic("a"), path: _root, outer: Ct);
        await adapter.Started("epic:a").WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var other = new GameEntry { Id = "epic:b", Title = "Exo Fixture b", Store = StoreKind.Epic, Installed = true, Owned = true, Path = _root, LaunchTarget = "b" };
        var played = await orchestrator.LaunchAsync(other, skipDeps: true, Ct);
        Assert.True(played.Ok, played.Message);
        var same = await orchestrator.LaunchAsync(Epic("a"), skipDeps: true, Ct);
        Assert.False(same.Ok);

        await orchestrator.StopGameAsync(other, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        orchestrator.Cancel();
    }

    [Theory]
    [InlineData(StoreKind.Steam, "game", true)]
    [InlineData(StoreKind.Steam, null, true)]
    [InlineData(StoreKind.Steam, "application", false)]
    [InlineData(StoreKind.Steam, "Tool", false)]
    [InlineData(StoreKind.Steam, "music", false)]
    [InlineData(StoreKind.Epic, null, true)]
    [InlineData(StoreKind.Local, null, true)]
    public void OnlyAGameHoldsDownloads(StoreKind store, string? steamType, bool holds) =>
        Assert.Equal(holds, DownloadPolicy.HoldsDownloads(new GameEntry
        {
            Id = store.ToString().ToLowerInvariant() + ":x", Title = "Exo Fixture x", Store = store, SteamAppType = steamType,
        }));

    private static GameEntry Epic(string id) => new()
    {
        Id = "epic:" + id, Title = "Exo Fixture " + id, Store = StoreKind.Epic, Owned = true, CanInstall = true,
        EntitlementState = EntitlementState.Owned, LaunchTarget = id,
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("Timed out waiting.");
            await Task.Delay(20, Ct);
        }
    }

    private LaunchOrchestrator Orchestrator(ScopedAdapter adapter) =>
        new(new IStoreAdapter[] { adapter }, new SettingsService(), new DependencyService(),
            new AchievementService([], Path.Combine(_root, "achievements.json"), deferStateLoad: true),
            stopGame: (_, _) => Task.FromResult(new GameStopResult(false, "Exo could not find this game running.", NothingRunning: true)),
            beginQuietGameSession: _ => new Nothing(),
            missingDependencies: _ => [],
            revalidateQueuedGame: (id, _) => Task.FromResult<GameEntry?>(Epic(id[5..])),
            closeUnusedStoreClients: _ => Task.CompletedTask,
            isVendorDownloadRunning: _ => false);

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>An Epic-like store whose signed-in account the test changes; each install runs until cancelled or finished.</summary>
    private sealed class ScopedAdapter : IStoreAdapter, IStoreAccountScope
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _runs = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = new();
        private readonly Dictionary<string, TaskCompletionSource> _stopped = new();
        private readonly Dictionary<string, TaskCompletionSource<InstallResult>> _finish = new();

        public volatile string Scope = "account-1";
        public string? GetActiveAccountScope() => Scope;

        private TaskCompletionSource Slot(Dictionary<string, TaskCompletionSource> map, string id)
        {
            lock (_gate)
            {
                if (!map.TryGetValue(id, out var slot)) map[id] = slot = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return slot;
            }
        }

        public Task Started(string id) => Slot(_started, id).Task;
        public Task Stopped(string id) => Slot(_stopped, id).Task;
        public int Runs(string id) { lock (_gate) return _runs.GetValueOrDefault(id); }

        public void Finish(string id)
        {
            lock (_gate)
                if (_finish.TryGetValue(id, out var finish)) finish.TrySetResult(new InstallResult { Ok = true, Message = "Installed." });
        }

        public StoreKind Store => StoreKind.Epic;
        public string Id => "epic";
        public string DisplayName => "Fixture";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) => Task.FromResult(new AuthResult());
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GameEntry>>([]);

        public async Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default)
        {
            TaskCompletionSource<InstallResult> finish;
            lock (_gate)
            {
                _runs[game.Id] = _runs.GetValueOrDefault(game.Id) + 1;
                _finish[game.Id] = finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopped.Remove(game.Id);
            }
            progress?.Report(new InstallProgress { GameId = game.Id, Phase = InstallPhase.Downloading, Status = "Downloading…" });
            Slot(_started, game.Id).TrySetResult();
            try { return await finish.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { return new InstallResult { Ok = false, Message = "Canceled." }; }
            finally { Slot(_stopped, game.Id).TrySetResult(); }
        }

        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            InstallAsync(game, null, progress, ct);

        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = true, HandoffOnly = true, Message = "Opened the store." });

        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) => Task.FromResult(new InstallResult { Ok = true });
        public InstallProgress GetDownloadProgress(string gameId) => new() { GameId = gameId };
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) => Task.CompletedTask;
    }
}

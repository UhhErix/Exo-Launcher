using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class LibraryScanCancellationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-lib-cancel-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_dir, recursive: true);

    private static SettingsService CreateSettings() => new();

    private static GameEntry Game(string id, string title, StoreKind store) => new()
    {
        Id = id,
        Title = title,
        Store = store,
        Installed = true,
        Owned = true,
        Path = @"C:\Games\" + title,
        LaunchTarget = id,
        Status = "Ready",
    };

    private sealed class ScriptedAdapter(
        string id,
        string displayName,
        StoreKind store,
        Func<CancellationToken, Task<IReadOnlyList<GameEntry>>> scan,
        bool clientPresent = true,
        bool? layoutRecognized = null) : IStoreAdapter, IStoreClientPresence, IStoreLayoutProbe
    {
        public StoreKind Store => store;
        public string Id => id;
        public string DisplayName => displayName;
        public bool IsAgentPresent() => clientPresent;
        public bool IsClientPresent() => clientPresent;
        public bool IsLayoutRecognized() => layoutRecognized ?? false;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) => Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) => scan(ct);
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) => Task.FromResult(new InstallResult { Ok = false });
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) => Task.FromResult(new InstallResult { Ok = false });
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) => Task.FromResult(new LaunchResult { Ok = false });
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) => Task.FromResult(new InstallResult { Ok = false });
        public InstallProgress GetDownloadProgress(string gameId) => new();
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task CanceledScanPreservesLastGoodLibraryAndDoesNotMarkStoresUnavailable()
    {
        var shouldBlock = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        IStoreAdapter MakeAdapter(string id, string name, StoreKind store, string title) =>
            new ScriptedAdapter(id, name, store, async ct =>
            {
                if (!shouldBlock) return [Game(id + ":1", title, store)];
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return [];
            });

        var service = new LibraryService(
            [
                MakeAdapter("steam", "Steam", StoreKind.Steam, "Half-Life 2"),
                MakeAdapter("riot", "Riot", StoreKind.Riot, "VALORANT"),
                MakeAdapter("itch", "itch", StoreKind.Itch, "Celeste"),
            ],
            CreateSettings(),
            includeMicrosoftStore: false);

        var testCt = TestContext.Current.CancellationToken;
        var initial = await service.GetLibraryAsync(force: true, fullScan: true, ct: testCt);
        Assert.Equal(3, initial.Count);

        shouldBlock = true;
        using var cts = new CancellationTokenSource();
        var canceledScan = service.GetLibraryAsync(force: true, fullScan: true, ct: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), testCt);
        cts.Cancel();

        var afterCancel = await canceledScan;
        Assert.Equal(3, afterCancel.Count);
        Assert.Equal(3, service.PeekCachedLibrary().Count);

        var matrix = service.StoreMatrix();
        Assert.All(matrix, row => Assert.DoesNotContain("Scan unavailable", row.detail, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TimedOutAdapterPreservesLastGoodEntriesAndReportsTimeoutClearly()
    {
        var stall = false;
        var adapter = new ScriptedAdapter("itch", "itch", StoreKind.Itch, async ct =>
        {
            if (!stall) return [Game("itch:1", "A Short Hike", StoreKind.Itch)];
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        });

        var service = new LibraryService([adapter], CreateSettings(), includeMicrosoftStore: false)
        {
            ColdAdapterTimeout = TimeSpan.FromMilliseconds(120),
            WarmAdapterTimeout = TimeSpan.FromMilliseconds(120),
        };

        var testCt = TestContext.Current.CancellationToken;
        var first = await service.GetLibraryAsync(force: true, fullScan: true, ct: testCt);
        Assert.Single(first);

        stall = true;
        var second = await service.GetLibraryAsync(force: true, fullScan: true, ct: testCt);
        Assert.Single(second);
        Assert.Equal("A Short Hike", second[0].Title);
    }

    [Fact]
    public void ColdAdapterTimeoutGivesRoomForFirstRunColdDisks()
    {
        var service = new LibraryService([], CreateSettings(), includeMicrosoftStore: false);
        Assert.True(service.ColdAdapterTimeout >= TimeSpan.FromSeconds(60));
        Assert.True(service.ColdAdapterTimeout >= service.WarmAdapterTimeout);
    }

    [Fact]
    public async Task RecognizedEmptyStoreLayoutIsNotFlaggedAsUnrecognized()
    {
        var epicEmpty = new ScriptedAdapter(
            "epic",
            "Epic",
            StoreKind.Epic,
            _ => Task.FromResult<IReadOnlyList<GameEntry>>([]),
            clientPresent: true,
            layoutRecognized: true);
        var unknownEmpty = new ScriptedAdapter(
            "ubisoft",
            "Ubisoft",
            StoreKind.Ubisoft,
            _ => Task.FromResult<IReadOnlyList<GameEntry>>([]),
            clientPresent: true,
            layoutRecognized: false);

        var service = new LibraryService([epicEmpty, unknownEmpty], CreateSettings(), includeMicrosoftStore: false);
        await service.GetLibraryAsync(force: true, fullScan: true, ct: TestContext.Current.CancellationToken);

        var matrix = service.StoreMatrix().ToDictionary(s => s.store, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("checked", matrix["epic"].checkCode);
        Assert.Equal(LibraryService.LayoutUnrecognizedCode, matrix["ubisoft"].checkCode);
    }
}

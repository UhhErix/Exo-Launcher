using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Exo's own update around a game, a full disk and a restart, through the real AppUpdateService with a stub update address
/// (no network), a fake installer (bytes only; its start is recorded, never run) and a fake game. Ways it could fail:
/// - Update now or Try again during a game starts the installer, which closes Exo (and what it runs in the game);
/// - the update downloaded during a game is thrown away and downloaded again for the next Update now;
/// - the kept installer is run at the next start while a game runs, or when it is stale (this version or older), changed on
///   disk, or not an Exo installer, or its record keeps it running at every start;
/// - a download starts on a drive without room for it, so it fails after the whole transfer;
/// - the installer's startup receipt names another version string than the one the installer compares (a suffix rolled back
///   every update).
/// </summary>
public sealed class AppUpdateSafetyTests : IDisposable
{
    private const string Remote = "9.9.9";
    private static readonly byte[] Installer = Enumerable.Range(0, 8192).Select(i => (byte)(i * 7)).ToArray();
    private static readonly string InstallerSha = Convert.ToHexString(SHA256.HashData(Installer)).ToLowerInvariant();
    private readonly string _updates = Path.Combine(Path.GetTempPath(), "exo-update-safety-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_updates, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class Address : HttpMessageHandler
    {
        public int InstallerAsked;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.EndsWith("/latest", StringComparison.Ordinal))
            {
                var body = JsonSerializer.Serialize(new { app = "launcher", tag = "v" + Remote, checks = new { setup = new { size = Installer.Length, sha256 = InstallerSha } } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
            if (url.EndsWith("/ExoLauncher-Setup.exe", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref InstallerAsked);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Rig
    {
        public bool Game;
        public long Free = long.MaxValue;
        public readonly List<string> Started = [];
    }

    private AppUpdateService Service(Address address, Rig rig)
    {
#pragma warning disable RS0030 // Stub handler: no request leaves the process.
        var http = new HttpClient(address);
#pragma warning restore RS0030
        return new AppUpdateService(http, _updates, TimeProvider.System)
        {
            GameRunning = () => rig.Game,
            FreeBytes = _ => rig.Free,
            AppFolderBytes = () => 500L * 1024 * 1024,
            IsInstaller = _ => true,
            StartInstaller = path => { rig.Started.Add(path); return true; },
        };
    }

    [Fact]
    public async Task UpdateNowDuringAGameDownloadsAndWaitsInsteadOfInstalling()
    {
        var rig = new Rig { Game = true };
        var service = Service(new Address(), rig);
        var result = await service.InstallAsync("1.0.0", null, TestContext.Current.CancellationToken);
        Assert.True(result.Staged);
        Assert.False(result.Installed);
        Assert.False(result.ShouldExit);
        Assert.Equal(AppUpdateService.WaitsForGameMessage, result.Message);
        Assert.Empty(rig.Started);
        Assert.Equal(UpdateFlow.State.WaitingForGame, service.State);
        var staged = AppUpdateService.ReadStaged(_updates);
        Assert.NotNull(staged);
        Assert.Equal(Remote, staged.Version);
        Assert.Equal(Installer, File.ReadAllBytes(Path.Combine(_updates, staged.File)));
    }

    [Fact]
    public async Task AfterTheGameTheKeptInstallerGoesInWithoutASecondDownload()
    {
        var rig = new Rig { Game = true };
        var address = new Address();
        var service = Service(address, rig);
        Assert.True((await service.InstallAsync("1.0.0", null, TestContext.Current.CancellationToken)).Staged);
        Assert.Equal(1, address.InstallerAsked);
        // Try again while the game still runs: still kept, nothing started, nothing downloaded again.
        Assert.True((await service.InstallAsync("1.0.0", null, TestContext.Current.CancellationToken)).Staged);
        Assert.Empty(rig.Started);
        rig.Game = false;
        service.GameChanged();
        Assert.Equal(UpdateFlow.State.Ready, service.State);
        var result = await service.InstallAsync("1.0.0", null, TestContext.Current.CancellationToken);
        Assert.True(result.ShouldExit);
        Assert.Single(rig.Started);
        Assert.Equal(1, address.InstallerAsked);
        Assert.Equal(UpdateFlow.State.Restarting, service.State);
        // Started once: the record keeps the count, so a rolled-back installer runs at most once more.
        Assert.Equal(1, AppUpdateService.ReadStaged(_updates)!.Starts);
    }

    [Fact]
    public async Task WithNoGameUpdateNowInstallsAtOnce()
    {
        var rig = new Rig();
        var service = Service(new Address(), rig);
        var result = await service.InstallAsync("1.0.0", null, TestContext.Current.CancellationToken);
        Assert.True(result.ShouldExit);
        Assert.Single(rig.Started);
        Assert.Equal(1, AppUpdateService.ReadStaged(_updates)!.Starts);
    }

    [Fact]
    public async Task ADriveWithoutRoomDownloadsNothingAndSaysHowMuchIsNeeded()
    {
        var rig = new Rig { Free = 100L * 1024 * 1024 };
        var address = new Address();
        var service = Service(address, rig);
        var result = await service.InstallAsync("1.0.0", null, TestContext.Current.CancellationToken);
        Assert.False(result.Installed);
        Assert.Equal(0, address.InstallerAsked);
        Assert.Empty(rig.Started);
        Assert.Matches(@"^Exo needs \d+\.\d GB free on drive [A-Z] to update\.$", result.Message);
        Assert.Equal(UpdateFlow.State.Failed, service.State);
    }


    // ---- the next start (ApplyKeptAtStartAsync) ----

    private string Stage(string version, byte[]? bytes = null, int starts = 0)
    {
        Directory.CreateDirectory(_updates);
        var file = $"ExoLauncher-Setup-{Guid.NewGuid():N}.exe";
        File.WriteAllBytes(Path.Combine(_updates, file), bytes ?? Installer);
        File.WriteAllText(Path.Combine(_updates, AppUpdateService.StagedRecordName),
            JsonSerializer.Serialize(new { schema = 1, version, sha256 = InstallerSha, size = Installer.Length, file, starts }));
        return Path.Combine(_updates, file);
    }

    private sealed class Start
    {
        public bool Game;
        public string? Released = InstallerSha;
        public bool Takes = true;
        public readonly List<string> Started = [];
        public readonly List<string> Events = [];
        public int? StartsSeenAtStart;
    }

    private AppUpdateService AtStart(Start start, Func<string, bool>? isInstaller = null)
    {
#pragma warning disable RS0030 // No request is made: the release's SHA-256 is injected.
        var http = new HttpClient(new Address());
#pragma warning restore RS0030
        return new AppUpdateService(http, _updates, TimeProvider.System)
        {
            GameRunning = () => start.Game,
            GameRunningAtStart = () => Task.FromResult(start.Game),
            InstallFolder = InstallRoot,
            IsInstaller = isInstaller ?? (_ => true),
            ReleaseSha256 = (_, _) => Task.FromResult(start.Released),
            StartInstaller = path =>
            {
                start.StartsSeenAtStart = AppUpdateService.ReadStaged(_updates)?.Starts;
                start.Events.Add("installer");
                start.Started.Add(path);
                return start.Takes;
            },
        };
    }

    [Fact]
    public async Task TheNextStartRunsTheKeptInstallerOnlyWithNoGameAndAtMostTwice()
    {
        var installer = Stage(Remote);
        var start = new Start { Game = true };
        Assert.False(await AtStart(start).ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
        Assert.Empty(start.Started);
        Assert.Equal(0, AppUpdateService.ReadStaged(_updates)!.Starts);
        start.Game = false;
        Assert.True(await AtStart(start).ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
        // The start is counted on disk before the installer runs.
        Assert.Equal(1, start.StartsSeenAtStart);
        // The update did not take (still 1.0.0): once more, then never again, and the next update downloads anew.
        Assert.True(await AtStart(start).ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
        Assert.Equal(2, start.StartsSeenAtStart);
        Assert.False(await AtStart(start).ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
        Assert.Equal([installer, installer], start.Started);
        Assert.Null(AppUpdateService.ReadStaged(_updates));
        Assert.False(File.Exists(installer));
    }

    [Fact]
    public async Task AReleaseTheUpdateAddressNowGivesAnotherHashForIsRemovedNotRun()
    {
        var installer = Stage(Remote);
        var start = new Start { Released = new string('a', 64) };
        Assert.False(await AtStart(start).ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
        Assert.Empty(start.Started);
        Assert.False(File.Exists(installer));
    }

    [Fact]
    public async Task OfflineTheKeptRecordStands()
    {
        Stage(Remote);
        var start = new Start { Released = null };
        Assert.True(await AtStart(start).ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
        Assert.Single(start.Started);
    }

    [Theory]
    [InlineData(Remote)]
    [InlineData("10.0.0")]
    public async Task AKeptInstallerThisVersionAlreadyHasIsRemovedNotRun(string running)
    {
        var installer = Stage(Remote);
        var start = new Start();
        Assert.False(await AtStart(start).ApplyKeptAtStartAsync(running, TestContext.Current.CancellationToken));
        Assert.Empty(start.Started);
        Assert.False(File.Exists(installer));
        Assert.Null(AppUpdateService.ReadStaged(_updates));
    }

    [Fact]
    public async Task AKeptInstallerChangedOnDiskIsRemovedNotRun()
    {
        var changed = (byte[])Installer.Clone();
        changed[100] ^= 0xff;
        var installer = Stage(Remote, changed);
        var start = new Start();
        Assert.False(await AtStart(start).ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
        Assert.Empty(start.Started);
        Assert.False(File.Exists(installer));
    }

    [Fact]
    public async Task AKeptFileThatIsNotAnExoInstallerIsRemovedNotRun()
    {
        var installer = Stage(Remote);
        var start = new Start();
        Assert.False(await AtStart(start, _ => false).ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
        Assert.Empty(start.Started);
        Assert.False(File.Exists(installer));
    }

    [Fact]
    public async Task UpdateNowCountsTheStartOnDiskBeforeTheInstallerRuns()
    {
        var rig = new Rig();
        int? seen = null;
#pragma warning disable RS0030 // Stub handler: no request leaves the process.
        var http = new HttpClient(new Address());
#pragma warning restore RS0030
        var service = new AppUpdateService(http, _updates, TimeProvider.System)
        {
            GameRunning = () => false,
            FreeBytes = _ => long.MaxValue,
            AppFolderBytes = () => 0,
            IsInstaller = _ => true,
            StartInstaller = _ => { seen = AppUpdateService.ReadStaged(_updates)?.Starts; return true; },
        };
        Assert.True((await service.InstallAsync("1.0.0", null, TestContext.Current.CancellationToken)).ShouldExit);
        Assert.Equal(1, seen);
    }

    private string InstallRoot => Path.Combine(_updates, "install");

    private void Outcome(string json)
    {
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllText(Path.Combine(InstallRoot, UpdateOutcome.ResultFile), json);
    }

    [Theory]
    [InlineData("rolled-back")]
    [InlineData("checking-startup")]
    public async Task AnUpdateWhoseLastTryDidNotTakeIsNotRunAgainByItsOwnRollback(string status)
    {
        // The rollback restarts the old Exo, marked as Setup's own start: it must not run the same installer again.
        var installer = Stage(Remote, starts: 1);
        Outcome($$"""{"schema":1,"version":"{{Remote}}","status":"{{status}}"}""");
        var start = new Start();
        var service = AtStart(start);
        Assert.False(await service.ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
        Assert.Empty(start.Started);
        Assert.False(File.Exists(installer));
        Assert.Null(AppUpdateService.ReadStaged(_updates));
        // Not offered again by itself.
        Assert.Equal(Remote, service.SkippedVersion);
    }

    [Fact]
    public void AnEngineFailureBeforeTheSwapLeavesTheKeptUpdateAndSkipsNothing()
    {
        // The folder was in use: only an error note, no result. The installer worked on nothing, so the starts counter
        // (not a skip) decides how often it is tried.
        var installer = Stage(Remote, starts: 1);
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllText(Path.Combine(InstallRoot, UpdateOutcome.ErrorFile), "the folder is in use");
        var service = AtStart(new Start());
        Assert.True(service.SettleKeptUpdate("1.0.0"));
        Assert.True(File.Exists(installer));
        Assert.NotNull(AppUpdateService.ReadStaged(_updates));
        Assert.Null(service.SkippedVersion);
    }

    [Fact]
    public void AHalfStoppedResultAloneLeavesTheKeptUpdateToTheStartsCounter()
    {
        Stage(Remote, starts: 1);
        Outcome($$"""{"schema":1,"version":"{{Remote}}","status":"failed-preserved"}""");
        var service = AtStart(new Start());
        Assert.True(service.SettleKeptUpdate("1.0.0"));
        Assert.Null(service.SkippedVersion);
    }

    [Fact]
    public void AnotherVersionsFailureLeavesTheKeptUpdateAlone()
    {
        Stage(Remote);
        Outcome("""{"schema":1,"version":"5.0.0","status":"rolled-back"}""");
        Assert.True(AtStart(new Start()).SettleKeptUpdate("1.0.0"));
    }

    [Fact]
    public void AKeptUpdateForTheVersionAlreadyReachedGoesBeforeAnythingShows()
    {
        var installer = Stage(Remote, starts: 1);
        var start = new Start();
        Assert.False(AtStart(start).SettleKeptUpdate(Remote));
        Assert.False(File.Exists(installer));
        Assert.Null(AppUpdateService.ReadStaged(_updates));
        Assert.Empty(start.Events);
    }

    [Fact]
    public async Task AReadOnlyRecordStopsNothing()
    {
        Stage(Remote);
        var record = Path.Combine(_updates, AppUpdateService.StagedRecordName);
        File.SetAttributes(record, FileAttributes.ReadOnly);
        var start = new Start();
        Assert.True(await AtStart(start).ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
        Assert.Equal(1, AppUpdateService.ReadStaged(_updates)!.Starts);
        // And once that version is reached, the read-only record still goes.
        File.SetAttributes(record, FileAttributes.ReadOnly);
        Assert.False(AtStart(start).SettleKeptUpdate(Remote));
        Assert.False(File.Exists(record));
    }

    [Fact]
    public async Task ARecordThatCannotBeRemovedIsIgnoredAndTheStartGoesOn()
    {
        var installer = Stage(Remote, starts: AppUpdateService.MaxStarts);
        var record = Path.Combine(_updates, AppUpdateService.StagedRecordName);
        using (new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var start = new Start();
            Assert.False(await AtStart(start).ApplyKeptAtStartAsync("1.0.0", TestContext.Current.CancellationToken));
            Assert.Empty(start.Started);
            // Ignored from then on: no kept update is seen, so nothing shows Updating Exo again.
            Assert.False(AtStart(start).SettleKeptUpdate("1.0.0"));
        }
    }

    // ---- a check that can't answer ----

    [Fact]
    public async Task AnOfflineCheckIsNotAFailedUpdateButAnOfflineUpdateNowIs()
    {
#pragma warning disable RS0030 // Stub handler: no request leaves the process.
        var http = new HttpClient(new Offline());
#pragma warning restore RS0030
        var service = new AppUpdateService(http, _updates, TimeProvider.System) { GameRunning = () => false };
        Assert.False((await service.CheckAsync("1.0.0", TestContext.Current.CancellationToken)).UpdateAvailable);
        Assert.Equal(UpdateFlow.State.Idle, service.State);
        Assert.False((await service.InstallAsync("1.0.0", null, TestContext.Current.CancellationToken)).Installed);
        Assert.Equal(UpdateFlow.State.Failed, service.State);
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
    // ---- free space, by drive ----

    [Fact]
    public void SpaceIsCountedOnEachDriveThatGetsAPart()
    {
        var one = AppUpdateService.SpaceNeeded(212, 495, @"C:\u", @"C:\t", @"C:\i");
        Assert.Equal(2 * 212L + 495L + AppUpdateService.SpaceMargin, Assert.Single(one).Value);
        var split = AppUpdateService.SpaceNeeded(212, 495, @"C:\u", @"D:\t", @"E:\i");
        Assert.Equal(212 + AppUpdateService.SpaceMargin, split[@"C:\"]);
        Assert.Equal(212 + AppUpdateService.SpaceMargin, split[@"D:\"]);
        Assert.Equal(495 + AppUpdateService.SpaceMargin, split[@"E:\"]);
        // An app folder whose size could not be read counts as three installers, never as nothing.
        Assert.Equal(3 * 212 + AppUpdateService.SpaceMargin, AppUpdateService.SpaceNeeded(212, null, @"C:\u", @"D:\t", @"E:\i")[@"E:\"]);
    }

    [Fact]
    public async Task ATempDriveWithoutRoomStopsTheDownloadAndADriveThatCantBeReadDoesNot()
    {
        var address = new Address();
#pragma warning disable RS0030 // Stub handler: no request leaves the process.
        var http = new HttpClient(address);
#pragma warning restore RS0030
        AppUpdateService Service(Func<string, long?> free) => new(http, _updates, TimeProvider.System)
        {
            GameRunning = () => false,
            FreeBytes = free,
            AppFolderBytes = () => 0,
            TempFolder = @"Q:\temp",
            InstallFolder = _updates,
            IsInstaller = _ => true,
            StartInstaller = _ => true,
        };
        var full = await Service(folder => folder.StartsWith('Q') ? 1024 : long.MaxValue).InstallAsync("1.0.0", null, TestContext.Current.CancellationToken);
        Assert.Matches(@"^Exo needs \d+\.\d GB free on drive Q to update\.$", full.Message);
        Assert.Equal(0, address.InstallerAsked);
        var unknown = await Service(folder => folder.StartsWith('Q') ? null : long.MaxValue).InstallAsync("1.0.0", null, TestContext.Current.CancellationToken);
        Assert.True(unknown.ShouldExit);
    }
    // ---- the startup receipt and the installer's check of it, together ----

    [Theory]
    [InlineData("3.26.0")]
    [InlineData("3.26.0-beta.1")]
    public void TheReceiptTheAppWritesIsTheOneTheInstallerAccepts(string version)
    {
        var root = Path.Combine(_updates, "install");
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        var exe = Path.Combine(app, "ExoLauncher.exe");
        File.WriteAllText(exe, "fixture");
        var nonce = Guid.NewGuid().ToString("N");
        // What Complete-ExoUpdate.ps1 writes before it starts the new build: the version is NSIS' PRODUCT_VERSION as it is.
        File.WriteAllText(Path.Combine(root, "update-pending.json"), JsonSerializer.Serialize(new
        {
            schema = 1, nonce, version, executable = exe, createdUtc = DateTimeOffset.UtcNow.ToString("o"),
        }));
        Assert.True(UpdateStartupHealth.TryWrite(root, exe, nonce, version, 4242));
        var receipt = Path.Combine(root, "update-health-" + nonce + ".json");
        var script = Path.Combine(RepoRoot(), "tools", "installer", "Complete-ExoUpdate.ps1");
        var check = $". '{script}' -DefinitionsOnly; if (Test-ExoStartupReceipt '{receipt}' '{nonce}' '{version}' '{exe}' 4242) {{ 'accepted' }} else {{ 'refused' }}";
        Assert.Equal("accepted", Pwsh(check));
    }

    private static string Pwsh(string command)
    {
        var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, errors);
        return output.Trim();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "installer", "Complete-ExoUpdate.ps1"))) return dir.FullName;
        throw new InvalidOperationException("The repo root was not found above the test output.");
    }
}

using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the names-only anti-cheat read could fail, each of which lets Exo start or change a protected game:
/// - it misses what a full scan finds: a file by name, a folder by name, a marker in a folder the walk skips, one beside
///   the install root above a game known by its exe;
/// - a missing install reads differently from a full scan;
/// - Riot's games get walked at all;
/// - the status keeps showing a game unblocked after this read found anti-cheat the last scan didn't.
/// </summary>
public sealed class AntiCheatNowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("exo-anticheat-").FullName;
    private readonly string _data = Directory.CreateTempSubdirectory("exo-anticheat-data-").FullName;
    private readonly string? _priorData = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly DlssSwapService _service = new();

    public AntiCheatNowTests() => Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _data);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorData);
        DeletionGate.DeleteDirectory(_root, recursive: true);
        DeletionGate.DeleteDirectory(_data, recursive: true);
    }

    private GameEntry Game(string? path = null, StoreKind store = StoreKind.Epic) =>
        new() { Id = "steam:1", Title = "Game", Store = store, Installed = true, Path = path ?? Path.Combine(_root, "game") };

    private void Ship(string relative)
    {
        var path = Path.Combine(_root, "game", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Theory]
    [InlineData("Binaries/Win64/EasyAntiCheat/settings.json")]
    [InlineData("EasyAntiCheat_EOS/settings.json")]
    [InlineData("BattlEye/BEService_x64.exe")]
    [InlineData("Binaries/Win64/game.exe")]
    public void ItSaysWhatAFullScanSays(string shipped)
    {
        Ship(shipped);
        var game = Game();
        Assert.Equal(_service.ScanGame(game, TestContext.Current.CancellationToken).AntiCheat,
            DlssSwapService.AntiCheatInFolders(game, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AMarkerAboveAGameKnownByItsExeStillCounts()
    {
        Ship("Game/Binaries/Win64/game.exe");
        Ship("Game/EasyAntiCheat/settings.json");
        var game = Game(Path.Combine(_root, "game", "Game", "Binaries", "Win64", "game.exe"));
        var found = DlssSwapService.AntiCheatInFolders(game, TestContext.Current.CancellationToken);
        Assert.Equal("Easy Anti-Cheat", found);
        Assert.Equal(_service.ScanGame(game, TestContext.Current.CancellationToken).AntiCheat, found);
    }

    [Fact]
    public void AMissingInstallReadsAsAFullScanReadsIt()
    {
        var game = Game(Path.Combine(_root, "not-here"));
        Assert.Null(DlssSwapService.AntiCheatInFolders(game, TestContext.Current.CancellationToken));
        Assert.Null(_service.ScanGame(game, TestContext.Current.CancellationToken).AntiCheat);
    }

    [Fact]
    public void RiotsGamesAreNotWalked()
    {
        var riot = new GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = StoreKind.Riot, Installed = true, Path = Path.Combine(_root, "not-here") };
        Assert.Equal(DlssSwapService.RiotVanguard, _service.AntiCheatNow(riot, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AntiCheatTheLastScanMissedReachesTheStatus()
    {
        Ship("Binaries/Win64/game.exe");
        var game = Game();
        Assert.Null(_service.GetStatus(game, refresh: true, consented: null, TestContext.Current.CancellationToken).AntiCheat);

        // Deep in the tree: no fingerprint the status checks changes, so its cached scan still says none.
        Ship("Binaries/Win64/EasyAntiCheat/settings.json");
        Assert.Null(_service.GetStatus(game, refresh: false, consented: null, TestContext.Current.CancellationToken).AntiCheat);

        Assert.Equal("Easy Anti-Cheat", _service.AntiCheatNow(game, TestContext.Current.CancellationToken));
        Assert.Equal("Easy Anti-Cheat", _service.GetStatus(game, refresh: false, consented: null, TestContext.Current.CancellationToken).AntiCheat);
    }

    [Fact]
    public void TheUpscalerStatusAndTheConsentStepUseTheSharedAnswer()
    {
        // A title off every list with a BattlEye launcher stub: the swap rows are blocked until the player allows BattlEye.
        Ship("Game.exe");
        Ship("Game_BE.exe");
        var game = Game();
        Assert.Equal("BattlEye", _service.GetStatus(game, refresh: true, consented: null, TestContext.Current.CancellationToken).AntiCheat);
        Assert.Equal("BattlEye", _service.AntiCheatNow(game, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AMissingInstallIsUnknownToTheConsentStep() =>
        Assert.Equal(AntiCheatCheck.NoInstall, _service.AntiCheatNow(Game(Path.Combine(_root, "not-here")), TestContext.Current.CancellationToken));
}

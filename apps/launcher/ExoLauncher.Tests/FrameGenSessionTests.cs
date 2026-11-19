using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Services.DlssSwapService;

namespace ExoLauncher.Tests;

/// <summary>
/// A Steam game started without its anti-cheat (NBA 2K26's "without EAC (offline only)") has Exo's frame generation and
/// shader recorder in its folder only for a session Exo starts (DlssSwapService.SessionMods.cs). Ways it could fail:
/// - Best settings puts the mod in between sessions, or the files stay after the game exits, so Steam's default entry
///   runs Easy Anti-Cheat beside them;
/// - they come out while a program of the game still runs (its launcher, a child), or one Steam is updating is touched;
/// - a crash or a closed Exo leaves them, or a game running with its anti-cheat has its files in use pulled away;
/// - a file of the game's is moved, renamed or written over, or what goes back in isn't byte for byte what came out;
/// - a file the game has under Exo's name since is overwritten on the way back in;
/// - the recorder stays in, or what Exo held goes back into a game set back to "with anti-cheat".
/// </summary>
public sealed partial class FrameGenModTests
{
    private const string ProgramName = "Game-Win64-Shipping.exe";

    /// <summary>NBA 2K26's shape: the player's pick "without", Steam's entry without the anti-cheat offered.</summary>
    private async Task WithoutAntiCheat(Func<Task> run)
    {
        var chose = AntiCheatCheck.ChoseWithoutAntiCheat;
        var launch = AntiCheatCheck.WithoutAntiCheatLaunch;
        AntiCheatCheck.ChoseWithoutAntiCheat = game => game.Id == _game.Id || chose(game);
        AntiCheatCheck.WithoutAntiCheatLaunch = game => game.Id == _game.Id
            ? new Adapters.SteamAppInfoNames.AntiCheatFreeLaunch("option1", "Frame Gen Test without EAC (offline only)", "Game.exe")
            : launch(game);
        try { await run(); }
        finally
        {
            AntiCheatCheck.ChoseWithoutAntiCheat = chose;
            AntiCheatCheck.WithoutAntiCheatLaunch = launch;
        }
    }

    /// <summary>An Unreal game with Easy Anti-Cheat and libraries of its own beside its program.</summary>
    private void ProtectedGame()
    {
        UnrealGame();
        Put(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", Pe(300));
        Put("start_protected_game.exe", Pe(300));
        Put(@"Game\Binaries\Win64\steam_api64.dll", Pe(700, 7));
        Put(@"Game\Binaries\Win64\D3D12\D3D12Core.dll", Pe(900, 9));
    }

    private void Play(DlssSwapService service, bool bestSettings = true) => service.ForSessionAtLaunch(_game, Rtx3070, bestSettings, _ => false);

    [Fact]
    public Task WithoutAntiCheatTheModGoesInAtPlayAndOutWhenTheGameExits() => WithoutAntiCheat(async () =>
    {
        ProtectedGame();
        var before = Snapshot();
        IReadOnlyList<string> running = [];
        var service = Service(running: _ => running);
        var ct = TestContext.Current.CancellationToken;

        // Between sessions Best settings leaves it alone.
        Assert.Equal(0, await BestSettingsPass(service));
        Assert.Equal(before, Snapshot());

        // Exo's Play: in just before the start, nothing of the game's changed.
        Play(service);
        var during = Snapshot();
        Assert.NotEqual(before, during);
        Assert.Equal("on", State(service).State);
        foreach (var (path, sha) in before) Assert.Equal(sha, during[path]);

        // A program of the game still runs: nothing moves.
        running = [ProgramName];
        Assert.Equal("running", await service.TakeOutAfterSession(_game, ct));
        Assert.Equal(during, Snapshot());

        // All of it gone: out, the folder byte for byte as before, and the line says it goes in only from Exo.
        running = [];
        Assert.Null(await service.TakeOutAfterSession(_game, ct));
        Assert.Equal(before, Snapshot());
        Assert.Contains("only while you play it from Exo", State(service).Note);
        Assert.Equal(0, await BestSettingsPass(service));
        Assert.Equal(before, Snapshot());

        // The next Play puts back exactly what came out; its exit takes it out again.
        Play(service);
        Assert.Equal(during, Snapshot());
        Assert.Equal("on", State(service).State);
        Assert.Null(await service.TakeOutAfterSession(_game, ct));
        Assert.Equal(before, Snapshot());
    });

    [Fact]
    public Task WithoutAntiCheatAStartAfterACrashTakesTheLeftoversOutAndWarnsWhileTheyAreInUse() => WithoutAntiCheat(async () =>
    {
        ProtectedGame();
        var before = Snapshot();
        Play(Service());
        var during = Snapshot();
        Assert.NotEqual(before, during);

        // Exo starts again: the game runs from Steam's protected entry, Easy Anti-Cheat beside it. In use: left, warned once.
        IReadOnlyList<string> running = ["start_protected_game.exe", ProgramName];
        var warned = 0;
        var service = Service(running: _ => running, warn: _ => warned++, antiCheatRunning: () => true);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(0, await service.TakeOutWhereIdle([_game], _ => false, _ => false, ct));
        Assert.Equal(0, await service.TakeOutWhereIdle([_game], _ => false, _ => false, ct));
        Assert.Equal(1, warned);
        Assert.Equal(during, Snapshot());

        // Steam updating it, or an Exo session of it: never touched.
        running = [];
        Assert.Equal(0, await service.TakeOutWhereIdle([_game], _ => false, _ => true, ct));
        Assert.Equal(0, await service.TakeOutWhereIdle([_game], _ => true, _ => false, ct));
        Assert.Equal(during, Snapshot());

        // It exited: out, and the game's own files are as they were.
        Assert.Equal(1, await service.TakeOutWhereIdle([_game], _ => false, _ => false, ct));
        Assert.Equal(before, Snapshot());

        // In again; then only the anti-cheat's bootstrapper runs (the game itself hasn't loaded them yet): out at once.
        Play(service);
        Assert.Equal(during, Snapshot());
        running = ["start_protected_game.exe"];
        Assert.Equal(1, await service.TakeOutWhereIdle([_game], _ => false, _ => false, ct));
        Assert.Equal(before, Snapshot());
    });

    [Fact]
    public Task WithoutAntiCheatAFileOfTheGamesUnderExosNameIsNeverWrittenOver() => WithoutAntiCheat(async () =>
    {
        ProtectedGame();
        var service = Service();
        var ct = TestContext.Current.CancellationToken;
        Play(service);
        var loader = ReadFrameGenManifest(Path.Combine(_root, UnrealProgram))!.Loader;
        Assert.Null(await service.TakeOutAfterSession(_game, ct));

        // A Steam update shipped a file of its own under the loader's name: it stays, and Exo keeps what it holds.
        var own = Put(Path.Combine(UnrealProgram, loader), Pe(333, 3));
        var withIt = Snapshot();
        Assert.NotNull(await service.PutInForSession(_game, ct));
        Assert.Equal(withIt, Snapshot());
        Assert.NotNull(service.SessionHeldFrameGen(_game.Id));

        // It went again: the next Play puts Exo's back.
        DeletionGate.DeleteFile(own);
        Assert.Null(await service.PutInForSession(_game, ct));
        Assert.Null(service.SessionHeldFor(_game.Id));
        Assert.Equal("on", State(service).State);
    });

    [Fact]
    public Task WithoutAntiCheatTheRecorderIsOffBetweenSessionsAndBackWithAntiCheatNothingGoesBack() => WithoutAntiCheat(async () =>
    {
        ProtectedGame();
        var before = Snapshot();
        var ini = Path.Combine(_root, UnrealProgram, "scskiller.ini");
        var calls = new List<bool>();
        var service = Service(recorder: (_, on, _) =>
        {
            calls.Add(on);
            if (on) File.WriteAllText(ini, "[scskiller]");
            else DeletionGate.DeleteFile(ini);
            return Task.FromResult<string?>(null);
        });
        var ct = TestContext.Current.CancellationToken;
        Play(service);
        File.WriteAllText(ini, "[scskiller]");

        Assert.Null(await service.TakeOutAfterSession(_game, ct));
        Assert.Equal([false], calls);
        Assert.Equal(before, Snapshot());
        Assert.True(service.SessionHeldFor(_game.Id)!.Recorder);

        Play(service);
        Assert.Equal([false, true], calls);
        Assert.True(File.Exists(ini));
        Assert.Null(await service.TakeOutAfterSession(_game, ct));
        Assert.Equal(before, Snapshot());

        // Back with anti-cheat: what Exo held is let go, so no later start puts it back.
        Assert.True(AntiCheatBackOn(service).Removed);
        Assert.Null(service.SessionHeldFor(_game.Id));
        Play(service, bestSettings: false);
        Assert.Equal(before, Snapshot());
    });
}

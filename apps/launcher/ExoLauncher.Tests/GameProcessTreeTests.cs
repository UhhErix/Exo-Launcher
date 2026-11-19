using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways following a game's processes can fail, silently (play time, Game awake, downloads held and Capture all follow
/// the registry's run state):
/// - the process Exo bound ends while the game's successor lives on (Phantom Blade Zero: steam\PBZ.exe starts
///   platform\steam\PBZ.exe), and the game reads as closed, ending its session mid-play;
/// - a rebind to the successor is reported as a stop and a new start, so a session is cut in two;
/// - a store-named folder inside the install (steam, platform) is taken for a helper folder, so the game is never seen;
/// - the bound process's start time is lost or made up, so a game found running counts from the wrong instant.
/// Process ids are odd and huge: Windows' are multiples of 4, so no real process is ever opened.
/// </summary>
public sealed class GameProcessTreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-tree-" + Guid.NewGuid().ToString("N"));
    private IReadOnlyList<GameProcessRegistry.ProcessObservation> _snapshot = [];
    private readonly List<(string Id, bool Running)> _events = [];
    private readonly GameProcessRegistry _registry;
    private readonly GameEntry _game;
    private static readonly DateTimeOffset Started = new(2026, 10, 29, 3, 22, 0, TimeSpan.Zero);

    public GameProcessTreeTests()
    {
        Directory.CreateDirectory(_root);
        _game = new GameEntry
        {
            Id = "steam:999999931", Title = "Exo Tree Fixture Qx", Store = StoreKind.Steam,
            LaunchTarget = "999999931", Installed = true, Path = _root,
        };
        _registry = new GameProcessRegistry(readSteamProcessLog: () => "", readSnapshot: () => _snapshot);
        _registry.StateChanged += (id, state) => _events.Add((id, state.IsRunning));
    }

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, recursive: true); } catch { /* temp */ }
    }

    private GameProcessRegistry.ProcessObservation Observed(int pid, string relative, DateTimeOffset? started) =>
        new(pid, "PBZ", Path.Combine(_root, relative), started?.UtcTicks ?? 0);

    private bool Running() => _registry.GetStates([_game], underRootOnly: true)[_game.Id].IsRunning;

    [Fact]
    public void TheGameStaysRunningWhenTheBoundProcessEndsAndItsSuccessorLivesOn()
    {
        var bootstrap = Observed(2_000_000_001, @"steam\PBZ.exe", Started);
        var successor = Observed(2_000_000_003, @"platform\steam\PBZ.exe", Started.AddSeconds(1));

        _snapshot = [bootstrap, successor];
        Assert.True(Running());
        Assert.Equal(Started, _registry.StartedAtOf(_game.Id));

        _snapshot = [successor];
        Assert.True(Running());
        Assert.Equal(Started.AddSeconds(1), _registry.StartedAtOf(_game.Id));

        _snapshot = [];
        Assert.False(Running());
        Assert.Equal(new[] { (_game.Id, true), (_game.Id, false) }, _events);
    }

    [Fact]
    public void AnUnreadableStartTimeIsNotMadeUp()
    {
        _snapshot = [Observed(2_000_000_005, @"steam\PBZ.exe", started: null)];
        Assert.True(Running());
        Assert.Null(_registry.StartedAtOf(_game.Id));
        Assert.Null(_registry.StartedAtOf("steam:not-bound"));
    }
}

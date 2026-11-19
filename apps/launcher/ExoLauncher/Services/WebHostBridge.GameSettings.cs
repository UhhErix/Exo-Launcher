using System.Text.Json;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    /// <summary>
    /// game.settings: a game's best settings for this PC (<see cref="GameSettings"/>), each group with whether it is in place;
    /// supported false when Exo has none for it, found false until the game has written its settings file; again when Exo
    /// wrote them again after an update reset them, until the game next runs; windowMode, the mode chosen on the line, null
    /// for a game whose mode Exo can't set; standing, whether what was asked for is in place, applied only when Exo found and wrote all of it (<see cref="GameSettings.Stands"/>). With group (or all, which Optimize sends) and on, that group is set or put back
    /// first, and with mode that mode is remembered and all of it applied with it, each refused while the game runs.
    /// </summary>
    private async Task<object> GameSettingsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };
        var group = ReadString(p, hasParams, "group");
        var all = ReadBool(p, hasParams, "all") == true;
        var on = ReadBool(p, hasParams, "on");
        var mode = ReadString(p, hasParams, "mode");
        var smoothMotion = ReadBool(p, hasParams, "smoothMotion");
        return await Task.Run(object () =>
        {
            var pc = GameSettings.ThisPc();
            var supportsSmooth = GameSettings.SupportsSmoothMotion(game, pc);
            if (smoothMotion is true && !supportsSmooth)
                return new { ok = false, message = "Smooth Motion requires Wuthering Waves on an RTX 40 or 50 GPU." };
            var initialSmooth = supportsSmooth && _services.Settings.SmoothMotionEnabled(game.Id);
            if (GameSettings.For(game, pc, initialSmooth) is not { } profile)
                return new { ok = true, supported = false, note = GameSettings.AntiCheatNote(game, pc) };
            var running = MainWindow.RunningLibraryGames().ContainsKey(game.Id) || GameSettings.Busy(profile);
            if (mode is not null)
            {
                if (profile.WindowMode is null || mode is not (GameSettings.Borderless or GameSettings.Fullscreen)) return new { ok = false, message = "This game has no such window mode." };
                if (running) return new { ok = false, message = $"Close {profile.Title} first." };
                if (!GameSettings.ChooseWindowMode(profile, mode)) return new { ok = false, message = "Could not save. Try again." };
                profile = GameSettings.For(game, pc, initialSmooth)!;
                all = true;
                on = true;
            }
            if (smoothMotion is { } wantSmooth)
            {
                if (running) return new { ok = false, message = $"Close {profile.Title} first." };
                _services.Settings.SetSmoothMotion(game.Id, wantSmooth);
                profile = GameSettings.For(game, pc, wantSmooth)!;
            }
            else if (all && on == false && supportsSmooth)
            {
                if (running) return new { ok = false, message = $"Close {profile.Title} first." };
                _services.Settings.SetSmoothMotion(game.Id, false);
                profile = GameSettings.For(game, pc, false)!;
            }
            if ((group is not null || all) && on is { } turnOn)
            {
                if (running) return new { ok = false, message = $"Close {profile.Title} first." };
                if (GameSettings.ApplyWithNvidia(game, profile, group, all, turnOn) is { } refused)
                    return new { ok = false, message = refused };
            }
            var summary = GameSettings.InspectState(game, profile);
            var groups = summary.Groups?.Select(state => new { id = state.Id, label = state.Label, on = state.On, exo = state.Exo }).ToList();
            bool? smoothMotionState = supportsSmooth ? _services.Settings.SmoothMotionEnabled(game.Id) : null;
            return new
            {
                ok = true,
                supported = true,
                title = profile.Title,
                running,
                found = summary.Found,
                waiting = GameSettings.Waiting(profile),
                groups,
                standing = summary.Standing.ToString().ToLowerInvariant(),
                again = GameSettings.Again(profile),
                windowMode = profile.WindowMode,
                smoothMotion = smoothMotionState,
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>game.optimizeHeld: Exo put a game's window mode back (the page reads it again, and says nothing).</summary>
    private void OnOptimizeHeld(string title) => PostEvent("game.optimizeHeld", new { title });

    /// <summary>game.optimizedAgain: Exo wrote a game's Optimize again after its update reset it (the page reads it again, and says so once).</summary>
    private void OnOptimizedAgain(string title) => PostEvent("game.optimizedAgain", new { title });
}

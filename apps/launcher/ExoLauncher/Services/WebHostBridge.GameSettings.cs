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
        return await Task.Run(object () =>
        {
            var pc = GameSettings.ThisPc();
            if (GameSettings.For(game, pc) is not { } profile) return new { ok = true, supported = false, note = GameSettings.AntiCheatNote(game, pc) };
            var running = MainWindow.RunningLibraryGames().ContainsKey(game.Id) || GameSettings.Busy(profile);
            if (mode is not null)
            {
                if (profile.WindowMode is null || mode is not (GameSettings.Borderless or GameSettings.Fullscreen)) return new { ok = false, message = "This game has no such window mode." };
                if (running) return new { ok = false, message = $"Close {profile.Title} first." };
                if (!GameSettings.ChooseWindowMode(profile, mode)) return new { ok = false, message = "Could not save. Try again." };
                profile = GameSettings.For(game, pc)!;
                all = true;
                on = true;
            }
            if ((group is not null || all) && on is { } turnOn)
            {
                if (running) return new { ok = false, message = $"Close {profile.Title} first." };
                if (!GameSettings.Set(profile, all ? null : group, turnOn, profile.Program?.Invoke(game))) return new { ok = false, message = "Could not save. Try again." };
                // The record remembers the game it was pressed for: the page's status is told under its id without a walk of its folder.
                GameSettings.NoteGame(profile.Id, game.Id);
                // The game's own NVIDIA profile goes with Optimize (all) or on its own (its group), after the file it reads from.
                if ((all || group == GameSettings.NvidiaGroup) && GameSettings.ReadAll(profile) is [var first, ..]
                    && GameSettings.SetNvidia(game, profile, first.File, turnOn) is { } refused)
                    return new { ok = false, message = refused };
            }
            var files = GameSettings.ReadAll(profile);
            var states = files.Count == 0 ? null : GameSettings.States(profile, files, GameSettings.Kept(profile.Id), GameSettings.KeptOn(profile));
            if (states is not null && GameSettings.NvidiaState(game, profile, files[0].File) is { } nvidia) states.Add(nvidia);
            var groups = states?.Select(state => new { id = state.Id, label = state.Label, on = state.On, exo = state.Exo }).ToList();
            var standing = states is null ? GameSettings.Standing.Off : GameSettings.Stands(states, GameSettings.Missing(profile, files).Count);
            return new { ok = true, supported = true, title = profile.Title, running, found = files.Count > 0, waiting = GameSettings.Waiting(profile), groups, standing = standing.ToString().ToLowerInvariant(), again = GameSettings.Again(profile), windowMode = profile.WindowMode };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>game.optimizeHeld: Exo put a game's window mode back (the page reads it again, and says nothing).</summary>
    private void OnOptimizeHeld(string title) => PostEvent("game.optimizeHeld", new { title });

    /// <summary>game.optimizedAgain: Exo wrote a game's Optimize again after its update reset it (the page reads it again, and says so once).</summary>
    private void OnOptimizedAgain(string title) => PostEvent("game.optimizedAgain", new { title });
}

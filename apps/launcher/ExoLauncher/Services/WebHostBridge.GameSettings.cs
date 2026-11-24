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
        var key = ReadString(p, hasParams, "key");
        var all = ReadBool(p, hasParams, "all") == true;
        var on = ReadBool(p, hasParams, "on");
        var mode = ReadString(p, hasParams, "mode");
        var smoothMotion = ReadBool(p, hasParams, "smoothMotion");
        var jvm = ReadBool(p, hasParams, "jvm");
        var modsAction = ReadString(p, hasParams, "modsAction");
        var instanceId = ReadString(p, hasParams, "instanceId");
        var pc = GameSettings.ThisPc();
        var isMinecraft = GameSettings.IsMinecraftJava(game);

        if (isMinecraft && modsAction is "install" or "undo")
        {
            var instances = GameSettings.DiscoverMinecraftInstances();
            var target = instances.FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return new { ok = false, message = "Minecraft instance not found." };
            var modResult = modsAction == "install"
                ? await GameSettings.InstallMinecraftPerformanceModsAsync(target, ct: ct).ConfigureAwait(false)
                : GameSettings.UndoMinecraftPerformanceMods(target.Id);
            if (!modResult.Ok)
                return new { ok = false, message = modResult.Message };
        }

        return await Task.Run(object () =>
        {
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
            if (key is not null && on is { } keyOn)
            {
                if (running) return new { ok = false, message = $"Close {profile.Title} first." };
                if (!GameSettings.SetKey(profile, key, keyOn, profile.Program?.Invoke(game)))
                    return new { ok = false, message = "Could not save. Try again." };
            }
            else if ((group is not null || all) && on is { } turnOn)
            {
                if (running) return new { ok = false, message = $"Close {profile.Title} first." };
                if (GameSettings.ApplyWithNvidia(game, profile, group, all, turnOn, includePerGamePower: true) is { } refused)
                    return new { ok = false, message = refused };
                if (isMinecraft && all)
                {
                    foreach (var inst in GameSettings.DiscoverMinecraftInstances())
                    {
                        if (turnOn) GameSettings.ApplyMinecraftJvm(inst, pc.RamGb);
                        else GameSettings.UndoMinecraftJvm(inst);
                    }
                }
            }
            if (isMinecraft && jvm is { } wantJvm)
            {
                if (running) return new { ok = false, message = $"Close {profile.Title} first." };
                var instances = GameSettings.DiscoverMinecraftInstances();
                var targets = string.IsNullOrEmpty(instanceId)
                    ? instances
                    : instances.Where(i => string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var inst in targets)
                {
                    if (wantJvm) GameSettings.ApplyMinecraftJvm(inst, pc.RamGb);
                    else GameSettings.UndoMinecraftJvm(inst);
                }
            }
            var summary = GameSettings.InspectState(game, profile, includePerGamePower: true);
            var groups = summary.Groups?.Select(state => new
            {
                id = state.Id,
                label = state.Label,
                on = state.On,
                exo = state.Exo,
                changes = state.Changes?.Select(change => new
                {
                    key = change.Key,
                    current = change.Current,
                    target = change.Target,
                    applied = change.Applied,
                    kept = change.Kept,
                }).ToList(),
            }).ToList();
            var primaryFile = string.IsNullOrEmpty(profile.File) ? null : GameSettings.Read(profile);
            var advice = profile.Advice?.Invoke(primaryFile);
            object? minecraft = isMinecraft
                ? new
                {
                    heapGb = GameSettings.ComputeMinecraftHeapGb(pc.RamGb),
                    instances = GameSettings.DiscoverMinecraftInstances().Select(inst => new
                    {
                        id = inst.Id,
                        name = inst.Name,
                        launcher = inst.Launcher,
                        gameVersion = inst.GameVersion,
                        loader = inst.Loader,
                        isFabric = inst.IsFabric,
                        jvmApplied = GameSettings.IsMinecraftJvmApplied(inst.Id),
                        canInstallMods = inst.IsFabric && !string.IsNullOrWhiteSpace(inst.GameVersion) && !string.IsNullOrEmpty(inst.ModsDir),
                        modsInstalled = GameSettings.InstalledMinecraftMods(inst.Id).Select(m => m.Slug).ToList(),
                    }).ToList(),
                }
                : null;
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
                advice,
                minecraft,
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>game.optimizeHeld: Exo put a game's window mode back (the page reads it again, and says nothing).</summary>
    private void OnOptimizeHeld(string title) => PostEvent("game.optimizeHeld", new { title });

    /// <summary>game.optimizedAgain: Exo wrote a game's Optimize again after its update reset it (the page reads it again, and says so once).</summary>
    private void OnOptimizedAgain(string title) => PostEvent("game.optimizedAgain", new { title });
}

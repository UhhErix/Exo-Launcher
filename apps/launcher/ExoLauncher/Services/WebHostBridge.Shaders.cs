using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>The game sheet's Shaders line (shaders.status) and shaders.changed, both from the Exo Shaders engine; the Shaders page's Prepare (shaders.prepare.*).</summary>
public sealed partial class WebHostBridge
{
    private async Task<object> ShadersStatusAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, state = "error", reason = "Game not found." };
        var status = await Task.Run(() => _services.Shaders.For(game), ct).ConfigureAwait(false);
        return new
        {
            ok = true,
            state = status.State,
            reason = status.Reason,
            engineId = status.EngineId,
            warmed = status.WarmedAt is { } at
                ? new { at = at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture), driver = status.Driver }
                : null,
        };
    }

    /// <summary>shaders.estimates: how long each game the engine lists takes to compile here, in seconds, by its engine id.</summary>
    private async Task<object> ShadersEstimatesAsync(CancellationToken ct) =>
        new { ok = true, seconds = await Task.Run(_services.Shaders.CompileSeconds, ct).ConfigureAwait(false) };

    /// <summary>Before a launch: the shader recorder comes out of a game it would hang (<see cref="DlssSwapService.RecorderClashes"/>). Never keeps the game from starting.</summary>
    private async Task RecorderOutOfTheWayAsync(Models.GameEntry game)
    {
        try
        {
            if (!await Task.Run(() => _dlss.RecorderClashes(game.Id)).ConfigureAwait(true) || ShadersAddon.Live is not { } engine) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await engine.CallAsync("recorder.set", new System.Text.Json.Nodes.JsonObject { ["id"] = game.Id, ["on"] = false }, timeout.Token, "frame generation in " + game.Title).ConfigureAwait(true);
            AppLog.Info($"Shaders: recording stops in {game.Title}: with its frame generation the game can hang as it starts.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException)
        {
            AppLog.Warn($"Shaders: recording in {game.Title} stays on: {ex.Message}");
        }
    }

    private void OnShadersChanged() => PostEvent("shaders.changed", new { });

    /// <summary>shaders.rebuild: where the rebuild after a driver change stands (a state and two counts). Reads only.</summary>
    private static object ShadersRebuildStatus()
    {
        var status = ShaderRebuildWatch.Status;
        return new { ok = true, state = status.State, ready = status.Ready, total = status.Total };
    }

    private void OnShadersRebuildChanged()
    {
        var status = ShaderRebuildWatch.Status;
        PostEvent("shaders.rebuild", new { state = status.State, ready = status.Ready, total = status.Total });
    }

    private System.Threading.Timer? _shaderPreparePoll;
    private int _shaderPreparePolling;
    // Whether a game can be prepared, from the kept answer (walked again when the game changes or after ten minutes);
    // each start, the automatic one included, reads it fresh (ShaderPrepareStartAsync).
    private bool ShaderPrepareEligible(Models.GameEntry game) =>
        ShaderPrepare.Eligible(game, AntiCheatCheck.Cached(game), _services.Shaders.EngineFamilyOf(game));

    /// <summary>The Shaders page's first-launch posters: the installed games Prepare may start, and each one's state.</summary>
    private async Task<object> ShaderPrepareGamesAsync(CancellationToken ct)
    {
        var library = _services.Library.PeekCachedLibrary();
        var games = await Task.Run(() => library
            .Where(game => game.Installed && ShaderPrepareEligible(game))
            .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(game =>
            {
                var (state, message) = _services.ShaderPrepare.StateOf(game);
                return new { id = game.Id, title = game.Title, installDir = game.Path, state, message };
            })
            .ToList(), ct).ConfigureAwait(false);
        return new { ok = true, games };
    }

    /// <summary>The press, or an automatic run: the game starts the way Play starts it, and the run watches it from there.</summary>
    private async Task<object> ShaderPrepareStartAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (HostMode.IsDisposable) return new { ok = false, message = (string?)HostMode.Refusal };
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(true);
        if (game is null || !await Task.Run(() => ShaderPrepare.Eligible(game, _dlss.AntiCheatAnswer(game, ct), _services.Shaders.EngineFamilyOf(game)), ct).ConfigureAwait(true))
            return new { ok = false, message = (string?)"Not for this game" };
        var library = _services.Library.PeekCachedLibrary();
        if (await Task.Run(() => _services.Launcher.GetGameRunStates(library).Values.Any(run => run.IsRunning), ct).ConfigureAwait(true))
            return new { ok = false, message = (string?)"Close your game first" };
        if (_services.ShaderPrepare.Busy) return new { ok = false, message = (string?)"One is running" };
        // A compile's cache growth would read as the game's.
        if (ShadersAddon.Live is { } engine && await engine.CompilingAsync(ct).ConfigureAwait(true)) return new { ok = false, message = (string?)"A compile is running" };
        var launch = await GameLaunchAsync(p, hasParams).ConfigureAwait(true);
        var reply = JsonSerializer.SerializeToElement(launch, launch.GetType(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (!reply.TryGetProperty("ok", out var started) || started.ValueKind != JsonValueKind.True)
        {
            _services.ShaderPrepare.Failed(game.Id, "Did not start");
            return new { ok = false, message = (string?)"Did not start" };
        }
        return _services.ShaderPrepare.Start(game) ? new { ok = true, message = (string?)null } : new { ok = false, message = (string?)"One is running" };
    }

    private object ShaderPrepareCancel(JsonElement p, bool hasParams)
    {
        if (ReadString(p, hasParams, "id") is { } id) _services.ShaderPrepare.Cancel(id);
        return new { ok = true };
    }

    private void KeepShadersPrepared()
    {
        if (HostMode.IsDisposable) return;
        _shaderPreparePoll = new System.Threading.Timer(_ => _ = ShaderPrepareTickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Automatic: while nobody is using the PC (<see cref="PcIdle"/>: no input, sound, microphone, full screen, game or battery), the next game that needs it.</summary>
    private async Task ShaderPrepareTickAsync()
    {
        if (HostMode.IsDisposable) return;
        if (_detached)
        {
            Interlocked.Exchange(ref _shaderPreparePoll, null)?.Dispose();
            return;
        }
        if (Interlocked.Exchange(ref _shaderPreparePolling, 1) == 1) return;
        try
        {
            var prepare = _services.ShaderPrepare;
            if (prepare.Busy || !PcIdle.Now(() => _services.Launcher.AnyGameRunning || LeanSignal.Active)) return;
            var token = _bridgeLifetime.Token;
            var next = _services.Library.PeekCachedLibrary().FirstOrDefault(game => game.Installed &&
                ShaderPrepareEligible(game) && ShaderPrepare.WantsAuto(prepare.StateOf(game).State, game.PlaytimeMinutes));
            if (App.MainAppWindow is not { } window) return;
            if (next is null)
            {
                // Nothing to prepare: one compile's short check instead, once per game per driver. The engine says whether compiling reaches the game.
                if (ShadersAddon.Live is { Running: true }) return;
                var check = _services.Library.PeekCachedLibrary()
                    .Select(game => (Game: game, Status: _services.Shaders.For(game)))
                    .FirstOrDefault(item => item.Status is { State: "warmed", EngineId: not null } && !prepare.VerifiedOnThisDriver(item.Game.Id));
                if (check.Game is null) return;
                AppLog.Info($"Shaders: checking {check.Game.Title}'s compile, the PC is idle.");
                prepare.NoteVerified(check.Game.Id);
                await window.ShadersVerifyAsync(check.Status.EngineId!, token).ConfigureAwait(false);
                return;
            }
            AppLog.Info($"Prepare shaders: {next.Title} starts by itself, the PC is idle.");
            var ask = JsonSerializer.SerializeToElement(new { id = next.Id });
            window.DispatcherQueue.EnqueueAsync(async () =>
            {
                try { await ShaderPrepareStartAsync(ask, true, token).ConfigureAwait(true); }
                catch (Exception error) when (error is not OutOfMemoryException) { AppLog.Warn($"Prepare shaders: {next.Title} did not start: {error.Message}"); }
            }, "Shader auto-prepare");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { AppLog.Debug("Prepare shaders: automatic check: " + error.GetType().Name); }
        finally { Volatile.Write(ref _shaderPreparePolling, 0); }
    }
}

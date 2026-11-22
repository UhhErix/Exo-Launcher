using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal sealed class ShadersHostService : IDisposable
{
    private readonly AppServices _services;
    private readonly Lock _gate = new();
    private ShadersAddon? _addon;

    internal event Action<string, JsonNode?>? Event;

    internal ShadersHostService(AppServices services)
    {
        _services = services;
    }

    private ShadersAddon? Addon
    {
        get
        {
            if (_addon is not null || !ShadersAddon.Present) return _addon;
            using var scope = _gate.EnterScope();
            if (_addon is not null) return _addon;
            var addon = new ShadersAddon();
            addon.Event += (name, data) =>
            {
                Event?.Invoke(name, data);
                if (name != "ready") return;
                _ = addon.CallAsync("exitWhenIdle", new JsonObject { ["seconds"] = ShadersAddon.IdleExitSeconds }, CancellationToken.None)
                    .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                var visible = addon.UiVisible;
                _ = addon.CallAsync("ui.visible", new JsonObject { ["visible"] = visible }, CancellationToken.None)
                    .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                addon.SyncLibrary(_services.Library.PeekCachedLibrary, force: true);
            };
            _services.Library.LibraryUpdated += () => addon.SyncLibrary(_services.Library.PeekCachedLibrary);
            return _addon = addon;
        }
    }

    internal async Task<bool> VerifyAsync(string engineId, CancellationToken ct)
    {
        if (Addon is not { } addon) return false;
        var reply = await addon.CallAsync("game.verify", new JsonObject { ["id"] = engineId }, ct, "Idle check").ConfigureAwait(false);
        return reply?["started"] is JsonValue started && started.TryGetValue<bool>(out var on) && on;
    }

    internal async Task<bool> RebuildStartAsync(IReadOnlyList<string> engineIds, CancellationToken ct)
    {
        if (Addon is not { } addon || engineIds.Count == 0) return false;
        var ids = new JsonArray();
        foreach (var id in engineIds) ids.Add(id);
        await addon.CallAsync("queue.add", new JsonObject { ["ids"] = ids }, ct, "the new driver").ConfigureAwait(false);
        await addon.CallAsync("queue.start", new JsonObject { ["idle"] = true }, ct, "the new driver").ConfigureAwait(false);
        try { await addon.CallAsync("queue.resume", null, ct).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        await RefreshLocalAsdAsync(addon, driverUpdated: true, ct).ConfigureAwait(false);
        return true;
    }

    internal async Task<bool> ScanAsync(CancellationToken ct)
    {
        if (Addon is not { } addon) return false;
        await addon.CallAsync("scan", null, ct, "new or updated games").ConfigureAwait(false);
        await RefreshLocalAsdAsync(addon, driverUpdated: false, ct).ConfigureAwait(false);
        return true;
    }

    private static async Task RefreshLocalAsdAsync(ShadersAddon addon, bool driverUpdated, CancellationToken ct)
    {
        try
        {
            await addon.CallAsync("asd.refresh", new JsonObject { ["driverUpdated"] = driverUpdated }, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
    }

    internal async Task RebuildPauseAsync(CancellationToken ct)
    {
        if (_addon is not { Running: true } addon) return;
        await addon.CallAsync("queue.pause", null, ct).ConfigureAwait(false);
    }

    internal object Status()
    {
        if (Addon is not { } addon) return new { ok = true, present = false };
        return new { ok = true, present = true, state = addon.State, message = addon.Message, snapshot = ShadersAddon.AtRest() };
    }

    internal async Task<string?> RecorderSetAsync(string gameId, bool on, CancellationToken ct)
    {
        if (Addon is not { } addon) return on ? "Exo Shaders isn't installed." : null;
        try
        {
            var ask = new JsonObject { ["id"] = gameId, ["on"] = on };
            if (on) ask["session"] = true;
            await addon.CallAsync("recorder.set", ask, ct, "a session without anti-cheat").ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException)
        {
            AppLog.Warn($"Shaders: the recorder could not be turned {(on ? "on" : "off")} in {gameId}: {ex.Message}");
            return $"Exo Shaders couldn't turn its recorder {(on ? "on" : "off")}.";
        }
    }

    internal async Task<string?> RecorderOffAsync(string gameId, CancellationToken ct)
    {
        if (Addon is not { } addon) return null;
        try
        {
            await addon.CallAsync("recorder.set", new JsonObject { ["id"] = gameId, ["on"] = false }, ct, "anti-cheat back on").ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException)
        {
            AppLog.Warn($"Anti-cheat: the shader recorder could not be turned off in {gameId}: {ex.Message}");
            return "Exo Shaders couldn't turn its recorder off in this game, so it still starts without anti-cheat. Try again.";
        }
    }

    internal async Task<object> CallAsync(string? method, JsonNode? parameters, CancellationToken ct)
    {
        if (Addon is not { } addon) return new { ok = false, message = "Exo Shaders is not installed." };
        if (string.IsNullOrWhiteSpace(method) || !ShadersAddon.PageMethods.Contains(method)) return new { ok = false, message = "Unknown request." };
        if (await Task.Run(() => ShadersAddon.RecorderRefusal(method, parameters, _services.Library.Find, game => AntiCheatCheck.Of(game, ct)), ct).ConfigureAwait(false) is { } refused)
            return new { ok = false, message = refused };
        if (method == "ui.visible")
            addon.SetUiVisible(parameters?["visible"] is JsonValue value && value.TryGetValue<bool>(out var on) && on);
        var starts = ShadersAddon.StartMethods.Contains(method);
        var startFor = starts ? (method == "scan" ? "Refresh" : method is "game.compile" or "queue.add" or "queue.start" or "queue.resume" ? "Compile" : "Shaders page") : null;
        try
        {
            var (usedAtRest, result) = await ShadersAddon.InvokeWithStoppingFallbackAsync(
                method,
                starts,
                () => addon.Running,
                () => addon.CallAsync(method, parameters, ct, startFor),
                () => method == "snapshot" ? ShadersAddon.AtRest() : null).ConfigureAwait(false);
            return usedAtRest && method != "snapshot"
                ? new { ok = true }
                : new { ok = true, result };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("Exo Shaders call " + method + ": " + ex.Message);
            return new { ok = false, message = Regex.IsMatch(ex.Message, @"[A-Za-z]:\\|\\\\") ? "Exo Shaders could not do that." : ex.Message };
        }
    }

    public void Dispose()
    {
        var addon = Interlocked.Exchange(ref _addon, null);
        try { addon?.Dispose(); } catch { }
    }
}

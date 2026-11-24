using System.Text.Json;
using ExoLauncher.Services.Macros;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private int _macrosWatched;
    private Action<string, string?, bool, string?>? _macrosChangedHandler;

    private static readonly JsonSerializerOptions MacroWireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private void WatchMacros()
    {
        if (Interlocked.Exchange(ref _macrosWatched, 1) != 0) return;
        _macrosChangedHandler = (reason, activeProfileId, paused, pauseReason) =>
            PostEvent("macros.changed", new { reason, activeProfileId, softwarePausedForAntiCheat = paused, pauseReason });
        _services.Macros.StateChanged += _macrosChangedHandler;
    }

    private void UnwatchMacros()
    {
        if (Interlocked.Exchange(ref _macrosWatched, 0) == 0) return;
        if (Interlocked.Exchange(ref _macrosChangedHandler, null) is { } handler)
            _services.Macros.StateChanged -= handler;
    }

    private object MacrosState()
    {
        WatchMacros();
        return _services.Macros.GetState();
    }

    private object MacrosSaveProfile(JsonElement p, bool hasParams)
    {
        WatchMacros();
        if (!hasParams || !p.TryGetProperty("profile", out var profileEl))
        {
            return new { ok = false, message = "Profile payload is required.", profile = (MacroProfile?)null };
        }
        var profile = JsonSerializer.Deserialize<MacroProfile>(profileEl.GetRawText(), MacroWireJson);
        if (profile is null)
        {
            return new { ok = false, message = "Invalid profile payload.", profile = (MacroProfile?)null };
        }
        var (ok, message, saved) = _services.Macros.SaveProfile(profile);
        return new { ok, message, profile = saved };
    }

    private object MacrosDeleteProfile(JsonElement p, bool hasParams)
    {
        WatchMacros();
        var id = ReadString(p, hasParams, "id") ?? "";
        var (ok, message) = _services.Macros.DeleteProfile(id);
        return new { ok, message };
    }

    private object MacrosActivateProfile(JsonElement p, bool hasParams)
    {
        WatchMacros();
        var id = ReadString(p, hasParams, "id") ?? "";
        var (ok, message, softwarePaused) = _services.Macros.ActivateProfile(id);
        return new { ok, message, softwarePaused };
    }

    private object MacrosSetEnabled(JsonElement p, bool hasParams)
    {
        WatchMacros();
        var enabled = ReadBool(p, hasParams, "enabled") ?? true;
        var current = _services.Macros.SetEnabled(enabled);
        return new { ok = true, enabled = current };
    }

    private object MacrosConfirmGame(JsonElement p, bool hasParams)
    {
        WatchMacros();
        var gameId = ReadString(p, hasParams, "gameId") ?? "";
        var confirmed = ReadBool(p, hasParams, "confirmed") ?? false;
        var (ok, message, policy) = _services.Macros.ConfirmGame(gameId, confirmed);
        return new { ok, message, policy };
    }

    private object MacrosRecord(JsonElement p, bool hasParams)
    {
        WatchMacros();
        var start = ReadBool(p, hasParams, "start") ?? false;
        var macroId = ReadString(p, hasParams, "macroId");
        var (ok, recording, activeId) = _services.Macros.SetRecording(start, macroId);
        return new { ok, recording, macroId = activeId };
    }

    private object MacrosTestRun(JsonElement p, bool hasParams, CancellationToken ct)
    {
        WatchMacros();
        var profileId = ReadString(p, hasParams, "profileId") ?? "";
        var bindingId = ReadString(p, hasParams, "bindingId") ?? "";
        return _services.Macros.TestRun(profileId, bindingId, ct);
    }
}

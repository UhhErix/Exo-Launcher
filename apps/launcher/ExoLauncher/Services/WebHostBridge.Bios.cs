using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// bios.update: PC › Updates' BIOS for ASUS boards (<see cref="BiosPrep"/>). No action: what is staged. "prepare":
/// downloaded from ASUS, checked against ASUS's SHA-256 and put on the EFI partition for EZ Flash, BitLocker paused for
/// one restart; Windows' one prompt is its confirm. "restart": the PC restarts into the BIOS, asked in place on the page
/// (DESIGN.md › Confirm), what to do there on its row. "windows": a plain restart (the driver screen's Restart now). "bios" and "recovery": a restart into either, on any PC, asked the
/// same way, with Windows' prompt after it. Each answers with what is staged after.
/// </summary>
public sealed partial class WebHostBridge
{
    private async Task<object> BiosUpdateAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        string? message = null;
        if (action == "prepare")
            message = await Task.Run(() => BiosPrep.PrepareAsync(new Progress<double>(share => PostEvent("bios.progress", new { share })), ct)).ConfigureAwait(true);
        else if (action == "restart")
            message = BiosPrep.RestartIntoBios();
        else if (action == "windows")
            message = AnyGame() ? "Close the game first." : BiosPrep.RestartWindows();
        else if (action is "bios" or "recovery")
            message = AnyGame() ? "Close the game first." : BiosPrep.RestartInto(action == "bios");
        else if (action is not null) message = "Exo doesn't know that change.";
        var now = BiosPrep.Current();
        return new
        {
            ok = message is null,
            message,
            staged = now is null ? null : new { version = now.Version, file = now.File, bitlocker = now.BitLockerPaused },
        };
    }
}

/// <summary>achievements.preview: a sample in-game banner of a rarity and kind (Settings' Preview).</summary>
public sealed partial class WebHostBridge
{
    /// <summary>Settings › Achievements: banners on or off and their sound's volume (0 to 100), each saved as it changes.</summary>
    private void ApplyAchievementSettings(JsonElement p)
    {
        if (p.TryGetProperty("achievementBanners", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _services.Settings.UpdateProfile(s => s.AchievementBanners = on.GetBoolean());
        if (p.TryGetProperty("achievementVolume", out var volume) && volume.ValueKind == JsonValueKind.Number && volume.TryGetInt32(out var level))
            _services.Settings.UpdateProfile(s => s.AchievementVolume = Math.Clamp(level, 0, 100));
    }

    private static object AchievementsPreview(JsonElement p, bool hasParams)
    {
        var tier = ReadString(p, hasParams, "tier") is "rare" or "epic" or "legendary" or "completion" ? ReadString(p, hasParams, "tier")! : "common";
        Achievements.AchievementWatch.Preview(tier);
        return new { ok = true };
    }
}

/// <summary>Settings' FPS counter: its switch (which may show Windows' prompt) and state, its place, its preview (<see cref="FpsCounter"/>).</summary>
public sealed partial class WebHostBridge
{
    private void ApplyFpsCounterSettings(JsonElement p)
    {
        var changed = false;
        if (p.TryGetProperty("fpsCounterPosition", out var position) && position.ValueKind == JsonValueKind.String)
        {
            _services.Settings.UpdateProfile(s => s.FpsCounterPosition = FpsOverlay.PositionOf(position.GetString()));
            changed = true;
        }
        if (p.TryGetProperty("fpsCounterItems", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var chosen = FpsItems.Clean(items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!));
            _services.Settings.UpdateProfile(s => s.FpsCounterItems = chosen);
            changed = true;
        }
        if (changed) FpsCounter.SettingsChanged();
    }

    /// <summary>No "on": the state now. Off the window's thread: the state asks Windows, and the switch may wait on its prompt.</summary>
    private static async Task<object> FpsCounterRpcAsync(JsonElement p, bool hasParams)
    {
        bool? on = hasParams && p.TryGetProperty("on", out var wanted) && wanted.ValueKind is JsonValueKind.True or JsonValueKind.False ? wanted.GetBoolean() : null;
        var (now, state, message) = await Task.Run(() => on is { } value ? FpsCounter.Set(value) : FpsCounter.Status()).ConfigureAwait(true);
        return new { on = now, state, message };
    }
}

using System.Text.Json;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    /// <summary>Settings > Downloads: pauseDownloadsWhilePlaying, downloadHoursStart/End (null for any time), autoUpdateGames, autoUpdateChipset.</summary>
    private void ApplyDownloadSettings(JsonElement p)
    {
        bool? pause = p.TryGetProperty("pauseDownloadsWhilePlaying", out var pw) && pw.ValueKind is JsonValueKind.True or JsonValueKind.False ? pw.GetBoolean() : null;
        bool? auto = p.TryGetProperty("autoUpdateGames", out var au) && au.ValueKind is JsonValueKind.True or JsonValueKind.False ? au.GetBoolean() : null;
        bool? chipset = p.TryGetProperty("autoUpdateChipset", out var ac) && ac.ValueKind is JsonValueKind.True or JsonValueKind.False ? ac.GetBoolean() : null;
        bool? upscalers = p.TryGetProperty("autoUpdateUpscalers", out var au2) && au2.ValueKind is JsonValueKind.True or JsonValueKind.False ? au2.GetBoolean() : null;
        bool? keepAwake = p.TryGetProperty("keepAwakeDownloads", out var ka) && ka.ValueKind is JsonValueKind.True or JsonValueKind.False ? ka.GetBoolean() : null;
        bool? antiCheat = p.TryGetProperty("upscalerAntiCheatEverywhere", out var ae) && ae.ValueKind is JsonValueKind.True or JsonValueKind.False ? ae.GetBoolean() : null;
        int? start = null, end = null;
        var anyTime = false;
        var hasStart = p.TryGetProperty("downloadHoursStart", out var hs);
        var hasEnd = p.TryGetProperty("downloadHoursEnd", out var he);
        if (hasStart && hs.ValueKind == JsonValueKind.Null) anyTime = true;
        else if (hasStart && hasEnd && hs.ValueKind == JsonValueKind.Number && he.ValueKind == JsonValueKind.Number &&
                 hs.TryGetInt32(out var s) && he.TryGetInt32(out var e)) (start, end) = (s, e);
        // Each field on its own is a change: the upscaler switches were missing here, so saving one alone did nothing.
        if (pause is null && auto is null && chipset is null && keepAwake is null && upscalers is null && antiCheat is null && start is null && !anyTime) return;
        _services.Settings.ApplyDownloadPatch(pause, start, end, anyTime, auto, chipset, keepAwake, upscalers, antiCheat);
        _services.Downloads.Apply();
    }


    /// <summary>The download manager's list (LaunchOrchestrator.Queue.cs), each game named from the library where the job has no title.</summary>
    private object DownloadsList()
    {
        var (lines, held, pausedByPlayer) = _services.Launcher.DownloadsSnapshot();
        var library = _services.Library.PeekCachedLibrary().ToDictionary(g => g.Id, g => g.Title, StringComparer.OrdinalIgnoreCase);
        return new
        {
            ok = true,
            held,
            pausedByPlayer,
            autoUpdateGames = _services.Settings.Current.AutoUpdateGames,
            lines = lines.Select(line => new
            {
                gameId = line.GameId,
                title = line.Title ?? library.GetValueOrDefault(line.GameId) ?? line.GameId,
                store = line.Store,
                state = line.State,
                canPause = line.CanPause,
                autoUpdate = !_services.Settings.AutoUpdateOff(line.GameId),
            }).ToArray(),
        };
    }

    /// <summary>A game's Update automatically switch: read without <c>on</c>; set with it.</summary>
    private object GameAutoUpdate(JsonElement p, bool hasParams)
    {
        var id = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(id)) return new { ok = false, message = "Game not found." };
        if (hasParams && p.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _services.Settings.SetAutoUpdateOff(id, !on.GetBoolean());
        return new { ok = true, on = !_services.Settings.AutoUpdateOff(id) };
    }
}

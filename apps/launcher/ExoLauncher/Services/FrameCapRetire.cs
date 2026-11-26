using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static class FrameCapRetire
{
    internal const string BackendOwnInterface = "own_interface";
    internal const string BackendNvidiaDrs = "nvidia_drs";
    internal const string BackendIntelIgcl = "intel_igcl";
    internal const string BackendAmdChill = "amd_chill";

    internal static IReadOnlyList<string> Run(IReadOnlyDictionary<string, GameFrameCapPref> caps, Func<string, GameEntry?> find)
    {
        var kept = new List<string>();
        foreach (var (gameId, pref) in caps)
        {
            if (string.IsNullOrWhiteSpace(pref.Backend)) continue;
            if (find(gameId) is not { } game) { kept.Add(gameId); continue; }
            string? error;
            try { error = Remove(game, pref); }
            catch (Exception ex) { error = ex.GetType().Name; }
            if (error is null) continue;
            AppLog.Warn($"Frame cap for {game.Title} not taken out yet: {error}");
            kept.Add(gameId);
        }
        return kept;
    }

    private static string? Remove(GameEntry game, GameFrameCapPref pref)
    {
        switch (pref.Backend)
        {
            case BackendNvidiaDrs:
            {
                if (ResolveExe(game) is not { } exe) return null;
                uint? before = uint.TryParse(pref.PriorValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;
                var result = GpuProfileService.SetGameFrameCap(game, exe, before);
                return result.Ok ? null : result.Message ?? "the NVIDIA profile did not change";
            }
            case BackendIntelIgcl:
                return IntelProfileService.Shared.SetFrameLimitCap(game.Id, ResolveExe(game), null);
            case BackendAmdChill:
                return RadeonProfileService.Shared.SetChillCap(game.Id, null) ? null : "the Radeon profile did not change";
            case BackendOwnInterface:
                return RestoreOwnInterfaceCap(game, pref.PriorValue);
            default:
                return null;
        }
    }

    private static string? RestoreOwnInterfaceCap(GameEntry game, string? priorValue)
    {
        if (!game.Installed || string.IsNullOrWhiteSpace(game.Path) || AntiCheatCheck.IsRiot(game)) return null;
        try
        {
            var (file, key, format) = Target(game);
            if (file is null || !File.Exists(file)) return null;
            var restoreTo = !string.IsNullOrWhiteSpace(priorValue) ? priorValue : format == "ini_float" ? "0.000000" : "0";
            var raw = File.ReadAllText(file, Encoding.UTF8);
            var pattern = format == "json"
                ? $"(\"{Regex.Escape(key!)}\"\\s*:\\s*)(-?\\d+(?:\\.\\d+)?)"
                : $"^(\\s*{Regex.Escape(key!)}\\s*=\\s*)([^\\r\\n]+)";
            var rx = new Regex(pattern, RegexOptions.Multiline);
            if (!rx.IsMatch(raw)) return null;
            File.WriteAllText(file, rx.Replace(raw, m => m.Groups[1].Value + restoreTo, 1), Encoding.UTF8);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static (string? File, string? Key, string? Format) Target(GameEntry game)
    {
        var path = game.Path!;
        if (GameSettings.IsRocketLeague(path)) return (GameSettings.RocketLeague(new GameSettings.Pc(null, null, null)).File, "CustomFPS", "ini");
        if (GameSettings.IsControlResonant(path)) return (GameSettings.ControlResonantConfig, "m_maxFrameRate", "json");
        if (GameHdr.UnrealProjects(path) is [var project, ..] && GameHdr.UnrealSettingsFile(project, game) is { } ue && File.Exists(ue) &&
            File.ReadAllText(ue, Encoding.Latin1).Contains("FrameRateLimit=", StringComparison.Ordinal))
            return (ue, "FrameRateLimit", "ini_float");
        return (null, null, null);
    }

    private static string? ResolveExe(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.Path) || !Directory.Exists(game.Path)) return null;
        if (GameHdr.MainProgram(game.Path) is { } main && File.Exists(main)) return main;
        try
        {
            return Directory.EnumerateFiles(game.Path, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

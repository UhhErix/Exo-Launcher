using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal enum FrameGenAntiCheatVerdict
{
    DllSafe,
    Unavailable,
}

internal sealed record FrameGenAntiCheatRule(
    string Id,
    string GameOrAntiCheat,
    FrameGenAntiCheatVerdict Verdict,
    bool RunsInOfflineMode,
    bool OfficialWithoutAntiCheatLaunch,
    bool CommunityBanReportsForDlls,
    string Reason,
    string Evidence);

internal static partial class FrameGenAntiCheat
{
    internal static readonly IReadOnlyList<FrameGenAntiCheatRule> PerGameRules =
    [
        new("steam:3472040", "NBA 2K26", FrameGenAntiCheatVerdict.Unavailable, false, true, true,
            "Easy Anti-Cheat",
            "EAC runs online; DLL mods are only allowed when the player picks the official Steam without-EAC offline launch entry."),
        new("steam:251570", "7 Days to Die", FrameGenAntiCheatVerdict.Unavailable, false, true, true,
            "Easy Anti-Cheat",
            "EAC is optional via Steam's official without-EAC launch entry; OptiScaler is only allowed when the player picks that entry."),
        new("steam:976730", "Halo: The Master Chief Collection", FrameGenAntiCheatVerdict.Unavailable, false, true, true,
            "Easy Anti-Cheat",
            "EAC is optional via Steam's official Anti-Cheat Disabled launch entry; DLL mods are never loaded while EAC is active."),
        new("steam:1245620", "ELDEN RING", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Easy Anti-Cheat",
            "EAC launches by default even in offline mode; DLL mods risk bans."),
        new("steam:1888160", "ARMORED CORE VI FIRES OF RUBICON", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Easy Anti-Cheat",
            "EAC launches by default; DLL injection is unsafe."),
        new("steam:271590", "Grand Theft Auto V", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "BattlEye",
            "BattlEye launches by default; Exo never passes anti-cheat-disabling flags or loads DLLs."),
        new("steam:3513350", "Wuthering Waves", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Anti-Cheat Expert",
            "Anti-Cheat Expert (ACE) kernel driver is active; DLL mods risk bans."),
        new("steam:1938090", "Call of Duty", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Activision Ricochet",
            "Activision Ricochet kernel anti-cheat blocks DLL mods."),
        new("steam:2000950", "Call of Duty: Modern Warfare II", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Activision Ricochet",
            "Activision Ricochet kernel anti-cheat blocks DLL mods."),
        new("steam:1962663", "Call of Duty: Warzone", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Activision Ricochet",
            "Activision Ricochet kernel anti-cheat blocks DLL mods."),
        new("riot:valorant", "VALORANT", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Riot Vanguard",
            "Riot Vanguard kernel anti-cheat blocks third-party DLLs."),
        new("riot:league_of_legends", "League of Legends", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Riot Vanguard",
            "Riot Vanguard kernel anti-cheat blocks third-party DLLs."),
        new("riot:teamfighttactics", "Teamfight Tactics", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Riot Vanguard",
            "Riot Vanguard kernel anti-cheat blocks third-party DLLs."),
        new("roblox:player", "Roblox", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Hyperion",
            "Hyperion anti-tamper blocks third-party DLLs."),
        new("steam:730", "Counter-Strike 2", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Valve Anti-Cheat",
            "VAC online blocks unsigned DLL injection."),
        new("steam:570", "Dota 2", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Valve Anti-Cheat",
            "VAC online; DLL injection is unsafe."),
        new("steam:1172470", "Apex Legends", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Easy Anti-Cheat",
            "EAC online; DLL injection is banned."),
        new("steam:553850", "HELLDIVERS 2", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "nProtect GameGuard",
            "nProtect GameGuard is active; DLL injection is blocked/banned."),
        new("steam:1085660", "Destiny 2", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "BattlEye",
            "BattlEye is active; DLL injection is banned."),
        new("steam:252490", "Rust", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Easy Anti-Cheat",
            "EAC online; DLL injection is banned."),
        new("steam:381210", "Dead by Daylight", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Easy Anti-Cheat",
            "EAC online; DLL injection is banned."),
        new("steam:2943650", "FragPunk", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "NetEase anti-cheat",
            "NetEase NeacSafe is active; DLL injection is banned."),
        new("steam:2767030", "Marvel Rivals", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "NetEase anti-cheat",
            "NetEase NeacSafe is active; DLL injection is banned."),
        new("steam:1551360", "Forza Horizon 5", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Forza anti-cheat",
            "Forza anti-cheat is active; DLL injection is unsafe."),
        new("steam:238960", "Path of Exile", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Path of Exile anti-cheat",
            "Path of Exile anti-cheat is active; DLL injection is unsafe."),
        new("steam:2694490", "Path of Exile 2", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Path of Exile anti-cheat",
            "Path of Exile anti-cheat is active; DLL injection is unsafe."),
        new("steam:230410", "Warframe", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Warframe anti-cheat",
            "Warframe anti-cheat is active; DLL injection is unsafe."),
        new("steam:359550", "Tom Clancy's Rainbow Six Siege", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "BattlEye",
            "BattlEye is active; DLL injection is banned."),
        new("steam:578080", "PUBG: BATTLEGROUNDS", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "BattlEye",
            "BattlEye is active; DLL injection is banned."),
        new("steam:2073850", "THE FINALS", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Easy Anti-Cheat",
            "EAC / Elytra is active; DLL injection is banned."),
        new("steam:2507950", "Delta Force", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "Anti-Cheat Expert",
            "Anti-Cheat Expert (ACE) is active; DLL injection is banned."),
        new("steam:1517290", "Battlefield 2042", FrameGenAntiCheatVerdict.Unavailable, true, false, true,
            "EA Javelin",
            "EA Javelin kernel anti-cheat is active; DLL injection is banned."),
    ];

    private static readonly Dictionary<string, FrameGenAntiCheatRule> RulesById =
        PerGameRules.ToDictionary(rule => rule.Id, StringComparer.OrdinalIgnoreCase);

    internal static FrameGenAntiCheatVerdict Evaluate(GameEntry? game, string? antiCheatLabel)
    {
        if (game is not null && AntiCheatCheck.IsRiot(game)) return FrameGenAntiCheatVerdict.Unavailable;
        if (antiCheatLabel is null) return FrameGenAntiCheatVerdict.DllSafe;
        if (AntiCheatCheck.IsUnknownLabel(antiCheatLabel)) return FrameGenAntiCheatVerdict.Unavailable;
        if (game is not null && RulesById.TryGetValue(game.Id, out var rule)) return rule.Verdict;
        if (game is not null && TitleBlocked().IsMatch(game.Title ?? string.Empty)) return FrameGenAntiCheatVerdict.Unavailable;
        return FrameGenAntiCheatVerdict.Unavailable;
    }

    internal static string UnavailableMessage(GameEntry? game, string? antiCheatLabel)
    {
        var reason = ReasonFor(game, antiCheatLabel);
        return $"Frame generation isn't available here: {reason}";
    }

    internal static string ReasonFor(GameEntry? game, string? antiCheatLabel)
    {
        if (game is not null && AntiCheatCheck.IsRiot(game)) return DlssSwapService.RiotVanguard;
        if (game is not null && RulesById.TryGetValue(game.Id, out var rule)) return rule.Reason;
        if (string.IsNullOrWhiteSpace(antiCheatLabel) || AntiCheatCheck.IsUnknownLabel(antiCheatLabel))
            return "anti-cheat could not be verified";
        return antiCheatLabel;
    }

    internal static FrameGenAntiCheatRule? RuleFor(GameEntry game) =>
        RulesById.GetValueOrDefault(game.Id);

    internal static bool CanOfferOfflineLaunch(GameEntry game) =>
        game.Store == StoreKind.Steam &&
        !AntiCheatCheck.IsRiot(game) &&
        AntiCheatCheck.SteamAppId(game) is { } appId &&
        AntiCheatCheck.SteamFreeEntry(appId) is not null;

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:valorant|league of legends|teamfight tactics|call of duty|warzone|faceit|roblox)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleBlocked();
}

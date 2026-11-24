using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal enum FrameGenAntiCheatVerdict
{
    DllSafe,
    NeedsConfirmation,
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
        new("steam:3472040", "NBA 2K26", FrameGenAntiCheatVerdict.NeedsConfirmation, false, true, true,
            "Easy Anti-Cheat",
            "EAC runs online; official Steam without-EAC offline launch entry is clean when chosen by the user, otherwise DLL mods require explicit ban-risk confirmation."),
        new("steam:251570", "7 Days to Die", FrameGenAntiCheatVerdict.NeedsConfirmation, false, true, true,
            "Easy Anti-Cheat",
            "EAC is optional via Steam's official without-EAC launch entry; with EAC active, DLL mods require explicit ban-risk confirmation."),
        new("steam:976730", "Halo: The Master Chief Collection", FrameGenAntiCheatVerdict.NeedsConfirmation, false, true, true,
            "Easy Anti-Cheat",
            "EAC is optional via Steam's official Anti-Cheat Disabled launch entry; with EAC active, DLL mods require explicit ban-risk confirmation."),
        new("steam:1245620", "ELDEN RING", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Easy Anti-Cheat",
            "EAC launches by default even in offline mode; DLL mods require explicit ban-risk confirmation."),
        new("steam:1888160", "ARMORED CORE VI FIRES OF RUBICON", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Easy Anti-Cheat",
            "EAC launches by default; DLL mods require explicit ban-risk confirmation."),
        new("steam:271590", "Grand Theft Auto V", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "BattlEye",
            "BattlEye launches by default; DLL mods require explicit ban-risk confirmation."),
        new("steam:3513350", "Wuthering Waves", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Anti-Cheat Expert",
            "Anti-Cheat Expert (ACE) kernel driver is active; prefer built-in FSR 3 / XeSS FG + NVIDIA DRS profile, with opt-in DLL mod behind explicit ban-risk confirmation."),
        new("steam:1938090", "Call of Duty", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Activision Ricochet",
            "Activision Ricochet kernel anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:2000950", "Call of Duty: Modern Warfare II", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Activision Ricochet",
            "Activision Ricochet kernel anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:1962663", "Call of Duty: Warzone", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Activision Ricochet",
            "Activision Ricochet kernel anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
        new("riot:valorant", "VALORANT", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Riot Vanguard",
            "Riot Vanguard kernel anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
        new("riot:league_of_legends", "League of Legends", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Riot Vanguard",
            "Riot Vanguard kernel anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
        new("riot:teamfighttactics", "Teamfight Tactics", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Riot Vanguard",
            "Riot Vanguard kernel anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
        new("roblox:player", "Roblox", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Hyperion",
            "Hyperion anti-tamper is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:730", "Counter-Strike 2", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Valve Anti-Cheat",
            "VAC online is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:570", "Dota 2", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Valve Anti-Cheat",
            "VAC online is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:1172470", "Apex Legends", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Easy Anti-Cheat",
            "EAC online is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:553850", "HELLDIVERS 2", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "nProtect GameGuard",
            "nProtect GameGuard is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:1085660", "Destiny 2", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "BattlEye",
            "BattlEye is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:252490", "Rust", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Easy Anti-Cheat",
            "EAC online is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:381210", "Dead by Daylight", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Easy Anti-Cheat",
            "EAC online is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:2943650", "FragPunk", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "NetEase anti-cheat",
            "NetEase NeacSafe is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:2767030", "Marvel Rivals", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "NetEase anti-cheat",
            "NetEase NeacSafe is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:1551360", "Forza Horizon 5", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Forza anti-cheat",
            "Forza anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:238960", "Path of Exile", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Path of Exile anti-cheat",
            "Path of Exile anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:2694490", "Path of Exile 2", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Path of Exile anti-cheat",
            "Path of Exile anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:230410", "Warframe", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Warframe anti-cheat",
            "Warframe anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:359550", "Tom Clancy's Rainbow Six Siege", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "BattlEye",
            "BattlEye is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:578080", "PUBG: BATTLEGROUNDS", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "BattlEye",
            "BattlEye is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:2073850", "THE FINALS", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Easy Anti-Cheat",
            "EAC / Elytra is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:2507950", "Delta Force", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "Anti-Cheat Expert",
            "Anti-Cheat Expert (ACE) is active; DLL mods require explicit ban-risk confirmation."),
        new("steam:1517290", "Battlefield 2042", FrameGenAntiCheatVerdict.NeedsConfirmation, true, false, true,
            "EA Javelin",
            "EA Javelin kernel anti-cheat is active; DLL mods require explicit ban-risk confirmation."),
    ];

    private static readonly Dictionary<string, FrameGenAntiCheatRule> RulesById =
        PerGameRules.ToDictionary(rule => rule.Id, StringComparer.OrdinalIgnoreCase);

    internal static FrameGenAntiCheatVerdict Evaluate(GameEntry? game, string? antiCheatLabel)
    {
        if (game is not null && AntiCheatCheck.IsRiot(game)) return FrameGenAntiCheatVerdict.NeedsConfirmation;
        if (antiCheatLabel is null) return FrameGenAntiCheatVerdict.DllSafe;
        if (AntiCheatCheck.IsUnknownLabel(antiCheatLabel)) return FrameGenAntiCheatVerdict.NeedsConfirmation;
        if (game is not null && RulesById.TryGetValue(game.Id, out var rule)) return rule.Verdict;
        if (game is not null && TitleProtected().IsMatch(game.Title ?? string.Empty)) return FrameGenAntiCheatVerdict.NeedsConfirmation;
        return FrameGenAntiCheatVerdict.NeedsConfirmation;
    }

    internal static string ConfirmationWarning(GameEntry? game, string? antiCheatLabel)
    {
        var title = string.IsNullOrWhiteSpace(game?.Title) ? "This game" : game.Title.Trim();
        var ac = ReasonFor(game, antiCheatLabel);
        return $"{title} runs {ac}. A mod loading into it can get the account banned.";
    }

    internal static string ReasonFor(GameEntry? game, string? antiCheatLabel)
    {
        if (game is not null && AntiCheatCheck.IsRiot(game)) return DlssSwapService.RiotVanguard;
        if (game is not null && RulesById.TryGetValue(game.Id, out var rule)) return rule.Reason;
        if (string.IsNullOrWhiteSpace(antiCheatLabel) || AntiCheatCheck.IsUnknownLabel(antiCheatLabel))
            return "anti-cheat";
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
    private static partial Regex TitleProtected();
}

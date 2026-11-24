using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal enum FrameGenAntiCheatVerdict
{
    DllSafe,
    LosslessOnly,
    Blocked,
}

internal sealed record FrameGenAntiCheatRule(
    string Id,
    string GameOrAntiCheat,
    FrameGenAntiCheatVerdict Verdict,
    bool RunsInOfflineMode,
    bool OfficialWithoutAntiCheatLaunch,
    bool CommunityBanReportsForDlls,
    bool OverlayCaptureAllowed,
    string Evidence);

internal static partial class FrameGenAntiCheat
{
    internal static readonly IReadOnlyList<FrameGenAntiCheatRule> PerGameRules =
    [
        new("steam:3472040", "NBA 2K26", FrameGenAntiCheatVerdict.LosslessOnly, false, true, true, true,
            "EAC active online; official Steam launch entry without EAC (offline only) allows SessionMods DLLs; Lossless Scaling allowed with EAC active."),
        new("steam:251570", "7 Days to Die", FrameGenAntiCheatVerdict.LosslessOnly, false, true, true, true,
            "EAC optional via official Steam launch entry without EAC; OptiScaler confirmed safe with EAC off; Lossless Scaling allowed with EAC on."),
        new("steam:976730", "Halo: The Master Chief Collection", FrameGenAntiCheatVerdict.LosslessOnly, false, true, true, true,
            "EAC optional via official Steam Anti-Cheat Disabled launch entry; Lossless Scaling allowed with EAC on."),
        new("steam:1245620", "ELDEN RING", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "EAC launches by default even in offline mode; DLL mods risk bans unless EAC is absent; Lossless Scaling window capture allowed."),
        new("steam:1888160", "ARMORED CORE VI FIRES OF RUBICON", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "EAC launches by default; DLL injection unsafe; Lossless Scaling window capture allowed."),
        new("steam:271590", "Grand Theft Auto V", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "BattlEye launches by default; Exo never passes anti-cheat-disabling flags; Lossless Scaling window capture allowed."),
        new("steam:3513350", "Wuthering Waves", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "Anti-Cheat Expert (ACE) kernel driver active; DLL mods banned; Lossless Scaling window capture allowed in borderless windowed mode."),
        new("steam:1938090", "Call of Duty", FrameGenAntiCheatVerdict.Blocked, true, false, true, false,
            "Activision Ricochet kernel anti-cheat monitors overlays and capture aggressively; neither DLLs nor Lossless Scaling."),
        new("steam:2000950", "Call of Duty: Modern Warfare II", FrameGenAntiCheatVerdict.Blocked, true, false, true, false,
            "Activision Ricochet kernel anti-cheat monitors overlays and capture aggressively; neither DLLs nor Lossless Scaling."),
        new("steam:1962663", "Call of Duty: Warzone", FrameGenAntiCheatVerdict.Blocked, true, false, true, false,
            "Activision Ricochet kernel anti-cheat monitors overlays and capture aggressively; neither DLLs nor Lossless Scaling."),
        new("riot:valorant", "VALORANT", FrameGenAntiCheatVerdict.Blocked, true, false, true, false,
            "Riot Vanguard kernel anti-cheat restricts third-party hooks and capture overlays; neither DLLs nor Lossless Scaling."),
        new("riot:league_of_legends", "League of Legends", FrameGenAntiCheatVerdict.Blocked, true, false, true, false,
            "Riot Vanguard kernel anti-cheat restricts third-party hooks and capture overlays; neither DLLs nor Lossless Scaling."),
        new("riot:teamfighttactics", "Teamfight Tactics", FrameGenAntiCheatVerdict.Blocked, true, false, true, false,
            "Riot Vanguard kernel anti-cheat restricts third-party hooks and capture overlays; neither DLLs nor Lossless Scaling."),
        new("roblox:player", "Roblox", FrameGenAntiCheatVerdict.Blocked, true, false, true, false,
            "Hyperion anti-tamper blocks external overlays and capture hooks; neither DLLs nor Lossless Scaling."),
        new("steam:730", "Counter-Strike 2", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "VAC online blocks unsigned DLL injection; Lossless Scaling allowed on VAC servers (never with FACEIT AC)."),
        new("steam:570", "Dota 2", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "VAC online; no DLL injection; Lossless Scaling window capture allowed."),
        new("steam:1172470", "Apex Legends", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "EAC online; DLL injection banned; Lossless Scaling window capture allowed in borderless mode."),
        new("steam:553850", "HELLDIVERS 2", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "nProtect GameGuard active; DLL injection blocked/banned; Lossless Scaling window capture allowed."),
        new("steam:1085660", "Destiny 2", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "BattlEye active; DLL injection banned; Lossless Scaling window capture allowed in borderless windowed mode."),
        new("steam:252490", "Rust", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "EAC online; DLL injection banned; Lossless Scaling window capture allowed."),
        new("steam:381210", "Dead by Daylight", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "EAC online; DLL injection banned; Lossless Scaling window capture allowed."),
        new("steam:2943650", "FragPunk", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "NetEase NeacSafe active; DLL injection banned; Lossless Scaling window capture allowed."),
        new("steam:2767030", "Marvel Rivals", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "NetEase NeacSafe active; DLL injection banned; Lossless Scaling window capture allowed."),
        new("steam:1551360", "Forza Horizon 5", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "Forza anti-cheat active; DLL injection unsafe; Lossless Scaling window capture allowed."),
        new("steam:238960", "Path of Exile", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "Path of Exile anti-cheat active; DLL injection unsafe; Lossless Scaling window capture allowed."),
        new("steam:2694490", "Path of Exile 2", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "Path of Exile anti-cheat active; DLL injection unsafe; Lossless Scaling window capture allowed."),
        new("steam:230410", "Warframe", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "Warframe anti-cheat active; DLL injection unsafe; Lossless Scaling window capture allowed."),
        new("steam:359550", "Tom Clancy's Rainbow Six Siege", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "BattlEye active; DLL injection banned; Lossless Scaling window capture allowed in borderless mode."),
        new("steam:578080", "PUBG: BATTLEGROUNDS", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "BattlEye active; DLL injection banned; Lossless Scaling window capture allowed."),
        new("steam:2073850", "THE FINALS", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "EAC / Elytra active; DLL injection banned; Lossless Scaling window capture allowed."),
        new("steam:2507950", "Delta Force", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "Anti-Cheat Expert (ACE) active; DLL injection banned; Lossless Scaling window capture allowed."),
        new("steam:1517290", "Battlefield 2042", FrameGenAntiCheatVerdict.LosslessOnly, true, false, true, true,
            "EA Javelin kernel anti-cheat active; DLL injection banned; Lossless Scaling window capture allowed."),
    ];

    private static readonly Dictionary<string, FrameGenAntiCheatRule> RulesById =
        PerGameRules.ToDictionary(rule => rule.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> OverlayBlockingAntiCheats = new(StringComparer.OrdinalIgnoreCase)
    {
        DlssSwapService.RiotVanguard,
        "Ricochet",
        "FACEIT",
        "Hyperion",
    };

    internal static FrameGenAntiCheatVerdict Evaluate(GameEntry? game, string? antiCheatLabel)
    {
        if (game is not null && AntiCheatCheck.IsRiot(game)) return FrameGenAntiCheatVerdict.Blocked;
        if (antiCheatLabel is null) return FrameGenAntiCheatVerdict.DllSafe;
        if (AntiCheatCheck.IsUnknownLabel(antiCheatLabel)) return FrameGenAntiCheatVerdict.Blocked;
        var names = AntiCheatCheck.Split(antiCheatLabel);
        if (names.Any(name => OverlayBlockingAntiCheats.Contains(name) || AntiCheatCheck.IsUnknownLabel(name)))
            return FrameGenAntiCheatVerdict.Blocked;
        if (game is not null && RulesById.TryGetValue(game.Id, out var rule)) return rule.Verdict;
        if (game is not null && TitleBlocked().IsMatch(game.Title ?? string.Empty)) return FrameGenAntiCheatVerdict.Blocked;
        return FrameGenAntiCheatVerdict.LosslessOnly;
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

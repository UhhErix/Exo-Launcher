using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal enum FrameGenAntiCheatVerdict
{
    DllSafe,
    OfflineOfficialOnly,
    OnlineNoMods,
}

internal sealed record FrameGenAntiCheatRule(
    string Id,
    string GameOrAntiCheat,
    FrameGenAntiCheatVerdict Verdict,
    bool RunsInOfflineMode,
    bool OfficialWithoutAntiCheatLaunch,
    bool CommunityBanReportsForDlls,
    string Reason,
    string Source,
    string Evidence);

internal static partial class FrameGenAntiCheat
{
    internal const string OnlineBlockedMessage = "Mods would get you banned";

    internal static readonly IReadOnlyList<FrameGenAntiCheatRule> PerGameRules =
    [
        new("steam:3472040", "NBA 2K26", FrameGenAntiCheatVerdict.OfflineOfficialOnly, false, true, true,
            "Easy Anti-Cheat",
            "Steam appinfo launch option (without EAC offline only)",
            "EAC runs online; official Steam without-EAC offline launch entry allows DLL mods with a one-time note."),
        new("steam:251570", "7 Days to Die", FrameGenAntiCheatVerdict.OfflineOfficialOnly, false, true, true,
            "Easy Anti-Cheat",
            "Steam appinfo launch option (without EAC)",
            "EAC is optional via Steam's official without-EAC launch entry; DLL mods run only in that official offline mode with a one-time note."),
        new("steam:976730", "Halo: The Master Chief Collection", FrameGenAntiCheatVerdict.OfflineOfficialOnly, false, true, true,
            "Easy Anti-Cheat",
            "Steam appinfo launch option (Anti-Cheat Disabled)",
            "EAC is optional via Steam's official Anti-Cheat Disabled launch entry; DLL mods run only in that official offline mode with a one-time note."),
        new("steam:1245620", "ELDEN RING", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Easy Anti-Cheat",
            "FromSoftware / Bandai Namco Steam package (no official without-EAC launch entry)",
            "EAC launches by default with no official without-EAC mode; only built-in or driver frame generation routes are allowed."),
        new("steam:1888160", "ARMORED CORE VI FIRES OF RUBICON", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Easy Anti-Cheat",
            "FromSoftware / Bandai Namco Steam package (no official without-EAC launch entry)",
            "EAC launches by default with no official without-EAC mode; only built-in or driver frame generation routes are allowed."),
        new("steam:271590", "Grand Theft Auto V", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "BattlEye",
            "Rockstar Games / BattlEye integration",
            "BattlEye runs by default with no official Steam without-AC launch entry; only built-in or driver frame generation routes are allowed."),
        new("steam:3513350", "Wuthering Waves", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Anti-Cheat Expert",
            "Kuro Games / Anti-Cheat Expert (ACE) always-online client and local install scan",
            "Always-online ACE kernel anti-cheat; no DLL mods ever. Use built-in FSR 3 / XeSS FG under DX12 plus NVIDIA DRS profile."),
        new("steam:1938090", "Call of Duty", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Activision Ricochet",
            "Activision Ricochet kernel anti-cheat policy",
            "Always-online Ricochet kernel anti-cheat; no DLL mods."),
        new("steam:2000950", "Call of Duty: Modern Warfare II", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Activision Ricochet",
            "Activision Ricochet kernel anti-cheat policy",
            "Always-online Ricochet kernel anti-cheat; no DLL mods."),
        new("steam:1962663", "Call of Duty: Warzone", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Activision Ricochet",
            "Activision Ricochet kernel anti-cheat policy",
            "Always-online Ricochet kernel anti-cheat; no DLL mods."),
        new("riot:valorant", "VALORANT", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Riot Vanguard",
            "Riot Games / Vanguard kernel anti-cheat policy",
            "Always-online Vanguard kernel anti-cheat; no DLL mods."),
        new("riot:league_of_legends", "League of Legends", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Riot Vanguard",
            "Riot Games / Vanguard kernel anti-cheat policy",
            "Always-online Vanguard kernel anti-cheat; no DLL mods."),
        new("riot:teamfighttactics", "Teamfight Tactics", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Riot Vanguard",
            "Riot Games / Vanguard kernel anti-cheat policy",
            "Always-online Vanguard kernel anti-cheat; no DLL mods."),
        new("epic:fortnite", "Fortnite", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Easy Anti-Cheat",
            "Epic Games / Fortnite online competitive anti-cheat",
            "Always-online EAC / BattlEye competitive anti-cheat; no DLL mods."),
        new("roblox:player", "Roblox", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Hyperion",
            "Roblox Hyperion anti-tamper policy",
            "Always-online Hyperion anti-tamper; no DLL mods."),
        new("steam:730", "Counter-Strike 2", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Valve Anti-Cheat",
            "Valve VAC competitive servers",
            "Online VAC anti-cheat; no DLL mods."),
        new("steam:570", "Dota 2", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Valve Anti-Cheat",
            "Valve VAC competitive servers",
            "Online VAC anti-cheat; no DLL mods."),
        new("steam:1172470", "Apex Legends", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Easy Anti-Cheat",
            "EA / Respawn EAC online",
            "Online EAC anti-cheat; no DLL mods."),
        new("steam:553850", "HELLDIVERS 2", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "nProtect GameGuard",
            "Arrowhead / nProtect GameGuard always-online",
            "Always-online GameGuard anti-cheat; no DLL mods."),
        new("steam:1085660", "Destiny 2", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "BattlEye",
            "Bungie / BattlEye always-online policy",
            "Always-online BattlEye anti-cheat; no DLL mods."),
        new("steam:252490", "Rust", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Easy Anti-Cheat",
            "Facepunch / EAC online servers",
            "Online EAC anti-cheat; no DLL mods."),
        new("steam:381210", "Dead by Daylight", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Easy Anti-Cheat",
            "Behaviour / EAC online servers",
            "Online EAC anti-cheat; no DLL mods."),
        new("steam:2943650", "FragPunk", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "NetEase anti-cheat",
            "NetEase NeacSafe online competitive anti-cheat",
            "Always-online NeacSafe kernel anti-cheat; no DLL mods."),
        new("steam:2767030", "Marvel Rivals", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "NetEase anti-cheat",
            "NetEase NeacSafe online competitive anti-cheat",
            "Always-online NeacSafe kernel anti-cheat; no DLL mods."),
        new("steam:1551360", "Forza Horizon 5", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Forza anti-cheat",
            "Playground Games / Forza anti-tamper online",
            "Online Forza anti-cheat; no DLL mods."),
        new("steam:238960", "Path of Exile", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Path of Exile anti-cheat",
            "Grinding Gear Games always-online policy",
            "Always-online client; no DLL mods."),
        new("steam:2694490", "Path of Exile 2", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Path of Exile anti-cheat",
            "Grinding Gear Games always-online policy",
            "Always-online client; no DLL mods."),
        new("steam:230410", "Warframe", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Warframe anti-cheat",
            "Digital Extremes always-online policy",
            "Always-online client; no DLL mods."),
        new("steam:359550", "Tom Clancy's Rainbow Six Siege", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "BattlEye",
            "Ubisoft / BattlEye competitive servers",
            "Online BattlEye anti-cheat; no DLL mods."),
        new("steam:578080", "PUBG: BATTLEGROUNDS", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "BattlEye",
            "KRAFTON / BattlEye competitive servers",
            "Online BattlEye anti-cheat; no DLL mods."),
        new("steam:2073850", "THE FINALS", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Easy Anti-Cheat",
            "Embark Studios / EAC online competitive servers",
            "Online EAC / Elytra anti-cheat; no DLL mods."),
        new("steam:2507950", "Delta Force", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "Anti-Cheat Expert",
            "Team Jade / ACE online competitive servers",
            "Online ACE kernel anti-cheat; no DLL mods."),
        new("steam:1517290", "Battlefield 2042", FrameGenAntiCheatVerdict.OnlineNoMods, true, false, true,
            "EA Javelin",
            "EA Javelin kernel anti-cheat",
            "Online EA Javelin kernel anti-cheat; no DLL mods."),
    ];

    private static readonly Dictionary<string, FrameGenAntiCheatRule> RulesById =
        PerGameRules.ToDictionary(rule => rule.Id, StringComparer.OrdinalIgnoreCase);

    internal static FrameGenAntiCheatVerdict Evaluate(GameEntry? game, string? antiCheatLabel)
    {
        if (game is not null && AntiCheatCheck.IsRiot(game)) return FrameGenAntiCheatVerdict.OnlineNoMods;
        if (game is not null && RulesById.TryGetValue(game.Id, out var rule)) return rule.Verdict;
        if (game is not null && TitleProtected().IsMatch(game.Title ?? string.Empty)) return FrameGenAntiCheatVerdict.OnlineNoMods;
        if (antiCheatLabel is null)
        {
            if (game is not null && AntiCheatCheck.WithoutAntiCheatLaunch(game) is not null)
                return FrameGenAntiCheatVerdict.OfflineOfficialOnly;
            return FrameGenAntiCheatVerdict.DllSafe;
        }
        if (AntiCheatCheck.IsUnknownLabel(antiCheatLabel)) return FrameGenAntiCheatVerdict.OnlineNoMods;
        return FrameGenAntiCheatVerdict.OnlineNoMods;
    }

    internal static bool BlocksDllMods(GameEntry? game, string? antiCheatLabel)
    {
        if (game is not null && AntiCheatCheck.IsRiot(game)) return true;
        if (game is not null && RulesById.TryGetValue(game.Id, out var rule) && rule.Verdict == FrameGenAntiCheatVerdict.OnlineNoMods)
            return true;
        if (game is not null && TitleProtected().IsMatch(game.Title ?? string.Empty)) return true;
        return antiCheatLabel is not null;
    }

    internal static bool IsOfficialOfflineModeActive(GameEntry? game, string? evidence = null) =>
        game is not null &&
        !BlocksDllMods(game, null) &&
        (AntiCheatCheck.WithoutAntiCheatLaunch(game) is not null ||
         string.Equals(evidence, AntiCheatCheck.WithoutAntiCheatEvidence, StringComparison.OrdinalIgnoreCase));

    internal static string OfflineModeNote(GameEntry? game, string? antiCheatLabel = null)
    {
        var title = string.IsNullOrWhiteSpace(game?.Title) ? "This game" : game.Title.Trim();
        var ac = ReasonFor(game, antiCheatLabel);
        return $"{title} runs {ac} online. Exo uses frame generation mods only in its official offline (without anti-cheat) mode, and Off restores every file.";
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
        !(RulesById.TryGetValue(game.Id, out var rule) && rule.Verdict == FrameGenAntiCheatVerdict.OnlineNoMods) &&
        AntiCheatCheck.SteamAppId(game) is { } appId &&
        AntiCheatCheck.SteamFreeEntry(appId) is not null;

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:valorant|league of legends|teamfight tactics|call of duty|warzone|faceit|roblox|fortnite|wuthering waves|counter-strike 2|dota 2|apex legends|rainbow six siege|pubg)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleProtected();
}

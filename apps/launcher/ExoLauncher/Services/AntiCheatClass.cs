using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal enum GameAntiCheatClass
{
    NoAntiCheat,
    OfflineAntiCheat,
    OnlineNonCompetitivePve,
    CompetitivePvpOnline,
}

internal enum DllModPolicy
{
    Allowed,
    AllowedWithOfflineNote,
    OptInWithBanRiskConfirmation,
    Disallowed,
}

internal sealed record AntiCheatClassRule(
    string? GameId,
    string TitleKey,
    GameAntiCheatClass Class,
    string Notes);

internal static class AntiCheatClass
{
    internal static readonly IReadOnlyList<AntiCheatClassRule> PerGameTable =
    [
        new("steam:3513350", "wutheringwaves", GameAntiCheatClass.OnlineNonCompetitivePve, "Online gacha/PvE action RPG (Anti-Cheat Expert)."),
        new("local:wuthering-waves", "wutheringwaves", GameAntiCheatClass.OnlineNonCompetitivePve, "Online gacha/PvE action RPG (Anti-Cheat Expert)."),
        new(null, "genshinimpact", GameAntiCheatClass.OnlineNonCompetitivePve, "Online gacha/PvE action RPG (HoYoKProtect)."),
        new(null, "honkaistarrail", GameAntiCheatClass.OnlineNonCompetitivePve, "Online gacha/PvE RPG (HoYoKProtect)."),
        new(null, "zenlesszonezero", GameAntiCheatClass.OnlineNonCompetitivePve, "Online gacha/PvE action RPG (HoYoKProtect)."),
        new(null, "honkaiimpact3rd", GameAntiCheatClass.OnlineNonCompetitivePve, "Online gacha/PvE action game."),
        new(null, "toweroffantasy", GameAntiCheatClass.OnlineNonCompetitivePve, "Online PvE gacha RPG."),
        new(null, "arknightsendfield", GameAntiCheatClass.OnlineNonCompetitivePve, "Online gacha/PvE action RPG."),
        new(null, "punishinggrayraven", GameAntiCheatClass.OnlineNonCompetitivePve, "Online gacha/PvE action RPG."),
        new("steam:230410", "warframe", GameAntiCheatClass.OnlineNonCompetitivePve, "Online co-op PvE shooter."),
        new("steam:238960", "pathofexile", GameAntiCheatClass.OnlineNonCompetitivePve, "Online PvE action RPG."),
        new("steam:2694490", "pathofexile2", GameAntiCheatClass.OnlineNonCompetitivePve, "Online PvE action RPG."),
        new("steam:553850", "helldivers2", GameAntiCheatClass.OnlineNonCompetitivePve, "Online co-op PvE shooter (nProtect GameGuard)."),
        new("steam:2074920", "thefirstdescendant", GameAntiCheatClass.OnlineNonCompetitivePve, "Online co-op PvE shooter (EAC)."),
        new("steam:2246340", "monsterhunterwilds", GameAntiCheatClass.OnlineNonCompetitivePve, "Online co-op PvE action RPG."),

        new("steam:1245620", "eldenring", GameAntiCheatClass.OfflineAntiCheat, "Single-player/offline-capable title shipping EAC."),
        new("steam:1888160", "armoredcorevifiresofrubicon", GameAntiCheatClass.OfflineAntiCheat, "Single-player/offline-capable title shipping EAC."),
        new("steam:251570", "7daystodie", GameAntiCheatClass.OfflineAntiCheat, "Official offline/no-EAC launch mode supported."),
        new("steam:976730", "halothemasterchiefcollection", GameAntiCheatClass.OfflineAntiCheat, "Official Anti-Cheat Disabled offline campaign mode supported."),

        new("riot:valorant", "valorant", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP shooter (Riot Vanguard)."),
        new("riot:league_of_legends", "leagueoflegends", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP MOBA (Riot Vanguard)."),
        new("riot:teamfighttactics", "teamfighttactics", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP autobattler (Riot Vanguard)."),
        new("riot:lion", "2xko", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP fighting game (Riot Vanguard)."),
        new("steam:1938090", "callofdutymodernwarfare4", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP shooter (Activision Ricochet)."),
        new("steam:2000950", "callofdutymodernwarfare", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP shooter (Activision Ricochet)."),
        new("steam:1962663", "callofdutywarzone", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP battle royale (Activision Ricochet)."),
        new("steam:730", "counterstrike2", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP shooter (VAC)."),
        new("steam:570", "dota2", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP MOBA (VAC)."),
        new("steam:440", "teamfortress2", GameAntiCheatClass.CompetitivePvpOnline, "Online PvP shooter (VAC)."),
        new(null, "fortnite", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP battle royale (EAC / BattlEye)."),
        new("steam:1172470", "apexlegends", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP battle royale (EAC)."),
        new("steam:359550", "tomclancysrainbowsixsiege", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP tactical shooter (BattlEye)."),
        new("roblox:player", "roblox", GameAntiCheatClass.CompetitivePvpOnline, "Online multiplayer platform (Hyperion)."),
        new(null, "overwatch2", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP hero shooter."),
        new(null, "escapefromtarkov", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP extraction shooter (BattlEye)."),
        new("steam:2073620", "arenabreakoutinfinite", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP extraction shooter (ACE)."),
        new("steam:2507950", "deltaforce", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP shooter (ACE)."),
        new("steam:2943650", "fragpunk", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP shooter (NetEase NeacSafe)."),
        new("steam:2767030", "marvelrivals", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP hero shooter (NetEase NeacSafe)."),
        new("steam:2073850", "thefinals", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP shooter (EAC)."),
        new("steam:578080", "pubgbattlegrounds", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP battle royale (BattlEye)."),
        new("steam:252490", "rust", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP survival (EAC)."),
        new("steam:381210", "deadbydaylight", GameAntiCheatClass.CompetitivePvpOnline, "Online asymmetric PvP (EAC)."),
        new("epic:Sugar", "rocketleague", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP sports game (EAC)."),
        new("steam:3472040", "nba2k26", GameAntiCheatClass.CompetitivePvpOnline, "Online competitive sports game (EAC active unless official offline mode chosen)."),
        new("steam:1085660", "destiny2", GameAntiCheatClass.CompetitivePvpOnline, "Online PvP/PvE shooter (BattlEye)."),
        new("steam:1517290", "battlefield2042", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP shooter (EA Javelin)."),
        new("steam:2807960", "battlefield6", GameAntiCheatClass.CompetitivePvpOnline, "Competitive PvP shooter (EA Javelin)."),
        new("steam:1551360", "forzahorizon5", GameAntiCheatClass.CompetitivePvpOnline, "Online racing game."),
    ];

    private static readonly Dictionary<string, GameAntiCheatClass> ById = PerGameTable
        .Where(r => !string.IsNullOrEmpty(r.GameId))
        .GroupBy(r => r.GameId!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Class, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, GameAntiCheatClass> ByTitle = PerGameTable
        .GroupBy(r => r.TitleKey, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Class, StringComparer.Ordinal);

    internal static GameAntiCheatClass Classify(GameEntry game, AntiCheatCheck.Answer antiCheat)
    {
        if (AntiCheatCheck.IsRiot(game) || antiCheat.Names.Contains(DlssSwapService.RiotVanguard))
            return GameAntiCheatClass.CompetitivePvpOnline;

        if (antiCheat.Kind == AntiCheatCheck.Kind.None)
        {
            return string.Equals(antiCheat.Evidence, AntiCheatCheck.WithoutAntiCheatEvidence, StringComparison.Ordinal)
                ? GameAntiCheatClass.OfflineAntiCheat
                : GameAntiCheatClass.NoAntiCheat;
        }

        if (antiCheat.Kind == AntiCheatCheck.Kind.Unknown)
            return GameAntiCheatClass.CompetitivePvpOnline;

        if (!string.IsNullOrEmpty(game.Id) && ById.TryGetValue(game.Id, out var byId))
            return byId;

        var key = StoreService.TitleKey(game.Title ?? "");
        if (key.Length > 0 && ByTitle.TryGetValue(key, out var byTitle))
            return byTitle;

        if (key.StartsWith("callofduty", StringComparison.Ordinal) || key.StartsWith("battlefield", StringComparison.Ordinal))
            return GameAntiCheatClass.CompetitivePvpOnline;

        return GameAntiCheatClass.CompetitivePvpOnline;
    }

    internal static string WireClass(GameEntry game, AntiCheatCheck.Answer? cachedAnswer)
    {
        if (AntiCheatCheck.IsRiot(game) || cachedAnswer is { Names: var names } && names.Contains(DlssSwapService.RiotVanguard))
            return "competitivePvpOnline";

        if (cachedAnswer is { Kind: AntiCheatCheck.Kind.None } clean)
        {
            return ToWire(Classify(game, clean));
        }

        if (!string.IsNullOrEmpty(game.Id) && ById.TryGetValue(game.Id, out var byId))
            return ToWire(byId);

        var key = StoreService.TitleKey(game.Title ?? "");
        if (key.Length > 0 && ByTitle.TryGetValue(key, out var byTitle))
            return ToWire(byTitle);

        if (key.StartsWith("callofduty", StringComparison.Ordinal) || key.StartsWith("battlefield", StringComparison.Ordinal))
            return "competitivePvpOnline";

        if (cachedAnswer is null || cachedAnswer.Value.Kind == AntiCheatCheck.Kind.Unknown)
        {
            return AntiCheatCheck.Quick(game) is not null ? "competitivePvpOnline" : "unknown";
        }

        return ToWire(Classify(game, cachedAnswer.Value));
    }

    private static string ToWire(GameAntiCheatClass acClass) => acClass switch
    {
        GameAntiCheatClass.NoAntiCheat or GameAntiCheatClass.OfflineAntiCheat => "offlineAntiCheat",
        GameAntiCheatClass.OnlineNonCompetitivePve => "onlineNonCompetitivePve",
        _ => "competitivePvpOnline",
    };

    internal static DllModPolicy DllPolicyFor(GameAntiCheatClass acClass) => acClass switch
    {
        GameAntiCheatClass.NoAntiCheat => DllModPolicy.Allowed,
        GameAntiCheatClass.OfflineAntiCheat => DllModPolicy.AllowedWithOfflineNote,
        GameAntiCheatClass.OnlineNonCompetitivePve => DllModPolicy.OptInWithBanRiskConfirmation,
        _ => DllModPolicy.Disallowed,
    };

    internal static bool AllowsAutoInstallDll(GameAntiCheatClass acClass) =>
        acClass == GameAntiCheatClass.NoAntiCheat;

    internal static string BanRiskConfirmation(string gameTitle, string? antiCheatName)
    {
        var title = string.IsNullOrWhiteSpace(gameTitle) ? "This game" : gameTitle.Trim();
        var ac = string.IsNullOrWhiteSpace(antiCheatName) ? "anti-cheat" : antiCheatName.Trim();
        return $"{title} runs {ac}. A mod loading into it can get the account banned.";
    }

    internal static string OfflineOneTimeNote(string gameTitle)
    {
        var title = string.IsNullOrWhiteSpace(gameTitle) ? "This game" : gameTitle.Trim();
        return $"{title} runs in offline mode with anti-cheat. Mods load only in offline play.";
    }
}

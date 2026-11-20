using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>An automatic EQ profile selected for a game by Exo without manual tuning.</summary>
internal sealed record EqGameProfile(string Sound, string Label, string Summary);

/// <summary>A running library game paired with its automatically selected Exo EQ profile.</summary>
internal sealed record EqActiveGame(string Id, string Title, string Sound, string Label, string Summary);

/// <summary>
/// Zero-config per-game Exo EQ classification. Maps the game's title key, Steam tags, and PCGamingWiki genres/modes to
/// one of four purpose-built curves: Reference (rhythm/strategy/sandbox/MOBA), Footsteps (competitive shooters/extraction),
/// Vocal (story/RPG/narrative), or Immersion (cinematic action/soulslike/racing/sports/fighting/horror).
/// </summary>
internal static class ExoEqProfiles
{
    private static readonly EqGameProfile Reference = new(ExoEqSounds.Flat, "Reference", "Studio neutral balance for rhythm, strategy, and timing");
    private static readonly EqGameProfile Footsteps = new(ExoEqSounds.Footsteps, "Footsteps", "Cuts explosion rumble and lifts 2–4.5 kHz footsteps & reloads");
    private static readonly EqGameProfile Vocal = new(ExoEqSounds.Vocal, "Vocal", "Clears lower-mid mud and brings dialogue & narrative detail forward");
    private static readonly EqGameProfile Immersion = new(ExoEqSounds.Cinematic, "Immersion", "Adds warm sub-bass punch and spatial air without muddying mids");

    private static readonly (string[] Exact, string[] Prefixes, EqGameProfile Profile)[] TitleBuckets =
    [
        (
            [
                "osu", "beatsaber", "geometrydash", "clonehero", "hifirush", "musedash", "djmaxrespectv", "adofai",
                "adanceoffireandice", "rhythmdoctor", "cryptofthenecrodancer", "metalhellsinger", "trombonechamp",
                "taikonotatsujin", "stepmania", "quaver", "etterna", "spinrhythmxds", "guitarhero", "rocksmith",
            ],
            [],
            Reference
        ),
        (
            [
                "counterstrike2", "counterstrikeglobaloffensive", "valorant", "apexlegends", "fortnite",
                "pubgbattlegrounds", "playerunknownsbattlegrounds", "pubg", "overwatch", "overwatch2", "marvelrivals",
                "thefinals", "deadlock", "escapefromtarkov", "arenabreakoutinfinite", "huntshowdown1896", "huntshowdown",
                "deltaforce", "haloinfinite", "halothemasterchiefcollection", "teamfortress2", "splitgate", "splitgate2",
                "xdefiant", "spectredivide", "strinova", "arcraiders", "rust", "dayz", "hellletloose", "squad",
                "insurgencysandstorm", "readyornot", "grayzonewarfare", "bodycam", "destiny2", "paladins", "fragpunk",
                "warzone", "titanfall2", "quakechampions", "quakelive", "krunker", "ironsight", "combatmaster",
                "sniperelite5", "scum", "arma3", "armareforger", "payday2", "payday3", "left4dead2", "gtfo",
            ],
            [
                "callofduty", "battlefield", "tomclancysrainbowsix", "rainbowsix", "counterstrike",
                "tomclancysghostrecon", "tomclancysthedivision", "borderlands", "farcry",
            ],
            Footsteps
        ),
        (
            [
                "cyberpunk2077", "discoelysium", "metaphorrefantazio", "clairobscurexpedition33", "starfield", "skyrim",
                "hogwartslegacy", "avowed", "outerwilds", "firewatch", "whatremainsofedithfinch", "pentiment",
                "slaytheprincess", "road96", "oxenfree", "heavyrain", "beyondtwosouls", "untildawn", "thequarry",
                "ittakestwo", "splitfiction", "awayout", "tellmewhy", "control", "quantumbreak", "deathstranding",
                "horizonzerodawn", "horizonforbiddenwest", "ghostoftsushima", "bioshock", "prey", "uncharted",
            ],
            [
                "baldursgate", "thewitcher", "reddeadredemption", "thelastofus", "godofwar", "masseffect", "persona",
                "alanwake", "lifeisstrange", "detroitbecomehuman", "finalfantasy", "theelderscrolls", "fallout",
                "dragonage", "kingdomcomedeliverance", "plaguetale", "aplaguetale", "hellblade", "senua", "tombraider",
                "shadowofthetombraider", "riseofthetombraider", "silenthill", "residentevil", "dishonored", "deusex",
                "yakuza", "likeadragon", "judgment", "octopath", "nier", "divinityoriginalsin", "pillarsofeternity",
                "pathfinder", "aceattorney", "danganronpa", "steinsgate", "batmanarkham", "marvelsspiderman",
                "starwarsjedi", "dragonquest", "talesof", "xenoblade", "shinmegamitensei",
            ],
            Vocal
        ),
        (
            [
                "eldenring", "sekiro", "liesofp", "blackmythwukong", "wutheringwaves", "genshinimpact",
                "honkaistarrail", "zenlesszonezero", "helldivers2", "iracing", "beamng", "rocketleague",
                "streetfighter6", "tekken8", "mortalkombat1", "2xko", "dragonballfighterz", "brawlhalla", "hollowknight",
                "hades", "hades2", "deadcells", "celeste", "cuphead", "returnal", "narakabladepoint", "naraka",
                "warframe", "lastepoch", "lostark", "worldofwarcraft", "guildwars2", "blackdesert", "crimsondesert",
                "phantombladezero", "hellisus", "stellarblade", "palworld", "valheim", "enshrouded", "subnautica",
                "nomanssky", "elitedangerous", "starcitizen", "microsoftflightsimulator", "warthunder", "worldoftanks",
                "worldofwarships", "phasmophobia", "lethalcompany", "repo", "contentwarning", "deadbydaylight",
                "alienisolation", "deadspace", "daysgone", "seaofthieves", "chivalry2", "mordhau", "forhonor", "sifu",
                "pacificdrive", "snowrunner", "eurotrucksimulator2", "americantrucksimulator",
            ],
            [
                "darksouls", "armoredcore", "monsterhunter", "doom", "devilmaycry", "bayonetta", "ninjagaiden", "forza",
                "assettocorsa", "f1", "dirtrally", "eawrc", "needforspeed", "granturismo", "streetfighter", "tekken",
                "mortalkombat", "guiltygear", "grandtheftauto", "gta", "spacemarine", "warhammer40000spacemarine",
                "oriandthe", "remnant", "diablo", "pathofexile", "phantomblade", "easportsfc", "fifa", "nba2k",
                "madden", "nhl", "mlbtheshow", "wwe2k", "ufc", "outlast", "amnesia", "dyinglight", "deadisland",
                "eurotrucksimulator", "americantrucksimulator",
            ],
            Immersion
        ),
        (
            [
                "leagueoflegends", "teamfighttactics", "dota2", "smite", "smite2", "hearthstone", "balatro",
                "slaythespire", "minecraft", "terraria", "stardewvalley", "factorio", "satisfactory",
                "dysonsphereprogram", "btd6", "bloonstd6", "amongus", "roblox", "vrchat", "vampiresurvivors",
                "brotato", "runescape", "oldschoolrunescape", "albiononline", "eveonline",
            ],
            [
                "civilization", "sidmeierscivilization", "ageofempires", "starcraft", "totalwar", "stellaris",
                "crusaderkings", "europauniversalis", "heartsofiron", "citiesskylines", "rimworld", "dwarffortress",
                "oxygennotincluded",
            ],
            Reference
        ),
    ];

    private static readonly HashSet<int> RhythmTags = [1752, 1621];
    private static readonly HashSet<int> ShooterTags = [1663, 1708, 176981, 620519, 5547, 1199779];
    private static readonly HashSet<int> CompetitiveTags = [1775, 3878, 5055];
    private static readonly HashSet<int> StoryRpgTags = [1742, 3799, 11014, 6426, 4474, 122, 5094];
    private static readonly HashSet<int> ImmersionTags = [699, 701, 1743, 4736, 6506, 29482, 1646, 1667, 4166, 1695, 19];
    private static readonly HashSet<int> ReferenceTags = [9, 1664, 1666, 1718, 599];

    private static readonly HashSet<string> CompetitiveWikiGenres = new(StringComparer.Ordinal)
        { "tactical shooter", "battle royale", "hero shooter", "arena shooter" };
    private static readonly HashSet<string> ShooterWikiGenres = new(StringComparer.Ordinal)
        { "fps", "shooter", "tps" };
    private static readonly HashSet<string> StoryWikiGenres = new(StringComparer.Ordinal)
        { "rpg", "role-playing", "visual novel", "interactive movie", "adventure" };
    private static readonly HashSet<string> ImmersionWikiGenres = new(StringComparer.Ordinal)
        { "racing", "driving", "sports", "fighting", "hack and slash", "survival horror", "horror", "action" };

    /// <summary>True when Exo already has enough local signal (title match, Steam tags, or PCGamingWiki facts) to classify without a network fetch.</summary>
    internal static bool HasKnownSignal(GameEntry game, IReadOnlyList<int>? steamTags, GameWiki.Facts? wiki)
    {
        var key = StoreService.TitleKey(game.Title);
        if (MatchTitle(key) is not null) return true;
        if (steamTags is { Count: > 0 } || StoreService.OffSteamTagsFor(game.Title) is { Count: > 0 }) return true;
        return wiki is { Genres.Count: > 0 } or { Modes.Count: > 0 };
    }

    internal static EqGameProfile Resolve(GameEntry game, IReadOnlyList<int>? steamTags = null, GameWiki.Facts? wiki = null, bool knownOnly = false)
    {
        var key = StoreService.TitleKey(game.Title);
        if (MatchTitle(key) is { } byTitle) return byTitle;

        var tags = (steamTags is { Count: > 0 } ? steamTags : StoreService.OffSteamTagsFor(game.Title))?.Take(10).ToList();
        if (tags is { Count: > 0 } && MatchSteamTags(tags) is { } byTags) return byTags;

        if (wiki is not null && MatchWiki(wiki) is { } byWiki) return byWiki;
        return knownOnly ? Reference : Reference;
    }

    private static EqGameProfile? MatchTitle(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var (exact, prefixes, profile) in TitleBuckets)
        {
            if (exact.Contains(key, StringComparer.Ordinal)) return profile;
            if (prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))) return profile;
        }
        return null;
    }

    private static EqGameProfile? MatchSteamTags(IReadOnlyList<int> tags)
    {
        for (var i = 0; i < Math.Min(4, tags.Count); i++)
            if (RhythmTags.Contains(tags[i])) return Reference;

        var shooterAt = FindFirst(tags, ShooterTags);
        var storyAt = FindFirst(tags, StoryRpgTags);
        var hasCompetitive = tags.Any(CompetitiveTags.Contains);
        if (shooterAt >= 0 && (hasCompetitive || storyAt < 0 || shooterAt < storyAt)) return Footsteps;
        if (storyAt >= 0) return Vocal;
        if (tags.Any(ImmersionTags.Contains)) return Immersion;
        if (tags.Any(ReferenceTags.Contains)) return Reference;
        return null;
    }

    private static int FindFirst(IReadOnlyList<int> tags, HashSet<int> set)
    {
        for (var i = 0; i < tags.Count; i++)
            if (set.Contains(tags[i])) return i;
        return -1;
    }

    private static EqGameProfile? MatchWiki(GameWiki.Facts wiki)
    {
        var genres = wiki.Genres;
        if (genres.Any(g => g.Contains("rhythm", StringComparison.Ordinal) || g.Contains("music", StringComparison.Ordinal))) return Reference;
        if (genres.Any(CompetitiveWikiGenres.Contains)) return Footsteps;
        var multiplayer = wiki.Modes.Any(m => m.Contains("multiplayer", StringComparison.Ordinal));
        if (multiplayer && genres.Any(ShooterWikiGenres.Contains)) return Footsteps;
        if (genres.Any(StoryWikiGenres.Contains)) return Vocal;
        if (genres.Any(ImmersionWikiGenres.Contains)) return Immersion;
        return null;
    }
}

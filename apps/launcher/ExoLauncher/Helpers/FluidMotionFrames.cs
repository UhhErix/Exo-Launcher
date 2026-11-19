using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

/// <summary>
/// Whether a game gets AMD Fluid Motion Frames, decided by Exo for each game on each PC: no switch, no exception in
/// Properties. The game sheet reads the decision and <see cref="RadeonProfileService"/> sets the driver to it as the
/// game starts. Pure, so every rule is tested (FluidMotionFramesTests: real games on an RX 6600, 7800 XT, 9070 XT, an
/// RX 5700 and a Radeon 780M).
///
/// What it is, from AMD's notes (AFMF 2 in Adrenalin 24.9.1, 2.1 in 25.3.1) and the reviews: the driver draws a frame
/// between each two the game draws, so motion looks up to twice as smooth, but the game reads input no more often and
/// every real frame reaches the screen later (5 to 13 ms more in Tech4Gamers' measurements; AMD's "28% lower latency"
/// is against AFMF 1, not against off). It has no motion vectors and no HUD mask, so crosshairs and HUDs can smear, it
/// pauses itself in fast motion (a flick), and it adds frames only while AMD Software runs.
///
/// Off:
/// - Competitive and esports games, where lag costs more than smoothness gives and fast flicks are where it pauses
///   anyway: <see cref="Competitive"/> by title (Fortnite, VALORANT, Counter-Strike 2, Apex Legends, Overwatch 2,
///   Rainbow Six Siege, Call of Duty, Rocket League, League of Legends, Dota 2, Marvel Rivals, THE FINALS, Deadlock and
///   their like), Steam's own tags (<see cref="CompetitiveTags"/>: PvP, Competitive, eSports, Battle Royale, MOBA, Hero
///   Shooter, Arena Shooter, Fighting, Extraction Shooter) among a game's eight most voted, unless Singleplayer, Story
///   Rich or MMORPG is voted higher (Elden Ring's invasions, an MMO's arenas), and PCGamingWiki's multiplayer-only
///   shooters, battle royales, MOBAs, fighting, sports and racing games. A fighting game's 60 fps lock doesn't make it a
///   fit: its inputs are counted in frames.
/// - Games with frame generation of their own (FSR, XeSS or DLSS frame generation, from PCGamingWiki or the libraries
///   the install ships): the game's own sees motion vectors and leaves the HUD out, so it looks better and lags less,
///   and AMD's and the handheld makers' guides say to use it with AFMF off. DLSS's counts as well: on a Radeon, mods
///   such as OptiScaler run it as FSR frame generation, and nearly every game with it ships FSR's too.
/// - Games this PC can't run near the base frame rate AMD asks for (55 fps at 1080p, 70 at 1440p and up; under it the
///   gaps it fills are too wide and its artifacts show): when Steam's requirements can be read against this PC's
///   graphics card and it meets only their Minimum (made for about 30 fps) or less. Where they can't be read, the
///   genre decides.
/// - Where it can't run: GPUs before the RX 6000 series and integrated Radeons before the 700M (<see cref="Runs"/>),
///   and games on Direct3D 9 or 10 alone (AFMF 2 runs on Direct3D 11 and 12, Vulkan and OpenGL). It also needs
///   exclusive or borderless fullscreen with the game's V-Sync off (on RX 6000 AMD lists exclusive fullscreen); Exo
///   can't see a game's window mode before it starts, so that stays the game's own setting.
/// - A game Exo knows nothing about: no frames added rather than lag added unasked.
///
/// On: every other game Exo knows (single-player, story, open world, RPG, simulation, strategy, MMO, co-op), most of
/// all those held at 60 fps (Elden Ring, Sekiro, Dark Souls III), which it takes to 120. AMD pairs it with Anti-Lag,
/// which Exo's Radeon profile keeps on.
/// </summary>
internal static partial class FluidMotionFrames
{
    /// <summary>What Exo knows about a game; any part may be missing.</summary>
    /// <param name="Title">The library's title.</param>
    internal sealed record Game(string Title)
    {
        /// <summary>Steam's tag ids, most voted first (a big game off Steam: the ones Steam gives games like it).</summary>
        public IReadOnlyList<int> Tags { get; init; } = [];
        /// <summary>PCGamingWiki's page.</summary>
        public GameWiki.Facts? Wiki { get; init; }
        /// <summary>The install ships an AMD-usable frame generation library (amd_fidelityfx_framegeneration_dx12, libxess_fg; excludes NVIDIA-only nvngx_dlssg).</summary>
        public bool FrameGenFiles { get; init; }
        /// <summary>This PC's graphics card against the game's Steam requirements: "recommended", "minimum", "below", or null.</summary>
        public string? Graphics { get; init; }
    }

    /// <param name="Why">For app.log only; the sheet shows On or Off alone.</param>
    internal sealed record Decision(bool On, string Why);

    /// <summary>Big competitive games by title key (<see cref="StoreService.TitleKey"/>), so a game off Steam or one whose tags undersell it (Call of Duty's lead with Singleplayer) is known.</summary>
    internal static readonly HashSet<string> Competitive = new(StringComparer.Ordinal)
    {
        "fortnite", "valorant", "counterstrike2", "counterstrikeglobaloffensive", "apexlegends", "overwatch", "overwatch2",
        "rocketleague", "leagueoflegends", "teamfighttactics", "2xko", "dota2", "marvelrivals", "thefinals", "deadlock",
        "pubgbattlegrounds", "playerunknownsbattlegrounds", "haloinfinite", "teamfortress2", "warthunder", "worldoftanks",
        "worldofwarships", "huntshowdown1896", "escapefromtarkov", "arenabreakoutinfinite", "deltaforce", "splitgate",
        "splitgate2", "xdefiant", "smite", "smite2", "paladins", "brawlhalla", "streetfighter6", "tekken8", "mortalkombat1",
        "hearthstone", "strinova", "spectredivide", "arcraiders", "naraka", "narakabladepoint",
    };

    /// <summary>Series that are played for their multiplayer, by the start of the title key.</summary>
    private static readonly string[] CompetitiveSeries = ["callofduty", "battlefield", "tomclancysrainbowsixsiege", "rainbowsixsiege"];

    /// <summary>Steam tags that make a game competitive: PvP, Competitive, eSports, Battle Royale, MOBA, Hero Shooter, Arena Shooter, Fighting, 2D Fighter, 3D Fighter, Extraction Shooter.</summary>
    internal static readonly HashSet<int> CompetitiveTags = [1775, 3878, 5055, 176981, 1718, 620519, 5547, 1743, 4736, 6506, 1199779];

    /// <summary>Steam tags that, voted above a competitive one, say the game is played for something else: Singleplayer, Story Rich, MMORPG.</summary>
    private static readonly HashSet<int> PlayedAlone = [4182, 1742, 1754];

    /// <summary>How many of a game's most voted tags say what it is (as the Store reads them).</summary>
    private const int TagsRead = 8;

    /// <summary>PCGamingWiki genres that, in a game with multiplayer only, make it competitive.</summary>
    private static readonly HashSet<string> CompetitiveGenres = new(StringComparer.Ordinal)
    {
        "battle royale", "fps", "tps", "shooter", "tactical shooter", "hero shooter", "arena shooter", "moba", "fighting",
        "sports", "racing", "vehicle combat", "brawler",
    };

    internal static Decision Decide(string? gpu, Game game)
    {
        if (!Runs(gpu)) return new(false, "this GPU has no Fluid Motion Frames");
        if (IsCompetitive(game)) return new(false, "a competitive game");
        if (HasOwnFrameGen(game)) return new(false, "it has frame generation of its own");
        if (game.Wiki?.Apis is { Count: > 0 } apis && !apis.Any(api => api is "dx11" or "dx12" or "vulkan" or "opengl"))
            return new(false, "it runs on " + string.Join(" and ", apis));
        if (game.Graphics is "minimum" or "below") return new(false, "this GPU meets only its Minimum or less, under the base frame rate it needs");
        if (!Known(game)) return new(false, "nothing is known about it");
        return new(true, game.Wiki?.Over60 == false ? "held at 60 fps, which it doubles" : "not a competitive game");
    }

    /// <summary>
    /// How this PC's graphics card meets a game's Steam requirements (<see cref="Game.Graphics"/>), on the Store's scale
    /// (<see cref="StoreService.FitSpecs"/>); a game that names only a Minimum asks for no more than that.
    /// </summary>
    internal static string? Graphics(IReadOnlyList<StoreService.Spec> specs, StoreService.ThisPc pc) =>
        StoreService.FitSpecs(specs, pc).Rows.FirstOrDefault(row => row.Label == "Graphics") switch
        {
            { Meets: "minimum", Recommended: null } => "recommended",
            var row => row?.Meets,
        };

    /// <summary>
    /// Whether the game ships its own AMD-usable frame generation (cross-vendor files, or PCGamingWiki "true" unless
    /// its framegen tech list names NVIDIA DLSS alone, which cannot run on Radeon without mods blocked by anti-cheat).
    /// </summary>
    private static bool HasOwnFrameGen(Game game)
    {
        if (game.FrameGenFiles) return true;
        if (game.Wiki?.FrameGen != "true") return false;
        var tech = game.Wiki.FrameGenTech;
        return tech is not { Count: > 0 } || tech.Any(t => !t.Contains("dlss", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Exo knows what kind of game it is: Steam's tags, or PCGamingWiki's modes or genres.</summary>
    private static bool Known(Game game) => game.Tags.Count > 0 || game.Wiki is { } wiki && (wiki.Modes.Count > 0 || wiki.Genres.Count > 0);

    /// <summary>
    /// Whether AMD lists the GPU for AFMF 2: RX 6000, 7000 and 9000 (with their PRO and laptop parts), Radeon 700M and
    /// 800M, Ryzen AI Max's 8000S; not RX 5000 or older, Vega, or the 600M and older integrated Radeons. A name that
    /// says no generation (an APU's plain "Radeon Graphics") is left to the driver, which offers the setting only where
    /// it runs.
    /// </summary>
    internal static bool Runs(string? gpu)
    {
        if (string.IsNullOrWhiteSpace(gpu)) return true;
        var name = gpu.ToUpperInvariant();
        if (name.Contains("AI PRO", StringComparison.Ordinal)) return true;
        if (Discrete().Match(name) is { Success: true } rx) return int.Parse(rx.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 6000;
        if (Integrated().Match(name) is { Success: true } apu) return apu.Groups[1].Value[0] >= '7';
        if (Strix().IsMatch(name)) return true;
        return !Older().IsMatch(name);
    }

    internal static bool IsCompetitive(Game game)
    {
        var key = StoreService.TitleKey(game.Title);
        if (Competitive.Contains(key) || CompetitiveSeries.Any(series => key.StartsWith(series, StringComparison.Ordinal))) return true;
        var top = game.Tags.Take(TagsRead).ToList();
        var first = top.FindIndex(CompetitiveTags.Contains);
        if (first >= 0 && !top.Take(first).Any(PlayedAlone.Contains)) return true;
        return game.Wiki is { } wiki && wiki.Modes.Contains("multiplayer") && !wiki.Modes.Contains("singleplayer")
            && wiki.Genres.Any(CompetitiveGenres.Contains);
    }

    // "RX 6600", "RX 7800 XT", "PRO W7900", "RX 5700": the series is the model's thousands.
    [GeneratedRegex(@"\b(?:RX|PRO\s+W|PRO\s+V)\s*-?\s*(\d{4})")]
    internal static partial Regex Discrete();

    // "Radeon 780M", "Radeon(TM) 890M Graphics", "680M".
    [GeneratedRegex(@"\b(\d)\d0M\b")]
    internal static partial Regex Integrated();

    // Ryzen AI Max: "Radeon 8060S Graphics".
    [GeneratedRegex(@"\b80[4-6]0S\b")]
    internal static partial Regex Strix();

    // RX 580 and other three-digit RX cards, Vega, Radeon VII, R9 and R7, HD.
    [GeneratedRegex(@"\bRX\s*\d{3}\b|\bVEGA\b|\bRADEON\s+VII\b|\bR[579]\s+\d{3}|\bHD\s*\d{4}")]
    private static partial Regex Older();
}

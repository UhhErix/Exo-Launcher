using System.Diagnostics;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// The one answer to "does this game run anti-cheat?", and which ones. Evidence, every source counted (not the first):
/// the game's store (Riot is Vanguard, Battle.net is Warden, Roblox is Hyperion); a game Exo Shaders knows by its id; every
/// anti-cheat folder, file, launcher stub and game program <see cref="Markers"/> names anywhere in the install; a Windows
/// service whose program sits inside the install; Steam's own word for a Steam game (appinfo's Valve Anti-Cheat category,
/// the anti-cheat page or Deck test it lists, and Valve's own VAC titles); and only when nothing else is found, the title
/// list. A Steam game Steam's data says nothing about is "possibly Valve Anti-Cheat" (<see cref="Kind.PossiblyVac"/>), not
/// unknown. Unknown is only an install Exo cannot read: no folder and nothing else known, a folder that cannot be listed, a
/// link whose folder cannot be read, a tree deeper than the walk goes, a walk out of entries or time. <see cref="Allows"/>
/// decides each change by its kind. Every place that decides this calls here.
/// </summary>
internal static partial class AntiCheatCheck
{
    internal enum Kind
    {
        None,
        Found,
        /// <summary>Only Steam's silence: a Steam game whose appinfo can't be read or doesn't list it.</summary>
        PossiblyVac,
        Unknown,
    }

    /// <summary>The install could not be read (a folder, or a link's folder, that can't be listed; a tree too deep).</summary>
    internal const string Unreadable = DlssSwapService.UnreadableInstall;
    /// <summary>The walk ran out of time or entries.</summary>
    internal const string OutOfTime = "out of time";
    /// <summary>No install folder, and nothing else known.</summary>
    internal const string NoInstall = "no install";
    internal const string ValveAntiCheat = "Valve Anti-Cheat";
    /// <summary>Steam can't say: the name the set carries for it.</summary>
    internal const string PossiblyVacName = "possibly Valve Anti-Cheat";
    internal const string SteamListed = "an anti-cheat";

    internal static bool IsUnknownLabel(string? label) => label is Unreadable or OutOfTime or NoInstall;

    /// <param name="Names">Every anti-cheat found, sorted; for unknown, what was found before the walk gave up.</param>
    /// <param name="Evidence">What decided it: store, known game, files, service, steam, title, none; for unknown its reason (<see cref="Unreadable"/>, <see cref="OutOfTime"/>, <see cref="NoInstall"/>).</param>
    internal readonly record struct Answer(Kind Kind, IReadOnlyList<string> Names, string Evidence)
    {
        internal Answer(Kind kind, string? name, string evidence) : this(kind, name is null ? [] : [name], evidence) { }

        /// <summary>Anything but none: treated as protected by the changes that put something into the game.</summary>
        public bool AssumeProtected => Kind != Kind.None;

        /// <summary>The names as one phrase ("BattlEye and possibly Valve Anti-Cheat"), or null.</summary>
        public string? Name => Names.Count == 0 ? null : Join(Names);

        /// <summary>The string scans and consent keep: null for none, the full set's phrase, or the unknown's reason.</summary>
        public string? Label => Kind switch
        {
            Kind.None => null,
            Kind.Unknown => Evidence,
            _ => Name,
        };

        public static readonly Answer Clean = new(Kind.None, [], "none");
    }

    /// <summary>An answer back from its <see cref="Answer.Label"/> (what a scan or consent kept).</summary>
    internal static Answer FromLabel(string? label)
    {
        if (label is null) return Answer.Clean;
        if (IsUnknownLabel(label)) return new(Kind.Unknown, [], label);
        var names = Split(label);
        return new(names.All(name => name == PossiblyVacName) ? Kind.PossiblyVac : Kind.Found, names, "kept");
    }

    /// <summary>What Exo is about to do to the game, each of one <see cref="Category"/>.</summary>
    internal enum Change
    {
        /// <summary>(b) Arguments on the game's command line, as the store itself lets a player set them (never a switch that
        /// turns the anti-cheat off, lets foreign code in or names a path code is loaded from: <see cref="RefusedArgument"/>).</summary>
        LaunchOptions,
        /// <summary>(a) A working directory the player picks: Windows looks for DLLs there, so it can place a file in the game.</summary>
        WorkingDirectory,
        /// <summary>(a) Run as administrator or a compatibility flag.</summary>
        RunAsAdmin,
        /// <summary>(a) Starting the game as a Steam shortcut (Steam's overlay goes into it).</summary>
        StartThroughSteam,
        /// <summary>(a) The shader recorder's file beside the game's program.</summary>
        ShaderRecorder,
        /// <summary>(a) Prepare: Exo starts the game, watches it and closes it on its own.</summary>
        ShaderPrepare,
        /// <summary>(a) Exo Capture's game hook: OBS's graphics hook injected into the game.</summary>
        InjectedHook,
        /// <summary>(a) A frame generation mod or loader in the game's folder.</summary>
        FrameGenMod,
        /// <summary>(b) A value Exo writes into the game's own settings file.</summary>
        GameSettingsFile,
        /// <summary>(b) Deleting what the game itself makes again at a known path (Call of Duty's __cod marker, its shadercache folders).</summary>
        DeleteRegenerated,
        /// <summary>(c) The vendor's own signed upscaler file swapped for a newer one.</summary>
        UpscalerSwap,
        /// <summary>(d) The game's files compressed by Windows (WOF): stored smaller, every byte read back the same.</summary>
        Compress,
    }

    /// <summary>The three kinds of change (the orchestrator's decision, 2026-11-10).</summary>
    internal enum Category
    {
        /// <summary>(a) Injected or placed: a file put in the game's folder, a hook, a mod, frame generation files, the shader
        /// recorder, Exo Capture's game hook, a working directory (DLLs load from it), Run as admin and compatibility flags,
        /// Prepare. Only for a game with none.</summary>
        Placed,
        /// <summary>(b) The game's own interface: its settings files, its launch arguments (arguments only: never a path code is
        /// loaded from, never a switch that turns the anti-cheat off or lets foreign code in), deleting what it makes again at a
        /// known path. Allowed with anti-cheat found and with "possibly Valve Anti-Cheat"; never Riot or Vanguard, never an
        /// install that could not be read; a deletion only while neither the game nor an anti-cheat runs (the caller's check).</summary>
        OwnInterface,
        /// <summary>(c) The vendor's own signed upscaler file (DIRECTION.md, the owner's correction of 2026-11-10): with the
        /// player's consent naming this exact set of anti-cheats; never Riot, Vanguard or an install that could not be read.</summary>
        SignedUpscaler,
        /// <summary>(d) Stored differently: Windows' own file compression (<see cref="GameCompression"/>), no byte changed.
        /// With anti-cheat, only with the player's consent for this game naming this exact set, and then only its data
        /// (no program, library or anti-cheat file); never Riot, Vanguard or an install that could not be read.</summary>
        Stored,
    }

    internal static Category CategoryOf(Change change) => change switch
    {
        Change.LaunchOptions or Change.GameSettingsFile or Change.DeleteRegenerated => Category.OwnInterface,
        Change.UpscalerSwap => Category.SignedUpscaler,
        Change.Compress => Category.Stored,
        _ => Category.Placed,
    };

    private static bool Riots(GameEntry game, Answer answer) => IsRiot(game) || answer.Names.Contains(DlssSwapService.RiotVanguard);

    /// <summary>
    /// The policy table, by category and answer:
    /// none: every change. Riot or Vanguard: no change at all.
    /// (a) placed: none only.
    /// (b) own interface: found and possibly Valve Anti-Cheat too; never unknown.
    /// (c) signed upscaler: found or possibly Valve Anti-Cheat with consent naming this full set; never unknown.
    /// (d) stored: as (c), its own consent (the game's data only; <see cref="GameCompression.Guard"/>).
    /// </summary>
    internal static bool Allows(GameEntry game, Answer answer, Change change, string? consented = null)
    {
        if (answer.Kind == Kind.None) return !IsRiot(game);
        if (Riots(game, answer) || answer.Kind == Kind.Unknown) return false;
        return CategoryOf(change) switch
        {
            Category.OwnInterface => true,
            Category.SignedUpscaler or Category.Stored => DlssSwapService.Accepted(game, answer.Label!, consented),
            _ => false,
        };
    }

    /// <summary>
    /// The one sentence a refused change shows on its line, or null when it is allowed. Each reason its own sentence, none
    /// naming a file.
    /// </summary>
    internal static string? Sentence(GameEntry game, Answer answer, Change change, string? consented = null)
    {
        if (Allows(game, answer, change, consented)) return null;
        if (Riots(game, answer)) return "Exo never changes Riot games or games with Vanguard.";
        if (answer.Kind == Kind.Unknown) return answer.Evidence switch
        {
            OutOfTime => "Exo couldn't finish checking this game for anti-cheat, so it leaves it as it is.",
            NoInstall => "Exo couldn't find this game's folder, so it leaves it as it is.",
            _ => "Exo couldn't check this game for anti-cheat, so it leaves it as it is.",
        };
        var uses = answer.Kind == Kind.PossiblyVac ? "Steam can't say whether this game uses Valve Anti-Cheat" : $"This game uses {answer.Name}";
        return CategoryOf(change) switch
        {
            Category.SignedUpscaler => uses + ", so Exo changes nothing here unless you allow it for this game.",
            Category.Stored => uses + ", so Exo compresses it only if you allow it for this game.",
            _ => uses + ", so Exo puts nothing into it.",
        };
    }

    /// <summary>
    /// The one sentence for launch options Play leaves out (<paramref name="admin"/>: Run as admin), or the one Properties
    /// shows in place of the field (<paramref name="field"/>).
    /// </summary>
    internal static string LaunchSentence(GameEntry game, Answer answer, bool admin, bool field = false, bool folder = false)
    {
        if (Riots(game, answer)) return field ? "Exo passes no launch options to Riot games." : "Exo starts Riot games without your launch options.";
        if (answer is { Kind: Kind.Unknown, Evidence: NoInstall }) return "Exo couldn't find this game's folder.";
        if (answer.Kind == Kind.Unknown) return field
            ? "Exo couldn't check this game for anti-cheat, so it passes no launch options."
            : "Exo couldn't check this game for anti-cheat, so it starts it without your launch options.";
        var uses = answer.Kind == Kind.PossiblyVac ? "Steam can't say whether this game uses Valve Anti-Cheat" : $"This game uses {answer.Name}";
        return admin ? uses + ", so Exo starts it without administrator rights."
            : folder ? uses + ", so Exo starts it from its own folder."
            : uses + ", so Exo starts it without your launch options.";
    }

    /// <summary>
    /// Launch arguments refused in any game that isn't clean: a switch that turns an anti-cheat off or lets foreign code in,
    /// and anything naming a DLL or a plugin path. Each row: the switch without its leading dashes or plus, matched whole.
    /// </summary>
    private static readonly string[] RefusedSwitches =
    [
        "insecure", "allow_third_party_software", "noeac", "no-eac", "no_eac", "eac_disable", "disableeac", "nobattleye", "no-battleye",
        "no_battleye", "nobe", "battleye_disable", "disablebattleye", "noanticheat", "no-anticheat", "no_anticheat", "disableanticheat",
        "anticheat_disable", "nosteamac", "novac",
    ];

    /// <summary>A "-switch=off" form for an anti-cheat: eac=0, battleye=false, anticheat=off and the like.</summary>
    [GeneratedRegex(@"^(eac|easyanticheat|battleye|be|anticheat|ac|vac)[=:](0|off|false|no|disabled?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AntiCheatOff();

    /// <summary>The first launch argument refused in a game that isn't clean, or null.</summary>
    internal static string? RefusedArgument(string? arguments)
    {
        foreach (var raw in (arguments ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('"', '\'');
            if (token.Contains(".dll", StringComparison.OrdinalIgnoreCase) || token.Contains("plugin", StringComparison.OrdinalIgnoreCase)) return raw;
            var name = token.TrimStart('-', '+', '/');
            if (RefusedSwitches.Contains(name, StringComparer.OrdinalIgnoreCase) || AntiCheatOff().IsMatch(name)) return raw;
        }
        return null;
    }

    internal const string RefusedArgumentSentence = "Exo leaves out launch options that turn anti-cheat off or load other code in this game.";

    /// <summary>Frame generation's note when it comes out of a game for its anti-cheat, by reason.</summary>
    internal static string TakenOut(string label) => FromLabel(label) switch
    {
        { Kind: Kind.Unknown, Evidence: OutOfTime } => "Taken out: Exo couldn't finish checking the game for anti-cheat",
        { Kind: Kind.Unknown } => "Taken out: Exo couldn't check the game for anti-cheat",
        { Kind: Kind.PossiblyVac } => "Taken out: Steam can't say whether the game uses Valve Anti-Cheat",
        var answer => $"Taken out: the game uses {answer.Name}",
    };

    /// <summary><see cref="Sentence"/> from a kept label (a scan's anti-cheat).</summary>
    internal static string? SentenceFor(GameEntry game, string? label, Change change) => Sentence(game, FromLabel(label), change);

    /// <summary>What Steam's own data says of a Steam game's anti-cheat.</summary>
    internal enum SteamWord
    {
        /// <summary>Not a Steam game.</summary>
        NotSteam,
        /// <summary>Steam's info for it is there and names no anti-cheat.</summary>
        None,
        /// <summary>Valve Anti-Cheat: appinfo's category_8, or one of Valve's VAC titles.</summary>
        Vac,
        /// <summary>An anti-cheat page or a Deck test about anti-cheat, the anti-cheat not named.</summary>
        Listed,
        /// <summary>Steam can't say: no app id, appinfo unreadable or of a format Exo doesn't read, the app not in it.</summary>
        CannotTell,
    }

    /// <summary>
    /// The evidence table. Riot's store is Vanguard and nothing else is looked at. Otherwise every source adds its
    /// anti-cheat: the store, a known game, the walk, a service, Steam (its unnamed anti-cheat only when nothing names one;
    /// its silence as "possibly Valve Anti-Cheat"). Unknown when the walk could not finish, or there is no install and
    /// nothing at all is known. The title counts only when every other source found nothing. Possibly Valve Anti-Cheat
    /// alone is its own kind.
    /// </summary>
    /// <param name="store">The store's anti-cheat (<see cref="ForStore"/>).</param>
    /// <param name="known">The anti-cheat Exo Shaders lists the game's id for (<see cref="KnownGames"/>).</param>
    /// <param name="disk">The walk's phrase: null for nothing found, the names, with <see cref="Unreadable"/> or <see cref="OutOfTime"/> among them when it could not finish.</param>
    /// <param name="withoutEntry">The player picked the Play-menu entry Steam offers without the anti-cheat
    /// (<see cref="WithoutAntiCheatLaunch"/>): what the files, title, a known id or Steam's silence say counts for nothing,
    /// since that entry never starts it. Never Riot or Vanguard, never an install that could not be read, never Valve
    /// Anti-Cheat (Steam's own, on in every entry), a store's or a service's anti-cheat.</param>
    internal static Answer Decide(string? store, string? known, bool installFound, string? disk, string? service, SteamWord steam, string? title, bool withoutEntry = false)
    {
        if (store == DlssSwapService.RiotVanguard) return new(Kind.Found, store, "store");
        var names = new SortedSet<string>(StringComparer.Ordinal);
        string? evidence = null;
        void Add(string? name, string source)
        {
            if (name is null || !names.Add(name)) return;
            evidence ??= source;
        }
        Add(store, "store");
        Add(known, "known game");
        var walked = Split(disk);
        foreach (var name in walked.Where(name => !IsUnknownLabel(name))) Add(name, "files");
        Add(service, "service");
        if (steam == SteamWord.Vac) Add(ValveAntiCheat, "steam");
        if (steam == SteamWord.Listed && names.Count == 0) Add(SteamListed, "steam");

        string? unknown = walked.Contains(OutOfTime) ? OutOfTime
            : walked.Contains(Unreadable) ? Unreadable
            : !installFound && names.Count == 0 && title is null ? NoInstall
            : null;
        if (unknown is not null) return new(Kind.Unknown, names.ToList(), unknown);
        if (names.Count == 0 && title is not null) Add(title, "title");
        if (steam == SteamWord.CannotTell && !names.Contains(ValveAntiCheat)) Add(PossiblyVacName, "steam");
        if (withoutEntry && names.Count > 0 && store is null && service is null && steam != SteamWord.Vac &&
            !names.Contains(ValveAntiCheat) && !names.Contains(DlssSwapService.RiotVanguard))
            return new(Kind.None, [], WithoutAntiCheatEvidence);
        if (names.Count == 0) return Answer.Clean;
        return new(names.All(name => name == PossiblyVacName) ? Kind.PossiblyVac : Kind.Found, names.ToList(), evidence!);
    }

    // ------------------------------------------------------------------ the sources

    /// <summary>A fresh walk of the install (names only, no file is opened), the services and Steam's word.</summary>
    internal static Answer Of(GameEntry game, CancellationToken ct) => Of(game, AntiCheatServices(), SteamSource, ct);

    internal static Answer Of(GameEntry game, IReadOnlyList<(string Name, string ImagePath)> services, Func<uint, SteamWord> steam, CancellationToken ct)
    {
        if (IsRiot(game)) return Decide(DlssSwapService.RiotVanguard, null, false, null, null, SteamWord.NotSteam, null);
        var root = DlssSwapService.ResolveInstallRoot(game.Path);
        return FromParts(game, root, root is null ? null : DlssSwapService.AntiCheatInFolders(game, ct), services, steam);
    }

    /// <summary>The same answer from a walk already made (a scan's anti-cheat).</summary>
    internal static Answer FromScan(GameEntry game, string? walked) => FromScan(game, walked, AntiCheatServices(), SteamSource);

    internal static Answer FromScan(GameEntry game, string? walked, IReadOnlyList<(string Name, string ImagePath)> services, Func<uint, SteamWord> steam)
    {
        if (IsRiot(game)) return Decide(DlssSwapService.RiotVanguard, null, false, null, null, SteamWord.NotSteam, null);
        return FromParts(game, DlssSwapService.ResolveInstallRoot(game.Path), walked, services, steam);
    }

    private static Answer FromParts(GameEntry game, string? root, string? walked, IReadOnlyList<(string Name, string ImagePath)> services, Func<uint, SteamWord> steam) =>
        Decide(ForStore(game.Store), KnownGames.GetValueOrDefault(game.Id), root is not null, walked, root is null ? null : ServiceIn(root, services),
            SteamSays(game, steam), ByTitle(game), WithoutAntiCheatLaunch(game) is not null);

    /// <summary>What decided a clean answer for a game started from Steam's entry without its anti-cheat.</summary>
    internal const string WithoutAntiCheatEvidence = "launch without anti-cheat";

    /// <summary>The player's pick in Properties: this Steam game starts from Steam's entry without its anti-cheat (settings, wired at start).</summary>
    internal static Func<GameEntry, bool> ChoseWithoutAntiCheat { get; set; } = _ => false;

    /// <summary>Steam's entry without the anti-cheat for an app id, from its appinfo (<see cref="SteamVacFile.Free"/>).</summary>
    internal static Func<uint, SteamAppInfoNames.AntiCheatFreeLaunch?> SteamFreeEntry { get; set; } = SteamVacFile.Default.Free;

    /// <summary>
    /// The entry the game starts from when it starts without its anti-cheat: only a Steam game (never Riot's) the player set
    /// to "Launch without anti-cheat" whose appinfo still offers the entry; else null, and it starts, and is judged, with it.
    /// Every place asks this one: Play (which entry), the policy (<see cref="Decide"/>) and frame generation at launch.
    /// </summary>
    internal static Func<GameEntry, SteamAppInfoNames.AntiCheatFreeLaunch?> WithoutAntiCheatLaunch { get; set; } = game =>
        game.Store == StoreKind.Steam && !IsRiot(game) && SteamAppId(game) is { } id && ChoseWithoutAntiCheat(game) ? SteamFreeEntry(id) : null;

    /// <summary>A Steam game's app id: the launch target, else the library id's ("steam:440"); null when it has none.</summary>
    internal static uint? SteamAppId(GameEntry game)
    {
        var text = game.LaunchTarget is { Length: > 0 } target ? target : game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) ? game.Id[6..] : null;
        return uint.TryParse(text, out var id) && id != 0 ? id : null;
    }

    /// <summary>Where Steam's word comes from: Steam's own appinfo, read once per change of its files (<see cref="SteamVacFile"/>).</summary>
    internal static Func<uint, SteamWord> SteamSource { get; set; } = SteamVacFile.Default.Word;

    /// <summary>Riot's games: by store, installed under a Riot Games folder, or a Riot program as the game's own.</summary>
    internal static bool IsRiot(GameEntry game) =>
        game.Store == StoreKind.Riot ||
        (game.Path ?? string.Empty).Replace('/', '\\').Contains("\\Riot Games\\", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(game.LaunchTarget ?? game.Path ?? "") is { Length: > 0 } program && RiotPrograms.Contains(program, StringComparer.OrdinalIgnoreCase);

    /// <summary>The store's own anti-cheat: Riot's Vanguard, Battle.net's Warden, Roblox's Hyperion.</summary>
    internal static string? ForStore(StoreKind store) => store switch
    {
        StoreKind.Riot => DlssSwapService.RiotVanguard,
        StoreKind.BattleNet => "Warden",
        StoreKind.Roblox => "Hyperion",
        _ => null,
    };

    /// <summary>
    /// Games whose anti-cheat leaves no file in the install, by id (Exo Shaders' GameFiles.AntiCheatIds and the ids of its
    /// game-verdicts.json "anti-cheat" entries; AntiCheatCheckTests compares them).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> KnownGames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["steam:2139460"] = NetEase, ["steam:2767030"] = NetEase, ["epic:575efd0b5dd54429b035ffc8fe2d36d0"] = NetEase, ["steam:2943650"] = NetEase,
        ["steam:2782500"] = NetEase, ["steam:1203220"] = NetEase, ["steam:2073850"] = "Elytra", ["steam:2016590"] = "IronShield",
        ["steam:1551360"] = Forza, ["steam:2483190"] = Forza, ["xbox:Microsoft.ForteBaseGame_8wekyb3d8bbwe"] = Forza,
        ["xbox:Microsoft.624F8B84B80_8wekyb3d8bbwe"] = Forza, ["steam:2000950"] = Ricochet, ["steam:1938090"] = Ricochet,
        ["steam:1962663"] = Ricochet, ["steam:238960"] = Poe, ["steam:2694490"] = Poe,
    };

    /// <summary>
    /// The cheap half (store, known id and title, no disk): only for filters that run over the whole library. What then
    /// changes the game asks <see cref="Of"/> or <see cref="Cached"/>.
    /// </summary>
    internal static string? Quick(GameEntry game) =>
        IsRiot(game) ? DlssSwapService.RiotVanguard : ForStore(game.Store) ?? KnownGames.GetValueOrDefault(game.Id) ?? ByTitle(game);

    /// <summary>
    /// The game, or its anti-cheat, runs now: a process whose program is in the install (the game, its launcher stub), or
    /// one named for an anti-cheat this game has (<paramref name="names"/>: Easy Anti-Cheat's service while it plays). A
    /// deletion of the game's own files waits for both.
    /// </summary>
    internal static bool GameOrAntiCheatRunning(GameEntry game, IReadOnlyList<string> names, Func<IEnumerable<(string Name, string? Path)>>? processes = null)
    {
        var root = DlssSwapService.ResolveInstallRoot(game.Path);
        var inside = root is null ? null : Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        foreach (var (name, path) in (processes ?? RunningProcesses)())
        {
            if (inside is not null && path is not null && path.StartsWith(inside, StringComparison.OrdinalIgnoreCase)) return true;
            if (ForFile(name + ".exe") is { } antiCheat && names.Contains(antiCheat)) return true;
        }
        return false;
    }

    /// <summary>
    /// Why Exo may not delete what the game makes again (Call of Duty's marker and shader caches) now, one sentence; null
    /// when it may: the own-interface rule, and neither the game nor its anti-cheat running.
    /// </summary>
    internal static string? DeletionRefusal(GameEntry game, Answer answer, Func<IEnumerable<(string Name, string? Path)>>? processes = null)
    {
        if (Sentence(game, answer, Change.DeleteRegenerated) is { } refused) return refused;
        return GameOrAntiCheatRunning(game, answer.Names, processes) ? $"Close {game.Title} first." : null;
    }

    /// <summary>An anti-cheat's own program runs now (Easy Anti-Cheat's service, a game's bootstrapper), Vanguard's left out: it runs whatever the game.</summary>
    internal static bool AntiCheatProcessRunning(Func<IEnumerable<(string Name, string? Path)>>? processes = null) =>
        (processes ?? RunningProcesses)().Any(process => ForFile(process.Name + ".exe") is { } antiCheat && antiCheat != DlssSwapService.RiotVanguard);

    private static IEnumerable<(string Name, string? Path)> RunningProcesses()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path = null;
                try { path = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                yield return (process.ProcessName, path);
            }
        }
    }

    // ------------------------------------------------------------------ Steam

    /// <summary>Valve's multiplayer titles with VAC (Exo Shaders' Vac.Valve): Counter-Strike, Condition Zero, Counter-Strike: Source,
    /// Day of Defeat: Source, Half-Life 2: Deathmatch, Team Fortress 2, Left 4 Dead 1 and 2, Dota 2, Counter-Strike 2,
    /// Garry's Mod, Deadlock; and Day of Defeat (30).</summary>
    internal static readonly IReadOnlySet<uint> ValveVac = new HashSet<uint> { 10, 30, 80, 240, 300, 320, 440, 500, 550, 570, 730, 4000, 1422450 };

    internal static SteamWord SteamSays(GameEntry game, Func<uint, SteamWord> steam)
    {
        if (game.Store != StoreKind.Steam) return SteamWord.NotSteam;
        if (SteamAppId(game) is not { } id) return SteamWord.CannotTell;
        if (ValveVac.Contains(id)) return SteamWord.Vac;
        return steam(id);
    }

    /// <summary>An app's appinfo record: Valve Anti-Cheat by its category_8, an anti-cheat it lists, none, or cannot tell.</summary>
    internal static SteamWord FromAppInfo(SteamKeyValues? app)
    {
        if (app is null || app.Section("common").Keys.Count == 0) return SteamWord.CannotTell;
        if (app.Section("common/category").Value("category_8") is not null) return SteamWord.Vac;
        return SteamOfflineMode.SteamMarksAntiCheat(app) ? SteamWord.Listed : SteamWord.None;
    }

    /// <summary>
    /// Steam's appinfo (its shards, or the one appinfo.vdf of older Steam) read whole once per change of any file's size,
    /// time or presence (as Exo Shaders' Vac.cs keeps it), every app's word kept from that one parse. A read that fails, a
    /// file gone between listing and reading, or a file Steam is still writing (it ends before its last record, or a
    /// record's hash doesn't match) is tried again after <see cref="RetryAfter"/>, the last good words kept meanwhile; a
    /// format Exo doesn't read, or files over <see cref="MaxBytes"/> together, say "cannot tell" for every app (possibly
    /// Valve Anti-Cheat), never unknown. A whole-read failure is logged once.
    /// </summary>
    internal sealed class SteamVacFile(Func<IReadOnlyList<string>> files, Func<DateTime>? now = null, long maxBytes = SteamVacFile.MaxBytes)
    {
        internal const long MaxBytes = 256L * 1024 * 1024;
        internal static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
        internal static readonly SteamVacFile Default = new(() => SteamInstallation.ResolveRoot() is { } root ? SteamAppInfoNames.Files(root) : []);

        private readonly Lock _gate = new();
        private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
        private Dictionary<uint, SteamWord> _words = [];
        private Dictionary<uint, SteamAppInfoNames.AntiCheatFreeLaunch> _free = [];
        private string? _stamp;
        private DateTime _retryAt;
        private string? _warned;

        /// <summary>How many times the files were parsed (the tests count them).</summary>
        internal int Parses { get; private set; }

        internal SteamWord Word(uint appId)
        {
            lock (_gate)
            {
                Load();
                return _words.TryGetValue(appId, out var word) ? word : SteamWord.CannotTell;
            }
        }

        /// <summary>The Play-menu entry Steam offers that starts the game without its anti-cheat, or null (<see cref="SteamAppInfoNames.WithoutAntiCheat"/>).</summary>
        internal SteamAppInfoNames.AntiCheatFreeLaunch? Free(uint appId)
        {
            lock (_gate)
            {
                Load();
                return _free.GetValueOrDefault(appId);
            }
        }

        private void Load()
        {
            var list = files();
            if (list.Count == 0)
            {
                Fail("Steam's app list isn't there", keepWords: false);
                return;
            }
            var stamp = SteamAppInfoNames.Stamp(list);
            if (stamp is null)
            {
                Fail("Steam's app list can't be read: Steam is rewriting it", keepWords: true);
                return;
            }
            if (_stamp == stamp || _now() < _retryAt) return;
            var all = new List<byte[]>(list.Count);
            long total = 0;
            foreach (var file in list)
            {
                byte[] bytes;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    total += stream.Length;
                    if (total > maxBytes)
                    {
                        Settle(stamp, [], "Steam's app list is larger than Exo reads");
                        return;
                    }
                    bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Fail("Steam's app list can't be read: " + ex.GetType().Name, keepWords: true);
                    return;
                }
                if (!SteamAppInfoNames.IsKnownFormat(bytes))
                {
                    Settle(stamp, [], "Steam's app list is of a format Exo doesn't read");
                    return;
                }
                all.Add(bytes);
            }
            var words = new Dictionary<uint, SteamWord>();
            var free = new Dictionary<uint, SteamAppInfoNames.AntiCheatFreeLaunch>();
            Parses++;
            foreach (var bytes in all)
            {
                if (SteamAppInfoNames.EachApp(bytes, (id, app) =>
                {
                    words[id] = FromAppInfo(app);
                    if (SteamAppInfoNames.WithoutAntiCheat(SteamAppInfoNames.LaunchEntries(app)) is { } entry) free[id] = entry;
                })) continue;
                // Steam is writing it: tried again soon, never kept.
                _retryAt = _now() + RetryAfter;
                AppLog.Debug("Anti-cheat: Steam's app list ended early; it is read again shortly.");
                return;
            }
            Settle(stamp, words, null, free);
        }

        private void Settle(string stamp, Dictionary<uint, SteamWord> words, string? problem, Dictionary<uint, SteamAppInfoNames.AntiCheatFreeLaunch>? free = null)
        {
            _stamp = stamp;
            _words = words;
            _free = free ?? [];
            _retryAt = default;
            if (problem is not null) Warn(problem);
        }

        private void Fail(string problem, bool keepWords)
        {
            if (!keepWords)
            {
                _words = [];
                _free = [];
                _stamp = null;
            }
            _retryAt = _now() + RetryAfter;
            Warn(problem);
        }

        private void Warn(string problem)
        {
            if (_warned == problem) return;
            _warned = problem;
            AppLog.Warn($"Anti-cheat: {problem}; Steam games count as possibly Valve Anti-Cheat until it reads.");
        }
    }

    // ------------------------------------------------------------------ markers

    /// <summary>How a marker's name counts.</summary>
    internal enum Rule
    {
        /// <summary>An anti-cheat's own folder or file, anywhere in the install.</summary>
        Module,
        /// <summary>The game's own program, for a game whose anti-cheat leaves no file of its own: anywhere in the install,
        /// and never a reason to treat the program as not the game's.</summary>
        Program,
        /// <summary>A name too common to count deep in the tree: only in the install's root or the game program's folder.</summary>
        RootOrExe,
        /// <summary>A launcher stub (Game_BE.exe): only with its game (Game.exe) or the anti-cheat's folder beside it.</summary>
        Stub,
    }

    /// <summary>
    /// The marker table: a name matched whole, "*x" a name ending in x, "x*" one starting with x, any case, files and
    /// folders alike. Ported from Exo Shaders (shaders\src\SCSKiller.Core\Games\GameFiles.cs, Markers, ExeMarkers and
    /// RootMarkers; the owner's own detector) and Exo's own earlier table. The two must stay in step:
    /// AntiCheatCheckTests.EveryExoShadersMarkerIsHere fails when Exo Shaders has a marker this table lacks.
    /// </summary>
    internal static readonly (string Pattern, string AntiCheat, Rule Rule)[] Markers =
    [
        // Easy Anti-Cheat
        ("EasyAntiCheat", Eac, Rule.Module), ("EasyAntiCheat_EOS", Eac, Rule.Module), ("start_protected_game.exe", Eac, Rule.Module),
        ("EasyAntiCheat_EOS_Setup.exe", Eac, Rule.Module), ("EasyAntiCheat_Setup.exe", Eac, Rule.Module),
        ("easyanticheat*", Eac, Rule.Module), ("eac_launcher*", Eac, Rule.Module), ("*_EAC.exe", Eac, Rule.Stub),
        // BattlEye
        ("BattlEye", Be, Rule.Module), ("BEService.exe", Be, Rule.Module), ("BEService_x64.exe", Be, Rule.Module), ("BELauncher.exe", Be, Rule.Module),
        ("BEClient_x64.dll", Be, Rule.Module), ("BEClient.dll", Be, Rule.Module), ("beservice*", Be, Rule.Module), ("beclient*", Be, Rule.Module),
        ("bedaisy*", Be, Rule.Module), ("*_BE.exe", Be, Rule.Stub),
        // EA Javelin
        ("EAAntiCheat.Installer.exe", Javelin, Rule.Module), ("EAAntiCheat.GameServiceLauncher.exe", Javelin, Rule.Module),
        ("EAAntiCheat.GameServiceLauncher.dll", Javelin, Rule.Module), ("EAAntiCheat", Javelin, Rule.Module), ("eaanticheat*", Javelin, Rule.Module),
        // nProtect GameGuard, XIGNCODE3, NCGuard
        ("GameGuard", "nProtect GameGuard", Rule.Module), ("nProtect", "nProtect GameGuard", Rule.Module), ("gameguard.des", "nProtect GameGuard", Rule.Module),
        ("XIGNCODE", "XIGNCODE3", Rule.Module), ("*.xem", "XIGNCODE3", Rule.Module),
        ("NCGuardSDK", "NCGuard", Rule.Module), ("NCGuard", "NCGuard", Rule.Module),
        // Ricochet (Call of Duty): its driver isn't always in the folder
        ("randgrid.sys", Ricochet, Rule.Module), ("randgrid*", Ricochet, Rule.Module), ("ModernWarfare.exe", Ricochet, Rule.Program), ("cod.exe", Ricochet, Rule.RootOrExe),
        // Anti-Cheat Expert (Tencent)
        ("AntiCheatExpert", Ace, Rule.Module), ("AceAntibotClient", Ace, Rule.Module), ("TP3Helper.exe", Ace, Rule.Module),
        ("SGuard", Ace, Rule.Module), ("SGuard64.exe", Ace, Rule.Module), ("SGuardSvc64.exe", Ace, Rule.Module), ("sguard*", Ace, Rule.Module),
        ("ACE-Base64.dll", Ace, Rule.Module), ("ACE-Base.dat", Ace, Rule.Module), ("ACE-Setup64.exe", Ace, Rule.Module), ("ACE-Service64.exe", Ace, Rule.Module),
        ("ACE-ATS64.dll", Ace, Rule.Module), ("ACE-CSI64.dll", Ace, Rule.Module), ("ACE-DFS64.dll", Ace, Rule.Module), ("ACE-BASE.sys", Ace, Rule.Module),
        ("ace-base*", Ace, Rule.Module),
        // TenProtect (Tencent)
        ("TenProtect", "TenProtect", Rule.Module), ("TesSafe.sys", "TenProtect", Rule.Module),
        // HoYoverse: every game of theirs ships a kernel anti-cheat, whatever its driver is named in a version
        ("HoYoKProtect.sys", HoYo, Rule.Module), ("mhypbase.dll", HoYo, Rule.Module), ("mhyprot.sys", HoYo, Rule.Module), ("mhyprot2.sys", HoYo, Rule.Module),
        ("mhyprot3.sys", HoYo, Rule.Module), ("mhyprot*", HoYo, Rule.Module), ("mhypbase*", HoYo, Rule.Module),
        ("GenshinImpact.exe", HoYo, Rule.Program), ("YuanShen.exe", HoYo, Rule.Program), ("StarRail.exe", HoYo, Rule.Program),
        ("ZenlessZoneZero.exe", HoYo, Rule.Program), ("BH3.exe", HoYo, Rule.Program),
        ("HYP.exe", HoYo, Rule.Module), ("HYPHelper.exe", HoYo, Rule.Module), ("HYPWorker.exe", HoYo, Rule.Module),
        // Riot Vanguard, and Riot's programs: a Riot game added by hand is still Riot's
        ("vgk.sys", DlssSwapService.RiotVanguard, Rule.Module), ("vgc.exe", DlssSwapService.RiotVanguard, Rule.Module),
        ("VALORANT.exe", DlssSwapService.RiotVanguard, Rule.Program), ("VALORANT-Win64-Shipping.exe", DlssSwapService.RiotVanguard, Rule.Program),
        ("League of Legends.exe", DlssSwapService.RiotVanguard, Rule.Program), ("LeagueClient.exe", DlssSwapService.RiotVanguard, Rule.Program),
        ("LeagueClientUx.exe", DlssSwapService.RiotVanguard, Rule.Program), ("LeagueClientUxRender.exe", DlssSwapService.RiotVanguard, Rule.Program),
        ("LoR.exe", DlssSwapService.RiotVanguard, Rule.Program), ("Lion-Win64-Shipping.exe", DlssSwapService.RiotVanguard, Rule.Program),
        ("RiotClientServices.exe", DlssSwapService.RiotVanguard, Rule.Module), ("RiotClientUx.exe", DlssSwapService.RiotVanguard, Rule.Module),
        ("RiotClientUxRender.exe", DlssSwapService.RiotVanguard, Rule.Module),
        // NetEase: its anti-cheat often installs outside the game's folder
        ("NeacClient.exe", NetEase, Rule.Module), ("NeacSafe64.sys", NetEase, Rule.Module), ("NeacSafe64_ex.sys", NetEase, Rule.Module),
        ("neacclient*", NetEase, Rule.Module), ("neacsafe*", NetEase, Rule.Module),
        ("NEP2.dll", "NetEase Protect", Rule.Module), ("NEPCleaner.exe", "NetEase Protect", Rule.Module),
        ("ONCE_HUMAN.exe", NetEase, Rule.Program), ("Marvel-Win64-Shipping.exe", NetEase, Rule.Program), ("Marvel-Win64-Test.exe", NetEase, Rule.Program),
        ("Marvel.exe", NetEase, Rule.RootOrExe), ("MarvelRivals_Launcher.exe", NetEase, Rule.Program),
        ("FragPunk.exe", NetEase, Rule.Program), ("nshm.exe", NetEase, Rule.Program), ("NarakaBladepoint.exe", NetEase, Rule.Program),
        // Nexon Game Security, AhnLab HackShield
        ("BlackCall.aes", "Nexon Game Security", Rule.Module), ("BlackCall64.aes", "Nexon Game Security", Rule.Module), ("BlackCat64.sys", "Nexon Game Security", Rule.Module),
        ("HShield", "AhnLab HackShield", Rule.Module),
        // PunkBuster, EQU8, FACEIT
        ("PunkBuster", "PunkBuster", Rule.Module), ("PnkBstrA.exe", "PunkBuster", Rule.Module), ("pbsvc.exe", "PunkBuster", Rule.Module),
        ("pbsv.dll", "PunkBuster", Rule.Module), ("pnkbstr*", "PunkBuster", Rule.Module), ("pbcl*", "PunkBuster", Rule.Module),
        ("equ8_conf.json", "EQU8", Rule.Module), ("EQU8", "EQU8", Rule.Module), ("equ8*", "EQU8", Rule.Module),
        ("FACEIT", "FACEIT", Rule.Module), ("faceit*", "FACEIT", Rule.Module),
        // Battle.net's install (a Blizzard game another store lists, or one added by hand): Warden
        (".build.info", "Warden", Rule.Module), (".product.db", "Warden", Rule.Module), (@"data\.build.config", "Warden", Rule.RootOrExe),
        // Anti-cheat built into the game itself, no file of its own
        ("Warframe.x64.exe", "Warframe's anti-cheat", Rule.Program),
        ("Discovery.exe", "Elytra", Rule.RootOrExe), ("Discovery-e.exe", "Elytra", Rule.Program), ("Discovery-d.exe", "Elytra", Rule.Program),
        ("Elytra", "Elytra", Rule.Module), ("Elytra.exe", "Elytra", Rule.Module), ("Elytra-Setup.exe", "Elytra", Rule.Module),
        ("DungeonCrawler.exe", "IronShield", Rule.Program),
        ("forzahorizon5.exe", Forza, Rule.Program), ("forzahorizon6.exe", Forza, Rule.Program),
        ("PathOfExile.exe", Poe, Rule.Program), ("PathOfExile_x64.exe", Poe, Rule.Program), ("PathOfExileSteam.exe", Poe, Rule.Program),
        ("PathOfExile_x64Steam.exe", Poe, Rule.Program), ("PathOfExileEGS.exe", Poe, Rule.Program), ("PathOfExile_x64EGS.exe", Poe, Rule.Program),
        ("PathOfExile_KG.exe", Poe, Rule.Program), ("PathOfExile_x64_KG.exe", Poe, Rule.Program),
        ("RobloxPlayerBeta.exe", "Hyperion", Rule.Program), ("RobloxPlayerBeta.dll", "Hyperion", Rule.Module),
        ("RobloxPlayerLauncher.exe", "Hyperion", Rule.Program), ("RobloxPlayerInstaller.exe", "Hyperion", Rule.Module),
        ("arbiter.dll", "Arbiter", Rule.Module),
        // Denuvo Anti-Cheat
        ("DenuvoAC", Denuvo, Rule.Module), ("Denuvo Anti-Cheat", Denuvo, Rule.Module), ("denuvo-anti-cheat.sys", Denuvo, Rule.Module),
        ("denuvo-anti-cheat-runtime.dll", Denuvo, Rule.Module), ("denuvo-anti-cheat-update-service.exe", Denuvo, Rule.Module),
        ("Denuvo Anti-Cheat Installer.exe", Denuvo, Rule.Module), ("denuvo-anti-cheat*", Denuvo, Rule.Module),
        // Exo Shaders' ExoBuild.AntiCheatModules, by the start of a file's name
        ("battleye*", Be, Rule.Module), ("vgk*", DlssSwapService.RiotVanguard, Rule.Module), ("vgc*", DlssSwapService.RiotVanguard, Rule.Module),
        ("gameguard*", "nProtect GameGuard", Rule.Module), ("ricochet*", Ricochet, Rule.Module), ("xigncode*", "XIGNCODE3", Rule.Module),
    ];

    private const string Eac = "Easy Anti-Cheat", Be = "BattlEye", Javelin = "EA Javelin", Ricochet = "Ricochet", Ace = "Anti-Cheat Expert",
        HoYo = "HoYoverse anti-cheat", NetEase = "NetEase anti-cheat", Poe = "Path of Exile's anti-cheat", Denuvo = "Denuvo Anti-Cheat",
        Forza = "Forza's anti-cheat";

    private static readonly string[] RiotPrograms = Markers
        .Where(m => m.AntiCheat == DlssSwapService.RiotVanguard && m.Rule == Rule.Program).Select(m => m.Pattern).ToArray();

    private static bool Matches(string pattern, string name) =>
        pattern.StartsWith('*') ? name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
        : pattern.EndsWith('*') ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
        : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first marker row for a name, by name alone (no path rule applied).</summary>
    internal static (string Pattern, string AntiCheat, Rule Rule)? MarkerFor(string name)
    {
        foreach (var marker in Markers)
            if (!marker.Pattern.Contains('\\') && Matches(marker.Pattern, name)) return marker;
        return null;
    }

    /// <summary>A folder named for an anti-cheat (its contents are its own; the walk does not go in).</summary>
    internal static string? ForFolder(string folderName) => MarkerFor(folderName) is { Rule: Rule.Module } marker ? marker.AntiCheat : null;

    /// <summary>A file of an anti-cheat's own (not a game's program, which a program list must keep).</summary>
    internal static string? ForFile(string fileName) => MarkerFor(fileName) is { Rule: Rule.Module or Rule.Stub } marker ? marker.AntiCheat : null;

    /// <summary>
    /// The anti-cheat an entry in <paramref name="dir"/> names, with each rule's place: a program-only name only in the
    /// root or the program's folder, a stub only beside its game or the anti-cheat's folder.
    /// </summary>
    internal static string? ForEntry(string dir, string name, bool atRootOrExe)
    {
        if (MarkerFor(name) is not { } marker) return null;
        return marker.Rule switch
        {
            Rule.RootOrExe => atRootOrExe ? marker.AntiCheat : null,
            Rule.Stub => StubHasItsGame(dir, name, marker) ? marker.AntiCheat : null,
            _ => marker.AntiCheat,
        };
    }

    private static bool StubHasItsGame(string dir, string name, (string Pattern, string AntiCheat, Rule Rule) marker)
    {
        var game = name[..^(marker.Pattern.Length - 1)] + ".exe";
        var folder = marker.AntiCheat == Be ? "BattlEye" : "EasyAntiCheat";
        try { return File.Exists(Path.Combine(dir, game)) || Directory.Exists(Path.Combine(dir, folder)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Markers that are a path under the root or the program's folder (Blizzard's TACT container on Overwatch's Steam install).</summary>
    internal static IEnumerable<string> PathMarkersIn(string dir)
    {
        foreach (var (pattern, antiCheat, _) in Markers)
            if (pattern.Contains('\\') && File.Exists(Path.Combine(dir, pattern))) yield return antiCheat;
    }

    // ------------------------------------------------------------------ the walk's bookkeeping

    internal const int MaxDepth = 16;
    internal const int MaxEntries = 2_000_000;
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Folders that never hold an anti-cheat and can be huge (packed assets, videos, caches, saves, logs, source trees):
    /// the walk doesn't go in. Exo Shaders' own walk has no skip list; these are Exo's.
    /// </summary>
    internal static readonly IReadOnlySet<string> NoMarkerFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "paks", "movies", "videos", "cinematics", "shadercache", "saved", "logs", "crashdumps", "node_modules", ".git", "intermediate",
    };

    /// <summary>
    /// What one walk of an install found: every anti-cheat, and whether it could read the tree whole. The walk itself is
    /// <see cref="DlssSwapService.Walk"/> (it also lists the upscaler files). A link is walked where it points, once,
    /// within the same budget (one into the install is read where it is). A folder that cannot be listed, a link whose
    /// folder is gone or can't be read, or a tree deeper than <see cref="MaxDepth"/> make it unreadable; more than
    /// <paramref name="maxEntries"/> entries or a walk longer than the budget make it out of time.
    /// </summary>
    internal sealed class Walker(string root, string? exeDir, int maxEntries = MaxEntries, TimeSpan? budget = null)
    {
        private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        private readonly string? _exeDir = exeDir is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDir));
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TimeSpan _budget = budget ?? Budget;
        private readonly HashSet<string> _linked = new(StringComparer.OrdinalIgnoreCase);
        private int _entries;
        internal SortedSet<string> Found { get; } = new(StringComparer.Ordinal);
        internal string? Why { get; private set; }
        internal bool TimedOut { get; private set; }

        internal void Unknown(string why) => Why ??= why;

        /// <summary>A name, or a phrase of several.</summary>
        internal void Add(string? antiCheat)
        {
            foreach (var name in Split(antiCheat))
                if (IsUnknownLabel(name)) Unknown("a folder above could not be read");
                else Found.Add(name);
        }

        /// <summary>Counts an entry; true once the walk is over its entries or its time (out of time from then on).</summary>
        internal bool Spent()
        {
            if (TimedOut) return true;
            if (++_entries <= maxEntries && _clock.Elapsed <= _budget) return false;
            TimedOut = true;
            Unknown(_entries > maxEntries ? "more than " + maxEntries + " entries" : "out of time");
            return true;
        }

        internal bool AtRootOrExe(string dir)
        {
            var full = Path.TrimEndingDirectorySeparator(dir);
            return full.Equals(_root, StringComparison.OrdinalIgnoreCase) || _exeDir is not null && full.Equals(_exeDir, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Where to walk for a link: its target, once; null when there is nothing more to read (a target inside the
        /// install, read where it is, or one already walked). A reparse point that is no link (a OneDrive placeholder) is
        /// read as the folder it is. A target that is gone or can't be resolved makes the walk unreadable.
        /// </summary>
        internal string? Link(DirectoryInfo link)
        {
            try
            {
                if (link.LinkTarget is not { } target) return link.FullName;
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target, link.Parent?.FullName ?? _root));
                if (full.Equals(_root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                if (!Directory.Exists(full))
                {
                    Unknown("a link whose folder is gone: " + link.FullName);
                    return null;
                }
                return _linked.Add(full) ? full : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Unknown("a link that can't be read: " + link.FullName);
                return null;
            }
        }

        /// <summary>The walk's phrase: the names found, with <see cref="OutOfTime"/> or <see cref="Unreadable"/> when it could not finish; null for none.</summary>
        internal string? Result => Why is null
            ? Found.Count == 0 ? null : Join(Found)
            : Join(Found.Append(TimedOut ? OutOfTime : Unreadable));
    }

    /// <summary>Names as one phrase: "A", "A and B", "A, B and C" (sorted, so one set always reads the same).</summary>
    internal static string Join(IEnumerable<string> names)
    {
        var list = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list[..^1]) + " and " + list[^1];
    }

    /// <summary>A phrase back into its names.</summary>
    internal static IReadOnlyList<string> Split(string? phrase) =>
        string.IsNullOrEmpty(phrase) ? [] : phrase.Replace(" and ", ", ", StringComparison.Ordinal).Split(", ", StringSplitOptions.RemoveEmptyEntries);

    // ------------------------------------------------------------------ services and titles

    /// <summary>Windows service names (prefixes) of anti-cheat drivers and services.</summary>
    private static readonly (string Prefix, string AntiCheat)[] ServicePrefixes =
    [
        ("EasyAntiCheat", Eac), ("BEService", Be), ("BEDaisy", Be), ("vgk", DlssSwapService.RiotVanguard), ("vgc", DlssSwapService.RiotVanguard),
        ("FACEIT", "FACEIT"), ("EAAntiCheat", Javelin), ("PnkBstr", "PunkBuster"), ("xhunter", "XIGNCODE3"), ("npggsvc", "nProtect GameGuard"),
        ("ACE-", Ace), ("AntiCheatExpert", Ace), ("mhyprot", HoYo), ("HoYoKProtect", HoYo), ("atvi-randgrid", Ricochet), ("randgrid", Ricochet),
        ("EQU8", "EQU8"), ("NEAC", NetEase), ("TesSafe", "TenProtect"), ("denuvo-anti-cheat", Denuvo), ("Elytra", "Elytra"),
    ];

    /// <summary>
    /// Titles whose anti-cheat lives outside the install folder or leaves no file: the fallback when nothing else is found.
    /// Whole words only, so "Rust" does not catch "Rusty Lake".
    /// </summary>
    private static readonly (Regex Title, string AntiCheat)[] Titles =
    [
        (Word("valorant|league of legends|teamfight tactics|legends of runeterra|2xko"), DlssSwapService.RiotVanguard),
        (Word("call of duty|warzone"), Ricochet),
        (Word("battlefield"), Javelin),
        (Word("fortnite|apex legends|dead by daylight|elden ring|hunt: showdown|rust|multiversus|predecessor|lost ark|new world|fall guys"), Eac),
        (Word("pubg|playerunknown's battlegrounds|rainbow six|escape from tarkov|destiny 2|dayz|arma 3|arma reforger|the division|for honor|splitgate"), Be),
        (Word("counter-strike|dota 2|deadlock|team fortress 2|left 4 dead|garry's mod|day of defeat|half-life 2: deathmatch"), ValveAntiCheat),
        // Blizzard's online-only games; Diablo and StarCraft are left to the store (GOG sells the single-player Diablo).
        (Word("overwatch|world of warcraft|hearthstone|heroes of the storm"), "Warden"),
        (Word("marvel rivals|naraka|once human|fragpunk"), NetEase),
        (Word("the finals"), "Elytra"),
        (Word("delta force|arena breakout"), Ace),
        (Word("genshin impact|honkai: star rail|honkai impact|zenless zone zero"), HoYo),
        (Word("wuthering waves|throne and liberty|black desert"), "an anti-cheat"),
        (Word("path of exile"), Poe),
        (Word("warframe"), "Warframe's anti-cheat"),
        (Word("forza horizon"), Forza),
        (Word("dark and darker"), "IronShield"),
        (Word("halo infinite"), "Arbiter"),
        (Word("roblox"), "Hyperion"),
    ];

    private static Regex Word(string alternatives) => new(
        @"(?<![\p{L}\p{N}])(?:" + alternatives + @")(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string? ForService(string serviceName)
    {
        foreach (var (prefix, antiCheat) in ServicePrefixes)
            if (serviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return antiCheat;
        return null;
    }

    internal static string? ByTitle(GameEntry game)
    {
        var title = game.Title ?? string.Empty;
        foreach (var (pattern, antiCheat) in Titles)
            if (pattern.IsMatch(title)) return antiCheat;
        return null;
    }

    /// <summary>An anti-cheat service whose program is inside the install: that game's own.</summary>
    internal static string? ServiceIn(string root, IReadOnlyList<(string Name, string ImagePath)> services)
    {
        var inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        foreach (var (name, image) in services)
        {
            if (ProgramOf(image) is not { } program || !program.StartsWith(inside, StringComparison.OrdinalIgnoreCase)) continue;
            if ((ForService(name) ?? ForFile(Path.GetFileName(program))) is { } antiCheat) return antiCheat;
        }
        return null;
    }

    /// <summary>A service's ImagePath as a full path: quotes and arguments off, <c>\??\</c> and <c>\SystemRoot\</c> resolved.</summary>
    internal static string? ProgramOf(string imagePath)
    {
        var text = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            text = end > 0 ? text[1..end] : text[1..];
        }
        else if (text.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase) is var exe and > 0) text = text[..(exe + 4)];
        else if (text.IndexOf(".sys ", StringComparison.OrdinalIgnoreCase) is var sys and > 0) text = text[..(sys + 4)];
        if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text[4..];
        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), text[@"\SystemRoot\".Length..]);
        try { return Path.IsPathFullyQualified(text) ? Path.GetFullPath(text) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static readonly Lock ServicesGate = new();
    private static (DateTime At, IReadOnlyList<(string, string)> List) _services = (DateTime.MinValue, []);

    /// <summary>The registered services that are anti-cheat by name or program, read at most once a minute.</summary>
    internal static IReadOnlyList<(string Name, string ImagePath)> AntiCheatServices()
    {
        lock (ServicesGate)
        {
            if (DateTime.UtcNow - _services.At < TimeSpan.FromMinutes(1)) return _services.List;
            var list = new List<(string, string)>();
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                foreach (var name in root?.GetSubKeyNames() ?? [])
                {
                    try
                    {
                        using var key = root!.OpenSubKey(name);
                        if (key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string image) continue;
                        if (ForService(name) is not null || (ProgramOf(image) is { } program && ForFile(Path.GetFileName(program)) is not null))
                            list.Add((name, image));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            _services = (DateTime.UtcNow, list);
            return list;
        }
    }

    // ------------------------------------------------------------------ the kept answer

    /// <summary>
    /// The install walk kept per game, so nothing that asks often or must not wait walks the install (Play, Properties, a
    /// game starting, game settings, Prepare's list, Exo Capture). Walked again at once when the game's files change (its
    /// install folder's or program's time or size, its library size, an update); after ten minutes the kept walk is still
    /// used while a walk behind it refreshes it. Only the walk is kept: every ask makes the answer again from it with
    /// Steam's word as it is now (<paramref name="compose"/>), so a game bought minutes ago, not yet in Steam's list, stops
    /// reading as possibly Valve Anti-Cheat at the first ask after Steam writes it, never ten minutes later.
    /// </summary>
    internal sealed class AnswerCache(Func<GameEntry, string?> walk, Func<GameEntry, string?, Answer> compose, Func<DateTime>? now = null, bool refreshBehind = true)
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
        private readonly Lock _gate = new();
        private readonly Dictionary<string, (string Stamp, DateTime At, string? Walked)> _kept = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _refreshing = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

        /// <summary>The kept answer for the game as it is now (stale or not), else null; nothing is walked.</summary>
        internal Answer? Peek(GameEntry game)
        {
            var stamp = Stamp(game);
            string? walked;
            lock (_gate)
                if (!_kept.TryGetValue(game.Id, out var kept) || kept.Stamp != stamp) return null;
                else walked = kept.Walked;
            return compose(game, walked);
        }

        internal Answer For(GameEntry game)
        {
            var stamp = Stamp(game);
            (bool Kept, string? Walked) held = default;
            lock (_gate)
            {
                if (_kept.TryGetValue(game.Id, out var kept) && kept.Stamp == stamp && (_now() - kept.At < Fresh || refreshBehind))
                {
                    if (_now() - kept.At >= Fresh && _refreshing.Add(game.Id)) _ = Task.Run(() => Refresh(game, stamp));
                    held = (true, kept.Walked);
                }
            }
            // Steam's word and the services are asked now, outside the lock, every time.
            return compose(game, held.Kept ? held.Walked : Refresh(game, stamp));
        }

        private string? Refresh(GameEntry game, string stamp)
        {
            try
            {
                var walked = walk(game);
                lock (_gate) _kept[game.Id] = (stamp, _now(), walked);
                return walked;
            }
            finally { lock (_gate) _refreshing.Remove(game.Id); }
        }

        /// <summary>What changes when the game is updated or moved.</summary>
        internal static string Stamp(GameEntry game)
        {
            static string Of(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return "-";
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists) return info.Length + "@" + info.LastWriteTimeUtc.Ticks;
                    var dir = new DirectoryInfo(path);
                    return dir.Exists ? "d@" + dir.LastWriteTimeUtc.Ticks : "missing";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return "unreadable"; }
            }
            var program = game.LaunchTarget is { } target && Path.IsPathFullyQualified(target) ? target : null;
            return string.Join('|', game.Store, game.Title, game.Path, game.SizeBytes, game.UpdateAvailable, Of(game.Path), Of(program));
        }
    }

    private static readonly AnswerCache Kept = new(
        game => IsRiot(game) || DlssSwapService.ResolveInstallRoot(game.Path) is null ? null : DlssSwapService.AntiCheatInFolders(game, CancellationToken.None),
        (game, walked) => FromScan(game, walked));

    /// <summary>The kept answer (<see cref="AnswerCache"/>): walked now only when the game changed or was never walked.</summary>
    internal static Answer Cached(GameEntry game) => Kept.For(game);

    /// <summary>
    /// For a place that must not wait at all (a game starting): the kept answer, else the store, id and title's
    /// (<see cref="Quick"/>) while a walk runs behind for the next ask.
    /// </summary>
    internal static Answer KeptOrQuick(GameEntry game)
    {
        if (Kept.Peek(game) is { } kept) return kept;
        _ = Task.Run(() => Kept.For(game));
        return Quick(game) is { } quick ? new(Kind.Found, quick, "title") : Answer.Clean;
    }

    /// <summary>Walks the installed games behind everything else, so Play and Properties find their answer kept.</summary>
    internal static void Warm(IEnumerable<GameEntry> games)
    {
        var installed = games.Where(game => game.Installed).ToList();
        _ = Task.Run(() =>
        {
            foreach (var game in installed)
            {
                try { Kept.For(game); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        });
    }
}

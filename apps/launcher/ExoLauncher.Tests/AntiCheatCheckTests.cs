using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Services.AntiCheatCheck;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the one anti-cheat answer could fail, each of which lets Exo change, inject into or start a protected game its
/// own way:
/// - a game's anti-cheat on disk is missed because its title isn't on a list, or a marker's name is missing or misspelled;
/// - a store whose games all run anti-cheat (Battle.net, Roblox) or Steam's own VAC flag is not counted;
/// - only the first anti-cheat is returned, so consent for one carries to a game that gained another;
/// - a folder that cannot be listed, a link out of the install, a tree deeper than the walk, or a walk out of time or
///   entries reads as "none found" instead of unknown, or Steam not knowing a Steam game reads as clean;
/// - a common name (cod.exe, a _BE.exe stub) counts where it means nothing;
/// - a game with none is reported as protected;
/// - unknown is let through for a change that places a file or changes how the game starts, the settings-file row
///   disagrees with DIRECTION.md, or the upscaler swap row disagrees with the owner's correction;
/// - the kept answer outlives an update of the game;
/// - Exo Shaders' detector knows a marker this one doesn't.
/// </summary>
public sealed class AntiCheatCheckTests : IDisposable
{
    private static readonly IReadOnlyList<(string, string)> NoServices = [];
    private static SteamWord NoSteam(uint _) => SteamWord.CannotTell;
    private static SteamWord SteamNone(uint _) => SteamWord.None;
    private readonly string _root = Directory.CreateTempSubdirectory("exo-anticheat-check-").FullName;
    private readonly List<string> _locked = [];
    private readonly List<string> _links = [];

    public void Dispose()
    {
        foreach (var path in _locked) Unlock(path);
        // A junction goes first, by itself: a recursive delete stops at it.
        foreach (var link in _links) DeletionGate.DeleteDirectory(link, recursive: false);
        DeletionGate.DeleteDirectory(_root, recursive: true);
    }

    private string Install => Path.Combine(_root, "game");

    private GameEntry Game(string title = "Some Game", StoreKind store = StoreKind.Local, string? path = null, string? target = null) =>
        new() { Id = "local:1", Title = title, Store = store, Installed = true, Path = path ?? Install, LaunchTarget = target };

    private void Ship(params string[] relative)
    {
        Directory.CreateDirectory(Install);
        foreach (var file in relative)
        {
            var path = Path.Combine(Install, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    private void Folder(params string[] relative)
    {
        foreach (var folder in relative) Directory.CreateDirectory(Path.Combine(Install, folder));
    }

    private Answer Check(GameEntry? game = null) => Of(game ?? Game(), NoServices, NoSteam, Ct);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ the evidence table, every row

    public static TheoryData<string?, string?, bool, string?, string?, string, string?, string, string[], string> EvidenceRows() => new()
    {
        // store, known game, install found, disk, service, steam, title => kind, names, evidence
        { DlssSwapService.RiotVanguard, "Ricochet", false, "BattlEye", "FACEIT", "Vac", "Ricochet", "Found", [DlssSwapService.RiotVanguard], "store" },
        { "Warden", null, true, null, null, "NotSteam", "Warden", "Found", ["Warden"], "store" },
        { "Warden", null, false, null, null, "NotSteam", null, "Found", ["Warden"], "store" },
        { null, "Ricochet", true, null, null, "NotSteam", null, "Found", ["Ricochet"], "known game" },
        { null, null, false, null, null, "NotSteam", null, "Unknown", [], NoInstall },
        { null, null, false, null, null, "NotSteam", "Ricochet", "Found", ["Ricochet"], "title" },
        { null, null, true, "BattlEye and " + Unreadable, null, "NotSteam", "Ricochet", "Unknown", ["BattlEye"], Unreadable },
        { null, null, true, Unreadable, null, "NotSteam", null, "Unknown", [], Unreadable },
        { null, null, true, "BattlEye and " + OutOfTime, null, "NotSteam", null, "Unknown", ["BattlEye"], OutOfTime },
        { null, null, true, "BattlEye and Easy Anti-Cheat", "FACEIT", "Vac", "Ricochet", "Found", ["BattlEye", "Easy Anti-Cheat", "FACEIT", ValveAntiCheat], "files" },
        { null, null, true, null, "FACEIT", "NotSteam", "Ricochet", "Found", ["FACEIT"], "service" },
        { null, null, true, null, null, "Vac", null, "Found", [ValveAntiCheat], "steam" },
        { null, null, true, null, null, "Listed", null, "Found", [SteamListed], "steam" },
        { null, null, true, "BattlEye", null, "Listed", null, "Found", ["BattlEye"], "files" },
        // Steam can't say: possibly Valve Anti-Cheat, its own kind alone, never unknown.
        { null, null, true, null, null, "CannotTell", null, "PossiblyVac", [PossiblyVacName], "steam" },
        { null, null, true, "BattlEye", null, "CannotTell", null, "Found", ["BattlEye", PossiblyVacName], "files" },
        { null, null, true, null, null, "None", "Ricochet", "Found", ["Ricochet"], "title" },
        { null, null, true, "BattlEye", null, "None", "Ricochet", "Found", ["BattlEye"], "files" },
        { null, null, true, null, null, "None", null, "None", [], "none" },
    };

    [Theory]
    [MemberData(nameof(EvidenceRows))]
    public void EveryEvidenceRow(string? store, string? known, bool installFound, string? disk, string? service, string steam, string? title, string kind, string[] names, string evidence)
    {
        var answer = Decide(store, known, installFound, disk, service, Enum.Parse<SteamWord>(steam), title);
        Assert.Equal(Enum.Parse<Kind>(kind), answer.Kind);
        Assert.Equal(names, answer.Names);
        Assert.Equal(evidence, answer.Evidence);
    }

    // ------------------------------------------------------------------ the policy table, every row

    public static TheoryData<string, string, bool> PolicyRows()
    {
        var rows = new TheoryData<string, string, bool>();
        foreach (var change in Enum.GetValues<Change>())
        {
            var category = CategoryOf(change);
            rows.Add(nameof(Kind.None), change.ToString(), true);
            // (a) placed: never with anti-cheat; (b) own interface: allowed; (c) the signed upscaler: with consent for the set.
            rows.Add(nameof(Kind.Found), change.ToString(), category != Category.Placed);
            // Steam can't say blocks only (a).
            rows.Add(nameof(Kind.PossiblyVac), change.ToString(), category != Category.Placed);
            // An install that couldn't be read: nothing.
            rows.Add(nameof(Kind.Unknown), change.ToString(), false);
        }
        return rows;
    }

    [Theory]
    [MemberData(nameof(PolicyRows))]
    public void EveryPolicyRow(string kindName, string changeName, bool allowed)
    {
        var (kind, change) = (Enum.Parse<Kind>(kindName), Enum.Parse<Change>(changeName));
        var answer = kind switch
        {
            Kind.None => Answer.Clean,
            Kind.Found => new Answer(Kind.Found, "BattlEye", "files"),
            Kind.PossiblyVac => new Answer(Kind.PossiblyVac, PossiblyVacName, "steam"),
            _ => new Answer(Kind.Unknown, (string?)null, Unreadable),
        };
        Assert.Equal(allowed, Allows(Game(), answer, change, consented: answer.Label));
        Assert.Equal(allowed, Sentence(Game(), answer, change, consented: answer.Label) is null);
    }

    [Fact]
    public void EveryChangeHasItsCategory()
    {
        Assert.Equal(Category.OwnInterface, CategoryOf(Change.LaunchOptions));
        Assert.Equal(Category.OwnInterface, CategoryOf(Change.GameSettingsFile));
        Assert.Equal(Category.OwnInterface, CategoryOf(Change.DeleteRegenerated));
        Assert.Equal(Category.SignedUpscaler, CategoryOf(Change.UpscalerSwap));
        foreach (var placed in new[] { Change.RunAsAdmin, Change.StartThroughSteam, Change.ShaderRecorder, Change.ShaderPrepare, Change.InjectedHook, Change.FrameGenMod })
            Assert.Equal(Category.Placed, CategoryOf(placed));
    }

    [Fact]
    public void EachReasonHasItsOwnSentenceAndNoneNamesAFile()
    {
        var game = Game();
        string? Say(Answer answer, Change change) => Sentence(game, answer, change, consented: null);
        Assert.Equal("Exo couldn't check this game for anti-cheat, so it leaves it as it is.", Say(new(Kind.Unknown, (string?)null, Unreadable), Change.GameSettingsFile));
        Assert.Equal("Exo couldn't finish checking this game for anti-cheat, so it leaves it as it is.", Say(new(Kind.Unknown, (string?)null, OutOfTime), Change.FrameGenMod));
        Assert.Equal("Exo couldn't find this game's folder, so it leaves it as it is.", Say(new(Kind.Unknown, (string?)null, NoInstall), Change.LaunchOptions));
        Assert.Equal("Steam can't say whether this game uses Valve Anti-Cheat, so Exo puts nothing into it.", Say(new(Kind.PossiblyVac, PossiblyVacName, "steam"), Change.FrameGenMod));
        Assert.Equal("This game uses BattlEye, so Exo puts nothing into it.", Say(new(Kind.Found, "BattlEye", "files"), Change.InjectedHook));
        Assert.Equal("Steam can't say whether this game uses Valve Anti-Cheat, so Exo changes nothing here unless you allow it for this game.",
            Say(new(Kind.PossiblyVac, PossiblyVacName, "steam"), Change.UpscalerSwap));
        Assert.Equal("Exo never changes Riot games or games with Vanguard.", Sentence(Game("VALORANT", StoreKind.Riot), new(Kind.Found, DlssSwapService.RiotVanguard, "store"), Change.GameSettingsFile));
        // The upscaler panel: the folder was fine and Steam couldn't say; it says that, not that the folder couldn't be checked.
        Assert.Equal("May use Valve Anti-Cheat. Upscaler updates are off for this game.", DlssSwapService.AntiCheatMessage(game, PossiblyVacName, null));
        Assert.Equal("Taken out: Steam can't say whether the game uses Valve Anti-Cheat", TakenOut(PossiblyVacName));
        Assert.Equal("Taken out: Exo couldn't finish checking the game for anti-cheat", TakenOut(OutOfTime));
        // Launch options: a Riot game and an unread install, in Properties' field; Run as admin with anti-cheat at Play.
        Assert.Equal("Exo passes no launch options to Riot games.", LaunchSentence(Game("VALORANT", StoreKind.Riot), new(Kind.Found, DlssSwapService.RiotVanguard, "store"), admin: false, field: true));
        Assert.Equal("Exo couldn't check this game for anti-cheat, so it passes no launch options.", LaunchSentence(game, new(Kind.Unknown, (string?)null, Unreadable), admin: false, field: true));
        Assert.Equal("This game uses Easy Anti-Cheat, so Exo starts it without administrator rights.", LaunchSentence(game, new(Kind.Found, "Easy Anti-Cheat", "files"), admin: true));
    }

    [Fact]
    public void RiotAndVanguardGetNoSettingsFileAndNoSwap()
    {
        var vanguard = new Answer(Kind.Found, DlssSwapService.RiotVanguard, "store");
        Assert.False(Allows(Game("VALORANT", StoreKind.Riot), vanguard, Change.GameSettingsFile));
        Assert.False(Allows(Game(), vanguard, Change.GameSettingsFile));
        Assert.False(Allows(Game("VALORANT", StoreKind.Riot), vanguard, Change.UpscalerSwap, consented: DlssSwapService.RiotVanguard));
    }

    [Fact]
    public void ConsentNamesTheFullSetAndLapsesWhenTheGameGainsAnother()
    {
        Ship("Game.exe", "EasyAntiCheat/Settings.json");
        var one = Check();
        Assert.Equal("Easy Anti-Cheat", one.Label);
        Assert.True(Allows(Game(), one, Change.UpscalerSwap, consented: one.Label));
        Ship("BEClient_x64.dll");
        var two = Check();
        Assert.Equal(["BattlEye", "Easy Anti-Cheat"], two.Names);
        Assert.Equal("BattlEye and Easy Anti-Cheat", two.Label);
        Assert.False(Allows(Game(), two, Change.UpscalerSwap, consented: "Easy Anti-Cheat"));
        Assert.True(Allows(Game(), two, Change.UpscalerSwap, consented: two.Label));
    }

    [Fact]
    public void EveryNameReadsBackFromItsPhrase()
    {
        var names = Markers.Select(m => m.AntiCheat).Append(ValveAntiCheat).Append(SteamListed).Distinct().ToList();
        foreach (var name in names) Assert.Equal([name], Split(Join([name])));
        Assert.Equal(names.Order(StringComparer.Ordinal), Split(Join(names)));
    }

    // ------------------------------------------------------------------ fixture folders: each folder, each file at the root

    [Theory]
    [InlineData("EasyAntiCheat", "Easy Anti-Cheat")]
    [InlineData("EasyAntiCheat_EOS", "Easy Anti-Cheat")]
    [InlineData("BattlEye", "BattlEye")]
    [InlineData("EAAntiCheat", "EA Javelin")]
    [InlineData("GameGuard", "nProtect GameGuard")]
    [InlineData("nProtect", "nProtect GameGuard")]
    [InlineData("XIGNCODE", "XIGNCODE3")]
    [InlineData("NCGuard", "NCGuard")]
    [InlineData("NCGuardSDK", "NCGuard")]
    [InlineData("AntiCheatExpert", "Anti-Cheat Expert")]
    [InlineData("TenProtect", "TenProtect")]
    [InlineData("HShield", "AhnLab HackShield")]
    [InlineData("PunkBuster", "PunkBuster")]
    [InlineData("FACEIT", "FACEIT")]
    [InlineData("DenuvoAC", "Denuvo Anti-Cheat")]
    [InlineData("Elytra", "Elytra")]
    [InlineData("Binaries/Win64/EasyAntiCheat", "Easy Anti-Cheat")]
    public void EachAntiCheatFolderIsFound(string folder, string antiCheat)
    {
        Ship("Game.exe");
        Folder(folder);
        var answer = Check();
        Assert.Equal((Kind.Found, antiCheat, "files"), (answer.Kind, answer.Name, answer.Evidence));
    }

    [Theory]
    [InlineData("start_protected_game.exe", "Easy Anti-Cheat")]
    [InlineData("EasyAntiCheat_EOS_Setup.exe", "Easy Anti-Cheat")]
    [InlineData("BEClient_x64.dll", "BattlEye")]
    [InlineData("BEService.exe", "BattlEye")]
    [InlineData("BELauncher.exe", "BattlEye")]
    [InlineData("EAAntiCheat.GameServiceLauncher.exe", "EA Javelin")]
    [InlineData("EAAntiCheat.Installer.exe", "EA Javelin")]
    [InlineData("gameguard.des", "nProtect GameGuard")]
    [InlineData("x3.xem", "XIGNCODE3")]
    [InlineData("splash.xem", "XIGNCODE3")]
    [InlineData("randgrid.sys", "Ricochet")]
    [InlineData("ModernWarfare.exe", "Ricochet")]
    [InlineData("SGuard64.exe", "Anti-Cheat Expert")]
    [InlineData("ACE-Base64.dll", "Anti-Cheat Expert")]
    [InlineData("TP3Helper.exe", "Anti-Cheat Expert")]
    [InlineData("TesSafe.sys", "TenProtect")]
    [InlineData("HoYoKProtect.sys", "HoYoverse anti-cheat")]
    [InlineData("mhyprot3.sys", "HoYoverse anti-cheat")]
    [InlineData("GenshinImpact.exe", "HoYoverse anti-cheat")]
    [InlineData("vgk.sys", DlssSwapService.RiotVanguard)]
    [InlineData("VALORANT-Win64-Shipping.exe", DlssSwapService.RiotVanguard)]
    [InlineData("League of Legends.exe", DlssSwapService.RiotVanguard)]
    [InlineData("NeacSafe64.sys", "NetEase anti-cheat")]
    [InlineData("NEP2.dll", "NetEase Protect")]
    [InlineData("ONCE_HUMAN.exe", "NetEase anti-cheat")]
    [InlineData("FragPunk.exe", "NetEase anti-cheat")]
    [InlineData("BlackCall64.aes", "Nexon Game Security")]
    [InlineData("BlackCat64.sys", "Nexon Game Security")]
    [InlineData("PnkBstrA.exe", "PunkBuster")]
    [InlineData("pbsv.dll", "PunkBuster")]
    [InlineData("pbcl.dll", "PunkBuster")]
    [InlineData("equ8_conf.json", "EQU8")]
    [InlineData(".build.info", "Warden")]
    [InlineData(".product.db", "Warden")]
    [InlineData("Warframe.x64.exe", "Warframe's anti-cheat")]
    [InlineData("Elytra.exe", "Elytra")]
    [InlineData("DungeonCrawler.exe", "IronShield")]
    [InlineData("forzahorizon5.exe", "Forza's anti-cheat")]
    [InlineData("PathOfExile_x64.exe", "Path of Exile's anti-cheat")]
    [InlineData("RobloxPlayerBeta.exe", "Hyperion")]
    [InlineData("arbiter.dll", "Arbiter")]
    [InlineData("denuvo-anti-cheat.sys", "Denuvo Anti-Cheat")]
    [InlineData("denuvo-anti-cheat-runtime.dll", "Denuvo Anti-Cheat")]
    [InlineData("Denuvo Anti-Cheat Installer.exe", "Denuvo Anti-Cheat")]
    public void EachAntiCheatFileAtTheRootIsFound(string file, string antiCheat)
    {
        Ship(file);
        var answer = Check();
        Assert.Equal((Kind.Found, antiCheat, "files"), (answer.Kind, answer.Name, answer.Evidence));
    }

    [Fact]
    public void ALauncherStubCountsOnlyBesideItsGameOrItsAntiCheat()
    {
        Ship("Shooter_BE.exe");
        Assert.Equal(Kind.None, Check().Kind);
        Ship("Shooter.exe");
        Assert.Equal("BattlEye", Check().Name);
        DeletionGate.DeleteFile(Path.Combine(Install, "Shooter.exe"));
        Folder("BattlEye");
        Assert.Equal("BattlEye", Check().Name);
    }

    [Fact]
    public void ACommonProgramNameCountsOnlyAtTheRootOrBesideTheGame()
    {
        Ship("Game.exe", "Tools/Editor/cod.exe");
        Assert.Equal(Kind.None, Check().Kind);
        var exe = Path.Combine(Install, "Tools", "Editor", "cod.exe");
        Assert.Equal("Ricochet", Check(Game(target: exe)).Name);
        Ship("cod.exe");
        Assert.Equal("Ricochet", Check().Name);
    }

    [Fact]
    public void BlizzardsContainerCountsAtTheRoot()
    {
        Ship("Overwatch.exe", "data/.build.config");
        Assert.Equal("Warden", Check().Name);
    }

    [Fact]
    public void AGameWithNoneIsClean()
    {
        Ship("Game.exe", "Game/Content/Paks/pak0.pak", "Engine/Binaries/ThirdParty/nvngx_dlss.dll", "tribe.exe", "maybe_be.txt");
        var answer = Check();
        Assert.Equal(Kind.None, answer.Kind);
        Assert.True(Allows(Game(), answer, Change.LaunchOptions));
        Assert.True(Allows(Game(), answer, Change.FrameGenMod));
    }

    [Fact]
    public void AMarkerInAFolderAScanSkipsStillCountsButHugeFoldersAreNotWalked()
    {
        Ship("Game.exe", "Content/Paks/EasyAntiCheat/Settings.json");
        Assert.Equal(Kind.None, Check().Kind);
        Ship("_CommonRedist/x/EasyAntiCheat/Settings.json");
        Assert.Equal("Easy Anti-Cheat", Check().Name);
    }

    [Fact]
    public void TheTitleListIsOnlyTheFallback()
    {
        Ship("Game.exe", "BEClient_x64.dll");
        Assert.Equal(("BattlEye", "files"), (Check(Game("Call of Duty")).Name, Check(Game("Call of Duty")).Evidence));
        DeletionGate.DeleteFile(Path.Combine(Install, "BEClient_x64.dll"));
        Assert.Equal(("Ricochet", "title"), (Check(Game("Call of Duty")).Name, Check(Game("Call of Duty")).Evidence));
        foreach (var title in new[] { "Path of Exile 2", "Warframe", "Forza Horizon 5", "Dark and Darker", "Once Human", "FragPunk" })
            Assert.Equal(Kind.Found, Check(Game(title)).Kind);
        Assert.Equal(Kind.None, Check(Game("Rusty Lake")).Kind);
    }

    // ------------------------------------------------------------------ stores and Steam

    [Theory]
    [InlineData("Diablo IV")]
    [InlineData("World of Warcraft")]
    [InlineData("Hearthstone")]
    [InlineData("StarCraft II")]
    public void EveryBattleNetGameIsWarden(string title)
    {
        Ship("Game.exe");
        var answer = Check(Game(title, StoreKind.BattleNet));
        Assert.Equal((Kind.Found, "Warden", "store"), (answer.Kind, answer.Name, answer.Evidence));
    }

    [Fact]
    public void GogsSinglePlayerDiabloIsNotWarden()
    {
        Ship("Diablo.exe");
        Assert.Equal(Kind.None, Check(Game("Diablo", StoreKind.Gog)).Kind);
        Assert.Equal(Kind.None, Check(Game("Diablo + Hellfire", StoreKind.Gog)).Kind);
        Assert.Equal("Warden", Check(Game("Diablo IV", StoreKind.BattleNet)).Name);
    }

    [Fact]
    public void AGameExoShadersKnowsByIdIsFound()
    {
        Ship("cod.exe");
        var hq = new GameEntry { Id = "steam:1938090", Title = "Call of Duty HQ", Store = StoreKind.Steam, Installed = true, Path = Install, LaunchTarget = "1938090" };
        Assert.Contains("Ricochet", Of(hq, NoServices, SteamNone, Ct).Names);
    }

    [Fact]
    public void RobloxIsHyperionByStoreAndByItsPlayer()
    {
        Ship("RobloxPlayerLauncher.exe");
        Assert.Equal("Hyperion", Check(Game("Roblox", StoreKind.Roblox)).Name);
        Assert.Equal("Hyperion", Check(Game("Some Experience")).Name);
    }

    [Fact]
    public void ARiotGameAddedByHandIsRiots()
    {
        Ship("VALORANT-Win64-Shipping.exe");
        var hand = Game("My shooter", target: Path.Combine(Install, "VALORANT-Win64-Shipping.exe"));
        Assert.True(IsRiot(hand));
        Assert.Equal(DlssSwapService.RiotVanguard, Check(hand).Name);
        Assert.True(DlssSwapService.NeverAllowed(hand, "BattlEye"));
    }

    private GameEntry SteamGame(string appId, string title = "Some Game") =>
        new() { Id = "steam:" + appId, Title = title, Store = StoreKind.Steam, Installed = true, Path = Install, LaunchTarget = appId };

    private static SteamKeyValues App(Action<SteamKeyValues> fill)
    {
        var app = new SteamKeyValues();
        app.Add("common").Set("name", "x");
        fill(app);
        return app;
    }

    [Theory]
    [InlineData("440")]      // Team Fortress 2
    [InlineData("500")]      // Left 4 Dead
    [InlineData("550")]      // Left 4 Dead 2
    [InlineData("4000")]     // Garry's Mod
    [InlineData("30")]       // Day of Defeat
    [InlineData("320")]      // Half-Life 2: Deathmatch
    [InlineData("730")]      // Counter-Strike 2
    public void ValvesOwnVacTitlesAreVacWithoutAskingSteam(string appId)
    {
        Ship("game.exe");
        var answer = Of(SteamGame(appId), NoServices, _ => throw new InvalidOperationException("not asked"), Ct);
        Assert.Equal((Kind.Found, ValveAntiCheat, "steam"), (answer.Kind, answer.Name, answer.Evidence));
    }

    [Fact]
    public void SteamsOwnWordDecidesForEveryOtherSteamGame()
    {
        Ship("game.exe");
        var game = SteamGame("1234567");
        var vac = App(app => app.Section("common").Add("category").Set("category_8", "1"));
        Assert.Equal(SteamWord.Vac, FromAppInfo(vac));
        Assert.Equal(ValveAntiCheat, Of(game, NoServices, _ => FromAppInfo(vac), Ct).Name);
        var listed = App(app => app.Add("extended").Set("anti_cheat_support_url", "https://example.invalid/ac"));
        Assert.Equal(SteamListed, Of(game, NoServices, _ => FromAppInfo(listed), Ct).Name);
        var clean = App(app => app.Section("common").Add("category").Set("category_2", "1"));
        Assert.Equal(Kind.None, Of(game, NoServices, _ => FromAppInfo(clean), Ct).Kind);
        Assert.Equal(SteamWord.CannotTell, FromAppInfo(null));
        Assert.Equal((Kind.PossiblyVac, PossiblyVacName), (Of(game, NoServices, NoSteam, Ct).Kind, Of(game, NoServices, NoSteam, Ct).Name));
        var noId = new GameEntry { Id = "steam:x", Title = "x", Store = StoreKind.Steam, Installed = true, Path = Install };
        Assert.Equal(Kind.PossiblyVac, Of(noId, NoServices, SteamNone, Ct).Kind);
    }

    // ------------------------------------------------------------------ a tree that can't be read whole

    [Fact]
    public void AMissingInstallIsUnknownAndUnknownIsProtected()
    {
        var game = Game("Some Game", path: Path.Combine(_root, "not-here"));
        var answer = Check(game);
        Assert.Equal(Kind.Unknown, answer.Kind);
        Assert.Equal(NoInstall, answer.Label);
        foreach (var change in Enum.GetValues<Change>()) Assert.False(Allows(game, answer, change, consented: "anything"));
    }

    [Fact]
    public void ASubfolderThatCannotBeListedIsUnknown()
    {
        Ship("Game.exe", "Locked/inside.txt");
        Lock(Path.Combine(Install, "Locked"));
        var answer = Check();
        Assert.Equal((Kind.Unknown, "unreadable"), (answer.Kind, answer.Evidence));
    }

    [Fact]
    public void ALinkIsWalkedWhereItPointsAndOnlyAGoneOneIsUnknown()
    {
        Ship("Game.exe", "Data/pak0.pak");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        Junction(Path.Combine(Install, "Inner"), Path.Combine(Install, "Data"));
        Assert.Equal(Kind.None, Check().Kind);
        // A folder the player moved to another drive: read where it is, clean.
        Junction(Path.Combine(Install, "Moved"), outside);
        Assert.Equal(Kind.None, Check().Kind);
        // Its contents count as the game's.
        File.WriteAllText(Path.Combine(outside, "BEClient_x64.dll"), "x");
        Assert.Equal(("BattlEye", "files"), (Check().Name, Check().Evidence));
        // A link whose folder is gone can't be read.
        var gone = Directory.CreateDirectory(Path.Combine(_root, "gone")).FullName;
        Junction(Path.Combine(Install, "Gone"), gone);
        DeletionGate.DeleteDirectory(gone, recursive: true);
        Assert.Equal((Kind.Unknown, Unreadable), (Check().Kind, Check().Evidence));
    }

    [Fact]
    public void ATreeDeeperThanTheWalkIsUnknown()
    {
        Ship("Game.exe", string.Join('/', Enumerable.Range(0, MaxDepth).Select(i => "d" + i)) + "/x.txt");
        Assert.Equal(Kind.None, Check().Kind);
        Ship(string.Join('/', Enumerable.Range(0, MaxDepth + 2).Select(i => "e" + i)) + "/x.txt");
        Assert.Equal(Kind.Unknown, Check().Kind);
    }

    [Fact]
    public void AWalkOutOfEntriesOrTimeIsUnknown()
    {
        Ship("a.txt", "b.txt", "c.txt", "d.txt", "e.txt", "f.txt");
        var few = new Walker(Install, null, maxEntries: 3);
        DlssSwapService.Walk(Install, [], few, 0, Ct);
        Assert.Contains(OutOfTime, Split(few.Result));
        var late = new Walker(Install, null, budget: TimeSpan.Zero);
        DlssSwapService.Walk(Install, [], late, 0, Ct);
        Assert.Contains(OutOfTime, Split(late.Result));
        Assert.Equal(OutOfTime, Decide(null, null, true, late.Result, null, SteamWord.NotSteam, null).Evidence);
        var whole = new Walker(Install, null);
        DlssSwapService.Walk(Install, [], whole, 0, Ct);
        Assert.Null(whole.Result);
    }

    [Fact]
    public void AnAntiCheatServiceInsideTheInstallCounts()
    {
        Ship("Game.exe");
        var inside = Path.Combine(Install, "Driver", "acdrv.sys");
        var outside = Path.Combine(_root, "elsewhere", "BEService.exe");
        Assert.Equal(Kind.None, Of(Game(), [("BEService", $"\"{outside}\" -service")], NoSteam, Ct).Kind);
        var answer = Of(Game(), [("atvi-randgrid_sr", @"\??\" + inside)], NoSteam, Ct);
        Assert.Equal(("Ricochet", "service"), (answer.Name, answer.Evidence));
    }

    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\EasyAntiCheat\\EasyAntiCheat.exe\" -start", "C:\\Program Files (x86)\\EasyAntiCheat\\EasyAntiCheat.exe")]
    [InlineData("C:\\Games\\X\\BEService.exe -service", "C:\\Games\\X\\BEService.exe")]
    [InlineData("\\??\\D:\\Games\\COD\\randgrid.sys", "D:\\Games\\COD\\randgrid.sys")]
    [InlineData("system32\\drivers\\vgk.sys", null)]
    public void AServicesProgramIsReadFromItsImagePath(string image, string? program) => Assert.Equal(program, ProgramOf(image));

    // ------------------------------------------------------------------ the kept answer

    [Fact]
    public async Task TheKeptAnswerIsWalkedAgainAfterAnUpdateAndAfterTenMinutes()
    {
        Ship("Game.exe");
        var exe = Path.Combine(Install, "Game.exe");
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var walks = 0;
        using var held = new ManualResetEventSlim(true);
        var cache = new AntiCheatCheck.AnswerCache(
            game => { Interlocked.Increment(ref walks); held.Wait(Ct); return DlssSwapService.AntiCheatInFolders(game, Ct); },
            (game, walked) => FromScan(game, walked, NoServices, NoSteam), () => now);
        var game = Game(target: exe);
        Assert.Equal(Kind.None, cache.For(game).Kind);
        Assert.Equal(Kind.None, cache.For(game).Kind);
        Assert.Equal(1, walks);
        // The update brings an anti-cheat and a new program.
        Ship("EasyAntiCheat/Settings.json");
        File.WriteAllText(exe, "a newer build");
        Assert.Equal("Easy Anti-Cheat", cache.For(game).Name);
        Assert.Equal(2, walks);
        now += TimeSpan.FromMinutes(11);
        // Ten minutes on, the kept answer is given at once, without waiting for the walk behind that refreshes it.
        held.Reset();
        var asked = Task.Run(() => cache.For(game), Ct);
        var kept = await asked.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("Easy Anti-Cheat", kept.Name);
        held.Set();
        for (var waited = 0; Volatile.Read(ref walks) < 3 && waited < 5_000; waited += 20) await Task.Delay(20, Ct);
        Assert.Equal(3, Volatile.Read(ref walks));
        Assert.NotNull(cache.Peek(game));
    }

    // ------------------------------------------------------------------ Native Exo Shaders policy parity

    [Fact]
    public void NativeExoShadersPolicyMatchesOfflineAntiCheatRule()
    {
        var anticheatRs = File.ReadAllText(Repo.PathOf("native-ui", "crates", "exo-rpc", "src", "anticheat.rs"));
        var policyRs = File.ReadAllText(Repo.PathOf("native-ui", "crates", "exo-shaders", "src", "policy.rs"));
        Assert.Contains("OfflineAntiCheat", anticheatRs);
        Assert.Contains("CompetitivePvpOnline", anticheatRs);
        Assert.Contains("OnlineNonCompetitivePve", anticheatRs);
        Assert.Contains("allows_offline_dll_mod", anticheatRs);
        Assert.Contains("RouteKind::RecorderProxy", policyRs);
        Assert.Contains("allows_offline_dll_mod()", policyRs);
    }

    [Fact]
    public void AntiCheatPrefixModulesAreMatchedByPrefix()
    {
        string[] prefixModules = ["EasyAntiCheat", "BEService", "BEClient", "BattlEye", "vgk", "vgc", "GameGuard", "Ricochet", "XIGNCODE"];
        Assert.All(prefixModules, module => Assert.True(ForFile(module + "x.dll") is not null, module + " is not matched by its start"));
    }

    // ------------------------------------------------------------------ the callers

    [Fact]
    public void LaunchOptionsReachAnAntiCheatGameButRunAsAdminDoesNot()
    {
        Ship("Game.exe", "BattlEye/BEClient_x64.dll");
        var launch = new GameLaunchOverride { ExtraArgs = "-dx11", RunAsAdmin = true };
        Answer Probe(GameEntry g) => Of(g, NoServices, NoSteam, Ct);
        Assert.Equal((true, false, "This game uses BattlEye, so Exo starts it without administrator rights."),
            Lo(Game("Some Shooter"), launch, Probe));
        DeletionGate.DeleteDirectory(Path.Combine(Install, "BattlEye"), recursive: true);
        Assert.Equal((true, true, (string?)null), Lo(Game("Some Shooter"), launch, Probe));
        // Nothing set: nothing to check, whatever the install.
        var missing = Game("Some Shooter", path: Path.Combine(_root, "not-here"));
        Assert.Equal((true, true, (string?)null), Lo(missing, new GameLaunchOverride(), _ => throw new InvalidOperationException("not asked")));
        // An install Exo couldn't read, and a Riot game: no launch options either.
        Assert.False(Lo(missing, launch, Probe).Item1);
        var riot = Game("VALORANT", StoreKind.Riot);
        Assert.Equal((false, false, "Exo starts Riot games without your launch options."), Lo(riot, launch, Probe));
    }

    [Fact]
    public void TheShaderRecorderIsNeverTurnedOnInAGameWithAntiCheat()
    {
        Ship("Game.exe", "EasyAntiCheat/Settings.json");
        var game = Game();
        GameEntry? Find(string id) => id == game.Id ? game : null;
        Answer Probe(GameEntry g) => Of(g, NoServices, NoSteam, Ct);
        System.Text.Json.Nodes.JsonObject Set(string id, bool on) => new() { ["id"] = id, ["on"] = on };

        Assert.Equal("This game uses Easy Anti-Cheat, so Exo adds no recorder.", ShadersAddon.RecorderRefusal("recorder.set", Set(game.Id, true), Find, Probe));
        Assert.Null(ShadersAddon.RecorderRefusal("recorder.set", Set(game.Id, false), Find, Probe));
        Assert.NotNull(ShadersAddon.RecorderRefusal("recorder.set", Set("steam:unknown", true), Find, Probe));
        Assert.Null(ShadersAddon.RecorderRefusal("game.compile", Set(game.Id, true), Find, Probe));
        DeletionGate.DeleteDirectory(Path.Combine(Install, "EasyAntiCheat"), recursive: true);
        Assert.Null(ShadersAddon.RecorderRefusal("recorder.set", Set(game.Id, true), Find, Probe));
    }

    [Fact]
    public void TheEngineKeepsItsRecorderOutOfWhatExoRefusesAndOfGamesWithoutAntiCheatBetweenSessions()
    {
        // exo.library's noRecorder: the engine's "Record games" never puts the recorder into these by itself.
        Ship("Game.exe", "EasyAntiCheat/Settings.json");
        var game = Game();
        Answer Probe(GameEntry g) => Of(g, NoServices, NoSteam, Ct);
        Assert.True(ShadersAddon.KeepsRecorderOut(game, Probe));
        Assert.True(ShadersAddon.KeepsRecorderOut(game, _ => new Answer(Kind.Unknown, (string?)null, "unreadable")));
        DeletionGate.DeleteDirectory(Path.Combine(Install, "EasyAntiCheat"), recursive: true);
        Assert.False(ShadersAddon.KeepsRecorderOut(game, Probe));
        // Set to start without its anti-cheat: its answer is clean, yet Steam's default entry still runs it, so only a session lets it in.
        var prior = WithoutAntiCheatLaunch;
        try
        {
            WithoutAntiCheatLaunch = g => g.Id == game.Id ? new ExoLauncher.Adapters.SteamAppInfoNames.AntiCheatFreeLaunch("option1", "Play without anti-cheat", "Game.exe") : prior(g);
            Assert.True(ShadersAddon.KeepsRecorderOut(game, _ => Answer.Clean));
        }
        finally { WithoutAntiCheatLaunch = prior; }
    }

    [Fact]
    public void PrepareIsNotOfferedWhenTheAnswerIsUnknown()
    {
        Ship("Game.exe", "Engine/Binaries/Win64/x.dll");
        Assert.True(ShaderPrepare.Eligible(Game(), Answer.Clean));
        Assert.False(ShaderPrepare.Eligible(Game(), new Answer(Kind.Unknown, (string?)null, "unreadable")));
        Assert.False(ShaderPrepare.Eligible(Game(), new Answer(Kind.Found, "BattlEye", "files")));
    }

    [Fact]
    public void EverySettingsWriterIsBehindTheSettingsFileRow()
    {
        // Modern Warfare's profile: allowed with Ricochet found (DIRECTION.md point 4), never when Exo can't check the game.
        Ship("cod26/x.txt");
        var pc = new GameSettings.Pc(null, null, null);
        var found = new GameEntry { Id = "local:mw-found-" + Guid.NewGuid().ToString("N"), Title = "Call of Duty", Store = StoreKind.Local, Installed = true, Path = Install };
        Assert.NotNull(GameSettings.For(found, pc));
        Ship("Locked/inside.txt");
        Lock(Path.Combine(Install, "Locked"));
        var unknown = new GameEntry { Id = "local:mw-unknown-" + Guid.NewGuid().ToString("N"), Title = "Call of Duty", Store = StoreKind.Local, Installed = true, Path = Install };
        Assert.Null(GameSettings.For(unknown, pc));
    }

    [Fact]
    public void ADeletionOfTheGamesOwnFilesWaitsForTheGameAndItsAntiCheat()
    {
        Ship("cod.exe");
        var game = Game("Call of Duty");
        var ricochet = new Answer(Kind.Found, "Ricochet", "files");
        IEnumerable<(string, string?)> Nothing() => [("explorer", @"C:\Windows\explorer.exe")];
        Assert.Null(DeletionRefusal(game, ricochet, Nothing));
        IEnumerable<(string, string?)> TheGame() => [("cod", Path.Combine(Install, "cod.exe"))];
        Assert.Equal("Close Call of Duty first.", DeletionRefusal(game, ricochet, TheGame));
        var eac = new Answer(Kind.Found, "Easy Anti-Cheat", "files");
        IEnumerable<(string, string?)> ItsAntiCheat() => [("EasyAntiCheat", null)];
        Assert.Equal("Close Call of Duty first.", DeletionRefusal(game, eac, ItsAntiCheat));
        // Another game's anti-cheat running is not this game's.
        Assert.Null(DeletionRefusal(game, ricochet, ItsAntiCheat));
        Assert.Equal("Exo couldn't check this game for anti-cheat, so it leaves it as it is.", DeletionRefusal(game, new(Kind.Unknown, (string?)null, Unreadable), Nothing));
        Assert.Equal("Exo never changes Riot games or games with Vanguard.", DeletionRefusal(Game("VALORANT", StoreKind.Riot), new(Kind.Found, DlssSwapService.RiotVanguard, "store"), Nothing));
        Assert.Null(DeletionRefusal(game, new(Kind.PossiblyVac, PossiblyVacName, "steam"), Nothing));
    }

    [Fact]
    public void SteamsWordIsAskedAtEveryAskNotKeptWithTheWalk()
    {
        // A game bought minutes ago: not in Steam's list yet (possibly VAC), then Steam writes it (none).
        Ship("game.exe");
        var steamWord = SteamWord.CannotTell;
        var walks = 0;
        var cache = new AntiCheatCheck.AnswerCache(
            game => { walks++; return DlssSwapService.AntiCheatInFolders(game, Ct); },
            (game, walked) => FromScan(game, walked, NoServices, _ => steamWord));
        var bought = new GameEntry { Id = "steam:3300000", Title = "New game", Store = StoreKind.Steam, Installed = true, Path = Install, LaunchTarget = "3300000" };
        Assert.Equal(Kind.PossiblyVac, cache.For(bought).Kind);
        Assert.False(Allows(bought, cache.For(bought), Change.FrameGenMod));
        steamWord = SteamWord.None;
        Assert.Equal(Kind.None, cache.For(bought).Kind);
        Assert.True(Allows(bought, cache.For(bought), Change.FrameGenMod));
        Assert.Equal(Kind.None, cache.Peek(bought)!.Value.Kind);
        Assert.Equal(1, walks);
    }

    public static TheoryData<string, bool> LaunchArgumentRows() => new()
    {
        // the argument => refused in a game that isn't clean
        { "-insecure", true },
        { "+insecure", true },
        { "-allow_third_party_software", true },
        { "-noeac", true },
        { "-NoBattlEye", true },
        { "-nobe", true },
        { "-noanticheat", true },
        { "-eac=0", true },
        { "-battleye=false", true },
        { "-anticheat:off", true },
        { "-plugin=C:\\mods", true },
        { "+plugin_load mymod", true },
        { "-inject C:\\mods\\hook.dll", true },
        { "\"D:\\x\\reshade.dll\"", true },
        { "-dx11", false },
        { "-novid -high -fullscreen", false },
        { "+fps_max 240", false },
        { "-windowed -w 1920 -h 1080", false },
        { "-nobeep", false },
        { "-eac=1", false },
    };

    [Theory]
    [MemberData(nameof(LaunchArgumentRows))]
    public void EveryRefusedLaunchArgumentRow(string arguments, bool refused)
    {
        Assert.Equal(refused, RefusedArgument(arguments) is not null);
        Ship("Game.exe", "EasyAntiCheat/Settings.json");
        Answer Probe(GameEntry g) => Of(g, NoServices, NoSteam, Ct);
        var launch = new GameLaunchOverride { ExtraArgs = arguments };
        var found = LaunchOrchestrator.LaunchOverrideAllowed(Game("Some Shooter"), launch, Probe);
        Assert.Equal((!refused, refused ? RefusedArgumentSentence : null), (found.Args, found.Dropped));
        // A game with none takes what the player typed.
        DeletionGate.DeleteDirectory(Path.Combine(Install, "EasyAntiCheat"), recursive: true);
        Assert.True(LaunchOrchestrator.LaunchOverrideAllowed(Game("Some Shooter"), launch, Probe).Args);
    }

    [Fact]
    public void AWorkingDirectoryIsPlacedSoOnlyACleanGameGetsIt()
    {
        Ship("Game.exe", "BattlEye/BEClient_x64.dll");
        Answer Probe(GameEntry g) => Of(g, NoServices, NoSteam, Ct);
        var launch = new GameLaunchOverride { WorkingDirectory = Path.Combine(_root, "elsewhere") };
        Assert.Equal((true, false, false, "This game uses BattlEye, so Exo starts it from its own folder."),
            LaunchOrchestrator.LaunchOverrideAllowed(Game("Some Shooter"), new GameLaunchOverride { WorkingDirectory = launch.WorkingDirectory, ExtraArgs = null }, Probe) is var d ? (d.Args, d.Folder, d.Admin, d.Dropped) : default);
        Assert.Equal(Category.Placed, CategoryOf(Change.WorkingDirectory));
        Assert.False(Allows(Game(), new Answer(Kind.PossiblyVac, PossiblyVacName, "steam"), Change.WorkingDirectory));
        DeletionGate.DeleteDirectory(Path.Combine(Install, "BattlEye"), recursive: true);
        Assert.True(LaunchOrchestrator.LaunchOverrideAllowed(Game("Some Shooter"), launch, Probe).Folder);
    }

    [Fact]
    public void TheUpscalerPanelSaysOnePlainStatus()
    {
        var game = Game();
        Assert.Equal("Uses BattlEye. Upscaler updates are off for this game.", DlssSwapService.AntiCheatMessage(game, "BattlEye", null));
        Assert.Equal("Uses BattlEye. Upscaler updates allowed.", DlssSwapService.AntiCheatMessage(game, "BattlEye", "BattlEye"));
        Assert.Equal("May use Valve Anti-Cheat. Upscaler updates are off for this game.", DlssSwapService.AntiCheatMessage(game, PossiblyVacName, null));
        Assert.Equal("Uses an anti-cheat. Upscaler updates are off for this game.", DlssSwapService.AntiCheatMessage(game, SteamListed, null));
        Assert.Equal("Exo couldn't check this game for anti-cheat, so it leaves it as it is.", DlssSwapService.AntiCheatMessage(game, Unreadable, null));
        Assert.Equal("Exo couldn't find this game's folder.", LaunchSentence(game, new(Kind.Unknown, (string?)null, NoInstall), admin: false, field: true));
        Assert.Equal("Exo couldn't find this game's folder.", LaunchSentence(game, new(Kind.Unknown, (string?)null, NoInstall), admin: false));
        Assert.Equal("an anti-cheat", Check(Game("Black Desert")).Name);
    }

    [Fact]
    public void CallOfDutysMarkerStaysWhenExoCouldNotReadTheGame()
    {
        Ship("cod.exe", "Locked/inside.txt");
        var marker = Path.Combine(Install, "__cod");
        File.WriteAllBytes(marker, BitConverter.GetBytes(int.MaxValue - 7));
        Lock(Path.Combine(Install, "Locked"));
        var unread = new GameEntry { Id = "local:cod-unread-" + Guid.NewGuid().ToString("N"), Title = "Call of Duty", Store = StoreKind.Local, Installed = true, Path = Install };
        Assert.False(GameSettings.ClearLeftoverMarker(unread));
        Assert.True(File.Exists(marker));
        Unlock(Path.Combine(Install, "Locked"));
        var read = new GameEntry { Id = "local:cod-read-" + Guid.NewGuid().ToString("N"), Title = "Call of Duty", Store = StoreKind.Local, Installed = true, Path = Install };
        Assert.True(GameSettings.ClearLeftoverMarker(read));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void ExoCapturesHookIsOnlyForAGameWithNone()
    {
        Assert.True(Allows(Game(), Answer.Clean, Change.InjectedHook));
        Assert.False(Allows(Game(), new Answer(Kind.Found, "Easy Anti-Cheat", "files"), Change.InjectedHook));
        Assert.False(Allows(Game(), new Answer(Kind.Unknown, (string?)null, "unreadable"), Change.InjectedHook));
        Assert.False(Allows(Game(), new Answer(Kind.PossiblyVac, PossiblyVacName, "steam"), Change.InjectedHook));
    }

    [Fact]
    public void WutheringWavesUsesAntiCheatExpertAndKeepsPlacedChangesBlocked()
    {
        var game = Game("Wuthering Waves");
        var answer = Check(game);
        Assert.Equal((Kind.Found, AntiCheatCheck.Ace), (answer.Kind, answer.Name));

        // Placed files/hooks (OptiScaler, FrameGenMod, InjectedHook, ShaderRecorder, etc.) stay hard-blocked for ACE.
        foreach (var placed in new[] { Change.FrameGenMod, Change.InjectedHook, Change.ShaderRecorder, Change.WorkingDirectory, Change.RunAsAdmin, Change.StartThroughSteam })
            Assert.False(Allows(game, answer, placed));

        // Safe interface / NVIDIA DRS profile optimization remains allowed.
        Assert.True(Allows(game, answer, Change.GameSettingsFile));
    }

    // ------------------------------------------------------------------ fixture helpers

    /// <summary>Arguments, Run as admin and the sentence of a launch override's verdict.</summary>
    private static (bool, bool, string?) Lo(GameEntry game, GameLaunchOverride launch, Func<GameEntry, Answer> check)
    {
        var d = LaunchOrchestrator.LaunchOverrideAllowed(game, launch, check);
        return (d.Args, d.Admin, d.Dropped);
    }

    private void Lock(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(security);
        _locked.Add(folder);
    }

    private static void Unlock(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            var security = info.GetAccessControl();
            security.RemoveAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "the junction was not made");
        _links.Add(link);
    }
}

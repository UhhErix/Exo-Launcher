using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Adapters.SteamAppInfoNames;
using static ExoLauncher.Services.AntiCheatCheck;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways "Launch without anti-cheat" (a Steam game whose Play menu has an entry without it, NBA 2K26's) could go wrong:
/// - the wrong entry is picked: the protected one, one tied to a beta or another OS, an editor or benchmark beside a plain
///   game, an entry that names the anti-cheat as on, or one Steam gives no "optionN" word (which steam://launch needs);
/// - a game with one entry, or none without its anti-cheat, is offered the choice;
/// - the pick does not make the game count as without anti-cheat, so frame generation and the rest stay refused;
/// - the pick reaches past the hard stops: Riot or Vanguard, an install Exo could not read, Valve Anti-Cheat (on in every
///   entry), a store's or a service's anti-cheat;
/// - with the pick off ("with anti-cheat"), anything is let in;
/// - the launch link takes anything but an app id and an option word.
/// </summary>
public sealed class SteamWithoutAntiCheatTests
{
    private static LaunchEntry Entry(string id, string exe, string description, string type, string os = "windows", string beta = "") =>
        new(id, exe, "", description, type, "", os, beta);

    /// <summary>NBA 2K26's config/launch, as its appinfo has it.</summary>
    private static readonly LaunchEntry[] Nba2K26 =
    [
        Entry("0", "start_protected_game.exe", "Play NBA 2K26", "default"),
        Entry("1", "NBA2K26.exe", "NBA 2K26 without EAC (offline only)", "option1"),
    ];

    [Fact]
    public void Nba2K26sEntryWithoutEacIsOption1()
    {
        Assert.Equal(new AntiCheatFreeLaunch("option1", "NBA 2K26 without EAC (offline only)", "NBA2K26.exe"), WithoutAntiCheat(Nba2K26));
        // Steam's order doesn't matter, nor a description in other words beside the bootstrapper.
        Assert.Equal("option2", WithoutAntiCheat(
        [
            Entry("0", "Game_BE.exe", "Play", "default"),
            Entry("1", "Game_BE.exe", "Play (DX11)", "option1"),
            Entry("2", "bin/Game.exe", "Play", "option2"),
        ])?.Option);
        // A plain game whose other entry says offline.
        Assert.Equal("option1", WithoutAntiCheat([Entry("0", "Game.exe", "Play", "default"), Entry("1", "Game.exe", "Play offline", "option1")])?.Option);
    }

    [Fact]
    public void NoChoiceWhereSteamOffersNoEntryWithoutTheAntiCheat()
    {
        Assert.Null(WithoutAntiCheat([]));
        Assert.Null(WithoutAntiCheat([Nba2K26[0]]));
        // An editor beside a plain game is not a start without anti-cheat.
        Assert.Null(WithoutAntiCheat([Entry("0", "Game.exe", "Play", "default"), Entry("1", "Editor.exe", "Level editor", "option1")]));
        // The other entry is the bootstrapper too, or says the anti-cheat is on.
        Assert.Null(WithoutAntiCheat([Nba2K26[0], Entry("1", "EasyAntiCheat_launcher.exe", "Play (DX11)", "option1")]));
        Assert.Null(WithoutAntiCheat([Nba2K26[0], Entry("1", "Game.exe", "Play with EasyAntiCheat", "option1")]));
        // Only on a beta branch, only on another OS, or with no option word for steam://launch.
        Assert.Null(WithoutAntiCheat([Nba2K26[0], Nba2K26[1] with { BetaKey = "public_test" }]));
        Assert.Null(WithoutAntiCheat([Nba2K26[0], Nba2K26[1] with { OsList = "linux" }]));
        Assert.Null(WithoutAntiCheat([Nba2K26[0], Nba2K26[1] with { Type = "" }]));
        Assert.Null(WithoutAntiCheat([Nba2K26[0], Nba2K26[1] with { Type = "option0" }]));
        Assert.Null(WithoutAntiCheat([Nba2K26[0], Nba2K26[1] with { Executable = "NBA2K26.sh" }]));
    }

    private static readonly GameEntry Nba = new() { Id = "steam:3472040", Title = "NBA 2K26", Store = StoreKind.Steam, LaunchTarget = "3472040", Installed = true, Path = @"D:\SteamLibrary\steamapps\common\NBA 2K26" };

    /// <summary>The files show Easy Anti-Cheat; Steam names no anti-cheat.</summary>
    private static Answer Judge(bool without, SteamWord steam = SteamWord.None, string? store = null, string? service = null, string disk = "Easy Anti-Cheat") =>
        Decide(store, null, true, disk, service, steam, null, withoutEntry: without);

    [Fact]
    public void WithoutAntiCheatTheGameCountsAsWithoutIt()
    {
        var with = Judge(without: false);
        Assert.Equal(Kind.Found, with.Kind);
        foreach (var change in new[] { Change.FrameGenMod, Change.ShaderRecorder, Change.InjectedHook, Change.WorkingDirectory })
            Assert.False(Allows(Nba, with, change));

        var without = Judge(without: true);
        Assert.Equal((Kind.None, WithoutAntiCheatEvidence), (without.Kind, without.Evidence));
        Assert.False(without.AssumeProtected);
        foreach (var change in new[] { Change.FrameGenMod, Change.ShaderRecorder, Change.InjectedHook, Change.WorkingDirectory, Change.LaunchOptions })
            Assert.True(Allows(Nba, without, change));
        // Steam's silence alone flips the same way.
        Assert.Equal(Kind.None, Decide(null, null, true, null, null, SteamWord.CannotTell, null, withoutEntry: true).Kind);
        Assert.Equal(Kind.PossiblyVac, Decide(null, null, true, null, null, SteamWord.CannotTell, null).Kind);
    }

    [Fact]
    public void TheHardStopsHoldWhicheverEntryIsPicked()
    {
        // Valve Anti-Cheat runs in every entry; a store's or a service's anti-cheat is not the entry's to skip.
        Assert.Equal(Kind.Found, Judge(without: true, steam: SteamWord.Vac).Kind);
        Assert.Equal(Kind.Found, Judge(without: true, store: "Warden").Kind);
        Assert.Equal(Kind.Found, Judge(without: true, service: "Easy Anti-Cheat").Kind);
        // An install Exo could not read stays unknown.
        Assert.Equal(Kind.Unknown, Judge(without: true, disk: OutOfTime).Kind);
        Assert.Equal(Kind.Unknown, Judge(without: true, disk: "Easy Anti-Cheat and " + Unreadable).Kind);
        // Riot and Vanguard: never, by store, by name or by the game itself.
        Assert.Equal(Kind.Found, Decide(DlssSwapService.RiotVanguard, null, true, null, null, SteamWord.NotSteam, null, withoutEntry: true).Kind);
        Assert.Equal(Kind.Found, Judge(without: true, disk: DlssSwapService.RiotVanguard).Kind);
        var riot = new GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = StoreKind.Riot, Installed = true, Path = @"C:\Riot Games\VALORANT" };
        Assert.False(Allows(riot, Judge(without: true), Change.FrameGenMod));
        Assert.Null(WithoutAntiCheatLaunch(riot));
        // Picked, and Steam offers the entry: only the Steam game that isn't Riot's starts without.
        var chose = ChoseWithoutAntiCheat;
        var free = SteamFreeEntry;
        ChoseWithoutAntiCheat = game => game.LaunchTarget == "3472040" || chose(game);
        SteamFreeEntry = id => id == 3472040 ? WithoutAntiCheat(Nba2K26) : free(id);
        try
        {
            Assert.Equal("option1", WithoutAntiCheatLaunch(Nba)?.Option);
            Assert.Null(WithoutAntiCheatLaunch(new GameEntry { Id = "steam:3472040", Title = "VALORANT", Store = StoreKind.Steam, LaunchTarget = "3472040", Path = @"C:\Riot Games\VALORANT" }));
            Assert.Null(WithoutAntiCheatLaunch(new GameEntry { Id = "epic:nba", Title = "NBA 2K26", Store = StoreKind.Epic, LaunchTarget = "3472040" }));
        }
        finally
        {
            ChoseWithoutAntiCheat = chose;
            SteamFreeEntry = free;
        }
    }

    [Fact]
    public void TheLaunchLinkTakesOnlyAnAppIdAndAnOptionWord()
    {
        Assert.Equal("steam://launch/3472040/option1", SteamProtocol.LaunchEntryUri("3472040", "option1"));
        Assert.Throws<ArgumentException>(() => SteamProtocol.LaunchEntryUri("3472040", "option1/../dialog"));
        Assert.Throws<ArgumentException>(() => SteamProtocol.LaunchEntryUri("3472040", "dialog"));
        Assert.Throws<ArgumentException>(() => SteamProtocol.LaunchEntryUri("3472040", "option"));
        Assert.Throws<ArgumentException>(() => SteamProtocol.LaunchEntryUri("0", "option1"));
    }
}

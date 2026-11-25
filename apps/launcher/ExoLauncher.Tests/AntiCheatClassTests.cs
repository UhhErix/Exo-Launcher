using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class AntiCheatClassTests
{
    private static GameEntry Make(string id, string title, StoreKind store = StoreKind.Steam) =>
        new() { Id = id, Title = title, Store = store, Installed = true, Path = @"C:\Games\Dummy" };

    [Theory]
    [InlineData("riot:valorant", "VALORANT", StoreKind.Riot, "Riot Vanguard")]
    [InlineData("riot:league_of_legends", "League of Legends", StoreKind.Riot, "Riot Vanguard")]
    [InlineData("steam:1938090", "Call of Duty: Modern Warfare 4", StoreKind.Steam, "Ricochet")]
    [InlineData("steam:730", "Counter-Strike 2", StoreKind.Steam, "Valve Anti-Cheat")]
    [InlineData("epic:fortnite", "Fortnite", StoreKind.Epic, "Easy Anti-Cheat")]
    [InlineData("steam:1172470", "Apex Legends", StoreKind.Steam, "Easy Anti-Cheat")]
    [InlineData("steam:359550", "Tom Clancy's Rainbow Six Siege", StoreKind.Steam, "BattlEye")]
    [InlineData("roblox:player", "Roblox", StoreKind.Local, "Hyperion")]
    [InlineData("local:tarkov", "Escape from Tarkov", StoreKind.Local, "BattlEye")]
    [InlineData("steam:2943650", "FragPunk", StoreKind.Steam, "NetEase anti-cheat")]
    public void CompetitivePvpOnlineGamesAllowConfigAndDriverOnlyNeverDllMods(string id, string title, StoreKind store, string acName)
    {
        var game = Make(id, title, store);
        var ac = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, [acName], "test");

        var acClass = AntiCheatClass.Classify(game, ac);

        Assert.Equal(GameAntiCheatClass.CompetitivePvpOnline, acClass);
        Assert.Equal(DllModPolicy.Disallowed, AntiCheatClass.DllPolicyFor(acClass));
        Assert.False(AntiCheatClass.AllowsAutoInstallDll(acClass));
    }

    [Theory]
    [InlineData("steam:3513350", "Wuthering Waves", "Anti-Cheat Expert")]
    [InlineData("local:genshin", "Genshin Impact", "HoYoKProtect")]
    [InlineData("local:hsr", "Honkai: Star Rail", "HoYoKProtect")]
    [InlineData("local:zzz", "Zenless Zone Zero", "HoYoKProtect")]
    [InlineData("steam:230410", "Warframe", "Warframe anti-cheat")]
    public void OnlineNonCompetitivePveGamesAllowConfigByDefaultAndOptInDllBehindBanRiskConfirmation(string id, string title, string acName)
    {
        var game = Make(id, title);
        var ac = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, [acName], "test");

        var acClass = AntiCheatClass.Classify(game, ac);

        Assert.Equal(GameAntiCheatClass.OnlineNonCompetitivePve, acClass);
        Assert.Equal(DllModPolicy.OptInWithBanRiskConfirmation, AntiCheatClass.DllPolicyFor(acClass));
        Assert.False(AntiCheatClass.AllowsAutoInstallDll(acClass));
    }

    [Fact]
    public void OfflineAntiCheatGamesAllowDllWithOneTimeNote()
    {
        var nbaOffline = Make("steam:3472040", "NBA 2K26");
        var officialOffline = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.None, [], AntiCheatCheck.WithoutAntiCheatEvidence);
        Assert.Equal(GameAntiCheatClass.OfflineAntiCheat, AntiCheatClass.Classify(nbaOffline, officialOffline));
        Assert.Equal(DllModPolicy.AllowedWithOfflineNote, AntiCheatClass.DllPolicyFor(GameAntiCheatClass.OfflineAntiCheat));
        Assert.False(AntiCheatClass.AllowsAutoInstallDll(GameAntiCheatClass.OfflineAntiCheat));

        var eldenRing = Make("steam:1245620", "ELDEN RING");
        var eac = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Easy Anti-Cheat"], "test");
        Assert.Equal(GameAntiCheatClass.OfflineAntiCheat, AntiCheatClass.Classify(eldenRing, eac));
    }

    [Fact]
    public void UnknownAntiCheatGameTreatedAsCompetitivePvpOnline()
    {
        var mystery = Make("steam:987654", "Unknown Online Game");
        var unknown = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Unknown, [], "test");
        var unlistedFound = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Easy Anti-Cheat"], "test");

        Assert.Equal(GameAntiCheatClass.CompetitivePvpOnline, AntiCheatClass.Classify(mystery, unknown));
        Assert.Equal(GameAntiCheatClass.CompetitivePvpOnline, AntiCheatClass.Classify(mystery, unlistedFound));
        Assert.Equal(DllModPolicy.Disallowed, AntiCheatClass.DllPolicyFor(AntiCheatClass.Classify(mystery, unknown)));
    }

    [Fact]
    public void CleanGameWithNoAntiCheatAllowsAllRoutes()
    {
        var control = Make("steam:3669870", "CONTROL Resonant");
        var none = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.None, [], "test");

        var acClass = AntiCheatClass.Classify(control, none);
        Assert.Equal(GameAntiCheatClass.NoAntiCheat, acClass);
        Assert.Equal(DllModPolicy.Allowed, AntiCheatClass.DllPolicyFor(acClass));
        Assert.True(AntiCheatClass.AllowsAutoInstallDll(acClass));
    }

    [Fact]
    public void WireClassMapsAllFourHostContractValues()
    {
        var val = Make("riot:valorant", "VALORANT", StoreKind.Riot);
        var wuwa = Make("steam:3513350", "Wuthering Waves");
        var elden = Make("steam:1245620", "ELDEN RING");
        var clean = Make("steam:3669870", "CONTROL Resonant");
        var unscanned = Make("steam:999991", "Mystery Indie");

        Assert.Equal("competitivePvpOnline", AntiCheatClass.WireClass(val, null));
        Assert.Equal("onlineNonCompetitivePve", AntiCheatClass.WireClass(wuwa, new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Anti-Cheat Expert"], "test")));
        Assert.Equal("offlineAntiCheat", AntiCheatClass.WireClass(elden, new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Easy Anti-Cheat"], "test")));
        Assert.Equal("offlineAntiCheat", AntiCheatClass.WireClass(clean, AntiCheatCheck.Answer.Clean));
        Assert.Equal("unknown", AntiCheatClass.WireClass(unscanned, null));
        Assert.Equal("unknown", AntiCheatClass.WireClass(unscanned, new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Unknown, [], AntiCheatCheck.Unreadable)));
    }
}

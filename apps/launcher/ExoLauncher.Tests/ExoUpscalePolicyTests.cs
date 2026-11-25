using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ExoUpscalePolicyTests
{
    private static GameEntry Make(string id, string title, StoreKind store = StoreKind.Steam) =>
        new() { Id = id, Title = title, Store = store, Installed = true, Path = @"C:\Games\Dummy" };

    [Fact]
    public void CompetitivePvpAndUnknownAllowDrsOnlyAndNeverDllSwapOrProxy()
    {
        var valorant = Make("riot:valorant", "VALORANT", StoreKind.Riot);
        var vanguard = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Riot Vanguard"], "test");
        var vPolicy = ExoUpscaleService.EvaluatePolicy(valorant, vanguard, banRiskConfirmed: true, officialOfflineMode: true);
        Assert.Equal(GameAntiCheatClass.CompetitivePvpOnline, vPolicy.Class);
        Assert.True(vPolicy.DrsOverrideAllowed);
        Assert.False(vPolicy.SignedDllSwapAllowed);
        Assert.False(vPolicy.FgBridgeAllowed);

        var apex = Make("steam:1172470", "Apex Legends");
        var eac = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Easy Anti-Cheat"], "test");
        var aPolicy = ExoUpscaleService.EvaluatePolicy(apex, eac, banRiskConfirmed: true, officialOfflineMode: true);
        Assert.Equal(GameAntiCheatClass.CompetitivePvpOnline, aPolicy.Class);
        Assert.True(aPolicy.DrsOverrideAllowed);
        Assert.False(aPolicy.SignedDllSwapAllowed);
        Assert.False(aPolicy.FgBridgeAllowed);

        var mystery = Make("steam:9999999", "Unknown Game");
        var unknown = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Unknown, (string?)null, "unreadable");
        var uPolicy = ExoUpscaleService.EvaluatePolicy(mystery, unknown, banRiskConfirmed: true, officialOfflineMode: true);
        Assert.Equal(GameAntiCheatClass.CompetitivePvpOnline, uPolicy.Class);
        Assert.True(uPolicy.DrsOverrideAllowed);
        Assert.False(uPolicy.SignedDllSwapAllowed);
        Assert.False(uPolicy.FgBridgeAllowed);
    }

    [Fact]
    public void OnlineNonCompetitivePveAllowsDrsByDefaultAndDllSwapOrBridgeBehindBanRiskConfirmation()
    {
        var wuwa = Make("steam:3513350", "Wuthering Waves");
        var ace = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Anti-Cheat Expert"], "test");

        var unconfirmed = ExoUpscaleService.EvaluatePolicy(wuwa, ace, banRiskConfirmed: false);
        Assert.Equal(GameAntiCheatClass.OnlineNonCompetitivePve, unconfirmed.Class);
        Assert.True(unconfirmed.DrsOverrideAllowed);
        Assert.True(unconfirmed.RequiresBanRiskConfirmation);
        Assert.False(unconfirmed.SignedDllSwapAllowed);
        Assert.False(unconfirmed.FgBridgeAllowed);
        Assert.Equal(AntiCheatClass.BanRiskConfirmation("Wuthering Waves", "Anti-Cheat Expert"), unconfirmed.WarningOrNote);

        var confirmed = ExoUpscaleService.EvaluatePolicy(wuwa, ace, banRiskConfirmed: true);
        Assert.True(confirmed.DrsOverrideAllowed);
        Assert.True(confirmed.SignedDllSwapAllowed);
        Assert.True(confirmed.FgBridgeAllowed);
    }

    [Fact]
    public void OfflineAntiCheatAllowsInOfficialOfflineModeWithOfflineOneTimeNote()
    {
        var eldenRing = Make("steam:1245620", "ELDEN RING");
        var eac = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Easy Anti-Cheat"], "test");

        var online = ExoUpscaleService.EvaluatePolicy(eldenRing, eac, officialOfflineMode: false);
        Assert.Equal(GameAntiCheatClass.OfflineAntiCheat, online.Class);
        Assert.True(online.DrsOverrideAllowed);
        Assert.True(online.RequiresOfflineNote);
        Assert.False(online.SignedDllSwapAllowed);
        Assert.False(online.FgBridgeAllowed);
        Assert.Equal(AntiCheatClass.OfflineOneTimeNote("ELDEN RING"), online.WarningOrNote);

        var offline = ExoUpscaleService.EvaluatePolicy(eldenRing, eac, officialOfflineMode: true);
        Assert.True(offline.DrsOverrideAllowed);
        Assert.True(offline.RequiresOfflineNote);
        Assert.True(offline.SignedDllSwapAllowed);
        Assert.True(offline.FgBridgeAllowed);

        var nba = Make("steam:3472040", "NBA 2K26");
        var officialWithoutAc = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.None, [], AntiCheatCheck.WithoutAntiCheatEvidence);
        var nbaOffline = ExoUpscaleService.EvaluatePolicy(nba, officialWithoutAc, officialOfflineMode: false);
        Assert.Equal(GameAntiCheatClass.OfflineAntiCheat, nbaOffline.Class);
        Assert.True(nbaOffline.SignedDllSwapAllowed);
        Assert.True(nbaOffline.FgBridgeAllowed);
        Assert.True(nbaOffline.RequiresOfflineNote);
    }

    [Fact]
    public void NoAntiCheatAllowsDrsSignedDllSwapAndFgBridgeCleanly()
    {
        var sekiro = Make("steam:814380", "Sekiro: Shadows Die Twice");
        var none = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.None, (string?)null, "clean");

        var policy = ExoUpscaleService.EvaluatePolicy(sekiro, none);
        Assert.Equal(GameAntiCheatClass.NoAntiCheat, policy.Class);
        Assert.True(policy.DrsOverrideAllowed);
        Assert.True(policy.SignedDllSwapAllowed);
        Assert.True(policy.FgBridgeAllowed);
        Assert.False(policy.RequiresBanRiskConfirmation);
        Assert.False(policy.RequiresOfflineNote);
    }
}

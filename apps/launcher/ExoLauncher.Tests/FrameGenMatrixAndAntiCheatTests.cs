using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;
using static ExoLauncher.Services.DlssSwapService;

namespace ExoLauncher.Tests;

public sealed class FrameGenMatrixAndAntiCheatTests
{
    private static FrameGenGame Game(
        bool dlssg = false,
        int? major = 310,
        bool own = false,
        int? streamline = 2,
        bool vulkan = false,
        bool noDlssg = false,
        string? antiCheat = null,
        bool hasUpscaler = true,
        string api = "dx12",
        bool noMfg = false,
        bool versionTaken = false,
        bool dlssFailed = false) =>
        new(@"C:\Game", @"C:\Game\Game.exe", dlssg, major, own, streamline, vulkan, null, antiCheat, null, noDlssg,
            HasUpscaler: hasUpscaler, Api: api, NoMfgHere: noMfg, VersionDllTaken: versionTaken, DlssFailed: dlssFailed);

    [Fact]
    public void MatrixCoversEveryCardAndGameNeverReturnsNull()
    {
        var rtx50 = FrameGenCardFor("NVIDIA GeForce RTX 5090");
        var rtx40 = FrameGenCardFor("NVIDIA GeForce RTX 4080");
        var rtx30 = FrameGenCardFor("NVIDIA GeForce RTX 3070");
        var rtx20 = FrameGenCardFor("NVIDIA GeForce RTX 2080 Ti");
        var gtx16 = FrameGenCardFor("NVIDIA GeForce GTX 1660 Ti");
        var amd = FrameGenCardFor("AMD Radeon RX 7800 XT");
        var intel = FrameGenCardFor("Intel Arc B580 Graphics");

        Assert.Equal("native", FrameGenPath(rtx50, Game(dlssg: true)));
        Assert.Equal("native", FrameGenPath(rtx40, Game(dlssg: true)));
        Assert.Equal("optifg", FrameGenPath(rtx50, Game(dlssg: false, hasUpscaler: true, api: "dx12")));
        Assert.Equal("optifg", FrameGenPath(rtx40, Game(dlssg: false, hasUpscaler: true, api: "dx12")));
        Assert.Equal("optifg", FrameGenPath(rtx40, Game(dlssg: false, hasUpscaler: false, api: "dx11")));

        Assert.Equal("dlss", FrameGenPath(rtx30, Game(dlssg: true, major: 310, streamline: 2)));
        Assert.Equal("dlss", FrameGenPath(rtx20, Game(dlssg: true, major: 310, streamline: 2)));
        Assert.Equal("mfg", FrameGenPath(rtx30, Game(dlssg: true, major: 310, streamline: 2, vulkan: true)));
        Assert.Equal("mfg", FrameGenPath(rtx30, Game(dlssg: true, major: 310, streamline: 1)));
        Assert.Equal("mfg", FrameGenPath(rtx30, Game(dlssg: true, major: 3, streamline: 2)));
        Assert.Equal("mfg", FrameGenPath(rtx30, Game(dlssg: true, major: 310, streamline: 2, versionTaken: true)));
        Assert.Equal("fsr", FrameGenPath(rtx30, Game(dlssg: true, major: 310, streamline: 2, noDlssg: true, noMfg: true)));
        Assert.Equal("fsr", FrameGenPath(rtx30, Game(dlssg: true, major: 310, streamline: 2, noDlssg: true, dlssFailed: true)));
        Assert.Equal("optifg", FrameGenPath(rtx30, Game(dlssg: false, hasUpscaler: true, api: "dx12")));
        Assert.Equal("optifg", FrameGenPath(rtx30, Game(dlssg: false, hasUpscaler: false, api: "dx11")));

        Assert.Equal("fsr", FrameGenPath(gtx16, Game(dlssg: true)));
        Assert.Equal("fsr", FrameGenPath(amd, Game(dlssg: true)));
        Assert.Equal("fsr", FrameGenPath(intel, Game(dlssg: true)));
        Assert.Equal("optifg", FrameGenPath(gtx16, Game(dlssg: false, hasUpscaler: false, api: "dx11")));
        Assert.Equal("optifg", FrameGenPath(amd, Game(dlssg: false, hasUpscaler: false, api: "dx11")));
        Assert.Equal("optifg", FrameGenPath(intel, Game(dlssg: false, hasUpscaler: false, api: "dx11")));
        Assert.Equal("optifg", FrameGenPath(null, Game(dlssg: false, hasUpscaler: false, api: "dx11")));
        Assert.Equal("native", FrameGenPath(null, Game(own: true, hasUpscaler: false, api: "dx11")));
    }

    [Fact]
    public void WutheringWavesOnRtx3070PrefersNativeBuiltInWithDlssgSm86OptInFallback()
    {
        var rtx30 = FrameGenCardFor("NVIDIA GeForce RTX 3070");
        var wuwa = new GameEntry { Id = "steam:3513350", Title = "Wuthering Waves", Store = StoreKind.Steam };
        var layout = Game(dlssg: true, major: 310, own: true, streamline: 2, antiCheat: "Anti-Cheat Expert");

        Assert.Equal("native", FrameGenPath(rtx30, layout, wuwa));
        Assert.Equal("dlss", FrameGenModFallbackPath(rtx30, layout));

        var verdict = FrameGenDecide(new DlssSwapService.FrameGenFacts(
            Readable: true,
            Wanted: "native",
            Native: true,
            AntiCheat: "Anti-Cheat Expert",
            RiskWarning: FrameGenAntiCheat.ConfirmationWarning(wuwa, "Anti-Cheat Expert"),
            FallbackPath: "dlss"));
        Assert.Equal("native", verdict.Status.State);
        Assert.Equal("dlss", verdict.Status.Path);
        Assert.Equal("DLSSG SM86", verdict.Status.Package);
        Assert.True(verdict.Status.CanToggle);
        Assert.Equal("Wuthering Waves runs Anti-Cheat Expert. A mod loading into it can get the account banned.", verdict.Status.RiskWarning);
        Assert.Equal(FrameGenNext.Nothing, verdict.Next);
    }

    [Fact]
    public void AntiCheatGamesNeverHideOrBlockAndRequireExplicitPerGameConfirmation()
    {
        var rtx30 = FrameGenCardFor("NVIDIA GeForce RTX 3070");
        var valorant = new GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = StoreKind.Riot };
        var cod = new GameEntry { Id = "steam:1938090", Title = "Call of Duty", Store = StoreKind.Steam };
        var roblox = new GameEntry { Id = "roblox:player", Title = "Roblox", Store = StoreKind.Roblox };
        var wuwa = new GameEntry { Id = "steam:3513350", Title = "Wuthering Waves", Store = StoreKind.Steam };
        var elden = new GameEntry { Id = "steam:1245620", Title = "ELDEN RING", Store = StoreKind.Steam };
        var cs2 = new GameEntry { Id = "steam:730", Title = "Counter-Strike 2", Store = StoreKind.Steam };
        var clean = new GameEntry { Id = "steam:1620730", Title = "Hell is Us", Store = StoreKind.Steam };

        Assert.Equal(FrameGenAntiCheatVerdict.DllSafe, FrameGenAntiCheat.Evaluate(clean, null));
        Assert.Equal(FrameGenAntiCheatVerdict.NeedsConfirmation, FrameGenAntiCheat.Evaluate(valorant, RiotVanguard));
        Assert.Equal(FrameGenAntiCheatVerdict.NeedsConfirmation, FrameGenAntiCheat.Evaluate(cod, "Ricochet"));
        Assert.Equal(FrameGenAntiCheatVerdict.NeedsConfirmation, FrameGenAntiCheat.Evaluate(roblox, "Hyperion"));
        Assert.Equal(FrameGenAntiCheatVerdict.NeedsConfirmation, FrameGenAntiCheat.Evaluate(wuwa, "Anti-Cheat Expert"));
        Assert.Equal(FrameGenAntiCheatVerdict.NeedsConfirmation, FrameGenAntiCheat.Evaluate(elden, "Easy Anti-Cheat"));
        Assert.Equal(FrameGenAntiCheatVerdict.NeedsConfirmation, FrameGenAntiCheat.Evaluate(cs2, "Valve Anti-Cheat"));
        Assert.Equal(FrameGenAntiCheatVerdict.NeedsConfirmation, FrameGenAntiCheat.Evaluate(clean, AntiCheatCheck.Unreadable));

        Assert.Equal("dlss", FrameGenPath(rtx30, Game(dlssg: true, antiCheat: RiotVanguard), valorant));
        Assert.Equal("optifg", FrameGenPath(rtx30, Game(dlssg: false, antiCheat: "Ricochet"), cod));
        Assert.Equal("optifg", FrameGenPath(rtx30, Game(dlssg: false, antiCheat: "Easy Anti-Cheat"), elden));
        Assert.Equal("native", FrameGenPath(rtx30, Game(own: true, antiCheat: "Anti-Cheat Expert"), wuwa));

        Assert.Equal("VALORANT runs Riot Vanguard. A mod loading into it can get the account banned.", FrameGenAntiCheat.ConfirmationWarning(valorant, RiotVanguard));
        Assert.Equal("Call of Duty runs Activision Ricochet. A mod loading into it can get the account banned.", FrameGenAntiCheat.ConfirmationWarning(cod, "Ricochet"));
        Assert.Equal("ELDEN RING runs Easy Anti-Cheat. A mod loading into it can get the account banned.", FrameGenAntiCheat.ConfirmationWarning(elden, "Easy Anti-Cheat"));
        Assert.Equal("Wuthering Waves runs Anti-Cheat Expert. A mod loading into it can get the account banned.", FrameGenAntiCheat.ConfirmationWarning(wuwa, "Anti-Cheat Expert"));

        var decideAc = FrameGenDecide(new DlssSwapService.FrameGenFacts(
            Readable: true,
            Wanted: "optifg",
            AntiCheat: "Easy Anti-Cheat",
            RiskWarning: FrameGenAntiCheat.ConfirmationWarning(elden, "Easy Anti-Cheat")));
        Assert.Equal("off", decideAc.Status.State);
        Assert.Equal("optifg", decideAc.Status.Path);
        Assert.Equal("OptiScaler FG", decideAc.Status.Package);
        Assert.True(decideAc.Status.CanToggle);
        Assert.Equal(FrameGenNext.Nothing, decideAc.Next);
    }

    [Fact]
    public void AntiCheatPerGameTableHasAtLeastThirtyEvidenceBackedRulesWithoutLossless()
    {
        Assert.True(FrameGenAntiCheat.PerGameRules.Count >= 30);
        foreach (var rule in FrameGenAntiCheat.PerGameRules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Id));
            Assert.False(string.IsNullOrWhiteSpace(rule.Reason));
            Assert.False(string.IsNullOrWhiteSpace(rule.Evidence));
            Assert.Equal(FrameGenAntiCheatVerdict.NeedsConfirmation, rule.Verdict);
            Assert.DoesNotContain("Lossless", rule.Evidence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Lossless", rule.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExoNeverLaunchesWithoutAntiCheatByItselfWhenUserHasNotOptedIn()
    {
        var nba = new GameEntry { Id = "steam:3472040", Title = "NBA 2K26", Store = StoreKind.Steam, LaunchTarget = "3472040" };
        var priorChose = AntiCheatCheck.ChoseWithoutAntiCheat;
        var priorFree = AntiCheatCheck.SteamFreeEntry;
        try
        {
            var optedIn = false;
            AntiCheatCheck.ChoseWithoutAntiCheat = g => optedIn && g.Id == nba.Id;
            AntiCheatCheck.SteamFreeEntry = appId => appId == 3472040
                ? new SteamAppInfoNames.AntiCheatFreeLaunch("option1", "NBA 2K26 without EAC (offline only)", "NBA2K26.exe")
                : null;

            Assert.True(FrameGenAntiCheat.CanOfferOfflineLaunch(nba));
            Assert.Null(AntiCheatCheck.WithoutAntiCheatLaunch(nba));

            optedIn = true;
            var launch = AntiCheatCheck.WithoutAntiCheatLaunch(nba);
            Assert.NotNull(launch);
            Assert.Equal("option1", launch.Option);
        }
        finally
        {
            AntiCheatCheck.ChoseWithoutAntiCheat = priorChose;
            AntiCheatCheck.SteamFreeEntry = priorFree;
        }
    }

    [Fact]
    public void AllThreeModSourcesHavePinnedSha256AndByteSize()
    {
        foreach (var mod in new[] { DlssgSm86, MfgUnlocker, OptiScaler })
        {
            Assert.False(string.IsNullOrWhiteSpace(mod.Repo));
            var pin = PinnedFrameGenReleases.FirstOrDefault(p => p.Repo == mod.Repo).Release;
            Assert.NotNull(pin);
            Assert.True(pin.Size > 100_000, $"Unexpected pin size for {mod.Repo}");
            Assert.Equal(64, pin.Sha256.Length);
        }
    }
}

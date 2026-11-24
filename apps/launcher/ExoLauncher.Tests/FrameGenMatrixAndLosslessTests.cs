using System.Xml.Linq;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Services.DlssSwapService;

namespace ExoLauncher.Tests;

public sealed class FrameGenMatrixAndLosslessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-fg-matrix-" + Guid.NewGuid().ToString("N"));

    public FrameGenMatrixAndLosslessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) DeletionGate.DeleteDirectory(_root, recursive: true); }
        catch (IOException) { }
    }

    private static FrameGenGame Game(
        bool dlssg = false,
        int? major = 310,
        bool own = false,
        int? streamline = 2,
        bool vulkan = false,
        string? antiCheat = null,
        bool noDlssg = false,
        bool hasUpscaler = false,
        string api = "dx12",
        bool noMfg = false,
        bool versionTaken = false,
        bool dlssFailed = false) =>
        new(@"C:\Game", @"C:\Game\Game.exe", dlssg, major, own, streamline, vulkan, null, antiCheat, null,
            noDlssg, dlssg, hasUpscaler, api, noMfg, versionTaken, dlssFailed);

    [Fact]
    public void MatrixCoversEveryCardAndGameType()
    {
        var rtx50 = FrameGenCardFor("NVIDIA GeForce RTX 5090");
        var rtx40 = FrameGenCardFor("NVIDIA GeForce RTX 4080");
        var rtx30 = FrameGenCardFor("NVIDIA GeForce RTX 3070");
        var rtx20 = FrameGenCardFor("NVIDIA GeForce RTX 2070 SUPER");
        var gtx16 = FrameGenCardFor("NVIDIA GeForce GTX 1660 SUPER");
        var amd = FrameGenCardFor("AMD Radeon RX 7800 XT");
        var intel = FrameGenCardFor("Intel(R) Arc(TM) B580 Graphics");

        Assert.Equal("dlss", FrameGenPath(rtx30, Game(dlssg: true)));
        Assert.Equal("dlss", FrameGenPath(rtx20, Game(dlssg: true)));
        Assert.Equal("fsr", FrameGenPath(rtx30, Game(dlssg: true, noDlssg: true, dlssFailed: true)));
        Assert.Equal("mfg", FrameGenPath(rtx30, Game(dlssg: true, vulkan: true, api: "vulkan")));
        Assert.Equal("mfg", FrameGenPath(rtx30, Game(dlssg: true, noDlssg: true, versionTaken: true)));
        Assert.Equal("mfg", FrameGenPath(rtx30, Game(dlssg: true, major: 3)));
        Assert.Equal("fsr", FrameGenPath(rtx30, Game(dlssg: true, vulkan: true, api: "vulkan", noMfg: true)));

        Assert.Equal("native", FrameGenPath(rtx50, Game(dlssg: true)));
        Assert.Equal("native", FrameGenPath(rtx40, Game(dlssg: true)));
        Assert.Equal("native", FrameGenPath(rtx40, Game(own: true)));
        Assert.Equal("optifg", FrameGenPath(rtx50, Game(hasUpscaler: true, api: "dx12")));
        Assert.Equal("optifg", FrameGenPath(rtx40, Game(hasUpscaler: true, api: "dx12")));
        Assert.Equal("optifg", FrameGenPath(rtx30, Game(hasUpscaler: true, api: "dx12")));
        Assert.Equal("optifg", FrameGenPath(rtx20, Game(hasUpscaler: true, api: "vulkan")));
        Assert.Equal("optifg", FrameGenPath(gtx16, Game(hasUpscaler: true, api: "dx12")));
        Assert.Equal("optifg", FrameGenPath(amd, Game(hasUpscaler: true, api: "dx12")));
        Assert.Equal("optifg", FrameGenPath(intel, Game(hasUpscaler: true, api: "vulkan")));

        Assert.Equal("fsr", FrameGenPath(amd, Game(dlssg: true)));
        Assert.Equal("fsr", FrameGenPath(intel, Game(dlssg: true)));
        Assert.Equal("fsr", FrameGenPath(gtx16, Game(dlssg: true)));

        Assert.Equal("lossless", FrameGenPath(rtx50, Game(hasUpscaler: false, api: "dx11")));
        Assert.Equal("lossless", FrameGenPath(rtx40, Game(hasUpscaler: true, api: "dx11")));
        Assert.Equal("lossless", FrameGenPath(rtx30, Game(hasUpscaler: false, api: "dx12")));
        Assert.Equal("lossless", FrameGenPath(amd, Game(hasUpscaler: false, api: "dx11")));
        Assert.Equal("lossless", FrameGenPath(intel, Game(hasUpscaler: false, api: "dx11")));
        Assert.Equal("lossless", FrameGenPath(null, Game(hasUpscaler: false, api: "dx11")));
    }

    [Fact]
    public void AntiCheatPolicyBlocksKernelOverlayRestrictorsAndOffersLosslessOnlyOtherwise()
    {
        var valorant = new GameEntry { Id = "riot:valorant", Title = "VALORANT", Store = StoreKind.Riot };
        var cod = new GameEntry { Id = "steam:1938090", Title = "Call of Duty", Store = StoreKind.Steam };
        var roblox = new GameEntry { Id = "roblox:player", Title = "Roblox", Store = StoreKind.Roblox };
        var wuwa = new GameEntry { Id = "steam:3513350", Title = "Wuthering Waves", Store = StoreKind.Steam };
        var elden = new GameEntry { Id = "steam:1245620", Title = "ELDEN RING", Store = StoreKind.Steam };
        var nba = new GameEntry { Id = "steam:3472040", Title = "NBA 2K26", Store = StoreKind.Steam };
        var cs2 = new GameEntry { Id = "steam:730", Title = "Counter-Strike 2", Store = StoreKind.Steam };
        var sekiro = new GameEntry { Id = "steam:814380", Title = "Sekiro", Store = StoreKind.Steam };

        Assert.Equal(FrameGenAntiCheatVerdict.Blocked, FrameGenAntiCheat.Evaluate(valorant, "Riot Vanguard"));
        Assert.Equal(FrameGenAntiCheatVerdict.Blocked, FrameGenAntiCheat.Evaluate(cod, "Ricochet"));
        Assert.Equal(FrameGenAntiCheatVerdict.Blocked, FrameGenAntiCheat.Evaluate(cs2, "FACEIT"));
        Assert.Equal(FrameGenAntiCheatVerdict.Blocked, FrameGenAntiCheat.Evaluate(roblox, "Hyperion"));
        Assert.Equal(FrameGenAntiCheatVerdict.Blocked, FrameGenAntiCheat.Evaluate(elden, AntiCheatCheck.Unreadable));
        Assert.Equal(FrameGenAntiCheatVerdict.LosslessOnly, FrameGenAntiCheat.Evaluate(wuwa, "Anti-Cheat Expert"));
        Assert.Equal(FrameGenAntiCheatVerdict.LosslessOnly, FrameGenAntiCheat.Evaluate(elden, "Easy Anti-Cheat"));
        Assert.Equal(FrameGenAntiCheatVerdict.LosslessOnly, FrameGenAntiCheat.Evaluate(nba, "Easy Anti-Cheat"));
        Assert.Equal(FrameGenAntiCheatVerdict.LosslessOnly, FrameGenAntiCheat.Evaluate(cs2, "Valve Anti-Cheat"));
        Assert.Equal(FrameGenAntiCheatVerdict.DllSafe, FrameGenAntiCheat.Evaluate(sekiro, null));

        var rtx30 = FrameGenCardFor("NVIDIA GeForce RTX 3070");
        Assert.Null(FrameGenPath(rtx30, Game(dlssg: true, antiCheat: "Riot Vanguard"), valorant));
        Assert.Null(FrameGenPath(rtx30, Game(dlssg: true, antiCheat: "Ricochet"), cod));
        Assert.Equal("lossless", FrameGenPath(rtx30, Game(dlssg: true, antiCheat: "Easy Anti-Cheat"), elden));
        Assert.Equal("lossless", FrameGenPath(rtx30, Game(dlssg: true, antiCheat: "Anti-Cheat Expert"), wuwa));
        Assert.True(FrameGenAntiCheat.PerGameRules.Count >= 25);
        Assert.All(FrameGenAntiCheat.PerGameRules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.Evidence)));
    }

    [Fact]
    public void PinnedReleasesIncludeDlssgSm86MfgUnlockerAndOptiScaler()
    {
        var sm86 = NewestPinnedRelease(DlssgSm86, PinnedFrameGenReleases);
        var mfg = NewestPinnedRelease(MfgUnlocker, PinnedFrameGenReleases);
        var opti = NewestPinnedRelease(OptiScaler, PinnedFrameGenReleases);

        Assert.Equal("0.4.2", sm86.Tag);
        Assert.Equal(89769758L, sm86.Size);
        Assert.Equal("v1.7.2", mfg.Tag);
        Assert.Equal(5334929L, mfg.Size);
        Assert.Equal("v0.9.5", opti.Tag);
        Assert.Equal(70418897L, opti.Size);
    }

    [Fact]
    public void OptiFgIniEnablesUpscalerInputFsrfgOutputHudfixAndLogging()
    {
        var ini = FrameGenIniText("upscaler", 140);
        Assert.Contains("Enabled=true", ini, StringComparison.Ordinal);
        Assert.Contains("FGInput=upscaler", ini, StringComparison.Ordinal);
        Assert.Contains("FGOutput=fsrfg", ini, StringComparison.Ordinal);
        Assert.Contains("HUDFix=true", ini, StringComparison.Ordinal);
        Assert.Contains("LogToFile=true", ini, StringComparison.Ordinal);
        Assert.Contains("FramerateLimit=140", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void LosslessScalingProfileRoundTripPreservesExistingProfilesAndRestoresCleanly()
    {
        var fakeExe = Path.Combine(_root, "LosslessScaling.exe");
        File.WriteAllText(fakeExe, "stub");
        var settingsPath = Path.Combine(_root, "Settings.xml");
        File.WriteAllText(settingsPath, """
<?xml version="1.0" encoding="utf-8"?>
<Settings>
  <Hotkey>S</Hotkey>
  <HotkeyModifierKeys>Alt Control</HotkeyModifierKeys>
  <GameProfiles>
    <Profile>
      <Title>Default</Title>
      <FrameGeneration>Off</FrameGeneration>
    </Profile>
  </GameProfiles>
</Settings>
""");
        var started = false;
        var closed = false;
        var service = new LosslessScalingService
        {
            StatePath = Path.Combine(_root, "lossless.json"),
            SettingsPathProvider = () => settingsPath,
            ExePathProvider = () => fakeExe,
            IsProcessRunningFunc = () => started,
            LaunchProcessFunc = _ => { started = true; return true; },
            CloseProcessAction = () => { closed = true; started = false; },
        };

        var detected = service.Detect();
        Assert.True(detected.Installed);
        Assert.Equal("Alt+Ctrl+S", detected.Hotkey);

        var game = new GameEntry { Id = "steam:814380", Title = "Sekiro", Store = StoreKind.Steam };
        var gameExe = Path.Combine(_root, "sekiro.exe");
        var enabled = service.EnableGame(game, gameExe, "FIXED", 3);
        Assert.True(enabled.Ok);
        Assert.True(service.IsEnabled(game.Id));

        var doc = XDocument.Load(settingsPath);
        var profiles = doc.Root!.Element("GameProfiles")!.Elements("Profile").ToList();
        Assert.Equal(2, profiles.Count);
        var added = profiles[1];
        Assert.Equal("Sekiro", added.Element("Title")?.Value);
        Assert.Equal("LSFG3", added.Element("FrameGeneration")?.Value);
        Assert.Equal("FIXED", added.Element("LSFG3Mode1")?.Value);
        Assert.Equal("3", added.Element("LSFG3Multiplier")?.Value);
        Assert.Equal("true", added.Element("AutoScale")?.Value);

        service.OnGameLaunching(game);
        Assert.True(started);
        service.OnGameEnded(game);
        Assert.True(closed);
        Assert.False(started);

        var disabled = service.DisableGame(game.Id);
        Assert.True(disabled.Ok);
        Assert.False(service.IsEnabled(game.Id));
        var after = XDocument.Load(settingsPath).Root!.Element("GameProfiles")!.Elements("Profile").ToList();
        Assert.Single(after);
        Assert.Equal("Default", after[0].Element("Title")?.Value);
    }

    [Fact]
    public void LosslessScalingNotInstalledRefusesEnableAndProvidesSteamStoreUrl()
    {
        var service = new LosslessScalingService
        {
            StatePath = Path.Combine(_root, "lossless.json"),
            SettingsPathProvider = () => Path.Combine(_root, "missing.xml"),
            ExePathProvider = () => null,
        };

        var info = service.Detect();
        Assert.False(info.Installed);
        Assert.Equal(LosslessScalingService.SteamStoreUrl, info.StoreUrl);

        var res = service.EnableGame(new GameEntry { Id = "steam:814380", Title = "Sekiro", Store = StoreKind.Steam }, @"C:\Game\sekiro.exe");
        Assert.False(res.Ok);
    }
}

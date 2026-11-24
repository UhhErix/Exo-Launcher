using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class GameGraphicsFeaturesTests : IDisposable
{
    private readonly string _root;

    public GameGraphicsFeaturesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "exo-gfx-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) DeletionGate.DeleteDirectory(_root, recursive: true);
        }
        catch
        {
        }
    }

    private static GameSettings.Pc Rtx3070 => new("nvidia", 7, 8.0, 1, 144, true, 1000, "NVIDIA GeForce RTX 3070");
    private static GameSettings.Pc Rx7800Xt => new("amd", 7, 16.0, 1, 144, true, 1000, "AMD Radeon RX 7800 XT");
    private static AntiCheatCheck.Answer NoAntiCheat => new(AntiCheatCheck.Kind.None, [], "test");

    [Fact]
    public void OfflineModeModNoteExplainsOfflineMode()
    {
        var note = GameGraphicsFeatures.OfflineModeModNote("NBA 2K26");
        Assert.Equal("NBA 2K26 runs in offline mode with anti-cheat. Mods load only in offline play.", note);
    }

    [Fact]
    public void OnlineNonCompetitivePveAllowsOptInDllBehindBanRiskConfirmationNeverAutoApplied()
    {
        var install = Path.Combine(_root, "pve-opti");
        Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(install, "GenshinImpact.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(install, "nvngx_dlss.dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(install, "OptiScaler.ini"), "[Upscalers]\r\nDx12Upscaler=auto\r\n");

        var game = new GameEntry { Id = "local:genshin", Title = "Genshin Impact", Store = StoreKind.Local, Installed = true, Path = install };
        var ac = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["HoYoKProtect"], "test");
        var stateRoot = Path.Combine(_root, "state-pve");
        var ctx = new GameGraphicsFeatures.RuntimeContext(Rx7800Xt, ac, null, false, stateRoot);

        var snap = GameGraphicsFeatures.Inspect(game, ctx);
        var up = snap.Features.Single(f => f.Id == GameGraphicsFeatures.FeatureUpscalers);
        Assert.Equal(GameGraphicsFeatures.RouteDll, up.RouteKind);
        Assert.True(up.CanToggle);
        Assert.True(up.NeedsAntiCheatConfirm);
        Assert.Equal("Genshin Impact runs HoYoKProtect. A mod loading into it can get the account banned.", up.AntiCheatWarning);

        var refused = GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureUpscalers, true, acceptAntiCheatRisk: false, null, ctx);
        Assert.False(refused.Ok);

        string? savedConsent = null;
        var allowed = GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureUpscalers, true, acceptAntiCheatRisk: true, (_, token) => savedConsent = token, ctx);
        Assert.True(allowed.Ok);
        Assert.Equal("HoYoKProtect", savedConsent);
        Assert.Contains("Dx12Upscaler=fsr31", File.ReadAllText(Path.Combine(install, "OptiScaler.ini")));
    }

    [Fact]
    public void EveryGameAlwaysExposesAllFiveFeatureRows()
    {
        var dir = Path.Combine(_root, "generic-game");
        Directory.CreateDirectory(dir);
        var game = new GameEntry { Id = "steam:999999", Title = "Indie Raster", Store = StoreKind.Steam, Installed = true, Path = dir };
        var ctx = new GameGraphicsFeatures.RuntimeContext(Rtx3070, NoAntiCheat, null, false, Path.Combine(_root, "state"));

        var snap = GameGraphicsFeatures.Inspect(game, ctx);

        Assert.True(snap.Ok);
        Assert.Equal(5, snap.Features.Count);
        Assert.Equal(
            [GameGraphicsFeatures.FeatureUpscalers, GameGraphicsFeatures.FeatureReflex, GameGraphicsFeatures.FeatureRayTracing, GameGraphicsFeatures.FeaturePathTracing, GameGraphicsFeatures.FeatureHiddenSettings],
            snap.Features.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void Unreal5ApplyAndRevertLeavesEngineIniAndGameUserSettingsByteIdentical()
    {
        var install = Path.Combine(_root, "ue5-install");
        Directory.CreateDirectory(Path.Combine(install, "Engine"));
        Directory.CreateDirectory(Path.Combine(install, "DemoProj", "Binaries", "Win64"));
        File.WriteAllBytes(Path.Combine(install, "DemoProj", "Binaries", "Win64", "DemoProj-Win64-Shipping.exe"), [0x4D, 0x5A, 0x90, 0x00]);
        File.WriteAllBytes(Path.Combine(install, "DemoProj", "Binaries", "Win64", "sl.reflex.dll"), [0x4D, 0x5A]);

        var cfgDir = Path.Combine(install, "DemoProj", "Saved", "Config", "Windows");
        Directory.CreateDirectory(cfgDir);
        var gusPath = Path.Combine(cfgDir, "GameUserSettings.ini");
        var enginePath = Path.Combine(cfgDir, "Engine.ini");
        var originalGus = Encoding.Latin1.GetBytes("[ScalabilityGroups]\r\nsg.ResolutionQuality=60\r\n\r\n[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\nFrameRateLimit=60.000000\r\nNvidiaReflex=Disabled\r\n");
        File.WriteAllBytes(gusPath, originalGus);
        Assert.False(File.Exists(enginePath));

        var game = new GameEntry { Id = "steam:555001", Title = "Demo UE5", Store = StoreKind.Steam, Installed = true, Path = install };
        var stateRoot = Path.Combine(_root, "state-ue5");
        var ctx = new GameGraphicsFeatures.RuntimeContext(Rtx3070, NoAntiCheat, null, false, stateRoot);

        var prevSeam = NvidiaProfiles.ForGameSeam;
        NvidiaProfiles.ForGameSeam = (_, ids, writes) =>
        {
            var before = ids.ToDictionary(id => id, _ => (uint?)null);
            var after = writes is null ? before : ids.ToDictionary(id => id, id => writes.GetValueOrDefault(id));
            return new NvidiaProfiles.GameState(true, null, "Exo: Demo UE5", before, after, []);
        };
        try
        {
            var rtOn = GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureRayTracing, true, false, null, ctx);
            Assert.True(rtOn.Ok);
            Assert.True(File.Exists(enginePath));
            var engineText = File.ReadAllText(enginePath);
            Assert.Contains("r.RayTracing=1", engineText);
            Assert.Contains("r.Lumen.HardwareRayTracing=1", engineText);

            var hiddenOn = GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureHiddenSettings, true, false, null, ctx);
            Assert.True(hiddenOn.Ok);
            var gusAfter = File.ReadAllText(gusPath);
            Assert.Contains("FrameRateLimit=0.000000", gusAfter);
            Assert.Contains("bUseVSync=False", gusAfter);
            Assert.Contains("sg.ResolutionQuality=100", gusAfter);
            Assert.Contains("r.MaxAnisotropy=16", File.ReadAllText(enginePath));

            var rtOff = GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureRayTracing, false, false, null, ctx);
            Assert.True(rtOff.Ok);
            Assert.DoesNotContain("r.Lumen.HardwareRayTracing=1", File.ReadAllText(enginePath));
            Assert.Contains("r.MaxAnisotropy=16", File.ReadAllText(enginePath));

            var hiddenOff = GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureHiddenSettings, false, false, null, ctx);
            Assert.True(hiddenOff.Ok);
            Assert.False(File.Exists(enginePath));
            Assert.Equal(originalGus, File.ReadAllBytes(gusPath));
        }
        finally
        {
            NvidiaProfiles.ForGameSeam = prevSeam;
        }
    }

    [Fact]
    public void WutheringWavesLocalStorageAndIniUnlockAndRevertByteIdentically()
    {
        var install = Path.Combine(_root, "wuwa");
        var win64 = Path.Combine(install, "Client", "Binaries", "Win64");
        var cfgDir = Path.Combine(install, "Client", "Saved", "Config", "WindowsNoEditor");
        var dbDir = Path.Combine(install, "Client", "Saved", "LocalStorage");
        Directory.CreateDirectory(win64);
        Directory.CreateDirectory(cfgDir);
        Directory.CreateDirectory(dbDir);
        File.WriteAllBytes(Path.Combine(win64, "Client-Win64-Shipping.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(win64, "Client-Win64-ShippingBase.dll"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(win64, "sl.reflex.dll"), [0x4D, 0x5A]);

        var dbPath = Path.Combine(dbDir, "LocalStorage.db");
        using (var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE LocalStorage (key TEXT PRIMARY KEY, value TEXT); INSERT INTO LocalStorage VALUES ('CustomFrameRate','2'),('RayTracing','0'),('RayTracedGI','0'),('RayTracedReflection','0'),('RayTracedShadow','0'),('NvidiaSuperSamplingEnable','0'),('NvidiaSuperSamplingQuality','2');";
            cmd.ExecuteNonQuery();
        }
        var originalDbBytes = File.ReadAllBytes(dbPath);

        var gusPath = Path.Combine(cfgDir, "GameUserSettings.ini");
        var originalGus = Encoding.Latin1.GetBytes("[ScalabilityGroups]\r\nsg.ResolutionQuality=100.000000\r\nsg.RayTracingQuality=0\r\n[/Script/Engine.GameUserSettings]\r\nFrameRateLimit=60.000000\r\nFramePace=0\r\n");
        File.WriteAllBytes(gusPath, originalGus);

        var enginePath = Path.Combine(cfgDir, "Engine.ini");
        var originalEngine = Encoding.Latin1.GetBytes("[Core.System]\r\nPaths=../../../Client/Content\r\n");
        File.WriteAllBytes(enginePath, originalEngine);

        var game = new GameEntry { Id = "steam:3513350", Title = "Wuthering Waves", Store = StoreKind.Steam, Installed = true, Path = install };
        var aceAnswer = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Anti-Cheat Expert"], "test");
        var ctx = new GameGraphicsFeatures.RuntimeContext(Rtx3070, aceAnswer, null, false, Path.Combine(_root, "state-wuwa"));

        var snap = GameGraphicsFeatures.Inspect(game, ctx);
        Assert.Equal("Unreal Engine 4.26 (Kuro)", snap.Engine);
        var rtRow = snap.Features.Single(f => f.Id == GameGraphicsFeatures.FeatureRayTracing);
        var hiddenRow = snap.Features.Single(f => f.Id == GameGraphicsFeatures.FeatureHiddenSettings);
        Assert.True(rtRow.CanToggle);
        Assert.False(rtRow.NeedsAntiCheatConfirm);
        Assert.True(hiddenRow.CanToggle);
        Assert.False(hiddenRow.NeedsAntiCheatConfirm);

        var hiddenOn = GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureHiddenSettings, true, false, null, ctx);
        Assert.True(hiddenOn.Ok);
        Assert.Equal("3", GameGraphicsFeatures.ReadWuWaLocalStorageValue(dbPath, "CustomFrameRate"));
        Assert.Contains("FrameRateLimit=120.000000", File.ReadAllText(gusPath));
        Assert.Contains("r.MaxAnisotropy=16", File.ReadAllText(enginePath));

        var rtOn = GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureRayTracing, true, false, null, ctx);
        Assert.True(rtOn.Ok);
        Assert.Equal("3", GameGraphicsFeatures.ReadWuWaLocalStorageValue(dbPath, "RayTracing"));
        Assert.Equal("1", GameGraphicsFeatures.ReadWuWaLocalStorageValue(dbPath, "RayTracedGI"));
        Assert.Contains("sg.RayTracingQuality=3", File.ReadAllText(gusPath));
        Assert.Contains("r.RayTracing=1", File.ReadAllText(enginePath));

        Assert.True(GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureRayTracing, false, false, null, ctx).Ok);
        Assert.True(GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureHiddenSettings, false, false, null, ctx).Ok);

        Assert.Equal(originalDbBytes, File.ReadAllBytes(dbPath));
        Assert.Equal(originalGus, File.ReadAllBytes(gusPath));
        Assert.Equal(originalEngine, File.ReadAllBytes(enginePath));
    }

    [Fact]
    public void OnlineAndUnknownAntiCheatGamesGetConfigAndDriverOnlyNoDllMods()
    {
        var install = Path.Combine(_root, "ac-opti");
        Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(install, "Game.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(install, "nvngx_dlss.dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(install, "OptiScaler.ini"), "[Upscalers]\r\nDx12Upscaler=auto\r\n");

        var game = new GameEntry { Id = "steam:777001", Title = "Online Shooter", Store = StoreKind.Steam, Installed = true, Path = install };
        var eac = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["Easy Anti-Cheat"], "test");
        var unknownAc = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Unknown, [], "test");
        var stateRoot = Path.Combine(_root, "state-ac");

        foreach (var ac in new[] { eac, unknownAc })
        {
            var ctx = new GameGraphicsFeatures.RuntimeContext(Rx7800Xt, ac, null, false, stateRoot);
            var snap = GameGraphicsFeatures.Inspect(game, ctx);
            var upRow = snap.Features.Single(f => f.Id == GameGraphicsFeatures.FeatureUpscalers);
            Assert.NotEqual(GameGraphicsFeatures.RouteDll, upRow.RouteKind);
            Assert.False(upRow.NeedsAntiCheatConfirm);
        }

        var offlineMode = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.None, [], AntiCheatCheck.WithoutAntiCheatEvidence);
        var offlineCtx = new GameGraphicsFeatures.RuntimeContext(Rx7800Xt, offlineMode, null, false, stateRoot);
        var offlineSnap = GameGraphicsFeatures.Inspect(game, offlineCtx);
        var offlineUp = offlineSnap.Features.Single(f => f.Id == GameGraphicsFeatures.FeatureUpscalers);
        Assert.Equal(GameGraphicsFeatures.RouteDll, offlineUp.RouteKind);
        Assert.True(offlineUp.CanToggle);
        Assert.True(offlineUp.NeedsAntiCheatConfirm);

        var needsAck = GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureUpscalers, true, acceptAntiCheatRisk: false, null, offlineCtx);
        Assert.False(needsAck.Ok);

        string? savedNote = null;
        var allowed = GameGraphicsFeatures.Set(game, GameGraphicsFeatures.FeatureUpscalers, true, acceptAntiCheatRisk: true, (_, note) => savedNote = note, offlineCtx);
        Assert.True(allowed.Ok);
        Assert.Equal(AntiCheatCheck.WithoutAntiCheatEvidence, savedNote);
        Assert.Contains("Dx12Upscaler=fsr31", File.ReadAllText(Path.Combine(install, "OptiScaler.ini")));
    }

    [Fact]
    public void BestSettingsNeverAutoAppliesDllModsToAntiCheatGames()
    {
        var install = Path.Combine(_root, "ac-best");
        Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(install, "Game.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(install, "sl.reflex.dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(install, "OptiScaler.ini"), "[Reflex]\r\nForceReflex=0\r\n");
        var originalIni = File.ReadAllBytes(Path.Combine(install, "OptiScaler.ini"));

        var game = new GameEntry { Id = "steam:777002", Title = "Online Co-Op", Store = StoreKind.Steam, Installed = true, Path = install };
        var stateRoot = Path.Combine(_root, "state-best");

        var count = GameGraphicsFeatures.ApplyBestSettingsSafe([game], Rx7800Xt, stateRoot);
        Assert.Equal(0, count);
        Assert.Equal(originalIni, File.ReadAllBytes(Path.Combine(install, "OptiScaler.ini")));
    }

    [Fact]
    public void Dx89ClassicTitleQualifiesForRtxRemixPathTracing()
    {
        var install = Path.Combine(_root, "desert-storm");
        Directory.CreateDirectory(install);
        var game = new GameEntry { Id = "steam:211780", Title = "Conflict Desert Storm", Store = StoreKind.Steam, Installed = true, Path = install };
        var ctx = new GameGraphicsFeatures.RuntimeContext(Rtx3070, NoAntiCheat, null, false, Path.Combine(_root, "state-remix"));

        var snap = GameGraphicsFeatures.Inspect(game, ctx);
        var pt = snap.Features.Single(f => f.Id == GameGraphicsFeatures.FeaturePathTracing);
        Assert.Contains("RTX Remix", pt.Route);
        Assert.Equal(GameGraphicsFeatures.RouteDll, pt.RouteKind);
    }

    [Fact]
    public void SekiroUtf16XmlPatchAndRevertStaysByteIdentical()
    {
        var xmlPath = Path.Combine(_root, "GraphicsConfig.xml");
        var originalText = "<?xml version=\"1.0\" encoding=\"UTF-16\" ?>\r\n<config><QualitySetting>HIGH</QualitySetting><SSAO>HIGH</SSAO></config>\r\n";
        var originalBytes = new UnicodeEncoding(false, false).GetBytes(originalText);
        File.WriteAllBytes(xmlPath, originalBytes);

        GameGraphicsFeatures.PatchXmlTagsPreservingEncoding(xmlPath, new Dictionary<string, string> { ["SSAO"] = "MAX", ["QualitySetting"] = "CUSTOM" });
        var patchedBytes = File.ReadAllBytes(xmlPath);
        var patchedText = Encoding.Unicode.GetString(patchedBytes);
        Assert.Contains("<SSAO>MAX</SSAO>", patchedText);
        Assert.Contains("<QualitySetting>CUSTOM</QualitySetting>", patchedText);
    }
}

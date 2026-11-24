using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class GameProfilesTests
{
    private const string NoGame = "__exo_no_such_game__";

    private static readonly GameSettings.Pc NvidiaMidVrr = new(
        "nvidia", 5, 8, 1, 165, Gpu: "NVIDIA GeForce RTX 3070", Vrr: true, RamGb: 16, X3d: false, LowBandwidth: false);

    private static readonly GameSettings.Pc NvidiaStrongX3dVrr = new(
        "nvidia", 7, 16, 1, 240, Gpu: "NVIDIA GeForce RTX 4080", Vrr: true, RamGb: 32, X3d: true, LowBandwidth: false);

    private static readonly GameSettings.Pc AmdMidNoVrr = new(
        "amd", 7, 12, 1, 144, Gpu: "AMD Radeon RX 7800 XT", Vrr: false, RamGb: 16, X3d: true, LowBandwidth: false);

    private static readonly GameSettings.Pc IntelLowVram = new(
        "intel", 5, 4, 1, 144, Gpu: "Intel(R) Arc(TM) A380 Graphics", Vrr: true, RamGb: 8, X3d: false, LowBandwidth: true);

    private static string NewFolder(string prefix)
    {
        var dir = Path.Combine(TestDataDirectory.Root, prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] ReadFixture(string folder, string file, string lineEnding = "\r\n") =>
        Encoding.Latin1.GetBytes(File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", folder, file)).ReplaceLineEndings(lineEnding));

    [Theory]
    [InlineData(60, true, 59)]
    [InlineData(120, true, 116)]
    [InlineData(144, true, 138)]
    [InlineData(165, true, 157)]
    [InlineData(240, true, 224)]
    [InlineData(360, true, 324)]
    [InlineData(240, false, null)]
    [InlineData(0, true, null)]
    public void VrrFrameCapUsesReflexFormulaOnlyWhenVrrIsOn(int refresh, bool vrr, int? expected)
    {
        var pc = new GameSettings.Pc("nvidia", 7, 8, TopRefresh: refresh, Vrr: vrr);
        Assert.Equal(expected, GameSettings.VrrFrameCap(pc));
    }

    [Fact]
    public void MarvelRivalsHasNoEngineIniProfile()
    {
        var dir = NewFolder("marvel");
        Directory.CreateDirectory(Path.Combine(dir, "MarvelGame"));
        var game = new GameEntry { Id = "steam:2767030", Title = "Marvel Rivals", Store = StoreKind.Steam, Installed = true, Path = dir };
        Assert.Null(GameSettings.For(game, NvidiaMidVrr));
    }

    [Fact]
    public void CallOfDutyWarzoneReadsDecidesWritesPreservesUnknownAndUndoes()
    {
        var dir = NewFolder("cod");
        var cfg = Path.Combine(dir, "options.4.cod24.cst");
        var original = ReadFixture("CallOfDuty", "options.4.cod24.cst");
        File.WriteAllBytes(cfg, original);

        var profile = GameSettings.CallOfDutyWarzone(NvidiaMidVrr, cfg) with
        {
            Id = "cod-" + Guid.NewGuid().ToString("N"),
            Processes = [NoGame],
            Helpers = null,
            Nvidia = null,
        };

        Assert.True(GameSettings.Set(profile, null, true));
        var afterText = File.ReadAllText(cfg);
        Assert.Contains("TextureDownload@0;1;41 = Minimal", afterText, StringComparison.Ordinal);
        Assert.Contains("VideoMemoryScale@0;1;38 = 0.700000", afterText, StringComparison.Ordinal);
        Assert.Contains("ShaderPreload@0;1;44 = true", afterText, StringComparison.Ordinal);
        Assert.Contains("Upscaling@0;1;36 = NVIDIA DLSS", afterText, StringComparison.Ordinal);
        Assert.Contains("GameplayCustomFramerateLimit@0;7723;12213 = 157", afterText, StringComparison.Ordinal);
        Assert.Contains("PlayerCustomKeybind@0;99;99 = Space", afterText, StringComparison.Ordinal);

        Assert.True(GameSettings.SetKey(profile, "TextureDownload", false));
        Assert.Contains("TextureDownload@0;1;41 = Optimized", File.ReadAllText(cfg), StringComparison.Ordinal);

        Assert.True(GameSettings.Set(profile, null, false));
        Assert.Equal(original, File.ReadAllBytes(cfg));
    }

    [Fact]
    public void CallOfDutyDecidesPerHardwareClassAndLowBandwidth()
    {
        var codFile = GameSettings.Parse(ReadFixture("CallOfDuty", "options.4.cod24.cst"));
        var amdFile = GameSettings.Parse(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(ReadFixture("CallOfDuty", "options.4.cod24.cst"))
                .Replace("NVIDIA GeForce RTX 3070", "AMD Radeon RX 7800 XT", StringComparison.Ordinal)));
        var intelFile = GameSettings.Parse(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(ReadFixture("CallOfDuty", "options.4.cod24.cst"))
                .Replace("NVIDIA GeForce RTX 3070", "Intel(R) Arc(TM) A380 Graphics", StringComparison.Ordinal)));

        var nvidiaGroups = GameSettings.CallOfDutyGroups(NvidiaMidVrr, codFile);
        Assert.Contains(nvidiaGroups, g => g.Id == "streaming");
        Assert.Contains(nvidiaGroups, g => g.Id == "framecap");
        Assert.Contains(nvidiaGroups.Single(g => g.Id == "upscaler").Changes, c => c.Key == "Upscaling" && c.Value == "NVIDIA DLSS");

        var amdGroups = GameSettings.CallOfDutyGroups(AmdMidNoVrr, amdFile);
        Assert.DoesNotContain(amdGroups, g => g.Id == "streaming");
        Assert.DoesNotContain(amdGroups, g => g.Id == "framecap");
        Assert.Contains(amdGroups.Single(g => g.Id == "latency").Changes, c => c.Key == "AmdAntilag2");
        Assert.Contains(amdGroups.Single(g => g.Id == "upscaler").Changes, c => c.Key == "Upscaling" && c.Value == "AMD FSR 3");

        var intelGroups = GameSettings.CallOfDutyGroups(IntelLowVram, intelFile);
        Assert.Contains(intelGroups, g => g.Id == "streaming");
        Assert.Contains(intelGroups.Single(g => g.Id == "upscaler").Changes, c => c.Key == "Upscaling" && c.Value == "Intel XeSS");
        Assert.Contains(intelGroups.Single(g => g.Id == "memory").Changes, c => c.Key == "VideoMemoryScale" && c.Value == "0.7");
    }

    [Fact]
    public void RainbowSixSiegeDetectsWritesAllGuidFoldersPreservesUnknownAndUndoes()
    {
        var installDir = NewFolder("r6-install");
        File.WriteAllBytes(Path.Combine(installDir, "RainbowSix.exe"), []);
        var game = new GameEntry { Id = "ubisoft:r6", Title = "Rainbow Six Siege", Store = StoreKind.Ubisoft, Installed = true, Path = installDir };
        Assert.True(GameSettings.IsRainbowSixSiege(game));

        var configRoot = NewFolder("r6-cfg");
        var guidA = Path.Combine(configRoot, "11111111-2222-3333-4444-555555555555");
        var guidB = Path.Combine(configRoot, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Directory.CreateDirectory(guidA);
        Directory.CreateDirectory(guidB);
        var original = ReadFixture("RainbowSixSiege", "GameSettings.ini");
        var iniA = Path.Combine(guidA, "GameSettings.ini");
        var iniB = Path.Combine(guidB, "GameSettings.ini");
        File.WriteAllBytes(iniA, original);
        File.WriteAllBytes(iniB, original);

        Assert.Equal(2, GameSettings.RainbowSixSiegeConfigFiles(configRoot).Count);

        var profile = GameSettings.RainbowSixSiege(NvidiaStrongX3dVrr, configRoot) with
        {
            Id = "r6-" + Guid.NewGuid().ToString("N"),
            Processes = [NoGame],
        };

        Assert.True(GameSettings.Set(profile, null, true));
        foreach (var path in new[] { iniA, iniB })
        {
            var text = File.ReadAllText(path);
            Assert.Contains("WindowMode=2\r\n", text, StringComparison.Ordinal);
            Assert.Contains("VSync=0\r\n", text, StringComparison.Ordinal);
            Assert.Contains("NvidiaReflex=1\r\n", text, StringComparison.Ordinal);
            Assert.Contains("AntiAliasingMethod=0\r\n", text, StringComparison.Ordinal);
            Assert.Contains("TAARenderScaling=100\r\n", text, StringComparison.Ordinal);
            Assert.Contains("ShadowQuality=1\r\n", text, StringComparison.Ordinal);
            Assert.Contains("LODQuality=3\r\n", text, StringComparison.Ordinal);
            Assert.Contains("FPSLimit=224\r\n", text, StringComparison.Ordinal);
            Assert.Contains("CustomUnknownKey=KeepMeExact\r\n", text, StringComparison.Ordinal);
        }

        Assert.True(GameSettings.SetKey(profile, "ShadowQuality", false));
        Assert.Contains("ShadowQuality=3\r\n", File.ReadAllText(iniA), StringComparison.Ordinal);

        Assert.True(GameSettings.Set(profile, null, false));
        Assert.Equal(original, File.ReadAllBytes(iniA));
        Assert.Equal(original, File.ReadAllBytes(iniB));
    }

    [Fact]
    public void RainbowSixSiegeDecidesPerHardwareAndHandlesMissingOrLockedFile()
    {
        var lowGroups = GameSettings.RainbowSixSiegeGroups(IntelLowVram);
        Assert.DoesNotContain(lowGroups.SelectMany(g => g.Changes), c => c.Key == "NvidiaReflex");
        Assert.Contains(lowGroups.Single(g => g.Id == "antialiasing").Changes, c => c.Key == "AntiAliasingMethod" && c.Value == "1");
        Assert.Contains(lowGroups.Single(g => g.Id == "antialiasing").Changes, c => c.Key == "TAARenderScaling" && c.Value == "85");
        Assert.Contains(lowGroups.Single(g => g.Id == "quality").Changes, c => c.Key == "TextureQuality" && c.Value == "0");
        Assert.Contains(GameSettings.RainbowSixSiegeAdvice(AmdMidNoVrr), line => line.Contains("Vulkan", StringComparison.OrdinalIgnoreCase));

        var amdGroups = GameSettings.RainbowSixSiegeGroups(AmdMidNoVrr);
        Assert.DoesNotContain(amdGroups, g => g.Id == "framecap");
        Assert.DoesNotContain(amdGroups.SelectMany(g => g.Changes), c => c.Key == "NvidiaReflex");

        var missingRoot = NewFolder("r6-missing");
        var missingProfile = GameSettings.RainbowSixSiege(NvidiaMidVrr, missingRoot) with
        {
            Id = "r6-missing-" + Guid.NewGuid().ToString("N"),
            Processes = [NoGame],
        };
        Assert.True(GameSettings.TryReadAll(missingProfile, out var emptyFiles));
        Assert.Empty(emptyFiles);

        var guidDir = Path.Combine(missingRoot, "locked-guid");
        Directory.CreateDirectory(guidDir);
        var lockedIni = Path.Combine(guidDir, "GameSettings.ini");
        File.WriteAllBytes(lockedIni, ReadFixture("RainbowSixSiege", "GameSettings.ini"));
        var lockedProfile = GameSettings.RainbowSixSiege(NvidiaMidVrr, missingRoot) with
        {
            Id = "r6-locked-" + Guid.NewGuid().ToString("N"),
            Processes = [NoGame],
        };
        using var hold = new FileStream(lockedIni, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(GameSettings.TryReadAll(lockedProfile, out _));
        Assert.False(GameSettings.Set(lockedProfile, null, true));
    }

    [Fact]
    public void RocketLeagueExtendsMissingResearchItemsAndVrrCap()
    {
        var dir = NewFolder("rl");
        var ini = Path.Combine(dir, "TASystemSettings.ini");
        var original = Encoding.ASCII.GetBytes(
            File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "RocketLeague", "TASystemSettings.ini"))
                .Replace("DynamicShadows=False", "DynamicShadows=True", StringComparison.Ordinal)
                .Replace("bAllowD3D9MSAA=False", "bAllowD3D9MSAA=True", StringComparison.Ordinal)
                .ReplaceLineEndings("\r\n"));
        File.WriteAllBytes(ini, original);

        var profile = GameSettings.RocketLeague(NvidiaMidVrr) with
        {
            Id = "rl-ext-" + Guid.NewGuid().ToString("N"),
            File = ini,
            Processes = [NoGame],
            Nvidia = null,
            Program = null,
        };

        Assert.True(GameSettings.Set(profile, null, true));
        var after = File.ReadAllText(ini);
        Assert.Contains("DynamicShadows=False\r\n", after, StringComparison.Ordinal);
        Assert.Contains("bAllowD3D9MSAA=False\r\n", after, StringComparison.Ordinal);
        Assert.Contains("CustomFPS=157\r\n", after, StringComparison.Ordinal);
        Assert.Contains("MaxAnisotropy=16\r\n", after, StringComparison.Ordinal);

        Assert.True(GameSettings.Set(profile, null, false));
        Assert.Equal(original, File.ReadAllBytes(ini));
    }

    [Fact]
    public void ApexLegendsDetectsReadsDecidesWritesPreservesUnknownUndoesAndNeverWritesAutoexec()
    {
        var installDir = NewFolder("apex-install");
        File.WriteAllBytes(Path.Combine(installDir, "r5apex.exe"), []);
        var game = new GameEntry { Id = "steam:1172470", Title = "Apex Legends", Store = StoreKind.Steam, Installed = true, Path = installDir };
        Assert.True(GameSettings.IsApexLegends(game));

        var cfgDir = NewFolder("apex-cfg");
        var videoCfg = Path.Combine(cfgDir, "videoconfig.txt");
        var original = ReadFixture("ApexLegends", "videoconfig.txt");
        File.WriteAllBytes(videoCfg, original);

        var profile = GameSettings.ApexLegends(NvidiaMidVrr, videoCfg) with
        {
            Id = "apex-" + Guid.NewGuid().ToString("N"),
            Processes = [NoGame],
        };

        Assert.True(GameSettings.Set(profile, null, true));
        var after = File.ReadAllText(videoCfg);
        Assert.Contains("\"setting.fullscreen\"\t\t\"0\"", after, StringComparison.Ordinal);
        Assert.Contains("\"setting.nowindowborder\"\t\t\"1\"", after, StringComparison.Ordinal);
        Assert.Contains("\"setting.csm_enabled\"\t\t\"0\"", after, StringComparison.Ordinal);
        Assert.Contains("\"setting.dvs_enable\"\t\t\"0\"", after, StringComparison.Ordinal);
        Assert.Contains("\"setting.stream_memory\"\t\t\"4000000\"", after, StringComparison.Ordinal);
        Assert.Contains("\"setting.nv_reflex\"\t\t\"2\"", after, StringComparison.Ordinal);
        Assert.Contains("\"setting.r_lod_switch_scale\"\t\t\"0.8\"", after, StringComparison.Ordinal);
        Assert.Contains("\"setting.custom_player_preserve\"\t\t\"untouched_value\"", after, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(installDir, "cfg", "autoexec.cfg")));

        Assert.True(GameSettings.Set(profile, null, false));
        Assert.Equal(original, File.ReadAllBytes(videoCfg));

        Assert.Equal("8000000", GameSettings.ApexStreamMemoryKb(16));
        Assert.Equal("2000000", GameSettings.ApexStreamMemoryKb(4));
        Assert.Contains(GameSettings.ApexLegendsGroups(NvidiaStrongX3dVrr).SelectMany(g => g.Changes),
            c => c.Key == "setting.r_lod_switch_scale" && c.Value == "1.0");
        Assert.DoesNotContain(GameSettings.ApexLegendsGroups(AmdMidNoVrr).SelectMany(g => g.Changes),
            c => c.Key == "setting.nv_reflex");
        Assert.Contains(GameSettings.ApexLegendsAdvice(NvidiaMidVrr, "+exec autoexec"), line => line.Contains("autoexec.cfg", StringComparison.Ordinal));
        Assert.Contains(GameSettings.ApexLegendsAdvice(NvidiaMidVrr), line => line.Contains("+fps_max 157", StringComparison.Ordinal));
    }

    [Fact]
    public void ApexLegendsHandlesMissingAndLockedFiles()
    {
        var cfgDir = NewFolder("apex-lock");
        var missingPath = Path.Combine(cfgDir, "videoconfig.txt");
        var profile = GameSettings.ApexLegends(NvidiaMidVrr, missingPath) with
        {
            Id = "apex-missing-" + Guid.NewGuid().ToString("N"),
            Processes = [NoGame],
        };
        Assert.True(GameSettings.TryReadAll(profile, out var empty));
        Assert.Empty(empty);

        File.WriteAllBytes(missingPath, ReadFixture("ApexLegends", "videoconfig.txt"));
        using var hold = new FileStream(missingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(GameSettings.TryReadAll(profile, out _));
        Assert.False(GameSettings.Set(profile, null, true));
    }

    [Fact]
    public void Overwatch2DetectsReadsDecidesWritesPreservesQuotesUnknownAndUndoes()
    {
        var installDir = NewFolder("ow2-install");
        Directory.CreateDirectory(Path.Combine(installDir, "_retail_"));
        File.WriteAllBytes(Path.Combine(installDir, "_retail_", "Overwatch.exe"), []);
        var game = new GameEntry { Id = "battlenet:pro", Title = "Overwatch 2", Store = StoreKind.BattleNet, Installed = true, Path = installDir };
        Assert.True(GameSettings.IsOverwatch2(game));

        var cfgDir = NewFolder("ow2-cfg");
        var iniPath = Path.Combine(cfgDir, "Settings_v0.ini");
        var original = ReadFixture("Overwatch2", "Settings_v0.ini");
        File.WriteAllBytes(iniPath, original);

        var profile = GameSettings.Overwatch2(NvidiaMidVrr, iniPath) with
        {
            Id = "ow2-" + Guid.NewGuid().ToString("N"),
            Processes = [NoGame],
        };

        Assert.True(GameSettings.Set(profile, null, true));
        var after = File.ReadAllText(iniPath);
        Assert.Contains("WindowMode = \"2\"", after, StringComparison.Ordinal);
        Assert.Contains("ReflexMode = \"2\"", after, StringComparison.Ordinal);
        Assert.Contains("ReducedBuffering = \"1\"", after, StringComparison.Ordinal);
        Assert.Contains("DynamicRenderScale = \"0\"", after, StringComparison.Ordinal);
        Assert.Contains("FrameRateCap = \"157\"", after, StringComparison.Ordinal);
        Assert.Contains("LocalReflections = \"0\"", after, StringComparison.Ordinal);
        Assert.Contains("LocalFogDetail = \"1\"", after, StringComparison.Ordinal);
        Assert.Contains("CustomUserSetting = \"preserve-me\"", after, StringComparison.Ordinal);
        Assert.Contains("MasterVolume = \"80.000000\"", after, StringComparison.Ordinal);

        Assert.True(GameSettings.Set(profile, null, false));
        Assert.Equal(original, File.ReadAllBytes(iniPath));

        var amdGroups = GameSettings.Overwatch2Groups(AmdMidNoVrr);
        Assert.DoesNotContain(amdGroups.SelectMany(g => g.Changes), c => c.Key == "ReflexMode");
        Assert.DoesNotContain(amdGroups, g => g.Id == "framecap");
        Assert.Contains(amdGroups.Single(g => g.Id == "latency").Changes, c => c.Key == "ReducedBuffering" && c.Value == "1");
    }

    [Fact]
    public void Overwatch2HandlesMissingAndLockedFiles()
    {
        var dir = NewFolder("ow2-lock");
        var path = Path.Combine(dir, "Settings_v0.ini");
        var profile = GameSettings.Overwatch2(NvidiaMidVrr, path) with
        {
            Id = "ow2-lock-" + Guid.NewGuid().ToString("N"),
            Processes = [NoGame],
        };
        Assert.True(GameSettings.TryReadAll(profile, out var empty));
        Assert.Empty(empty);

        File.WriteAllBytes(path, ReadFixture("Overwatch2", "Settings_v0.ini"));
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(GameSettings.TryReadAll(profile, out _));
        Assert.False(GameSettings.Set(profile, null, true));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(4, 2)]
    [InlineData(6, 3)]
    [InlineData(8, 4)]
    [InlineData(16, 4)]
    [InlineData(32, 6)]
    [InlineData(64, 6)]
    public void MinecraftHeapNeverExceedsHalfRamAndAtLeastTwoGb(int ramGb, int expectedHeap)
    {
        var heap = GameSettings.ComputeMinecraftHeapGb(ramGb);
        Assert.Equal(expectedHeap, heap);
        Assert.True(heap >= 2);
        if (ramGb >= 4) Assert.True(heap <= ramGb / 2);
        var args = GameSettings.BuildMinecraftJvmArgs(ramGb);
        Assert.Contains($"-Xms{expectedHeap}G -Xmx{expectedHeap}G", args, StringComparison.Ordinal);
        Assert.Contains("-XX:+UseG1GC", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MinecraftDetectsAllFourLaunchersExcludesServersTunesOptionsAndJvmWithUndo()
    {
        var root = NewFolder("mc-all");

        var vanillaDir = Path.Combine(root, "vanilla");
        Directory.CreateDirectory(vanillaDir);
        File.WriteAllBytes(Path.Combine(vanillaDir, "options.txt"), ReadFixture("Minecraft", "options.txt", "\n"));
        File.WriteAllText(Path.Combine(vanillaDir, "launcher_profiles.json"), """
            {
              "profiles": {
                "fabric-main": {
                  "name": "Fabric 1.21.4",
                  "lastVersionId": "fabric-loader-0.16.9-1.21.4",
                  "javaArgs": "-Xmx2G"
                },
                "vanilla-main": {
                  "name": "Vanilla 1.21.4",
                  "lastVersionId": "1.21.4",
                  "javaArgs": "-Xmx2G"
                }
              }
            }
            """);

        var prismRoot = Path.Combine(root, "prism");
        var prismInst = Path.Combine(prismRoot, "FastFabric");
        Directory.CreateDirectory(Path.Combine(prismInst, ".minecraft"));
        File.WriteAllBytes(Path.Combine(prismInst, ".minecraft", "options.txt"), ReadFixture("Minecraft", "options.txt", "\n"));
        File.WriteAllText(Path.Combine(prismInst, "instance.cfg"), "name=Prism Fast\nOverrideMemory=false\nMaxMemAlloc=2048\n");
        File.WriteAllText(Path.Combine(prismInst, "mmc-pack.json"), """
            {
              "components": [
                { "uid": "net.minecraft", "version": "1.21.1" },
                { "uid": "net.fabricmc.fabric-loader", "version": "0.16.5" }
              ]
            }
            """);

        var cfRoot = Path.Combine(root, "curseforge");
        var cfInst = Path.Combine(cfRoot, "ForgePack");
        Directory.CreateDirectory(cfInst);
        File.WriteAllBytes(Path.Combine(cfInst, "options.txt"), ReadFixture("Minecraft", "options.txt", "\n"));
        File.WriteAllText(Path.Combine(cfInst, "minecraftinstance.json"), """
            {
              "name": "CF Forge Pack",
              "gameVersion": "1.20.1",
              "baseModLoader": { "name": "forge-47.3.0", "type": 1, "minecraftVersion": "1.20.1" },
              "allocatedMemory": 4096
            }
            """);

        var mrRoot = Path.Combine(root, "modrinth");
        var mrInst = Path.Combine(mrRoot, "MrFabric");
        Directory.CreateDirectory(mrInst);
        File.WriteAllBytes(Path.Combine(mrInst, "options.txt"), ReadFixture("Minecraft", "options.txt", "\n"));
        File.WriteAllText(Path.Combine(mrInst, "profile.json"), """
            {
              "name": "Modrinth Fabric",
              "game_version": "1.21.4",
              "loader": "fabric"
            }
            """);

        var serverDir = Path.Combine(mrRoot, "DedicatedServer");
        Directory.CreateDirectory(serverDir);
        File.WriteAllText(Path.Combine(serverDir, "server.properties"), "server-port=25565\n");
        File.WriteAllText(Path.Combine(serverDir, "profile.json"), """{"name":"Server","game_version":"1.21.4","loader":"fabric"}""");

        var roots = new GameSettings.MinecraftRoots(vanillaDir, prismRoot, cfRoot, mrRoot);
        var found = GameSettings.DiscoverMinecraftInstances(roots);
        Assert.Equal(5, found.Count);
        Assert.Contains(found, i => i.Launcher == "Vanilla" && i.IsFabric && i.GameVersion == "1.21.4");
        Assert.Contains(found, i => i.Launcher == "Vanilla" && !i.IsFabric && i.Loader == "vanilla");
        Assert.Contains(found, i => i.Launcher == "Prism" && i.IsFabric && i.GameVersion == "1.21.1");
        Assert.Contains(found, i => i.Launcher == "CurseForge" && !i.IsFabric && i.Loader == "forge");
        Assert.Contains(found, i => i.Launcher == "Modrinth" && i.IsFabric && i.GameVersion == "1.21.4");
        Assert.DoesNotContain(found, i => i.Name == "Server");

        var originalOptions = File.ReadAllBytes(Path.Combine(vanillaDir, "options.txt"));
        var profile = GameSettings.MinecraftJava(NvidiaMidVrr, roots) with
        {
            Id = "mc-" + Guid.NewGuid().ToString("N"),
            Processes = [NoGame],
        };
        Assert.True(GameSettings.Set(profile, null, true));
        var afterOptions = File.ReadAllText(Path.Combine(vanillaDir, "options.txt"));
        Assert.Contains("enableVsync:false\n", afterOptions, StringComparison.Ordinal);
        Assert.Contains("maxFps:157\n", afterOptions, StringComparison.Ordinal);
        Assert.Contains("renderClouds:\"false\"\n", afterOptions, StringComparison.Ordinal);
        Assert.Contains("customUnknownOption:keep_exact\n", afterOptions, StringComparison.Ordinal);
        Assert.True(GameSettings.Set(profile, null, false));
        Assert.Equal(originalOptions, File.ReadAllBytes(Path.Combine(vanillaDir, "options.txt")));

        GameSettings.BusySeam.Value = _ => false;
        try
        {
            foreach (var inst in new[]
            {
                found.First(i => i.Launcher == "Prism"),
                found.First(i => i.Launcher == "CurseForge"),
                found.First(i => i.Launcher == "Modrinth"),
                found.First(i => i.Launcher == "Vanilla" && i.IsFabric),
            })
            {
                var origJvm = File.ReadAllBytes(inst.JvmConfigPath!);
                Assert.True(GameSettings.ApplyMinecraftJvm(inst, 16));
                Assert.True(GameSettings.IsMinecraftJvmApplied(inst.Id));
                Assert.Contains("-XX:+UseG1GC", File.ReadAllText(inst.JvmConfigPath!), StringComparison.Ordinal);
                Assert.True(GameSettings.UndoMinecraftJvm(inst));
                Assert.False(GameSettings.IsMinecraftJvmApplied(inst.Id));
                Assert.Equal(origJvm, File.ReadAllBytes(inst.JvmConfigPath!));
            }
        }
        finally
        {
            GameSettings.BusySeam.Value = null;
        }
    }

    [Fact]
    public async Task MinecraftPerformanceModsOnlyInstallOnFabricVerifyHashAndUndoCleanly()
    {
        var root = NewFolder("mc-mods");
        var fabricDir = Path.Combine(root, "fabric-inst");
        var forgeDir = Path.Combine(root, "forge-inst");
        Directory.CreateDirectory(fabricDir);
        Directory.CreateDirectory(forgeDir);

        var fabric = new GameSettings.MinecraftInstance(
            "prism:fabric-" + Guid.NewGuid().ToString("N"),
            "Fabric 1.21.4",
            "Prism",
            fabricDir,
            Path.Combine(fabricDir, "options.txt"),
            "1.21.4",
            "fabric",
            true,
            Path.Combine(fabricDir, "mods"),
            null,
            null);

        var forge = new GameSettings.MinecraftInstance(
            "curseforge:forge-" + Guid.NewGuid().ToString("N"),
            "Forge 1.21.4",
            "CurseForge",
            forgeDir,
            Path.Combine(forgeDir, "options.txt"),
            "1.21.4",
            "forge",
            false,
            Path.Combine(forgeDir, "mods"),
            null,
            null);

        var ct = TestContext.Current.CancellationToken;
        GameSettings.BusySeam.Value = _ => false;
        try
        {
            var refused = await GameSettings.InstallMinecraftPerformanceModsAsync(forge, new FakeModrinthHandler(badHash: false), ct);
            Assert.False(refused.Ok);
            Assert.False(Directory.Exists(forge.ModsDir));

            var badHash = await GameSettings.InstallMinecraftPerformanceModsAsync(fabric, new FakeModrinthHandler(badHash: true), ct);
            Assert.False(badHash.Ok);
            Assert.Empty(Directory.GetFiles(fabric.ModsDir!));

            var ok = await GameSettings.InstallMinecraftPerformanceModsAsync(fabric, new FakeModrinthHandler(badHash: false), ct);
            Assert.True(ok.Ok);
            Assert.Equal(["sodium", "lithium", "ferrite-core"], ok.Installed);
            Assert.Equal(3, Directory.GetFiles(fabric.ModsDir!, "*.jar").Length);
            Assert.Equal(3, GameSettings.InstalledMinecraftMods(fabric.Id).Count);

            var undone = GameSettings.UndoMinecraftPerformanceMods(fabric.Id);
            Assert.True(undone.Ok);
            Assert.Equal(3, undone.Removed.Count);
            Assert.Empty(Directory.GetFiles(fabric.ModsDir!, "*.jar"));
            Assert.Empty(GameSettings.InstalledMinecraftMods(fabric.Id));
        }
        finally
        {
            GameSettings.BusySeam.Value = null;
        }
    }

    private sealed class FakeModrinthHandler(bool badHash) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host.Equals("api.modrinth.com", StringComparison.OrdinalIgnoreCase))
            {
                var slug = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[2];
                var jarBytes = Encoding.UTF8.GetBytes($"fake-jar-bytes-for-{slug}");
                var hash = badHash
                    ? new string('0', 128)
                    : Convert.ToHexString(SHA512.HashData(jarBytes)).ToLowerInvariant();
                var payload = $$"""
                    [
                      {
                        "id": "ver-{{slug}}",
                        "version_type": "release",
                        "loaders": ["fabric"],
                        "game_versions": ["1.21.4"],
                        "files": [
                          {
                            "primary": true,
                            "filename": "{{slug}}-1.0.0+mc1.21.4.jar",
                            "url": "https://cdn.modrinth.com/data/{{slug}}/versions/ver-{{slug}}/{{slug}}-1.0.0+mc1.21.4.jar",
                            "hashes": { "sha512": "{{hash}}" }
                          }
                        ]
                      }
                    ]
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                });
            }
            if (uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
            {
                var slug = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"fake-jar-bytes-for-{slug}")),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

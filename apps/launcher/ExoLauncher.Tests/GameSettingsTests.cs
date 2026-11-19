using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways best game settings can go wrong:
/// - writing changes anything in the game's file beyond the values Exo sets (its comments, ids, spacing, line breaks or BOM);
/// - a value the line doesn't list is written (a choice an update dropped, a number out of range, a misspelled choice);
/// - one of a setting's duplicate lines is missed, so the game keeps the other;
/// - turning a group off loses what the player had, or undoes a change they made in the game since;
/// - a group already set that way in the game looks like Exo's, or one Exo set looks like the player's;
/// - the render threads don't follow the processor (a hybrid's E-cores counted, a big chip given every core).
/// </summary>
public sealed class GameSettingsTests
{
    // As Modern Warfare 4 writes it (s.1.0.cod26.txt), cut down.
    private const string Mw4 = """
        1


        //
        // Display

        // Synchronizes framerate with refresh rate to prevent screen tearing issues
        VSync@0;9203;8071 = 100% // one of disabled, 100%, 50%, 33%, 25%

        NvidiaReflex@0;7717;12207 = Disabled // one of Disabled, Enabled, Enabled + boost

        // Ray tracing
        DxrMode@0;5126;8722 = On // one of Off, On

        DxrMode@0;29061;5591 = High // one of Off, Low, Medium, High, Ultra

        MotionBlur@0;11212;9876 = On // one of Off, On, Cinematic Only

        WeaponMotionBlur@0;23023;1234 = true

        RendererWorkerCount@0;3329;17262 = 16 // -1 to 16

        VideoMemoryScaleMP@0;4519;31987 = 0.750000 // 0.000000 to 2.000000

        ESSDI@0;1;1 =

        """;

    private static GameSettings.SettingsFile File(string text, string newLine = "\r\n", bool bom = false)
    {
        var body = Encoding.UTF8.GetBytes(text.ReplaceLineEndings(newLine));
        return GameSettings.Parse(bom ? [0xEF, 0xBB, 0xBF, .. body] : body);
    }

    private static GameSettings.Line Line(GameSettings.SettingsFile file, string name, int nth = 0) => file.Settings.Where(l => l.Name == name).ElementAt(nth);

    [Theory]
    [InlineData("\r\n", false)]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    public void WritingChangesOnlyTheValues(string newLine, bool bom)
    {
        var body = Encoding.UTF8.GetBytes(Mw4.ReplaceLineEndings(newLine));
        byte[] bytes = bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
        var file = GameSettings.Parse(bytes);
        Assert.Equal(bytes, GameSettings.Write(file, new Dictionary<int, string>()));

        var vsync = Line(file, "VSync");
        var written = GameSettings.Write(file, new Dictionary<int, string> { [vsync.Index] = "disabled" });
        var expected = Encoding.UTF8.GetBytes(Mw4.ReplaceLineEndings(newLine).Replace("VSync@0;9203;8071 = 100% //", "VSync@0;9203;8071 = disabled //", StringComparison.Ordinal));
        Assert.Equal(bom ? [0xEF, 0xBB, 0xBF, .. expected] : expected, written);
    }

    [Fact]
    public void LinesAreReadWithTheirIdsAndAllowedValues()
    {
        var file = File(Mw4);
        Assert.Equal(9, file.Settings.Count);
        var vsync = Line(file, "VSync");
        Assert.Equal("VSync@0;9203;8071", vsync.Id);
        Assert.Equal("100%", vsync.Value);
        Assert.Equal("one of disabled, 100%, 50%, 33%, 25%", vsync.Allowed);
        Assert.Null(Line(file, "WeaponMotionBlur").Allowed);
        Assert.Equal("", Line(file, "ESSDI").Value);
        Assert.Equal(2, file.Settings.Count(l => l.Name == "DxrMode"));
    }

    [Fact]
    public void OnlyValuesTheLineListsAreWritten()
    {
        var file = File(Mw4);
        Assert.Equal("Enabled + boost", GameSettings.Fit(Line(file, "NvidiaReflex"), "enabled + BOOST"));
        Assert.Null(GameSettings.Fit(Line(file, "NvidiaReflex"), "On"));
        Assert.Equal("7", GameSettings.Fit(Line(file, "RendererWorkerCount"), "7"));
        Assert.Null(GameSettings.Fit(Line(file, "RendererWorkerCount"), "17"));
        Assert.Null(GameSettings.Fit(Line(file, "RendererWorkerCount"), "lots"));
        Assert.Equal("0.850000", GameSettings.Fit(Line(file, "VideoMemoryScaleMP"), "0.85"));
        Assert.Equal("false", GameSettings.Fit(Line(file, "WeaponMotionBlur"), "false"));
        Assert.Null(GameSettings.Fit(Line(file, "WeaponMotionBlur"), "Off"));
        Assert.Null(GameSettings.Fit(Line(file, "ESSDI"), "x"));
    }

    [Fact]
    public void EveryDuplicateLineIsChangedAndOneThatCantTakeItIsLeftOut()
    {
        var file = File(Mw4);
        var group = new GameSettings.Group("effects", "Effects", [new("DxrMode", "Off"), new("MotionBlur", "Cinematic"), new("Nowhere", "1")]);
        var targets = GameSettings.Targets(file, group);
        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal("DxrMode", t.Line.Name));
    }

    [Fact]
    public void OffPutsBackWhatItHeldButNotWhatChangedInTheGameSince()
    {
        var group = new GameSettings.Group("latency", "Latency", [new("VSync", "disabled"), new("NvidiaReflex", "Enabled"), new("MotionBlur", "Off")]);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var file = File(Mw4);

        var on = GameSettings.Plan(file, group, true, kept);
        Assert.Equal(3, on.Count);
        file = GameSettings.Parse(GameSettings.Write(file, on));
        Assert.True(GameSettings.States(new("t", "T", "", _ => [group], []), file, kept).Single() is { On: true, Exo: true });

        // In the game, Reflex is changed to boost since.
        file = GameSettings.Parse(GameSettings.Write(file, new Dictionary<int, string> { [Line(file, "NvidiaReflex").Index] = "Enabled + boost" }));
        var off = GameSettings.Plan(file, group, false, kept);
        file = GameSettings.Parse(GameSettings.Write(file, off));
        Assert.Equal("100%", Line(file, "VSync").Value);
        Assert.Equal("On", Line(file, "MotionBlur").Value);
        Assert.Equal("Enabled + boost", Line(file, "NvidiaReflex").Value);
        Assert.Empty(kept);
    }

    [Fact]
    public void AGroupAlreadySetInTheGameIsOnButNotExos()
    {
        var group = new GameSettings.Group("vsync", "V-Sync", [new("VSync", "100%")]);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var file = File(Mw4);
        Assert.Empty(GameSettings.Plan(file, group, true, kept));
        Assert.True(GameSettings.States(new("t", "T", "", _ => [group], []), file, kept).Single() is { On: true, Exo: false });
        // Off leaves the player's own value.
        Assert.Empty(GameSettings.Plan(file, group, false, kept));
    }

    [Fact]
    public void AGroupWithNothingInThisVersionIsLeftOut()
    {
        var group = new GameSettings.Group("gone", "Gone", [new("Nowhere", "1")]);
        Assert.Empty(GameSettings.States(new("t", "T", "", _ => [group], []), File(Mw4), new Dictionary<string, GameSettings.Remembered>()));
    }

    /// <summary>Fast cores (two threads each) then slow ones; ccds L3s of the given sizes, splitting the fast cores evenly.</summary>
    private static MouseCore.Topology Cpu(int fast, int slow = 0, params long[] ccds)
    {
        MouseCore.Core[] cores =
        [.. Enumerable.Range(0, fast).Select(i => new MouseCore.Core([i * 2, i * 2 + 1], 1)),
         .. Enumerable.Range(0, slow).Select(i => new MouseCore.Core([fast * 2 + i], 0))];
        var per = ccds.Length == 0 ? fast : fast / ccds.Length;
        var l3 = ccds.Select((bytes, c) => new MouseCore.Cache(Enumerable.Range(c * per * 2, per * 2).Aggregate(0UL, (mask, cpu) => mask | 1UL << cpu), bytes)).ToArray();
        return new MouseCore.Topology(cores, l3, fast * 2 + slow);
    }

    [Theory]
    [InlineData(8, 16, 7)]  // i9 hybrid: its P-cores less one
    [InlineData(6, 8, 5)]   // i5 hybrid
    [InlineData(8, 0, 7)]   // Ryzen 7: its cores less one
    [InlineData(6, 0, 5)]
    [InlineData(4, 0, 3)]
    [InlineData(2, 0, 2)]   // never fewer than two
    public void RenderThreadsFollowTheFastCores(int fast, int slow, int expected) =>
        Assert.Equal(expected, GameSettings.RenderThreads(Cpu(fast, slow)));

    [Fact]
    public void ARyzenWithTwoCcdsCountsOne()
    {
        Assert.Equal(7, GameSettings.RenderThreads(Cpu(16, 0, 32L << 20, 32L << 20)));  // 9950X
        Assert.Equal(7, GameSettings.RenderThreads(Cpu(16, 0, 32L << 20, 96L << 20)));  // 9950X3D: the V-Cache CCD
        Assert.Equal(5, GameSettings.RenderThreads(Cpu(12, 0, 32L << 20, 32L << 20)));  // 9900X
    }

    private static GameSettings.SettingsFile Mw4File => File(Mw4);

    [Fact]
    public void NoCoresMeansNoRenderThreadsGroup()
    {
        Assert.Null(GameSettings.RenderThreads(new MouseCore.Topology([], [], 0)));
        Assert.DoesNotContain(GameSettings.CallOfDutyGroups(new("nvidia", null, 8), Mw4File), g => g.Id == "threads");
    }

    [Theory]
    [InlineData("nvidia", "NvidiaReflex")]
    [InlineData("amd", "AmdAntilag2")]
    public void LatencyUsesTheCardMakersOwnSwitch(string vendor, string key)
    {
        var latency = GameSettings.CallOfDutyGroups(new(vendor, 7, 8), Mw4File).Single(g => g.Id == "latency");
        Assert.Contains(latency.Changes, c => c.Key == key);
        Assert.Contains(latency.Changes, c => c.Key == "VSync" && c.Value == "disabled");
    }

    [Fact]
    public void IntelLeavesXeLLAsTheGameWritesIt()
    {
        // Every MW4 file holds XeLL twice, the second already on whatever the card.
        var latency = GameSettings.CallOfDutyGroups(new("intel", 7, 8), Mw4File).Single(g => g.Id == "latency");
        Assert.DoesNotContain(latency.Changes, c => c.Key == "IntelXeLL");
    }

    [Theory]
    // CNN only where it's much cheaper and looks close: an RTX 20 or 30 at 1440p and 4K. At 1080p it upscales from 720p and smears.
    [InlineData("NVIDIA GeForce RTX 3070", "Auto:1920x1080", "DLSS", "TRANSFORMER", "Maximum Quality")]
    [InlineData("NVIDIA GeForce RTX 3070", "Auto:2560x1440", "DLSS", "CNN", "Balanced")]
    [InlineData("NVIDIA GeForce RTX 2060 SUPER", "3840x2160", "DLSS", "CNN", "Maximum Performance")]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", "Auto:2560x1440", "DLSS", "TRANSFORMER", "Balanced")]
    [InlineData("NVIDIA GeForce RTX 5090", "Auto:3840x2160", "DLSS", "TRANSFORMER", "Maximum Performance")]
    [InlineData("AMD Radeon RX 7800 XT", "Auto:2560x1440", "FSR AA", null, "Balanced")]
    [InlineData("AMD Radeon RX 9070 XT", "Auto:1920x1080", "FSR AA", null, "Maximum Quality")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "Auto:1920x1080", "XeSS", null, "Maximum Quality")]
    public void TheUpscalerIsTheCardsOwn(string gpu, string resolution, string technique, string? model, string preset)
    {
        var group = GameSettings.Upscaler(gpu, resolution);
        Assert.NotNull(group);
        Assert.Contains(group.Changes, c => c.Key == "AATechniquePreferredMP" && c.Value == technique);
        if (model is not null) Assert.Contains(group.Changes, c => c.Key == "DLSSModelUI" && c.Value == model);
        Assert.Contains(group.Changes, c => c.Value == preset && c.Key is "DLSSPerfModeMP" or "AMDSuperResolution2Quality" or "XeSSQuality");
    }

    // MW4's own NVIDIA profile: only where Optimize picks CNN does the game's profile lift the global transformer preset.
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070", "2560x1440", true)]
    [InlineData("NVIDIA GeForce RTX 3070", "1920x1080", false)]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", "3840x2160", false)]
    [InlineData("AMD Radeon RX 7800 XT", "2560x1440", false)]
    public void Mw4sOwnNvidiaProfileLiftsTheOverrideOnlyForCnn(string gpu, string resolution, bool lifts)
    {
        var file = File(Mw4 + $"GPUName@0;1;1 = {gpu}\nResolution@0;4;4 = {resolution}\n");
        var values = GameSettings.ModernWarfare4Nvidia().Values(file);
        if (!lifts) { Assert.Empty(values); return; }
        var lift = Assert.Single(values);
        Assert.Equal((0x10E41DF3u, (uint?)0u), (lift.Id, lift.Value));
    }

    [Theory]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER")]
    [InlineData("AMD Radeon RX 6700 XT")]
    [InlineData("")]
    public void AnOlderCardKeepsItsOwnUpscaler(string gpu) => Assert.Null(GameSettings.Upscaler(gpu, "Auto:1920x1080"));

    [Theory]
    [InlineData(4, "3", "0.7")]
    [InlineData(8, "2", "0.7")]
    [InlineData(12, "1", "0.8")]
    [InlineData(24, "1", "0.8")]
    public void TexturesAndMemoryFollowTheCardsMemory(double gb, string textures, string scale)
    {
        var group = GameSettings.Memory(gb);
        Assert.Contains(group.Changes, c => c.Key == "TextureQuality" && c.Value == textures);
        Assert.Contains(group.Changes, c => c.Key == "VideoMemoryScaleMP" && c.Value == scale);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", true)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", true)]
    [InlineData("NVIDIA GeForce RTX 3080", true)]
    [InlineData("AMD Radeon RX 7900 XTX", true)]
    [InlineData("AMD Radeon RX 9070 XT", true)]
    [InlineData("NVIDIA GeForce RTX 3070", false)]
    [InlineData("NVIDIA GeForce RTX 4060 Ti", false)]
    [InlineData("AMD Radeon RX 7600", false)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", false)]
    public void AStrongCardIsTold(string gpu, bool strong) => Assert.Equal(strong, GameSettings.Strong(gpu));

    /// <summary>The lines his list names, each as the game writes it (its own ids and allowed values), held at their lowest.</summary>
    private const string Mw4Effects = """
        GPUName@0;1;1 = NVIDIA GeForce RTX 3070
        DxrMode@0;37334;7764 = On // one of Off, On
        DxrMode@1;8707;7764 = Low // one of Off, Low, Medium, High, Ultra
        SSRQuality@0;63075;11445 = Off // one of Off, Low, Medium, High
        SSRQuality@1;63075;43096 = Off // one of Off, Low, Medium, High
        ScreenSpaceShadowQuality@0;42742;43096 = Off // one of Off, Low, High
        AmbientLightingQuality@0;20439;58629 = Off // one of Off, Low, Medium, High, Ultra
        ShadowQuality@0;3981;58629 = Very_Low // one of Very_Low, Low, Medium, High, Very_High
        ShadowFilteringQuality@0;43900;43096 = Low // one of Low, Medium, High
        ShadingQuality@0;14472;11445 = Low // one of Low, High
        ShaderQuality@0;19450;7764 = Default // one of Default, Medium, Low
        ModelQuality@0;15002;43096 = Low Quality // one of Low Quality, Medium Quality, High Quality
        TerrainQuality@0;36295;58629 = Very Low // one of Very Low, Low, Medium, High, Very High
        ParticleQuality@0;60198;58629 = high // one of very low, low, medium, high
        VolumetricQuality@0;4276;43096 = QUALITY_HIGH // one of QUALITY_LOW, QUALITY_MEDIUM, QUALITY_HIGH, QUALITY_VERYHIGH
        VolumetricQuality@1;4276;58629 = QUALITY_HIGH // one of QUALITY_LOW, QUALITY_MEDIUM, QUALITY_HIGH, QUALITY_VERYHIGH
        WeatherGridVolumesQuality@0;38459;58629 = Off // one of Off, Low, Medium, High, Ultra
        WaterCausticsMode@0;41499;11445 = Off // one of Off, Low Quality, High Quality
        WaterWaveWetness@0;57752;20945 = false
        DeferredPhysics@0;6619;43096 = High Quality // one of Low Quality, Medium Quality, High Quality
        ReflectionProbeHalfResolution@0;57752;48403 = true
        WorldStreamingQuality@0;6770;11445 = Low // one of Low, High
        CinematicQuality@0;57752;48403 = false
        BulletImpacts@0;57752;20945 = false
        PersistentDamageLayer@0;57752;20945 = false
        Tessellation@0;27349;11445 = 1_Near // one of 0_Off, 1_Near, 2_All
        """;

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070")]
    [InlineData("NVIDIA GeForce RTX 4090")]
    public void Mw4IsBenchmarKingsCompetitiveListOnEveryCard(string gpu)
    {
        var file = File(Mw4Effects.ReplaceLineEndings("\n").Replace("NVIDIA GeForce RTX 3070", gpu, StringComparison.Ordinal));
        var effects = GameSettings.CallOfDutyGroups(new("nvidia", 7, 12), file).Single(g => g.Id == "effects");
        var targets = GameSettings.Targets(file, effects).ToDictionary(t => t.Line.Id, t => t.Value);
        string Of(string id) => targets[id];
        Assert.Equal("Off", Of("DxrMode@0;37334;7764"));
        Assert.Equal("Off", Of("DxrMode@1;8707;7764"));
        Assert.Equal("Low", Of("SSRQuality@0;63075;11445"));
        Assert.Equal("Low", Of("SSRQuality@1;63075;43096"));
        Assert.Equal("High", Of("ScreenSpaceShadowQuality@0;42742;43096"));
        Assert.Equal("Ultra", Of("AmbientLightingQuality@0;20439;58629"));
        Assert.Equal("Low", Of("ShadowQuality@0;3981;58629"));
        Assert.Equal("Medium", Of("ShadowFilteringQuality@0;43900;43096"));
        Assert.Equal("High", Of("ShadingQuality@0;14472;11445"));
        Assert.Equal("Low", Of("ShaderQuality@0;19450;7764"));
        Assert.Equal("Medium Quality", Of("ModelQuality@0;15002;43096"));
        Assert.Equal("Medium", Of("TerrainQuality@0;36295;58629"));
        Assert.Equal("very low", Of("ParticleQuality@0;60198;58629"));
        Assert.Equal("QUALITY_LOW", Of("VolumetricQuality@0;4276;43096"));
        Assert.Equal("Ultra", Of("WeatherGridVolumesQuality@0;38459;58629"));
        Assert.Equal("High Quality", Of("WaterCausticsMode@0;41499;11445"));
        Assert.Equal("true", Of("WaterWaveWetness@0;57752;20945"));
        Assert.Equal("Low Quality", Of("DeferredPhysics@0;6619;43096"));
        Assert.Equal("false", Of("ReflectionProbeHalfResolution@0;57752;48403"));
        Assert.Equal("High", Of("WorldStreamingQuality@0;6770;11445"));
        Assert.Equal("true", Of("CinematicQuality@0;57752;48403"));
        Assert.Equal("true", Of("BulletImpacts@0;57752;20945"));
        Assert.Equal("true", Of("PersistentDamageLayer@0;57752;20945"));
        // Tessellation goes with his Terrain Quality, not on its own line.
        Assert.DoesNotContain("Tessellation@0;27349;11445", targets.Keys);
    }

    [Fact]
    public void Mw4CapsNoFramesAndClutterIsOnlyBloodAndCasings()
    {
        var file = File(Mw4Effects.ReplaceLineEndings("\n"));
        var groups = GameSettings.CallOfDutyGroups(new("nvidia", 7, 8), file);
        Assert.DoesNotContain(groups.SelectMany(g => g.Changes), c => c.Key is "MaxFpsInGame" or "Tessellation");
        Assert.Equal(["ShowBlood", "BloodLimit", "ShowBrass"], groups.Single(g => g.Id == "clutter").Changes.Select(c => c.Key));
    }

    [Fact]
    public void AnOlderCardKeepsItsRenderResolution()
    {
        // No upscaler Exo sets on a GTX, so its render resolution stays the player's.
        var file = File(Mw4 + "GPUName@0;1;1 = NVIDIA GeForce GTX 1660 SUPER\nResolutionMultiplier@0;3;3 = 85 // 0 to 200\n");
        Assert.DoesNotContain(GameSettings.CallOfDutyGroups(new("nvidia", 5, 6), file).SelectMany(g => g.Changes), c => c.Key == "ResolutionMultiplier");
        Assert.Contains(GameSettings.Upscaler("NVIDIA GeForce RTX 3060", "1920x1080")!.Changes, c => c.Key == "ResolutionMultiplier" && c.Value == "100");
    }

    [Fact]
    public void BorderlessAtTheHighestRefresh()
    {
        var display = GameSettings.Display(new("nvidia", 7, 8), "144.000");
        Assert.Contains(display.Changes, c => c.Key == "DisplayMode" && c.Value == "Fullscreen borderless window");
        Assert.Contains(display.Changes, c => c.Key == "RefreshRate" && c.Value == "Auto:144.000" && c.Free);
        Assert.DoesNotContain(display.Changes, c => c.Key == "ConstrainMouse");
        // Already Auto (the highest): left as the game wrote it.
        Assert.DoesNotContain(GameSettings.Display(new("nvidia", 7, 8), "Auto:164.917").Changes, c => c.Key == "RefreshRate");
        // A second screen keeps the mouse in the game.
        Assert.Contains(GameSettings.Display(new("nvidia", 7, 8, Screens: 2), "Auto:164.917").Changes, c => c.Key == "ConstrainMouse" && c.Value == "true");
    }

    [Fact]
    public void AFreeValueGoesOnlyToALineThatListsNone()
    {
        var file = File(Mw4 + "RefreshRate@0;4;4 = 144.000\nVSync@0;5;5 = 100% // one of disabled, 100%\n");
        var group = new GameSettings.Group("g", "G", [new("RefreshRate", "Auto:144.000", Free: true), new("VSync", "anything", Free: true)]);
        var targets = GameSettings.Targets(file, group);
        Assert.Single(targets);
        Assert.Equal("Auto:144.000", targets[0].Value);
    }

    [Fact]
    public void FootstepsOverMusicAndTheUpscalersSharpening()
    {
        var file = File(Mw4 + "MusicVolume@0;6;6 = 1.000000 // 0.000000 to 1.000000\n");
        var audio = GameSettings.CallOfDutyGroups(new("nvidia", 7, 8), file).Single(g => g.Id == "audio");
        Assert.Equal("0.000000", GameSettings.Targets(file, audio).Single().Value);
        Assert.Contains(GameSettings.Upscaler("NVIDIA GeForce RTX 3070", "1920x1080")!.Changes, c => c.Key == "DLSSSharpnessMP" && c.Value == "0.75");
    }

    [Theory]
    [InlineData("Auto:1920x1080", "120")]
    [InlineData("Auto:2560x1440", "120")]
    [InlineData("Auto:3440x1440", "110")]  // ultrawide: the game already widens it
    [InlineData(null, "120")]
    public void TheWidestViewForTheScreen(string? resolution, string fov) =>
        Assert.Contains(GameSettings.View(resolution).Changes, c => c.Key == "Fov" && c.Value == fov);

    // A player's file as MW4 writes it: names with a space are its own, and are never taken for another.
    private const string Player = """
        1

        // 1st Person Field of View
        Fov@1;65095;45350 = 90.000000 // 60.000000 to 120.000000

        // 1st Person Field of View (Handheld)
        Handheld Fov@0;24892;5108 = 80.000000 // 60.000000 to 120.000000

        MusicVolume@0;54363;21371 = 1.000000 // 0.000000 to 1.000000

        """;

    [Fact]
    public void APlayerFilesLinesAreKeptApartFromTheMainFiles()
    {
        var main = File(Mw4 + "MusicVolume@0;6;6 = 1.000000 // 0.000000 to 1.000000\n");
        var player = File(Player);
        Assert.DoesNotContain(player.Settings, l => l.Name == "Handheld");
        var group = new GameSettings.Group("audio", "Audio", [new("MusicVolume", "0"), new("Fov", "120")]);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var mainEdits = GameSettings.Plan(main, group, true, kept);
        var playerEdits = GameSettings.Plan(player, group, true, kept, "123/g.cod26.1.0.l.txt|");
        Assert.Single(mainEdits);
        Assert.Equal(2, playerEdits.Count);
        Assert.Contains("123/g.cod26.1.0.l.txt|Fov@1;65095;45350", kept.Keys);
        Assert.Equal(3, kept.Count);

        // Applied only once both files hold it.
        GameSettings.OpenFile Open(string key, GameSettings.SettingsFile f) => new("", key, f);
        var profile = new GameSettings.Profile("t", "T", "", _ => [group], []);
        Assert.False(GameSettings.States(profile, [Open("", GameSettings.Parse(GameSettings.Write(main, mainEdits))), Open("123/g.cod26.1.0.l.txt|", player)], kept).Single().On);
        Assert.True(GameSettings.States(profile, [Open("", GameSettings.Parse(GameSettings.Write(main, mainEdits))),
            Open("123/g.cod26.1.0.l.txt|", GameSettings.Parse(GameSettings.Write(player, playerEdits)))], kept).Single() is { On: true, Exo: true });
    }


    [Fact]
    public void RawMouseInputAndYourSensitivityStays()
    {
        var mouse = GameSettings.CallOfDutyGroups(new("nvidia", 7, 8), File(Mw4)).Single(g => g.Id == "mouse");
        Assert.Contains(mouse.Changes, c => c.Key == "MouseAcceleration" && c.Value == "0");
        Assert.Contains(mouse.Changes, c => c.Key == "ADSTimingSensitivity" && c.Value == "interpolated");
        // Sensitivity, ADS multipliers and invert are muscle memory: never part of it.
        Assert.DoesNotContain(GameSettings.CallOfDutyGroups(new("nvidia", 7, 8, Headphones: true), File(Mw4)).SelectMany(g => g.Changes),
            c => c.Key.Contains("Sensibility", StringComparison.Ordinal) || c.Key is "ADSSensitivity" || c.Key.Contains("ZoomSensitivity", StringComparison.Ordinal)
                 || c.Key.Contains("Multiplier", StringComparison.Ordinal) || c.Key.Contains("Invert", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHeadphonesMixOnlyForHeadphones()
    {
        var file = File(Mw4 + "AudioMixPreset@0;7;7 = home_theater\nWindowsSonicEnable@0;8;8 = true\n");
        Assert.DoesNotContain(GameSettings.CallOfDutyGroups(new("nvidia", 7, 8), file), g => g.Id == "headphones");
        var headphones = GameSettings.CallOfDutyGroups(new("nvidia", 7, 8, Headphones: true), file).Single(g => g.Id == "headphones");
        var targets = GameSettings.Targets(file, headphones);
        Assert.Contains(targets, t => t.Line.Name == "AudioMixPreset" && t.Value == "headphones_rex");
        Assert.Contains(targets, t => t.Line.Name == "WindowsSonicEnable" && t.Value == "false");
    }

    private const string Control = """
        {
        	"m_iOutputResolutionY": 1080,
        	"m_eWindowMode": 2,
        	"m_eVSync": 1,
        	"m_eTextureResolution": 3,
        	"m_eTextureFilteringQuality": 0,
        	"m_eSSAAMethod": 0,
        	"m_eMotionBlur": 1,
        	"m_bFilmGrain": true,
        	"m_eGIQuality": 2,
        	"m_eSSRQuality": 3,
        	"m_ePTQuality": 1,
        	"m_eRTReflectionQuality": 1,
        	"m_eFSRFGMode": 1,
        	"m_eDLSSFGMode": 6,
        	"m_bDLSSLegacy": true,
        	"m_DLSSRR": true,
        	"m_maxFrameRate": 360.0,
        	"m_fDLSSFGTargetFrameRate": 360.0
        }
        """;

    private static string ControlWritten(GameSettings.Pc pc)
    {
        var file = GameSettings.ParseControl(Encoding.UTF8.GetBytes(Control.ReplaceLineEndings("\n")));
        var edits = new Dictionary<int, string>();
        foreach (var group in GameSettings.ControlResonant(pc).Groups(file))
            foreach (var (index, value) in GameSettings.Plan(file, group, true, []))
                edits[index] = value;
        return Encoding.UTF8.GetString(GameSettings.Write(file, edits));
    }

    [Fact]
    public void ControlOnAnRtx3070At1080DropsTheHeavyStepsAndStaysAt2x()
    {
        var written = ControlWritten(new("nvidia", null, 8, TopRefresh: 165, Gpu: "NVIDIA GeForce RTX 3070"));
        Assert.Contains("\"m_eWindowMode\": 1", written);
        Assert.Contains("\"m_eVSync\": 1", written);
        Assert.Contains("\"m_maxFrameRate\": 360.0", written);
        Assert.DoesNotContain("m_bUnlockAspectRatio", written);
        Assert.Contains("\"m_fDLSSFGTargetFrameRate\": 165.0", written);
        Assert.Contains("\"m_eSSAAMethod\": 2", written);
        Assert.Contains("\"m_eDLSSFGMode\": 1", written);
        Assert.Contains("\"m_eFSRFGMode\": 0", written);
        Assert.Contains("\"m_eTextureResolution\": 1", written);
        Assert.Contains("\"m_ePTQuality\": 0", written);
        Assert.Contains("\"m_eRTReflectionQuality\": 0", written);
        Assert.Contains("\"m_eGIQuality\": 2", written);
        Assert.Contains("\"m_eSSRQuality\": 1", written);
        Assert.Contains("\"m_eMotionBlur\": 0", written);
        Assert.Contains("\"m_bFilmGrain\": false", written);
        Assert.Contains("\"m_bDLSSLegacy\": false", written);
        Assert.Contains("\"m_DLSSRR\": false", written);
        Assert.Contains("\"m_iOutputResolutionY\": 1080", written);
    }

    [Fact]
    public void ControlOnALargerCardAimsDynamicFrameGenerationAtTheScreen()
    {
        var written = ControlWritten(new("nvidia", null, 16, TopRefresh: 240, Gpu: "NVIDIA GeForce RTX 4080"));
        Assert.Contains("\"m_eDLSSFGMode\": 6", written);
        Assert.Contains("\"m_fDLSSFGTargetFrameRate\": 240.0", written);
        Assert.Contains("\"m_eTextureResolution\": 3", written);
        Assert.Contains("\"m_eGIQuality\": 2", written);
        Assert.Contains("\"m_DLSSRR\": true", written);
        Assert.DoesNotContain("\"m_ePTQuality\": 0", written);
    }

    [Fact]
    public void ControlOnARadeonUsesFsr()
    {
        var written = ControlWritten(new("amd", null, 16, TopRefresh: 144, Gpu: "AMD Radeon RX 7900 XTX"));
        Assert.Contains("\"m_eSSAAMethod\": 3", written);
        Assert.Contains("\"m_eFSRFGMode\": 1", written);
        Assert.Contains("\"m_eDLSSFGMode\": 0", written);
    }

    [Fact]
    public void ControlUnlocksCutscenesOnlyOnAWideScreen()
    {
        var wide = Control.Replace("\"m_iOutputResolutionY\": 1080", "\"m_iOutputResolutionX\": 3440,\n\t\"m_iOutputResolutionY\": 1440,\n\t\"m_bUnlockAspectRatio\": false", StringComparison.Ordinal);
        var settings = GameSettings.ParseControl(Encoding.UTF8.GetBytes(wide.ReplaceLineEndings("\n")));
        var group = GameSettings.ControlResonant(new("nvidia", null, 8, TopRefresh: 144, Gpu: "NVIDIA GeForce RTX 3070")).Groups(settings).Single(g => g.Id == "wide");
        Assert.Equal("true", GameSettings.Targets(settings, group).Single().Value);
    }

    [Fact]
    public void ControlTexturesFollowTheCardAndTheResolution()
    {
        string Texture(double gb, int height)
        {
            var body = Control.Replace("\"m_iOutputResolutionY\": 1080", $"\"m_iOutputResolutionY\": {height}", StringComparison.Ordinal);
            var settings = GameSettings.ParseControl(Encoding.UTF8.GetBytes(body));
            var group = GameSettings.ControlResonant(new("nvidia", null, gb, Gpu: "NVIDIA GeForce RTX 3070")).Groups(settings).Single(g => g.Id == "memory");
            return GameSettings.Targets(settings, group).Single(t => t.Line.Name == "m_eTextureResolution").Value;
        }
        Assert.Equal("0", Texture(4, 1080));
        Assert.Equal("1", Texture(8, 1080));
        Assert.Equal("0", Texture(8, 1440));
        Assert.Equal("1", Texture(12, 1440));
        Assert.Equal("2", Texture(16, 2160));
        Assert.Equal("3", Texture(16, 1440));
    }

    /// <summary>The lines his list names, each held away from his value, with ray tracing on.</summary>
    private const string ControlUltra = """
        {
        	"m_iOutputResolutionY": 1080,
        	"m_eVolumeLightQuality": 2,
        	"m_eVolumetricSpotLightQuality": 2,
        	"m_eShadowResolutionQuality": 2,
        	"m_eShadowFilteringQuality": 0,
        	"m_eShadowDetailQuality": 1,
        	"m_eSSAOQuality": 0,
        	"m_eSSRQuality": 3,
        	"m_eLODDetail": 0,
        	"m_eGIQuality": 0,
        	"m_eSDFQuality": 0,
        	"m_eHighQualityPost": 1,
        	"m_eFogQuality": 1,
        	"m_eRTReSTIRQuality": 1
        }
        """;

    private static Dictionary<string, string> ControlTargets(GameSettings.Pc pc)
    {
        var file = GameSettings.ParseControl(Encoding.UTF8.GetBytes(ControlUltra.ReplaceLineEndings("\n")));
        return GameSettings.ControlResonant(pc).Groups(file).SelectMany(group => GameSettings.Targets(file, group))
            .ToDictionary(t => t.Line.Name, t => t.Value);
    }

    [Fact]
    public void ControlOnEightGigabytesIsBenchmarKingsListAtTheLowerOfHisTwoValues()
    {
        var set = ControlTargets(new("nvidia", null, 8, TopRefresh: 165, Gpu: "NVIDIA GeForce RTX 3070"));
        Assert.Equal("0", set["m_eVolumeLightQuality"]);
        Assert.Equal("1", set["m_eVolumetricSpotLightQuality"]);
        Assert.Equal("1", set["m_eShadowResolutionQuality"]);
        Assert.Equal("1", set["m_eShadowFilteringQuality"]);
        Assert.Equal("2", set["m_eShadowDetailQuality"]);
        Assert.Equal("1", set["m_eSSAOQuality"]);
        Assert.Equal("1", set["m_eSSRQuality"]);
        Assert.Equal("2", set["m_eLODDetail"]);
        Assert.Equal("2", set["m_eGIQuality"]);
        Assert.Equal("2", set["m_eSDFQuality"]);
        Assert.Equal("0", set["m_eHighQualityPost"]);
        Assert.Equal("0", set["m_eFogQuality"]);
        Assert.Equal("0", set["m_eRTReSTIRQuality"]);
    }

    [Fact]
    public void ControlWithRoomTakesTheUpperAndNeverUltraVolumetricsOrReflections()
    {
        var set = ControlTargets(new("nvidia", null, 16, TopRefresh: 240, Gpu: "NVIDIA GeForce RTX 4080"));
        Assert.Equal("1", set["m_eVolumeLightQuality"]);
        Assert.Equal("1", set["m_eVolumetricSpotLightQuality"]);
        Assert.Equal("2", set["m_eShadowResolutionQuality"]);
        Assert.Equal("2", set["m_eSSRQuality"]);
        Assert.Equal("0", set["m_eHighQualityPost"]);
        // Ray tracing stays the player's on a card with room.
        Assert.DoesNotContain("m_eRTReSTIRQuality", set.Keys);
    }

    [Fact]
    public void WutheringWavesIsDetectedAcrossSteamEpicAndLocalAndSkipsUnrelatedUeGames()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-wuwa-detect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var steamWin64 = Path.Combine(root, "steam", "Client", "Binaries", "Win64");
            Directory.CreateDirectory(steamWin64);
            System.IO.File.WriteAllBytes(Path.Combine(steamWin64, "Client-Win64-Shipping.exe"), []);
            System.IO.File.WriteAllBytes(Path.Combine(steamWin64, "Client-Win64-ShippingBase.dll"), []);

            var epicWin64 = Path.Combine(root, "epic", "Wuthering Waves Game", "Client", "Binaries", "Win64");
            Directory.CreateDirectory(epicWin64);
            System.IO.File.WriteAllBytes(Path.Combine(epicWin64, "Client-Win64-Shipping.exe"), []);
            System.IO.File.WriteAllBytes(Path.Combine(epicWin64, "Client-Win64-ShippingBase.dll"), []);

            var otherUeWin64 = Path.Combine(root, "other", "Client", "Binaries", "Win64");
            Directory.CreateDirectory(otherUeWin64);
            System.IO.File.WriteAllBytes(Path.Combine(otherUeWin64, "Client-Win64-Shipping.exe"), []);

            var rtx4070 = new GameSettings.Pc("nvidia", null, 12, Gpu: "NVIDIA GeForce RTX 4070");
            var amd7800 = new GameSettings.Pc("amd", null, 16, Gpu: "AMD Radeon RX 7800 XT");
            var gtx1660 = new GameSettings.Pc("nvidia", null, 6, Gpu: "NVIDIA GeForce GTX 1660 SUPER");

            var steamGame = new GameEntry { Id = "steam:3513350", Title = "Wuthering Waves", Store = StoreKind.Steam, Installed = true, Path = Path.Combine(root, "steam") };
            var epicGame = new GameEntry { Id = "epic:wuwa-app", Title = "Wuthering Waves", Store = StoreKind.Epic, Installed = true, Path = Path.Combine(root, "epic") };
            var localGame = new GameEntry { Id = "local:wuthering-waves", Title = "Wuthering Waves", Store = StoreKind.Local, Installed = true, Path = Path.Combine(root, "epic") };
            var otherGame = new GameEntry { Id = "local:other-ue", Title = "Some Other UE Game", Store = StoreKind.Local, Installed = true, Path = Path.Combine(root, "other") };

            foreach (var entry in new[] { steamGame, epicGame, localGame })
            {
                var profile = GameSettings.For(entry, rtx4070);
                Assert.NotNull(profile);
                Assert.Equal(GameSettings.WutheringWavesId, profile.Id);
                Assert.Equal("Client-Win64-ShippingBase.dll", profile.Nvidia?.FileInFolder);
                Assert.EndsWith("Client-Win64-Shipping.exe", profile.Program?.Invoke(entry));
            }

            Assert.Null(GameSettings.For(otherGame, rtx4070));
            Assert.Null(GameSettings.For(steamGame, amd7800));
            Assert.Null(GameSettings.For(steamGame, gtx1660));
        }
        finally
        {
            try { DeletionGate.DeleteDirectory(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void WutheringWavesNvidiaUsesSignedDlssOverridesAndOptInSmoothMotionOnlyOnRtx40PlusWithoutNativeDlssG()
    {
        // RTX 3070: Transformer SR Preset K (11), RR Preset F (6), FG Preset A (1), overrides + Streamline 0x10E41E06, never Smooth Motion.
        var rtx30 = GameSettings.WutheringWavesNvidiaValues(30, smoothMotionOptIn: true, usingNativeDlssG: false)
            .ToDictionary(v => v.Id, v => v.Value);
        Assert.Equal(2u, rtx30[GpuProfile.ModelPresetProfile]);
        Assert.Equal(1u, rtx30[GraphicsCatalog.SrOverride]);
        Assert.Equal(GpuProfile.PresetK, rtx30[GraphicsCatalog.SrPreset]);
        Assert.Equal(1u, rtx30[GraphicsCatalog.RrOverride]);
        Assert.Equal(GpuProfile.PresetF, rtx30[GraphicsCatalog.RrPreset]);
        Assert.Equal(1u, rtx30[GraphicsCatalog.FgOverride]);
        Assert.Equal(GpuProfile.PresetA, rtx30[GraphicsCatalog.FgPreset]);
        Assert.Equal(1u, rtx30[GameSettings.StreamlineDlssOverride]);
        Assert.False(rtx30.ContainsKey(GraphicsCatalog.SmoothMotion));

        // RTX 40/50 default (opt-in off): Recommended presets (0x00FFFFFF), NO Smooth Motion.
        var rtx40Default = GameSettings.WutheringWavesNvidiaValues(40, smoothMotionOptIn: false, usingNativeDlssG: false)
            .ToDictionary(v => v.Id, v => v.Value);
        Assert.Equal(GpuProfile.Recommended, rtx40Default[GraphicsCatalog.SrPreset]);
        Assert.Equal(GpuProfile.Recommended, rtx40Default[GraphicsCatalog.RrPreset]);
        Assert.False(rtx40Default.ContainsKey(GraphicsCatalog.SmoothMotion));

        // RTX 40/50 opt-in when NOT using native DLSS-G: enables driver Smooth Motion (0xB0D384C0 = 1).
        var rtx40Smooth = GameSettings.WutheringWavesNvidiaValues(40, smoothMotionOptIn: true, usingNativeDlssG: false)
            .ToDictionary(v => v.Id, v => v.Value);
        Assert.Equal(1u, rtx40Smooth[GraphicsCatalog.SmoothMotion]);
        Assert.Contains(
            GameSettings.WutheringWavesNvidiaValues(50, smoothMotionOptIn: true, usingNativeDlssG: false),
            v => v.Id == GraphicsCatalog.SmoothMotion && v.Value == 1u);

        // RTX 40/50 opt-in when native DLSS-G IS used: leaves Smooth Motion off.
        var rtx40Native = GameSettings.WutheringWavesNvidiaValues(40, smoothMotionOptIn: true, usingNativeDlssG: true)
            .ToDictionary(v => v.Id, v => v.Value);
        Assert.False(rtx40Native.ContainsKey(GraphicsCatalog.SmoothMotion));

        // GTX / non-RTX: nothing.
        Assert.Empty(GameSettings.WutheringWavesNvidiaValues(16, smoothMotionOptIn: true));
    }

    [Fact]
    public void WutheringWavesOptimizeAndPutBackApplyAndRestoreNvidiaOnlyProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-wuwa-apply-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(root, "game");
        var dataDir = Path.Combine(root, "data");
        var prevData = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var prevSeam = NvidiaProfiles.ForGameSeam;
        try
        {
            var win64 = Path.Combine(gameDir, "Client", "Binaries", "Win64");
            Directory.CreateDirectory(win64);
            Directory.CreateDirectory(dataDir);
            System.IO.File.WriteAllBytes(Path.Combine(win64, "Client-Win64-Shipping.exe"), []);
            System.IO.File.WriteAllBytes(Path.Combine(win64, "Client-Win64-ShippingBase.dll"), []);
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, dataDir);

            var driverHeld = new Dictionary<uint, uint?>();
            string? capturedFileInFolder = null;
            NvidiaProfiles.ForGameSeam = (program, ids, writes) =>
            {
                capturedFileInFolder = program.FileInFolder;
                if (writes is not null)
                    foreach (var (id, val) in writes)
                    {
                        if (val is { } v) driverHeld[id] = v;
                        else driverHeld.Remove(id);
                    }
                var after = ids.ToDictionary(id => id, id => driverHeld.GetValueOrDefault(id));
                return new NvidiaProfiles.GameState(true, null, "Exo: Wuthering Waves", new Dictionary<uint, uint?>(), after, []);
            };

            var game = new GameEntry { Id = "steam:3513350", Title = "Wuthering Waves", Store = StoreKind.Steam, Installed = true, Path = gameDir };
            var rtx4080 = new GameSettings.Pc("nvidia", null, 16, Gpu: "NVIDIA GeForce RTX 4080");

            var profileOff = GameSettings.For(game, rtx4080, smoothMotion: false)!;
            var initial = GameSettings.InspectState(game, profileOff);
            Assert.True(initial.Found);
            Assert.Equal(GameSettings.Standing.Off, initial.Standing);
            Assert.Single(initial.Groups!);

            Assert.Null(GameSettings.ApplyWithNvidia(game, profileOff, null, all: true, on: true));
            Assert.Equal("Client-Win64-ShippingBase.dll", capturedFileInFolder);
            Assert.Equal(1u, driverHeld[GameSettings.StreamlineDlssOverride]);
            Assert.False(driverHeld.ContainsKey(GraphicsCatalog.SmoothMotion));
            Assert.Equal(GameSettings.Standing.Applied, GameSettings.InspectState(game, profileOff).Standing);

            var profileSmooth = GameSettings.For(game, rtx4080, smoothMotion: true)!;
            Assert.Null(GameSettings.ApplyWithNvidia(game, profileSmooth, null, all: true, on: true));
            Assert.Equal(1u, driverHeld[GraphicsCatalog.SmoothMotion]);
            Assert.Equal(GameSettings.Standing.Applied, GameSettings.InspectState(game, profileSmooth).Standing);

            Assert.Null(GameSettings.ApplyWithNvidia(game, profileOff, null, all: true, on: false));
            Assert.Empty(driverHeld);
            Assert.Equal(GameSettings.Standing.Off, GameSettings.InspectState(game, profileOff).Standing);
        }
        finally
        {
            NvidiaProfiles.ForGameSeam = prevSeam;
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, prevData);
            try { DeletionGate.DeleteDirectory(root, recursive: true); } catch (IOException) { }
        }
    }
}

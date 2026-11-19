using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using AmdKeys = ExoLauncher.Helpers.AmdAdlx.Keys;
using IntelKeys = ExoLauncher.Helpers.IntelIgcl.Keys;

namespace ExoLauncher.Tests;

/// <summary>
/// Exo's automatic choices on 26 simulated PCs, as varied as the PCs its users have: Intel hybrid and not, Ryzen with one
/// CCD, X3D and two CCDs, APUs and a handheld, laptops with two graphics chips, GeForce GT and GTX to RTX 50, Radeon RX 6000
/// to 9000, Arc and Intel's built-in graphics, two cards from two makers, 2 to 32 GB of video memory, 8 GB of RAM, 1080p 60
/// to 4K 240, one to three screens. Ways they could go wrong on a PC that isn't the one Exo is built on:
/// - a processor's graphics (a Ryzen's Radeon Graphics, Intel's UHD, an APU's "RX Vega 11 Graphics") is taken for the card
///   games run on, so the wrong driver, profile, requirements verdict or frame generation follows it;
/// - a vendor's feature reaches another's card (Reflex, DLSS or an NVIDIA profile on a Radeon or an Arc, Anti-Lag on a
///   GeForce, FSR 4 or Fluid Motion Frames where they can't run), or frame generation is offered where it can't run;
/// - render threads land on E-cores or across CCDs, or fall under 2 on a small processor;
/// - a value falls outside what the game's line lists, textures outgrow the card's memory, or an Optimize sets a frame cap;
/// - an Intel processor is classed as a Ryzen for Exo's power plan (the i9-9900X is a "9900X");
/// - the Store reads a laptop's Core i5-1235U as a first-generation Core, or calls a PC Recommended from its processor
///   while its graphics can't be placed;
/// - RTX Video HDR is offered on a screen the NVIDIA card doesn't drive (an Optimus laptop's panel).
/// Each check runs every PC and lists every PC it fails on; the decisions themselves go to the test output as a table.
/// </summary>
public sealed class PcMatrixTests
{
    /// <summary>The main screen: resolution, highest refresh, variable refresh (as its driver says) and HDR.</summary>
    private sealed record Screen(int Width, int Height, int Hz, bool Vrr, bool Hdr = false);

    /// <summary>
    /// A PC: its processor as Windows names it and CPUID's hybrid flag, its cores, its display adapters as the driver key
    /// lists them, the card games run on, video memory (the most an adapter reports; null when none does), installed RAM, the
    /// main screen, and which vendor libraries answer. Expected: render threads, the power plan class, MW4's upscaler,
    /// Elden Ring's requirements verdict, a card a frame generation mod runs on, and AMD's FSR 4 override.
    /// <paramref name="Old"/>: Windows 10, no Balanced plan, games on a hard drive.
    /// </summary>
    private sealed record Rig(string Name, string Cpu, bool? Hybrid, MouseCore.Topology Cores, string[] Adapters, string Card, double? Vram, int Ram,
        Screen Main, int Threads, string Class, string? Upscaler, string? Store, bool FrameGen = true, bool Fsr4 = false, int Screens = 1,
        bool Headphones = false, bool Laptop = false, bool Nvapi = false, bool Adlx = false, bool Igcl = false, string? AmdSoftware = null,
        string? NvidiaDevice = null, bool Old = false);

    /// <summary>A processor: each kind of core (how many, two threads or one, efficiency class, which L3 it shares; -1 none) and its L3s in MB.</summary>
    private static MouseCore.Topology Chip(int[] l3Mb, params (int Count, bool Smt, byte Efficiency, int L3)[] kinds)
    {
        var cores = new List<MouseCore.Core>();
        var masks = new ulong[l3Mb.Length];
        var next = 0;
        foreach (var (count, smt, efficiency, l3) in kinds)
            for (var i = 0; i < count; i++)
            {
                int[] threads = smt ? [next, next + 1] : [next];
                next += threads.Length;
                cores.Add(new(threads, efficiency));
                if (l3 >= 0) foreach (var thread in threads) masks[l3] |= 1UL << thread;
            }
        return new(cores, [.. l3Mb.Select((mb, i) => new MouseCore.Cache(masks[i], mb * 1048576L))], next);
    }

    private static MouseCore.Topology Mono(int cores, bool smt, int l3Mb) => Chip([l3Mb], (cores, smt, 0, 0));
    private static MouseCore.Topology Hybrid(int p, int e, bool smt = true, int l3Mb = 30) => Chip([l3Mb], (p, smt, 1, 0), (e, false, 0, 0));
    private static MouseCore.Topology TwoCcds(int each, int firstMb, int secondMb) => Chip([firstMb, secondMb], (each, true, 0, 0), (each, true, 0, 1));

    private static readonly Rig[] Rigs =
    [
        new("i7-14700K, RTX 4070", "Intel(R) Core(TM) i7-14700K", true, Hybrid(8, 12),
            ["Intel(R) UHD Graphics 770", "NVIDIA GeForce RTX 4070"], "NVIDIA GeForce RTX 4070", 12, 32, new(2560, 1440, 165, true),
            7, "intel-hybrid", "DLSS", "recommended", Nvapi: true, Igcl: true, NvidiaDevice: "2786"),
        new("i5-13600K, Arc A770 listed after UHD 770", "13th Gen Intel(R) Core(TM) i5-13600K", true, Hybrid(6, 8),
            ["Intel(R) UHD Graphics 770", "Intel(R) Arc(TM) A770 Graphics"], "Intel(R) Arc(TM) A770 Graphics", 16, 32, new(1920, 1080, 144, true),
            5, "intel-hybrid", "XeSS", "recommended", Igcl: true),
        new("i5-12400F, Arc B580", "12th Gen Intel(R) Core(TM) i5-12400F", false, Mono(6, true, 18),
            ["Intel(R) Arc(TM) B580 Graphics"], "Intel(R) Arc(TM) B580 Graphics", 12, 16, new(2560, 1440, 180, true),
            5, "intel-non-hybrid", "XeSS", "recommended", Igcl: true),
        new("Core Ultra 7 155H laptop, Arc graphics", "Intel(R) Core(TM) Ultra 7 155H", true, Chip([24], (6, true, 1, 0), (8, false, 0, 0), (2, false, 0, -1)),
            ["Intel(R) Arc(TM) Graphics"], "Intel(R) Arc(TM) Graphics", null, 32, new(2880, 1800, 120, true),
            5, "intel-hybrid", "XeSS", null, Laptop: true, Igcl: true),
        new("i7-1165G7 laptop, Iris Xe", "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", false, Mono(4, true, 12),
            ["Intel(R) Iris(R) Xe Graphics"], "Intel(R) Iris(R) Xe Graphics", null, 16, new(1920, 1080, 60, false),
            3, "intel-non-hybrid", null, "below", FrameGen: false, Laptop: true, Igcl: true),
        new("i5-1235U laptop, UHD, 8 GB", "12th Gen Intel(R) Core(TM) i5-1235U", true, Hybrid(2, 8, l3Mb: 12),
            ["Intel(R) UHD Graphics"], "Intel(R) UHD Graphics", null, 8, new(1920, 1080, 60, false),
            2, "intel-hybrid", null, "below", FrameGen: false, Laptop: true, Igcl: true),
        new("i7-12700H laptop, Iris Xe + RTX 3060 Laptop", "12th Gen Intel(R) Core(TM) i7-12700H", true, Hybrid(6, 8, l3Mb: 24),
            ["Intel(R) Iris(R) Xe Graphics", "NVIDIA GeForce RTX 3060 Laptop GPU"], "NVIDIA GeForce RTX 3060 Laptop GPU", 6, 16, new(1920, 1080, 144, false),
            5, "intel-hybrid", "DLSS", "recommended", Laptop: true, Nvapi: true, Igcl: true, NvidiaDevice: "2520"),
        new("i5-7600K (4 cores), GTX 1060", "Intel(R) Core(TM) i5-7600K CPU @ 3.80GHz", false, Mono(4, false, 6),
            ["NVIDIA GeForce GTX 1060 6GB"], "NVIDIA GeForce GTX 1060 6GB", 6, 16, new(1920, 1080, 60, false),
            3, "intel-non-hybrid", null, "below", Nvapi: true, NvidiaDevice: "1C03"),
        new("i5-9600K (6 cores), GTX 1650", "Intel(R) Core(TM) i5-9600K CPU @ 3.70GHz", false, Mono(6, false, 9),
            ["NVIDIA GeForce GTX 1650"], "NVIDIA GeForce GTX 1650", 4, 16, new(1920, 1080, 75, true),
            5, "intel-non-hybrid", null, "below", Nvapi: true, NvidiaDevice: "1F82"),
        new("Ryzen 5 5600, RX 6600, AMD Software 26.9.2", "AMD Ryzen 5 5600 6-Core Processor", null, Mono(6, true, 32),
            ["AMD Radeon RX 6600"], "AMD Radeon RX 6600", 8, 16, new(1920, 1080, 144, true),
            5, "amd-single-ccd", null, "recommended", Adlx: true, AmdSoftware: "26.9.2"),
        new("Ryzen 7 7800X3D, RX 7900 XTX, 4K HDR", "AMD Ryzen 7 7800X3D 8-Core Processor", null, Mono(8, true, 96),
            ["AMD Radeon(TM) Graphics", "AMD Radeon RX 7900 XTX"], "AMD Radeon RX 7900 XTX", 24, 32, new(3840, 2160, 144, true, Hdr: true),
            7, "amd-single-ccd", "FSR", "recommended", Fsr4: true, Adlx: true, AmdSoftware: "26.10.1"),
        new("Ryzen 7 9800X3D, RX 9070 XT, 1440p 240", "AMD Ryzen 7 9800X3D 8-Core Processor", null, Mono(8, true, 96),
            ["AMD Radeon(TM) Graphics", "AMD Radeon RX 9070 XT"], "AMD Radeon RX 9070 XT", 16, 32, new(2560, 1440, 240, true),
            7, "amd-single-ccd", "FSR", "recommended", Fsr4: true, Headphones: true, Adlx: true, AmdSoftware: "26.10.1"),
        new("Ryzen 9 7950X3D, RTX 4090, 4K 120 HDR TV", "AMD Ryzen 9 7950X3D 16-Core Processor", null, TwoCcds(8, 96, 32),
            ["AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 4090"], "NVIDIA GeForce RTX 4090", 24, 64, new(3840, 2160, 120, true, Hdr: true),
            7, "amd-dual-ccd-x3d", "DLSS", "recommended", Nvapi: true, Adlx: true, NvidiaDevice: "2684"),
        new("Ryzen 9 9950X3D, RTX 5090, 360 Hz, V-Cache on the second CCD", "AMD Ryzen 9 9950X3D 16-Core Processor", null, TwoCcds(8, 32, 96),
            ["NVIDIA GeForce RTX 5090", "AMD Radeon(TM) Graphics"], "NVIDIA GeForce RTX 5090", 32, 64, new(2560, 1440, 360, true),
            7, "amd-dual-ccd-x3d", "DLSS", "recommended", Headphones: true, Nvapi: true, Adlx: true, NvidiaDevice: "2B85"),
        new("Ryzen 7 8700G, Radeon 780M only", "AMD Ryzen 7 8700G w/ Radeon 780M Graphics", null, Mono(8, true, 16),
            ["AMD Radeon 780M Graphics"], "AMD Radeon 780M Graphics", 4, 32, new(1920, 1080, 60, false),
            7, "amd-single-ccd", null, "below", Adlx: true, AmdSoftware: "26.10.1"),
        new("ROG Ally, Ryzen Z1 Extreme", "AMD Ryzen Z1 Extreme", null, Mono(8, true, 16),
            ["AMD Radeon(TM) Graphics"], "AMD Radeon(TM) Graphics", 4, 16, new(1920, 1080, 120, true),
            7, "amd-single-ccd", null, null, Laptop: true, Adlx: true, AmdSoftware: "26.10.1"),
        new("Ryzen AI 9 HX 370 laptop, Radeon 890M", "AMD Ryzen AI 9 HX 370 w/ Radeon 890M", null, Chip([16, 8], (4, true, 1, 0), (8, true, 0, 1)),
            ["AMD Radeon(TM) 890M Graphics"], "AMD Radeon(TM) 890M Graphics", 2, 32, new(2880, 1800, 120, true),
            3, "amd-single-ccd", null, "below", Fsr4: true, Laptop: true, Adlx: true, AmdSoftware: "26.10.1"),
        new("Ryzen 7 7840HS laptop, 780M + RX 7600S", "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", null, Mono(8, true, 16),
            ["AMD Radeon 780M Graphics", "AMD Radeon RX 7600S"], "AMD Radeon RX 7600S", 8, 16, new(2560, 1600, 165, true),
            7, "amd-single-ccd", "FSR", null, Fsr4: true, Laptop: true, Adlx: true, AmdSoftware: "26.10.1"),
        new("Ryzen 5 7600, Arc B580 beside the Ryzen's Radeon", "AMD Ryzen 5 7600 6-Core Processor", null, Mono(6, true, 32),
            ["AMD Radeon(TM) Graphics", "Intel(R) Arc(TM) B580 Graphics"], "Intel(R) Arc(TM) B580 Graphics", 12, 32, new(2560, 1440, 165, true),
            5, "amd-single-ccd", "XeSS", "recommended", Adlx: true, Igcl: true),
        new("i9-13900K, RX 7900 XT beside UHD 770, three screens", "13th Gen Intel(R) Core(TM) i9-13900K", true, Hybrid(8, 16, l3Mb: 36),
            ["Intel(R) UHD Graphics 770", "AMD Radeon RX 7900 XT"], "AMD Radeon RX 7900 XT", 20, 64, new(2560, 1440, 165, true),
            7, "intel-hybrid", "FSR", "recommended", Fsr4: true, Screens: 3, Adlx: true, Igcl: true, AmdSoftware: "26.10.1"),
        new("Ryzen 9 5900X, RTX 3070 and an RX 580 for more screens", "AMD Ryzen 9 5900X 12-Core Processor", null, TwoCcds(6, 32, 32),
            ["AMD Radeon RX 580 2048SP", "NVIDIA GeForce RTX 3070"], "NVIDIA GeForce RTX 3070", 8, 32, new(2560, 1440, 165, true),
            5, "amd-dual-ccd", "DLSS", "recommended", Screens: 2, Nvapi: true, Adlx: true, NvidiaDevice: "2484"),
        new("i7-10700K, RTX 2060 SUPER, 1440p 144", "Intel(R) Core(TM) i7-10700K CPU @ 3.80GHz", false, Mono(8, true, 16),
            ["NVIDIA GeForce RTX 2060 SUPER"], "NVIDIA GeForce RTX 2060 SUPER", 8, 16, new(2560, 1440, 144, true),
            7, "intel-non-hybrid", "DLSS", "recommended", Nvapi: true, NvidiaDevice: "1F06"),
        new("i9-9900, RTX 2060, two 1080p 60 screens", "Intel(R) Core(TM) i9-9900 CPU @ 3.10GHz", false, Mono(8, true, 16),
            ["NVIDIA GeForce RTX 2060"], "NVIDIA GeForce RTX 2060", 6, 16, new(1920, 1080, 60, false),
            7, "intel-non-hybrid", "DLSS", "recommended", Screens: 2, Nvapi: true, NvidiaDevice: "1F08"),
        new("i9-9900X, RTX 5080, 4K 240 HDR", "Intel(R) Core(TM) i9-9900X CPU @ 3.50GHz", false, Mono(10, true, 19),
            ["NVIDIA GeForce RTX 5080"], "NVIDIA GeForce RTX 5080", 16, 64, new(3840, 2160, 240, true, Hdr: true),
            9, "intel-non-hybrid", "DLSS", "recommended", Nvapi: true, NvidiaDevice: "2C02"),
        new("Pentium G4560, GT 1030 2 GB, 8 GB, Windows 10, hard drive", "Intel(R) Pentium(R) CPU G4560 @ 3.50GHz", false, Mono(2, true, 3),
            ["NVIDIA GeForce GT 1030"], "NVIDIA GeForce GT 1030", 2, 8, new(1920, 1080, 60, false),
            2, "intel-non-hybrid", null, "below", FrameGen: false, Nvapi: true, NvidiaDevice: "1D01", Old: true),
        new("Core Ultra 9 285K, RTX 5070 Ti, headphones", "Intel(R) Core(TM) Ultra 9 285K", true, Hybrid(8, 16, smt: false, l3Mb: 36),
            ["Intel(R) Graphics", "NVIDIA GeForce RTX 5070 Ti"], "NVIDIA GeForce RTX 5070 Ti", 16, 32, new(2560, 1440, 240, true),
            7, "intel-hybrid", "DLSS", "recommended", Headphones: true, Nvapi: true, Igcl: true, NvidiaDevice: "2C05"),
    ];

    // ---- What each PC gives Exo ----

    private static string Maker(string card) => card.Contains("NVIDIA", StringComparison.Ordinal) ? "nvidia" : card.Contains("Radeon", StringComparison.Ordinal) ? "amd" : "intel";

    private static string Plain(string name) => name.Replace("(R)", "", StringComparison.Ordinal).Replace("(TM)", "", StringComparison.Ordinal).Trim();

    private static string? Vendor(Rig r) => GraphicsService.ActiveVendor(GraphicsService.Vendors(r.Adapters), r.Nvapi, r.Adlx, r.Igcl);

    /// <summary>What GameSettings.ThisPc reads on this PC.</summary>
    private static GameSettings.Pc Facts(Rig r)
    {
        var vendor = Vendor(r);
        return new(vendor, GameSettings.RenderThreads(r.Cores), r.Vram, r.Screens, r.Main.Hz, r.Headphones);
    }

    private static StoreService.ThisPc ThisPc(Rig r) =>
        new(SystemSpecs.Cpu(r.Cpu), SystemSpecs.MainGpu(r.Adapters), r.Ram, r.Old ? 10 : 11, 500, r.Vram, 12, true);

    // Modern Warfare 4's s.1.0.cod26.txt, the lines whose values follow the PC, with the game's own allowed values (October 2026).
    private const string Mw4Text = """
        1

        // Display
        VSync@0;1;1 = 100% // one of disabled, 100%, 50%, 33%, 25%
        DLSSFrameGeneration@0;1;2 = true
        FSRFrameInterpolation@0;1;3 = false
        IntelXeFG@0;1;4 = false
        NvidiaReflex@0;1;5 = Disabled // one of Disabled, Enabled, Enabled + boost
        AmdAntilag2@0;1;6 = false
        DisplayMode@0;1;7 = Fullscreen // one of Windowed, Fullscreen, Fullscreen borderless window, Fullscreen borderless extended window
        PreferredDisplayMode@0;1;8 = Fullscreen // one of Fullscreen, Fullscreen borderless window, Fullscreen borderless extended window
        FocusedMode@0;1;9 = true
        RefreshRate@0;1;10 = {hz}.000
        Resolution@0;1;11 = Auto:{size}
        ConstrainMouse@1;1;12 = false
        CapFps@0;1;13 = false
        MaxFpsInGame@0;1;14 = 250 // 30 to 300
        MaxFpsOutOfFocus@0;1;15 = 60 // 5 to 300
        RendererWorkerCount@0;1;16 = -1 // -1 to 16
        GPUName@0;1;17 = {card}
        DynamicSceneResolution@0;1;18 = true
        NVIDIAImageScalingMP@0;1;19 = false
        MotionBlur@0;1;20 = On // one of Off, On, Cinematic Only
        WeaponMotionBlur@0;1;21 = true
        EnableVelocityBasedBlur@0;1;22 = true
        DofEnable@0;1;23 = On // one of Off, On, Script
        DofWeaponDisable@0;1;24 = false
        DofWorldDisable@0;1;25 = false
        DepthOfFieldQuality@0;1;26 = High // one of Low, High
        ResolutionMultiplier@0;1;27 = 90 // 0 to 200
        DLSSSharpnessMP@0;1;28 = 0.500000 // 0.000000 to 1.000000
        AATechniquePreferredMP@0;1;29 = SMAA // one of SMAA, DLSS, XeSS, FSR AA
        DLSSModeMP@0;1;30 = DLSS // one of DLSS
        DLSSPerfModeMP@0;1;31 = Native Resolution // one of Ultra Performance, Maximum Performance, Balanced, Maximum Quality, Native Resolution
        DLSSModelUI@0;1;32 = TRANSFORMER // one of CNN, TRANSFORMER
        AMDSuperResolution2Quality@0;1;33 = Native Resolution // one of Ultra Performance, Maximum Performance, Balanced, Maximum Quality, Native Resolution
        AMDContrastAdaptiveSharpeningStrength@0;1;34 = 0.500000 // 0.000000 to 1.000000
        XeSSQuality@0;1;35 = Native Resolution // one of Ultra Performance, Maximum Performance, Balanced, Maximum Quality, Ultra Quality, Ultra Quality Plus, Native Resolution
        TextureQuality@0;1;36 = 0 // 0 to 3
        VideoMemoryScaleMP@0;1;37 = 0.900000 // 0.000000 to 2.000000
        TextureFilter@0;1;38 = aniso 8x // one of aniso 2x, aniso 4x, aniso 8x, aniso 16x
        ModelQuality@0;1;39 = High Quality // one of Low Quality, Medium Quality, High Quality
        TerrainQuality@0;1;40 = High // one of Very Low, Low, Medium, High, Very High
        AudioMixPreset@0;1;41 = default
        AudioWantedChannelsNumber@0;1;42 = 0 // 0 to 16
        WindowsSonicEnable@0;1;43 = true
        """;

    /// <summary>MW4's file as the game writes it on this PC: the card it found, the resolution it outputs, a refresh rate fixed by hand.</summary>
    private static GameSettings.SettingsFile Mw4(Rig r) => GameSettings.Parse(Encoding.UTF8.GetBytes(Mw4Text
        .Replace("{card}", r.Card, StringComparison.Ordinal).Replace("{hz}", r.Main.Hz.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{size}", $"{r.Main.Width}x{r.Main.Height}", StringComparison.Ordinal).ReplaceLineEndings("\r\n")));

    private static readonly byte[] RocketLeagueFile = Encoding.ASCII.GetBytes(
        File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "RocketLeague", "TASystemSettings.ini")).ReplaceLineEndings("\r\n"));

    private static readonly IReadOnlyList<StoreService.Spec> EldenRing =
    [
        new("OS", "Windows 10", "Windows 10/11"),
        new("Processor", "INTEL CORE I5-8400 or AMD RYZEN 3 3300X", "INTEL CORE I7-8700K or AMD RYZEN 5 3600X"),
        new("Memory", "12 GB RAM", "16 GB RAM"),
        new("Graphics", "NVIDIA GEFORCE GTX 1060 3 GB or AMD RADEON RX 580 4 GB", "NVIDIA GEFORCE GTX 1070 8 GB or AMD RADEON RX VEGA 56 8 GB"),
        new("DirectX", "Version 12", "Version 12"),
        new("Storage", "60 GB available space", "60 GB available space"),
    ];

    private static readonly int[] EldenRingTags = [29482, 1695, 4604, 122, 4026, 4231, 3859, 1697];

    private const uint SrPreset = 0x10E41DF3, FgPreset = 0x10E41DF1;

    private static readonly HashSet<uint> NvidiaIds =
    [
        0x0005F543, GpuProfile.GsyncFeature, GpuProfile.GsyncGlobalMode, GpuProfile.GsyncAppMode, GpuProfile.VrrControl, GpuProfile.VSync,
        GpuProfile.FrameRateLimit, GpuProfile.FrameRateLimitSlider, GpuProfile.ModelPresetProfile, SrPreset, FgPreset, GpuProfile.ShaderCacheSize,
    ];

    /// <summary>A game with NVIDIA's frame generation through Streamline 2 and the 310 runtime, with or without FSR or XeSS's own, on DirectX 12 or Vulkan.</summary>
    private static DlssSwapService.FrameGenGame Game(bool own, bool vulkan) => new(@"C:\Games\Game", @"C:\Games\Game\Game.exe", true, 310, own, 2, vulkan, null, null, null);

    private static string? Value(IEnumerable<GameSettings.Group> groups, string key) => groups.SelectMany(g => g.Changes).FirstOrDefault(c => c.Key == key)?.Value;

    /// <summary>Each change a line of the file can't take: a value the line doesn't list, or a free value on a line that lists some.</summary>
    private static IEnumerable<string> Misfits(GameSettings.SettingsFile file, IEnumerable<GameSettings.Group> groups) =>
        from g in groups
        from c in g.Changes
        from l in file.Settings
        where l.Name == c.Key && (c.Free ? l.Allowed is not null : GameSettings.Fit(l, c.Value) is null)
        select $"{c.Key} = {c.Value} isn't one of \"{l.Allowed ?? "true or false"}\"";

    /// <summary>Runs a check on every PC and fails with every PC it found something on.</summary>
    private static void Each(Func<Rig, IEnumerable<string>> check)
    {
        var failures = new List<string>();
        foreach (var rig in Rigs)
        {
            try { failures.AddRange(check(rig).Select(problem => $"{rig.Name}: {problem}")); }
            catch (Exception ex) { failures.Add($"{rig.Name}: {ex.GetType().Name}: {ex.Message}"); }
        }
        Assert.True(failures.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    // ---- The checks ----

    [Fact]
    public void TheCardGamesRunOnIsTheOneExoGoesBy() => Each(Card);

    private static IEnumerable<string> Card(Rig r)
    {
        if (SystemSpecs.MainGpu(r.Adapters) is var main && main != Plain(r.Card)) yield return $"main graphics card {main ?? "none"}";
        if (Vendor(r) is var vendor && vendor != Maker(r.Card)) yield return $"Exo talks to {vendor ?? "no vendor"}'s driver";
        if (GpuDriverUpdate.Pick(Maker(r.Card), r.Adapters) is var pick && pick != r.Card) yield return $"driver updates for {pick ?? "nothing"}";
        if (Maker(r.Card) == "nvidia" && GpuProfileService.Series(r.Adapters) != (GpuProfile.NvidiaSeries(r.Card) ?? 0))
            yield return $"NVIDIA profile built for series {GpuProfileService.Series(r.Adapters)}";
    }

    [Fact]
    public void RenderThreadsAreTheFastCoresOfOneCcdLessOne() => Each(Threads);

    private static IEnumerable<string> Threads(Rig r)
    {
        var threads = GameSettings.RenderThreads(r.Cores);
        if (threads != r.Threads) yield return $"{threads} render threads, not {r.Threads}";
        if (threads is < 2 or > 16) yield return $"{threads} render threads is outside the game's 2 to 16";
    }

    [Fact]
    public void CallOfDutyFitsTheGameTheCardAndTheScreen() => Each(CallOfDuty);

    private static IEnumerable<string> CallOfDuty(Rig r)
    {
        var pc = Facts(r);
        var file = Mw4(r);
        var groups = GameSettings.CallOfDutyGroups(pc, file);
        foreach (var misfit in Misfits(file, groups)) yield return "MW4 " + misfit;
        var keys = groups.SelectMany(g => g.Changes).Select(c => c.Key).ToHashSet();
        if (keys.Contains("NvidiaReflex") != (pc.Vendor == "nvidia")) yield return keys.Contains("NvidiaReflex") ? "Reflex on another maker's card" : "no Reflex";
        if (keys.Contains("AmdAntilag2") != (pc.Vendor == "amd")) yield return keys.Contains("AmdAntilag2") ? "Anti-Lag 2 on another maker's card" : "no Anti-Lag 2";
        var upscaler = groups.FirstOrDefault(g => g.Id == "upscaler")?.Label.Split(' ')[0];
        if (upscaler != r.Upscaler) yield return $"upscaler {upscaler ?? "none"}, not {r.Upscaler ?? "none"}";
        // CNN only on an RTX 20 or 30 at 1440p and up, and then MW4's own NVIDIA profile lets it run.
        var cnn = r.Main.Height >= 1300 && (GpuProfile.NvidiaSeries(r.Card) ?? 0) is 20 or 30;
        if (r.Upscaler == "DLSS" && Value(groups, "DLSSModelUI") != (cnn ? "CNN" : "TRANSFORMER")) yield return $"DLSS model {Value(groups, "DLSSModelUI")}";
        if (GameSettings.ModernWarfare4Nvidia().Values(file).Count > 0 != cnn) yield return cnn ? "no NVIDIA profile for CNN" : "an NVIDIA profile it doesn't need";
        if (Value(groups, "TextureQuality") is { } textures)
        {
            // 0 is the highest; 1 wants 10 GB, and 4 GB or less gets 3, the lowest.
            var quality = int.Parse(textures, CultureInfo.InvariantCulture);
            if (r.Vram < 10 && quality < 2 || r.Vram <= 4.5 && quality != 3) yield return $"textures {textures} on {r.Vram} GB";
            if (double.Parse(Value(groups, "VideoMemoryScaleMP")!, CultureInfo.InvariantCulture) > 0.8) yield return "the game may fill over 80% of video memory";
        }
        else if (r.Vram is not null) yield return "no textures for its memory";
        if (Value(groups, "MaxFpsInGame") is { } cap) yield return $"a frame cap of {cap}";
        if (Value(groups, "RendererWorkerCount") != r.Threads.ToString(CultureInfo.InvariantCulture)) yield return $"{Value(groups, "RendererWorkerCount")} render threads";
        if ((Value(groups, "ConstrainMouse") is not null) != (r.Screens > 1)) yield return "mouse kept in the game with one screen, or not with two";
        if (groups.Any(g => g.Id == "headphones") != r.Headphones) yield return "headphones mix without headphones, or not with them";
    }

    [Fact]
    public void RocketLeagueFitsTheScreenAndTheCard() => Each(RocketLeague);

    private static IEnumerable<string> RocketLeague(Rig r)
    {
        var pc = Facts(r);
        var file = GameSettings.ParseRocketLeague(RocketLeagueFile);
        var groups = GameSettings.RocketLeagueGroups(pc);
        foreach (var misfit in Misfits(file, groups)) yield return "Rocket League " + misfit;
        if (Value(groups, "CustomFPS") is { } cap) yield return $"a frame cap of {cap}";
        if (groups.Any(g => g.Id == "filtering") != r.Vram >= 2) yield return "16x filtering on graphics without its own memory, or not on a card";
    }

    [Fact]
    public void EachDriverProfileStaysWithItsCard() => Each(DriverProfile);

    private static IEnumerable<string> DriverProfile(Rig r)
    {
        var vrr = r.Main.Vrr;
        switch (Vendor(r))
        {
            case "nvidia":
            {
                var series = GpuProfileService.Series(r.Adapters);
                var writes = GpuProfile.Nvidia(new GpuProfile.Variant(series), 500, NvidiaIds, new Dictionary<uint, uint?>());
                uint? model = series >= 40 ? 0x00FFFFFFu : series >= 20 ? 11u : (uint?)null;
                if (writes.GetValueOrDefault(SrPreset) != model) yield return $"DLSS preset {writes.GetValueOrDefault(SrPreset)} on series {series}";
                if (writes.ContainsKey(FgPreset) != series >= 20) yield return "frame generation preset on a card without DLSS, or missing on an RTX";
                // G-SYNC off, so the driver sets no frame cap on any screen.
                if (writes.GetValueOrDefault(GpuProfile.FrameRateLimit) is not null) yield return $"cap {writes.GetValueOrDefault(GpuProfile.FrameRateLimit)} on {r.Main.Hz} Hz";
                if (writes.GetValueOrDefault(GpuProfile.ShaderCacheSize) != GpuProfile.CacheUnlimited) yield return "shader cache not unlimited with room";
                break;
            }
            case "amd":
            {
                var values = new Dictionary<string, int>
                {
                    [AmdKeys.AntiLag] = 0, [AmdKeys.Chill] = 1, [AmdKeys.Boost] = 1, [AmdKeys.Rsr] = 1, [AmdKeys.Sharpening] = 1,
                    [AmdKeys.EnhancedSync] = 1, [AmdKeys.VSync] = 0, [AmdKeys.Frtc] = 0, [AmdKeys.FrtcFps] = 300, [AmdKeys.Afmf] = 0,
                };
                if (vrr) values[AmdKeys.FreeSync + "Main"] = 0;
                // The driver lists its FSR 4 override on its cards, and on a processor's graphics only where AMD brought it.
                if (GraphicsService.Discrete(r.Card) || r.Fsr4) values[AmdKeys.Fsr] = 0;
                var writes = GpuProfile.Radeon(new GpuDriverState(r.Card, r.AmdSoftware, values, 30, 300)).ToDictionary();
                if (writes.GetValueOrDefault(AmdKeys.AntiLag) != 1) yield return "Anti-Lag not on";
                if (writes.ContainsKey(AmdKeys.Afmf)) yield return "Fluid Motion Frames written for every game";
                if (writes.ContainsKey(AmdKeys.Fsr) != r.Fsr4) yield return writes.ContainsKey(AmdKeys.Fsr) ? "FSR 4 where it can't run" : "no FSR 4";
                if (writes.ContainsKey(AmdKeys.FrtcFps) || writes.ContainsKey(AmdKeys.Frtc)) yield return "a frame rate target from Exo";
                break;
            }
            case "intel":
            {
                var values = new Dictionary<string, int>
                {
                    [IntelKeys.LowLatency] = 0, [IntelKeys.FrameLimit] = 0, [IntelKeys.Endurance] = 1, [IntelKeys.Sharpening] = 1,
                    [IntelKeys.AdaptiveSync] = 0, [IntelKeys.ShaderDownload] = 0,
                };
                var writes = GpuProfile.IntelWrites(new GpuDriverState(r.Card, null, values, 30, 300), GpuProfile.LowLatencyOn).ToDictionary();
                if (writes.ContainsKey(IntelKeys.FrameLimit)) yield return "a frame limit from Exo";
                if (writes.GetValueOrDefault(IntelKeys.LowLatency) != 1 || writes.GetValueOrDefault(IntelKeys.Endurance, 1) != 0) yield return "low latency off or Endurance Gaming on";
                break;
            }
            default:
                yield return "no vendor's driver answers";
                break;
        }
    }

    [Fact]
    public void FrameGenerationOnlyWhereItRuns() => Each(FrameGeneration);

    private static IEnumerable<string> FrameGeneration(Rig r)
    {
        var card = DlssSwapService.FrameGenCardFor(SystemSpecs.FrameGenName(SystemSpecs.MainGpu(r.Adapters), SystemSpecs.Cpu(r.Cpu)));
        if ((card is not null) != r.FrameGen) yield return card is null ? "no frame generation for its card" : $"frame generation on a {card.Vendor} card that can't run it";
        var series = card is { Vendor: "nvidia" } ? card.Series : 0;
        // DLSS (DLSSG SM86) on an RTX 20 or 30 in DirectX 12. OptiScaler where that does not apply. An RTX 40 and
        // an RTX 50 have NVIDIA's own, so no mod.
        foreach (var (own, vulkan) in new[] { (false, false), (true, false), (false, true) })
        {
            var expected = card is null || series >= 40 ? null
                : series is >= 20 && !vulkan ? "dlss"
                : own ? "native" : "fsr";
            var path = DlssSwapService.FrameGenPath(card, Game(own, vulkan));
            if (path != expected) yield return $"{(vulkan ? "Vulkan" : own ? "own and DLSS" : "DLSS-only")} game gets {path ?? "nothing"}, not {expected ?? "nothing"}";
        }
        if (Vendor(r) != "amd") yield break;
        // Fluid Motion Frames, Exo's per game: every Radeon here runs it (or its name leaves it to the driver).
        if (!FluidMotionFrames.Runs(r.Card)) yield return "no Fluid Motion Frames on a Radeon that has it";
        var graphics = FluidMotionFrames.Graphics(EldenRing, ThisPc(r));
        var single = FluidMotionFrames.Decide(r.Card, new FluidMotionFrames.Game("ELDEN RING") { Tags = EldenRingTags, Graphics = graphics });
        if (single.On == (graphics is "minimum" or "below")) yield return $"Fluid Motion Frames {(single.On ? "on" : "off")} in Elden Ring ({single.Why})";
        if (FluidMotionFrames.Decide(r.Card, new FluidMotionFrames.Game("Counter-Strike 2") { Tags = [1663, 1774, 3859] }).On) yield return "Fluid Motion Frames in a competitive game";
    }

    [Fact]
    public void RequirementsJudgeEveryPc() => Each(Requirements);

    private static IEnumerable<string> Requirements(Rig r)
    {
        var fit = StoreService.FitSpecs(EldenRing, ThisPc(r));
        if (fit.Verdict != r.Store)
            yield return $"Elden Ring {fit.Verdict ?? "not judged"}, not {r.Store ?? "not judged"} ({string.Join(", ", fit.Rows.Select(row => $"{row.Label} {row.Mine}: {row.Meets ?? "-"}"))})";
    }

    [Theory]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "minimum")]
    [InlineData("12th Gen Intel(R) Core(TM) i5-1235U", "minimum")]
    [InlineData("Intel(R) Core(TM) i5-1035G1 CPU @ 1.00GHz", "below")]
    [InlineData("13th Gen Intel(R) Core(TM) i7-1360P", "recommended")]
    [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", "recommended")]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "recommended")]
    public void LaptopProcessorsAreReadByTheirGeneration(string cpu, string meets) =>
        Assert.Equal(meets, StoreService.FitSpecs(EldenRing, new StoreService.ThisPc(SystemSpecs.Cpu(cpu), null, 16, 11, 500)).Rows.Single(row => row.Label == "Processor").Meets);

    [Fact]
    public void PowerAndHealthFollowTheProcessorAndCard() => Each(Health);

    private static IEnumerable<string> Health(Rig r)
    {
        var cls = PcHealth.ClassOf(r.Cpu, r.Hybrid);
        if (PowerProfiles.Keys[cls] != r.Class) yield return $"power plan class {PowerProfiles.Keys[cls]}";
        if (PcHealth.ShortCpu(r.Cpu) is { } name && (name.Contains("w/", StringComparison.Ordinal) || name.Contains("Radeon", StringComparison.Ordinal))) yield return $"processor named \"{name}\"";
        if (PcHealth.ChipsetVendor(r.Cpu) != (r.Cpu.StartsWith("AMD", StringComparison.Ordinal) ? "amd" : "intel")) yield return $"{PcHealth.ChipsetVendor(r.Cpu)} chipset driver";
        var plan = PowerProfiles.For(cls, r.Laptop);
        if (plan.Values.Count == 0 || plan.Values.Any(v => v.Name == "Sleep after") == r.Laptop) yield return "power plan values for the wrong kind of PC";
        // The rows this PC would see with core parking off, Resizable BAR off, GPU scheduling off and, on a laptop, two chips.
        var integrated = !GraphicsService.Discrete(r.Card);
        var two = r.Laptop && r.Adapters.Length > 1 ? new PcHealth.HybridGpu(r.Adapters.First(a => !GraphicsService.Discrete(a)), r.Card) : null;
        var power = new PowerPlan.State(PowerPlan.HighPerformance, "High performance", Guid.Empty, r.Laptop, false, false, new PowerPlan.Limits(100, 2, 100));
        var rows = PcHealth.Decide(new PcHealth.Reading([], new PcHealth.GpuBar(r.Card, integrated, false), r.Cpu, power, null, null, HagsOff: true, Hybrid: two))
            .Select(item => item.Key).ToHashSet();
        if (rows.Contains("power") != (r.Class is "amd-dual-ccd-x3d" or "amd-dual-ccd-zen5")) yield return rows.Contains("power") ? "core parking called out on a processor that doesn't park" : "core parking not called out";
        var series = GpuProfile.NvidiaSeries(r.Card) ?? 0;
        var radeon = Regex.Match(r.Card, @"\bRX (\d)\d{3}") is { Success: true } rx ? rx.Groups[1].Value[0] - '0' : 0;
        var arc = Regex.IsMatch(r.Card, @"Arc\(TM\) [AB]\d{3}");
        if (rows.Contains("rebar") != (!integrated && (series >= 30 || radeon >= 6 || arc))) yield return "Resizable BAR row on a card without it, or missing";
        if (rows.Contains("gpusched") != (!integrated && (series >= 40 || radeon >= 7))) yield return "GPU scheduling row on a card without frame generation, or missing";
        if (rows.Contains("gpupref") != (two is not null)) yield return "graphics for games row without two chips, or missing";
    }

    [Fact]
    public void ScreensGetWhatTheyRun() => Each(Screens);

    private static IEnumerable<string> Screens(Rig r)
    {
        if (DisplayOutput.BestRefresh(true, [60, r.Main.Hz], 60) != r.Main.Hz) yield return "main screen not at its highest refresh";
        // RTX Video HDR on an HDR screen an RTX card drives; an Optimus laptop's panel is on its built-in graphics, where NVIDIA's video calls find no display.
        var drives = Maker(r.Card) == "nvidia" && !(r.Laptop && r.Adapters.Length > 1);
        int? videoHdr = drives && (GpuProfile.NvidiaSeries(r.Card) ?? 0) >= 20 ? 0 : null;
        if (WebHostBridge.WantsVideoHdr(r.Main.Hdr, drives ? 1u : (uint?)null, videoHdr) != (r.Main.Hdr && videoHdr is not null)) yield return "RTX Video HDR where it can't run, or missing";
    }

    [Fact]
    public void DriversDevicesAndDrivesDontDependOnTheMaker() => Each(Devices);

    private static IEnumerable<string> Devices(Rig r)
    {
        // NVIDIA's current branches start at Turing (a GTX 16 too); a GTX 10 or GT 1030 never gets a newer branch's hotfix.
        if (r.NvidiaDevice is { } device)
        {
            if (NvidiaDriverService.CurrentBranches($@"pci\ven_10de&dev_{device}") != (GpuProfile.NvidiaSeries(r.Card) ?? 0) >= 16) yield return "driver branch for another generation";
            if (NvidiaDriverService.Workstation(r.Card)) yield return "a GeForce taken for a workstation card";
        }
        // The graphics card's interrupts on a shared line: MSI and high priority, whoever made it.
        var id = Maker(r.Card) switch { "nvidia" => "10DE", "amd" => "1002", _ => "8086" };
        var gpu = new InterruptMode.Device($@"PCI\VEN_{id}&DEV_0001&SUBSYS_00000000&REV_00\4&1&0&0008", r.Card, "drv", 0, 0x03, 0x00, 0, 3, 1, 0x00FF2001,
            InterruptMode.Mode.Line, null);
        if (InterruptMode.Decide(gpu, []).Plan != InterruptMode.Plan.Change) yield return "its graphics card left on a shared line";
        // A desktop's Ethernet with energy-efficient Ethernet on gets it off; a laptop on Wi-Fi alone has nothing to set.
        var ethernet = new NetworkTune.Adapter("0001", "Ethernet", "Intel(R) Ethernet Controller I226-V", @"PCI\VEN_8086&DEV_125C", false, 2_500_000_000,
            new Dictionary<string, NetworkTune.Param> { ["*EEE"] = new("1", "1", new Dictionary<string, string> { ["0"] = "Disabled", ["1"] = "Enabled" }) });
        var wifi = new NetworkTune.Adapter("0002", "Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", @"PCI\VEN_8086&DEV_51F1", true, 1_200_000_000, new Dictionary<string, NetworkTune.Param>());
        if ((r.Laptop ? NetworkTune.Decide(wifi, true) : NetworkTune.Decide(ethernet, false)).Count != (r.Laptop ? 0 : 1)) yield return "network settings for the wrong adapter";
        if (!r.Old) yield break;
        // Exo's plan from the plan that's on when Balanced is gone; games on a hard drive called out.
        if (PowerPlan.Sources(PowerPlan.HighPerformance) is not [var first, var then] || first != PowerPlan.Balanced || then != PowerPlan.HighPerformance) yield return "no plan to make Exo's from";
        var hdd = new Drives.Disk(0, "WDC WD10EZEX", Drives.Bus.Sata, "Hard drive", "01.01A01", null, null, "storahci", null, null, null,
            [new("C:", 1_000_000_000_000, 400_000_000_000)], null);
        if (!Drives.Verdicts(hdd, [new("steam:570", "Dota 2", "C:", 60_000_000_000)], false).Any(v => v.Key == "hdd")) yield return "games on a hard drive not called out";
    }

    // ---- Names ----

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060 Laptop GPU", true)]
    [InlineData("AMD Radeon RX 7600S", true)]
    [InlineData("AMD Radeon PRO W6800", true)]
    [InlineData("AMD Radeon AI PRO R9700", true)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", true)]
    [InlineData("Intel(R) Arc(TM) A370M Graphics", true)]
    [InlineData("AMD Radeon(TM) RX Vega 11 Graphics", false)]
    [InlineData("AMD Radeon(TM) Graphics", false)]
    [InlineData("AMD Radeon 780M Graphics", false)]
    [InlineData("AMD Radeon(TM) 8060S Graphics", false)]
    [InlineData("Intel(R) Arc(TM) Graphics", false)]
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)", false)]
    [InlineData("Intel(R) UHD Graphics 770", false)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", false)]
    public void ACardOfItsOwnIsToldFromAProcessorsGraphics(string adapter, bool discrete) => Assert.Equal(discrete, GraphicsService.Discrete(adapter));

    [Fact]
    public void ACardTakenOutIsNotTheMainOne()
    {
        // The driver key of a GTX 1060 taken out still names it, first; Direct3D lists only the card that's there.
        string[] adapters = ["NVIDIA GeForce GTX 1060 6GB", "NVIDIA GeForce RTX 4070"];
        Assert.Equal("NVIDIA GeForce RTX 4070", SystemSpecs.MainGpu(adapters, "NVIDIA GeForce RTX 4070"));
        // An APU's "RX Vega 11 Graphics" beside an Arc card: the Arc's maker first.
        Assert.Equal(new[] { "intel", "amd" }, GraphicsService.Vendors(["AMD Radeon(TM) RX Vega 11 Graphics", "Intel(R) Arc(TM) A750 Graphics"]));
    }

    // ---- The table ----

    [Fact]
    public void EveryPcsDecisions()
    {
        var output = TestContext.Current.TestOutputHelper;
        foreach (var r in Rigs)
        {
            var pc = Facts(r);
            var mw4 = GameSettings.CallOfDutyGroups(pc, Mw4(r));
            var rl = GameSettings.RocketLeagueGroups(pc);
            var card = DlssSwapService.FrameGenCardFor(SystemSpecs.FrameGenName(SystemSpecs.MainGpu(r.Adapters), SystemSpecs.Cpu(r.Cpu)));
            var paths = string.Join("/", new[] { (false, false), (true, false), (false, true) }.Select(g => DlssSwapService.FrameGenPath(card, Game(g.Item1, g.Item2)) ?? "-"));
            var latency = Value(mw4, "NvidiaReflex") is not null ? "Reflex" : Value(mw4, "AmdAntilag2") is not null ? "Anti-Lag 2" : "-";
            var upscaler = mw4.FirstOrDefault(g => g.Id == "upscaler") is { } u
                ? $"{u.Label.Split(' ')[0]} {Value(mw4, "DLSSPerfModeMP") ?? Value(mw4, "AMDSuperResolution2Quality") ?? Value(mw4, "XeSSQuality")}{(Value(mw4, "DLSSModelUI") is { } m ? " " + m : "")}"
                : "-";
            output?.WriteLine(string.Join(" | ",
                r.Name, $"{pc.Vendor ?? "-"} {SystemSpecs.MainGpu(r.Adapters)}", $"{pc.RenderThreads} threads",
                $"MW4 {latency}, {upscaler}, textures {Value(mw4, "TextureQuality") ?? "-"} at {Value(mw4, "VideoMemoryScaleMP") ?? "-"}, detail {Value(mw4, "ModelQuality")}",
                $"RL {(rl.Any(g => g.Id == "filtering") ? "16x" : "-")}",
                $"frame gen {card?.Vendor ?? "-"} {paths}",
                $"Elden Ring {StoreService.FitSpecs(EldenRing, ThisPc(r)).Verdict ?? "-"}",
                $"plan {PowerProfiles.Keys[PcHealth.ClassOf(r.Cpu, r.Hybrid)]}"));
        }
    }
}

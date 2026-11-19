using System.Globalization;

namespace ExoLauncher.Services;

/// <summary>
/// Hell is Us (Rogue Factor, Unreal Engine 5, DX12; Steam 1620730; project folder HellIsUs, module Charlie; no anti-cheat
/// found). Its Video menu is the engine's GameUserSettings.ini (%LOCALAPPDATA%\HellIsUs\Saved\Config\Windows), not a sealed
/// save: the quality levels are [ScalabilityGroups]' sg.* lines, and the upscalers, frame generation and Reflex are config
/// properties of its settings class, whose names and values are in its program's reflection data (read, never run):
/// NvidiaSuperResolution, NvidiaFrameGeneration, NvidiaReflex, IntelXeSSMode, AMDSuperResolutionMode. Its save,
/// HellIsUsProfile.sav, is Steam Cloud's: an open GVAS with motion blur, brightness and audio and no quality line, and Exo
/// leaves it. The game's pak index is encrypted, so Exo has not read its Scalability.ini: Low to Ultra as 0 to 4 is
/// the program's ECharlieScalabilityQuality in the engine's usual order. Nothing is written until the game has written the
/// file (<see cref="Set"/> waits for it), and a line it doesn't hold is left out.
/// The values are BenchmarKing's Optimized Settings (youtu.be/BFdz1s7gCRk, September 4 2025, measured on a 5700X3D with an
/// RTX 3060 Ti 8 GB and a 4070 Super 12 GB; Ultra preset to his: 32-44% faster at 1440p with DLSS Quality): Foliage and View
/// Distance are the costly scenery (Foliage up to 20%), Global Illumination and Reflections cost nothing until Very High
/// (12%, up to 18%), Shadows 9% to High and 20% to Ultra, Shading is free, and more than Medium textures fills 8 GB and
/// stutters. Where he gives two values a strong card takes the upper. Post-processing Ultra is his, as taste.
/// Beyond him: past 8 GB textures go to High (he has no recommendation there); the window mode choice; Reflex on; DLSS
/// Quality as he tests it, Performance at 4K, where he doesn't; and a Radeon or Arc card on XeSS, which he found stable where
/// FSR 3 and TSR shimmer. One upscaler at a time, since the game disables any other that is on (its
/// UpscalerDisabledByOtherEnabled), and the same for frame generation. With Exo's frame generation in the game (DLSSG SM86,
/// which runs on the game's own DLSS Frame Generation), that line stays on at 2x, as the mod's own ini generates one frame,
/// with V-Sync off as Streamline requires; the mod needs it on in the menu, so the line says so. Left alone: the preset (he
/// found picking one resets DLSS's render scale until the DLSS mode is switched), resolution scale, the frame cap, Motion
/// Blur (in the save), and no Engine.ini.
/// </summary>
internal static partial class GameSettings
{
    /// <summary>The id its HDR peak was kept under before it had a profile of its own (<see cref="UnrealHdr"/>), so Put back still finds that.</summary>
    internal const string HellIsUsId = "ue.HellIsUs";

    private const string HellIsUsProgramName = "HellIsUs-Win64-Shipping.exe";

    internal static string HellIsUsConfig =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HellIsUs", "Saved", "Config", "Windows");

    private static string HellIsUsBinaries(string path) => Path.Combine(path, "HellIsUs", "Binaries", "Win64");

    /// <summary>Hell is Us's install: its project folder and its program where the engine puts them.</summary>
    internal static bool IsHellIsUs(string path) => File.Exists(Path.Combine(HellIsUsBinaries(path), HellIsUsProgramName));

    /// <summary>Exo's frame generation is in this install (not a record of one Exo already took out).</summary>
    internal static bool HellIsUsHasFrameGeneration(string path) =>
        File.Exists(Path.Combine(HellIsUsBinaries(path), DlssSwapService.FrameGenManifestName)) && DlssSwapService.ReadFrameGenManifest(HellIsUsBinaries(path)) is not { Out: true };

    private const string Scalability = "0 to 4";

    /// <summary>
    /// The lines Exo may change, or read (ResolutionSizeY), and what each takes: Unreal's window modes, the game's own enums
    /// by the names its program registers, the HDR peak (<see cref="UnrealHdrGroup"/>).
    /// </summary>
    private static readonly Dictionary<string, string> HellIsUsKeys = new(StringComparer.Ordinal)
    {
        ["sg.ViewDistanceQuality"] = Scalability, ["sg.ShadowQuality"] = Scalability, ["sg.GlobalIlluminationQuality"] = Scalability,
        ["sg.ReflectionQuality"] = Scalability, ["sg.PostProcessQuality"] = Scalability, ["sg.TextureQuality"] = Scalability,
        ["sg.EffectsQuality"] = Scalability, ["sg.FoliageQuality"] = Scalability, ["sg.ShadingQuality"] = Scalability,
        ["FullscreenMode"] = "0 to 2", ["LastConfirmedFullscreenMode"] = "0 to 2", ["PreferredFullscreenMode"] = "0 to 1",
        ["ResolutionSizeY"] = "1 to 10000",
        ["NvidiaSuperResolution"] = "one of Off, UltraPerformance, Performance, Balanced, Quality, DLAA",
        ["NvidiaFrameGeneration"] = "one of Off, Auto, On2X, On3X, On4X",
        ["NvidiaReflex"] = "one of Disabled, Enabled, EnabledPlusBoost",
        ["IntelXeSSMode"] = "one of Off, UltraPerformance, Performance, Balanced, Quality, UltraQuality, UltraQualityPlus, AntiAliasing",
        ["AMDSuperResolutionMode"] = "one of Off, UltraPerformance, Performance, Balanced, Quality, NativeAA",
        ["bIntelXeSSFrameGenerationEnabled"] = Toggle, ["bAMDFrameGenerationEnabled"] = Toggle, ["bUseVSync"] = Toggle,
        ["bCompletedScalabilityBenchmark"] = Toggle,
        [UnrealHdrNits] = UnrealHdrRange,
    };

    internal static SettingsFile ParseHellIsUs(byte[] bytes) =>
        ParseIni(bytes, ["ScalabilityGroups", "/Script/Charlie.CharlieGameUserSettings"], HellIsUsKeys);

    /// <summary>Nothing is written while the game or its launcher stub runs.</summary>
    internal static Profile HellIsUs(Pc pc, bool frameGeneration = false) =>
        new(HellIsUsId, "Hell is Us", Path.Combine(HellIsUsConfig, "GameUserSettings.ini"), file => HellIsUsGroups(pc, frameGeneration, file),
            ["HellIsUs-Win64-Shipping", "HellIsUs"], Format: ParseHellIsUs,
            Program: game => string.IsNullOrEmpty(game.Path) ? null : Path.Combine(HellIsUsBinaries(game.Path), HellIsUsProgramName),
            WindowMode: pc.Fullscreen ? Fullscreen : Borderless, WindowLines: UnrealWindowLines);

    internal static IReadOnlyList<Group> HellIsUsGroups(Pc pc, bool frameGeneration, SettingsFile file)
    {
        var strong = Strong(pc.Gpu);
        List<Group> groups = [UnrealWindow(pc.Fullscreen), new("quality", strong ? "Graphics for a strong card" : "Graphics for this card",
        [
            new("sg.EffectsQuality", "2"),
            new("sg.FoliageQuality", strong ? "2" : "1"),
            new("sg.GlobalIlluminationQuality", "2"),
            new("sg.PostProcessQuality", "4"),
            new("sg.ReflectionQuality", "2"),
            new("sg.ShadingQuality", "2"),
            // His fallback under the card he tests with.
            new("sg.ShadowQuality", pc.VideoMemoryGb < 8 ? "1" : "2"),
            new("sg.ViewDistanceQuality", strong ? "3" : "2"),
            // The levels above stay as they are: without this the game's first-run benchmark would choose its own.
            new("bCompletedScalabilityBenchmark", "True"),
        ])];
        if (pc.VideoMemoryGb is { } memory)
            groups.Add(new("memory", "Textures for this card's memory", [new("sg.TextureQuality", memory <= 8.5 ? "1" : "2")]));
        if (HellIsUsUpscaler(pc, ValueOf(file, "ResolutionSizeY")) is { } upscaler) groups.Add(upscaler);
        if (frameGeneration)
            groups.Add(new("framegen", "DLSS Frame Generation on, for Exo's frame generation",
                [new("NvidiaFrameGeneration", "On2X"), new("bUseVSync", "False"), new("bAMDFrameGenerationEnabled", "False"), new("bIntelXeSSFrameGenerationEnabled", "False")]));
        if (pc.HdrPeak is { } peak) groups.Add(UnrealHdrGroup(peak));
        return groups;
    }

    /// <summary>
    /// DLSS with Reflex on a GeForce RTX, XeSS on any other card, and the other upscalers off: Quality, as he tests it, up to
    /// 1440p, and Performance at 4K. Null when the card is unknown.
    /// </summary>
    private static Group? HellIsUsUpscaler(Pc pc, string? outputHeight)
    {
        if (string.IsNullOrEmpty(pc.Gpu)) return null;
        var mode = int.TryParse(outputHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) && height >= 2000 ? "Performance" : "Quality";
        return GeForceRtx().IsMatch(pc.Gpu)
            ? new("upscaler", $"DLSS {mode} and Reflex",
                [new("NvidiaSuperResolution", mode), new("NvidiaReflex", "Enabled"), new("IntelXeSSMode", "Off"), new("AMDSuperResolutionMode", "Off")])
            : new("upscaler", $"XeSS {mode}",
                [new("IntelXeSSMode", mode), new("NvidiaSuperResolution", "Off"), new("AMDSuperResolutionMode", "Off")]);
    }
}

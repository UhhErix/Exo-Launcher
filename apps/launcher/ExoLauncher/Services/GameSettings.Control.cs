using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// CONTROL Resonant (Remedy, Northlight). Its video page is one JSON file,
/// %LOCALAPPDATA%\Remedy\CONTROLResonant\renderer.ini. There is no engine ini.
/// The numbers are the game's own enum order, read from the registration in CONTROLResonant.exe
/// (each list is the pointer table loaded with that setting): window mode Window, Borderless, Fullscreen;
/// DLSS frame generation Off, 2x, 3x, 4x, 5x, 6x, Dynamic; the upscaler Off, QB Upscaler, DLSS, FSR;
/// textures Low, 1G, 2G, 4G (the menu says Low, Medium, High, Ultra). The quality lines count up from the menu's lowest
/// step, as the Ultra preset's file shows by holding the top of each: volumetrics and shadow resolution Medium, High, Ultra;
/// shadow filtering and fog Medium, High; SDF reflections Off, Medium, High; reflections Off, Medium, High, Ultra;
/// post-processing Render Resolution, Display Resolution. The picture follows BenchmarKing's Optimized Settings
/// (youtu.be/peF6jgKQCeQ, September 25 2026, measured on a 5700X3D with an RTX 3060 Ti 8 GB and a 4070 Super 12 GB):
/// Volumetric Lighting never Ultra (22% for nothing he could see in play), Display Resolution post-processing never (up to
/// 13%), and what is free kept up. His RTX 20 and 30 rules are already Exo's: Preset K through the driver profile
/// (<see cref="GpuProfile"/>, against the default DLSS's Preset M, 14% slower at Balanced on a 3060 Ti by his DLSS 4.5 video, youtu.be/sTaqPIHRFsU), no Ray Reconstruction. Where he gives two
/// values a card with room takes the upper. Beyond him: effects, scattered entities, film grain, vignette, lens
/// distortion, HDR, frame generation and its target, none of which he measures or sets. What Exo picks from those follows
/// this PC: the card and its series, its
/// video memory, and the resolution and refresh the file already has. A 20 or 30 series card gets
/// DLSS at 2x (4x and a target past the panel are what ghost). A 40 or 50 gets Dynamic aimed at the
/// screen. The NVIDIA profile is the fixed-refresh one: G-SYNC off, no driver frame cap, and V-Sync left
/// to the game, so Optimize does not turn V-Sync off or add a cap. The window mode is the one chosen on the line, Borderless
/// by default. On an ultrawide screen, cutscenes
/// use the whole width. Ray tracing and path tracing go off under 12 GB; on a card with room they stay as you have them
/// (he says only with the performance and memory to spare).
/// </summary>
internal static partial class GameSettings
{
    internal static string ControlResonantConfig =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remedy", "CONTROLResonant", "renderer.ini");

    internal static bool IsControlResonant(string path) => File.Exists(Path.Combine(path, "CONTROLResonant.exe"));

    private const string Bool = "one of true, false";

    private static readonly Dictionary<string, string> ControlKeys = new(StringComparer.Ordinal)
    {
        ["m_iOutputResolutionX"] = "1 to 10000",
        ["m_iOutputResolutionY"] = "1 to 10000",
        ["m_eWindowMode"] = "0 to 2",
        ["m_bUnlockAspectRatio"] = Bool,
        ["m_eVolumeLightQuality"] = "0 to 2",
        ["m_eVolumetricSpotLightQuality"] = "0 to 2",
        ["m_eShadowResolutionQuality"] = "0 to 2",
        ["m_eShadowFilteringQuality"] = "0 to 1",
        ["m_eShadowDetailQuality"] = "0 to 2",
        ["m_eTextureResolution"] = "0 to 3",
        ["m_eTextureFilteringQuality"] = "0 to 2",
        ["m_eSSAOQuality"] = "0 to 1",
        ["m_eSSRQuality"] = "0 to 3",
        ["m_eEffectQuality"] = "0 to 3",
        ["m_eScatteredEntityQuantity"] = "0 to 3",
        ["m_eMotionBlur"] = "0 to 1",
        ["m_eGIQuality"] = "0 to 2",
        ["m_eRTReflectionQuality"] = "0 to 3",
        ["m_eRTTransparentQuality"] = "0 to 3",
        ["m_eRTIndirectDiffuseQuality"] = "0 to 2",
        ["m_eRTReSTIRQuality"] = "0 to 3",
        ["m_eRTDebrisQuality"] = "0 to 1",
        ["m_ePTQuality"] = "0 to 3",
        ["m_eSDFQuality"] = "0 to 2",
        ["m_eFogQuality"] = "0 to 1",
        ["m_eSSAAMethod"] = "0 to 3",
        ["m_eHighQualityPost"] = "0 to 1",
        ["m_eLODDetail"] = "0 to 2",
        ["m_eFSRFGMode"] = "0 to 1",
        ["m_eDLSSFGMode"] = "0 to 6",
        ["m_eHdrQuality"] = "0 to 1",
        ["m_maxFrameRate"] = "30 to 1000",
        ["m_fDLSSFGTargetFrameRate"] = "30 to 1000",
        ["m_bFilmGrain"] = Bool,
        ["m_bVignette"] = Bool,
        ["m_bLensDistortion"] = Bool,
        ["m_bDLSSLegacy"] = Bool,
        ["m_DLSSRR"] = Bool,
    };

    [GeneratedRegex("\"(?<name>[A-Za-z_][A-Za-z0-9_]*)\"\\s*:\\s*(?<value>true|false|-?\\d+(?:\\.\\d+)?)")]
    private static partial Regex JsonSetting();

    /// <summary>The JSON keys <paramref name="keys"/> names, each allowed what its entry says. Every other line stays as it is.</summary>
    internal static SettingsFile ParseJson(byte[] bytes, IReadOnlyDictionary<string, string> keys)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newLine);
        var settings = new List<Line>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (JsonSetting().Match(lines[i]) is not { Success: true } m || !keys.TryGetValue(m.Groups["name"].Value, out var allowed)) continue;
            var value = m.Groups["value"];
            settings.Add(new Line(i, m.Groups["name"].Value, m.Groups["name"].Value, value.Value, AllowedOn(allowed, value.Value), value.Index, value.Length));
        }
        return new SettingsFile(lines, newLine, bom, settings);
    }

    internal static SettingsFile ParseControl(byte[] bytes) => ParseJson(bytes, ControlKeys);

    internal static Profile ControlResonant(Pc pc) =>
        new("control-resonant", "CONTROL Resonant", ControlResonantConfig, file => ControlGroups(pc, file), ["CONTROLResonant"], Format: ParseControl, WindowMode: pc.Fullscreen ? Fullscreen : Borderless, WindowLines: ["m_eWindowMode"]);

    /// <summary>Output height the file already has, else 1080.</summary>
    private static int ControlHeight(SettingsFile file) =>
        int.TryParse(ValueOf(file, "m_iOutputResolutionY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) && height > 0 ? height : 1080;

    /// <summary>Ultrawide. Cutscene bars are a 16:9 frame on that screen; the game's own switch removes them.</summary>
    private static bool ControlWide(SettingsFile file)
    {
        if (!int.TryParse(ValueOf(file, "m_iOutputResolutionX"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width <= 0) return false;
        return width / (double)ControlHeight(file) > 2;
    }

    /// <summary>12 GB, or an RTX 40 or newer: room for the higher steps. An 8 GB RTX 30 is not.</summary>
    private static bool ControlHeadroom(Pc pc, int? series) => pc.VideoMemoryGb >= 12 || series >= 40;

    private static IReadOnlyList<Group> ControlGroups(Pc pc, SettingsFile file)
    {
        var height = ControlHeight(file);
        var series = pc.Gpu is { } gpu && pc.Vendor == "nvidia" ? GpuProfile.NvidiaSeries(gpu) : null;
        var room = ControlHeadroom(pc, series);
        List<Group> groups = [];
        var window = pc.Fullscreen ? "Fullscreen" : "Borderless";
        Change mode = new("m_eWindowMode", pc.Fullscreen ? "2" : "1");
        groups.Add(pc.TopRefresh >= 30
            ? new("frames", window + ", frame generation aimed at this screen",
                [mode, new("m_fDLSSFGTargetFrameRate", pc.TopRefresh.ToString(CultureInfo.InvariantCulture))])
            : new("frames", window, [mode]));
        if (ControlWide(file))
            groups.Add(new("wide", "Cutscenes use the whole screen", [new("m_bUnlockAspectRatio", "true")]));
        if (pc.Vendor == "nvidia")
            groups.Add(new("upscaler", "DLSS for this card", [new("m_eSSAAMethod", "2"), new("m_bDLSSLegacy", "false"), new("m_DLSSRR", series >= 40 ? "true" : "false")]));
        else if (pc.Vendor == "amd")
            groups.Add(new("upscaler", "FSR for this card", [new("m_eSSAAMethod", "3"), new("m_DLSSRR", "false")]));

        if (pc.Vendor == "nvidia")
            groups.Add(new("framegen", series >= 40 ? "Frame generation aimed at this screen" : "Frame generation at 2x",
                [new("m_eFSRFGMode", "0"), new("m_eDLSSFGMode", series >= 40 ? "6" : "1")]));
        else if (pc.Vendor == "amd")
            groups.Add(new("framegen", "FSR frame generation", [new("m_eFSRFGMode", "1"), new("m_eDLSSFGMode", "0")]));

        if (pc.VideoMemoryGb is { } memory)
            groups.Add(new("memory", "Textures for this card's memory",
                [new("m_eTextureResolution", ControlTextures(memory, height)), new("m_eTextureFilteringQuality", "2")]));

        groups.Add(new("picture", room ? "Detail up, blur off" : "The costly effects down",
        [
            new("m_eMotionBlur", "0"),
            new("m_bFilmGrain", "false"),
            new("m_bVignette", "false"),
            new("m_bLensDistortion", "false"),
            new("m_eSSAOQuality", "1"),
            new("m_eLODDetail", "2"),
            new("m_eGIQuality", "2"),
            new("m_eSSRQuality", room ? "2" : "1"),
            new("m_eSDFQuality", "2"),
            new("m_eVolumeLightQuality", room ? "1" : "0"),
            new("m_eVolumetricSpotLightQuality", "1"),
            new("m_eFogQuality", "0"),
            new("m_eShadowResolutionQuality", room ? "2" : "1"),
            new("m_eShadowFilteringQuality", "1"),
            new("m_eShadowDetailQuality", "2"),
            new("m_eEffectQuality", room ? "2" : "1"),
            new("m_eScatteredEntityQuantity", room ? "3" : "2"),
            new("m_eHighQualityPost", "0"),
            new("m_eHdrQuality", pc.HdrPeak is null ? "0" : "1"),
        ]));

        if (!room)
            groups.Add(new("ray", "Ray tracing off",
            [
                new("m_eRTReflectionQuality", "0"),
                new("m_eRTTransparentQuality", "0"),
                new("m_eRTIndirectDiffuseQuality", "0"),
                new("m_eRTReSTIRQuality", "0"),
                new("m_eRTDebrisQuality", "0"),
                new("m_ePTQuality", "0"),
            ]));
        return groups;
    }

    /// <summary>
    /// Texture budget, BenchmarKing's: on 8 GB at 1440p Low runs clean, Medium sits at the limit and High or Ultra fill the
    /// card, so Low "especially with frame generation", which Optimize turns on. 1080p leaves room for his other 8 GB choice,
    /// Medium. On 12 GB, Medium once frame generation or ray tracing is on. Past that he has no card: Exo's own 4G, 2G at 4K.
    /// </summary>
    private static string ControlTextures(double gb, int height) => gb switch
    {
        <= 6 => "0",
        <= 10 when height >= 1400 => "0",
        <= 12.5 => "1",
        _ when height >= 2000 => "2",
        _ => "3",
    };
}

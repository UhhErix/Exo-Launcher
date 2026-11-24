using System.Globalization;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static partial class GameSettings
{
    private const string RainbowSixSiegeId = "r6siege";
    private const string RainbowSixSiegeSteamId = "steam:359550";

    internal static string RainbowSixSiegeConfigRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Rainbow Six - Siege");

    private static readonly Dictionary<string, string> RainbowSixSiegeKeys = new(StringComparer.Ordinal)
    {
        ["WindowMode"] = "one of 0, 1, 2",
        ["VSync"] = "one of 0, 1, 2",
        ["FPSLimit"] = "0 to 1000",
        ["UseLetterbox"] = "one of 0, 1",
        ["NvidiaReflex"] = "one of 0, 1, 2",
        ["AntiAliasingMethod"] = "one of 0, 1, 2, 3",
        ["TAARenderScaling"] = "25 to 100",
        ["TAASharpenFactor"] = "0 to 1",
        ["DLSS"] = "one of 0, 1, 2, 3, 4, 5",
        ["TextureQuality"] = "one of 0, 1, 2, 3",
        ["TextureFiltering"] = "one of 0, 1, 2, 3, 4",
        ["LODQuality"] = "one of 0, 1, 2, 3",
        ["ShadingQuality"] = "one of 0, 1, 2",
        ["ShadowQuality"] = "one of 0, 1, 2, 3, 4",
        ["ReflectionQuality"] = "one of 0, 1, 2",
        ["AmbientOcclusion"] = "one of 0, 1, 2",
        ["LensEffects"] = "one of 0, 1, 2",
        ["ZoomInDepthOfField"] = "one of 0, 1",
    };

    private static readonly string[] RainbowSixSiegeSections =
    [
        "DISPLAY_SETTINGS",
        "DISPLAY",
        "CUSTOM_PRESET_SETTINGS",
        "GRAPHICS",
    ];

    internal static bool IsRainbowSixSiege(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return false;
        if (game.Id.Equals(RainbowSixSiegeSteamId, StringComparison.OrdinalIgnoreCase)) return true;
        return RainbowSixSiegeProgram(game) is not null;
    }

    internal static string? RainbowSixSiegeProgram(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return null;
        string[] candidates =
        [
            Path.Combine(game.Path, "RainbowSix.exe"),
            Path.Combine(game.Path, "RainbowSix_DX11.exe"),
            Path.Combine(game.Path, "RainbowSix_DX12.exe"),
            Path.Combine(game.Path, "RainbowSix_Vulkan.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static IReadOnlyList<string> RainbowSixSiegeConfigFiles(string? root = null)
    {
        var baseDir = root ?? RainbowSixSiegeConfigRoot;
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return [];
        try
        {
            var found = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(baseDir))
            {
                var info = new DirectoryInfo(dir);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var ini = Path.Combine(dir, "GameSettings.ini");
                if (File.Exists(ini)) found.Add(ini);
            }
            return [.. found.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static SettingsFile ParseRainbowSixSiege(byte[] bytes) =>
        ParseIni(bytes, RainbowSixSiegeSections, RainbowSixSiegeKeys);

    internal static Profile RainbowSixSiege(Pc pc, string? configRoot = null)
    {
        var files = RainbowSixSiegeConfigFiles(configRoot);
        var primary = files.Count > 0 ? files[0] : Path.Combine(configRoot ?? RainbowSixSiegeConfigRoot, "GameSettings.ini");
        Func<IReadOnlyList<string>>? extras = () =>
            [.. RainbowSixSiegeConfigFiles(configRoot).Where(path => !string.Equals(path, primary, StringComparison.OrdinalIgnoreCase))];
        return new(
            RainbowSixSiegeId,
            "Rainbow Six Siege",
            primary,
            file => RainbowSixSiegeGroups(pc, file),
            ["RainbowSix", "RainbowSix_DX11", "RainbowSix_DX12", "RainbowSix_Vulkan", "RainbowSix_BE"],
            PlayerFiles: extras,
            Format: ParseRainbowSixSiege,
            Program: RainbowSixSiegeProgram,
            WindowMode: pc.Fullscreen ? Fullscreen : Borderless,
            WindowLines: ["WindowMode"],
            Advice: _ => RainbowSixSiegeAdvice(pc));
    }

    internal static IReadOnlyList<Group> RainbowSixSiegeGroups(Pc pc, SettingsFile? file = null)
    {
        var strong = Strong(pc.Gpu);
        var lowVram = pc.VideoMemoryGb is <= 4.5;
        List<Group> groups =
        [
            new("display", pc.Fullscreen ? "Fullscreen without letterbox" : "Borderless without letterbox",
            [
                new("WindowMode", pc.Fullscreen ? "0" : "2"),
                new("UseLetterbox", "0", Spare: true),
            ]),
        ];

        List<Change> latencyChanges = [new("VSync", "0")];
        var latencyLabel = "V-Sync off";
        if (string.Equals(pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase))
        {
            latencyChanges.Add(new("NvidiaReflex", strong ? "1" : "2", Spare: true));
            latencyLabel = strong ? "Reflex on, V-Sync off" : "Reflex + Boost on, V-Sync off";
        }
        groups.Add(new("latency", latencyLabel, latencyChanges));

        var useTaaScaling = lowVram && !strong;
        List<Change> aaChanges = useTaaScaling
            ? [new("AntiAliasingMethod", "1"), new("TAARenderScaling", "85"), new("TAASharpenFactor", "0.5", Spare: true)]
            : [new("AntiAliasingMethod", "0"), new("TAARenderScaling", "100"), new("TAASharpenFactor", "0.5", Spare: true)];
        if (string.Equals(pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(pc.Gpu) && GeForceRtx().IsMatch(pc.Gpu))
            aaChanges.Add(new("DLSS", "0", Spare: true));
        groups.Add(new("antialiasing", useTaaScaling ? "T-AA at 85% render scale for low-VRAM GPUs" : "T-AA off at 100% native render scale", aaChanges));

        var textureQuality = pc.VideoMemoryGb switch
        {
            <= 4.5 => "0",
            <= 8.5 => "1",
            _ => "2",
        };
        var filtering = pc.VideoMemoryGb is >= 2 ? "4" : "2";
        groups.Add(new("quality", "Competitive visibility (Medium shadows, Ultra LOD)",
        [
            new("TextureQuality", textureQuality),
            new("TextureFiltering", filtering),
            new("LODQuality", "3"),
            new("ShadingQuality", "0"),
            new("ShadowQuality", "1"),
            new("ReflectionQuality", "0"),
            new("AmbientOcclusion", "0"),
            new("LensEffects", "0"),
            new("ZoomInDepthOfField", "0"),
        ]));

        if (VrrFrameCap(pc) is { } cap)
            groups.Add(new("framecap", $"Frame cap at {cap} FPS for VRR", [new("FPSLimit", cap.ToString(CultureInfo.InvariantCulture))]));

        return groups;
    }

    internal static IReadOnlyList<string> RainbowSixSiegeAdvice(Pc pc)
    {
        List<string> notes =
        [
            "Ubisoft retired the legacy Vulkan launch option in current Siege builds in favor of DX12 and DX11; use DX11 if DX12 stutters, or Vulkan on older builds that still expose it.",
            "Shadows stay at Medium and LOD at Ultra for competitive enemy silhouettes and floor shadows; you can keep any individual setting as it was.",
        ];
        if (string.Equals(pc.Vendor, "amd", StringComparison.OrdinalIgnoreCase))
            notes.Add("NVIDIA Reflex is NVIDIA-only; enable Radeon Anti-Lag in AMD Software for Siege.");
        else if (string.Equals(pc.Vendor, "intel", StringComparison.OrdinalIgnoreCase))
            notes.Add("NVIDIA Reflex is NVIDIA-only; enable Low Latency Mode in Intel Graphics Software for Siege.");
        return notes;
    }
}

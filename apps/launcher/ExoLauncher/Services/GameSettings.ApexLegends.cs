using System.Globalization;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static partial class GameSettings
{
    private const string ApexLegendsId = "apex";
    private const string ApexLegendsSteamId = "steam:1172470";

    internal static string ApexLegendsConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Respawn", "Apex", "local");

    private static readonly Dictionary<string, string> ApexLegendsKeys = new(StringComparer.Ordinal)
    {
        ["setting.fullscreen"] = "one of 0, 1",
        ["setting.nowindowborder"] = "one of 0, 1",
        ["setting.mat_vsync_mode"] = "one of 0, 1, 2, 3",
        ["setting.mat_backbuffer_count"] = "one of 1, 2, 3",
        ["setting.nv_reflex"] = "one of 0, 1, 2",
        ["setting.stream_memory"] = "0 to 16000000",
        ["setting.mat_forceaniso"] = "one of 1, 2, 4, 8, 16",
        ["setting.dvs_enable"] = "one of 0, 1",
        ["setting.mat_antialias_mode"] = "one of 0, 12",
        ["setting.csm_enabled"] = "one of 0, 1",
        ["setting.csm_coverage"] = "one of 1, 2",
        ["setting.csm_cascade_res"] = "one of 512, 1024, 2048",
        ["setting.shadow_enable"] = "one of 0, 1",
        ["setting.shadow_maxdynamic"] = "0 to 4",
        ["setting.ssao_enabled"] = "one of 0, 1",
        ["setting.volumetric_lighting"] = "one of 0, 1",
        ["setting.volumetric_fog"] = "one of 0, 1",
        ["setting.mat_disable_bloom"] = "one of 0, 1",
        ["setting.r_lod_switch_scale"] = "0.6 to 1.0",
        ["setting.ragdoll_maxcount"] = "0 to 8",
        ["setting.cl_gib_allow"] = "one of 0, 1",
    };

    internal static bool IsApexLegends(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return false;
        if (game.Id.Equals(ApexLegendsSteamId, StringComparison.OrdinalIgnoreCase)) return true;
        return ApexLegendsProgram(game) is not null;
    }

    internal static string? ApexLegendsProgram(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return null;
        string[] candidates =
        [
            Path.Combine(game.Path, "r5apex.exe"),
            Path.Combine(game.Path, "r5apex_dx12.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static SettingsFile ParseApexLegends(byte[] bytes) =>
        ParseLines(bytes, ValveLine(), ApexLegendsKeys);

    internal static Profile ApexLegends(Pc pc, string? configFile = null, string? launchOptions = null) =>
        new(
            ApexLegendsId,
            "Apex Legends",
            configFile ?? Path.Combine(ApexLegendsConfigDir, "videoconfig.txt"),
            _ => ApexLegendsGroups(pc),
            ["r5apex", "r5apex_dx12", "EasyAntiCheat_launcher", "start_protected_game"],
            Format: ParseApexLegends,
            Program: ApexLegendsProgram,
            WindowMode: pc.Fullscreen ? Fullscreen : Borderless,
            WindowLines: ["setting.fullscreen", "setting.nowindowborder"],
            Advice: _ => ApexLegendsAdvice(pc, launchOptions));

    internal static string ApexStreamMemoryKb(double? vramGb) => vramGb switch
    {
        <= 3.5 => "0",
        <= 4.5 => "2000000",
        <= 6.5 => "3000000",
        <= 8.5 => "4000000",
        < 12 => "6000000",
        _ => "8000000",
    };

    internal static IReadOnlyList<Group> ApexLegendsGroups(Pc pc)
    {
        var strong = Strong(pc.Gpu);
        List<Group> groups =
        [
            new("display", pc.Fullscreen ? "Fullscreen" : "Borderless windowed",
                pc.Fullscreen
                    ? [new("setting.fullscreen", "1"), new("setting.nowindowborder", "0")]
                    : [new("setting.fullscreen", "0"), new("setting.nowindowborder", "1")]),
        ];

        List<Change> latencyChanges =
        [
            new("setting.mat_vsync_mode", "0"),
            new("setting.mat_backbuffer_count", "1", Spare: true),
        ];
        var latencyLabel = "V-Sync off, minimal frame buffering";
        if (string.Equals(pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase))
        {
            latencyChanges.Add(new("setting.nv_reflex", strong ? "1" : "2", Spare: true));
            latencyLabel = strong ? "Reflex on, V-Sync off" : "Reflex + Boost on, V-Sync off";
        }
        groups.Add(new("latency", latencyLabel, latencyChanges));

        List<Change> memoryChanges = [new("setting.stream_memory", ApexStreamMemoryKb(pc.VideoMemoryGb))];
        if (pc.VideoMemoryGb is >= 2) memoryChanges.Add(new("setting.mat_forceaniso", "16"));
        groups.Add(new("memory", "Texture streaming budget scaled to your VRAM", memoryChanges));

        groups.Add(new("resolution", "Dynamic resolution and TSAA off",
        [
            new("setting.dvs_enable", "0"),
            new("setting.mat_antialias_mode", "0"),
        ]));

        var lodScale = (strong || pc.X3d) ? "1.0" : pc.VideoMemoryGb is <= 4.5 ? "0.6" : "0.8";
        groups.Add(new("effects", "Competitive shadows, fog and effects",
        [
            new("setting.csm_enabled", "0"),
            new("setting.csm_coverage", "1"),
            new("setting.csm_cascade_res", "512"),
            new("setting.shadow_enable", "0"),
            new("setting.shadow_maxdynamic", "0"),
            new("setting.ssao_enabled", "0"),
            new("setting.volumetric_lighting", "0"),
            new("setting.volumetric_fog", "0"),
            new("setting.mat_disable_bloom", "1", Spare: true),
            new("setting.r_lod_switch_scale", lodScale),
            new("setting.ragdoll_maxcount", "0", Spare: true),
            new("setting.cl_gib_allow", "0", Spare: true),
        ]));

        return groups;
    }

    internal static bool LaunchOptionsReferenceAutoexec(string? launchOptions) =>
        !string.IsNullOrWhiteSpace(launchOptions)
        && launchOptions.Contains("+exec", StringComparison.OrdinalIgnoreCase)
        && launchOptions.Contains("autoexec", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> ApexLegendsAdvice(Pc pc, string? launchOptions = null)
    {
        List<string> notes = [];
        if (VrrFrameCap(pc) is { } cap)
            notes.Add($"Add +fps_max {cap.ToString(CultureInfo.InvariantCulture)} -dev in Steam or EA launch options for a VRR frame cap and faster startup.");
        else
            notes.Add("Optional safe launch options: -dev skips intro videos; add +fps_max <cap> only if you want an in-game cap.");
        if (LaunchOptionsReferenceAutoexec(launchOptions))
            notes.Add("Your launch options reference autoexec.cfg; Exo leaves install-folder cfg files untouched under EAC. Keep only standard cvars and never use movement or superglide cfg chains.");
        else
            notes.Add("Exo only edits videoconfig.txt and never creates or modifies autoexec.cfg inside the EAC-protected install folder; avoid movement cfg scripts.");
        if (string.Equals(pc.Vendor, "amd", StringComparison.OrdinalIgnoreCase))
            notes.Add("Use Radeon Anti-Lag in AMD Software; avoid driver LOD stripping.");
        else if (string.Equals(pc.Vendor, "intel", StringComparison.OrdinalIgnoreCase))
            notes.Add("Use Intel Low Latency Mode in Intel Graphics Software.");
        return notes;
    }
}

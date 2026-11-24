using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static partial class GameSettings
{
    private const string Overwatch2Id = "overwatch2";
    private const string Overwatch2SteamId = "steam:2357570";

    internal static string Overwatch2ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Overwatch", "Settings", "Settings_v0.ini");

    [GeneratedRegex(@"^Render(?:\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex OverwatchRenderSection();

    [GeneratedRegex(@"^[ \t]*(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*""(?<value>[^""]*)""[ \t]*$")]
    private static partial Regex OverwatchQuotedIniLine();

    private static readonly Dictionary<string, string> Overwatch2Keys = new(StringComparer.Ordinal)
    {
        ["WindowMode"] = "one of 0, 1, 2",
        ["VSync"] = "one of 0, 1",
        ["TripleBuffering"] = "one of 0, 1",
        ["ReflexMode"] = "one of 0, 1, 2",
        ["ReducedBuffering"] = "one of 0, 1",
        ["DynamicRenderScale"] = "one of 0, 1",
        ["RenderScale"] = "0 to 200",
        ["HighQualityUpsampling"] = "one of 0, 1, 2, 3",
        ["FrameRateCap"] = "30 to 600",
        ["UseCustomFrameRateCap"] = "one of 0, 1",
        ["LocalReflections"] = "one of 0, 1",
        ["LocalFogDetail"] = "one of 0, 1, 2, 3, 4",
        ["DynamicReflections"] = "one of 0, 1, 2, 3",
        ["ShadowDetail"] = "one of 0, 1, 2, 3, 4",
        ["ModelQuality"] = "one of 0, 1, 2, 3, 4",
        ["ModelDetail"] = "one of 0, 1, 2, 3, 4",
        ["EffectsQuality"] = "one of 0, 1, 2, 3, 4",
        ["EffectsDetail"] = "one of 0, 1, 2, 3, 4",
        ["TextureDetail"] = "one of 1, 2, 3",
        ["TextureFilterQuality"] = "one of 0, 1, 2, 3, 4",
        ["RefractionDetail"] = "one of 0, 1, 2",
        ["SSAO"] = "one of 0, 1, 2, 3",
    };

    internal static bool IsOverwatch2(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return false;
        if (game.Id.Equals(Overwatch2SteamId, StringComparison.OrdinalIgnoreCase)) return true;
        if (Overwatch2Program(game) is not null) return true;
        var title = StoreService.TitleKey(game.Title ?? "");
        return title is "overwatch" or "overwatch2";
    }

    internal static string? Overwatch2Program(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return null;
        string[] candidates =
        [
            Path.Combine(game.Path, "Overwatch.exe"),
            Path.Combine(game.Path, "_retail_", "Overwatch.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static SettingsFile ParseOverwatch2(byte[] bytes)
    {
        var latin1 = Encoding.Latin1;
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..]) return new SettingsFile([latin1.GetString(bytes)], "\r\n", false, [], latin1);
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var text = latin1.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newLine);
        var settings = new List<Line>();
        var live = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                live = OverwatchRenderSection().IsMatch(trimmed[1..^1].Trim());
                continue;
            }
            if (!live || OverwatchQuotedIniLine().Match(lines[i]) is not { Success: true } m || !Overwatch2Keys.TryGetValue(m.Groups["name"].Value, out var allowed)) continue;
            var value = m.Groups["value"];
            settings.Add(new Line(i, m.Groups["name"].Value, m.Groups["name"].Value, value.Value, allowed, value.Index, value.Length));
        }
        return new SettingsFile(lines, newLine, bom, settings, latin1);
    }

    internal static Profile Overwatch2(Pc pc, string? configFile = null) =>
        new(
            Overwatch2Id,
            "Overwatch 2",
            configFile ?? Overwatch2ConfigPath,
            file => Overwatch2Groups(pc, file),
            ["Overwatch"],
            Format: ParseOverwatch2,
            Program: Overwatch2Program,
            WindowMode: pc.Fullscreen ? Fullscreen : Borderless,
            WindowLines: ["WindowMode"],
            Advice: _ => Overwatch2Advice(pc));

    internal static IReadOnlyList<Group> Overwatch2Groups(Pc pc, SettingsFile? file = null)
    {
        var strong = Strong(pc.Gpu);
        List<Group> groups =
        [
            new("display", pc.Fullscreen ? "Fullscreen" : "Borderless windowed",
                [new("WindowMode", pc.Fullscreen ? "0" : "2")]),
        ];

        List<Change> latencyChanges =
        [
            new("VSync", "0", Spare: true),
            new("TripleBuffering", "0", Spare: true),
            new("ReducedBuffering", "1", Spare: true),
        ];
        var latencyLabel = "Reduced Buffering on, V-Sync off";
        if (string.Equals(pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase))
        {
            latencyChanges.Add(new("ReflexMode", strong ? "1" : "2"));
            latencyLabel = strong ? "Reflex on, Reduced Buffering on, V-Sync off" : "Reflex + Boost on, Reduced Buffering on, V-Sync off";
        }
        groups.Add(new("latency", latencyLabel, latencyChanges));

        var existingScale = file is null ? null : ValueOf(file, "RenderScale");
        var renderScaleTarget = int.TryParse(existingScale, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 50 ? "100" : "0";
        groups.Add(new("scale", "Fixed 100% native render scale",
        [
            new("DynamicRenderScale", "0"),
            new("RenderScale", renderScaleTarget),
            new("HighQualityUpsampling", "0"),
        ]));

        var textureDetail = pc.VideoMemoryGb switch
        {
            <= 4.5 => "1",
            <= 8.5 => "2",
            _ => "3",
        };
        var modelTier = strong ? "2" : "1";
        List<Change> effectChanges =
        [
            new("LocalReflections", "0"),
            new("LocalFogDetail", "1"),
            new("DynamicReflections", "0", Spare: true),
            new("ShadowDetail", "1", Spare: true),
            new("ModelQuality", modelTier, Spare: true),
            new("ModelDetail", modelTier, Spare: true),
            new("EffectsQuality", "1", Spare: true),
            new("EffectsDetail", "1", Spare: true),
            new("TextureDetail", textureDetail, Spare: true),
            new("RefractionDetail", "0", Spare: true),
            new("SSAO", "0", Spare: true),
        ];
        if (pc.VideoMemoryGb is >= 2) effectChanges.Add(new("TextureFilterQuality", "4", Spare: true));
        groups.Add(new("effects", "Competitive clarity and low-cost effects", effectChanges));

        if (VrrFrameCap(pc) is { } cap)
        {
            var clamped = Math.Clamp(cap, 30, 600).ToString(CultureInfo.InvariantCulture);
            groups.Add(new("framecap", $"Frame cap at {clamped} FPS for VRR",
            [
                new("UseCustomFrameRateCap", "1", Spare: true),
                new("FrameRateCap", clamped),
            ]));
        }

        return groups;
    }

    internal static IReadOnlyList<string> Overwatch2Advice(Pc pc)
    {
        List<string> notes =
        [
            "Only standard menu-equivalent values in Settings_v0.ini are changed; hero outlines, shields and map geometry stay untouched.",
        ];
        if (string.Equals(pc.Vendor, "amd", StringComparison.OrdinalIgnoreCase))
            notes.Add("NVIDIA Reflex is NVIDIA-only; Overwatch 2 uses Reduced Buffering on AMD GPUs.");
        else if (string.Equals(pc.Vendor, "intel", StringComparison.OrdinalIgnoreCase))
            notes.Add("NVIDIA Reflex is NVIDIA-only; Overwatch 2 uses Reduced Buffering on Intel GPUs.");
        return notes;
    }
}

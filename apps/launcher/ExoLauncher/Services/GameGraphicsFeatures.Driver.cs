using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static partial class GameGraphicsFeatures
{
    private static bool HasNvidiaDlssOverride(GameRecord? record) =>
        record is not null && record.NvidiaSet.ContainsKey(GraphicsCatalog.SrOverride);

    private static bool HasNvidiaLowLatencyOverride(GameRecord? record) =>
        record is not null && (record.NvidiaSet.ContainsKey(GraphicsCatalog.LowLatencyState)
            || record.NvidiaSet.ContainsKey(GraphicsCatalog.PreRenderedFrames)
            || record.NvidiaSet.ContainsKey(GraphicsCatalog.FrameRateLimit));

    private static bool HasNvidiaHiddenOverride(GameRecord? record) =>
        record is not null && (record.NvidiaSet.ContainsKey(GraphicsCatalog.AnisotropicFilteringMode)
            || record.NvidiaSet.ContainsKey(GraphicsCatalog.PreferredRefreshRate)
            || record.NvidiaSet.ContainsKey(GpuProfile.PowerManagement));

    private static bool IsRadeonAntiLagActive()
    {
        try
        {
            if (!string.Equals(GraphicsService.Vendor(), "amd", StringComparison.OrdinalIgnoreCase)) return false;
            var driverState = AmdAdlx.Driver.Read();
            return driverState is not null && driverState.Values.TryGetValue(AmdAdlx.Keys.AntiLag, out var val) && val == 1;
        }
        catch
        {
            return false;
        }
    }

    private static string? RestoreNvidiaOverrides(GameRecord record)
    {
        if (record.NvidiaSet.Count == 0 || string.IsNullOrEmpty(record.NvidiaProgramExe) || !NvidiaProfiles.GameProfilesUsable)
        {
            record.NvidiaSet.Clear();
            record.NvidiaBefore.Clear();
            return null;
        }

        var writes = new Dictionary<uint, uint?>();
        foreach (var id in record.NvidiaSet.Keys)
            writes[id] = record.NvidiaBefore.GetValueOrDefault(id);

        var program = MakeGameProgram(record.NvidiaProgramExe, record.NvidiaTitle ?? Path.GetFileNameWithoutExtension(record.NvidiaProgramExe));
        var result = NvidiaProfiles.ForGameLive(program, writes.Keys.ToList(), writes);
        if (!result.Ok) return result.Message ?? "Could not restore the NVIDIA game profile.";

        record.NvidiaSet.Clear();
        record.NvidiaBefore.Clear();
        return null;
    }

    private static string? ApplyTargetDriverOverrides(
        GameEntry game,
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> targetFeatures)
    {
        if (!string.Equals(ctx.Pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase)) return null;
        if (!NvidiaProfiles.GameProfilesUsable || string.IsNullOrEmpty(d.ProgramPath)) return null;
        if (!AllowsConfigOrDriver(game, ctx.AntiCheat)) return null;

        var series = GpuProfile.NvidiaSeries(ctx.Pc.Gpu ?? "") ?? 0;
        var variant = new GpuProfile.Variant(series);
        var wanted = new Dictionary<uint, uint>();

        if (targetFeatures.Contains(FeatureUpscalers) && variant.Rtx && (d.HasDlss || d.Engine == EngineKind.WutheringWaves))
        {
            wanted[GpuProfile.ModelPresetProfile] = 2u;
            wanted[GraphicsCatalog.SrOverride] = 1u;
            wanted[GraphicsCatalog.SrPreset] = variant.Fp8 ? GpuProfile.Recommended : GpuProfile.PresetK;
            wanted[GraphicsCatalog.RrOverride] = 1u;
            wanted[GraphicsCatalog.RrPreset] = variant.Fp8 ? GpuProfile.Recommended : GpuProfile.PresetF;
            wanted[GraphicsCatalog.StreamlineDlssOverride] = 1u;
        }

        if (targetFeatures.Contains(FeatureReflex))
        {
            wanted[GraphicsCatalog.LowLatencyState] = 2u;
            wanted[GraphicsCatalog.UltraLowLatency] = 1u;
            wanted[GraphicsCatalog.PreRenderedFrames] = 1u;
            if (!d.HasReflex && ctx.Pc.TopRefresh >= 60)
                wanted[GraphicsCatalog.FrameRateLimit] = (uint)(ctx.Pc.TopRefresh - 3);
        }

        if (targetFeatures.Contains(FeatureHiddenSettings) && d.Engine is EngineKind.Generic or EngineKind.Unreal3)
        {
            wanted[GraphicsCatalog.AnisotropicFilteringMode] = 1u;
            wanted[GpuProfile.TextureFilteringQuality] = 0u;
            wanted[GraphicsCatalog.PreferredRefreshRate] = 1u;
            wanted[GpuProfile.PowerManagement] = 1u;
            wanted[GraphicsCatalog.ResizableBarMode] = 2u;
        }

        if (wanted.Count == 0) return null;

        var program = MakeGameProgram(d.ProgramPath, game.Title);
        var writes = wanted.ToDictionary(kv => kv.Key, kv => (uint?)kv.Value);
        var state = NvidiaProfiles.ForGameLive(program, writes.Keys.ToList(), writes);
        if (!state.Ok) return state.Message ?? "The NVIDIA driver refused the per-game profile.";

        var skipped = state.Skipped is { Count: > 0 } ? state.Skipped.ToHashSet() : [];
        var applied = wanted.Where(kv => !skipped.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (applied.Count == 0) return "This NVIDIA driver does not expose these per-game settings.";

        record.NvidiaProgramExe = d.ProgramPath;
        record.NvidiaTitle = game.Title;
        record.NvidiaSet = applied;
        record.NvidiaBefore = applied.Keys.ToDictionary(id => id, id => state.Before.GetValueOrDefault(id));
        return null;
    }

    private static NvidiaProfiles.GameProgram MakeGameProgram(string exe, string title) =>
        new(exe, title, "", (_, _) => true);

    private static void ApplyOptiScalerConfig(string optiIniPath, GameSettings.Pc pc, bool upscaler, bool reflex)
    {
        var isNvidia = string.Equals(pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase);
        var isAmd = string.Equals(pc.Vendor, "amd", StringComparison.OrdinalIgnoreCase);
        var upscalerChoice = isNvidia ? "dlss" : isAmd ? "fsr31" : "xess";

        var raw = File.Exists(optiIniPath) ? File.ReadAllBytes(optiIniPath) : [];
        var text = raw.Length == 0 ? "" : System.Text.Encoding.UTF8.GetString(raw);
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = raw.Length == 0 ? new List<string>() : [.. text.Split(nl)];

        if (upscaler)
        {
            SetIniKeyUnderSection(lines, "[Upscalers]", "Dx12Upscaler", upscalerChoice);
            SetIniKeyUnderSection(lines, "[Upscalers]", "Dx11Upscaler", upscalerChoice);
        }

        if (reflex)
        {
            SetIniKeyUnderSection(lines, "[NVAPI]", "OverrideNvapiDll", isNvidia ? "false" : "true");
            SetIniKeyUnderSection(lines, "[Reflex]", "ForceReflex", "2");
        }

        WriteBytesAtomic(optiIniPath, System.Text.Encoding.UTF8.GetBytes(string.Join(nl, lines)));
    }

    private static void SetIniKeyUnderSection(List<string> lines, string sectionHeader, string key, string value)
    {
        var sectionIdx = lines.FindIndex(l => l.Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase));
        if (sectionIdx < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add(sectionHeader);
            lines.Add($"{key}={value}");
            return;
        }

        var endIdx = lines.Count;
        for (var i = sectionIdx + 1; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']')) { endIdx = i; break; }
            var eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return;
            }
        }
        lines.Insert(endIdx, $"{key}={value}");
    }
}

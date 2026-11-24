using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Wuthering Waves (Kuro Games; Unreal Engine 4, DX11 / DX12, Anti-Cheat Expert).
/// Because Anti-Cheat Expert protects the install folder, Exo never places files in it
/// (<see cref="AntiCheatCheck.Category.Placed"/>). Safe optimization uses NVIDIA's signed NVAPI DRS driver profile
/// matched by <c>Client-Win64-Shipping.exe</c> alongside <c>Client-Win64-ShippingBase.dll</c>:
/// <list type="bullet">
/// <item>DLSS Super Resolution, Ray Reconstruction, Frame Generation, and Streamline overrides (<c>0x10E41E01</c>,
/// <c>0x10E41E02</c>, <c>0x10E41E03</c>, <c>0x10E41E06</c>) with DLSS 4 / 4.5 Transformer presets (<see cref="GpuProfile"/>:
/// Recommended on RTX 40/50 with FP8, Preset K SR + Preset F RR on RTX 20/30, Preset A FG).</item>
/// <item>Opt-in driver Smooth Motion (<see cref="GraphicsCatalog.SmoothMotion"/>, <c>0xB0D384C0</c>) on RTX 40/50 only
/// when the player opts in and is not using native DLSS Frame Generation.</item>
/// </list>
/// </summary>
internal static partial class GameSettings
{
    internal const string WutheringWavesId = "wuwa";
    internal const uint StreamlineDlssOverride = 0x10E41E06;

    private const uint CustomDlssModelPresetProfile = 2;
    private const string WutheringWavesExe = "Client-Win64-Shipping.exe";
    private const string WutheringWavesBaseDll = "Client-Win64-ShippingBase.dll";
    private const string WutheringWavesSteamId = "steam:3513350";
    private const string WutheringWavesLocalId = "local:wuthering-waves";

    /// <summary>
    /// Locates Wuthering Waves' main executable across Steam (<c>3513350</c>), Epic, Kuro launcher, and
    /// <c>local:wuthering-waves</c> installs; refuses unrelated UE games named <c>Client-Win64-Shipping.exe</c> unless
    /// <c>Client-Win64-ShippingBase.dll</c> is present or the library entry itself identifies Wuthering Waves.
    /// </summary>
    internal static string? WutheringWavesProgram(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.Path)) return null;
        var identified = IsWutheringWavesEntry(game);
        ReadOnlySpan<string> roots = [game.Path, Path.Combine(game.Path, "Wuthering Waves Game")];
        foreach (var root in roots)
        {
            var win64 = Path.Combine(root, "Client", "Binaries", "Win64");
            var exe = Path.Combine(win64, WutheringWavesExe);
            if (!File.Exists(exe)) continue;
            if (File.Exists(Path.Combine(win64, WutheringWavesBaseDll)) || identified) return exe;
        }
        return null;
    }

    internal static bool IsWutheringWaves(GameEntry game) => WutheringWavesProgram(game) is not null;

    /// <summary>Whether this game on this PC supports the opt-in RTX 40/50 Smooth Motion switch.</summary>
    internal static bool SupportsSmoothMotion(GameEntry game, Pc pc) =>
        IsWutheringWaves(game) && IsNvidiaRtx40Plus(pc);

    internal static bool IsWutheringWavesEntry(GameEntry game) =>
        game.Id.Equals(WutheringWavesSteamId, StringComparison.OrdinalIgnoreCase)
        || game.Id.Equals(WutheringWavesLocalId, StringComparison.OrdinalIgnoreCase)
        || StoreService.TitleKey(game.Title ?? "") == "wutheringwaves";

    private static bool IsNvidiaRtx40Plus(Pc pc) =>
        string.Equals(pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase)
        && GpuProfile.NvidiaSeries(pc.Gpu ?? "") >= 40;

    /// <summary>
    /// Signed NVAPI DRS values for Wuthering Waves for the given GeForce RTX series (<c>20..50+</c>). Returns empty for
    /// non-RTX cards. <see cref="GraphicsCatalog.SmoothMotion"/> is included only on explicit opt-in, RTX 40/50, and
    /// when not using native DLSS-G.
    /// </summary>
    internal static IReadOnlyList<NvidiaValue> WutheringWavesNvidiaValues(
        int series,
        bool smoothMotionOptIn = false,
        bool usingNativeDlssG = false)
    {
        var variant = new GpuProfile.Variant(series);
        if (!variant.Rtx) return [];

        List<NvidiaValue> values =
        [
            new(GpuProfile.ModelPresetProfile, CustomDlssModelPresetProfile, "DLSS model preset profile", "Custom"),
            new(GraphicsCatalog.SrOverride, 1u, "Use the newest DLSS Super Resolution", "On"),
            new(GraphicsCatalog.SrPreset, variant.Fp8 ? GpuProfile.Recommended : GpuProfile.PresetK,
                "DLSS Super Resolution preset", variant.Fp8 ? "Recommended" : "Preset K"),
            new(GraphicsCatalog.RrOverride, 1u, "Use the newest DLSS Ray Reconstruction", "On"),
            new(GraphicsCatalog.RrPreset, variant.Fp8 ? GpuProfile.Recommended : GpuProfile.PresetF,
                "DLSS Ray Reconstruction preset", variant.Fp8 ? "Recommended" : "Preset F"),
            new(GraphicsCatalog.FgOverride, 1u, "Use the newest DLSS Frame Generation", "On"),
            new(GraphicsCatalog.FgPreset, GpuProfile.PresetA, "DLSS Frame Generation preset", "Preset A"),
            new(StreamlineDlssOverride, 1u, "Use the newest Streamline for DLSS", "On"),
        ];

        if (smoothMotionOptIn && variant.Fp8 && !usingNativeDlssG)
            values.Add(new(GraphicsCatalog.SmoothMotion, 1u, "Smooth Motion", "On"));

        return values;
    }

    internal static Profile? WutheringWaves(
        Pc pc,
        GameEntry game,
        bool smoothMotionOptIn = false,
        bool usingNativeDlssG = false)
    {
        if (!string.Equals(pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase)) return null;
        if (GpuProfile.NvidiaSeries(pc.Gpu ?? "") is not { } series || series < 20) return null;
        var nvidia = new NvidiaGame(
            WutheringWavesBaseDll,
            _ => WutheringWavesNvidiaValues(series, smoothMotionOptIn, usingNativeDlssG));
        return new(
            WutheringWavesId,
            "Wuthering Waves",
            "",
            _ => [],
            ["Client-Win64-Shipping", "Wuthering Waves"],
            Nvidia: nvidia,
            Program: WutheringWavesProgram);
    }
}

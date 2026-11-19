using System.Text.RegularExpressions;
using Keys = ExoLauncher.Helpers.AmdAdlx.Keys;

namespace ExoLauncher.Helpers;

/// <summary>
/// Exo's Radeon profile: the global ADLX settings "Use Exo's profile" writes on an AMD GPU.
/// Pure, so every value is tested; docs/architecture/gpu-profiles.md › Radeon has the reasons.
/// - Chill, Boost, Radeon Super Resolution, Image Sharpening and Enhanced Sync off (Chill caps frames and turns
///   Anti-Lag and Boost off; Boost blurs motion; a game's own FSR beats RSR; sharpening every game adds halos to
///   what each game already sharpens; Enhanced Sync is known to stutter).
/// - Wait for Vertical Refresh "Off, unless application specifies" (AMD's default): V-Sync stays each game's,
///   as Fluid Motion Frames needs it off.
/// - FreeSync on for every screen that can run it. The frame rate target is never written: no cap from Exo.
/// - FSR 4 for FSR 3.1 games (the driver's FSR Upscaling override) on RX 9000 and RX 7000, and on
///   RX 6000 from AMD Software 26.10.1, the first driver with FSR 4 there.
/// - Anti-Lag on, always, with no choice: the user chose it. Globally on, the driver also hooks Windows' own apps
///   (the Start menu, WebView2) and costs some CPU (RadeonTuner's driver notes), which is why Exo used to keep it
///   off and turn it on only while a game Exo launched ran; but that left every game started from Steam, a
///   shortcut or another launcher without it, and ADLX has no per-game setting to do better.
/// - Fluid Motion Frames is never written here: it stays the player's, turned on per game at launch.
/// </summary>
internal static partial class GpuProfile
{

    /// <summary>ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE's WFVR_OFF_UNLESS_APP_SPECIFIES (ADLXDefines.h): V-Sync as each game sets it.</summary>
    internal const int VSyncGameDecides = 1;

    /// <summary>Wait for Vertical Refresh's mode in AMD Software's words (ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE 0-3).</summary>
    internal static string? VSyncMode(int mode) => mode switch
    {
        0 => "Always off",
        VSyncGameDecides => "Off, unless application specifies",
        2 => "On, unless application specifies",
        3 => "Always on",
        _ => null,
    };

    /// <summary>
    /// Whether the driver's FSR 4 upgrade runs on this GPU: RX 9000 and RX 7000 (and their Radeon PRO W cards), RX 6000
    /// from AMD Software 26.10.1, never RX 5000. From 26.10.1 AMD also brought it to GPUs whose names don't say a family
    /// (Ryzen AI Max 300/400, Ryzen AI 9 300/400 and Ryzen Z2 graphics, the Radeon AI PRO R9700/R9600 cards): there the
    /// driver's own answer decides (<paramref name="supported"/>: it offers the setting on this GPU).
    /// </summary>
    internal static bool FsrUpgrade(string? gpu, string? software, bool supported = false)
    {
        if (gpu is null) return false;
        if (RadeonFamilyPattern().Match(gpu) is not { Success: true } match) return supported && SoftwareAtLeast(software, 26, 10, 1);
        return match.Groups[1].Value switch
        {
            "9" or "7" => true,
            "6" => SoftwareAtLeast(software, 26, 10, 1),
            _ => false,
        };
    }

    /// <summary>AMD Software's version ("26.10.1", "26.10.1.0") at least year.month.patch; false when unknown.</summary>
    internal static bool SoftwareAtLeast(string? software, int year, int month, int patch)
    {
        if (software is null || SoftwarePattern().Match(software) is not { Success: true } match) return false;
        var parts = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0);
        return parts.CompareTo((year, month, patch)) >= 0;
    }

    /// <summary>The freeSync key of the main screen: the screen of that name (when Windows names none, the only FreeSync screen); null when it has none.</summary>
    internal static string? MainFreeSync(IReadOnlyDictionary<string, int> values, string? mainScreen)
    {
        var screens = values.Keys.Where(key => key.StartsWith(Keys.FreeSync, StringComparison.Ordinal)).ToList();
        if (string.IsNullOrWhiteSpace(mainScreen)) return screens.Count == 1 ? screens[0] : null;
        return screens.FirstOrDefault(key => string.Equals(key[Keys.FreeSync.Length..], mainScreen.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every write of the Radeon profile, only for settings this driver has, in the order the driver needs:
    /// Chill off before Anti-Lag (Chill turns it off), Super Resolution off before Image Sharpening (RSR holds
    /// sharpening on some GPUs), Enhanced Sync off before V-Sync's mode (Enhanced Sync turns V-Sync off).
    /// </summary>
    internal static List<KeyValuePair<string, int>> Radeon(GpuDriverState state)
    {
        var writes = new List<KeyValuePair<string, int>>();
        void Write(string key, int value)
        {
            if (state.Values.ContainsKey(key)) writes.Add(new(key, value));
        }
        Write(Keys.Chill, 0);
        Write(Keys.Boost, 0);
        Write(Keys.Rsr, 0);
        Write(Keys.Sharpening, 0);
        Write(Keys.EnhancedSync, 0);
        Write(Keys.VSync, VSyncGameDecides);
        Write(Keys.AntiLag, 1);
        foreach (var screen in state.Values.Keys.Where(key => key.StartsWith(Keys.FreeSync, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            writes.Add(new(screen, 1));
        if (FsrUpgrade(state.Gpu, state.Software, state.Values.ContainsKey(Keys.Fsr))) Write(Keys.Fsr, 1);
        return writes;
    }

    /// <summary>What the profile does, in one line under its name.</summary>
    internal static string RadeonSummary(IReadOnlyList<KeyValuePair<string, int>> writes)
    {
        bool Has(string key) => writes.Any(write => write.Key == key);
        var on = new List<string>();
        if (writes.Any(write => write.Key.StartsWith(Keys.FreeSync, StringComparison.Ordinal))) on.Add("FreeSync on");
        if (Has(Keys.Fsr)) on.Add("FSR 4 for FSR 3.1 games");
        if (Has(Keys.AntiLag)) on.Add("Anti-Lag on");
        if (Has(Keys.VSync)) on.Add("V-Sync as each game sets it");
        var off = new (string Key, string Name)[]
            {
                (Keys.Chill, "Chill"), (Keys.Boost, "Boost"), (Keys.Rsr, "Super Resolution"), (Keys.Sharpening, "Image Sharpening"),
                (Keys.EnhancedSync, "Enhanced Sync"),
            }
            .Where(item => Has(item.Key)).Select(item => item.Name).ToList();
        var parts = new List<string>();
        if (on.Count > 0) parts.Add(string.Join(", ", on));
        if (off.Count > 0) parts.Add((off.Count == 1 ? off[0] : string.Join(", ", off.Take(off.Count - 1)) + " and " + off[^1]) + " off");
        var line = string.Join("; ", parts);
        return line.Length == 0 ? line : char.ToUpperInvariant(line[0]) + line[1..];
    }

    [GeneratedRegex(@"\b(?:RX|PRO\s+W)\s*([5-9])\d{3}(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonFamilyPattern();

    [GeneratedRegex(@"^\s*(\d{2})\.(\d{1,2})(?:\.(\d{1,3}))?")]
    private static partial Regex SoftwarePattern();
}

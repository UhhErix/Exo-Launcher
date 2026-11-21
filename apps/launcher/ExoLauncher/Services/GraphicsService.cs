using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Which GPU vendor Exo talks to, and known NVIDIA setting IDs for the global profile.
/// </summary>
internal static class GraphicsService
{
    private static readonly Lock Gate = new();
    private static IReadOnlySet<uint>? _known;

    /// <summary>
    /// The GPU vendors among the display adapters: nvidia, amd, intel (in that order), but one with a card of its own
    /// before one with only a processor's graphics (a Ryzen's Radeon Graphics beside an Arc card).
    /// </summary>
    internal static IReadOnlyList<string> Vendors(IEnumerable<string> adapters)
    {
        var names = adapters.ToList();
        return [.. new[] { "nvidia", "amd", "intel" }.Where(vendor => names.Any(name => Maker(name) == vendor))
            .OrderByDescending(vendor => names.Any(name => Maker(name) == vendor && Discrete(name)))];
    }

    private static string? Maker(string adapter) =>
        adapter.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "nvidia"
        : Regex.IsMatch(adapter, @"\b(AMD|Radeon)\b", RegexOptions.IgnoreCase) ? "amd"
        : Regex.IsMatch(adapter, @"\bIntel\b", RegexOptions.IgnoreCase) && Regex.IsMatch(adapter, @"\b(Arc|Iris|UHD|HD|Graphics)\b", RegexOptions.IgnoreCase) ? "intel"
        : null;

    /// <summary>
    /// A graphics card of its own, not a processor's graphics, by its name: any GeForce; a Radeon RX, PRO or VII, but not
    /// an APU's "Graphics" ("Radeon(TM) RX Vega 11 Graphics"); an Arc A or B, not "Arc(TM) Graphics" or "Arc(TM) 140V GPU".
    /// </summary>
    internal static bool Discrete(string adapter) => Maker(adapter) switch
    {
        "nvidia" => true,
        "amd" => Regex.IsMatch(adapter, @"\b(?:RX|PRO|VII)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(adapter, @"\bGraphics\s*$", RegexOptions.IgnoreCase),
        "intel" => Regex.IsMatch(adapter, @"\bArc\b.*\b[AB]\d{3}", RegexOptions.IgnoreCase),
        _ => false,
    };

    /// <summary>
    /// The vendor Exo talks to: the first of <see cref="Vendors"/> whose driver answers (NVIDIA's, AMD's ADLX, Intel's
    /// IGCL), so an Arc card beside a Ryzen's Radeon Graphics is Intel's; null when none.
    /// </summary>
    internal static string? ActiveVendor(IReadOnlyList<string> vendors, bool nvapi, bool adlx, bool igcl) =>
        vendors.FirstOrDefault(vendor => vendor switch { "nvidia" => nvapi, "amd" => adlx, "intel" => igcl, _ => false });

    internal static string? Vendor() =>
        ActiveVendor(Vendors(GpuCapability.Adapters()), NvidiaProfiles.Available, AmdAdlx.Present, IntelIgcl.Present);

    /// <summary>Every setting id this NVIDIA driver lists (it refuses any other); read again until it answers.</summary>
    internal static IReadOnlySet<uint> KnownIds()
    {
        lock (Gate)
            return _known is { Count: > 0 } known ? known : _known = NvidiaProfiles.KnownSettingIds();
    }

    /// <summary>
    /// Forgets the list of setting ids the driver lists, so the next ask reads it from the driver now: a new driver lists
    /// other ids (<see cref="DriverKeep"/>, and <see cref="NvidiaProfiles.Reinitialize"/> after Exo's own install).
    /// </summary>
    internal static void ForgetKnownIds()
    {
        lock (Gate) _known = null;
    }
}

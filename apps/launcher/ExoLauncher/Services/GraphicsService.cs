using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Which GPU vendor Exo talks to, and a Profile Inspector import (.nip) to the
/// driver's global profile. An import is read into a preview first; Apply
/// writes it after Exo has read the values it replaces, and Undo puts those
/// back. The preview and the undo record live in memory until Exo closes.
/// </summary>
internal static class GraphicsService
{
    internal sealed record Preview(bool Ok, string? Message, string? ProfileName, IReadOnlyList<(string Label, string Value)> Items, int Unknown, int LeftOut, int NotInDriver = 0);

    internal sealed record Done(bool Ok, string? Message);

    private sealed record UndoRecord(string ProfileName, IReadOnlyDictionary<uint, uint?> Before, IReadOnlyList<uint> Written);

    private static readonly Lock Gate = new();
    private static NvidiaProfileImport.Plan? _preview;
    private static UndoRecord? _undo;
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

    /// <summary>The last import Undo can put back: its profile name and setting count.</summary>
    internal static (string Profile, int Count)? ImportUndo()
    {
        lock (Gate)
            return _undo is { } undo ? (undo.ProfileName, undo.Written.Count) : null;
    }

    /// <summary>Reads a .nip file into the list Apply would write to the global profile; nothing is written.</summary>
    internal static Preview PreviewImport(string path)
    {
        if (Vendor() != "nvidia") return new Preview(false, "Profile imports need an NVIDIA graphics card.", null, [], 0, 0);
        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new Preview(false, "The file is gone.", null, [], 0, 0);
            if (info.Length > NvidiaProfileImport.MaxBytes) return new Preview(false, "This file is too large to be a Profile Inspector export.", null, [], 0, 0);
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Preview(false, "Exo could not read the file.", null, [], 0, 0);
        }
        if (NvidiaProfileImport.Parse(text) is not { } profiles) return new Preview(false, "This is not a Profile Inspector export (.nip).", null, [], 0, 0);
        if (NvidiaProfileImport.PickGlobal(profiles, out var reason) is not { } profile) return new Preview(false, reason, null, [], 0, 0);
        var plan = NvidiaProfileImport.PlanFor(profile, KnownIds());
        if (plan.Writes.Count == 0) return new Preview(false, "This profile changes nothing this driver can take.", profile.Name, [], 0, plan.LeftOut, plan.NotInDriver);
        lock (Gate) _preview = plan;
        AppLog.Info($"Graphics import preview: settings={plan.Writes.Count}; unknown={plan.Unknown}; leftOut={plan.LeftOut}; notInDriver={plan.NotInDriver}.");
        return new Preview(true, null, plan.ProfileName,
            plan.Writes.Where(change => change.Name is not null).Select(change => (change.Name!, change.Words)).ToList(), plan.Unknown, plan.LeftOut, plan.NotInDriver);
    }

    /// <summary>Forgets a preview without writing.</summary>
    internal static void CancelImport()
    {
        lock (Gate) _preview = null;
    }

    /// <summary>Writes the previewed profile, keeping what it replaces for Undo.</summary>
    internal static Done ApplyImport()
    {
        NvidiaProfileImport.Plan? plan;
        lock (Gate) (plan, _preview) = (_preview, null);
        if (plan is null) return new Done(false, "Open the file again.");
        if (Vendor() != "nvidia") return new Done(false, "The NVIDIA driver did not answer, so nothing changed. Try again.");
        var ids = plan.Writes.Select(change => change.Id).ToList();
        var before = NvidiaProfiles.Read(ids);
        if (!before.Ok) return new Done(false, before.Message);
        var written = NvidiaProfiles.Write(plan.Writes.Select(change => new KeyValuePair<uint, uint?>(change.Id, change.Value)).ToList(), ids);
        if (!written.Ok) return new Done(false, written.Message);
        lock (Gate) _undo = new UndoRecord(plan.ProfileName, before.Values, ids);
        AppLog.Info($"Graphics import applied: settings={ids.Count}.");
        return new Done(true, null);
    }

    /// <summary>Puts back the values an import replaced.</summary>
    internal static Done UndoImport()
    {
        UndoRecord? record;
        lock (Gate) record = _undo;
        if (record is null) return new Done(false, "Nothing to undo.");
        if (Vendor() != "nvidia") return new Done(false, "The NVIDIA driver did not answer, so nothing was put back. Try again.");
        var restored = NvidiaProfiles.Write(NvidiaProfileImport.UndoWrites(record.Before, record.Written), record.Written);
        if (!restored.Ok) return new Done(false, restored.Message);
        lock (Gate) _undo = null;
        AppLog.Info($"Graphics import undone: settings={record.Written.Count}.");
        return new Done(true, null);
    }
}

using System.Globalization;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// System > Graphics > Driver and profile: Exo's global profile (<see cref="GpuProfile"/>)
/// for this PC's GeForce series, G-SYNC off.
/// Use writes every setting to the driver's global profile in one session after
/// saving every value it replaces to gpu-profile.json, so Undo puts back exactly
/// what was there (a value, or the driver's default), even after Exo restarts. A
/// re-apply keeps the first backup. Each read compares the driver with the
/// profile, so a driver update or another tool that moves a setting shows as changed with
/// Re-apply. A Radeon gets its own profile through ADLX (<see cref="RadeonProfileService"/>) and an Intel GPU
/// through IGCL (<see cref="IntelProfileService"/>), or what to set by hand when their driver library does not answer.
/// </summary>
internal static class GpuProfileService
{
    /// <summary>
    /// The profile and how the driver compares. <see cref="Skipped"/>: the ids of the profile this driver skipped or refused
    /// (it does not list them, or would not take them), so nothing was written for them; <see cref="Features"/>: what the page
    /// may say of each feature it names; <see cref="Partly"/>: at least one of the profile's settings is not in the driver.
    /// </summary>
    internal sealed record State(bool Ok, string? Message, string? Vendor, string? Gpu, int Series, GpuProfile.Screen Screen,
        string Status, bool CanUndo, bool DriverUpdated, IReadOnlyList<GpuProfile.Item> Manual,
        string? Summary = null, string? LowLatency = null, IReadOnlyList<uint>? Skipped = null, IReadOnlyList<GpuProfile.Feature>? Features = null,
        bool? VrrEnabled = null, bool? Rebar = null, long? CacheFreeGb = null, IReadOnlyList<GpuProfile.SettingDetail>? Details = null)
    {
        /// <summary>Something the profile asks for is not in this driver, so the page never says the whole profile is on.</summary>
        internal bool Partly => Skipped is { Count: > 0 };
    }

    private sealed record Backup(uint Driver, Dictionary<uint, uint?> Before);

    private static readonly Lock Gate = new();
    private static string BackupPath => Path.Combine(PathHelper.AppDataDir, "gpu-profile.json");

    /// <summary>Exo's global profile was applied and Undo can put it back (the backup is there); <see cref="DriverKeep"/> asks.</summary>
    internal static bool HasBackup
    {
        get { lock (Gate) return LoadBackup() is not null; }
    }

    /// <summary>The profile for this PC and how the driver compares.</summary>
    internal static State Read()
    {
        // While NVIDIA's installer swaps the driver, its settings can't be read: say so instead of an error.
        if (NvidiaDriverService.Current.Stage == "installing")
            return new State(false, "Your NVIDIA driver is being installed.", "nvidia", null, 0, MainScreen("nvidia"), "manual", false, false, []);
        // AMD's and Intel's too: a Radeon's settings are left alone until AMD's installer is done.
        if (GpuDriverUpdate.Current.Stage == "installing")
            return new State(false, "Your graphics driver is being installed.", GraphicsService.Vendor(), null, 0, MainScreen(null), "manual", false, false, []);
        lock (Gate) return ReadLocked();
    }

    /// <summary>
    /// As Exo starts, once: a PC still on the G-SYNC profile Exo used to write (its backup says "gsync":true, with its driver
    /// V-Sync and frame cap in the global profile) gets the profile as it is now, which sets no cap and leaves V-Sync to
    /// games. What it held before Exo stays in the backup for Undo. A backup without that mark isn't touched.
    /// </summary>
    internal static void RetireGsync()
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(BackupPath) || !WasGsync(File.ReadAllText(BackupPath)) || GraphicsService.Vendor() != "nvidia") return;
                AppLog.Info($"GPU profile: the G-SYNC profile and its cap are gone: {Apply().Status}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("GPU profile, leaving G-SYNC: " + ex.GetType().Name); }
    }

    /// <summary>Whether a backup comes from the G-SYNC profile.</summary>
    internal static bool WasGsync(string backup)
    {
        try
        {
            using var json = JsonDocument.Parse(backup);
            return json.RootElement.TryGetProperty("gsync", out var gsync) && gsync.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Applies Exo's profile to the global profile (on a Radeon, its profile; on Intel/NVIDIA, with Low latency and VRR choices),
    /// backing up what it replaces first.
    /// </summary>
    internal static State Apply(string? lowLatency = null) => ApplyWith(lowLatency, null);

    internal static State ApplyWith(string? lowLatency, bool? vrr)
    {
        lock (Gate)
        {
            var vendor = GraphicsService.Vendor();
            if (vendor == "amd")
                return Radeon(RadeonProfileService.Shared.Apply(MainScreen(vendor))) ?? ReadLocked() with { Ok = false, Message = "AMD's driver did not answer." };
            if (vendor == "intel")
                return Intel(IntelProfileService.Shared.Apply(lowLatency ?? GpuProfile.LowLatencyOn), MainScreen(vendor)) ?? ReadLocked() with { Ok = false, Message = "Intel's driver did not answer." };
            if (vendor != "nvidia") return ReadLocked() with { Ok = false, Message = "Exo changes only NVIDIA, AMD and Intel driver settings." };
            if (lowLatency is not null && GpuProfile.IsNvidiaLowLatencyChoice(lowLatency))
                App.Services.Settings.UpdateProfile(s => s.NvidiaLowLatency = lowLatency);
            if (vrr is { } chosenVrr)
                App.Services.Settings.UpdateProfile(s => s.NvidiaGsync = chosenVrr);
            var variant = NvidiaVariant(MainScreen("nvidia"));
            var (ok, message, writes, values, skipped, _) = Nvidia(variant);
            if (!ok) return ReadLocked() with { Ok = false, Message = message };
            var ids = writes.Keys.Order().ToList();
            var earlier = LoadBackup();
            if (!SaveBackup(new Backup(NvidiaProfiles.DriverVersion() ?? 0, GpuProfile.MergeBackup(earlier?.Before, values, ids))))
                return ReadLocked() with { Ok = false, Message = "Exo could not save a backup, so it changed nothing." };
            var written = NvidiaProfiles.Write(writes.OrderBy(write => write.Key).ToList(), ids);
            if (!written.Ok)
            {
                // One session, one save: nothing was written, so the backup goes back to what it was.
                if (earlier is null) DeleteBackup(); else SaveBackup(earlier);
                return ReadLocked() with { Ok = false, Message = written.Message };
            }
            // What the driver really took: the ids it does not list, and the ones it refused. Nothing else goes to Keep or the page.
            var notTaken = skipped.Concat(written.Refused ?? []).Distinct().Order().ToList();
            AppLog.Info($"GPU profile applied: series={Series()}; settings={ids.Count - (written.Refused?.Count ?? 0)}." +
                (notTaken.Count > 0 ? $" Not in this driver: {string.Join(", ", notTaken.Select(id => $"0x{id:X8}"))}." : ""));
            RememberNotTaken(notTaken);
            return ReadLocked();
        }
    }

    /// <summary>
    /// The ids the driver skipped or refused are kept for this driver (<see cref="Keep.KeepPc.DriverSkipped"/>); a new driver
    /// starts with none, and <see cref="DriverKeep"/> clears them when the driver identity changes. A record that can't be
    /// saved changes nothing the page is told: the read is what decides.
    /// </summary>
    private static void RememberNotTaken(IReadOnlyList<uint> notTaken)
    {
        if (!Keep.Update(doc =>
            {
                doc.Pc.DriverSkipped = notTaken.Select(id => $"0x{id:X8}").ToList();
                doc.Pc.DriverSkippedDriver = DriverIdentity.Current();
            }))
            AppLog.Warn("GPU profile: the settings this driver doesn't take couldn't be kept in Keep's record.");
    }

    /// <summary>Puts back every value the profile replaced and forgets the backup.</summary>
    internal static State Undo()
    {
        lock (Gate)
        {
            if (GraphicsService.Vendor() == "amd")
                return Radeon(RadeonProfileService.Shared.Undo(MainScreen("amd"))) ?? ReadLocked() with { Ok = false, Message = "AMD's driver did not answer." };
            if (GraphicsService.Vendor() == "intel")
                return Intel(IntelProfileService.Shared.Undo(), MainScreen("intel")) ?? ReadLocked() with { Ok = false, Message = "Intel's driver did not answer." };
            if (LoadBackup() is not { } backup) return ReadLocked() with { Ok = false, Message = "Nothing to undo." };
            if (GraphicsService.Vendor() != "nvidia") return ReadLocked() with { Ok = false, Message = "The NVIDIA driver did not answer, so nothing was put back. Try again." };
            var ids = backup.Before.Keys.Order().ToList();
            var restored = NvidiaProfiles.Write(NvidiaProfileImport.UndoWrites(backup.Before, ids), ids);
            if (!restored.Ok) return ReadLocked() with { Ok = false, Message = restored.Message };
            DeleteBackup();
            AppLog.Info($"GPU profile undone: settings={ids.Count - (restored.Refused?.Count ?? 0)}.");
            RememberNotTaken([.. restored.Refused ?? []]);
            return ReadLocked();
        }
    }

    private static State ReadLocked()
    {
        var vendor = GraphicsService.Vendor();
        var screen = MainScreen(vendor);
        if (vendor is null) return new State(true, null, null, null, 0, screen, "none", false, false, []);
        if (vendor == "amd" && Radeon(RadeonProfileService.Shared.Read(screen)) is { } radeon) return radeon;
        if (vendor == "intel" && Intel(IntelProfileService.Shared.Read(), screen) is { } intel) return intel;
        if (vendor != "nvidia")
        {
            var amd = vendor == "amd";
            return new State(true,
                amd ? "Set these in AMD Software. Exo could not reach AMD's driver settings on this PC."
                    : "Set these in Intel Graphics Software. Exo could not reach Intel's driver settings on this PC.",
                vendor, AdapterName(vendor), 0, screen, "manual", false, false,
                amd ? GpuProfile.Amd(AmdAdlx.Read()) : GpuProfile.Intel());
        }
        var backup = LoadBackup();
        var variant = NvidiaVariant(screen);
        var (ok, message, writes, values, skipped, free) = Nvidia(variant);
        var rebar = PcHealth.TryMainGpu(TimeSpan.FromSeconds(4))?.UploadHeap;
        if (!ok) return new State(false, message, vendor, AdapterName(vendor), Series(), screen, "none", backup is not null, false, [], Rebar: rebar);
        // The ids this driver does not list, and the ones the record says it refused at a write for the driver installed now.
        var notTaken = skipped.Concat(RefusedForThisDriver().Where(id => writes.ContainsKey(id))).Distinct().Order().ToList();
        var status = StatusOf(writes, values, notTaken, backup is not null);
        var driverUpdated = status == "changed" && backup!.Driver != (NvidiaProfiles.DriverVersion() ?? 0);
        var liveLatency = GpuProfile.InferNvidiaLowLatency(values) ?? variant.LowLatency;
        var liveVrr = values.TryGetValue(GpuProfile.GsyncFeature, out var gs) && gs is { } g ? g == 1 : variant.Vrr;
        return new State(true, null, vendor, AdapterName(vendor), Series(), screen, status, backup is not null, driverUpdated, [],
            LowLatency: liveLatency,
            Skipped: notTaken,
            Features: GpuProfile.Features(writes, values, notTaken),
            VrrEnabled: screen.Vrr ? liveVrr : false,
            Rebar: rebar,
            CacheFreeGb: free,
            Details: GpuProfile.NvidiaDetails(variant, free, writes, values, notTaken, rebar));
    }

    private static GpuProfile.Variant NvidiaVariant(GpuProfile.Screen screen)
    {
        var s = App.Services.Settings.Current;
        var latency = GpuProfile.IsNvidiaLowLatencyChoice(s.NvidiaLowLatency) ? s.NvidiaLowLatency! : GpuProfile.LowLatencyOn;
        var vrr = screen.Vrr && (s.NvidiaGsync ?? true);
        return new GpuProfile.Variant(Series(), Vrr: vrr, LowLatency: latency);
    }

    /// <summary>
    /// The profile's status over the writes the driver took: an id this driver skipped or refused never holds what Exo set,
    /// so counting it would read "changed" for ever and every put back would "fail".
    /// </summary>
    internal static string StatusOf(IReadOnlyDictionary<uint, uint?> writes, IReadOnlyDictionary<uint, uint?> values, IReadOnlyCollection<uint> notTaken, bool applied) =>
        GpuProfile.Status(writes.Where(write => !notTaken.Contains(write.Key)).ToDictionary(write => write.Key, write => write.Value), values, applied);

    /// <summary>
    /// The ids the record says this driver refused (<see cref="Keep.KeepPc.DriverSkipped"/>, kept for the identity it was
    /// applied under): none once the driver changed, since a new driver may take them.
    /// </summary>
    private static IReadOnlyList<uint> RefusedForThisDriver()
    {
        var pc = Keep.Load().Pc;
        if (pc.DriverSkipped.Count == 0 || pc.DriverSkippedDriver != DriverIdentity.Current()) return [];
        return [.. pc.DriverSkipped.Select(id => uint.TryParse(id.AsSpan(id.StartsWith("0x", StringComparison.Ordinal) ? 2 : 0), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0).Where(id => id != 0)];
    }

    /// <summary>The profile's writes and what the global profile holds for them, in one driver session; always reads back Low Latency ids even if EnumAvailableSettingIds omits legacy ids.</summary>
    private static (bool Ok, string? Message, IReadOnlyDictionary<uint, uint?> Writes, IReadOnlyDictionary<uint, uint?> Values, IReadOnlyList<uint> Skipped, long FreeGb) Nvidia(GpuProfile.Variant variant)
    {
        var known = GraphicsService.KnownIds();
        var free = FreeGb();
        if (known.Count == 0) return (false, "The NVIDIA driver did not list its settings.", new Dictionary<uint, uint?>(), new Dictionary<uint, uint?>(), [], free);
        var plan = GpuProfile.NvidiaPlan(variant, free, known);
        var toRead = plan.Writes.Keys
            .Concat([GraphicsCatalog.LowLatencyState, GraphicsCatalog.UltraLowLatency, GraphicsCatalog.PreRenderedFrames])
            .Distinct()
            .ToList();
        var read = NvidiaProfiles.Read(toRead);
        if (!read.Ok) return (false, read.Message, new Dictionary<uint, uint?>(), read.Values, plan.Skipped, free);
        return (true, null, plan.Writes, read.Values, plan.Skipped, free);
    }

    /// <summary>The Radeon profile as the page's state; null (ADLX did not answer, or offers nothing Exo sets) falls back to what to set by hand.</summary>
    private static State? Radeon(RadeonProfileService.State? radeon)
    {
        if (radeon is null || radeon.Status == "none") return null;
        var screen = MainScreen("amd") with { Vrr = radeon.FreeSync };
        return new State(radeon.Ok, radeon.Message, "amd", radeon.Gpu ?? AdapterName("amd"), 0, screen, radeon.Status,
            radeon.CanUndo, radeon.DriverUpdated, [], radeon.Summary);
    }

    /// <summary>The Intel profile as the page's state; null (IGCL did not answer, or offers nothing Exo sets) falls back to what to set by hand.</summary>
    private static State? Intel(IntelProfileService.State? intel, GpuProfile.Screen screen)
    {
        if (intel is null || intel.Status == "none") return null;
        return new State(intel.Ok, intel.Message, "intel", intel.Gpu ?? AdapterName("intel"), 0, screen, intel.Status,
            intel.CanUndo, intel.DriverUpdated, [], Summary: intel.Summary, LowLatency: intel.LowLatency);
    }

    private static int Series() => Series(GpuCapability.Adapters());

    /// <summary>The GeForce series the profile is built for: the first adapter that names one (0: none, as a GT, an MX or a card before Pascal).</summary>
    internal static int Series(IEnumerable<string> adapters) => adapters.Select(GpuProfile.NvidiaSeries).FirstOrDefault(series => series is not null) ?? 0;

    private static string? AdapterName(string vendor)
    {
        var name = GpuCapability.Adapters().FirstOrDefault(adapter => GraphicsService.Vendors([adapter]).Contains(vendor));
        if (vendor == "intel") return IntelIgcl.ShortName(name);
        return name?.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    /// <summary>The main screen; its variable refresh as NVIDIA's or Intel's driver reports it (a Radeon's comes from its FreeSync settings: false here).</summary>
    internal static GpuProfile.Screen MainScreen(string? vendor)
    {
        if (WindowsHdr.Primary() is not { } primary) return new GpuProfile.Screen(null, 0, false);
        var refresh = DisplayOutput.RefreshRates(primary.GdiName).Current;
        var vrr = vendor switch
        {
            "nvidia" => NvidiaProfiles.ScreenVrr(primary.GdiName) == true,
            "intel" => IntelIgcl.ScreenVrr(primary.Target),
            _ => false,
        };
        return new GpuProfile.Screen(string.IsNullOrWhiteSpace(primary.Name) ? null : primary.Name, refresh, vrr);
    }

    /// <summary>Free space on the drive the driver keeps its shader cache on (%LOCALAPPDATA%), in GB.</summary>
    private static long FreeGb()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace / (1024L * 1024 * 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    // gpu-profile.json: {"driver":61742,"before":{"0x10835002":141,"0x00A879CF":null}}
    private static Backup? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(BackupPath));
            var driver = json.RootElement.GetProperty("driver").GetUInt32();
            var before = new Dictionary<uint, uint?>();
            foreach (var item in json.RootElement.GetProperty("before").EnumerateObject())
            {
                if (!uint.TryParse(item.Name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) continue;
                before[id] = item.Value.ValueKind == JsonValueKind.Null ? null : item.Value.GetUInt32();
            }
            return new Backup(driver, before);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            AppLog.Warn("GPU profile backup unreadable: " + ex.GetType().Name);
            return null;
        }
    }

    private static bool SaveBackup(Backup backup)
    {
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["driver"] = backup.Driver,
                ["before"] = backup.Before.OrderBy(item => item.Key).ToDictionary(item => $"0x{item.Key:X8}", item => item.Value),
            };
            var temp = BackupPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(body));
            File.Move(temp, BackupPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("GPU profile backup not saved: " + ex.GetType().Name);
            return false;
        }
    }

    private static void DeleteBackup()
    {
        try { DeletionGate.DeleteFile(BackupPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("GPU profile backup not removed: " + ex.GetType().Name); }
    }
}

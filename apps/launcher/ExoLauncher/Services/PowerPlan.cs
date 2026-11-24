using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Exo's power plan: one Windows plan named "Exo", a copy of Balanced (so AMD's and Intel's tuning of Balanced carries
/// over, and X3D core parking keeps working; where Windows won't copy Balanced, a copy of the plan that's on, given
/// Balanced's personality: <see cref="Ensure"/>), with this PC's processor class's settings written into it
/// (<see cref="PowerProfiles"/>) and shown in Control Panel's advanced power settings. On a PC running one of Windows'
/// own plans Exo switches to it by itself once (<see cref="Auto"/>); on a custom or third-party plan System > This PC
/// offers it and Exo never switches by itself. Undo puts back the plan it replaced and Exo doesn't switch again; picking
/// another plan in Windows counts the same. A deleted Exo plan is made again the next time it's used, and Exo's
/// uninstaller deletes it (tools/installer/Remove-ExoPowerPlan.ps1 reads the same file). The active plan's own
/// processor settings are read for This PC's advice (<see cref="Limits"/>).
/// </summary>
internal sealed class PowerPlan(PcHealth.IPowerApi api, string statePath, Action<string>? logLine = null, Func<bool>? gameRunning = null)
{
    private readonly Action<string> log = logLine ?? AppLog.Info;
    private readonly Func<bool> gameIsRunning = gameRunning ?? (() => false);

    internal const string PlanName = "Exo";
    internal const string PlanDescription = "Exo Launcher's plan, tuned for this PC's processor. Undo in Exo's System > This PC puts your previous plan back.";

    // Start-up's Auto and a Setup's Ready can both find the plan missing at once; one makes it, the other finds it.
    private static readonly object Gate = new();
    private static readonly Lock StateGate = new();
    private const int StateIoAttempts = 5;
    internal static int StateIoBaseDelayMs { get; set; } = 25;

    internal static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    internal static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    internal static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    internal static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    internal static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    internal static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    // A plan's personality (SUB_NONE PERSONALITY in powercfg /qh: 0 Power saver, 1 High performance, 2 Balanced).
    private static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static readonly Guid PersonalitySetting = new("245d8541-3943-4422-b025-13a784f679b7");
    private const uint BalancedPersonality = 2;

    // Three of the processor's settings (PROCTHROTTLEMAX, PERFBOOSTMODE, CPMINCORES in powercfg /qh).
    private static readonly Guid MaxStateSetting = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid BoostModeSetting = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid MinCoresSetting = new("0cc5b647-c1df-4637-891a-dec35c318583");

    internal static PowerPlan Shared { get; } = new(new PcHealth.NativePower(), Path.Combine(PathHelper.AppDataDir, "exo-power-plan.json"), gameRunning: DriverKeep.AnyGameRunning);

    /// <summary>The active plan's processor settings on AC: maximum processor state (%), boost mode (0 = disabled) and core parking's minimum cores (%; 100 = parking off). Null where the plan doesn't say.</summary>
    internal sealed record Limits(uint? MaxState, uint? BoostMode, uint? MinCores);

    /// <summary>
    /// <paramref name="Exo"/>: Exo's plan is on. <paramref name="CanUndo"/>: Exo switched to it and holds the plan it
    /// replaced. <paramref name="Stock"/>: one of Windows' own plans is on.
    /// </summary>
    internal sealed record State(Guid Active, string? Name, Guid Overlay, bool Battery, bool Exo, bool CanUndo, Limits? Cpu = null)
    {
        internal bool Stock => IsStock(Active);
    }

    /// <summary>What Exo keeps: its plan, the plan it replaced (and its power mode), and whether the user turned it down.</summary>
    internal sealed record Saved(Guid? Plan, Guid? Previous, Guid PreviousOverlay, bool Declined, Guid[]? Retired = null);

    /// <summary>What writing a profile's values came to: how many took (read back as written), the ones Windows accepted but didn't keep, and the ones this PC has no setting for.</summary>
    internal sealed record Wrote(int Taken, IReadOnlyList<string> DidNotTake, IReadOnlyList<string> NotOnThisPc);

    /// <summary>What <see cref="ApplyWithReport"/> did: the line the page shows (null when everything took) and what each value came to.</summary>
    internal sealed record Applied(string? Message, Wrote? Values);

    /// <summary>What the page is told when Windows accepted a write and did not keep it.</summary>
    internal const string DidNotTakeLine = "Windows didn't take every setting of Exo's plan.";

    internal static bool IsStock(Guid scheme) => scheme == Balanced || scheme == HighPerformance || scheme == PowerSaver || scheme == Ultimate;

    internal State? Read()
    {
        if (api.Active() is not { } active) return null;
        var saved = Load();
        var exo = saved.Plan is { } plan && active == plan;
        // Another plan was picked in Windows since Exo switched: that's the user's choice now.
        if (!exo && saved.Previous is not null) Save(saved = saved with { Previous = null, Declined = true });
        var cpu = new Limits(api.AcValue(active, PowerProfiles.Processor, MaxStateSetting), api.AcValue(active, PowerProfiles.Processor, BoostModeSetting), api.AcValue(active, PowerProfiles.Processor, MinCoresSetting));
        return new State(active, api.Name(active), api.Overlay(), api.Battery(), exo, exo && saved.Previous is not null, cpu);
    }

    /// <summary>
    /// Writes <paramref name="profile"/> into Exo's plan (made from Balanced if it's missing), reads every value back, and
    /// switches to it, keeping the plan it replaces for Undo (a second Apply keeps the first). Null when done and every
    /// value took, else the line to show: a value Windows accepted and didn't keep says <see cref="DidNotTakeLine"/> (the
    /// plan is still switched to, and Undo still works).
    /// </summary>
    internal string? Apply(PowerProfiles.Profile profile) => ApplyWithReport(profile).Message;

    /// <summary><see cref="Apply"/>, with what each value came to.</summary>
    internal Applied ApplyWithReport(PowerProfiles.Profile profile)
    {
        if (api.Active() is not { } active) return new("Windows didn't say which plan is on.", null);
        var saved = Load();
        if (Ensure(saved, active, out var refused) is not { } plan) return new($"Windows didn't make Exo's plan: {refused}.", null);
        var wrote = Write(plan, profile.Values, show: true);
        if (active != plan) saved = saved with { Previous = active, PreviousOverlay = api.Overlay() };
        // Save before switching, so a crash right after still has the way back.
        if (!Save(saved with { Plan = plan, Declined = false })) return new("Your current plan couldn't be saved for Undo, so nothing changed.", wrote);
        if (!api.SetActive(plan))
        {
            Save(saved with { Plan = plan, Previous = null });
            return new("Windows didn't switch to Exo's plan.", wrote);
        }
        // "was" names a plan only when it wasn't Exo's own.
        var on = active == plan ? "already on" : $"on (was {active})";
        log($"Power plan: Exo's plan ({profile.Key}{(profile.Laptop ? ", laptop" : "")}) is {on}; {Counted(wrote, profile.Values.Count)}.");
        return new(wrote.DidNotTake.Count == 0 ? null : DidNotTakeLine, wrote);
    }

    /// <summary>"13 of 13 values written", and what didn't take or isn't on this PC, each named.</summary>
    private static string Counted(Wrote wrote, int total)
    {
        var line = $"{wrote.Taken} of {total} values written";
        if (wrote.DidNotTake.Count > 0) line += ", Windows didn't take " + string.Join(", ", wrote.DidNotTake);
        // A PC without the device (no SATA, no second NVMe) has no such setting: Windows refuses those, and that's fine.
        if (wrote.NotOnThisPc.Count > 0) line += ", not on this PC: " + string.Join(", ", wrote.NotOnThisPc);
        return line;
    }

    /// <summary>The plan Exo replaced, and its power mode if it was Balanced. Exo doesn't switch by itself again.</summary>
    internal string? Undo()
    {
        var saved = Load();
        if (saved.Previous is not { } previous) return "There's nothing to undo.";
        var plan = api.SetActive(previous);
        var mode = previous != Balanced || api.Battery() || api.Overlay() == saved.PreviousOverlay || api.SetOverlay(saved.PreviousOverlay);
        Save(saved with { Previous = null, Declined = true });
        log($"Power plan: put back to {previous}{(plan && mode ? "" : " (Windows refused part of it)")}.");
        return plan ? mode ? null : "Windows didn't put your power mode back." : "Windows didn't put your plan back. It may have been deleted.";
    }

    internal Guid? Active() => api.Active();

    /// <summary>
    /// For a Setup: Exo's plan, made (with this PC's profile) when it's missing, without switching to it or touching
    /// Undo. Null when Windows won't make it.
    /// </summary>
    internal Guid? Ready(PowerProfiles.Profile profile)
    {
        if (api.Active() is not { } active) return null;
        var saved = Load();
        if (saved.Plan is { } plan && api.Name(plan) is not null) return plan;
        if (Ensure(saved, active, out _) is not { } made) return null;
        var wrote = Write(made, profile.Values, show: true);
        if (wrote.DidNotTake.Count > 0) log($"Power plan: Exo's plan was made for a Setup; {Counted(wrote, profile.Values.Count)}.");
        return Save(saved with { Plan = made }) ? made : null;
    }

    /// <summary>For a Setup: switches plans and nothing else; the Setup keeps its own way back (<see cref="Setups"/>).</summary>
    internal bool Switch(Guid scheme)
    {
        // A Setup's way back can name an extra Exo plan that Tidy deleted since: Exo's kept plan stands in for it.
        if (api.Name(scheme) is null && Load() is { Plan: { } kept, Retired: { } retired } && retired.Contains(scheme) && api.Name(kept) is not null) scheme = kept;
        var done = api.SetActive(scheme);
        log($"Power plan: a Setup switched to {scheme}{(done ? "" : ", which Windows refused")}.");
        return done;
    }

    /// <summary>
    /// At start: on one of Windows' own plans, switch to Exo's once (never after Undo or another pick); with Exo's plan
    /// on, look at every value the profile sets (<see cref="Recheck"/>); with Exo's plan deleted since, make it again and
    /// switch to it. Null when nothing changed or it switched, else the line Apply or Recheck returned.
    /// </summary>
    internal string? Auto(PowerProfiles.Profile profile)
    {
        if (api.Active() is not { } active) return null;
        var saved = Tidy(Load(), active);
        var inUse = saved.Plan is { } plan && (active == plan || (saved.Previous is not null && api.Name(plan) is null));
        if (inUse) return active == saved.Plan ? Recheck(profile) : Apply(profile);
        if (saved.Declined || !IsStock(active)) return null;
        return Apply(profile);
    }

    private bool Differs(Guid plan, PowerProfiles.Value value) =>
        api.AcValue(plan, value.Subgroup, value.Setting) != value.Ac || (value.Dc && api.DcValue(plan, value.Subgroup, value.Setting) != value.Ac);

    /// <summary>What one <see cref="RecheckReport"/> came to: the values another tool had changed that Exo wrote again (and read back), and the ones Windows did not take.</summary>
    internal sealed record Rechecked(IReadOnlyList<string> PutBack, IReadOnlyList<string> DidNotTake);

    /// <summary><see cref="RecheckReport"/> as the start tells it: null when all is as Exo set it or was put back, else <see cref="DidNotTakeLine"/>.</summary>
    internal string? Recheck(PowerProfiles.Profile profile) => RecheckReport(profile).DidNotTake.Count == 0 ? null : DidNotTakeLine;

    /// <summary>
    /// With Exo's plan the one in use: every value the profile sets is compared with what the plan holds; the values another
    /// tool changed (an optimizer, a driver's own tool, a Windows update) are written again once and read back, and one log
    /// line names them. A value the plan has no setting for is not "changed by another tool". Nothing is written while a
    /// game runs.
    /// </summary>
    internal Rechecked RecheckReport(PowerProfiles.Profile profile)
    {
        if (gameIsRunning()) return new([], []);
        if (api.Active() is not { } active || Load().Plan is not { } plan || active != plan) return new([], []);
        var differing = profile.Values.Where(value => Differs(plan, value)).ToList();
        if (differing.Count == 0) return new([], []);
        var wrote = Write(plan, differing, show: false);
        var back = differing.Select(value => value.Name).Except(wrote.NotOnThisPc).Except(wrote.DidNotTake).ToList();
        if (back.Count > 0 || wrote.DidNotTake.Count > 0)
            log("Power plan: " + (back.Count > 0 ? "another tool had changed " + string.Join(", ", back) + "; Exo wrote " + (back.Count == 1 ? "it" : "them") + " again" : "another tool had changed values of Exo's plan")
                + (wrote.DidNotTake.Count > 0 ? (back.Count > 0 ? ", but" : ", and") + " Windows didn't take " + string.Join(", ", wrote.DidNotTake) : "") + ".");
        return new(back, wrote.DidNotTake);
    }

    /// <summary>
    /// Exo's plan, made and named when it doesn't exist (first use, or deleted since). A copy of Balanced; where Windows
    /// won't copy Balanced (a tweak tool or a debloat script can delete it), a copy of the plan that's on, which exists
    /// as long as Windows runs it, given Balanced's personality so Windows' Game Mode and
    /// LowLatency profiles and AMD's and Intel's Balanced tuning still apply; the profile then writes every value Exo
    /// sets. Each refusal goes to app.log with Windows' error code and whether Balanced is listed, and
    /// <paramref name="refused"/> is the last one's reason for the status line.
    /// </summary>
    private Guid? Ensure(Saved saved, Guid active, out string refused)
    {
        lock (Gate) return EnsureLocked(saved, active, out refused);
    }

    private Guid? EnsureLocked(Saved saved, Guid active, out string refused)
    {
        refused = "";
        if (saved.Plan is { } plan && api.Name(plan) is not null) return plan;
        // Exo's state was lost (a reinstall, a new data folder) but its plan is still there: use it, never a second one.
        if (Ours().OrderByDescending(p => p == active).FirstOrDefault() is var found && found != Guid.Empty)
        {
            log($"Power plan: found Exo's plan {found} again.");
            return found;
        }
        foreach (var source in Sources(active))
        {
            var code = api.Duplicate(source, out var made);
            if (code == 0 && made != Guid.Empty)
            {
                api.Rename(made, PlanName, PlanDescription);
                var personality = source == Balanced || (api.Write(made, NoSubgroup, PersonalitySetting, BalancedPersonality, dc: false)
                    & api.Write(made, NoSubgroup, PersonalitySetting, BalancedPersonality, dc: true));
                log($"Power plan: made Exo's plan {made} from {Describe(source)}{(personality ? "" : "; Windows refused Balanced's personality")}.");
                return made;
            }
            refused = Refusal(code);
            AppLog.Warn($"Power plan: Windows didn't copy {Describe(source)}: {refused}{(source == Balanced && api.Name(Balanced) is null ? "; Balanced isn't listed on this PC" : "")}.");
        }
        return null;
    }

    /// <summary>Exo's plans by name and Exo's own description, so a plan someone else named "Exo" is never taken or deleted.</summary>
    private List<Guid> Ours() => api.Schemes().Where(s => api.Name(s) == PlanName && api.Description(s) == PlanDescription).ToList();

    /// <summary>
    /// One Exo plan only: copies made by earlier versions each time Exo's state was lost are deleted, keeping the one Exo
    /// knows, else the one that's on, else the first. The plan that's on is never deleted (Windows refuses it anyway).
    /// </summary>
    private Saved Tidy(Saved saved, Guid active)
    {
        lock (Gate)
        {
            var ours = Ours();
            if (ours.Count == 0) return saved;
            var keep = ours.Contains(active) ? active : saved.Plan is { } known && ours.Contains(known) ? known : ours[0];
            var deleted = ours.Where(p => p != keep && p != active && api.Delete(p)).ToList();
            if (deleted.Count > 0) log($"Power plan: deleted {deleted.Count} extra Exo plan{(deleted.Count == 1 ? "" : "s")} ({string.Join(", ", deleted)}).");
            var next = saved with { Plan = keep, Retired = deleted.Count > 0 ? [.. saved.Retired ?? [], .. deleted] : saved.Retired };
            if (next != saved && Save(next)) saved = next;
            return saved;
        }
    }

    /// <summary>The plans Exo's is copied from, in order: Balanced, then the plan that's on.</summary>
    internal static IReadOnlyList<Guid> Sources(Guid active) => active == Balanced ? [Balanced] : [Balanced, active];

    /// <summary>Windows' reason for an error code, in its own words, and the code ("Access is denied (error 5)").</summary>
    internal static string Refusal(uint code) =>
        $"{new System.ComponentModel.Win32Exception((int)code).Message.TrimEnd('.', ' ')} (error {code})";

    private string Describe(Guid scheme) => $"{(scheme == Balanced ? "Balanced" : api.Name(scheme) is { } name ? $"“{name}”" : "the plan that's on")} {scheme}";

    /// <summary>
    /// Writes each value and reads it back (battery too where the profile writes it). A call Windows refuses is a setting this
    /// PC doesn't have; a call it accepts whose read back says another value didn't take. Neither counts as written.
    /// </summary>
    private Wrote Write(Guid plan, IEnumerable<PowerProfiles.Value> values, bool show)
    {
        List<string> didNotTake = [], notOnThisPc = [];
        var taken = 0;
        foreach (var value in values)
        {
            if (!api.Write(plan, value.Subgroup, value.Setting, value.Ac, dc: false) || (value.Dc && !api.Write(plan, value.Subgroup, value.Setting, value.Ac, dc: true)))
                notOnThisPc.Add(value.Name);
            else if (Differs(plan, value)) didNotTake.Add(value.Name);
            else taken++;
            // Shown in Control Panel's advanced settings (Windows hides several); needs an administrator, so a refusal only hides it.
            if (show) api.Show(value.Subgroup, value.Setting);
        }
        // Windows' other hidden settings worth tuning, shown too (left as they are unless the profile wrote them above).
        if (show) foreach (var (subgroup, setting) in WorthSeeing) api.Show(subgroup, setting);
        return new Wrote(taken, didNotTake, notOnThisPc);
    }

    private static readonly Guid ProcessorGroup = new("54533251-82be-4824-96c1-47b60b740d00"), UsbGroup = new("2a737441-1930-4402-8d77-b2bebba308a3");

    /// <summary>
    /// Hidden settings worth tuning, shown in Control Panel without being changed where the profile doesn't write them:
    /// boost, a frequency cap, every efficiency class's energy preference, state limits and parking (class 1 is a hybrid's
    /// P-cores), autonomous mode, how a hybrid places long and short threads and which policy is in effect, the latency
    /// hint, and USB 3 link power. Left hidden: what gaming plans turn without a measured effect (idle promote/demote, the
    /// check interval, throttle states) and what Windows stops using once the processor picks its own clocks (increase and
    /// decrease thresholds, times and policies).
    /// </summary>
    internal static readonly (Guid Subgroup, Guid Setting)[] WorthSeeing =
    [
        (ProcessorGroup, new("be337238-0d82-4146-a960-4f3749d470c7")), // boost mode (PERFBOOSTMODE)
        (ProcessorGroup, new("75b0ae3f-bce0-45a7-8c89-c9611c25e100")), // maximum frequency, MHz (PROCFREQMAX)
        (ProcessorGroup, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863")), // energy preference (PERFEPP)
        (ProcessorGroup, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6864")), // energy preference, efficiency class 1 (PERFEPP1)
        (ProcessorGroup, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6865")), // energy preference, efficiency class 2 (PERFEPP2)
        (ProcessorGroup, new("893dee8e-2bef-41e0-89c6-b55d0929964d")), // minimum state, efficiency class 1 (PROCTHROTTLEMIN1)
        (ProcessorGroup, new("bc5038f7-23e0-4960-96da-33abaf5935ed")), // maximum state, efficiency class 1 (PROCTHROTTLEMAX1)
        (ProcessorGroup, new("0cc5b647-c1df-4637-891a-dec35c318584")), // core parking min cores, efficiency class 1 (CPMINCORES1)
        (ProcessorGroup, new("ea062031-0e34-4ff1-9b6d-eb1059334028")), // core parking max cores (CPMAXCORES)
        (ProcessorGroup, new("8baa4a8a-14c6-4451-8e8b-14bdbd197537")), // autonomous mode (PERFAUTONOMOUS)
        (ProcessorGroup, new("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5")), // heterogeneous policy in effect (HETEROPOLICY)
        (ProcessorGroup, new("93b8b6dc-0698-4d1c-9ee4-0644e900c85d")), // heterogeneous thread scheduling (SCHEDPOLICY)
        (ProcessorGroup, new("bae08b81-2d5e-4688-ad6a-13243356654b")), // short running threads (SHORTSCHEDPOLICY)
        (ProcessorGroup, new("619b7505-003b-4e82-b7a6-4dd29c300971")), // latency sensitivity hint (LATENCYHINTPERF)
        (UsbGroup, new("d4e98f31-5ffe-4ce1-be31-1b38b384c009")),       // USB 3 link power management
    ];

    private Saved Load()
    {
        lock (StateGate)
        {
            for (var attempt = 0; attempt < StateIoAttempts; attempt++)
            {
                try
                {
                    if (!File.Exists(statePath)) return new(null, null, Guid.Empty, false);
                    using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    return JsonSerializer.Deserialize<Saved>(reader.ReadToEnd()) ?? new(null, null, Guid.Empty, false);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    return new(null, null, Guid.Empty, false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt + 1 >= StateIoAttempts) return new(null, null, Guid.Empty, false);
                    var delay = StateIoBaseDelayMs * (1 << attempt);
                    if (delay > 0) Thread.Sleep(delay);
                }
            }
            return new(null, null, Guid.Empty, false);
        }
    }

    private bool Save(Saved saved)
    {
        lock (StateGate)
        {
            var payload = JsonSerializer.Serialize(saved);
            var directory = Path.GetDirectoryName(statePath);
            for (var attempt = 0; attempt < StateIoAttempts; attempt++)
            {
                var temp = $"{statePath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllText(temp, payload);
                    File.Move(temp, statePath, overwrite: true);
                    temp = null;
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt + 1 >= StateIoAttempts)
                    {
                        AppLog.Warn("Power plan: Exo's plan state couldn't be saved: " + ex.Message);
                        return false;
                    }
                    var delay = StateIoBaseDelayMs * (1 << attempt);
                    if (delay > 0) Thread.Sleep(delay);
                }
                finally
                {
                    if (temp is not null)
                    {
                        try { if (File.Exists(temp)) DeletionGate.DeleteFile(temp); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                }
            }
            return false;
        }
    }
}

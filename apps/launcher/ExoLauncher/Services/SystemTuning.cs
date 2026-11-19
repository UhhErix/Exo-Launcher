using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Best settings › System: Windows' own settings below the power plan, each with its own switch, an exact record of what
/// was there, and detection read from Windows without administrator rights. Every write is one of the fixed lines below
/// (an allowlist in code: these keys, these values, these services), run in one elevated step Windows asks for once,
/// behind a System Restore point made in that same step. Research, sources and what was left out: docs/architecture/system-tuning.md.
/// <list type="bullet">
/// <item><c>timer</c> (Best settings): Windows' own timer back where a tweak forced the platform clock (bcdedit
/// useplatformclock, seen as a 14.318 MHz performance counter): every clock read a game makes then goes to the HPET, which
/// takes far longer than the processor's own counter. Only the value is removed, and only when the elevated step finds it.</item>
/// <item><c>scheduling</c> (Best settings): Win32PrioritySeparation back to Windows' "Programs" (2) where it was set to
/// fixed quanta or no foreground boost (Background services' 0x18 among them), so the game in front gets the longer turn.
/// 0x26 and the other variable, boosted values already are Programs and are left alone.</item>
/// <item><c>pagefile</c> (Best settings): a page file again, managed by Windows, where a tweak removed it: without one the
/// commit limit is the RAM, and a game that commits past it crashes out of memory. A size the user set stays theirs.</item>
/// <item><c>cache</c> (Best settings): LargeSystemCache back to 0 where it was 1, the server setting that favors the file
/// cache over programs' working sets, games included.</item>
/// <item><c>services</c> (opt-in): two services Windows starts at boot that a gaming PC has no use for, set to Manual (never
/// Disabled): Downloaded Maps Manager (Maps starts it when it needs it) and Program Compatibility Assistant (the "this program
/// might not have installed correctly" prompts for old installers), both "OK to disable" in Microsoft's own services guidance.
/// Only those that are Automatic now; each start type and delayed flag recorded first.</item>
/// <item><c>integrity</c> (opt-in): Memory Integrity (HVCI) off, Microsoft's own advice for gamers who want the last frames,
/// with a plain warning and a restart. Never in Best settings or a first run. Refused while an anti-cheat that can require it is
/// installed (Riot Vanguard, FACEIT), and where a policy or the firmware lock holds it.</item>
/// </list>
/// Turning a switch off puts back exactly what was recorded and keeps it out of Best settings until it's on again. Nothing
/// here needs TrustedInstaller: every key is writable by Administrators, and services change through the service manager
/// (some service keys grant Administrators only read).
/// </summary>
internal sealed partial class SystemTuning(SystemTuning.ISystem system, string path, Func<DateTimeOffset> now)
{
    internal const string TimerKey = "timer", SchedulingKey = "scheduling", PagefileKey = "pagefile", CacheKey = "cache", ServicesKey = "services", IntegrityKey = "integrity";

    /// <summary>The items Use best settings sets: each only puts back a Windows default a tweak changed.</summary>
    internal static readonly string[] Defaults = [TimerKey, SchedulingKey, PagefileKey, CacheKey];

    /// <summary>The items only their own switch sets.</summary>
    internal static readonly string[] OptIn = [ServicesKey, IntegrityKey];

    internal static readonly string[] Keys = [.. Defaults, .. OptIn];

    internal const string PriorityControl = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    internal const string Separation = "Win32PrioritySeparation";
    /// <summary>Windows' "Programs": short, variable quanta with the foreground's boost (what every client Windows ships).</summary>
    internal const int Programs = 2;

    internal const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    internal const string PagingFiles = "PagingFiles", LargeSystemCache = "LargeSystemCache";
    /// <summary>What Windows writes for "Automatically manage paging file size for all drives".</summary>
    internal const string WindowsManaged = @"?:\pagefile.sys";

    internal const string Hvci = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    internal const string HvciPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";

    /// <summary>The performance counter's rate when Windows reads the HPET (14.31818 MHz); the processor's own counter reads 10 MHz.</summary>
    internal const long HpetHz = 14_318_180;

    /// <summary>The only services this group changes, by name, with Windows' own name for them.</summary>
    internal static readonly (string Name, string Label)[] Trimmed =
    [
        ("MapsBroker", "Downloaded Maps Manager"),
        ("PcaSvc", "Program Compatibility Assistant Service"),
    ];

    /// <summary>Anti-cheats that can require Memory Integrity on, by their service, and who they are.</summary>
    internal static readonly (string Service, string Who)[] NeedIntegrity =
    [
        ("vgk", "Riot Vanguard (VALORANT, League of Legends)"),
        ("vgc", "Riot Vanguard (VALORANT, League of Legends)"),
        ("FACEIT", "FACEIT Anti-Cheat"),
        ("FACEITService", "FACEIT Anti-Cheat"),
    ];

    internal const string IntegrityWarning = "Less secure: Windows stops checking that every driver is signed and safe, which is what keeps malicious drivers out. Only for a PC you keep clean. Up to about 5-10% more frames in some games. Takes a restart.";

    internal interface ISystem
    {
        /// <summary>The performance counter's rate (QueryPerformanceFrequency).</summary>
        long Frequency();
        /// <summary>A DWORD under HKLM, or null when it isn't there (or isn't a DWORD).</summary>
        int? Dword(string key, string name);
        /// <summary>A REG_MULTI_SZ under HKLM, or null when the value isn't there.</summary>
        string[]? Strings(string key, string name);
        bool Exists(string key);
        /// <summary>A service's start type (2 automatic, 3 manual, 4 disabled) and delayed flag; null when there's no such service.</summary>
        (int Start, bool Delayed)? Service(string name);
        /// <summary>Memory Integrity runs now (Windows' code integrity options); null when Windows doesn't say.</summary>
        bool? IntegrityRunning();
        DateTimeOffset Boot();
        /// <summary>Runs the lines as administrator, Windows asking once; false when it was declined or didn't run.</summary>
        Task<bool> Admin(IReadOnlyList<string> lines);
        /// <summary>A fresh file path the elevated step writes an answer to (no quotes in it).</summary>
        string Answer();
        /// <summary>What the elevated step wrote there (null: nothing), and the file goes unless <paramref name="keep"/>.</summary>
        string? ReadAnswer(string answer, bool keep = false);
        /// <summary>A restore point made in the last day stands for a new one (Windows keeps one a day).</summary>
        bool PointStanding { get; }
        void PointMade(RestorePoint.Outcome outcome);
    }

    /// <summary>What a service was before Exo set it to Manual.</summary>
    internal sealed record ServiceWas(string Name, int Start, bool Delayed);

    /// <summary>
    /// What one item held before Exo changed it: a DWORD (<paramref name="Value"/>, null with <paramref name="Absent"/>: not
    /// there), a list of strings (the page file), each service's start, or the platform clock forced on. <paramref name="Probe"/>
    /// is the timer's change under way: the file its elevated step writes Windows' boot entry to before it removes anything,
    /// saved before the step, so a step Exo didn't see finish is settled from it the next time Exo reads (<see cref="Settle"/>).
    /// </summary>
    internal sealed record Was(int? Value = null, string[]? Strings = null, bool Absent = false, List<ServiceWas>? Services = null, bool Clock = false, string? Probe = null, DateTimeOffset? ProbeAt = null);

    /// <summary>
    /// What Exo keeps: the items the player turned off, what each changed item held, when each was last changed (a restart
    /// finishes it), and items a press found nothing to change for (the HPET Windows picks itself on this PC).
    /// </summary>
    internal sealed record Saved(List<string> Off, Dictionary<string, Was> Kept, Dictionary<string, DateTimeOffset> Changed, List<string> Native);

    /// <summary>
    /// One item: <paramref name="On"/> Exo's setting is in place; <paramref name="Restart"/> it was changed (or put back) since
    /// Windows started, so a restart finishes it; <paramref name="Blocked"/> why Exo won't change it here.
    /// </summary>
    internal sealed record Item(string Key, string Label, string Detail, bool On, bool CanUndo, bool Off, bool OptIn, bool Restart, string? Blocked = null);

    internal sealed record State(IReadOnlyList<Item> Items)
    {
        /// <summary>Every item Best settings sets is set or turned off by the player.</summary>
        internal bool Applied => Items.Where(i => !i.OptIn).All(i => i.On || i.Off || i.Blocked is not null);
        internal bool Restart => Items.Any(i => i.Restart);
    }

    /// <summary>The items as they are, a timer change Exo didn't see finish settled first (unless a change is running now).</summary>
    internal State Read()
    {
        if (!_gate.Wait(0)) return StateOf(Load());
        try { return StateOf(Settled()); }
        finally { _gate.Release(); }
    }

    private State StateOf(Saved saved)
    {
        var boot = system.Boot();
        bool Pending(string key) => saved.Changed.TryGetValue(key, out var at) && at > boot;
        var items = new List<Item>();
        var off = saved.Off;

        // A change waits on a restart: said first, whichever way it went.
        string Tell(string key, string normal) => !Pending(key) ? normal : saved.Kept.ContainsKey(key) ? "Restart to finish." : "Goes back to how it was after a restart.";

        // The timer: the performance counter says which clock Windows reads.
        var hpet = system.Frequency() == HpetHz;
        var timerKept = saved.Kept.TryGetValue(TimerKey, out var timerWas) && timerWas.Clock;
        if (timerKept || Pending(TimerKey))
            items.Add(new(TimerKey, "Windows' own timer", Tell(TimerKey, hpet ? "Windows still reads the platform clock (HPET)." : "Games read the processor's fast clock, not the platform clock (HPET)."),
                timerKept && (!hpet || Pending(TimerKey)), timerKept, off.Contains(TimerKey), false, Pending(TimerKey)));
        else if (hpet && !saved.Native.Contains(TimerKey))
            items.Add(new(TimerKey, "Windows' own timer", "A tweak forced the platform clock (HPET), so every clock read a game makes waits on it. Takes a restart.", false, false, off.Contains(TimerKey), false, false));

        // Scheduling: Programs, or anything that gives the foreground a longer, boosted turn.
        var separation = system.Dword(PriorityControl, Separation);
        var programs = separation is not { } v || ForegroundFirst(v);
        if (!programs || saved.Kept.ContainsKey(SchedulingKey) || Pending(SchedulingKey))
            items.Add(new(SchedulingKey, "Windows' scheduling for programs", Tell(SchedulingKey, programs ? "The game in front gets the longer turn on the processor." : "Set to favor background work, so the game in front gets no longer turn than anything behind it."),
                programs, saved.Kept.ContainsKey(SchedulingKey), off.Contains(SchedulingKey), false, Pending(SchedulingKey)));

        // The page file.
        var none = NoPageFile(system.Strings(MemoryManagement, PagingFiles));
        if (none || saved.Kept.ContainsKey(PagefileKey) || Pending(PagefileKey))
            items.Add(new(PagefileKey, "A page file, managed by Windows", Tell(PagefileKey, none ? "There's no page file, so a game that needs more memory than your RAM crashes instead of slowing down." : "Windows sizes it for you."),
                !none, saved.Kept.ContainsKey(PagefileKey), off.Contains(PagefileKey), false, Pending(PagefileKey)));

        // The file cache.
        var large = system.Dword(MemoryManagement, LargeSystemCache) == 1;
        if (large || saved.Kept.ContainsKey(CacheKey) || Pending(CacheKey))
            items.Add(new(CacheKey, "Memory for programs first", Tell(CacheKey, large ? "Set to the server setting, which gives Windows' file cache memory before your games." : "Games keep their memory before Windows' file cache."),
                !large, saved.Kept.ContainsKey(CacheKey), off.Contains(CacheKey), false, Pending(CacheKey)));

        // Services: only those Windows starts at boot now. On while Exo holds a record of any it set to Manual, so its switch
        // always puts those back, even when Windows kept another starting with it (turning it on again tries that one again).
        var automatic = Trimmed.Where(s => system.Service(s.Name) is { Start: 2 }).ToList();
        var servicesKept = saved.Kept.ContainsKey(ServicesKey);
        if (automatic.Count > 0 || servicesKept || Pending(ServicesKey))
        {
            var manual = Trimmed.Where(s => saved.Kept.GetValueOrDefault(ServicesKey)?.Services?.Any(w => w.Name == s.Name) == true && automatic.All(a => a.Name != s.Name)).ToList();
            var detail = !servicesKept || manual.Count == 0
                ? $"{Join(automatic.Select(s => s.Label))} start{S(automatic)} with Windows; Manual starts {Them(automatic)} only when something needs {Them(automatic)}. Takes a restart."
                : automatic.Count == 0
                    ? $"{Join(manual.Select(s => s.Label))} start{S(manual)} only when something needs {Them(manual)}."
                    : $"{Join(manual.Select(s => s.Label))} start{S(manual)} only when something needs {Them(manual)}; {Join(automatic.Select(s => s.Label))} still start{S(automatic)} with Windows.";
            items.Add(new(ServicesKey, "Services set to Manual", Tell(ServicesKey, detail), servicesKept, servicesKept, false, true, Pending(ServicesKey)));
        }

        // Memory Integrity: shown while it's on (or Exo turned it off).
        var enabled = system.Dword(Hvci, "Enabled");
        var running = system.IntegrityRunning();
        if (enabled == 1 || running == true || saved.Kept.ContainsKey(IntegrityKey) || Pending(IntegrityKey))
        {
            var isOff = enabled == 0;
            var blocked = isOff ? null : IntegrityBlocked();
            var restart = Pending(IntegrityKey) || isOff && running == true;
            var detail = isOff ? restart ? "Restart to finish. Turn this off to put Memory Integrity back on." : "Memory Integrity is off. Turn this off to put it back on."
                : Pending(IntegrityKey) ? "Goes back on after a restart." : blocked ?? IntegrityWarning;
            items.Add(new(IntegrityKey, "Memory Integrity off", detail, isOff, saved.Kept.ContainsKey(IntegrityKey), false, true, restart, blocked));
        }
        return new State(items);
    }

    /// <summary>Why Memory Integrity stays on here: an anti-cheat that can require it, a policy, or the firmware lock. Null when Exo may turn it off.</summary>
    internal string? IntegrityBlocked()
    {
        var who = NeedIntegrity.Where(a => system.Exists($@"SYSTEM\CurrentControlSet\Services\{a.Service}")).Select(a => a.Who).Distinct().ToList();
        if (who.Count > 0) return $"{Join(who)} can require Memory Integrity, so Exo leaves it on: games with it may refuse to start without it.";
        if (system.Dword(HvciPolicy, "HypervisorEnforcedCodeIntegrity") is { } policy && policy != 0) return "A policy on this PC turns Memory Integrity on, so Exo leaves it.";
        if (system.Dword(Hvci, "Locked") == 1) return "Memory Integrity is locked on in this PC's firmware, so Exo leaves it.";
        return null;
    }

    /// <summary>
    /// The foreground gets a longer, boosted turn: variable quanta (fixed is 10 in bits 2-3) and a boost (bits 0-1 not 00).
    /// Bits 4-5 (short or long) don't decide it. 2 (Programs), 0x26 and 0x16 pass; 0x18 (Background services), 0x28 and 0x24 don't.
    /// </summary>
    internal static bool ForegroundFirst(int value) => ((value >> 2) & 3) != 2 && (value & 3) != 0;

    /// <summary>No page file: the value missing, or only empty lines (what "No paging file" leaves).</summary>
    internal static bool NoPageFile(string[]? paging) => paging is null || paging.All(string.IsNullOrWhiteSpace);

    private static string S<T>(IReadOnlyCollection<T> list) => list.Count == 1 ? "s" : "";
    private static string Them<T>(IReadOnlyCollection<T> list) => list.Count == 1 ? "it" : "them";

    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    internal const string Busy = "Exo is still changing your system settings. Try again when it's done.";

    /// <summary>
    /// Sets the items in <paramref name="only"/> (a switch turned on: taken out of Off), or with null every Best settings item
    /// the player keeps on that isn't set yet (never an opt-in one), in one elevated step behind a restore point. Null when all
    /// went; else what didn't, plainly.
    /// </summary>
    internal async Task<string?> ApplyAsync(IReadOnlyCollection<string>? only = null)
    {
        if (!_gate.Wait(0)) return Busy;
        try { return Logged(await ApplyNowAsync(only).ConfigureAwait(false)); }
        finally { _gate.Release(); }
    }

    /// <summary>Turns one item off: puts back what Exo recorded and keeps it out of Best settings until it's on again.</summary>
    internal async Task<string?> TurnOffAsync(string key)
    {
        if (!_gate.Wait(0)) return Busy;
        try { return Logged(await TurnOffNowAsync(key).ConfigureAwait(false)); }
        finally { _gate.Release(); }
    }

    private static string? Logged(string? said)
    {
        if (said is not null) AppLog.Warn("System best settings: " + said);
        return said;
    }

    private async Task<string?> ApplyNowAsync(IReadOnlyCollection<string>? only)
    {
        if (only is not null && only.Any(k => !Keys.Contains(k))) return "Exo doesn't know that setting.";
        var saved = Settled();
        // What a declined prompt puts back: the player's choices as they were, an Off this press took out included.
        var prior = saved;
        if (only is not null && saved.Off.Any(only.Contains))
        {
            saved = saved with { Off = [.. saved.Off.Where(k => !only.Contains(k))] };
            if (!Save(saved)) return "Exo couldn't save your choice, so nothing changed.";
        }
        var state = StateOf(saved);
        var asked = state.Items.Where(i => !i.On && (only?.Contains(i.Key) ?? (!i.OptIn && !i.Off))).ToList();
        if (asked.FirstOrDefault(i => i.Blocked is not null) is { } held && only is not null) return held.Blocked;
        var wanted = asked.Where(i => i.Blocked is null).Select(i => i.Key).ToList();
        if (wanted.Count == 0) return null;

        var lines = new List<string>();
        var kept = new Dictionary<string, Was>(saved.Kept);
        var said = new List<string>();
        string? clockWas = null, clockAfter = null;
        foreach (var key in wanted)
        {
            switch (key)
            {
                case TimerKey:
                    clockWas = system.Answer();
                    clockAfter = system.Answer();
                    // Saved with the record below, before the step: what it finds there is settled even if Exo never sees it end.
                    kept[key] = (kept.TryGetValue(key, out var earlier) ? earlier : new Was()) with { Probe = clockWas, ProbeAt = now() };
                    lines.AddRange(TimerLines(clockWas, clockAfter));
                    break;
                case SchedulingKey:
                {
                    var was = system.Dword(PriorityControl, Separation);
                    kept.TryAdd(key, new Was(was, Absent: was is null));
                    lines.Add(RegDword(PriorityControl, Separation, Programs));
                    break;
                }
                case PagefileKey:
                {
                    var was = system.Strings(MemoryManagement, PagingFiles);
                    if (was is not null && was.Any(s => !SafeText().IsMatch(s)))
                    {
                        said.Add("Exo didn't recognize your page file setting, so it didn't change it.");
                        continue;
                    }
                    kept.TryAdd(key, new Was(Strings: was, Absent: was is null));
                    lines.Add(RegStrings(MemoryManagement, PagingFiles, [WindowsManaged]));
                    break;
                }
                case CacheKey:
                {
                    var was = system.Dword(MemoryManagement, LargeSystemCache);
                    kept.TryAdd(key, new Was(was, Absent: was is null));
                    lines.Add(RegDword(MemoryManagement, LargeSystemCache, 0));
                    break;
                }
                case ServicesKey:
                {
                    var now = Trimmed.Select(s => (s.Name, Start: system.Service(s.Name))).Where(s => s.Start is { Start: 2 })
                        .Select(s => new ServiceWas(s.Name, s.Start!.Value.Start, s.Start.Value.Delayed)).ToList();
                    var record = kept.TryGetValue(key, out var old) ? old.Services ?? [] : [];
                    kept[key] = new Was(Services: [.. record, .. now.Where(n => record.All(r => r.Name != n.Name))]);
                    lines.AddRange(now.Select(s => Sc(s.Name, "demand")));
                    break;
                }
                case IntegrityKey:
                {
                    var was = system.Dword(Hvci, "Enabled");
                    kept.TryAdd(key, new Was(was, Absent: was is null));
                    lines.Add(RegDword(Hvci, "Enabled", 0));
                    break;
                }
            }
        }
        if (lines.Count == 0) return Said(said);

        // Recorded before anything changes, so a crash or a step cut short still has the way back.
        var changed = new Dictionary<string, DateTimeOffset>(saved.Changed);
        foreach (var key in wanted.Where(k => k != TimerKey && kept.ContainsKey(k))) changed[key] = now();
        if (!Save(saved with { Kept = kept, Changed = changed })) return "Your current settings couldn't be saved for Undo, so nothing changed.";

        // A restore point in the same step, first (one Windows keeps from the last day stands for it). Exo's own record is the
        // way back for each item, so a PC with System Restore off still gets the change, and is told there's no point.
        var point = system.PointStanding ? null : system.Answer();
        var steps = point is null ? lines : [RestorePoint.Step(point, "Exo Launcher: before system settings", "MODIFY_SETTINGS"), .. lines];
        if (!await system.Admin(steps).ConfigureAwait(false))
        {
            if (clockWas is not null) { system.ReadAnswer(clockWas); system.ReadAnswer(clockAfter!); }
            if (point is not null) system.ReadAnswer(point);
            Save(prior);
            return "Windows' permission was declined, so nothing changed.";
        }
        if (point is not null)
        {
            var outcome = RestorePoint.Read(system.ReadAnswer(point));
            system.PointMade(outcome);
            if (outcome is RestorePoint.Outcome.Off or RestorePoint.Outcome.Failed)
                said.Add(outcome == RestorePoint.Outcome.Off ? "System Restore is off on this PC, so there's no restore point; each switch still puts its setting back." : "Windows didn't make a restore point; each switch still puts its setting back.");
        }

        // Read back: what didn't take keeps no record, so Off never "puts back" a change that never happened.
        var after = saved with { Kept = kept, Changed = changed };
        foreach (var key in wanted)
        {
            switch (key)
            {
                case TimerKey:
                {
                    var left = Forced(system.ReadAnswer(clockAfter!));
                    after = Settle(after, out var note);
                    if (note is not null) said.Add(note);
                    else if (left == true)
                    {
                        after = Drop(after, prior, key);
                        said.Add("Windows didn't take its own timer back.");
                    }
                    break;
                }
                case SchedulingKey when system.Dword(PriorityControl, Separation) != Programs:
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't take its scheduling for programs.");
                    break;
                case PagefileKey when NoPageFile(system.Strings(MemoryManagement, PagingFiles)) && after.Kept.ContainsKey(key):
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't take a page file.");
                    break;
                case CacheKey when system.Dword(MemoryManagement, LargeSystemCache) == 1:
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't take its memory setting.");
                    break;
                case ServicesKey:
                {
                    var still = Trimmed.Where(s => system.Service(s.Name) is { Start: 2 }).Select(s => s.Name).ToHashSet();
                    if (still.Count > 0)
                    {
                        var services = after.Kept[key].Services!.Where(w => !still.Contains(w.Name) || prior.Kept.GetValueOrDefault(key)?.Services?.Any(p => p.Name == w.Name) == true).ToList();
                        after = services.Count == 0 ? Drop(after, prior, key) : after with { Kept = new(after.Kept) { [key] = new Was(Services: services) } };
                        said.Add($"Windows didn't set {Join(Trimmed.Where(s => still.Contains(s.Name)).Select(s => s.Label))} to Manual.");
                    }
                    break;
                }
                case IntegrityKey when system.Dword(Hvci, "Enabled") == 1:
                    after = Drop(after, prior, key);
                    said.Add("Windows didn't turn Memory Integrity off.");
                    break;
            }
        }
        // The probe goes only once what it settled is saved; until then the record saved before the step still names it.
        if (!Save(after)) said.Add("Exo couldn't save what changed; it reads it again next time.");
        else if (clockWas is not null) system.ReadAnswer(clockWas);
        AppLog.Info($"System best settings: {string.Join(", ", wanted)}{(said.Count > 0 ? "; " + string.Join(" ", said) : "")}.");
        return Said(said);
    }

    /// <summary>An item that didn't take: its record goes back to what the press found (a record from before stays).</summary>
    private static Saved Drop(Saved after, Saved prior, string key)
    {
        var kept = new Dictionary<string, Was>(after.Kept);
        var changed = new Dictionary<string, DateTimeOffset>(after.Changed);
        if (prior.Kept.TryGetValue(key, out var was)) kept[key] = was; else kept.Remove(key);
        if (prior.Changed.TryGetValue(key, out var at)) changed[key] = at; else changed.Remove(key);
        return after with { Kept = kept, Changed = changed };
    }

    private async Task<string?> TurnOffNowAsync(string key)
    {
        if (!Keys.Contains(key)) return "Exo doesn't know that setting.";
        var saved = Settled();
        if (saved.Kept.TryGetValue(key, out var was))
        {
            if (Revert(key, was) is not { } lines) return "Exo's record of this setting couldn't be read, so nothing changed.";
            // The timer's BCD value can't be read without administrator rights: the same step reads it back after setting it.
            var check = key == TimerKey ? system.Answer() : null;
            if (check is not null) lines = [.. lines, TimerRead(check)];
            if (!await system.Admin(lines).ConfigureAwait(false))
            {
                if (check is not null) system.ReadAnswer(check);
                return "Windows' permission was declined, so nothing changed.";
            }
            var back = key switch
            {
                SchedulingKey => system.Dword(PriorityControl, Separation) == was.Value,
                PagefileKey => SameStrings(system.Strings(MemoryManagement, PagingFiles), was.Strings),
                CacheKey => system.Dword(MemoryManagement, LargeSystemCache) == was.Value,
                ServicesKey => was.Services!.All(s => system.Service(s.Name) is { } now && now.Start == s.Start && now.Delayed == s.Delayed),
                IntegrityKey => system.Dword(Hvci, "Enabled") == was.Value,
                _ => Forced(system.ReadAnswer(check!)) == true,
            };
            if (!back) return "Windows didn't put it back. Try again.";
            var kept = new Dictionary<string, Was>(saved.Kept);
            kept.Remove(key);
            saved = saved with { Kept = kept, Changed = new(saved.Changed) { [key] = now() } };
        }
        else if (key is IntegrityKey or ServicesKey) return "Exo didn't change this, so there's nothing to put back.";
        if (Defaults.Contains(key) && !saved.Off.Contains(key)) saved = saved with { Off = [.. saved.Off, key] };
        return Save(saved) ? null : "Exo couldn't save your choice.";
    }

    private static bool SameStrings(string[]? a, string[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);

    /// <summary>The lines that put back what <paramref name="was"/> recorded; null when the record holds something Exo wouldn't write.</summary>
    internal static IReadOnlyList<string>? Revert(string key, Was was) => key switch
    {
        TimerKey => was.Clock ? [$"{Tool("bcdedit.exe")} /set {{current}} useplatformclock true"] : [],
        SchedulingKey => [was.Absent ? RegDelete(PriorityControl, Separation) : RegDword(PriorityControl, Separation, was.Value ?? Programs)],
        PagefileKey => was.Absent ? [RegDelete(MemoryManagement, PagingFiles)] : was.Strings is { } s && s.All(SafeText().IsMatch) ? [RegStrings(MemoryManagement, PagingFiles, s)] : null,
        CacheKey => [was.Absent ? RegDelete(MemoryManagement, LargeSystemCache) : RegDword(MemoryManagement, LargeSystemCache, was.Value ?? 0)],
        ServicesKey => was.Services is { } list && list.All(s => Trimmed.Any(t => t.Name == s.Name))
            ? list.Select(s => Sc(s.Name, s.Start switch { 2 => s.Delayed ? "delayed-auto" : "auto", 3 => "demand", 4 => "disabled", _ => "auto" })).ToList()
            : null,
        IntegrityKey => [was.Absent ? RegDelete(Hvci, "Enabled") : RegDword(Hvci, "Enabled", was.Value ?? 1)],
        _ => null,
    };

    /// <summary>
    /// The timer's lines: bcdedit's entry for this Windows into <paramref name="was"/>; the value removed only when it's there;
    /// then the same read into <paramref name="after"/>, so Exo knows what it found and what's left.
    /// </summary>
    internal static IReadOnlyList<string> TimerLines(string was, string after) =>
    [
        TimerRead(was),
        $"({Tool("findstr.exe")} /i /c:\"useplatformclock\" \"{Quoted(was)}\" >nul && {Tool("bcdedit.exe")} /deletevalue {{current}} useplatformclock)",
        TimerRead(after),
    ];

    /// <summary>What a read of the boot entry ends with when bcdedit read it: without it the read failed, and says nothing.</summary>
    internal const string ReadOk = "exo-read-ok";

    /// <summary>bcdedit's entry for this Windows into <paramref name="answer"/>, then <see cref="ReadOk"/> only when bcdedit read it.</summary>
    internal static string TimerRead(string answer) =>
        $"({Tool("bcdedit.exe")} /enum {{current}} > \"{Quoted(answer)}\" && echo {ReadOk}>>\"{Quoted(answer)}\")";

    /// <summary>
    /// The platform clock forced in a boot entry read (<see cref="TimerRead"/>): true when the value is there (seen in any
    /// read, even one cut short), false when bcdedit read the entry and it isn't, null when the read didn't happen or failed.
    /// </summary>
    internal static bool? Forced(string? read) =>
        read is null ? null
        : read.Contains("useplatformclock", StringComparison.OrdinalIgnoreCase) ? true
        : read.Contains(ReadOk, StringComparison.Ordinal) ? false
        : null;

    internal static string RegDword(string key, string name, int value) =>
        $"{Tool("reg.exe")} add \"HKLM\\{key}\" /v {name} /t REG_DWORD /d {value} /f";

    internal static string RegStrings(string key, string name, IReadOnlyList<string> values) =>
        $"{Tool("reg.exe")} add \"HKLM\\{key}\" /v {name} /t REG_MULTI_SZ /d \"{string.Join("\\0", values)}\" /f";

    internal static string RegDelete(string key, string name) => $"{Tool("reg.exe")} delete \"HKLM\\{key}\" /v {name} /f";

    internal static string Sc(string service, string start) => $"{Tool("sc.exe")} config {service} start= {start}";

    /// <summary>A program in System32, unquoted (the folder has no spaces), so the step never starts with a quote cmd would strip.</summary>
    private static string Tool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    private static string Quoted(string answer) => answer.Replace("\"", "", StringComparison.Ordinal);

    /// <summary>A page file line Exo writes back as it was: a drive or ?, a path, sizes; nothing cmd or reg reads specially.</summary>
    [GeneratedRegex(@"^([A-Za-z?]:\\[^""%^&|<>\r\n\\]*(\\[^""%^&|<>\r\n\\]+)*( \d+ \d+)?)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex SafeText();

    private static string? Said(List<string> said) => said.Count == 0 ? null : string.Join(" ", said);

    /// <summary>
    /// The timer's change under way, settled from what its step read before removing anything: the clock was forced (Exo's
    /// record: Off forces it again), it wasn't (Windows picks the HPET itself here: nothing to change, and the timer isn't
    /// offered again), or the read never came (nothing was removed, since the removal runs only behind it, and the press can be
    /// tried again). A record from an earlier change stays whatever the read says. <paramref name="note"/> says why nothing changed.
    /// </summary>
    private Saved Settle(Saved saved, out string? note)
    {
        note = null;
        if (!saved.Kept.TryGetValue(TimerKey, out var record) || record.Probe is not { } probe) return saved;
        var forced = Forced(system.ReadAnswer(probe, keep: true));
        var kept = new Dictionary<string, Was>(saved.Kept);
        var changed = new Dictionary<string, DateTimeOffset>(saved.Changed);
        var native = saved.Native;
        if (forced == true) { kept[TimerKey] = new Was(Clock: true); changed[TimerKey] = record.ProbeAt ?? now(); }
        else if (record.Clock) kept[TimerKey] = record with { Probe = null, ProbeAt = null };
        else kept.Remove(TimerKey);
        if (forced == false && !record.Clock)
        {
            native = [.. native.Where(k => k != TimerKey), TimerKey];
            note = "Windows picks the platform clock on this PC by itself, so there's nothing to change.";
        }
        else if (forced is null) note = "Exo couldn't read Windows' timer setting, so it didn't change it. Try again.";
        return saved with { Kept = kept, Changed = changed, Native = native };
    }

    /// <summary>What Exo keeps, a timer step it didn't see finish settled and saved (its probe goes once that's saved).</summary>
    private Saved Settled()
    {
        var saved = Load();
        var settled = Settle(saved, out _);
        if (!ReferenceEquals(settled, saved) && Save(settled)) system.ReadAnswer(saved.Kept[TimerKey].Probe!);
        return settled;
    }

    private Saved Load()
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { } saved)
                return saved with { Off = saved.Off ?? [], Kept = saved.Kept ?? [], Changed = saved.Changed ?? [], Native = saved.Native ?? [] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { AppLog.Debug("System best settings: the record couldn't be read: " + ex.GetType().Name); }
        return new Saved([], [], [], []);
    }

    private bool Save(Saved saved)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("System best settings: the record couldn't be saved: " + ex.Message);
            return false;
        }
    }
}

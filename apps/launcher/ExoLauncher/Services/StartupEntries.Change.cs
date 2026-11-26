using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>Removing, cleaning and putting back: each is a list of changes handed over once, then read back, and only what the read-back confirms is recorded.</summary>
internal static partial class StartupEntries
{
    /// <summary>
    /// What Remove took out of startup, enough to put it back exactly: a Run value's data and kind, its StartupApproved value as
    /// it was (Approval; null: it had none, so it started), whether a task was on (and its definition, when Exo deleted it
    /// because its program was gone), a Store task's state, a service's start type. <see cref="ValueKept"/>: the Run value stayed
    /// where it was (the way Task Manager turns it off); an older Exo deleted it, and Put back writes it again.
    /// </summary>
    internal sealed record Removal(string Id, string Name, DateTime At, string? Value = null, bool Expand = false, string? Kept = null, int? State = null,
        byte[]? Approval = null, bool WasOn = true)
    {
        /// <summary>The program plus its arguments, so the same item under another name is matched.</summary>
        public string? Fingerprint { get; init; }
        /// <summary>The program it starts, for the lock check and the catalog.</summary>
        public string? Program { get; init; }
        public bool ValueKept { get; init; }
        /// <summary>A service's Start value before Exo set it to Disabled.</summary>
        public int? ServiceStart { get; init; }
        /// <summary>
        /// The other ids the same item has had since Exo removed it (a task made again under a new GUID, a Run value with a
        /// new hash in its name), each with what it takes to put it back: Put back restores every one of them.
        /// </summary>
        public List<Also>? Renamed { get; init; }
    }

    /// <summary>One id of a removed item, as it was: enough to put that one back too.</summary>
    internal sealed record Also(string Id, string Name, string? Value = null, bool Expand = false, int? State = null, byte[]? Approval = null, bool WasOn = true, int? ServiceStart = null);

    private static readonly Lock Gate = new();
    internal static Lock Sync => Gate;

    private static string KeptPath(IStartupHost host) => Path.Combine(host.DataFolder, "startup-removed.json");
    internal static string KeptFolder(IStartupHost host) => Path.Combine(host.DataFolder, "startup-removed");

    /// <summary>What Exo removed from startup, newest first.</summary>
    internal static IReadOnlyList<Removal> Kept() => Kept(Host);

    internal static IReadOnlyList<Removal> Kept(IStartupHost host)
    {
        // Windows' own tasks an earlier build kept are Clean up's extras now, with nothing to put back.
        try
        {
            return host.ReadFile(KeptPath(host)) is { } text
                ? (JsonSerializer.Deserialize<List<Removal>>(text) ?? []).Where(item => !item.Id.StartsWith(@"task:\Microsoft\", StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static void Keep(IStartupHost host, IReadOnlyList<Removal> list) => host.WriteFile(KeptPath(host), JsonSerializer.Serialize(list));

    /// <summary>"run:cu:Steam" as ("run:cu", "Steam"); "task:\X" as ("task", "\X"); "package:F!T" as ("package", "F!T"); "service:S" as ("service", "S").</summary>
    internal static (string Kind, string Key) Split(string id)
    {
        var first = id.IndexOf(':');
        if (first < 0) return ("", id);
        var head = id[..first];
        if (head is "task" or "package" or "service") return (head, id[(first + 1)..]);
        var second = id.IndexOf(':', first + 1);
        return second < 0 ? ("", id) : (id[..second], id[(second + 1)..]);
    }

    private static RunPlace? PlaceOf(string kind) => RunPlaces.FirstOrDefault(place => place.Prefix == kind + ":");

    /// <summary>
    /// Whether turning <paramref name="id"/> on or off needs administrator rights: the machine's own Run keys, the machine's
    /// Startup folder, a task, and a service. A user's own item never does.
    /// </summary>
    internal static bool MachineWide(string id)
    {
        var (kind, _) = Split(id);
        return kind.EndsWith(":lm", StringComparison.Ordinal) || kind == "service";
    }

    // ---- What "off" is, for each place ----

    /// <summary>
    /// The changes that turn <paramref name="id"/> off the way Windows' own tools do (9-startup §A); null when Exo can't. A Run
    /// value or a Startup folder's shortcut stays and StartupApproved is marked off, a task is disabled, a Store task is set to
    /// disabled by the user, a RunOnce value is taken out, a service's start type is Disabled.
    /// </summary>
    internal static IReadOnlyList<StartupChange>? OffChanges(IStartupHost host, string id)
    {
        var (kind, rest) = Split(id);
        var off = RegValue.Bin(StartupApps.Approved(false, host.UtcNow));
        if (kind == "folder:cu" || kind == "folder:lm")
            return [new SetReg(kind == "folder:lm" ? Hive.Machine : Hive.User, StartupKeys.Approved + "StartupFolder", rest, off)];
        if (kind == "task") return CmdSafe(rest) && rest.Length > 0 ? [new SetTask(rest, On: false)] : null;
        if (kind == "package")
        {
            var (family, task) = FamilyAndTask(rest);
            return family.Length == 0 || task.Length == 0 ? null : [new SetReg(Hive.User, $@"{StartupKeys.Packaged}\{family}\{task}", "State", RegValue.DWord(1))];
        }
        if (kind == "service") return [new SetReg(Hive.Machine, StartupKeys.Services + "\\" + rest, "Start", RegValue.DWord(4))];
        if (PlaceOf(kind) is not { ReadOnly: false } place) return null;
        return place.ApprovedKey is { } approved
            ? [new SetReg(place.Hive, StartupKeys.Approved + approved, rest, off)]
            : [new SetReg(place.Hive, place.Key, rest, null)];
    }

    /// <summary>The changes that turn <paramref name="id"/> back on when it was disabled outside Exo (e.g. Task Manager); null when Exo shouldn't guess.</summary>
    internal static IReadOnlyList<StartupChange>? OnChanges(IStartupHost host, string id)
    {
        var (kind, rest) = Split(id);
        var on = RegValue.Bin(StartupApps.Approved(true, host.UtcNow));
        if (kind is "folder:cu" or "folder:lm")
            return [new SetReg(kind == "folder:lm" ? Hive.Machine : Hive.User, StartupKeys.Approved + "StartupFolder", rest, on)];
        if (kind == "task") return CmdSafe(rest) && rest.Length > 0 ? [new SetTask(rest, On: true)] : null;
        if (kind == "package")
        {
            var (family, task) = FamilyAndTask(rest);
            return family.Length == 0 || task.Length == 0 ? null : [new SetReg(Hive.User, $@"{StartupKeys.Packaged}\{family}\{task}", "State", RegValue.DWord(2))];
        }
        // Services vary between Automatic and Manual, so Exo never guesses an external service's start type.
        if (kind == "service") return null;
        if (PlaceOf(kind) is not { ReadOnly: false, ApprovedKey: { } approved } place) return null;
        return [new SetReg(place.Hive, StartupKeys.Approved + approved, rest, on)];
    }

    private static (string Family, string Task) FamilyAndTask(string rest)
    {
        var bang = rest.IndexOf('!');
        return bang < 0 ? ("", "") : (rest[..bang], rest[(bang + 1)..]);
    }

    /// <summary>The changes that put <paramref name="kept"/> back as it was: StartupApproved as it was (deleted when it had none), the Run value when it is gone, the task's state, the Store task's state.</summary>
    internal static (IReadOnlyList<StartupChange> Changes, string? Error) RestoreChanges(IStartupHost host, Removal kept)
    {
        // Every id the item has had since (renamed by its program): each is put back as it was, not only the first.
        var changes = new List<StartupChange>();
        if (kept.Renamed is { Count: > 0 })
            foreach (var also in kept.Renamed)
            {
                var other = kept with { Id = also.Id, Name = also.Name, Value = also.Value, Expand = also.Expand, State = also.State, Approval = also.Approval, WasOn = also.WasOn, ServiceStart = also.ServiceStart };
                var (more, why) = RestoreOne(host, other);
                if (why is not null) return ([], why);
                changes.AddRange(more);
            }
        var (own, error) = RestoreOne(host, kept);
        changes.AddRange(own);
        return (changes, error);
    }

    private static (IReadOnlyList<StartupChange> Changes, string? Error) RestoreOne(IStartupHost host, Removal kept)
    {
        var (kind, rest) = Split(kept.Id);
        var changes = new List<StartupChange>();
        var approval = kept.Approval is null ? null : RegValue.Bin(kept.Approval);
        if (kind is "folder:cu" or "folder:lm")
        {
            var machine = kind == "folder:lm";
            // An older Exo moved the shortcut into its keeping: it goes back to where it was.
            if (kept.Kept is { } from)
            {
                if (!host.FileExists(from)) return ([], "The file Exo kept is gone, so it can't go back.");
                var folder = Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);
                changes.Add(new MoveFile(from, Path.Combine(folder, rest)));
            }
            changes.Add(new SetReg(machine ? Hive.Machine : Hive.User, StartupKeys.Approved + "StartupFolder", rest, approval));
            return (changes, null);
        }
        if (kind == "task")
        {
            if (kept.Kept is { } xml)
            {
                if (!host.FileExists(xml) || !CmdSafe(rest) || !CmdSafe(xml)) return ([], $"{kept.Name} can't be put back.");
                return ([new CreateTask(rest, xml)], null);
            }
            // One made again under another name is gone: there is nothing to turn on for it.
            if (Tasks(host, GuardWait).All(task => !string.Equals(task.Path, rest, StringComparison.OrdinalIgnoreCase))) return ([], null);
            return (kept.WasOn ? [new SetTask(rest, On: true)] : [], null);
        }
        if (kind == "package")
        {
            var (family, task) = FamilyAndTask(rest);
            var key = $@"{StartupKeys.Packaged}\{family}\{task}";
            // Back as it was; no State at all when it had none, so its manifest decides again.
            return ([new SetReg(Hive.User, key, "State", kept.State is { } state ? RegValue.DWord(state) : null)], null);
        }
        // A service whose start type wasn't recorded (an older Exo's removal): Exo won't guess it back to Automatic, which
        // could start a service the player had set to Manual on purpose.
        if (kind == "service")
            return kept.ServiceStart is { } start
                ? ([new SetReg(Hive.Machine, StartupKeys.Services + "\\" + rest, "Start", RegValue.DWord(start))], null)
                : ([], "Exo can't put this one back, so turn it on in Windows' Services.");
        if (PlaceOf(kind) is not { } place) return ([], "Exo can't put this one back.");
        var current = host.Get(place.Hive, place.Key, rest);
        // The value is written back only when it is gone (a RunOnce value, an older Exo's removal, or an app that deleted it).
        if (current is null && kept.Value is { } value)
            changes.Add(new SetReg(place.Hive, place.Key, rest, kept.Expand ? RegValue.Expand(value) : RegValue.Str(value)));
        if (place.ApprovedKey is { } approved) changes.Add(new SetReg(place.Hive, StartupKeys.Approved + approved, rest, approval));
        return (changes, null);
    }

    /// <summary>
    /// The first change that isn't as it was meant to be when read back; null when all are. <paramref name="tasks"/> is the
    /// task list as it is after the changes, when the caller has it (a check reads it once for all its changes); otherwise the
    /// list is read here, waited on for <paramref name="wait"/> (fifteen seconds: the player's own press).
    /// </summary>
    internal static StartupChange? Unverified(IStartupHost host, IEnumerable<StartupChange> changes, IReadOnlyList<TaskInfo>? tasks = null, TimeSpan? wait = null)
    {
        foreach (var change in changes)
        {
            var ok = change switch
            {
                SetReg set => RegValue.Same(host.Get(set.Hive, set.Key, set.Name), set.Value),
                // A task that isn't there wasn't changed: the change didn't happen.
                SetTask task => (tasks ??= host.Tasks(wait)).FirstOrDefault(t => string.Equals(t.Path, task.Path, StringComparison.OrdinalIgnoreCase)) is { } found && found.Enabled == task.On,
                DeleteTask task => (tasks ??= host.Tasks(wait)).All(t => !string.Equals(t.Path, task.Path, StringComparison.OrdinalIgnoreCase)),
                CreateTask task => (tasks ??= host.Tasks(wait)).Any(t => string.Equals(t.Path, task.Path, StringComparison.OrdinalIgnoreCase)),
                MoveFile move => !host.FileExists(move.From) && host.FileExists(move.To),
                DeleteFile del => !host.FileExists(del.Path),
                _ => true,
            };
            if (!ok) return change;
        }
        return null;
    }

    // ---- Removing ----

    /// <summary>Takes <paramref name="id"/> out of startup; null when it's out, else the line to show.</summary>
    internal static string? Remove(string id) => Remove(Host, id);

    internal static string? Remove(IStartupHost host, string id)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        host.EnsureSecurityReady(TimeSpan.FromSeconds(5));
        lock (Gate)
        {
            if (ReadAll(host).FirstOrDefault(entry => entry.Id == id) is not { } entry)
            {
                // The player pressed Remove on an item Exo already has (a machine-wide one that came back waits for this
                // press): kept but off, or on again, this press is the OK, so it is turned off again — with elevation, as
                // any of the player's presses may ask — and the guard is told.
                if (Kept(host).FirstOrDefault(item => item.Id == id) is not { } again) return "It's no longer in startup.";
                if (OffChanges(host, id) is not { } againChanges) return "Exo can't remove this one.";
                var againApplied = host.Apply(againChanges);
                // Read back afresh (a guard check's list, if one is under way, was read before this press).
                if (Unverified(host, againChanges) is not null)
                {
                    AppLog.Warn($"Startup: {again.Name} not removed: {againApplied.Error ?? "it was not as expected afterwards"}");
                    return againApplied.Error is { Length: > 0 } why && againApplied.Declined ? why : $"{again.Name} wasn't removed.";
                }
                StartupGuard.OnRemoved(host, again);
                AppLog.Info($"Startup: removed {again.Name} again ({id}).");
                return null;
            }
            if (entry.Locked) return StartupLocks.Sentence(entry.Lock);
            if (entry.ReadOnly) return "Exo only shows this one.";
            if (!SecurityChecked(host, entry)) return SecuritySentence;
            var (changes, removal, error) = BuildOff(host, entry);
            if (removal is null) return error ?? $"{entry.Name} wasn't removed.";
            var applied = host.Apply(changes);
            // Read back afresh: the press turns off a task here, and a guard check under way read the list before it.
            if (Unverified(host, changes) is not null)
            {
                if (removal.Kept is { } file) host.DeleteFile(file);
                AppLog.Warn($"Startup: {entry.Name} not removed: {applied.Error ?? "it was not as expected afterwards"}");
                return applied.Error is { Length: > 0 } why && applied.Declined ? why : $"{entry.Name} wasn't removed.";
            }
            if (entry.Missing)
            {
                // Its app is uninstalled: deleted for good, nothing kept to put back.
                Forget(host, removal);
                AppLog.Info($"Startup: deleted {entry.Name} for good, its program is gone ({id}).");
                return null;
            }
            Keep(host, [removal, .. Kept(host).Where(item => item.Id != id)]);
            StartupGuard.OnRemoved(host, removal);
            AppLog.Info($"Startup: removed {entry.Name} ({id}).");
            return null;
        }
    }

    /// <summary>The changes and the record for taking <paramref name="entry"/> out; or why it can't be.</summary>
    private static (IReadOnlyList<StartupChange> Changes, Removal? Removal, string? Error) BuildOff(IStartupHost host, StartupEntry entry)
    {
        var (kind, rest) = Split(entry.Id);
        var now = host.UtcNow;
        var removal = new Removal(entry.Id, entry.Name, now, WasOn: entry.Enabled) { Fingerprint = entry.Fingerprint, Program = entry.Program };
        if (kind == "task" && entry.Missing)
        {
            // A task whose program is gone is deleted for good (no Put back).
            if (!CmdSafe(rest) || rest.Length == 0) return ([], null, "Exo can't remove this task.");
            return ([new DeleteTask(rest)], removal, null);
        }
        if (entry.Missing)
        {
            if (kind is "folder:cu" or "folder:lm")
            {
                var machine = kind == "folder:lm";
                var filePath = Path.Combine(host.FolderPath(machine), rest);
                if (!CmdSafe(filePath)) return ([], null, "Exo can't remove this one.");
                return ([new DeleteFile(filePath), new SetReg(machine ? Hive.Machine : Hive.User, StartupKeys.Approved + "StartupFolder", rest, null)], removal, null);
            }
            if (PlaceOf(kind) is { ReadOnly: false } missingPlace)
            {
                var regChanges = new List<StartupChange> { new SetReg(missingPlace.Hive, missingPlace.Key, rest, null) };
                if (missingPlace.ApprovedKey is { } a) regChanges.Add(new SetReg(missingPlace.Hive, StartupKeys.Approved + a, rest, null));
                return (regChanges, removal, null);
            }
        }
        if (OffChanges(host, entry.Id) is not { } changes) return ([], null, "Exo can't remove this one.");
        if (kind == "task") return (changes, entry.Enabled ? removal : removal with { WasOn = false }, null);
        if (kind == "package")
        {
            var (family, task) = FamilyAndTask(rest);
            var state = host.Get(Hive.User, $@"{StartupKeys.Packaged}\{family}\{task}", "State");
            return (changes, removal with { State = state?.Number }, null);
        }
        if (kind == "service") return (changes, removal with { ServiceStart = host.Get(Hive.Machine, StartupKeys.Services + "\\" + rest, "Start")?.Number }, null);
        if (kind is "folder:cu" or "folder:lm")
        {
            var before = host.Get(kind == "folder:lm" ? Hive.Machine : Hive.User, StartupKeys.Approved + "StartupFolder", rest);
            return (changes, removal with { Approval = before?.Bytes }, null);
        }
        if (PlaceOf(kind) is not { } place) return ([], null, "Exo can't remove this one.");
        var value = host.Get(place.Hive, place.Key, rest);
        var recorded = removal with { Value = value?.Text, Expand = value?.Kind == RegistryValueKind.ExpandString, ValueKept = place.ApprovedKey is not null };
        return place.ApprovedKey is { } approved
            ? (changes, recorded with { Approval = host.Get(place.Hive, StartupKeys.Approved + approved, rest)?.Bytes }, null)
            : (changes, recorded, null);
    }

    /// <summary>The one sentence when Exo could not check this PC's security software and so can't vouch for an item.</summary>
    internal const string SecuritySentence = "Exo couldn't check this PC's security software, so it left this on.";

    /// <summary>
    /// Whether Exo may turn <paramref name="entry"/> off while it could not list this PC's security software: only what it
    /// can vouch for (the catalog says it is safe to turn off). Anything else might be security software's helper.
    /// </summary>
    private static bool SecurityChecked(IStartupHost host, StartupEntry entry) =>
        host.SecurityKnown || entry.Known?.Verdict is "remove" or "locked";

    /// <summary>Whether Clean up takes <paramref name="entry"/>: safe to remove (the catalog says so, or its program is gone), on, and not locked or read only.</summary>
    internal static bool Unneeded(StartupEntry entry) => !entry.Locked && !entry.ReadOnly && (entry.Missing || (entry.Enabled && entry.Known?.Verdict == "remove"));

    /// <summary>
    /// Clean up: every <see cref="Unneeded"/> entry out of startup at once, each kept for Put back as Remove keeps it, all
    /// handed over in one request (Windows asks once); how many went, and the line to show when some didn't.
    /// </summary>
    internal static (int Removed, string? Error) Clean() => Clean(Host);

    internal static (int Removed, string? Error) Clean(IStartupHost host)
    {
        if (HostMode.IsDisposable) return (0, HostMode.Refusal);
        host.EnsureSecurityReady(TimeSpan.FromSeconds(5));
        lock (Gate)
        {
            var all = ReadAll(host);
            var unneeded = all.Where(Unneeded).ToList();
            var picks = unneeded.Where(entry => SecurityChecked(host, entry)).ToList();
            // What it refused for want of the security list is said, as Remove says it: the button must not report success
            // with nothing removed.
            var refused = unneeded.Count - picks.Count;
            var extras = LastExtras.Where(CmdSafe).ToList();
            var built = new List<(StartupEntry? Entry, IReadOnlyList<StartupChange> Changes, Removal? Removal)>();
            string? error = null;
            foreach (var entry in picks)
            {
                var (changes, removal, why) = BuildOff(host, entry);
                if (removal is null) { error ??= why; continue; }
                built.Add((entry, changes, removal));
            }
            foreach (var path in extras) built.Add((null, [new SetTask(path, On: false)], null));
            var all0 = built.SelectMany(item => item.Changes).ToList();
            var applied = all0.Count == 0 ? ApplyResult.Done : host.Apply(all0);
            error ??= applied.Declined ? applied.Error : null;
            var done = 0;
            var failed = new List<string>();
            var kept = Kept(host).ToList();
            foreach (var (entry, changes, removal) in built)
            {
                if (Unverified(host, changes) is not null)
                {
                    if (removal?.Kept is { } file) host.DeleteFile(file);
                    failed.Add(entry?.Name ?? changes.OfType<SetTask>().FirstOrDefault()?.Path.Split('\\').Last() ?? "A task");
                    continue;
                }
                done++;
                // An extra (Windows' telemetry) isn't kept: it never had a row. Nor is what an uninstalled app left: it's deleted for good.
                if (entry is null || removal is null) continue;
                if (entry.Missing) { if (removal.Kept is { } file) host.DeleteFile(file); kept.RemoveAll(item => item.Id == entry.Id); continue; }
                kept.RemoveAll(item => item.Id == entry.Id);
                kept.Insert(0, removal);
            }
            // The list is saved first: the guard arms its watch from the saved list.
            Keep(host, kept);
            foreach (var (entry, _, removal) in built)
                if (entry is not null && removal is not null && !entry.Missing && kept.Any(item => item.Id == removal.Id))
                    StartupGuard.OnRemoved(host, removal);
            if (failed.Count > 0) AppLog.Warn($"Startup: Clean up couldn't turn off {string.Join(", ", failed)}.");
            error ??= failed.Count > 0 ? $"{string.Join(", ", failed.Distinct())} couldn't be turned off." : null;
            if (refused > 0) AppLog.Warn($"Startup: Clean up left {refused} it couldn't vouch for: this PC's security software could not be listed.");
            var total = built.Count;
            AppLog.Info($"Startup: Clean up removed {done} of {total} ({extras.Count} of Windows' telemetry tasks).");
            return (done, done < total ? error ?? "Some weren't removed. Try again." : refused > 0 ? SecuritySentence : null);
        }
    }

    /// <summary>
    /// The item was found again under another id (a task made again under a new GUID, a Run value with a new hash in its
    /// name): beside its own id, the removal keeps this one and what it takes to put it back, so Put back restores it too.
    /// </summary>
    internal static void NoteRenamed(IStartupHost host, string id, Also also)
    {
        var list = Kept(host).ToList();
        var at = list.FindIndex(item => item.Id == id);
        if (at < 0) return;
        var kept = list[at];
        if (kept.Renamed?.Any(one => one.Id == also.Id) == true) return;
        var names = new List<Also>(kept.Renamed ?? []);
        names.Add(also);
        // At most eight names are kept: the oldest goes, and nothing is turned on (it is not Exo's to turn on any more).
        while (names.Count > RenamedCap)
        {
            AppLog.Info($"Startup: {kept.Name} is no longer watched under its oldest name ({names[0].Id}).");
            names.RemoveAt(0);
        }
        list[at] = kept with { Renamed = names };
        Keep(host, list);
    }

    /// <summary>How many names of one item Exo keeps to put back.</summary>
    internal const int RenamedCap = 8;

    /// <summary>One id an item has had, as it is now and enough to put it back: taken before anything is applied.</summary>
    internal static Also? Filled(IStartupHost host, StartupEntry entry)
    {
        var (kind, rest) = Split(entry.Id);
        if (kind == "task") return new Also(entry.Id, entry.Name, WasOn: entry.Enabled);
        if (kind == "package")
        {
            var (family, task) = FamilyAndTask(rest);
            return new Also(entry.Id, entry.Name, State: host.Get(Hive.User, $@"{StartupKeys.Packaged}\{family}\{task}", "State")?.Number);
        }
        if (kind == "service") return new Also(entry.Id, entry.Name, ServiceStart: host.Get(Hive.Machine, StartupKeys.Services + "\\" + rest, "Start")?.Number);
        if (kind is "folder:cu" or "folder:lm")
            return new Also(entry.Id, entry.Name, Approval: host.Get(kind == "folder:lm" ? Hive.Machine : Hive.User, StartupKeys.Approved + "StartupFolder", rest)?.Bytes);
        if (PlaceOf(kind) is not { } place) return null;
        var value = host.Get(place.Hive, place.Key, rest);
        var approval = place.ApprovedKey is { } approved ? host.Get(place.Hive, StartupKeys.Approved + approved, rest)?.Bytes : null;
        return new Also(entry.Id, entry.Name, value?.Text, value?.Kind == RegistryValueKind.ExpandString, Approval: approval);
    }

    /// <summary>Drops what Exo kept of <paramref name="removal"/> (its record and any file it kept): there is no Put back for it.</summary>
    private static void Forget(IStartupHost host, Removal removal)
    {
        if (removal.Kept is { } file) host.DeleteFile(file);
        Keep(host, [.. Kept(host).Where(item => item.Id != removal.Id)]);
        StartupGuard.OnForgotten(host, removal.Id);
    }

    // ---- Putting back ----

    /// <summary>Puts <paramref name="id"/> back as it was; null when it's back, else the line to show.</summary>
    internal static string? Restore(string id) => Restore(Host, id);

    internal static string? Restore(IStartupHost host, string id)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        lock (Gate) return RestoreLocked(host, id, elevate: true);
    }

    internal static string? RestoreLocked(IStartupHost host, string id, bool elevate)
    {
        if (Kept(host).FirstOrDefault(item => item.Id == id) is not { } kept)
        {
            if (ReadAll(host).FirstOrDefault(entry => entry.Id == id) is not { } entry) return "Exo has nothing to put back for it.";
            if (entry.Enabled) return null;
            if (entry.Locked && entry.Lock != LockKind.Updater) return "Exo can't change this startup entry.";
            if (Split(id).Kind == "service") return "Exo can't put this one back, so turn it on in Windows' Services.";
            if (entry.ReadOnly) return "Exo can't change this startup entry.";
            if (OnChanges(host, id) is not { } changes) return "Exo can't turn this one back on.";
            var onApplied = host.Apply(changes, elevate);
            if (Unverified(host, changes) is not null)
            {
                AppLog.Warn($"Startup: {entry.Name} not turned on: {onApplied.Error ?? "it was not as expected afterwards"}");
                return onApplied is { Declined: true, Error: { } declinedWhy } ? declinedWhy : $"{entry.Name} wasn't turned on. Try again.";
            }
            AppLog.Info($"Startup: turned on {entry.Name} ({id}).");
            return null;
        }
        var (done, error, applied, _) = PutBackMany(host, [kept], elevate);
        if (done.Count > 0)
        {
            AppLog.Info($"Startup: put back {kept.Name} ({id}).");
            return null;
        }
        AppLog.Warn($"Startup: {kept.Name} not put back: {error ?? applied?.Error ?? "it was not as expected afterwards"}");
        return error ?? (applied is { Declined: true, Error: { } why } ? why : $"{kept.Name} wasn't put back. Try again.");
    }

    /// <summary>
    /// Puts each of <paramref name="items"/> back as it was, all handed over in one request, each read back; the ones that
    /// are back (their records gone and the guard told), the first line to show for one that can't be, what handing over
    /// came to, and the ones Windows left undone for want of its permission (the player's own press may ask for it).
    /// </summary>
    internal static (IReadOnlyList<Removal> Done, string? Error, ApplyResult? Applied, IReadOnlyList<Removal> Left) PutBackMany(
        IStartupHost host, IReadOnlyList<Removal> items, bool elevate, IReadOnlyList<TaskInfo>? before = null)
    {
        var built = new List<(Removal Kept, IReadOnlyList<StartupChange> Changes)>();
        string? error = null;
        foreach (var kept in items)
        {
            var (changes, why) = RestoreChanges(host, kept);
            if (why is not null) { error ??= why; continue; }
            built.Add((kept, changes));
        }
        if (built.Count == 0) return ([], error, null, []);
        var applied = host.Apply([.. built.SelectMany(item => item.Changes)], elevate);
        var left = LeftChanges(applied).ToHashSet();
        var all = Kept(host).ToList();
        var done = new List<Removal>();
        var leftUndone = new List<Removal>();
        foreach (var (kept, changes) in built)
        {
            // Left because it needs Windows' permission: not a change that failed, and nothing is retried by itself.
            if (changes.Any(left.Contains)) { leftUndone.Add(kept); continue; }
            // Held against the list as it was before this hand-over with these changes made to it, when the caller has it
            // (a guard check's own list, and the put-back is the first thing the check hands over); read otherwise.
            if (Unverified(host, changes, After(before, changes)) is not null) continue;
            if (kept.Kept is { } file && kept.Id.StartsWith("task:", StringComparison.Ordinal)) host.DeleteFile(file);
            all.RemoveAll(item => item.Id == kept.Id);
            done.Add(kept);
        }
        Keep(host, all);
        foreach (var kept in done) StartupGuard.OnPutBack(host, kept);
        return (done, error, applied, leftUndone);
    }

    /// <summary>
    /// A task list one operation already read, with these changes made to it: what it holds once they are made. Null when a
    /// change makes a task whose definition Exo doesn't have here (read the list then), or when there was no list.
    /// </summary>
    private static IReadOnlyList<TaskInfo>? After(IReadOnlyList<TaskInfo>? tasks, IReadOnlyList<StartupChange> changes)
    {
        if (tasks is null) return null;
        var list = tasks.ToList();
        foreach (var change in changes)
        {
            switch (change)
            {
                case SetTask set:
                    if (list.FindIndex(task => string.Equals(task.Path, set.Path, StringComparison.OrdinalIgnoreCase)) is var at and >= 0)
                        list[at] = list[at] with { Enabled = set.On };
                    break;
                case DeleteTask delete:
                    list.RemoveAll(task => string.Equals(task.Path, delete.Path, StringComparison.OrdinalIgnoreCase));
                    break;
                case CreateTask:
                    return null;
                default:
                    break;
            }
        }
        return list;
    }

    /// <summary>
    /// The changes a hand-over left undone for want of Windows' permission, one by one: the machine's own values were handed
    /// over together, and each is the change that was left.
    /// </summary>
    internal static IEnumerable<StartupChange> LeftChanges(ApplyResult applied) =>
        applied.Left.SelectMany(change => change is MachineValues values ? values.Values : [change]);

    /// <summary>The player turned these on themselves: their records go, and nothing of theirs is put back.</summary>
    internal static void Drop(IStartupHost host, IEnumerable<Removal> removals)
    {
        var ids = removals.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count > 0) Keep(host, [.. Kept(host).Where(item => !ids.Contains(item.Id))]);
    }

    /// <summary>
    /// Every removal that needs no administrator rights goes back as it was (Exo's uninstall); a machine-wide one stays, as the
    /// uninstaller touches nothing machine-wide without Windows' permission. How many went back.
    /// </summary>
    internal static int RestoreAllForUser(IStartupHost host)
    {
        if (HostMode.IsDisposable) return 0;
        var back = 0;
        lock (Gate)
        {
            foreach (var kept in Kept(host))
            {
                if (MachineWide(kept.Id)) continue;
                if (RestoreLocked(host, kept.Id, elevate: false) is null) back++;
            }
        }
        return back;
    }

    /// <summary>A name or string in a .reg file: backslashes and quotes escaped.</summary>
    internal static string RegText(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>Binary data in a .reg file: "hex:02,00,..".</summary>
    internal static string RegHex(byte[] bytes) => "hex:" + string.Join(',', bytes.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
}

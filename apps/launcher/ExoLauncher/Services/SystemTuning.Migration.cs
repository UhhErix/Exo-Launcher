using System.Globalization;

namespace ExoLauncher.Services;

internal sealed partial class SystemTuning
{
    internal const int CurrentMigrationVersion = 1;

    internal sealed record MigrationItem(string Key, string Label, string Reason, bool WholeKey, IReadOnlyList<string> Entries);

    internal sealed record MigrationState(bool Pending, IReadOnlyList<string> Keys, string Summary, IReadOnlyList<string> Reverted, bool Restart);

    internal static IReadOnlyList<MigrationItem> PendingMigrations(Saved saved, long totalMemKb)
    {
        if (saved.MigrationVersion >= CurrentMigrationVersion) return [];
        var list = new List<MigrationItem>();

        if (saved.Kept.TryGetValue(BootLatencyKey, out var bootWas) && bootWas.Strings is { Length: > 0 })
            list.Add(new(BootLatencyKey, "BCD boot flags (disabledynamictick, tscsyncpolicy, x2apicpolicy)", "myths on current Windows with zero gaming gain", true, bootWas.Strings));

        if (saved.Kept.TryGetValue(SvcHostSplitKey, out var svcWas) && svcWas.Strings is { Length: > 0 })
            list.Add(new(SvcHostSplitKey, "Service host consolidation", "no gaming gain and weakens Windows service isolation", true, svcWas.Strings));

        if (saved.Kept.TryGetValue(OsOverheadKey, out var osWas) && osWas.Strings is { Length: > 0 })
        {
            var ptEntries = osWas.Strings.Where(IsRetiredOsOverheadEntry).ToArray();
            if (ptEntries.Length > 0)
                list.Add(new(OsOverheadKey, "PowerThrottlingOff", "breaks Autopilot EcoQoS background throttling", false, ptEntries));
        }

        if (saved.Kept.TryGetValue(KernelLatencyKey, out var klWas) && klWas.Strings is { Length: > 0 })
        {
            var retired = klWas.Strings.Where(IsRetiredKernelLatencyEntry).ToArray();
            if (retired.Length > 0)
                list.Add(new(KernelLatencyKey, "DisablePagingExecutive, MMCSS and legacy timer keys", "myths on Windows 10/11 with no measured FPS gain", false, retired));
        }

        if (saved.Kept.TryGetValue(MpoOffKey, out var mpoWas) && mpoWas.Strings is { Length: > 0 })
            list.Add(new(MpoOffKey, "Multiplane Overlay (MPO) off", "adds up to 1 frame of latency with overlays; now opt-in for flicker only", true, mpoWas.Strings));

        if (saved.Kept.TryGetValue(GpuPstateKey, out var gpuWas) && gpuWas.Strings is { Length: > 0 })
            list.Add(new(GpuPstateKey, "Global GPU P-state lock", "raises desktop idle power by 25-60 W; now opt-in only", true, gpuWas.Strings));

        if (saved.Kept.TryGetValue(CpuMitigationsKey, out var cpuWas) && cpuWas.Strings is { Length: > 0 })
            list.Add(new(CpuMitigationsKey, "CPU Spectre/Meltdown mitigations off", "lowers OS security; now opt-in only", true, cpuWas.Strings));

        if (saved.Kept.TryGetValue(MemCompressionKey, out var mmWas) && mmWas.Strings is { Length: > 0 } && (totalMemKb == 0 || totalMemKb < MinMemCompressionOffKb))
            list.Add(new(MemCompressionKey, "Windows Memory Compression off", "hurts 1% lows on systems under 32 GB RAM", true, mmWas.Strings));

        return list;
    }

    internal static bool IsRetiredOsOverheadEntry(string entry) =>
        ParseOsOverheadRecord(entry) is { } rec
        && string.Equals(rec.SubKey, PowerThrottlingKey, StringComparison.OrdinalIgnoreCase)
        && string.Equals(rec.Name, PowerThrottlingOffValue, StringComparison.OrdinalIgnoreCase);

    internal static bool IsRetiredKernelLatencyEntry(string entry) =>
        ParseTaggedEntry(entry) is { } p
        && RetiredKernelLatencySpecs.Any(s => string.Equals(s.Key, p.SubKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Name, p.Name, StringComparison.OrdinalIgnoreCase));

    internal MigrationState? ReadMigrationState(Saved saved, Func<string, bool> pendingRestart)
    {
        var pending = PendingMigrations(saved, system.TotalMemoryKb());
        if (pending.Count > 0)
        {
            var labels = pending.Select(m => m.Label).ToList();
            var summary = $"An earlier Exo playbook set {Join(labels)}, which current Windows evidence marks as myths or harmful. Revert only what Exo changed back to Windows defaults.";
            return new(true, [.. pending.Select(m => m.Key)], summary, saved.RevertedByMigration ?? [], pending.Any(m => m.Key != MemCompressionKey));
        }

        if (saved.RevertedByMigration is { Count: > 0 } reverted)
        {
            var restart = reverted.Any(k => saved.Changed.TryGetValue(k, out var at) && at > system.Boot())
                || pendingRestart(BootLatencyKey)
                || pendingRestart(SvcHostSplitKey)
                || pendingRestart(OsOverheadKey)
                || pendingRestart(KernelLatencyKey)
                || pendingRestart(MpoOffKey)
                || pendingRestart(GpuPstateKey)
                || pendingRestart(CpuMitigationsKey);
            var summary = $"Exo reverted earlier playbook tweaks that evidence marks as myths or harmful ({Join(reverted)}).{(restart ? " Restart Windows to finish." : "")}";
            return new(false, [], summary, reverted, restart);
        }

        return null;
    }

    internal async Task<string?> MigrateAsync()
    {
        if (!_gate.Wait(0)) return Busy;
        try { return Logged(await MigrateNowAsync().ConfigureAwait(false)); }
        finally { _gate.Release(); }
    }

    private async Task<string?> MigrateNowAsync()
    {
        var saved = Settled();
        var pending = PendingMigrations(saved, system.TotalMemoryKb());
        if (pending.Count == 0)
        {
            if (saved.MigrationVersion < CurrentMigrationVersion)
                Save(saved with { MigrationVersion = CurrentMigrationVersion });
            return null;
        }

        var lines = new List<string>();
        string? bcdCheck = null;
        string? mmCheck = null;
        var needMmEnable = false;

        foreach (var item in pending)
        {
            if (item.Key == BootLatencyKey)
            {
                var bootLines = RevertBootLatency(new Was(Strings: [.. item.Entries]));
                if (bootLines is null) return "Exo's record of the earlier boot settings couldn't be read, so nothing changed.";
                lines.AddRange(bootLines);
                bcdCheck = system.Answer();
                lines.Add(TimerRead(bcdCheck));
            }
            else if (item.Key == MemCompressionKey)
            {
                if (item.Entries is [var mmTok] && mmTok == "was=true")
                {
                    mmCheck = system.Answer();
                    needMmEnable = true;
                    lines.Add(MemCompressionEnableLine(mmCheck));
                }
            }
            else if (item.Key == OsOverheadKey)
            {
                var osLines = RevertOsOverhead(new Was(Strings: [.. item.Entries]));
                if (osLines is null) return "Exo's record of PowerThrottlingOff couldn't be read, so nothing changed.";
                lines.AddRange(osLines);
            }
            else
            {
                var deepLines = RevertDeepKey(item.Key, new Was(Strings: [.. item.Entries]));
                if (deepLines is null) return $"Exo's record of {item.Label} couldn't be read, so nothing changed.";
                lines.AddRange(deepLines);
            }
        }

        if (lines.Count > 0)
        {
            var point = system.PointStanding ? null : system.Answer();
            var steps = point is null ? lines : [RestorePoint.Step(point, "Exo Launcher: revert retired playbook tweaks", "MODIFY_SETTINGS"), .. lines];
            if (!await system.Admin(steps).ConfigureAwait(false))
            {
                if (bcdCheck is not null) system.ReadAnswer(bcdCheck);
                if (mmCheck is not null) system.ReadAnswer(mmCheck);
                if (point is not null) system.ReadAnswer(point);
                return "Windows' permission was declined, so nothing changed.";
            }
            if (point is not null)
                system.PointMade(RestorePoint.Read(system.ReadAnswer(point)));
        }

        var kept = new Dictionary<string, Was>(saved.Kept);
        var changed = new Dictionary<string, DateTimeOffset>(saved.Changed);
        var revertedLabels = new List<string>(saved.RevertedByMigration ?? []);
        var failed = new List<string>();

        foreach (var item in pending)
        {
            bool ok;
            if (item.Key == BootLatencyKey)
            {
                ok = system.ReadAnswer(bcdCheck!)?.Contains(ReadOk, StringComparison.Ordinal) == true;
            }
            else if (item.Key == MemCompressionKey)
            {
                ok = !needMmEnable || string.Equals(system.ReadAnswer(mmCheck!)?.Trim(), "True", StringComparison.OrdinalIgnoreCase);
            }
            else if (item.Key == OsOverheadKey)
            {
                ok = VerifyOsOverheadReverted(new Was(Strings: [.. item.Entries]));
            }
            else
            {
                ok = VerifyDeepReverted(item.Key, new Was(Strings: [.. item.Entries]));
            }

            if (!ok)
            {
                failed.Add(item.Label);
                continue;
            }

            if (item.WholeKey)
            {
                kept.Remove(item.Key);
            }
            else if (kept.TryGetValue(item.Key, out var existing) && existing.Strings is { Length: > 0 } existingList)
            {
                var remaining = item.Key == OsOverheadKey
                    ? existingList.Where(e => !IsRetiredOsOverheadEntry(e)).ToArray()
                    : existingList.Where(e => !IsRetiredKernelLatencyEntry(e)).ToArray();
                if (remaining.Length == 0) kept.Remove(item.Key);
                else kept[item.Key] = existing with { Strings = remaining };
            }

            if (item.Key != MemCompressionKey)
                changed[item.Key] = now();
            if (!revertedLabels.Contains(item.Label, StringComparer.OrdinalIgnoreCase))
                revertedLabels.Add(item.Label);
        }

        var nextVersion = failed.Count == 0 ? CurrentMigrationVersion : saved.MigrationVersion;
        var nextSaved = saved with
        {
            Kept = kept,
            Changed = changed,
            MigrationVersion = nextVersion,
            RevertedByMigration = revertedLabels,
        };
        if (!Save(nextSaved)) return "Exo reverted the settings, but couldn't save the updated record.";
        if (failed.Count > 0) return $"Windows didn't revert {Join(failed)}. Try again.";
        AppLog.Info("System tuning migration reverted: " + string.Join(", ", revertedLabels));
        return null;
    }

    private Saved AutoAdvanceCleanMigration(Saved saved)
    {
        if (saved.MigrationVersion >= CurrentMigrationVersion) return saved;
        if (PendingMigrations(saved, system.TotalMemoryKb()).Count > 0) return saved;
        var updated = saved with { MigrationVersion = CurrentMigrationVersion };
        Save(updated);
        return updated;
    }
}

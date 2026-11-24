using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using static ExoLauncher.Services.InterruptMode;

namespace ExoLauncher.Services;

internal sealed partial class MouseDiagnostics
{
    internal const string FixAffinity = "affinity";
    internal const string FixMsi = "msi";
    internal const string FixUsbSuspend = "usb_suspend";
    internal const string HonestyNote = "Measure again after — there may be no change.";

    internal sealed record FixCard(
        string Id,
        string Title,
        string Summary,
        string ActionLabel,
        string HonestyNote,
        bool NeedsRestart,
        bool NeedsAdmin,
        string Status,
        bool CanApply,
        bool CanUndo,
        string? Detail,
        JitterSnapshot? Before,
        JitterSnapshot? After,
        bool? Improved);

    internal sealed record AffinityBackup(string ControllerId, int TargetCpu, int? WasPolicy, string? WasMask, DateTimeOffset WrittenAt, JitterSnapshot Before, JitterSnapshot? After = null, bool Reverted = false);

    internal sealed record MsiBackup(string ControllerId, int? WasMsi, DateTimeOffset WrittenAt, JitterSnapshot Before, JitterSnapshot? After = null, bool Reverted = false);

    internal sealed record HubPowerSaved(string HubInstanceId, int? WasEnhanced, int? WasAllowIdle, int? WasSelective);

    internal sealed record UsbSuspendBackup(List<HubPowerSaved> Hubs, uint? WasAcUsbSuspend, DateTimeOffset WrittenAt, JitterSnapshot Before, JitterSnapshot? After = null, bool Reverted = false);

    internal sealed record FixBackup(AffinityBackup? Affinity = null, MsiBackup? Msi = null, UsbSuspendBackup? UsbSuspend = null);

    internal sealed record FixContext(
        JitterReport? Jitter,
        Device? MouseController,
        MouseCore.Topology? Topology,
        MouseCore.Work? Work,
        IReadOnlyList<Device> PciDevices,
        IReadOnlySet<string> BootChain,
        IReadOnlyList<string> MouseHubIds,
        IReadOnlyDictionary<string, HubPowerSaved> HubPowerStates,
        uint? AcUsbSuspendValue);

    internal interface IFixApplier
    {
        string? WriteInterruptValues(IReadOnlyList<InterruptMode.RegValue> values);
        string? WriteHubPowerValues(IReadOnlyList<(string HubId, string ValueName, int? Value)> values);
        bool WriteAcUsbSuspend(uint value);
        uint? ReadAcUsbSuspend();
    }

    internal sealed partial class NativeFixApplier : IFixApplier
    {
        private static readonly Guid UsbSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
        private static readonly Guid UsbSuspendSetting = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

        [GeneratedRegex(@"^USB\\[A-Z0-9_&]+\\[A-Z0-9_&]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex UsbIdRegex();

        private static readonly HashSet<string> AllowedHubValues = new(StringComparer.Ordinal)
        {
            "EnhancedPowerManagementEnabled",
            "AllowIdleIrpInD3",
            "SelectiveSuspendEnabled",
        };

        public string? WriteInterruptValues(IReadOnlyList<InterruptMode.RegValue> values) => RunElevated(values);

        public string? WriteHubPowerValues(IReadOnlyList<(string HubId, string ValueName, int? Value)> values)
        {
            if (values.Count == 0) return null;
            var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
            var commands = new List<string>(values.Count);
            foreach (var (hubId, name, val) in values)
            {
                if (!UsbIdRegex().IsMatch(hubId) || !AllowedHubValues.Contains(name))
                    return "Exo didn't recognize a USB hub key, so nothing changed.";
                var key = $"\"HKLM\\SYSTEM\\CurrentControlSet\\Enum\\{hubId}\\Device Parameters\"";
                commands.Add(val is null
                    ? $"\"{reg}\" delete {key} /v {name} /f /reg:64"
                    : $"\"{reg}\" add {key} /v {name} /t REG_DWORD /d {(uint)val.Value} /f /reg:64");
            }
            return ElevatedCommands.Run(commands, "Mouse diagnostics");
        }

        public uint? ReadAcUsbSuspend()
        {
            var power = new PcHealth.NativePower();
            var scheme = power.Active();
            return scheme is { } s ? power.AcValue(s, UsbSubgroup, UsbSuspendSetting) : null;
        }

        public bool WriteAcUsbSuspend(uint value)
        {
            var power = new PcHealth.NativePower();
            var scheme = power.Active();
            return scheme is { } s && power.Write(s, UsbSubgroup, UsbSuspendSetting, value, dc: false) && power.SetActive(s);
        }
    }

    internal sealed class FixManager(IDevices devices, IFixApplier applier, string backupPath, Func<DateTimeOffset> bootTime, Func<DateTimeOffset> now)
    {
        private const int SpecifiedPolicy = 4;

        internal FixBackup? LoadBackup()
        {
            try
            {
                return File.Exists(backupPath) ? JsonSerializer.Deserialize<FixBackup>(File.ReadAllText(backupPath)) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return null;
            }
        }

        internal void RecordRetest(JitterReport jitter, string? controllerId)
        {
            if (jitter.CheckedGaps < 32) return;
            var backup = LoadBackup();
            if (backup is null) return;
            var snap = SnapshotOf(jitter);
            var changed = false;
            var boot = bootTime();

            var aff = backup.Affinity;
            if (aff is { Reverted: false } && aff.WrittenAt <= boot && (controllerId is null || string.Equals(controllerId, aff.ControllerId, StringComparison.OrdinalIgnoreCase)))
            {
                aff = aff with { After = snap };
                changed = true;
            }
            var msi = backup.Msi;
            if (msi is { Reverted: false } && msi.WrittenAt <= boot && (controllerId is null || string.Equals(controllerId, msi.ControllerId, StringComparison.OrdinalIgnoreCase)))
            {
                msi = msi with { After = snap };
                changed = true;
            }
            var susp = backup.UsbSuspend;
            if (susp is { Reverted: false })
            {
                susp = susp with { After = snap };
                changed = true;
            }

            if (changed) SaveBackup(new FixBackup(aff, msi, susp));
        }

        internal IReadOnlyList<FixCard> Evaluate(FixContext ctx)
        {
            var backup = CleanReverted(LoadBackup());
            var boot = bootTime();
            var hasProblem = ctx.Jitter is not null && HasProblem(ctx.Jitter);
            var cards = new List<FixCard>();

            if (EvaluateAffinity(ctx, backup?.Affinity, boot, hasProblem) is { } affCard) cards.Add(affCard);
            if (EvaluateMsi(ctx, backup?.Msi, boot, hasProblem) is { } msiCard) cards.Add(msiCard);
            if (EvaluateUsbSuspend(ctx, backup?.UsbSuspend, hasProblem) is { } suspCard) cards.Add(suspCard);

            return cards;
        }

        internal string? Apply(string fixId, FixContext ctx) => fixId switch
        {
            FixAffinity => ApplyAffinity(ctx),
            FixMsi => ApplyMsi(ctx),
            FixUsbSuspend => ApplyUsbSuspend(ctx),
            _ => "Unknown fix.",
        };

        internal string? Undo(string fixId) => fixId switch
        {
            FixAffinity => UndoAffinity(),
            FixMsi => UndoMsi(),
            FixUsbSuspend => UndoUsbSuspend(),
            _ => "Unknown fix.",
        };

        internal static int? SuggestQuietCore(FixContext ctx)
        {
            if (ctx.Jitter is null || !HasProblem(ctx.Jitter)) return null;
            if (ctx.MouseController is not { } ctrl) return null;
            if (ctx.Topology is not { } topology || topology.Logical > 64 || topology.Cores.Count == 0) return null;
            if (ctx.BootChain.Contains(ctrl.Id)) return null;
            if (ctrl.BaseClass != 0x0C || ctrl.SubClass != 0x03 || ctrl.ProgIf != 0x30 || !PciId().IsMatch(ctrl.Id)) return null;
            if (ctrl.Local == false) return null;

            var mouseCpu = ctx.Jitter.MouseCpu;
            if (mouseCpu is not { } onCpu) return null;
            var dpc = ctx.Work?.Dpc ?? [];
            var ints = ctx.Work?.Interrupts ?? [];
            var busiest = MouseCore.Busiest(dpc);
            var core0Siblings = topology.Cores.FirstOrDefault(c => c.Threads.Contains(0))?.Threads ?? [0];
            var onCore0 = core0Siblings.Contains(onCpu);
            var onBusiest = busiest is { } b && (topology.Cores.FirstOrDefault(c => c.Threads.Contains(b))?.Threads ?? [b]).Contains(onCpu);
            if (!onCore0 && !onBusiest && ctx.Jitter.Cause is not ("pc_busy" or "both")) return null;

            var pinned = ctx.PciDevices
                .Where(d => !string.Equals(d.Id, ctrl.Id, StringComparison.OrdinalIgnoreCase) && d.Policy == SpecifiedPolicy && d.Override is { Length: > 0 })
                .Aggregate(0UL, (all, d) => all | MouseCore.Bits(d.Override!));
            return MouseCore.Choose(topology, dpc, ints, busiest, pinned);
        }

        private FixCard? EvaluateAffinity(FixContext ctx, AffinityBackup? saved, DateTimeOffset boot, bool hasProblem)
        {
            if (saved is not null)
            {
                var pending = saved.WrittenAt > boot;
                if (saved.Reverted)
                {
                    return new FixCard(FixAffinity, "xHCI interrupt affinity", "Reverted to original Windows affinity policy.", "Move to quiet core",
                        HonestyNote, true, true, "restart", false, false, "Restart Windows to finish restoring interrupt affinity.", null, null, null);
                }
                var improved = saved.After is not null ? IsImproved(saved.Before, saved.After) : (bool?)null;
                var detail = pending
                    ? $"Pinned to CPU {saved.TargetCpu}. Restart Windows, then measure again."
                    : improved is null
                        ? $"Pinned to CPU {saved.TargetCpu}. Measure again to compare."
                        : improved.Value
                            ? $"Pinned to CPU {saved.TargetCpu} — jitter improved after restart."
                            : $"Pinned to CPU {saved.TargetCpu} — no measurable improvement; Undo puts it back.";
                return new FixCard(FixAffinity, "xHCI interrupt affinity", $"Mouse USB controller assigned to quiet CPU {saved.TargetCpu}.", "Move to quiet core",
                    HonestyNote, true, true, pending ? "restart" : "applied", false, true, detail, saved.Before, saved.After, improved);
            }

            if (!hasProblem) return null;
            if (ctx.MouseController is not { } ctrl) return null;
            if (ctrl.OddAffinity || ctrl.Policy is not (null or 0) || ctrl.Override is not null) return null;
            if (SuggestQuietCore(ctx) is not { } targetCpu) return null;

            return new FixCard(
                FixAffinity,
                "xHCI interrupt affinity",
                $"Move your mouse's USB controller off CPU {ctx.Jitter!.MouseCpu} to quiet non-zero CPU {targetCpu}.",
                $"Pin to CPU {targetCpu}",
                HonestyNote,
                true,
                true,
                "available",
                true,
                false,
                "Writes DevicePolicy and AssignmentSetOverride via admin helper. Restart required.",
                null,
                null,
                null);
        }

        private FixCard? EvaluateMsi(FixContext ctx, MsiBackup? saved, DateTimeOffset boot, bool hasProblem)
        {
            if (saved is not null)
            {
                var pending = saved.WrittenAt > boot;
                if (saved.Reverted)
                {
                    return new FixCard(FixMsi, "USB controller MSI mode", "Reverted to original interrupt mode.", "Enable MSI mode",
                        HonestyNote, true, true, "restart", false, false, "Restart Windows to finish restoring interrupt mode.", null, null, null);
                }
                var improved = saved.After is not null ? IsImproved(saved.Before, saved.After) : (bool?)null;
                var detail = pending
                    ? "MSI mode enabled. Restart Windows, then measure again."
                    : improved is null
                        ? "MSI mode enabled. Measure again to compare."
                        : improved.Value
                            ? "MSI mode enabled — jitter improved after restart."
                            : "MSI mode enabled — no measurable improvement; Undo puts it back.";
                return new FixCard(FixMsi, "USB controller MSI mode", "Message Signaled Interrupts (MSI) enabled on mouse USB controller.", "Enable MSI mode",
                    HonestyNote, true, true, pending ? "restart" : "applied", false, true, detail, saved.Before, saved.After, improved);
            }

            if (!hasProblem) return null;
            if (ctx.MouseController is not { } ctrl) return null;
            if (ctx.BootChain.Contains(ctrl.Id)) return null;
            if (!PciId().IsMatch(ctrl.Id) || ctrl.Problem != 0 || ctrl.DriverRank is >= 0x80000000) return null;
            var supportsMsi = ((ctrl.Support ?? 0) & 0x6) != 0;
            var msiOff = ctrl.Mode == Mode.Line || ctrl.MsiValue == 0;
            if (!supportsMsi || !msiOff) return null;

            return new FixCard(
                FixMsi,
                "USB controller MSI mode",
                $"{ctrl.Name} supports MSI but is currently using line-based interrupts.",
                "Enable MSI mode",
                HonestyNote,
                true,
                true,
                "available",
                true,
                false,
                "Sets MSISupported = 1 on the mouse's xHCI controller via admin helper. Restart required.",
                null,
                null,
                null);
        }

        private FixCard? EvaluateUsbSuspend(FixContext ctx, UsbSuspendBackup? saved, bool hasProblem)
        {
            if (saved is not null)
            {
                if (saved.Reverted) return null;
                var improved = saved.After is not null ? IsImproved(saved.Before, saved.After) : (bool?)null;
                var detail = improved is null
                    ? "Selective suspend disabled on your mouse's USB hub. Measure again to compare."
                    : improved.Value
                        ? "Selective suspend disabled — jitter improved on re-test."
                        : "Selective suspend disabled — no measurable improvement; Undo puts it back.";
                return new FixCard(FixUsbSuspend, "USB selective suspend off", "Power-saving suspend disabled for your mouse's USB hub.", "Disable selective suspend",
                    HonestyNote, false, true, "applied", false, true, detail, saved.Before, saved.After, improved);
            }

            if (!hasProblem) return null;
            if (ctx.MouseHubIds.Count == 0 && ctx.AcUsbSuspendValue != 1) return null;
            var hubHasSuspend = ctx.MouseHubIds.Any(id =>
            {
                if (!ctx.HubPowerStates.TryGetValue(id, out var st)) return true;
                return (st.WasEnhanced ?? 1) != 0 || (st.WasAllowIdle ?? 1) != 0 || (st.WasSelective ?? 1) != 0;
            });
            var acSuspendOn = ctx.AcUsbSuspendValue == 1;
            if (!hubHasSuspend && !acSuspendOn) return null;

            return new FixCard(
                FixUsbSuspend,
                "USB selective suspend off",
                "Disable USB selective suspend on your mouse's hub to prevent idle wake-up hitches while gaming.",
                "Disable on mouse hub",
                HonestyNote,
                false,
                true,
                "available",
                true,
                false,
                "Turns off hub selective suspend via admin helper and AC USB suspend while gaming.",
                null,
                null,
                null);
        }

        private string? ApplyAffinity(FixContext ctx)
        {
            if (ctx.Jitter is null || !HasProblem(ctx.Jitter)) return "Run a measurement showing a problem first.";
            if (ctx.MouseController is not { } ctrl) return "No eligible mouse USB controller was found.";
            if (SuggestQuietCore(ctx) is not { } targetCpu) return "No quiet non-zero CPU core is available for this controller.";
            var existing = LoadBackup() ?? new FixBackup();
            if (existing.Affinity is { Reverted: false }) return "Interrupt affinity fix is already applied.";

            var mask = MouseCore.Mask(targetCpu);
            var entry = new AffinityBackup(
                ctrl.Id,
                targetCpu,
                ctrl.Policy,
                ctrl.Override is { } o ? Convert.ToHexString(o) : null,
                now(),
                SnapshotOf(ctx.Jitter));
            if (!SaveBackup(existing with { Affinity = entry })) return "Could not save Undo backup, so nothing changed.";

            var err = applier.WriteInterruptValues([new InterruptMode.RegValue(ctrl.Id, MouseCore.PolicyName, SpecifiedPolicy), new InterruptMode.RegValue(ctrl.Id, MouseCore.OverrideName, mask)]);
            if (err is not null)
            {
                SaveBackup(existing);
                return err;
            }
            var after = devices.List().FirstOrDefault(d => string.Equals(d.Id, ctrl.Id, StringComparison.OrdinalIgnoreCase));
            if (after is not { Policy: SpecifiedPolicy } || after.Override is null || !after.Override.AsSpan().SequenceEqual(mask))
            {
                SaveBackup(existing);
                return "Windows didn't take the interrupt affinity change.";
            }
            return null;
        }

        private string? UndoAffinity()
        {
            var existing = LoadBackup();
            if (existing?.Affinity is not { Reverted: false } saved) return "There is no interrupt affinity change to undo.";
            byte[]? wasMask;
            try { wasMask = saved.WasMask is { } hex ? Convert.FromHexString(hex) : null; }
            catch (FormatException) { return "Backup data is damaged, so nothing changed."; }

            var err = applier.WriteInterruptValues([new InterruptMode.RegValue(saved.ControllerId, MouseCore.PolicyName, saved.WasPolicy), new InterruptMode.RegValue(saved.ControllerId, MouseCore.OverrideName, wasMask)]);
            if (err is not null) return err;
            SaveBackup(existing with { Affinity = saved with { Reverted = true, WrittenAt = now() } });
            return null;
        }

        private string? ApplyMsi(FixContext ctx)
        {
            if (ctx.Jitter is null || !HasProblem(ctx.Jitter)) return "Run a measurement showing a problem first.";
            if (ctx.MouseController is not { } ctrl) return "No eligible mouse USB controller was found.";
            if (ctx.BootChain.Contains(ctrl.Id)) return "Windows boots from this controller, so Exo leaves it alone.";
            var supportsMsi = ((ctrl.Support ?? 0) & 0x6) != 0;
            if (!supportsMsi || (ctrl.Mode != Mode.Line && ctrl.MsiValue != 0)) return "This controller already uses MSI or does not support MSI.";
            var existing = LoadBackup() ?? new FixBackup();
            if (existing.Msi is { Reverted: false }) return "MSI mode fix is already applied.";

            var entry = new MsiBackup(ctrl.Id, ctrl.MsiValue, now(), SnapshotOf(ctx.Jitter));
            if (!SaveBackup(existing with { Msi = entry })) return "Could not save Undo backup, so nothing changed.";

            var err = applier.WriteInterruptValues([new InterruptMode.RegValue(ctrl.Id, MsiName, 1)]);
            if (err is not null)
            {
                SaveBackup(existing);
                return err;
            }
            var after = devices.List().FirstOrDefault(d => string.Equals(d.Id, ctrl.Id, StringComparison.OrdinalIgnoreCase));
            if (after is null || after.MsiValue != 1)
            {
                SaveBackup(existing);
                return "Windows didn't take the MSI mode change.";
            }
            return null;
        }

        private string? UndoMsi()
        {
            var existing = LoadBackup();
            if (existing?.Msi is not { Reverted: false } saved) return "There is no MSI mode change to undo.";
            var err = applier.WriteInterruptValues([new InterruptMode.RegValue(saved.ControllerId, MsiName, saved.WasMsi)]);
            if (err is not null) return err;
            SaveBackup(existing with { Msi = saved with { Reverted = true, WrittenAt = now() } });
            return null;
        }

        private string? ApplyUsbSuspend(FixContext ctx)
        {
            if (ctx.Jitter is null || !HasProblem(ctx.Jitter)) return "Run a measurement showing a problem first.";
            var existing = LoadBackup() ?? new FixBackup();
            if (existing.UsbSuspend is { Reverted: false }) return "USB selective suspend fix is already applied.";

            var hubsSaved = new List<HubPowerSaved>();
            var writes = new List<(string HubId, string ValueName, int? Value)>();
            foreach (var hubId in ctx.MouseHubIds)
            {
                var st = ctx.HubPowerStates.TryGetValue(hubId, out var cur) ? cur : new HubPowerSaved(hubId, null, null, null);
                hubsSaved.Add(st);
                writes.Add((hubId, "EnhancedPowerManagementEnabled", 0));
                writes.Add((hubId, "AllowIdleIrpInD3", 0));
                writes.Add((hubId, "SelectiveSuspendEnabled", 0));
            }
            var wasAc = ctx.AcUsbSuspendValue ?? applier.ReadAcUsbSuspend();
            if (writes.Count == 0 && wasAc != 1) return "USB selective suspend is already off.";

            var entry = new UsbSuspendBackup(hubsSaved, wasAc, now(), SnapshotOf(ctx.Jitter));
            if (!SaveBackup(existing with { UsbSuspend = entry })) return "Could not save Undo backup, so nothing changed.";

            if (writes.Count > 0)
            {
                var err = applier.WriteHubPowerValues(writes);
                if (err is not null)
                {
                    SaveBackup(existing);
                    return err;
                }
            }
            if (wasAc == 1) applier.WriteAcUsbSuspend(0);
            return null;
        }

        private string? UndoUsbSuspend()
        {
            var existing = LoadBackup();
            if (existing?.UsbSuspend is not { Reverted: false } saved) return "There is no USB selective suspend change to undo.";
            var writes = new List<(string HubId, string ValueName, int? Value)>();
            foreach (var h in saved.Hubs)
            {
                writes.Add((h.HubInstanceId, "EnhancedPowerManagementEnabled", h.WasEnhanced));
                writes.Add((h.HubInstanceId, "AllowIdleIrpInD3", h.WasAllowIdle));
                writes.Add((h.HubInstanceId, "SelectiveSuspendEnabled", h.WasSelective));
            }
            if (writes.Count > 0)
            {
                var err = applier.WriteHubPowerValues(writes);
                if (err is not null) return err;
            }
            if (saved.WasAcUsbSuspend is { } acVal) applier.WriteAcUsbSuspend(acVal);
            SaveBackup(existing with { UsbSuspend = null });
            return null;
        }

        private FixBackup? CleanReverted(FixBackup? backup)
        {
            if (backup is null) return null;
            var boot = bootTime();
            var aff = backup.Affinity is { Reverted: true } a && a.WrittenAt <= boot ? null : backup.Affinity;
            var msi = backup.Msi is { Reverted: true } m && m.WrittenAt <= boot ? null : backup.Msi;
            var susp = backup.UsbSuspend is { Reverted: true } ? null : backup.UsbSuspend;
            if (aff == backup.Affinity && msi == backup.Msi && susp == backup.UsbSuspend) return backup;
            var next = new FixBackup(aff, msi, susp);
            if (aff is null && msi is null && susp is null)
            {
                ForgetBackup();
                return null;
            }
            SaveBackup(next);
            return next;
        }

        private bool SaveBackup(FixBackup backup)
        {
            try
            {
                var dir = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = backupPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(backup));
                File.Move(tmp, backupPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Mouse diag: could not save backup: " + ex.Message);
                return false;
            }
        }

        private void ForgetBackup()
        {
            try { if (File.Exists(backupPath)) DeletionGate.DeleteFile(backupPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static HubPowerSaved ReadHubPowerSaved(string hubInstanceId)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{hubInstanceId}\Device Parameters");
            return new HubPowerSaved(
                hubInstanceId,
                key?.GetValue("EnhancedPowerManagementEnabled") as int?,
                key?.GetValue("AllowIdleIrpInD3") as int?,
                key?.GetValue("SelectiveSuspendEnabled") as int?);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new HubPowerSaved(hubInstanceId, null, null, null);
        }
    }
}

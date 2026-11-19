using System.Text.Json;
using ExoLauncher.Helpers;
using static ExoLauncher.Services.InterruptMode;

namespace ExoLauncher.Services;

/// <summary>
/// After a test with late reports, what else takes its interrupts on the mouse's CPU, and where each could go: network, audio and
/// each other a core of their own, USB controllers with no mouse or keyboard on them one between them, always a core's first
/// thread, never core 0, the mouse's core, a core another device is pinned to or (with an Intel P- and E-core split) a slower core. With two
/// CCDs (a dual-CCD Ryzen) everything leaves the mouse's, and only then does the graphics card move as well. A network adapter with several queues
/// stays where it is: its driver spreads its work itself (RSS), which interrupts' affinity doesn't change. Exo's rules of thumb, so it asks: a press, one permission
/// prompt and a restart, the values before kept for Undo, never in Best settings. It changes only what shares the mouse's CPU; what doesn't is already spread.
/// </summary>
internal sealed partial class MouseCore
{
    /// <summary>One device's move. Id never reaches React.</summary>
    internal sealed record Move(string Id, string Name, string Kind, int Cpu);

    /// <summary>A device's values before the layout (null: absent; the mask as hex).</summary>
    internal sealed record Moved(string Id, int Cpu, int? WasPolicy, string? WasMask);

    /// <summary>The layout Exo wrote. <paramref name="Reverted"/>: put back, waiting on a restart.</summary>
    internal sealed record SavedLayout(DateTimeOffset WrittenAt, List<Moved> Devices, bool Reverted = false);

    /// <summary>What to show: moves on offer, or Exo's layout in place, how many devices, and whether it waits on a restart.</summary>
    internal sealed record LayoutState(IReadOnlyList<Move> Offer, int Applied, bool Restart, bool CanUndo);

    /// <summary>The suggestion for this PC, or nothing when it already follows the rules, or can't.</summary>
    /// <param name="list">The PCI devices.</param>
    /// <param name="t">The CPUs.</param>
    /// <param name="mouseCpu">The CPU that takes the mouse's input.</param>
    /// <param name="keep">USB controllers a mouse or keyboard hangs from, and anything Windows' drive needs.</param>
    /// <param name="pinned">CPUs another device's interrupts are set to.</param>
    internal static IReadOnlyList<Move> Suggest(IReadOnlyList<Device> list, Topology t, int mouseCpu, IReadOnlySet<string> keep, ulong pinned)
    {
        if (t.Logical > 64 || t.Cores.Count == 0) return [];
        var top = t.Cores.Max(c => c.Efficiency);
        var fast = t.Cores.Where(c => c.Efficiency == top && !c.Threads.Contains(0) && !c.Threads.Contains(mouseCpu) && !c.Threads.Any(x => Has(pinned, x))).ToList();
        var caches = t.L3.Where(l => t.Cores.Any(c => c.Efficiency == top && c.Threads.Any(x => Has(l.Mask, x)))).ToList();
        var mine = caches.FirstOrDefault(l => Has(l.Mask, mouseCpu));
        var away = fast.Where(c => mine is null || !c.Threads.All(x => Has(mine.Mask, x))).ToList();
        var twoCcds = caches.Count > 1 && away.Count > 0;
        // With two CCDs, everything leaves the mouse's and nothing goes back to it.
        var free = new Queue<int>((twoCcds ? away : fast).OrderBy(c => c.Threads.Min()).Select(c => c.Threads.Min()));

        bool Sharing(Device d) => d.Affinity is not { } aim || Has(aim, mouseCpu);
        bool Movable(Device d) => !keep.Contains(d.Id) && Sharing(d) && d.Mode == Mode.Msi && d.Local != false && !Dock(d.Name)
            && !string.IsNullOrEmpty(d.Service) && d.Problem == 0 && d.DriverRank is < 0x80000000 && !Taken(d) && PciId().IsMatch(d.Id);

        var moves = new List<Move>();
        void Give(IEnumerable<Device> devices)
        {
            foreach (var d in devices)
            {
                if (free.Count == 0) return;
                moves.Add(new Move(d.Id, d.Name, InterruptMode.Kind(d), free.Dequeue()));
            }
        }
        if (twoCcds) Give(list.Where(d => d.BaseClass == 0x03 && Movable(d)));
        Give(list.Where(d => d.BaseClass == 0x02 && d.MessageMax <= 1 && Movable(d)));
        Give(list.Where(d => (d.BaseClass, d.SubClass) == (0x04, 0x03) && Movable(d)));
        // USB controllers with nothing that needs them quick share one core.
        var usb = list.Where(d => (d.BaseClass, d.SubClass, d.ProgIf) == (0x0C, 0x03, 0x30) && Movable(d)).ToList();
        if (usb.Count > 0 && free.Count > 0)
        {
            var one = free.Dequeue();
            moves.AddRange(usb.Select(d => new Move(d.Id, d.Name, InterruptMode.Kind(d), one)));
        }
        return moves;
    }

    /// <summary>What the last test's mouse could have done about what shares its CPU, and where Exo's own layout stands.</summary>
    internal LayoutState ReadLayout()
    {
        var saved = LoadLayout();
        var pending = saved is not null && saved.WrittenAt > BootedAt();
        if (saved is { Reverted: true } && !pending)
        {
            ForgetLayout();
            saved = null;
        }
        if (saved is not null) return new LayoutState([], saved.Reverted ? 0 : saved.Devices.Count, pending, !saved.Reverted);
        return new LayoutState(CurrentMoves(), 0, false, false);
    }

    /// <summary>Moves for the last test: only with late reports that matter, and only from a test still fresh.</summary>
    private IReadOnlyList<Move> CurrentMoves()
    {
        if (_last is not { Verdict: not Verdict.Retry, Test: { Cpu: { } cpu } test, Controller: { } controller } last || !Real(test) || TimeNow() - last.At > TimeSpan.FromMinutes(30)) return [];
        var list = ListDevices();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { controller };
        keep.UnionWith(BootDevices());
        keep.UnionWith(InputControllers());
        var pinned = list.Where(d => d.Policy == Specified && d.Override is { Length: > 0 }).Aggregate(0UL, (all, d) => all | Bits(d.Override!));
        return Suggest(list, _topology ?? MouseCore.Sampler.ReadTopology(), cpu, keep, pinned);
    }

    /// <summary>Writes the suggestion in one permission prompt, the values before saved first. Null when done.</summary>
    internal string? ApplyLayout()
    {
        var moves = CurrentMoves();
        if (moves.Count == 0) return "Test your mouse first.";
        if (LoadLayout() is { Reverted: false }) return "Exo's layout is already in place.";
        var list = ListDevices();
        var entries = moves.Select(m => list.First(d => Same(d.Id, m.Id))).Select((d, i) => new Moved(d.Id, moves[i].Cpu, d.Policy, d.Override is { } o ? Convert.ToHexString(o) : null)).ToList();
        if (!SaveLayout(new SavedLayout(TimeNow(), entries))) return "Your current settings couldn't be saved for Undo, so nothing changed.";
        if (WriteAll(moves.Select(m => (m.Id, (int?)Specified, (byte[]?)Mask(m.Cpu))).ToList()) is { } refused)
        {
            ForgetLayout();
            return refused;
        }
        var after = ListDevices();
        var took = moves.Count(m => after.FirstOrDefault(d => Same(d.Id, m.Id)) is { Policy: Specified } d && SameBytes(d.Override, Mask(m.Cpu)));
        if (took == 0)
        {
            ForgetLayout();
            return "Windows didn't take the change.";
        }
        AppLog.Info($"Mouse core: {took} of {moves.Count} device(s) moved off the mouse's CPU.");
        return took == moves.Count ? null : "Windows took only part of the change. Undo puts it back.";
    }

    /// <summary>Puts back exactly what the devices held before the layout.</summary>
    internal string? UndoLayout()
    {
        if (LoadLayout() is not { Reverted: false } saved) return "There's nothing to undo.";
        var writes = new List<(string, int?, byte[]?)>();
        try
        {
            foreach (var m in saved.Devices) writes.Add((m.Id, m.WasPolicy, m.WasMask is { } hex ? Convert.FromHexString(hex) : null));
        }
        catch (FormatException) { return "Exo's backup is damaged, so nothing changed."; }
        if (WriteAll(writes) is { } refused) return refused;
        SaveLayout(saved with { Reverted = true, WrittenAt = TimeNow() });
        AppLog.Info("Mouse core: the devices Exo moved are put back.");
        return null;
    }

    private string LayoutPath => Path.ChangeExtension(BackupFile(), null) + "-layout.json";

    private SavedLayout? LoadLayout()
    {
        try { return File.Exists(LayoutPath) ? JsonSerializer.Deserialize<SavedLayout>(File.ReadAllText(LayoutPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private bool SaveLayout(SavedLayout saved)
    {
        try
        {
            File.WriteAllText(LayoutPath + ".tmp", JsonSerializer.Serialize(saved));
            File.Move(LayoutPath + ".tmp", LayoutPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Mouse core: the layout's backup couldn't be saved: " + ex.Message);
            return false;
        }
    }

    private void ForgetLayout()
    {
        try { DeletionGate.DeleteFile(LayoutPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Mouse core: the layout's backup couldn't be removed: " + ex.Message); }
    }
}

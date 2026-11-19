using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// A process Exo starts while its host is below normal inherits below normal (CreateProcess's default under a
/// below-normal or idle parent), and so does everything that process starts: a game, or Steam and every game Steam
/// runs after it. Exo lowers its host only to yield to a game or streaming software, never to hand that on, so each
/// trim tick puts Exo's direct children that started while the host was low back to normal, WebView2's browser process
/// included. The hosted clients the trim loop sets itself, Exo Shaders' engine and a store tool's install run (both
/// below normal by design) keep theirs.
/// </summary>
internal static class InheritedPriority
{
    private const uint ProcessQueryLimitedInformation = 0x1000, ProcessSetInformation = 0x0200;
    private const uint BelowNormalPriorityClass = 0x4000, NormalPriorityClass = 0x20;
    private static readonly Lock Gate = new();
    private static readonly List<(long From, long To)> LowSpans = [];
    private static long? _lowSince;

    /// <summary>The host's priority class was just set: below normal or not.</summary>
    internal static void HostLowered(bool low)
    {
        var now = DateTime.UtcNow.ToFileTimeUtc();
        lock (Gate)
        {
            if (low) { _lowSince ??= now; return; }
            if (_lowSince is not { } since) return;
            LowSpans.Add((since, now));
            _lowSince = null;
            LowSpans.RemoveAll(span => now - span.To > TimeSpan.FromDays(1).Ticks);
        }
    }

    /// <summary>Puts each direct child of the host that inherited below normal back to normal; returns how many.</summary>
    internal static int Restore(IReadOnlySet<int> managed)
    {
        (long From, long To)[] spans;
        lock (Gate)
        {
            if (_lowSince is null && LowSpans.Count == 0) return 0;
            spans = [.. LowSpans, .. _lowSince is { } since ? [(since, long.MaxValue)] : Array.Empty<(long, long)>()];
        }
        var host = Environment.ProcessId;
        var hostStarted = Started(GetCurrentProcess()) ?? long.MaxValue;
        var restored = 0;
        foreach (var (pid, parent, name) in BackgroundTrim.ProcessTable())
        {
            if (parent != host || managed.Contains(pid) || name.StartsWith("ExoShaders", StringComparison.OrdinalIgnoreCase) ||
                Adapters.Cli.CliRunner.IsDownloadRun(pid)) continue;
            var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetInformation, false, pid);
            if (handle == IntPtr.Zero) continue;
            try
            {
                // A reused parent pid: a process older than Exo is not its child.
                if (Started(handle) is not { } created || created < hostStarted) continue;
                if (!spans.Any(span => created >= span.From && created <= span.To)) continue;
                if (GetPriorityClass(handle) != BelowNormalPriorityClass || !SetPriorityClass(handle, NormalPriorityClass)) continue;
                restored++;
                AppLog.Info($"Lean while playing: {name} started under Exo's lowered priority; set back to normal.");
            }
            finally { CloseHandle(handle); }
        }
        return restored;
    }

    private static long? Started(IntPtr process) =>
        GetProcessTimes(process, out var created, out _, out _, out _) ? created : null;

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern uint GetPriorityClass(IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool SetPriorityClass(IntPtr process, uint priority);
}

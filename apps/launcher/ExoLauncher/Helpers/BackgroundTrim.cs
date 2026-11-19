using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Releases working-set pages for our own process tree while Exo is in the
/// background. Never touches processes outside the tree.
/// </summary>
internal static class BackgroundTrim
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessSetInformation = 0x0200;
    private const int ProcessMemoryPriority = 0;
    private const int ProcessPowerThrottling = 4;
    private const uint MemoryPriorityLow = 2;
    private const uint MemoryPriorityNormal = 5;
    private const uint PowerThrottlingExecutionSpeed = 0x1, PowerThrottlingIgnoreTimerResolution = 0x4;
    private static readonly Lock HostGate = new();
    private static bool _hostEfficient, _hostLive;
    private static int _ecoQosUnavailableLogged;
    private const int ProcessIoPriority = 33, IoPriorityLow = 1, IoPriorityNormal = 2;
    [DllImport("ntdll.dll")] private static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int length);

    /// The root pid plus every descendant in the current process table. A
    /// parent pid outlives its process: Windows reuses the number, so a
    /// process whose real parent exited can look like the child of whatever
    /// started later with that pid. A child older than its supposed parent is
    /// therefore not a child, and Exo itself is never part of another tree.
    internal static IReadOnlyList<int> TreePids(int rootPid)
    {
        var pids = new List<int> { rootPid };
        var self = Environment.ProcessId;
        var created = new Dictionary<int, long>();
        foreach (var pid in Descendants(rootPid, ProcessTable().Select(entry => (entry.Pid, entry.Parent)).ToArray(), (child, parent) =>
                     IsTreeChild(child, parent, self, created)))
            pids.Add(pid);
        return pids;
    }

    /// TreePids' membership test: never Exo itself, never a child older than its supposed parent.
    internal static bool IsTreeChild(int child, int parent, int self, Dictionary<int, long> created) =>
        child != self && !StartedBefore(child, parent, created);

    /// Breadth-first descendant selection from a (pid, parent) table.
    internal static List<int> Descendants(int root, IReadOnlyList<(int pid, int parent)> table,
        Func<int, int, bool> isChild)
    {
        var found = new List<int>();
        var seen = new HashSet<int> { root };
        var children = table.ToLookup(entry => entry.parent, entry => entry.pid);
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var current))
        {
            foreach (var pid in children[current])
            {
                if (pid <= 0 || seen.Contains(pid) || !isChild(pid, current)) continue;
                seen.Add(pid);
                found.Add(pid);
                queue.Enqueue(pid);
            }
        }
        return found;
    }

    /// True only when both start times are readable and the child's is
    /// earlier; an unreadable process keeps the table's word for it.
    internal static bool StartedBefore(int child, int parent, Dictionary<int, long> cache)
    {
        var childStart = StartTicks(child, cache);
        var parentStart = StartTicks(parent, cache);
        return childStart != 0 && parentStart != 0 && childStart < parentStart;
    }

    private static long StartTicks(int pid, Dictionary<int, long> cache)
    {
        if (cache.TryGetValue(pid, out var known)) return known;
        long ticks = 0;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle != IntPtr.Zero)
        {
            try { if (GetProcessTimes(handle, out var created, out _, out _, out _)) ticks = created; }
            finally { CloseHandle(handle); }
        }
        cache[pid] = ticks;
        return ticks;
    }

    /// EcoQoS on the host process only — renderers, GPU and utilities keep
    /// full speed so music playback is never throttled. Held off while live
    /// sound runs (<see cref="Services.DiscordClient.Voice.LiveAudio"/>).
    internal static void SetHostEfficiencyMode(bool on)
    {
        lock (HostGate)
        {
            _hostEfficient = on;
            ApplyHost();
        }
    }

    /// Live sound started or stopped: while it runs the host is never in
    /// EcoQoS, its memory priority stays normal, and Windows honors its timer
    /// resolution even with the window hidden.
    internal static void SetHostLiveAudio(bool live)
    {
        lock (HostGate)
        {
            _hostLive = live;
            ApplyHost();
        }
    }

    // Under HostGate.
    private static void ApplyHost()
    {
        try
        {
            var process = GetCurrentProcess();
            var state = new ProcessPowerThrottlingState
            {
                Version = 1,
                ControlMask = PowerThrottlingExecutionSpeed | (_hostLive ? PowerThrottlingIgnoreTimerResolution : 0),
                StateMask = _hostEfficient && !_hostLive ? PowerThrottlingExecutionSpeed : 0,
            };
            // PowerThrottlingOff=1 (a tweak playbook's) makes Windows refuse EcoQoS on the whole PC: unavailable here, not a
            // failure; the I/O and memory priorities below still apply.
            if (!SetProcessInformation(process, ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>())
                && Marshal.GetLastWin32Error() == 1 && Interlocked.Exchange(ref _ecoQosUnavailableLogged, 1) == 0)
                AppLog.Info("Background: EcoQoS unavailable on this PC (power throttling is off); priorities still apply.");
            // Away or in a game, the host's disk reads (library scans, covers) yield to the game's; live sound keeps them Normal.
            var io = _hostEfficient && !_hostLive ? IoPriorityLow : IoPriorityNormal;
            NtSetInformationProcess(process, ProcessIoPriority, ref io, sizeof(int));
            if (!_hostLive) return;
            var memory = new MemoryPriorityInformation { MemoryPriority = MemoryPriorityNormal };
            SetProcessInformation(process, ProcessMemoryPriority, ref memory, Marshal.SizeOf<MemoryPriorityInformation>());
        }
        catch { }
    }

    internal static bool ApplyManaged(ManagedProcessIdentity identity, bool low, bool trim)
    {
        var access = ProcessQueryInformation | ProcessSetInformation | (trim ? ProcessSetQuota : 0);
        var handle = OpenProcess(access, false, identity.Pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _) ||
                DateTime.FromFileTimeUtc(created).Ticks != identity.StartedUtcTicks) return false;
            var info = new MemoryPriorityInformation { MemoryPriority = low ? MemoryPriorityLow : MemoryPriorityNormal };
            var changed = SetProcessInformation(handle, ProcessMemoryPriority, ref info, Marshal.SizeOf<MemoryPriorityInformation>());
            if (trim) changed &= EmptyWorkingSet(handle);
            return changed;
        }
        catch { return false; }
        finally { CloseHandle(handle); }
    }

    /// Every process with its parent pid and executable file name (".exe" included; "" for the idle process). One
    /// NtQuerySystemInformation call reads the kernel's list in place: ToolHelp copied that same list into a snapshot
    /// and handed it out a marshalled row at a time, twice the processor time per table (9 ms against 4.6 ms on 240
    /// processes), and the run-state poll, the idle check and the store sweep each read one.
    internal static unsafe List<(int Pid, int Parent, string Name)> ProcessTable()
    {
        var table = new List<(int, int, string)>(256);
        var size = Volatile.Read(ref _tableBytes);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                fixed (byte* start = buffer)
                {
                    var status = NtQuerySystemInformation(SystemProcessInformation, start, buffer.Length, out var needed);
                    if (status == StatusInfoLengthMismatch)
                    {
                        // Processes start between the ask and the answer: room for some more.
                        size = Math.Max(buffer.Length, needed) + 64 * 1024;
                        continue;
                    }
                    if (status < 0) return table;
                    Volatile.Write(ref _tableBytes, Math.Max(size, needed + 64 * 1024));
                    // SYSTEM_PROCESS_INFORMATION (x64): NextEntryOffset at 0, ImageName (UNICODE_STRING) at 0x38,
                    // UniqueProcessId at 0x50, InheritedFromUniqueProcessId at 0x58. The name points into the buffer.
                    for (var entry = start; ; )
                    {
                        var name = *(char**)(entry + 0x40);
                        table.Add(((int)*(nint*)(entry + 0x50), (int)*(nint*)(entry + 0x58),
                            name == null ? "" : new string(name, 0, *(ushort*)(entry + 0x38) / 2)));
                        var next = *(int*)entry;
                        if (next == 0) break;
                        entry += next;
                    }
                }
                return table;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        return table;
    }

    private const int SystemProcessInformation = 5, StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private static int _tableBytes = 512 * 1024;
    [DllImport("ntdll.dll")] private static extern unsafe int NtQuerySystemInformation(int infoClass, byte* info, int length, out int returned);
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryPriorityInformation
    {
        public uint MemoryPriority;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool SetPriorityClass(IntPtr process, uint priority);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr hProcess, int informationClass, ref MemoryPriorityInformation info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr hProcess, int informationClass, ref ProcessPowerThrottlingState state, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}

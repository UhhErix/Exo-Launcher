using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// An install, update or verify run of a store's tool (Legendary, gogdl, Nile) and every process it starts: a job
/// object holds them all at below-normal CPU priority (idle while a game or streaming software runs, LeanSignal) and
/// each member gets low I/O priority (very low under lean), so a download never takes a frame or a disk read from what
/// the user is doing. Legendary's download workers start after the tool does; they join the job on their own, and a
/// sweep every 2 seconds gives each new one its I/O priority. Best effort: if Windows refuses, the run goes on as before.
/// </summary>
internal sealed class CliJobPriority : IDisposable
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(2);
    private readonly IntPtr _job;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<int> _lowered = [];
    private bool _lean;

    private CliJobPriority(IntPtr job, bool lean)
    {
        _job = job;
        _lean = lean;
        _ = Task.Run(SweepAsync);
    }

    internal static CliJobPriority? Attach(Process process)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return null;
        var lean = Services.LeanSignal.Active;
        try
        {
            if (SetPriority(job, lean) && AssignProcessToJobObject(job, process.Handle))
            {
                var attached = new CliJobPriority(job, lean);
                attached.Sweep();
                return attached;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        AppLog.Debug($"Download priority: job object refused ({Marshal.GetLastWin32Error()}).");
        CloseHandle(job);
        return null;
    }

    private async Task SweepAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(SweepEvery, _stop.Token).ConfigureAwait(false);
                Sweep();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Sweep()
    {
        lock (_lowered)
        {
            if (_stop.IsCancellationRequested) return;
            var lean = Services.LeanSignal.Active;
            if (lean != _lean && SetPriority(_job, lean))
            {
                _lean = lean;
                _lowered.Clear();
            }
            foreach (var pid in Members())
                if (!_lowered.Contains(pid) && SetIoPriority(pid, _lean ? IoPriorityVeryLow : IoPriorityLow))
                    _lowered.Add(pid);
        }
    }

    private List<int> Members()
    {
        const int Slots = 128;
        var buffer = new byte[8 + Slots * IntPtr.Size];
        var pids = new List<int>();
        // ERROR_MORE_DATA still fills the slots it has.
        QueryInformationJobObject(_job, JobObjectBasicProcessIdList, buffer, buffer.Length, IntPtr.Zero);
        var listed = Math.Min(BitConverter.ToInt32(buffer, 4), Slots);
        for (var i = 0; i < listed; i++)
            pids.Add((int)BitConverter.ToInt64(buffer, 8 + i * IntPtr.Size));
        return pids;
    }

    private static bool SetPriority(IntPtr job, bool lean)
    {
        var limits = new BasicLimitInformation
        {
            LimitFlags = JobObjectLimitPriorityClass,
            PriorityClass = lean ? IdlePriorityClass : BelowNormalPriorityClass,
        };
        return SetInformationJobObject(job, JobObjectBasicLimitInformationClass, ref limits, Marshal.SizeOf<BasicLimitInformation>());
    }

    private static bool SetIoPriority(int pid, int priority)
    {
        var handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try { return NtSetInformationProcess(handle, ProcessIoPriority, ref priority, sizeof(int)) >= 0; }
        finally { CloseHandle(handle); }
    }

    public void Dispose()
    {
        lock (_lowered)
        {
            if (_stop.IsCancellationRequested) return;
            _stop.Cancel();
            // No kill-on-close: closing the handle leaves any member running as it is.
            CloseHandle(_job);
        }
        _stop.Dispose();
    }

    private const int JobObjectBasicLimitInformationClass = 2, JobObjectBasicProcessIdList = 3, ProcessIoPriority = 33;
    private const int IoPriorityVeryLow = 0, IoPriorityLow = 1;
    private const uint JobObjectLimitPriorityClass = 0x20, IdlePriorityClass = 0x40, BelowNormalPriorityClass = 0x4000;
    private const uint ProcessSetInformation = 0x0200, ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref BasicLimitInformation info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, byte[] info, int length, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int length);
}

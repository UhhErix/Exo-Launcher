using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Helpers;

/// <summary>
/// Best settings › System's latency check: how long drivers hold the processor in their deferred procedure calls (DPCs) and
/// interrupt service routines (ISRs), measured the way LatencyMon and xperf do. A kernel system logger session (Windows 8+,
/// its own name, so it never takes the NT Kernel Logger from another tool) records the DPC and interrupt groups for ten
/// seconds; each event's header time minus its InitialTime (both the performance counter, raw) is how long that routine ran,
/// and its routine address names its driver (the loaded drivers' bases). Read-only: nothing is written but the answer file.
/// Starting a kernel session needs an administrator, so it runs as `ExoLauncher.exe --latency-check &lt;file&gt;`, started
/// elevated by the check's press (<see cref="Services.SystemTuning.LatencyAsync"/>).
/// Microsoft's driver guidance: an ISR should run under 25 µs and a DPC under 100 µs; LatencyMon calls over 1000 µs a
/// cause of audio drop-outs.
/// </summary>
internal static unsafe partial class LatencyTrace
{
    internal const string Flag = "--latency-check";
    private const string SessionName = "Exo Latency Check";

    // PerfInfo, the classic kernel provider of DPC and ISR events {ce1dbfb4-137e-4da6-87b0-3f59aa102cbc}: its first 4 bytes.
    private const uint PerfInfoFirst = 0xCE1DBFB4;
    private const byte ThreadDpc = 66, Isr = 67, Dpc = 68, TimerDpc = 69, IsrMsi = 50;
    private const uint FlagDpc = 0x20, FlagInterrupt = 0x40;
    private const uint RealTime = 0x100, SystemLogger = 0x02000000, ModeRealTime = 0x100, ModeRawTimestamp = 0x1000, ModeEventRecord = 0x10000000;
    private const uint ControlStop = 1, ClockQpc = 1, FlagTracedGuid = 0x00020000;
    private const ulong InvalidHandle = ulong.MaxValue;

    /// <summary>One routine's runs: how many, the longest and the total, in performance counter ticks.</summary>
    internal sealed class Routine
    {
        public bool Interrupt;
        public long Count, Max, Total;
    }

    /// <summary>Every routine seen, by address. Filled on the trace's one thread; read after it ends.</summary>
    internal sealed class Stats
    {
        internal readonly Dictionary<ulong, Routine> Routines = [];
        internal long Dpcs, Isrs;

        internal void Add(bool interrupt, ulong routine, long ticks)
        {
            // A clock that went back, or a run longer than a second (a lost event's pair), isn't a run.
            if (ticks < 0 || ticks > Stopwatch.Frequency) return;
            if (!Routines.TryGetValue(routine, out var r)) Routines[routine] = r = new Routine { Interrupt = interrupt };
            r.Count++;
            r.Total += ticks;
            if (ticks > r.Max) r.Max = ticks;
            if (interrupt) Isrs++; else Dpcs++;
        }
    }

    /// <summary>
    /// A DPC or ISR event's fields (64-bit Windows): InitialTime (UInt64) then Routine (a pointer); an ISR adds its return value
    /// and vector after them. Null for any other event or one too short.
    /// </summary>
    internal static (bool Interrupt, ulong Routine, long Initial)? Parse(byte opcode, ReadOnlySpan<byte> data)
    {
        bool interrupt;
        switch (opcode)
        {
            case ThreadDpc or Dpc or TimerDpc: interrupt = false; break;
            case Isr or IsrMsi: interrupt = true; break;
            default: return null;
        }
        if (data.Length < 16) return null;
        return (interrupt, BinaryPrimitives.ReadUInt64LittleEndian(data[8..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(data));
    }

    /// <summary>A loaded driver: its file name and where it starts in memory.</summary>
    internal sealed record Module(string Name, ulong Base);

    /// <summary>The driver an address falls in: the one with the highest base at or below it (bases sorted). Null below them all.</summary>
    internal static string? ModuleOf(ulong address, IReadOnlyList<Module> sorted)
    {
        int lo = 0, hi = sorted.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid].Base <= address) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found < 0 ? null : sorted[found].Name;
    }

    /// <summary>One driver's part: its DPCs' and ISRs' count, their longest runs and their total, in µs.</summary>
    internal sealed record Driver(string Name, long Count, double DpcMaxUs, double IsrMaxUs, double TotalUs);

    /// <summary>What the check measured: how long, how many, the longest DPC and ISR with their drivers, and the drivers by longest run (five).</summary>
    internal sealed record Result(DateTimeOffset At, double Seconds, long Dpcs, long Isrs, double DpcMaxUs, string? DpcDriver, double IsrMaxUs, string? IsrDriver, IReadOnlyList<Driver> Drivers);

    /// <summary>The check's answer file: a result, or the reason there's none.</summary>
    internal sealed record Answer(Result? Result, string? Problem);

    internal static Result Summarize(Stats stats, IReadOnlyList<Module> modules, long frequency, double seconds, DateTimeOffset at)
    {
        var sorted = modules.OrderBy(m => m.Base).ToList();
        double Us(long ticks) => Math.Round(ticks * 1_000_000.0 / frequency, 1);
        var byDriver = stats.Routines.GroupBy(r => ModuleOf(r.Key, sorted) ?? "unknown")
            .Select(g => new Driver(g.Key, g.Sum(r => r.Value.Count),
                Us(g.Where(r => !r.Value.Interrupt).Select(r => r.Value.Max).DefaultIfEmpty(0).Max()),
                Us(g.Where(r => r.Value.Interrupt).Select(r => r.Value.Max).DefaultIfEmpty(0).Max()),
                Us(g.Sum(r => r.Value.Total))))
            .OrderByDescending(d => Math.Max(d.DpcMaxUs, d.IsrMaxUs)).ToList();
        var dpc = byDriver.Where(d => d.DpcMaxUs > 0).MaxBy(d => d.DpcMaxUs);
        var isr = byDriver.Where(d => d.IsrMaxUs > 0).MaxBy(d => d.IsrMaxUs);
        return new Result(at, seconds, stats.Dpcs, stats.Isrs, dpc?.DpcMaxUs ?? 0, dpc?.Name, isr?.IsrMaxUs ?? 0, isr?.Name, byDriver.Take(5).ToList());
    }

    private static readonly Regex AnswerNameRegex = new(@"^exo-[0-9a-f]{32}\.latency\.json$", RegexOptions.CultureInvariant);

    /// <summary>The answer file the elevated check may write: one name in the user's temp folder, nothing else.</summary>
    internal static Regex AnswerName() => AnswerNameRegex;

    /// <summary>`ExoLauncher.exe --latency-check &lt;file&gt;`, elevated: ten seconds of tracing, then the answer file. 0 when it wrote one.</summary>
    internal static int Run(string[] args)
    {
        var at = Array.FindIndex(args, a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));
        var path = args.ElementAtOrDefault(at + 1);
        if (path is null || !AnswerName().IsMatch(Path.GetFileName(path)) || !Path.IsPathFullyQualified(path)) return 2;
        Answer answer;
        try { answer = Measure(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            answer = new Answer(null, "Windows didn't start the trace.");
            AppLog.Warn("Latency check: " + ex.Message);
        }
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(answer));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Latency check: the answer couldn't be written: " + ex.Message);
            return 1;
        }
    }

    private static Stats? _stats;

    private static Answer Measure(TimeSpan length)
    {
        var stats = new Stats();
        Volatile.Write(ref _stats, stats);
        Stop();
        var properties = Properties();
        ulong session;
        uint error;
        fixed (char* name = SessionName) error = StartTraceW(&session, name, &properties);
        if (error != 0) return new Answer(null, error == 5 ? "Windows' permission is needed to measure." : $"Windows didn't start the trace (error {error}).");
        ulong consumer;
        fixed (char* name = SessionName)
        {
            var logfile = new PresentTrace.EventTraceLogfile
            {
                LoggerName = name,
                ProcessTraceMode = ModeRealTime | ModeEventRecord | ModeRawTimestamp,
                EventRecordCallback = &OnEvent,
            };
            consumer = OpenTraceW(&logfile);
        }
        if (consumer == InvalidHandle)
        {
            Stop();
            return new Answer(null, "Windows didn't open the trace.");
        }
        var watch = Stopwatch.StartNew();
        var thread = new Thread(() => { var handle = consumer; ProcessTrace(&handle, 1, null, null); }, 256 * 1024) { IsBackground = true, Name = "Exo latency trace" };
        thread.Start();
        Thread.Sleep(length);
        Stop();
        thread.Join(TimeSpan.FromSeconds(5));
        CloseTrace(consumer);
        Volatile.Write(ref _stats, null);
        return new Answer(Summarize(stats, Drivers(), Stopwatch.Frequency, watch.Elapsed.TotalSeconds, DateTimeOffset.UtcNow), null);
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(PresentTrace.EventRecord* record)
    {
        try
        {
            if (record->ProviderFirst != PerfInfoFirst || Volatile.Read(ref _stats) is not { } stats) return;
            if (Parse(record->Opcode, new ReadOnlySpan<byte>(record->UserData, record->UserDataLength)) is not { } run) return;
            stats.Add(run.Interrupt, run.Routine, record->TimeStamp - run.Initial);
        }
        catch { }
    }

    private static void Stop()
    {
        var properties = Properties();
        fixed (char* name = SessionName) ControlTraceW(0, name, &properties, ControlStop);
    }

    private static PresentTrace.TraceProperties Properties() => new()
    {
        WnodeBufferSize = (uint)sizeof(PresentTrace.TraceProperties),
        WnodeGuid = new Guid("6d1f3c52-7a0b-4f0e-9c1e-3f5e0b7a9d21"),
        WnodeClientContext = ClockQpc,
        WnodeFlags = FlagTracedGuid,
        BufferKb = 256,
        MinimumBuffers = 8,
        MaximumBuffers = 64,
        LogFileMode = RealTime | SystemLogger,
        EnableFlags = FlagDpc | FlagInterrupt,
        FlushTimerSeconds = 1,
        LoggerNameOffset = PresentTrace.TraceProperties.NameOffset,
    };

    /// <summary>The loaded drivers' file names and bases (real only to an administrator, which the check is).</summary>
    private static List<Module> Drivers()
    {
        var list = new List<Module>();
        var bases = new nint[1024];
        fixed (nint* p = bases)
        {
            if (!K32EnumDeviceDrivers(p, (uint)(bases.Length * IntPtr.Size), out var needed)) return list;
            var count = Math.Min(bases.Length, (int)(needed / IntPtr.Size));
            var name = stackalloc char[260];
            for (var i = 0; i < count; i++)
            {
                var length = K32GetDeviceDriverBaseNameW(bases[i], name, 260);
                if (length > 0 && bases[i] != 0) list.Add(new Module(new string(name, 0, (int)length), (ulong)bases[i]));
            }
        }
        return list;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint StartTraceW(ulong* session, char* name, PresentTrace.TraceProperties* properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTraceW(ulong session, char* name, PresentTrace.TraceProperties* properties, uint code);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ulong OpenTraceW(PresentTrace.EventTraceLogfile* logfile);
    [DllImport("advapi32.dll")] private static extern uint ProcessTrace(ulong* handles, uint count, void* start, void* end);
    [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool K32EnumDeviceDrivers(nint* bases, uint size, out uint needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint K32GetDeviceDriverBaseNameW(nint imageBase, char* name, uint size);
}

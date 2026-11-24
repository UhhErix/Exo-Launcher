using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Helpers;

internal static unsafe partial class MouseDiagTrace
{
    internal const string Flag = "--mouse-diag-etw";
    internal const string SessionName = "Exo Mouse Diagnostics";

    private const uint PerfInfoFirst = 0xCE1DBFB4;
    private const byte OpcodeIsrMsi = 50;
    private const byte OpcodeThreadDpc = 66;
    private const byte OpcodeIsr = 67;
    private const byte OpcodeDpc = 68;
    private const byte OpcodeTimerDpc = 69;
    private const uint FlagDpc = 0x20;
    private const uint FlagInterrupt = 0x40;
    private const uint ModeRealTime = 0x100;
    private const uint ModeRawTimestamp = 0x1000;
    private const uint ModeSystemLogger = 0x02000000;
    private const uint ModeEventRecord = 0x10000000;
    private const uint ControlStop = 1;
    private const uint ClockQpc = 1;
    private const uint FlagTracedGuid = 0x00020000;
    private const ushort FlagProcessorIndex = 0x0200;
    private const ulong InvalidHandle = ulong.MaxValue;

    internal sealed record CoreStats(
        int Core,
        long IsrCount,
        double IsrTotalUs,
        double IsrMaxUs,
        long DpcCount,
        double DpcTotalUs,
        double DpcMaxUs,
        bool IsMouseCore);

    internal sealed record DriverStats(
        string Driver,
        long DpcCount,
        double DpcTotalUs,
        double DpcMaxUs,
        long IsrCount,
        double IsrTotalUs,
        double IsrMaxUs,
        int TopCore,
        IReadOnlyList<int> Cores);

    internal sealed record Result(
        DateTimeOffset At,
        double Seconds,
        long TotalIsrs,
        long TotalDpcs,
        double TotalIsrUs,
        double TotalDpcUs,
        double MaxIsrUs,
        double MaxDpcUs,
        long EventsLost,
        int? MouseControllerCore,
        IReadOnlyList<CoreStats> Cores,
        IReadOnlyList<DriverStats> TopDrivers);

    internal sealed record Answer(Result? Result, string? Problem);

    internal sealed record DriverModule(string Name, ulong Base);

    internal sealed class Aggregator
    {
        private sealed class CoreAcc
        {
            public long IsrCount;
            public long IsrTotal;
            public long IsrMax;
            public long DpcCount;
            public long DpcTotal;
            public long DpcMax;
        }

        private sealed class RoutineAcc
        {
            public bool Interrupt;
            public long Count;
            public long Total;
            public long Max;
            public readonly Dictionary<int, long> ByCore = [];
        }

        private readonly Dictionary<int, CoreAcc> _cores = [];
        private readonly Dictionary<(ulong Routine, bool Interrupt), RoutineAcc> _routines = [];
        private long _totalIsrs;
        private long _totalDpcs;
        private long _totalIsrTicks;
        private long _totalDpcTicks;
        private long _maxIsrTicks;
        private long _maxDpcTicks;

        internal void AddEvent(byte opcode, int processor, long timestamp, ReadOnlySpan<byte> userData, long frequency)
        {
            bool interrupt;
            switch (opcode)
            {
                case OpcodeThreadDpc or OpcodeDpc or OpcodeTimerDpc:
                    interrupt = false;
                    break;
                case OpcodeIsr or OpcodeIsrMsi:
                    interrupt = true;
                    break;
                default:
                    return;
            }
            if (userData.Length < 16 || processor < 0 || processor >= 1024) return;
            var initialTime = BinaryPrimitives.ReadInt64LittleEndian(userData);
            var routine = BinaryPrimitives.ReadUInt64LittleEndian(userData[8..]);
            var ticks = timestamp - initialTime;
            if (ticks < 0 || ticks > frequency) return;

            if (!_cores.TryGetValue(processor, out var core)) _cores[processor] = core = new CoreAcc();
            if (interrupt)
            {
                core.IsrCount++;
                core.IsrTotal += ticks;
                if (ticks > core.IsrMax) core.IsrMax = ticks;
                _totalIsrs++;
                _totalIsrTicks += ticks;
                if (ticks > _maxIsrTicks) _maxIsrTicks = ticks;
            }
            else
            {
                core.DpcCount++;
                core.DpcTotal += ticks;
                if (ticks > core.DpcMax) core.DpcMax = ticks;
                _totalDpcs++;
                _totalDpcTicks += ticks;
                if (ticks > _maxDpcTicks) _maxDpcTicks = ticks;
            }

            var key = (routine, interrupt);
            if (!_routines.TryGetValue(key, out var r)) _routines[key] = r = new RoutineAcc { Interrupt = interrupt };
            r.Count++;
            r.Total += ticks;
            if (ticks > r.Max) r.Max = ticks;
            r.ByCore[processor] = r.ByCore.GetValueOrDefault(processor) + ticks;
        }

        internal Result Summarize(IReadOnlyList<DriverModule> modules, long frequency, double seconds, DateTimeOffset at, long eventsLost = 0, int? hintMouseCore = null)
        {
            var sorted = modules.OrderBy(m => m.Base).ToList();
            double Us(long ticks) => frequency <= 0 ? 0 : Math.Round(ticks * 1_000_000.0 / frequency, 1);

            var driverMap = new Dictionary<string, (long DpcCount, long DpcTotal, long DpcMax, long IsrCount, long IsrTotal, long IsrMax, Dictionary<int, long> Cores)>(StringComparer.OrdinalIgnoreCase);
            foreach (var ((routine, interrupt), acc) in _routines)
            {
                var name = ResolveModule(routine, sorted) ?? "unknown";
                if (!driverMap.TryGetValue(name, out var entry))
                {
                    entry = (0, 0, 0, 0, 0, 0, []);
                }
                if (interrupt)
                {
                    entry.IsrCount += acc.Count;
                    entry.IsrTotal += acc.Total;
                    if (acc.Max > entry.IsrMax) entry.IsrMax = acc.Max;
                }
                else
                {
                    entry.DpcCount += acc.Count;
                    entry.DpcTotal += acc.Total;
                    if (acc.Max > entry.DpcMax) entry.DpcMax = acc.Max;
                }
                foreach (var (coreId, ticks) in acc.ByCore)
                {
                    entry.Cores[coreId] = entry.Cores.GetValueOrDefault(coreId) + ticks;
                }
                driverMap[name] = entry;
            }

            int? xhciCore = null;
            if (driverMap.TryGetValue("usbxhci.sys", out var xhci) && xhci.Cores.Count > 0)
            {
                xhciCore = xhci.Cores.MaxBy(kv => kv.Value).Key;
            }
            var mouseCore = hintMouseCore ?? xhciCore;

            var topDrivers = driverMap
                .Select(kv =>
                {
                    var v = kv.Value;
                    var orderedCores = v.Cores.OrderByDescending(c => c.Value).Select(c => c.Key).Take(4).ToList();
                    var topCore = orderedCores.Count > 0 ? orderedCores[0] : -1;
                    return new DriverStats(
                        kv.Key,
                        v.DpcCount,
                        Us(v.DpcTotal),
                        Us(v.DpcMax),
                        v.IsrCount,
                        Us(v.IsrTotal),
                        Us(v.IsrMax),
                        topCore,
                        orderedCores);
                })
                .OrderByDescending(d => d.DpcTotalUs)
                .ThenByDescending(d => d.DpcMaxUs)
                .Take(10)
                .ToList();

            var coreStats = _cores
                .OrderBy(kv => kv.Key)
                .Select(kv => new CoreStats(
                    kv.Key,
                    kv.Value.IsrCount,
                    Us(kv.Value.IsrTotal),
                    Us(kv.Value.IsrMax),
                    kv.Value.DpcCount,
                    Us(kv.Value.DpcTotal),
                    Us(kv.Value.DpcMax),
                    mouseCore == kv.Key))
                .ToList();

            return new Result(
                at,
                Math.Round(seconds, 2),
                _totalIsrs,
                _totalDpcs,
                Us(_totalIsrTicks),
                Us(_totalDpcTicks),
                Us(_maxIsrTicks),
                Us(_maxDpcTicks),
                eventsLost,
                mouseCore,
                coreStats,
                topDrivers);
        }

        internal static string? ResolveModule(ulong address, IReadOnlyList<DriverModule> sorted)
        {
            int lo = 0, hi = sorted.Count - 1, found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (sorted[mid].Base <= address)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return found < 0 ? null : sorted[found].Name;
        }
    }

    private static readonly Regex AnswerNameRegexValue = new(@"^exo-[0-9a-f]{32}\.mouse-diag\.json$", RegexOptions.CultureInvariant);
    private static readonly Regex SignalNameRegexValue = new(@"^exo-[0-9a-f]{32}\.(ready|stop)$", RegexOptions.CultureInvariant);

    internal static Regex AnswerNameRegex() => AnswerNameRegexValue;

    internal static Regex SignalNameRegex() => SignalNameRegexValue;

    internal static int Run(string[] args)
    {
        var at = Array.FindIndex(args, a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));
        var answerPath = args.ElementAtOrDefault(at + 1);
        var readyPath = args.ElementAtOrDefault(at + 2);
        var stopPath = args.ElementAtOrDefault(at + 3);
        var secondsText = args.ElementAtOrDefault(at + 4);
        if (answerPath is null || !AnswerNameRegex().IsMatch(Path.GetFileName(answerPath)) || !Path.IsPathFullyQualified(answerPath)) return 2;
        if (readyPath is not null && (!SignalNameRegex().IsMatch(Path.GetFileName(readyPath)) || !Path.IsPathFullyQualified(readyPath))) return 2;
        if (stopPath is not null && (!SignalNameRegex().IsMatch(Path.GetFileName(stopPath)) || !Path.IsPathFullyQualified(stopPath))) return 2;
        var durationSeconds = int.TryParse(secondsText, out var s) ? Math.Clamp(s, 1, 30) : 10;

        EventHandler onExit = (_, _) => StopSession();
        AppDomain.CurrentDomain.ProcessExit += onExit;
        Answer answer;
        try
        {
            answer = Capture(TimeSpan.FromSeconds(durationSeconds), readyPath, stopPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            StopSession();
            answer = new Answer(null, "Windows didn't start the ETW kernel trace.");
            AppLog.Warn("Mouse diag ETW: " + ex.Message);
        }
        finally
        {
            StopSession();
            AppDomain.CurrentDomain.ProcessExit -= onExit;
        }

        try
        {
            File.WriteAllText(answerPath, JsonSerializer.Serialize(answer));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Mouse diag ETW: could not write answer: " + ex.Message);
            return 1;
        }
    }

    internal static void StopSession()
    {
        var properties = BuildProperties();
        fixed (char* name = SessionName) ControlTraceW(0, name, &properties, ControlStop);
    }

    private static Aggregator? _active;
    private static long _activeFrequency;

    private static Answer Capture(TimeSpan length, string? readyPath, string? stopPath)
    {
        var agg = new Aggregator();
        Volatile.Write(ref _activeFrequency, Stopwatch.Frequency);
        Volatile.Write(ref _active, agg);
        StopSession();

        var properties = BuildProperties();
        ulong session;
        uint error;
        fixed (char* name = SessionName) error = StartTraceW(&session, name, &properties);
        if (error != 0)
        {
            Volatile.Write(ref _active, null);
            var msg = error == 5
                ? "Administrator permission is required for ETW ISR/DPC tracing."
                : $"Windows didn't start the ETW session (error {error}).";
            return new Answer(null, msg);
        }

        ulong consumer = InvalidHandle;
        try
        {
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
                StopSession();
                return new Answer(null, "Windows didn't open the ETW real-time trace.");
            }

            var watch = Stopwatch.StartNew();
            var thread = new Thread(() =>
            {
                var handle = consumer;
                ProcessTrace(&handle, 1, null, null);
            }, 256 * 1024)
            {
                IsBackground = true,
                Name = "Exo mouse diag ETW",
            };
            thread.Start();

            if (readyPath is not null)
            {
                try { File.WriteAllText(readyPath, "1"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            var deadline = watch.Elapsed + length;
            while (watch.Elapsed < deadline)
            {
                if (stopPath is not null && File.Exists(stopPath)) break;
                var remaining = deadline - watch.Elapsed;
                Thread.Sleep((int)Math.Clamp(remaining.TotalMilliseconds, 10, 100));
            }

            var stopProps = BuildProperties();
            fixed (char* name = SessionName) ControlTraceW(0, name, &stopProps, ControlStop);
            thread.Join(TimeSpan.FromSeconds(5));
            var lost = (long)stopProps.EventsLost + stopProps.RealTimeBuffersLost;
            return new Answer(agg.Summarize(EnumDrivers(), Stopwatch.Frequency, watch.Elapsed.TotalSeconds, DateTimeOffset.UtcNow, lost), null);
        }
        finally
        {
            StopSession();
            if (consumer != InvalidHandle) CloseTrace(consumer);
            Volatile.Write(ref _active, null);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(PresentTrace.EventRecord* record)
    {
        if (record->ProviderFirst != PerfInfoFirst || Volatile.Read(ref _active) is not { } agg) return;
        var raw = (byte*)record;
        var processor = (record->HeaderFlags & FlagProcessorIndex) != 0
            ? (int)*(ushort*)(raw + 80)
            : raw[80];
        agg.AddEvent(record->Opcode, processor, record->TimeStamp, new ReadOnlySpan<byte>(record->UserData, record->UserDataLength), Volatile.Read(ref _activeFrequency));
    }

    private static PresentTrace.TraceProperties BuildProperties() => new()
    {
        WnodeBufferSize = (uint)sizeof(PresentTrace.TraceProperties),
        WnodeGuid = new Guid("9c42e8a1-3b17-4f63-91a8-4e6a2d8f3c11"),
        WnodeClientContext = ClockQpc,
        WnodeFlags = FlagTracedGuid,
        BufferKb = 256,
        MinimumBuffers = 8,
        MaximumBuffers = 64,
        LogFileMode = ModeRealTime | ModeSystemLogger,
        EnableFlags = FlagDpc | FlagInterrupt,
        FlushTimerSeconds = 1,
        LoggerNameOffset = PresentTrace.TraceProperties.NameOffset,
    };

    private static List<DriverModule> EnumDrivers()
    {
        var list = new List<DriverModule>();
        var bases = new nint[1024];
        fixed (nint* p = bases)
        {
            if (!K32EnumDeviceDrivers(p, (uint)(bases.Length * IntPtr.Size), out var needed)) return list;
            var count = Math.Min(bases.Length, (int)(needed / IntPtr.Size));
            var name = stackalloc char[260];
            for (var i = 0; i < count; i++)
            {
                var len = K32GetDeviceDriverBaseNameW(bases[i], name, 260);
                if (len > 0 && bases[i] != 0) list.Add(new DriverModule(new string(name, 0, (int)len), (ulong)bases[i]));
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

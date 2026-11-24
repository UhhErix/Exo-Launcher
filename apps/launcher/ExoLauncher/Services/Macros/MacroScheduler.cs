using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExoLauncher.Services.Macros;

internal interface IMacroOutputSink
{
    void Emit(MacroStep step, string delivery);
}

internal sealed class DryRunMacroSink : IMacroOutputSink
{
    private readonly List<(MacroStep Step, long TimestampTicks)> _events = [];

    internal IReadOnlyList<(MacroStep Step, long TimestampTicks)> Events => _events;

    public void Emit(MacroStep step, string delivery)
    {
        _events.Add((step, Stopwatch.GetTimestamp()));
    }
}

internal sealed class MacroChordMatcher
{
    private readonly Dictionary<string, long> _pressed = new(StringComparer.OrdinalIgnoreCase);

    internal void KeyDown(string inputKey, long timestampTicks)
    {
        _pressed[inputKey] = timestampTicks;
    }

    internal void KeyUp(string inputKey)
    {
        _pressed.Remove(inputKey);
    }

    internal bool Matches(MacroTrigger trigger, string activeLayerId, long nowTicks)
    {
        if (!string.Equals(trigger.LayerId, activeLayerId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (trigger.Chord.Count == 0) return false;

        long earliest = long.MaxValue;
        long latest = long.MinValue;

        foreach (var spec in trigger.Chord)
        {
            var key = FormatKey(spec);
            if (!_pressed.TryGetValue(key, out var downAt))
            {
                if (spec.DeviceId is not null)
                {
                    return false;
                }
                var anyMatch = _pressed.FirstOrDefault(kv => kv.Key.EndsWith(":" + spec.DeviceKind + ":" + spec.Code, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(anyMatch.Key)) return false;
                downAt = anyMatch.Value;
            }
            if (downAt < earliest) earliest = downAt;
            if (downAt > latest) latest = downAt;
        }

        if (trigger.Chord.Count == 1) return true;
        var spanMs = (latest - earliest) * 1000.0 / Stopwatch.Frequency;
        var ageMs = (nowTicks - latest) * 1000.0 / Stopwatch.Frequency;
        return spanMs <= Math.Max(1, trigger.ChordWindowMs) && ageMs <= Math.Max(50, trigger.ChordWindowMs * 2);
    }

    internal static string FormatKey(MacroInputSpec spec) =>
        $"{spec.DeviceId ?? "*"}:{spec.DeviceKind}:{spec.Code}";
}

internal sealed class MacroScheduler
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const int ProcessPowerThrottlingInfo = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;
    private const double SpinThresholdMs = 2.0;

    private readonly Lock _metricsGate = new();
    private readonly List<double> _recentErrorUs = [];

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        int processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimerEx(
        IntPtr hTimer,
        ref long lpDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        IntPtr wakeContext,
        uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    internal MacroTestRunResult Execute(
        MacroBinding binding,
        IMacroOutputSink sink,
        CancellationToken cancellationToken,
        int? seed = null)
    {
        var repeats = binding.Trigger.Mode == MacroPlaybackMode.Repeat
            ? Math.Clamp(binding.Trigger.RepeatCount, 1, 100)
            : 1;
        var jitterMs = Math.Clamp(binding.Trigger.JitterMs, 0, 50);
        var rng = jitterMs > 0 ? (seed.HasValue ? new Random(seed.Value) : Random.Shared) : null;

        var trace = new List<MacroStepTrace>(binding.Steps.Count * repeats);
        var errorsUs = new List<double>(binding.Steps.Count * repeats);

        DisableTimerResolutionThrottling();
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        var periodSet = timeBeginPeriod(1) == 0;
        var prevPriority = Thread.CurrentThread.Priority;
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            var startTicks = Stopwatch.GetTimestamp();
            var targetOffsetMs = 0.0;

            for (var round = 0; round < repeats; round++)
            {
                foreach (var step in binding.Steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var stepDelayMs = Math.Max(0.0, step.DelayMs);
                    if (stepDelayMs > 0.0 && rng is not null)
                    {
                        var delta = (rng.NextDouble() * 2.0 - 1.0) * jitterMs;
                        stepDelayMs = Math.Max(0.0, stepDelayMs + delta);
                    }

                    targetOffsetMs += stepDelayMs;
                    var targetTicks = startTicks + (long)Math.Round(targetOffsetMs * Stopwatch.Frequency / 1000.0);

                    if (stepDelayMs > 0.0)
                    {
                        WaitUntil(timer, targetTicks, cancellationToken);
                    }

                    var actualTicks = Stopwatch.GetTimestamp();
                    var actualOffsetMs = (actualTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
                    var errorUs = Math.Abs(actualOffsetMs - targetOffsetMs) * 1000.0;

                    if (step.Kind != MacroStepKind.Delay)
                    {
                        sink.Emit(step, binding.Delivery);
                    }

                    trace.Add(new MacroStepTrace
                    {
                        StepId = step.Id,
                        Kind = step.Kind,
                        TargetOffsetMs = Math.Round(targetOffsetMs, 3),
                        ActualOffsetMs = Math.Round(actualOffsetMs, 3),
                        ErrorUs = Math.Round(errorUs, 1),
                    });

                    if (stepDelayMs > 0.0)
                    {
                        errorsUs.Add(errorUs);
                        RecordSample(errorUs);
                    }
                }
            }

            var totalMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            return new MacroTestRunResult
            {
                Ok = true,
                Delivery = binding.Delivery,
                TotalMs = Math.Round(totalMs, 3),
                Latency = Summarize(errorsUs),
                Trace = trace,
            };
        }
        finally
        {
            Thread.CurrentThread.Priority = prevPriority;
            if (periodSet) timeEndPeriod(1);
            if (timer != IntPtr.Zero) CloseHandle(timer);
        }
    }

    internal MacroLatencySummary SnapshotMetrics()
    {
        lock (_metricsGate)
        {
            return Summarize(_recentErrorUs);
        }
    }

    private void RecordSample(double errorUs)
    {
        lock (_metricsGate)
        {
            _recentErrorUs.Add(errorUs);
            if (_recentErrorUs.Count > 512)
            {
                _recentErrorUs.RemoveRange(0, _recentErrorUs.Count - 512);
            }
        }
    }

    private static void DisableTimerResolutionThrottling()
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = ProcessPowerThrottlingIgnoreTimerResolution,
            StateMask = 0,
        };
        _ = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingInfo, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
    }

    private static void WaitUntil(IntPtr timer, long targetTicks, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remainingTicks = targetTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return;

            var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > SpinThresholdMs && timer != IntPtr.Zero)
            {
                var sleepMs = remainingMs - SpinThresholdMs;
                var due100Ns = -(long)(sleepMs * 10_000.0);
                if (due100Ns < 0 && SetWaitableTimerEx(timer, ref due100Ns, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                {
                    WaitForSingleObject(timer, 50);
                    continue;
                }
            }

            while (Stopwatch.GetTimestamp() < targetTicks)
            {
                if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
                Thread.SpinWait(20);
            }
            return;
        }
    }

    internal static MacroLatencySummary Summarize(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
        {
            return new MacroLatencySummary { Samples = 0, TargetUs = 1000.0 };
        }
        var sorted = samples.OrderBy(x => x).ToArray();
        return new MacroLatencySummary
        {
            Samples = sorted.Length,
            P50Us = Math.Round(Percentile(sorted, 0.50), 1),
            P95Us = Math.Round(Percentile(sorted, 0.95), 1),
            P99Us = Math.Round(Percentile(sorted, 0.99), 1),
            MaxUs = Math.Round(sorted[^1], 1),
            TargetUs = 1000.0,
        };
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        var index = Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

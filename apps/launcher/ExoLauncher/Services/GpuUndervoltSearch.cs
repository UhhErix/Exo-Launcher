namespace ExoLauncher.Services;

internal sealed record UndervoltStep(int StepIndex, string Label, GpuManualProfile Candidate);

internal sealed record UndervoltSearchResult(
    bool Completed,
    bool Cancelled,
    bool AppliedSafeProfile,
    int TestedSteps,
    int? FailedAtStepIndex,
    int AppliedStepIndex,
    GpuManualProfile FinalProfile,
    string Summary,
    string? FailureReason);

internal static class GpuUndervoltSearch
{
    internal const int BackOffSteps = 2;

    internal static IReadOnlyList<UndervoltStep> BuildSteps(
        GpuTuningCapabilities caps,
        GpuManualProfile baseProfile,
        int? stockPeakClockMhz = null)
    {
        if (caps.LockedByMaker || !caps.SupportsUndervoltSearch) return [];
        var steps = new List<UndervoltStep>();

        if (caps.Vendor == "amd")
        {
            if (caps.VoltageOffsetMv.Supported && caps.VoltageOffsetMv.Min < caps.VoltageOffsetMv.Default)
            {
                var stepSize = Math.Max(10, caps.VoltageOffsetMv.Step);
                var start = caps.VoltageOffsetMv.Default;
                var idx = 1;
                for (var mv = start - stepSize; mv >= caps.VoltageOffsetMv.Min && idx <= 12; mv -= stepSize, idx++)
                {
                    var clamped = caps.VoltageOffsetMv.Clamp(mv);
                    var candidate = baseProfile with { Mode = "manual", VoltageOffsetMv = clamped };
                    steps.Add(new(idx, $"Voltage offset {clamped} mV", candidate));
                }
                return steps;
            }
            if (caps.VoltageMv.Supported && caps.VoltageMv.Min < caps.VoltageMv.Default)
            {
                var stepSize = Math.Max(15, caps.VoltageMv.Step);
                var start = caps.VoltageMv.Default;
                var idx = 1;
                for (var mv = start - stepSize; mv >= caps.VoltageMv.Min && idx <= 12; mv -= stepSize, idx++)
                {
                    var clamped = caps.VoltageMv.Clamp(mv);
                    var candidate = baseProfile with { Mode = "manual", VoltageMv = clamped };
                    steps.Add(new(idx, $"GPU voltage {clamped} mV", candidate));
                }
                return steps;
            }
            return [];
        }

        if (caps.Vendor == "nvidia")
        {
            var offsetRange = caps.CoreClockOffsetMhz.Supported ? caps.CoreClockOffsetMhz : new GpuTuningRange(-500, 500, 15, 0, true);
            var stepSize = Math.Max(15, offsetRange.Step);
            var maxOffset = Math.Min(offsetRange.Max, 240);
            var peakClock = stockPeakClockMhz
                ?? (caps.VfCurvePoints.Count > 0 ? caps.VfCurvePoints.Max(p => p.BaseClockMhz) : (caps.ClockLockMhz.Supported ? caps.ClockLockMhz.Default : 0));

            var idx = 1;
            for (var off = stepSize; off <= maxOffset && idx <= 14; off += stepSize, idx++)
            {
                var clampedOff = offsetRange.Clamp(off);
                GpuManualProfile candidate;
                if (caps.SupportsCurveEditor && caps.VfCurvePoints.Count > 0)
                {
                    var curve = caps.VfCurvePoints.Select(pt =>
                    {
                        var targetFreq = pt.BaseClockMhz + clampedOff;
                        var cappedOffset = peakClock > 0 && targetFreq > peakClock
                            ? Math.Max(0, peakClock - pt.BaseClockMhz)
                            : clampedOff;
                        return pt with { OffsetMhz = offsetRange.Clamp(cappedOffset) };
                    }).ToList();
                    candidate = baseProfile with
                    {
                        Mode = "manual",
                        CoreClockOffsetMhz = clampedOff,
                        VfCurve = curve,
                    };
                }
                else
                {
                    candidate = baseProfile with
                    {
                        Mode = "manual",
                        CoreClockOffsetMhz = clampedOff,
                        ClockLockMhz = peakClock > 0 && caps.ClockLockMhz.Supported ? caps.ClockLockMhz.Clamp(peakClock) : null,
                    };
                }
                steps.Add(new(idx, $"V/F curve +{clampedOff} MHz undervolt", candidate));
            }
            return steps;
        }

        if (caps.Vendor == "intel" && caps.VoltageOffsetMv.Supported && caps.VoltageOffsetMv.Min < 0)
        {
            var stepSize = Math.Max(10, caps.VoltageOffsetMv.Step);
            var idx = 1;
            for (var mv = -stepSize; mv >= caps.VoltageOffsetMv.Min && idx <= 10; mv -= stepSize, idx++)
            {
                var clamped = caps.VoltageOffsetMv.Clamp(mv);
                var candidate = baseProfile with { Mode = "manual", VoltageOffsetMv = clamped };
                steps.Add(new(idx, $"Voltage offset {clamped} mV", candidate));
            }
        }

        return steps;
    }

    internal static int BackedOffStepIndex(int? failedAtStepIndex, int deepestPassedStepIndex)
    {
        if (failedAtStepIndex is { } failed)
            return Math.Max(0, failed - BackOffSteps);
        return Math.Max(0, deepestPassedStepIndex);
    }

    internal static UndervoltSearchResult Run(
        GpuTuningCapabilities caps,
        GpuManualProfile stockProfile,
        Func<GpuManualProfile, GpuApplyResult> applyProfile,
        Func<GpuApplyResult> resetToStock,
        Func<UndervoltStep, GpuStabilityResult> runStepTest,
        Action<GpuStabilityProgress>? onProgress,
        Func<bool> isCancelled,
        int? stockPeakClockMhz = null)
    {
        if (caps.LockedByMaker)
            return new(false, false, false, 0, null, 0, stockProfile, caps.LockReason ?? GpuTuningCapabilities.LockedMessage, caps.LockReason ?? GpuTuningCapabilities.LockedMessage);
        if (caps.RequiresWaiver && !caps.WaiverAccepted)
            return new(false, false, false, 0, null, 0, stockProfile, "Accept Intel's overclocking waiver first.", "Waiver not accepted.");

        var steps = BuildSteps(caps, stockProfile, stockPeakClockMhz);
        if (steps.Count == 0)
            return new(false, false, false, 0, null, 0, stockProfile, "Undervolt search is not supported on this GPU.", "Not supported.");

        var deepestPassed = 0;
        int? failedAt = null;
        string? failReason = null;
        var tested = 0;

        foreach (var step in steps)
        {
            if (isCancelled())
            {
                resetToStock();
                return new(false, true, false, tested, null, 0, stockProfile, "Undervolt search cancelled and restored to stock.", "Cancelled.");
            }

            onProgress?.Invoke(new GpuStabilityProgress(
                Running: true,
                Kind: "undervolt",
                Phase: "stepping",
                ElapsedSec: tested,
                TotalSec: steps.Count,
                Passes: tested,
                TempC: null,
                ClockMhz: null,
                ChecksumOk: true,
                CurrentStep: $"Step {step.StepIndex}/{steps.Count}: {step.Label}",
                Passed: null,
                RolledBack: false,
                FailureReason: null,
                Summary: null));

            var apply = applyProfile(step.Candidate);
            if (!apply.Ok)
            {
                failedAt = step.StepIndex;
                failReason = apply.Message ?? "Driver rejected candidate step.";
                resetToStock();
                break;
            }

            tested++;
            var test = runStepTest(step);
            if (test.Cancelled)
            {
                resetToStock();
                return new(false, true, false, tested, null, 0, stockProfile, "Undervolt search cancelled and restored to stock.", "Cancelled.");
            }

            if (!test.Passed)
            {
                failedAt = step.StepIndex;
                failReason = test.FailureReason ?? "Instability detected.";
                resetToStock();
                break;
            }

            deepestPassed = step.StepIndex;
        }

        var targetIndex = BackedOffStepIndex(failedAt, deepestPassed);
        if (targetIndex == 0)
        {
            resetToStock();
            var msg = failedAt is not null
                ? $"Instability appeared at step {failedAt} ({failReason}); backed off {BackOffSteps} steps to stock."
                : "No stable undervolt margin found; restored to stock.";
            onProgress?.Invoke(new GpuStabilityProgress(
                Running: false,
                Kind: "undervolt",
                Phase: "done",
                ElapsedSec: tested,
                TotalSec: steps.Count,
                Passes: tested,
                TempC: null,
                ClockMhz: null,
                ChecksumOk: true,
                CurrentStep: null,
                Passed: false,
                RolledBack: true,
                FailureReason: failReason,
                Summary: msg));
            return new(true, false, false, tested, failedAt, 0, stockProfile, msg, failReason);
        }

        var chosen = steps[targetIndex - 1];
        var finalApply = applyProfile(chosen.Candidate);
        if (!finalApply.Ok)
        {
            resetToStock();
            return new(false, false, false, tested, failedAt, 0, stockProfile, "Could not apply backed-off undervolt; restored to stock.", finalApply.Message);
        }

        var verify = runStepTest(chosen);
        if (!verify.Passed)
        {
            resetToStock();
            var reason = verify.FailureReason ?? "Verification failed.";
            onProgress?.Invoke(new GpuStabilityProgress(
                Running: false,
                Kind: "undervolt",
                Phase: "failed",
                ElapsedSec: tested,
                TotalSec: steps.Count,
                Passes: tested,
                TempC: verify.PeakTempC,
                ClockMhz: verify.PeakClockMhz,
                ChecksumOk: verify.ChecksumOk,
                CurrentStep: null,
                Passed: false,
                RolledBack: true,
                FailureReason: reason,
                Summary: $"Backed-off step failed verification ({reason}); restored to stock."));
            return new(false, false, false, tested, failedAt, 0, stockProfile, "Backed-off step failed verification; restored to stock.", reason);
        }

        var summary = failedAt is not null
            ? $"Stopped at step {failedAt} ({failReason}), backed off {BackOffSteps} steps to {chosen.Label}."
            : $"All {steps.Count} steps held; applied {chosen.Label}.";

        onProgress?.Invoke(new GpuStabilityProgress(
            Running: false,
            Kind: "undervolt",
            Phase: "passed",
            ElapsedSec: tested,
            TotalSec: steps.Count,
            Passes: tested,
            TempC: verify.PeakTempC,
            ClockMhz: verify.PeakClockMhz,
            ChecksumOk: true,
            CurrentStep: chosen.Label,
            Passed: true,
            RolledBack: false,
            FailureReason: null,
            Summary: summary));

        return new(true, false, true, tested, failedAt, targetIndex, chosen.Candidate, summary, failReason);
    }
}

using System.Diagnostics;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal interface IGpuComputePassRunner : IDisposable
{
    ulong RunPass();
}

internal interface IGpuSensorSampler
{
    GpuSensors? Sample();
    int HardwareErrorCount();
}

internal sealed record GpuStabilityOptions(
    TimeSpan Duration,
    double MaxTempC = 90.0,
    double MaxClockDropRatio = 0.18,
    int ClockDropConsecutiveSamples = 3,
    TimeSpan? SampleInterval = null);

internal sealed record GpuStabilityResult(
    bool Passed,
    bool Cancelled,
    bool RolledBack,
    bool ChecksumOk,
    long Passes,
    int ElapsedSec,
    double? PeakTempC,
    int? BaselineClockMhz,
    int? MinClockMhz,
    int? PeakClockMhz,
    string? FailureReason);

internal sealed class D3D11GpuComputePassRunner(uint pciVendorId, uint groups = 8192, uint iterations = 192) : IGpuComputePassRunner
{
    private readonly GpuLoad _load = new(pciVendorId, groups, iterations);
    public ulong RunPass() => _load.Pass();
    public void Dispose() => _load.Dispose();
}

internal sealed class LiveGpuSensorSampler(string vendor) : IGpuSensorSampler
{
    public GpuSensors? Sample() => vendor switch
    {
        "nvidia" => NvidiaSensors.Read(),
        "amd" => AmdAdlx.ReadSensors(),
        _ => null,
    };

    public int HardwareErrorCount() => TuneEvents.Count;
}

internal static class GpuStabilityTest
{
    internal static TimeSpan DurationFor(string? preset) => string.Equals(preset, "5m", StringComparison.OrdinalIgnoreCase)
        ? TimeSpan.FromMinutes(5)
        : TimeSpan.FromSeconds(60);

    internal static uint PciVendorIdFor(string vendor) => GpuTuningPolicy.NormalizeVendor(vendor) switch
    {
        "nvidia" => 0x10DE,
        "amd" => 0x1002,
        "intel" => 0x8086,
        _ => 0,
    };

    internal static GpuStabilityResult Run(
        Func<IGpuComputePassRunner> createRunner,
        IGpuSensorSampler sampler,
        GpuStabilityOptions options,
        Action rollback,
        Action<GpuStabilityProgress>? onProgress,
        Func<bool> isCancelled,
        string kind = "stability",
        string? stepLabel = null)
    {
        var totalSec = Math.Max(1, (int)Math.Ceiling(options.Duration.TotalSeconds));
        var sampleEvery = options.SampleInterval ?? TimeSpan.FromSeconds(1);
        var startErrors = sampler.HardwareErrorCount();
        var clock = Stopwatch.StartNew();
        long passes = 0;
        ulong? baselineHash = null;
        double? peakTemp = null;
        int? baselineClock = null;
        int? minClock = null;
        int? peakClock = null;
        var lowClockStreak = 0;
        var nextSample = TimeSpan.Zero;

        GpuStabilityResult Fail(string reason, bool checksumOk, bool cancelled = false)
        {
            var rolledBack = false;
            if (!cancelled)
            {
                try { rollback(); rolledBack = true; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("GPU stability test rollback failed: " + ex.Message); }
            }
            var elapsed = (int)Math.Round(clock.Elapsed.TotalSeconds);
            onProgress?.Invoke(new GpuStabilityProgress(
                Running: false,
                Kind: kind,
                Phase: cancelled ? "cancelled" : "failed",
                ElapsedSec: elapsed,
                TotalSec: totalSec,
                Passes: passes,
                TempC: peakTemp,
                ClockMhz: minClock ?? peakClock,
                ChecksumOk: checksumOk,
                CurrentStep: stepLabel,
                Passed: false,
                RolledBack: rolledBack,
                FailureReason: reason,
                Summary: rolledBack ? $"{reason} Rolled back to previous stable settings." : reason));
            return new GpuStabilityResult(false, cancelled, rolledBack, checksumOk, passes, elapsed, peakTemp, baselineClock, minClock, peakClock, reason);
        }

        try
        {
            using var runner = createRunner();
            while (clock.Elapsed < options.Duration)
            {
                if (isCancelled())
                    return Fail("Stability test cancelled.", checksumOk: true, cancelled: true);

                var hash = runner.RunPass();
                baselineHash ??= hash;
                if (hash != baselineHash.Value)
                    return Fail("Artifact detected (compute shader checksum mismatch).", checksumOk: false);

                passes++;
                if (clock.Elapsed < nextSample) continue;
                nextSample = clock.Elapsed + sampleEvery;

                if (sampler.HardwareErrorCount() > startErrors)
                    return Fail("Graphics driver reset or hardware error (TDR/WHEA) detected.", checksumOk: true);

                var sensors = sampler.Sample();
                var temp = sensors?.Limit is { OnHotspot: true }
                    ? sensors.HotspotC ?? sensors.TemperatureC
                    : sensors?.TemperatureC;
                if (temp is { } t)
                {
                    peakTemp = peakTemp is { } prev ? Math.Max(prev, t) : t;
                    var ceiling = sensors?.Limit?.BadC is { } badC ? Math.Min(options.MaxTempC, badC) : options.MaxTempC;
                    if (t >= ceiling)
                        return Fail($"GPU temperature reached {Math.Round(t)} °C (limit {Math.Round(ceiling)} °C).", checksumOk: true);
                }

                if (sensors?.ClockMhz is { } mhz && mhz > 0)
                {
                    baselineClock ??= mhz;
                    peakClock = peakClock is { } pk ? Math.Max(pk, mhz) : mhz;
                    minClock = minClock is { } mn ? Math.Min(mn, mhz) : mhz;
                    var reference = Math.Max(baselineClock.Value, peakClock.Value);
                    var dropRatio = reference > 0 ? (reference - mhz) / (double)reference : 0;
                    if (dropRatio > options.MaxClockDropRatio)
                    {
                        lowClockStreak++;
                        if (lowClockStreak >= Math.Max(1, options.ClockDropConsecutiveSamples))
                            return Fail($"Sustained GPU clock drop detected ({mhz} MHz vs {reference} MHz peak).", checksumOk: true);
                    }
                    else
                    {
                        lowClockStreak = 0;
                    }
                }

                onProgress?.Invoke(new GpuStabilityProgress(
                    Running: true,
                    Kind: kind,
                    Phase: "testing",
                    ElapsedSec: Math.Min(totalSec, (int)clock.Elapsed.TotalSeconds),
                    TotalSec: totalSec,
                    Passes: passes,
                    TempC: temp ?? peakTemp,
                    ClockMhz: sensors?.ClockMhz ?? peakClock,
                    ChecksumOk: true,
                    CurrentStep: stepLabel ?? $"Checking compute stability ({Math.Min(totalSec, (int)clock.Elapsed.TotalSeconds)}/{totalSec} s)",
                    Passed: null,
                    RolledBack: false,
                    FailureReason: null,
                    Summary: null));
            }

            if (sampler.HardwareErrorCount() > startErrors)
                return Fail("Graphics driver reset or hardware error (TDR/WHEA) detected.", checksumOk: true);

            var doneSec = Math.Max(1, (int)Math.Round(clock.Elapsed.TotalSeconds));
            onProgress?.Invoke(new GpuStabilityProgress(
                Running: false,
                Kind: kind,
                Phase: "passed",
                ElapsedSec: doneSec,
                TotalSec: totalSec,
                Passes: passes,
                TempC: peakTemp,
                ClockMhz: peakClock,
                ChecksumOk: true,
                CurrentStep: stepLabel,
                Passed: true,
                RolledBack: false,
                FailureReason: null,
                Summary: $"Passed {passes} compute passes ({doneSec} s) with zero artifacts."));
            return new GpuStabilityResult(true, false, false, true, passes, doneSec, peakTemp, baselineClock, minClock, peakClock, null);
        }
        catch (GpuLoad.Lost lost)
        {
            return Fail(string.IsNullOrWhiteSpace(lost.Message) ? "Graphics driver reset (TDR)." : lost.Message, checksumOk: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail($"Stability test stopped: {ex.Message}", checksumOk: true);
        }
    }
}

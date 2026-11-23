using System.Globalization;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

internal static class CapLabAbTester
{
    internal const int BlockDurationSeconds = 5;
    internal const int BlockCount = 4;
    internal const int TotalDurationSeconds = BlockDurationSeconds * BlockCount;
    internal const double DefaultNoiseThresholdPercent = 3.0;
    internal const int MinimumFramesPerArm = 30;

    internal sealed record ArmMetrics(
        int CapFps,
        long Frames,
        double AverageFps,
        double Low1Fps,
        double Low01Fps,
        double AverageMs,
        double FrameTimeVarianceMs2,
        double FrameTimeStdDevMs);

    internal sealed record AbVerdict(
        string Winner,
        double DeltaPercent,
        double NoiseThresholdPercent,
        string Verdict,
        ArmMetrics ArmA,
        ArmMetrics ArmB);

    internal static string ArmForSecond(int elapsedSeconds)
    {
        if (elapsedSeconds < 0) return "A";
        var block = elapsedSeconds / BlockDurationSeconds;
        return block % 2 == 0 ? "A" : "B";
    }

    internal static ArmMetrics MeasureArm(int capFps, IReadOnlyList<double> framesMs)
    {
        var valid = new List<double>(framesMs.Count);
        for (var i = 0; i < framesMs.Count; i++)
        {
            var ms = framesMs[i];
            if (ms > 0 && ms < FrameStats.NotAFrameMs && !double.IsNaN(ms) && !double.IsInfinity(ms))
                valid.Add(ms);
        }
        var stats = FrameStats.Of(valid);
        var (variance, stdDev) = FrameCapPlanner.ComputeVariance(valid);
        return new ArmMetrics(
            capFps,
            stats.Frames,
            Math.Round(stats.AverageFps, 1),
            Math.Round(stats.Low1Fps, 1),
            Math.Round(stats.Low01Fps, 1),
            Math.Round(stats.AverageMs, 2),
            Math.Round(variance, 3),
            Math.Round(stdDev, 3));
    }

    internal static AbVerdict Evaluate(
        int capAFps,
        int capBFps,
        IReadOnlyList<double> framesAMs,
        IReadOnlyList<double> framesBMs,
        double noiseThresholdPercent = DefaultNoiseThresholdPercent)
    {
        var threshold = Math.Max(0.5, noiseThresholdPercent);
        var armA = MeasureArm(capAFps, framesAMs);
        var armB = MeasureArm(capBFps, framesBMs);

        if (armA.Frames < MinimumFramesPerArm || armB.Frames < MinimumFramesPerArm)
        {
            return new AbVerdict(
                "none",
                0,
                threshold,
                "No measurable difference",
                armA,
                armB);
        }

        var low1Delta = RelativeImprovement(armB.Low1Fps, armA.Low1Fps);
        var low01Delta = RelativeImprovement(armB.Low01Fps, armA.Low01Fps);
        var jitterDelta = JitterImprovement(armA.FrameTimeStdDevMs, armB.FrameTimeStdDevMs);
        var composite = 0.45 * low1Delta + 0.25 * low01Delta + 0.30 * jitterDelta;

        if (Math.Abs(composite) < threshold)
        {
            return new AbVerdict(
                "none",
                Math.Round(Math.Abs(composite), 1),
                threshold,
                "No measurable difference",
                armA,
                armB);
        }

        var rounded = Math.Round(Math.Abs(composite), 1);
        if (composite > 0)
        {
            return new AbVerdict(
                "b",
                rounded,
                threshold,
                string.Create(CultureInfo.InvariantCulture, $"B is smoother by {rounded:0.#}%"),
                armA,
                armB);
        }

        return new AbVerdict(
            "a",
            rounded,
            threshold,
            string.Create(CultureInfo.InvariantCulture, $"A is smoother by {rounded:0.#}%"),
            armA,
            armB);
    }

    private static double RelativeImprovement(double candidate, double baseline)
    {
        var denom = Math.Max(Math.Min(candidate, baseline), 1.0);
        return Math.Clamp((candidate - baseline) / denom * 100.0, -100.0, 100.0);
    }

    private static double JitterImprovement(double stdDevA, double stdDevB)
    {
        if (stdDevA < 0.15 && stdDevB < 0.15) return 0;
        var denom = Math.Max(Math.Max(stdDevA, stdDevB), 0.15);
        return Math.Clamp((stdDevA - stdDevB) / denom * 100.0, -100.0, 100.0);
    }
}

using System.Globalization;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

internal static class FrameCapPlanner
{
    internal const int DefaultVrrFloorHz = 48;
    internal const int MinCapFps = 24;
    internal const int MeasuredMinimumFrames = 60;

    internal const string TierOff = "off";
    internal const string TierSmooth = "smooth";
    internal const string TierBalanced = "balanced";
    internal const string TierMax = "max";
    internal const string TierRefresh = "refresh";
    internal const string TierUncapped = "uncapped";
    internal const string TierCustom = "custom";

    internal sealed record CapTier(
        string Id,
        string Label,
        int? CapFps,
        int? OutputFps,
        double PredictedVarianceMs2,
        double PredictedStdDevMs,
        double PredictedDipsPerMinute,
        bool Measured,
        string Summary);

    internal sealed record CapSuggestion(
        string Tier,
        int? CapFps,
        string Reason);

    internal sealed record FrameCapPlan(
        double RefreshHz,
        bool VrrEnabled,
        bool InVrrRange,
        int VrrFloorHz,
        int FgMultiplier,
        int OutputCeilingFps,
        int MaxBaseCapFps,
        bool SupportsLowLatency,
        string? LowLatencyLabel,
        bool HasMeasuredFrames,
        int MeasuredFrameCount,
        IReadOnlyList<CapTier> Tiers,
        IReadOnlyList<int> RefreshLockedCaps,
        CapTier? UncappedOption);

    internal static int VrrCap(double refreshHz, int vrrFloorHz = DefaultVrrFloorHz)
    {
        if (!(refreshHz > 0) || double.IsNaN(refreshHz) || double.IsInfinity(refreshHz)) return 0;
        var raw = (int)Math.Floor(refreshHz - (refreshHz * refreshHz) / 3600.0);
        if (vrrFloorHz > 0) raw = Math.Max(vrrFloorHz, raw);
        var maxAllowed = Math.Max(1, (int)Math.Floor(refreshHz));
        return Math.Min(raw, maxAllowed);
    }

    internal static int BaseCapForFrameGen(int outputCapFps, int fgMultiplier)
    {
        if (outputCapFps <= 0) return 0;
        var mult = Math.Clamp(fgMultiplier, 1, 4);
        if (mult == 1) return outputCapFps;
        return Math.Max(1, outputCapFps / mult);
    }

    internal static IReadOnlyList<int> RefreshLockedCaps(double refreshHz, int fgMultiplier = 1)
    {
        if (!(refreshHz > 0) || double.IsNaN(refreshHz) || double.IsInfinity(refreshHz)) return [];
        var whole = Math.Max(1, (int)Math.Round(refreshHz));
        var mult = Math.Clamp(fgMultiplier, 1, 4);
        var caps = new List<int>();
        for (var div = 1; div <= 4; div++)
        {
            if (whole % div != 0) continue;
            var output = whole / div;
            if (output < 30 && div > 1) continue;
            var baseCap = BaseCapForFrameGen(output, mult);
            if (baseCap >= MinCapFps && !caps.Contains(baseCap)) caps.Add(baseCap);
        }
        if (caps.Count == 0)
        {
            var fallback = BaseCapForFrameGen(whole, mult);
            if (fallback > 0) caps.Add(fallback);
        }
        return caps;
    }

    internal static FrameCapPlan Plan(
        double refreshHz,
        bool vrrEnabled,
        int vrrFloorHz = DefaultVrrFloorHz,
        int fgMultiplier = 1,
        bool supportsLowLatency = true,
        string? lowLatencyLabel = null,
        IReadOnlyList<double>? sessionFrameTimesMs = null)
    {
        var effectiveRefresh = refreshHz > 0 && !double.IsNaN(refreshHz) && !double.IsInfinity(refreshHz) ? refreshHz : 60.0;
        var floor = vrrFloorHz > 0 ? vrrFloorHz : DefaultVrrFloorHz;
        var mult = Math.Clamp(fgMultiplier, 1, 4);
        var inVrrRange = vrrEnabled && effectiveRefresh >= floor;

        var outputCeiling = inVrrRange
            ? VrrCap(effectiveRefresh, floor)
            : Math.Max(MinCapFps, (int)Math.Round(effectiveRefresh));

        var maxBase = BaseCapForFrameGen(outputCeiling, mult);
        var validFrames = FilterFrames(sessionFrameTimesMs);
        var measured = validFrames.Count >= MeasuredMinimumFrames;

        int smoothCap;
        int balancedCap;
        int maxCap = maxBase;
        double smoothVar, smoothStd, smoothDips;
        double balancedVar, balancedStd, balancedDips;
        double maxVar, maxStd, maxDips;

        if (measured)
        {
            var sorted = validFrames.ToArray();
            Array.Sort(sorted);
            var p99Ms = Percentile(sorted, 0.99);
            var p95Ms = Percentile(sorted, 0.95);
            var minTier = Math.Min(MinCapFps, maxCap);
            var rawSmooth = p99Ms > 0 ? (int)Math.Floor(1000.0 / p99Ms) : maxCap;
            var rawBalanced = p95Ms > 0 ? (int)Math.Floor(1000.0 / p95Ms) : maxCap;
            smoothCap = Math.Clamp(rawSmooth, minTier, maxCap);
            balancedCap = Math.Clamp(rawBalanced, smoothCap, maxCap);
            (smoothVar, smoothStd, smoothDips) = PredictVariance(validFrames, smoothCap);
            (balancedVar, balancedStd, balancedDips) = PredictVariance(validFrames, balancedCap);
            (maxVar, maxStd, maxDips) = PredictVariance(validFrames, maxCap);
        }
        else
        {
            var minTier = Math.Min(MinCapFps, maxCap);
            smoothCap = Math.Clamp((int)Math.Floor(maxCap * 0.75), minTier, maxCap);
            balancedCap = Math.Clamp((int)Math.Floor(maxCap * 0.88), smoothCap, maxCap);
            (smoothVar, smoothStd, smoothDips) = (0.25, 0.50, 0.2);
            (balancedVar, balancedStd, balancedDips) = (0.90, 0.95, 2.5);
            (maxVar, maxStd, maxDips) = (2.25, 1.50, 10.0);
        }

        var smoothOut = smoothCap * mult;
        var balancedOut = balancedCap * mult;
        var maxOut = maxCap * mult;

        var tiers = new List<CapTier>
        {
            new(
                TierSmooth,
                "Smooth",
                smoothCap,
                smoothOut,
                Math.Round(smoothVar, 2),
                Math.Round(smoothStd, 2),
                Math.Round(smoothDips, 1),
                measured,
                FormatTierSummary("1% low target", smoothCap, smoothOut, mult, smoothStd, measured)),
            new(
                TierBalanced,
                "Balanced",
                balancedCap,
                balancedOut,
                Math.Round(balancedVar, 2),
                Math.Round(balancedStd, 2),
                Math.Round(balancedDips, 1),
                measured,
                FormatTierSummary("5% low target", balancedCap, balancedOut, mult, balancedStd, measured)),
            new(
                TierMax,
                inVrrRange ? "Max (VRR)" : "Max (Refresh)",
                maxCap,
                maxOut,
                Math.Round(maxVar, 2),
                Math.Round(maxStd, 2),
                Math.Round(maxDips, 1),
                measured,
                FormatTierSummary(inVrrRange ? "VRR ceiling" : "Refresh lock", maxCap, maxOut, mult, maxStd, measured)),
        };

        var refreshCaps = inVrrRange ? [] : RefreshLockedCaps(effectiveRefresh, mult);
        CapTier? uncappedOption = null;
        if (!inVrrRange)
        {
            var llName = string.IsNullOrWhiteSpace(lowLatencyLabel) ? "Reflex / Anti-Lag" : lowLatencyLabel.Trim();
            var label = supportsLowLatency ? $"Uncapped ({llName})" : "Uncapped";
            var summary = supportsLowLatency
                ? $"Run uncapped with {llName} for lowest input lag"
                : "No frame cap";
            double uncappedVar = measured ? Math.Round(ComputeVariance(validFrames).VarianceMs2, 2) : 3.5;
            double uncappedStd = measured ? Math.Round(ComputeVariance(validFrames).StdDevMs, 2) : 1.87;
            uncappedOption = new CapTier(TierUncapped, label, null, null, uncappedVar, uncappedStd, 0, measured, summary);
        }

        return new FrameCapPlan(
            Math.Round(effectiveRefresh, 2),
            vrrEnabled,
            inVrrRange,
            floor,
            mult,
            outputCeiling,
            maxBase,
            supportsLowLatency,
            lowLatencyLabel,
            measured,
            validFrames.Count,
            tiers,
            refreshCaps,
            uncappedOption);
    }

    internal static CapSuggestion SuggestTier(
        FrameCapPlan plan,
        CapLabStutterReport? stutter = null,
        bool isCompetitive = false)
    {
        if (!plan.InVrrRange && isCompetitive && plan.SupportsLowLatency && plan.FgMultiplier == 1)
        {
            var ll = string.IsNullOrWhiteSpace(plan.LowLatencyLabel) ? "low latency mode" : plan.LowLatencyLabel;
            return new CapSuggestion(TierUncapped, null, $"VRR is off in a competitive game; uncapped with {ll} keeps input lag lowest.");
        }

        var smooth = plan.Tiers.FirstOrDefault(t => t.Id == TierSmooth);
        var balanced = plan.Tiers.FirstOrDefault(t => t.Id == TierBalanced);
        var max = plan.Tiers.FirstOrDefault(t => t.Id == TierMax);

        if (stutter is { TotalSpikes: > 0, TopCause: CapLabStutterAttribution.CauseFrameCap } && balanced is not null)
        {
            return new CapSuggestion(
                TierBalanced,
                balanced.CapFps,
                $"Recent stutters came from cap pacing; Balanced ({balanced.CapFps} fps) steadies frame delivery.");
        }

        if (plan.HasMeasuredFrames && smooth is not null && balanced is not null && max is not null)
        {
            var maxFps = max.CapFps ?? 1;
            var balFps = balanced.CapFps ?? maxFps;
            var smoothFps = smooth.CapFps ?? balFps;

            if (max.PredictedStdDevMs > 2.5
                && balanced.PredictedStdDevMs <= max.PredictedStdDevMs * 0.70
                && balFps >= maxFps * 0.75)
            {
                if (balanced.PredictedStdDevMs > 2.0
                    && smooth.PredictedStdDevMs <= balanced.PredictedStdDevMs * 0.70
                    && smoothFps >= balFps * 0.80)
                {
                    return new CapSuggestion(
                        TierSmooth,
                        smooth.CapFps,
                        $"Measured frame times swing heavily; Smooth ({smooth.CapFps} fps) cuts predicted jitter to ±{smooth.PredictedStdDevMs:0.0} ms.");
                }

                return new CapSuggestion(
                    TierBalanced,
                    balanced.CapFps,
                    $"Measured dips near the ceiling; Balanced ({balanced.CapFps} fps) cuts predicted jitter to ±{balanced.PredictedStdDevMs:0.0} ms.");
            }
        }

        if (plan.FgMultiplier > 1 && max is not null)
        {
            return new CapSuggestion(
                TierMax,
                max.CapFps,
                $"Frame generation {plan.FgMultiplier}x is active; cap base frames at {max.CapFps} fps so {max.OutputFps} fps output stays inside VRR.");
        }

        if (plan.InVrrRange && max is not null)
        {
            return new CapSuggestion(
                TierMax,
                max.CapFps,
                $"VRR is active at {plan.RefreshHz:0} Hz; {max.CapFps} fps keeps frames below the V-Sync ceiling.");
        }

        var locked = plan.RefreshLockedCaps.FirstOrDefault();
        if (locked > 0)
        {
            return new CapSuggestion(
                TierMax,
                locked,
                $"VRR is off at {plan.RefreshHz:0} Hz; {locked} fps locks frames cleanly to refresh.");
        }

        return new CapSuggestion(TierOff, null, "No frame cap needed.");
    }

    internal static (double VarianceMs2, double StdDevMs, double DipsPerMinute) PredictVariance(
        IReadOnlyList<double> validFramesMs,
        int capFps)
    {
        if (validFramesMs.Count == 0 || capFps <= 0) return (0, 0, 0);
        var targetMs = 1000.0 / capFps;
        var clamped = new double[validFramesMs.Count];
        double totalMs = 0;
        var dips = 0;
        var dipLine = Math.Max(targetMs * 1.10, targetMs + 1.0);
        for (var i = 0; i < validFramesMs.Count; i++)
        {
            var ms = Math.Max(validFramesMs[i], targetMs);
            clamped[i] = ms;
            totalMs += ms;
            if (ms > dipLine) dips++;
        }
        var (variance, stdDev) = ComputeVariance(clamped);
        var minutes = totalMs / 60_000.0;
        var dipsPerMin = minutes > 0 ? dips / minutes : 0;
        return (variance, stdDev, dipsPerMin);
    }

    internal static (double VarianceMs2, double StdDevMs) ComputeVariance(IReadOnlyList<double> values)
    {
        if (values.Count <= 1) return (0, 0);
        double sum = 0;
        for (var i = 0; i < values.Count; i++) sum += values[i];
        var mean = sum / values.Count;
        double sq = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var diff = values[i] - mean;
            sq += diff * diff;
        }
        var variance = sq / values.Count;
        return (variance, Math.Sqrt(variance));
    }

    private static List<double> FilterFrames(IReadOnlyList<double>? raw)
    {
        var list = new List<double>();
        if (raw is null) return list;
        for (var i = 0; i < raw.Count; i++)
        {
            var ms = raw[i];
            if (ms > 0 && ms < FrameStats.NotAFrameMs && !double.IsNaN(ms) && !double.IsInfinity(ms))
                list.Add(ms);
        }
        return list;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }

    private static string FormatTierSummary(string kind, int baseCap, int outputCap, int mult, double stdDevMs, bool measured)
    {
        var fpsText = mult > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{baseCap} base ({outputCap} out)")
            : string.Create(CultureInfo.InvariantCulture, $"{baseCap} fps");
        var varText = measured
            ? string.Create(CultureInfo.InvariantCulture, $"±{stdDevMs:0.0} ms predicted")
            : string.Create(CultureInfo.InvariantCulture, $"~±{stdDevMs:0.0} ms est.");
        return $"{kind} · {fpsText} · {varText}";
    }
}

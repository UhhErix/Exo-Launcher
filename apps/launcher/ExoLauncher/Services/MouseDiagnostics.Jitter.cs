using System.Buffers.Binary;
using System.Globalization;

namespace ExoLauncher.Services;

internal sealed partial class MouseDiagnostics
{
    private static readonly int[] StandardRates = [125, 250, 500, 1000, 2000, 4000, 8000];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal sealed record HistogramBucket(string Id, string Label, string RangeMs, int Count, double Percent, string Kind);

    internal sealed record JitterReport(
        int PollingRateHz,
        double NominalMs,
        int TotalReports,
        int CheckedGaps,
        double MedianMs,
        double SpreadMs,
        double P95Ms,
        double P99Ms,
        double P999Ms,
        double LongestMs,
        int LateCount,
        int DroppedCount,
        int BunchedCount,
        int StabilityScore,
        bool OnScreenRefresh,
        int? MouseCpu,
        string Cause,
        string CauseDetail,
        IReadOnlyList<HistogramBucket> Histogram);

    internal sealed record JitterSnapshot(
        int PollingRateHz,
        int StabilityScore,
        double P99Ms,
        double P999Ms,
        double LongestMs,
        int LateCount,
        int DroppedCount,
        int BunchedCount,
        int? MouseCpu);

    internal static void ParseRawInputBuffer(ReadOnlySpan<byte> buffer, uint count, long timestamp, Action<nint, int, int, long> onMouse)
    {
        var offset = 0;
        for (uint i = 0; i < count && offset + 24 <= buffer.Length; i++)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 4)..]);
            if (size < 24 || offset + size > buffer.Length) break;
            if (type == 0 && size >= 48)
            {
                var device = (nint)BinaryPrimitives.ReadInt64LittleEndian(buffer[(offset + 8)..]);
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 24)..]);
                if (device != 0 && (flags & 1) == 0)
                {
                    var dx = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 36)..]);
                    var dy = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 40)..]);
                    onMouse(device, dx, dy, timestamp);
                }
            }
            var aligned = (size + 7) & ~7;
            if (aligned <= 0) break;
            offset += aligned;
        }
    }

    internal static int DetectPollingRate(IReadOnlyList<MouseCore.Report> reports, long frequency)
    {
        if (reports.Count < 8 || frequency <= 0) return 0;
        var still = frequency / 20.0;
        var moving = new List<long>(reports.Count);
        var fallback = new List<long>(reports.Count);
        for (var i = 1; i < reports.Count; i++)
        {
            var dt = reports[i].At - reports[i - 1].At;
            if (dt <= 0 || dt >= still) continue;
            fallback.Add(dt);
            if (Math.Abs(reports[i].Dx) + Math.Abs(reports[i].Dy) >= 2) moving.Add(dt);
        }
        var sample = moving.Count >= 16 ? moving : fallback;
        if (sample.Count == 0) return 0;
        sample.Sort();
        var nonTiny = sample.Where(dt => dt * 1000.0 / frequency >= 0.06).ToList();
        var pool = nonTiny.Count >= sample.Count / 2 && nonTiny.Count > 0 ? nonTiny : sample;
        var medianTicks = pool[pool.Count / 2];
        var medianHz = (double)frequency / Math.Max(1, medianTicks);
        return StandardRates.MinBy(r => Math.Abs(Math.Log2(r / medianHz)));
    }

    internal static JitterReport AnalyzeJitter(
        IReadOnlyList<MouseCore.Report> reports,
        long frequency,
        double refreshHz,
        MouseCore.Work? work = null,
        Helpers.MouseDiagTrace.Result? etw = null)
    {
        var mouseCpu = etw?.MouseControllerCore ?? work?.Cpu;
        if (reports.Count < 64 || frequency <= 0)
        {
            return new JitterReport(0, 0, reports.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, mouseCpu,
                "insufficient", "Move your mouse continuously in circles during the 10-second test.", []);
        }

        var rate = DetectPollingRate(reports, frequency);
        if (rate <= 0)
        {
            return new JitterReport(0, 0, reports.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, mouseCpu,
                "insufficient", "Not enough steady motion to detect polling rate.", []);
        }

        var slot = (double)frequency / rate;
        var nominalMs = 1000.0 / rate;
        var still = frequency / 20.0;
        var lateFloor = Math.Max(slot * 1.5, slot + frequency * 0.00025);
        var bunchedCeiling = slot * 0.5;

        var intervals = new List<long>(reports.Count);
        var lateAt = new List<long>();
        var bunched = 0;
        var late = 0;
        var dropped = 0;
        var bucketCounts = new int[7];

        for (var k = 8; k + 1 < reports.Count; k++)
        {
            var dt = reports[k + 1].At - reports[k].At;
            var span = reports[k].At - reports[k - 8].At;
            if (dt <= 0 || dt >= still || span <= 0) continue;
            long counts = 0;
            for (var j = k - 7; j <= k; j++) counts += Math.Max(Math.Abs(reports[j].Dx), Math.Abs(reports[j].Dy));
            if (counts * slot < 2.0 * span) continue;

            intervals.Add(dt);
            var ratio = dt / slot;
            if (ratio < 0.5)
            {
                bucketCounts[0]++;
                bunched++;
            }
            else if (ratio < 0.85) bucketCounts[1]++;
            else if (ratio <= 1.15) bucketCounts[2]++;
            else if (ratio < 1.5) bucketCounts[3]++;
            else if (ratio < 2.0) bucketCounts[4]++;
            else if (ratio < 4.0) bucketCounts[5]++;
            else bucketCounts[6]++;

            if (dt >= lateFloor)
            {
                late++;
                lateAt.Add(reports[k].At);
                if (!CaughtUp(reports, k + 1, dt, slot))
                {
                    dropped += Math.Max(1, (int)Math.Round(dt / slot) - 1);
                }
            }
        }

        if (intervals.Count < 32)
        {
            return new JitterReport(rate, Math.Round(nominalMs, 3), reports.Count, intervals.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, mouseCpu,
                "insufficient", "Move your mouse faster in smooth circles so every polling slot fills.", []);
        }

        intervals.Sort();
        double ToMs(long ticks) => ticks * 1000.0 / frequency;
        long Percentile(double p) => intervals[Math.Clamp((int)Math.Ceiling(intervals.Count * p) - 1, 0, intervals.Count - 1)];

        var medianMs = Math.Round(ToMs(intervals[intervals.Count / 2]), 3);
        var q1Ms = ToMs(intervals[intervals.Count / 4]);
        var q3Ms = ToMs(intervals[intervals.Count * 3 / 4]);
        var spreadMs = Math.Round(Math.Max(0, q3Ms - q1Ms), 3);
        var p95Ms = Math.Round(ToMs(Percentile(0.95)), 3);
        var p99Ms = Math.Round(ToMs(Percentile(0.99)), 3);
        var p999Ms = Math.Round(ToMs(Percentile(0.999)), 3);
        var longestMs = Math.Round(ToMs(intervals[^1]), 3);
        var onRefresh = MouseCore.OnBeat(lateAt, frequency, refreshHz, slot);

        var histogram = BuildHistogram(bucketCounts, intervals.Count, nominalMs);
        var score = ComputeStabilityScore(intervals.Count, bucketCounts[2], late, dropped, bunched, p99Ms, p999Ms, nominalMs);
        var (cause, causeDetail) = ClassifyCause(score, late, dropped, bunched, intervals.Count, nominalMs, p999Ms, mouseCpu, work, etw);

        return new JitterReport(
            rate,
            Math.Round(nominalMs, 3),
            reports.Count,
            intervals.Count,
            medianMs,
            spreadMs,
            p95Ms,
            p99Ms,
            p999Ms,
            longestMs,
            late,
            dropped,
            bunched,
            score,
            onRefresh,
            mouseCpu,
            cause,
            causeDetail,
            histogram);
    }

    internal static bool HasProblem(JitterReport report)
    {
        if (report.Cause is "insufficient" or "steady") return false;
        if (report.CheckedGaps < 32) return false;
        return report.StabilityScore < 90
            || report.LateCount >= Math.Max(3, report.CheckedGaps / 1000)
            || report.DroppedCount >= 3
            || report.BunchedCount >= Math.Max(10, report.CheckedGaps / 50);
    }

    internal static JitterSnapshot SnapshotOf(JitterReport report) => new(
        report.PollingRateHz,
        report.StabilityScore,
        report.P99Ms,
        report.P999Ms,
        report.LongestMs,
        report.LateCount,
        report.DroppedCount,
        report.BunchedCount,
        report.MouseCpu);

    internal static bool IsImproved(JitterSnapshot before, JitterSnapshot after)
    {
        if (after.StabilityScore >= before.StabilityScore + 5) return true;
        if (before.LateCount > 0 && after.LateCount <= before.LateCount * 0.75) return true;
        if (before.DroppedCount > 0 && after.DroppedCount <= before.DroppedCount * 0.75) return true;
        if (before.P999Ms > 0 && after.P999Ms <= before.P999Ms * 0.8 && after.LateCount <= before.LateCount) return true;
        return false;
    }

    private static bool CaughtUp(IReadOnlyList<MouseCore.Report> reports, int from, long interval, double slot)
    {
        var skipped = Math.Max(1, (int)Math.Round(interval / slot) - 1);
        var until = reports[from].At + (skipped + 2) * slot;
        var came = 0;
        for (var i = from + 1; i < reports.Count && reports[i].At <= until; i++) came++;
        return came >= skipped + 2 + Math.Max(1, skipped / 2);
    }

    private static List<HistogramBucket> BuildHistogram(int[] counts, int total, double nominalMs)
    {
        string Fmt(double ms) => ms < 1 ? ms.ToString("0.00", Inv) : ms.ToString("0.0", Inv);
        double Pct(int c) => total == 0 ? 0 : Math.Round(100.0 * c / total, 1);
        return
        [
            new("bunched", "Bunched", $"< {Fmt(nominalMs * 0.5)} ms", counts[0], Pct(counts[0]), "warn"),
            new("early", "Early", $"{Fmt(nominalMs * 0.5)}–{Fmt(nominalMs * 0.85)} ms", counts[1], Pct(counts[1]), "ok"),
            new("nominal", "On time", $"{Fmt(nominalMs * 0.85)}–{Fmt(nominalMs * 1.15)} ms", counts[2], Pct(counts[2]), "ok"),
            new("slight", "Slight jitter", $"{Fmt(nominalMs * 1.15)}–{Fmt(nominalMs * 1.5)} ms", counts[3], Pct(counts[3]), "warn"),
            new("late", "Late", $"{Fmt(nominalMs * 1.5)}–{Fmt(nominalMs * 2.0)} ms", counts[4], Pct(counts[4]), "bad"),
            new("very_late", "Very late", $"{Fmt(nominalMs * 2.0)}–{Fmt(nominalMs * 4.0)} ms", counts[5], Pct(counts[5]), "bad"),
            new("dropout", "Dropout", $">= {Fmt(nominalMs * 4.0)} ms", counts[6], Pct(counts[6]), "bad"),
        ];
    }

    private static int ComputeStabilityScore(int total, int onTime, int late, int dropped, int bunched, double p99Ms, double p999Ms, double nominalMs)
    {
        if (total <= 0 || nominalMs <= 0) return 0;
        var onTimeFraction = (double)onTime / total;
        var lateFraction = (double)late / total;
        var droppedFraction = (double)dropped / total;
        var bunchedFraction = (double)bunched / total;
        var p99Excess = Math.Max(0, p99Ms / nominalMs - 1.15);
        var p999Excess = Math.Max(0, p999Ms / nominalMs - 1.5);

        var penalty = 0.0;
        penalty += Math.Max(0, 1.0 - onTimeFraction) * 35.0;
        penalty += Math.Min(35.0, lateFraction * 600.0);
        penalty += Math.Min(25.0, droppedFraction * 500.0);
        penalty += Math.Min(20.0, bunchedFraction * 120.0);
        penalty += Math.Min(20.0, p99Excess * 10.0);
        penalty += Math.Min(15.0, p999Excess * 4.0);
        return Math.Clamp((int)Math.Round(100.0 - penalty), 0, 100);
    }

    private static (string Cause, string Detail) ClassifyCause(
        int score,
        int late,
        int dropped,
        int bunched,
        int total,
        double nominalMs,
        double p999Ms,
        int? mouseCpu,
        MouseCore.Work? work,
        Helpers.MouseDiagTrace.Result? etw)
    {
        var realLate = late >= Math.Max(3, total / 1000);
        var realBunched = bunched >= Math.Max(10, total / 50);
        var realDropped = dropped >= 3;
        if (score >= 90 && !realLate && !realDropped && !realBunched)
        {
            return ("steady", "Reports arrive steadily near nominal spacing.");
        }

        var pcBusy = false;
        string? busyDriver = null;
        double busyMaxUs = 0;

        if (etw is not null)
        {
            var mouseCoreStats = mouseCpu is { } mc ? etw.Cores.FirstOrDefault(c => c.Core == mc) : null;
            var relevantMaxDpc = mouseCoreStats is not null ? Math.Max(mouseCoreStats.DpcMaxUs, mouseCoreStats.IsrMaxUs) : etw.MaxDpcUs;
            var topDriver = etw.TopDrivers.FirstOrDefault();
            if (relevantMaxDpc >= 500 || etw.MaxDpcUs >= 1000 || (mouseCoreStats is not null && (mouseCoreStats.DpcTotalUs + mouseCoreStats.IsrTotalUs) / Math.Max(1, etw.Seconds * 10_000.0) >= 1.5))
            {
                pcBusy = true;
                busyDriver = topDriver?.Driver;
                busyMaxUs = Math.Round(Math.Max(relevantMaxDpc, topDriver?.DpcMaxUs ?? 0), 0);
            }
        }
        else if (work is not null && work.Dpc.Length > 0)
        {
            var coreDpc = mouseCpu is { } c && c >= 0 && c < work.Dpc.Length ? work.Dpc[c] : 0;
            var maxDpc = work.Dpc.Max();
            if (coreDpc >= 0.5 || maxDpc >= 2.0) pcBusy = true;
        }

        var usbUneven = realBunched || (realDropped && p999Ms >= nominalMs * 2.5);

        if (pcBusy && usbUneven)
        {
            var who = busyDriver is not null ? $" ({busyDriver}, up to {busyMaxUs.ToString("0", Inv)} µs)" : "";
            return ("both", $"Both uneven USB delivery and high PC DPC/ISR load{who} showed up during the test.");
        }
        if (pcBusy)
        {
            var who = busyDriver is not null ? $" ({busyDriver} reached {busyMaxUs.ToString("0", Inv)} µs)" : "";
            return ("pc_busy", $"The PC was busy with driver DPC/ISR work{who} while reports arrived late.");
        }
        return ("usb_uneven", "PC DPC load stayed low, pointing to uneven delivery from the mouse, receiver, or USB hub.");
    }
}

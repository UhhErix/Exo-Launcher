using System.Text;

namespace ExoLauncher.Services;

/// <summary>
/// The Controller Lab: what a DualSense or DualSense Edge says about itself and how well it reports, measured, never
/// changed. It reads the controller's input reports (what every game reads) and, over USB only, its firmware report
/// (0x20); it sends nothing. Over Bluetooth a feature read switches the pad to its full reports until it reconnects, and
/// an older game that reads the short ones then loses it, so the lab only listens there.
///
/// Layouts follow Linux's hid-playstation.c and SDL's hidapi PS5 driver (data offsets; a USB report's data starts at
/// byte 1, a Bluetooth 0x31 report's at byte 2). The ways this could go wrong, covered by ControllerLabTests:
/// - Bluetooth's short report read with the USB layout (sticks and triggers from the wrong bytes);
/// - repeated reports counted as new ones (the pad's own sensor clock tells them apart);
/// - a polling rate read from a pause (the median interval, not the mean);
/// - a stick's center off by the half step every 8-bit stick has (127.5 is the center, not 128);
/// - circularity measured on a stick that was barely moved (too few directions covered says so instead);
/// - the firmware version read from the wrong bytes, or compared with another model's.
/// </summary>
internal static partial class ControllerLab
{
    internal const int SonyVendor = 0x054C, DualSense = 0x0CE6, DualSenseEdge = 0x0DF2;

    internal static bool Supported(int? vendor, int? product) => vendor == SonyVendor && product is DualSense or DualSenseEdge;

    /// <summary>
    /// One input report, as the lab uses it. Sticks and triggers 0..255; Sensor: the pad's clock (0.33 µs), when it sends one.
    /// Extras: an Edge's own buttons as its report has them (<see cref="FnLeft"/> and the rest), 0 on anything else.
    /// </summary>
    internal readonly record struct State(byte LX, byte LY, byte RX, byte RY, byte L2, byte R2, uint? Sensor, int? Profile, int? StopL, int? StopR, int? Battery, bool Charging, int Extras = 0, int Buttons = 0);

    /// <summary>An Edge's Fn and back buttons in its report's data byte 9 (dualshock-tools' input map, SDL's hidapi PS5 driver).</summary>
    internal const int FnLeft = 0x10, FnRight = 0x20, BackLeft = 0x40, BackRight = 0x80;

    /// <summary>A report's fields. <paramref name="usb"/>: the link (a Bluetooth pad sends 0x31 once in full mode, 0x01 short until then).</summary>
    internal static State? Parse(ReadOnlySpan<byte> report, bool usb, bool edge)
    {
        if (report.Length == 0) return null;
        // Bluetooth's short report (0x01): sticks, then buttons, then the triggers at bytes 8 and 9; no clock.
        if (!usb && report[0] == 0x01)
            return report.Length < 10 ? null : new State(report[1], report[2], report[3], report[4], report[8], report[9], null, null, null, null, null, false, 0, SonyButtons(report[5], report[6], report[7] & 0x03));
        var start = usb && report[0] == 0x01 ? 1 : !usb && report[0] == 0x31 ? 2 : -1;
        if (start < 0 || report.Length < start + 53) return null;
        var data = report[start..];
        int? profile = null, stopL = null, stopR = null;
        var extras = 0;
        if (edge)
        {
            extras = data[9] & (FnLeft | FnRight | BackLeft | BackRight);
            profile = data[48] >> 4 is var p and >= 1 and <= 4 ? p : null;
            stopL = (data[49] >> 4) & 0x3;
            stopR = (data[49] >> 6) & 0x3;
        }
        var level = data[52] & 0x0F;
        var charge = data[52] >> 4;
        return new State(data[0], data[1], data[2], data[3], data[4], data[5], BitConverter.ToUInt32(data[27..31]), profile, stopL, stopR,
            level <= 10 ? Math.Min(100, level * 10 + 5) : null, charge is 1 or 2, extras, SonyButtons(data[7], data[8], data[9]));
    }

    /// <summary>
    /// A DualSense's or DualShock 4's buttons as bits: its hat as four (up, right, down, left), its faces, then the next two bytes
    /// (hid-playstation.c). The hat is a number in the byte, so its pressing a diagonal doesn't read as a new button.
    /// </summary>
    internal static int SonyButtons(int first, int second, int third)
    {
        var hat = (first & 0x0F) switch { 0 => 0b0001, 1 => 0b0011, 2 => 0b0010, 3 => 0b0110, 4 => 0b0100, 5 => 0b1100, 6 => 0b1000, 7 => 0b1001, _ => 0 };
        return hat | (first & 0xF0) | second << 8 | third << 16;
    }

    /// <summary>How the controller reports, from when each report arrived (Stopwatch ticks) and the pad's clock in it.</summary>
    /// <param name="Rate">Reports a second, from the median interval.</param>
    /// <param name="Fresh">New readings a second: reports whose clock moved (what a game gets).</param>
    /// <param name="P99">The 99th percentile interval, ms.</param>
    /// <param name="Jitter">The intervals' standard deviation, ms.</param>
    /// <param name="Repeats">Share of reports that repeated the one before (the clock didn't move), %; null without a clock.</param>
    /// <param name="Batched">Share of intervals under a quarter of the mean, %: reports that came together.</param>
    /// <param name="Changes">It sends only when something changes (a slowest 1% of 20 ms or more, the pad's idle gaps): Rate is a lower bound and the slowest gaps aren't stutter.</param>
    internal sealed record Polling(double Rate, double? Fresh, double P99, double Jitter, double? Repeats, int Reports, double Batched = 0, bool Changes = false);

    internal static Polling? Measure(IReadOnlyList<(long At, uint? Sensor)> reports, long frequency)
    {
        if (reports.Count < 20 || frequency <= 0) return null;
        var intervals = new double[reports.Count - 1];
        for (var i = 1; i < reports.Count; i++) intervals[i - 1] = (reports[i].At - reports[i - 1].At) * 1000.0 / frequency;
        var sorted = intervals.Order().ToArray();
        var median = sorted[sorted.Length / 2];
        if (median <= 0) return null;
        var mean = intervals.Average();
        var jitter = Math.Sqrt(intervals.Sum(x => (x - mean) * (x - mean)) / intervals.Length);
        var p99 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.99) - 1)];
        double? repeats = null, fresh = null;
        if (reports.All(r => r.Sensor is not null))
        {
            var same = 0;
            for (var i = 1; i < reports.Count; i++) if (reports[i].Sensor == reports[i - 1].Sensor) same++;
            repeats = Math.Round(100.0 * same / (reports.Count - 1), 1);
            var seconds = (reports[^1].At - reports[0].At) / (double)frequency;
            fresh = seconds > 0 ? Math.Round((reports.Count - 1 - same) / seconds) : null;
        }
        // Against the mean, since in batches the median is one of the tiny gaps; for the same reason, then the mean says the rate.
        var batched = 100.0 * intervals.Count(x => x < mean / 4) / intervals.Length;
        var rate = batched >= 50 && p99 < 20 ? 1000 / mean : 1000 / median;
        return new Polling(Math.Round(rate), fresh, Math.Round(p99, 2), Math.Round(jitter, 2), repeats, reports.Count, Math.Round(batched, 1), p99 >= 20);
    }

    /// <summary>
    /// One stick at rest: how far its center is from the true center (% of half its travel, per axis), how much it wanders (steps),
    /// and how far it crept in one direction over the test (% of half its travel, the first third against the last): drift, as against noise.
    /// </summary>
    internal sealed record Rest(double OffsetX, double OffsetY, int Noise, double Creep = 0);

    internal static Rest? AtRest(IReadOnlyList<(byte X, byte Y)> samples)
    {
        if (samples.Count < 20) return null;
        const double center = 127.5;
        var x = samples.Average(s => s.X);
        var y = samples.Average(s => s.Y);
        var noise = Math.Max(samples.Max(s => s.X) - samples.Min(s => s.X), samples.Max(s => s.Y) - samples.Min(s => s.Y));
        var third = samples.Count / 3;
        double Moved(Func<(byte X, byte Y), byte> axis) => Math.Abs(samples.Skip(samples.Count - third).Average(s => axis(s)) - samples.Take(third).Average(s => axis(s))) / center * 100;
        return new Rest(Math.Round((x - center) / center * 100, 1), Math.Round((y - center) / center * 100, 1), noise, Math.Round(Math.Max(Moved(s => s.X), Moved(s => s.Y)), 1));
    }

    /// <summary>
    /// One stick rolled around its edge: how far it reaches in the eight directions (% of full travel; the shortest is
    /// what limits a game), how round its edge is (circularity error: the edge's mean distance from its own average, %
    /// of it), and how much of the circle the samples covered (under 0.75 the numbers aren't trusted).
    /// </summary>
    internal sealed record Edge(double Reach, double Circularity, double Covered);

    internal static Edge? Rolled(IReadOnlyList<(byte X, byte Y)> samples)
    {
        const int bins = 36;
        var edge = new double[bins];
        foreach (var (sx, sy) in samples)
        {
            var x = (sx - 127.5) / 127.5;
            var y = (sy - 127.5) / 127.5;
            var r = Math.Sqrt(x * x + y * y);
            if (r < 0.5) continue;
            var bin = (int)((Math.Atan2(y, x) + Math.PI) / (2 * Math.PI) * bins) % bins;
            edge[bin] = Math.Max(edge[bin], Math.Min(r, 1.5));
        }
        var hit = edge.Where(r => r > 0).ToArray();
        var covered = hit.Length / (double)bins;
        if (hit.Length < 8) return null;
        // The eight directions: the bins at every 45° (each bin is 10°, so every 4.5 bins: take the nearest).
        var directions = Enumerable.Range(0, 8).Select(i => edge[(int)Math.Round(i * bins / 8.0) % bins]).Where(r => r > 0).ToArray();
        var reach = directions.Length > 0 ? Math.Min(1, directions.Min()) : Math.Min(1, hit.Min());
        var average = hit.Average();
        var circularity = hit.Average(r => Math.Abs(r - average)) / average * 100;
        return new Edge(Math.Round(reach * 100, 1), Math.Round(circularity, 1), Math.Round(covered, 2));
    }

    /// <summary>
    /// One trigger pulled slowly through its travel: how far it went (0..100%), and how many different values it sent. That is a lower bound of its
    /// resolution, since a quick pull skips values, and it never counts past the 256 Exo reads. Null when it was barely pulled.
    /// </summary>
    internal sealed record Sweep(int Low, int High, int Steps, bool Full);

    internal static Sweep? Swept(IReadOnlyList<byte> samples)
    {
        if (samples.Count < 20) return null;
        int low = samples.Min(), high = samples.Max();
        if (high - low < 50) return null;
        return new Sweep((int)Math.Round(low / 2.55), (int)Math.Round(high / 2.55), samples.Distinct().Count(), low <= 5 && high >= 250);
    }

    /// <summary>A button's test: real presses, and bounces (a release and a press again, or the reverse, faster than a finger, under <see cref="BounceMs"/>) with the shortest in ms.</summary>
    internal sealed record Press(string Name, int Presses, int Bounces, double? Shortest);

    /// <summary>The buttons pressed in a test. ReportMs: the median gap between reports; a bounce shorter than that can't be seen, so a count of bounces is a lower bound.</summary>
    internal sealed record Chatter(IReadOnlyList<Press> Buttons, double ReportMs);

    internal const double BounceMs = 15;

    /// <summary>Reads every button's changes across a test's states; null with too few reports to say anything.</summary>
    internal static Chatter? Bounces(IReadOnlyList<(long At, int Buttons)> states, long frequency, Func<int, string> name)
    {
        if (states.Count < 20 || frequency <= 0) return null;
        var gaps = new double[states.Count - 1];
        for (var i = 1; i < states.Count; i++) gaps[i - 1] = (states[i].At - states[i - 1].At) * 1000.0 / frequency;
        Array.Sort(gaps);
        var list = new List<Press>();
        for (var bit = 0; bit < 31; bit++)
        {
            var mask = 1 << bit;
            long? last = null;
            var was = (states[0].Buttons & mask) != 0;
            int presses = 0, bounces = 0;
            double? shortest = null;
            foreach (var (at, buttons) in states.Skip(1))
            {
                var held = (buttons & mask) != 0;
                if (held == was) continue;
                var since = last is { } t ? (at - t) * 1000.0 / frequency : double.MaxValue;
                if (since < BounceMs)
                {
                    bounces++;
                    shortest = Math.Min(shortest ?? double.MaxValue, since);
                }
                else if (held) presses++;
                last = at;
                was = held;
            }
            if (presses + bounces > 0) list.Add(new Press(name(bit), presses, (bounces + 1) / 2, shortest is { } s ? Math.Round(s, 1) : null));
        }
        return new Chatter(list, Math.Round(gaps[gaps.Length / 2], 2));
    }

    /// <summary>What a bit of <see cref="State.Buttons"/> is called on this family's controllers.</summary>
    internal static string ButtonName(Family family, int bit) => family switch
    {
        Family.DualSense or Family.DualShock4 => bit < SonyNames.Length ? SonyNames[bit] : $"Button {bit + 1}",
        Family.Xbox => bit < XboxNames.Length && XboxNames[bit].Length > 0 ? XboxNames[bit] : $"Button {bit + 1}",
        _ => $"Button {bit + 1}",
    };

    private static readonly string[] SonyNames = ["Up", "Right", "Down", "Left", "Square", "Cross", "Circle", "Triangle", "L1", "R1", "L2", "R2", "Create", "Options", "L3", "R3", "PS", "Touchpad", "Mute"];
    private static readonly string[] XboxNames = ["Up", "Down", "Left", "Right", "Menu", "View", "Left stick", "Right stick", "LB", "RB", "", "", "A", "B", "X", "Y"];

    /// <summary>The firmware report (0x20): when it was built, its update version (what Sony's updater compares) and its series (which list to compare with).</summary>
    internal sealed record Firmware(string Built, ushort Update, byte Series);

    internal static Firmware? FirmwareOf(ReadOnlySpan<byte> report)
    {
        if (report.Length < 46 || report[0] != 0x20) return null;
        var built = Encoding.ASCII.GetString(report[1..12]).Trim('\0', ' ');
        return new Firmware(built, BitConverter.ToUInt16(report[44..46]), report[22]);
    }

    /// <summary>The key in Sony's list (fwupdater.dl.playstation.net/fwupdater/info.json) for a series: FwUpdate0044LatestVersion for the Edge.</summary>
    internal static string LatestKey(byte series) => $"FwUpdate00{series:X2}LatestVersion";

    /// <summary>A version as Sony's app shows it: 0x0217 is 2.17.</summary>
    internal static string VersionText(ushort update) => $"{update >> 8:X}.{update & 0xFF:X2}";

    /// <summary>Sony's "0x0217".</summary>
    internal static ushort? ParseVersion(string? text) =>
        text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
}

using System.Buffers.Binary;
using static ExoLauncher.Services.InterruptMode;

namespace ExoLauncher.Services;

/// <summary>
/// Which driver held the mouse's reports up, from Windows' own recording of its drivers' work (<see cref="Recorder"/>):
/// the interrupts and DPCs on the CPU that takes the mouse's input, set against each late report's gap. A driver is named
/// only when its own time on that CPU (what interrupted it taken off, and only the part inside the gap) explains at
/// least half of how late that report was, and only when that happens more than once. A driver that merely ran during
/// a gap is never named. It says how Windows handled the reports, not input lag.
/// </summary>
internal sealed partial class MouseCore
{
    /// <summary>One stretch of driver work on the CPU, on the Stopwatch's clock.</summary>
    internal readonly record struct Run(long From, long To, ulong Routine);

    /// <summary>A driver in memory; File is its name in the recording.</summary>
    internal sealed record Image(ulong Base, ulong Size, string File);

    /// <summary>A late report's gap, and the mouse's slot.</summary>
    internal readonly record struct Late(long From, long To, double Slot);

    /// <summary>What Windows recorded. <paramref name="OnClock"/>: its times are the Stopwatch's and cover the late reports; <paramref name="Lost"/>: events it dropped.</summary>
    internal sealed record Recording(IReadOnlyList<Run> Runs, IReadOnlyList<Image> Images, bool OnClock, long Lost);

    /// <summary>
    /// A driver the recording names. Gaps: the late reports it explains; Own and Gap: its own time in the worst of them and that gap (ms).
    /// Product and Version: from its file. Origin: where its device's interrupts are aimed by whom, when it drives a PCI device.
    /// Path: part of the mouse's own way in (USB, HID, or Windows' driver framework, under which Windows doesn't say whose work it was).
    /// </summary>
    internal sealed record Named(string File, int Gaps, double Own, double Gap, string? Product = null, string? Version = null, string? Origin = null, bool Path = false);

    /// <summary>What the recording says: of Late late reports, how many a driver's own time explains, and who. Problem: why it names nobody.</summary>
    internal sealed record Drivers(int Late, int Explained, IReadOnlyList<Named> Names, int? Cpu, string? Problem);

    private const double OwnShare = 0.5;
    private static readonly HashSet<string> MousePath = new(StringComparer.OrdinalIgnoreCase)
    {
        "usbxhci.sys", "ucx01000.sys", "usbhub3.sys", "usbhub.sys", "usbport.sys", "hidusb.sys", "hidclass.sys", "hidparse.sys", "mouhid.sys", "mouclass.sys", "wdf01000.sys",
    };

    private volatile Drivers? _drivers;

    /// <summary>What this session's last recording said.</summary>
    internal Drivers? LastDrivers => _drivers;

    /// <summary>The test's late gaps, in order, each a report's gap the test judged and found late.</summary>
    internal static List<Late> LateGaps(IReadOnlyList<Report> reports, long frequency)
    {
        if (reports.Count < 64 || frequency <= 0) return [];
        var slot = (double)frequency / Rate(reports, frequency);
        var late = Math.Max(slot * 1.5, slot + frequency * 0.00025);
        var gaps = new List<Late>();
        foreach (var k in JudgedGaps(reports, frequency, slot))
            if (reports[k + 1].At - reports[k].At >= late) gaps.Add(new Late(reports[k].At, reports[k + 1].At, slot));
        return gaps;
    }

    /// <summary>
    /// Each driver's own time inside one stretch: work on one CPU nests (an interrupt inside a DPC, a DPC inside a threaded
    /// one), so what interrupted a run comes off it, and only what falls inside the stretch counts.
    /// </summary>
    internal static Dictionary<string, long> OwnTime(IEnumerable<Run> runs, long from, long to, Func<ulong, string?> file)
    {
        var own = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var open = new List<(long To, long Inside, ulong Routine)>();
        long Clip(long a, long b) => Math.Max(0, Math.Min(b, to) - Math.Max(a, from));
        void Close(int at)
        {
            var (_, inside, routine) = open[at];
            open.RemoveAt(at);
            if (file(routine) is { } name) own[name] = own.GetValueOrDefault(name) + Math.Max(0, inside);
        }
        foreach (var run in runs.Where(r => r.To > from && r.From < to).OrderBy(r => r.From).ThenByDescending(r => r.To))
        {
            while (open.Count > 0 && open[^1].To <= run.From) Close(open.Count - 1);
            var clipped = Clip(run.From, run.To);
            if (open.Count > 0)
            {
                var broken = open[^1];
                broken.Inside -= clipped;
                open[^1] = broken;
            }
            open.Add((run.To, clipped, run.Routine));
        }
        while (open.Count > 0) Close(open.Count - 1);
        return own;
    }

    /// <summary>The driver at an address, by its file's name; null where Windows named nothing there.</summary>
    internal static string? FileAt(IReadOnlyList<Image> sorted, ulong address)
    {
        int low = 0, high = sorted.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var image = sorted[mid];
            if (address < image.Base) high = mid - 1;
            else if (address >= image.Base + image.Size) low = mid + 1;
            else return image.File[(image.File.LastIndexOf('\\') + 1)..];
        }
        return null;
    }

    /// <summary>
    /// Sets each late gap against the runs on its CPU. A gap's driver is the one with the most own time there, if that is
    /// at least half of how late the report was and at least 50 µs. It takes two such gaps, and one in twenty of the late
    /// reports, to name a driver.
    /// </summary>
    internal static (int Explained, List<Named> Names) Explain(IReadOnlyList<Late> gaps, IReadOnlyList<Run> runs, IReadOnlyList<Image> images, long frequency)
    {
        var sorted = images.OrderBy(i => i.Base).ToList();
        var ordered = runs.OrderBy(r => r.From).ToList();
        var counts = new Dictionary<string, (int Gaps, long Own, long Gap)>(StringComparer.OrdinalIgnoreCase);
        var longest = runs.Count == 0 ? 0 : runs.Max(r => r.To - r.From);
        var explained = 0;
        foreach (var gap in gaps)
        {
            var first = FirstFrom(ordered, gap.From - longest);
            var near = new List<Run>();
            for (var i = first; i < ordered.Count && ordered[i].From < gap.To; i++) near.Add(ordered[i]);
            var own = OwnTime(near, gap.From, gap.To, address => FileAt(sorted, address));
            if (own.Count == 0) continue;
            var top = own.MaxBy(o => o.Value);
            var late = gap.To - gap.From - gap.Slot;
            if (top.Value < Math.Max(late * OwnShare, frequency * 0.00005)) continue;
            explained++;
            var seen = counts.GetValueOrDefault(top.Key);
            counts[top.Key] = (seen.Gaps + 1, Math.Max(seen.Own, top.Value), top.Value > seen.Own ? gap.To - gap.From : seen.Gap);
        }
        double Ms(long ticks) => Math.Round(ticks * 1000.0 / frequency, 2);
        var enough = Math.Max(2, gaps.Count / 20);
        var names = counts.Where(c => c.Value.Gaps >= enough).OrderByDescending(c => c.Value.Gaps).ThenByDescending(c => c.Value.Own)
            .Select(c => new Named(c.Key, c.Value.Gaps, Ms(c.Value.Own), Ms(c.Value.Gap), Path: MousePath.Contains(c.Key))).ToList();
        return (names.Sum(n => n.Gaps), names);
    }

    private static int FirstFrom(List<Run> ordered, long from)
    {
        int low = 0, high = ordered.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (ordered[mid].From < from) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    /// <summary>
    /// A test recorded with Windows' permission: judged as any test (<see cref="Measure"/>), with what the recording says beside it. A driver of
    /// another's that the recording names on the mouse's CPU is evidence enough to offer a quiet one, wherever that CPU is.
    /// </summary>
    internal Drivers Name(Sample sample, Recording? recording, Func<string, (string? Product, string? Version)> describe)
    {
        var result = Measure(sample);
        var gaps = LateGaps(sample.Reports, sample.Frequency);
        var cpu = result.Test.Cpu;
        Drivers Say(string problem) => _drivers = new Drivers(gaps.Count, 0, [], cpu, problem);
        if (result.Verdict == Verdict.Retry) return Say(result.Detail);
        if (recording is null) return Say("Windows didn't record its drivers. Another program may be recording them, or anti-cheat may block it.");
        if (gaps.Count < 3) return Say("Too few reports came late during the recording to name a driver.");
        if (cpu is null) return Say("Exo couldn't tell which CPU takes your mouse's input, so it names no driver.");
        if (!recording.OnClock) return Say("Exo couldn't line the recording up with your mouse's reports, so it names no driver.");
        if (recording.Lost > 0) return Say("Windows dropped part of the recording, so Exo names no driver.");

        var (explained, names) = Explain(gaps, recording.Runs, recording.Images, sample.Frequency);
        var list = ListDevices();
        var saved = Load();
        var described = names.Select(named =>
        {
            var (product, version) = describe(named.File);
            var stem = Path.GetFileNameWithoutExtension(named.File);
            var device = list.FirstOrDefault(d => string.Equals(d.Service, stem, StringComparison.OrdinalIgnoreCase));
            return named with { Product = product, Version = version, Origin = device is null ? null : Origin(device, saved) };
        }).ToList();
        _drivers = new Drivers(gaps.Count, explained, described, cpu, null);
        if (result.Verdict == Verdict.Fine && described.Any(d => !d.Path)) _last = Judge(sample, blamed: true);
        return _drivers;
    }

    /// <summary>Who aimed a device's interrupts: Windows, Exo, or another tool.</summary>
    private static string Origin(Device d, Saved? saved) =>
        saved is { Reverted: false } && Same(saved.Id, d.Id) ? "Interrupts on the CPU Exo set"
        : Taken(d) ? "Interrupts set by another tool"
        : "Interrupts as Windows set them";

    /// <summary>
    /// What the recording holds that Exo needs: interrupts (opcode 67) and DPCs (66 threaded, 68, 69 timer) on the one CPU that
    /// overlap a late gap, and every kernel driver image. Fed by <see cref="Recorder"/>; pure, so a recording can be faked.
    /// </summary>
    internal sealed class RecordingReader(int cpu, IReadOnlyList<Late> gaps)
    {
        /// <summary>The kernel's PerfInfo and Image_Load event classes.</summary>
        internal static readonly Guid PerfInfo = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc"), ImageLoad = new("2cb15d1d-5fc1-11d2-abe1-00a0c911f518");

        private readonly List<Run> _runs = [];
        private readonly Dictionary<ulong, Image> _images = [];
        private long _first = long.MaxValue, _last = long.MinValue;

        internal void Take(in Guid provider, byte opcode, int processor, long stamp, ReadOnlySpan<byte> data)
        {
            if (provider == PerfInfo)
            {
                if (opcode is not (66 or 67 or 68 or 69) || data.Length < 16) return;
                var from = BinaryPrimitives.ReadInt64LittleEndian(data);
                if (from <= 0 || stamp < from) return;
                _first = Math.Min(_first, from);
                _last = Math.Max(_last, stamp);
                if (processor == cpu && Overlaps(from, stamp)) _runs.Add(new Run(from, stamp, BinaryPrimitives.ReadUInt64LittleEndian(data[8..])));
            }
            else if (provider == ImageLoad && opcode is 3 or 4 or 10 && data.Length > 58)
            {
                var address = BinaryPrimitives.ReadUInt64LittleEndian(data);
                if (address < 0xFFFF_8000_0000_0000) return; // a program's, not a driver's
                var name = System.Text.Encoding.Unicode.GetString(data[56..]);
                var end = name.IndexOf('\0');
                if (end >= 0) name = name[..end];
                if (name.Length > 0) _images[address] = new Image(address, BinaryPrimitives.ReadUInt64LittleEndian(data[8..]), name);
            }
        }

        private bool Overlaps(long from, long to)
        {
            int low = 0, high = gaps.Count;
            while (low < high)
            {
                var mid = (low + high) / 2;
                if (gaps[mid].To <= from) low = mid + 1;
                else high = mid;
            }
            return low < gaps.Count && gaps[low].From < to;
        }

        /// <summary><paramref name="clock"/>: the file says its times are the Stopwatch's; they must also cover every late gap.</summary>
        internal Recording Finish(bool clock, long lost) =>
            new(_runs, [.. _images.Values], clock && gaps.Count > 0 && _first <= gaps[0].From && _last >= gaps[^1].To, lost);
    }
}

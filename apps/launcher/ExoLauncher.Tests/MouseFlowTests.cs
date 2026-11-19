using ExoLauncher.Services;
using static ExoLauncher.Services.MouseCore;

namespace ExoLauncher.Tests;

/// <summary>
/// How steadily a mouse's reports arrive, and the ways that could mislead silently:
/// - a steady mouse reads as uneven or batched, or a mouse handed over in lumps reads as steady;
/// - slots the mouse skipped because it moved slowly count against how full its slots are;
/// - a late report whose skipped slots came right after reads as missing, or a truly missing one as made up;
/// - a few gaps across a whole test read as one hiccup, or one hiccup as a pattern;
/// - Exo's own cleanup holding its reader offers a fix for gaps Exo itself caused.
/// </summary>
public sealed class MouseFlowTests
{
    private const long F = 10_000_000;

    /// <summary>Reports each given number of ticks after the one before, moving fast enough to be judged.</summary>
    private static List<Report> From(IEnumerable<long> gaps, long start = F)
    {
        var at = start;
        var list = new List<Report>();
        foreach (var gap in gaps)
        {
            at += gap;
            list.Add(new Report(at, 8, 0));
        }
        return list;
    }

    private static Flow Read(List<Report> reports) => Analyze(reports, F, 0).Flow!;

    [Fact]
    public void A_steady_mouse_is_steady_and_fills_its_slots()
    {
        var flow = Read(From(Enumerable.Repeat(F / 1000, 5000)));
        Assert.Equal(1.0, flow.Median, 3);
        Assert.Equal(0, flow.Spread, 3);
        Assert.Equal(0, flow.Batched);
        Assert.Equal(1, flow.Batch);
        Assert.Equal(100, flow.Filled);
    }

    [Fact]
    public void Reports_handed_over_in_lumps_read_as_batches()
    {
        // 8 kHz, its eight reports of each millisecond arriving together at the end of it.
        var gaps = Enumerable.Range(0, 8000).Select(i => i % 8 == 7 ? F / 1000 - 7 * 100 : 100L);
        var flow = Read(From(gaps));
        Assert.True(flow.Batched > 80, $"batched {flow.Batched}");
        Assert.Equal(8, flow.Batch);
    }

    [Fact]
    public void Uneven_gaps_widen_the_spread()
    {
        var rng = new Random(7);
        var flow = Read(From(Enumerable.Range(0, 5000).Select(_ => F / 1000 + rng.NextInt64(-3000, 3000))));
        Assert.True(flow.Spread > 0.1, $"spread {flow.Spread}");
    }

    [Fact]
    public void Skipped_slots_under_a_fast_hand_leave_them_short_of_full()
    {
        // One slot in ten never comes.
        var flow = Read(From(Enumerable.Range(0, 5000).Select(i => i % 10 == 0 ? 2 * F / 1000 : F / 1000)));
        Assert.InRange(flow.Filled, 88, 93);
    }

    [Fact]
    public void A_late_report_that_catches_up_is_made_up_and_one_that_doesnt_is_not()
    {
        // Three slots late, then its three skipped reports close together: all there, only late.
        var late = new List<long>();
        for (var run = 0; run < 20; run++)
        {
            late.AddRange(Enumerable.Repeat(F / 1000, 200));
            late.Add(4 * F / 1000);
            late.AddRange(Enumerable.Repeat(F / 4000, 4));
        }
        late.AddRange(Enumerable.Repeat(F / 1000, 20)); // the last one's next slots
        var made = Analyze(From(late), F, 0);
        Assert.Equal(20, made.Missed);
        Assert.Equal(20, made.Flow!.MadeUp);

        // Three slots late, then the steady pace: those three never came.
        var gone = new List<long>();
        for (var run = 0; run < 20; run++)
        {
            gone.AddRange(Enumerable.Repeat(F / 1000, 200));
            gone.Add(4 * F / 1000);
        }
        var lost = Analyze(From(gone), F, 0);
        Assert.Equal(20, lost.Missed);
        Assert.Equal(0, lost.Flow!.MadeUp);
    }

    [Fact]
    public void Most_late_reports_in_one_second_are_a_stretch_and_spread_ones_are_not()
    {
        var spread = Enumerable.Range(0, 8000).Select(i => i % 1000 == 500 ? 3 * F / 1000 : F / 1000);
        Assert.False(Read(From(spread)).Stretch);
        var hiccup = Enumerable.Range(0, 8000).Select(i => i is >= 4000 and < 4400 && i % 50 == 0 ? 3 * F / 1000 : F / 1000);
        Assert.True(Read(From(hiccup)).Stretch);
    }

    [Fact]
    public void Too_few_late_reports_are_no_stretch()
    {
        var few = Enumerable.Range(0, 3000).Select(i => i is 1000 or 1010 or 1020 ? 3 * F / 1000 : F / 1000);
        Assert.False(Read(From(few)).Stretch);
    }

    [Fact]
    public void Exos_own_cleanup_during_the_test_offers_nothing_and_asks_again()
    {
        var folder = Directory.CreateTempSubdirectory("exo-mouseflow-").FullName;
        try
        {
            var core = new MouseCore(new NoDevices(), new NoWriter(), Path.Combine(folder, "b.json"), () => DateTimeOffset.UtcNow.AddHours(-1), () => DateTimeOffset.UtcNow);
            var late = Enumerable.Range(0, 8000).Select(i => i % 200 == 0 ? 3 * F / 1000 : F / 1000);
            var sample = new Sample(From(late), "HID\\X", F, 0, [], new Topology([], [], 8), PauseMs: 12);
            var result = core.Judge(sample);
            Assert.Equal(Verdict.Retry, result.Verdict);
            Assert.Contains("cleanup", result.Detail);
            // The same read with no cleanup is judged as usual, and says it can't set this PC's CPUs.
            Assert.NotEqual(Verdict.Retry, core.Judge(sample with { PauseMs = 1 }).Verdict);
        }
        finally
        {
            DeletionGate.DeleteDirectory(folder, recursive: true);
        }
    }

    private sealed class NoDevices : InterruptMode.IDevices
    {
        public IReadOnlyList<InterruptMode.Device> List() => [];
        public IReadOnlyList<string> BootDisks() => [];
        public string? Parent(string id) => null;
    }

    private sealed class NoWriter : IWriter
    {
        public string? Write(string id, int? policy, byte[]? mask) => null;
    }
}

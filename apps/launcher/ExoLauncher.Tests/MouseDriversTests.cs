using System.Buffers.Binary;
using System.Text;
using ExoLauncher.Services;
using static ExoLauncher.Services.MouseCore;

namespace ExoLauncher.Tests;

/// <summary>
/// Naming the driver on the mouse's CPU. Tested on made-up recordings: no real recording can run in a test (it needs
/// administrator rights). The ways it could go wrong silently:
/// - a driver that merely ran during a gap is named (too little own time, once only, or outside the gap);
/// - a DPC's own time counts the interrupt that broke into it, so the wrong driver takes the blame;
/// - an address falls in the wrong driver, or a program's image is taken for a driver's;
/// - work on another CPU counts, or a run that only touches a gap counts whole;
/// - a recording on another clock, one that doesn't cover the test or one Windows dropped events from still names someone;
/// - a stray character in the user's folder reaches the administrator's command line;
/// - USB's own work reads as someone else's fault.
/// </summary>
public sealed class MouseDriversTests
{
    private const long F = 10_000_000;
    private const long Ms = F / 1000;
    private const ulong Gpu = 0xFFFF_F800_1000_0000, Net = 0xFFFF_F800_2000_0000, Usb = 0xFFFF_F800_3000_0000;
    private static readonly Image[] Images =
    [
        new(Gpu, 0x100000, @"\SystemRoot\System32\drivers\nvlddmkm.sys"),
        new(Net, 0x100000, @"\SystemRoot\system32\drivers\rt640x64.sys"),
        new(Usb, 0x100000, @"\SystemRoot\System32\drivers\USBXHCI.SYS"),
    ];

    [Fact]
    public void A_drivers_own_time_explaining_most_of_a_gap_names_it()
    {
        var gaps = Gaps(10);
        var runs = gaps.Select(g => new Run(g.From + Ms / 10, g.From + 3 * Ms, Gpu + 16)).ToList();
        var (explained, names) = Explain(gaps, runs, Images, F);
        Assert.Equal(10, explained);
        var named = Assert.Single(names);
        Assert.Equal("nvlddmkm.sys", named.File);
        Assert.Equal(10, named.Gaps);
        Assert.Equal(2.9, named.Own, 2);
        Assert.False(named.Path);
    }

    [Fact]
    public void A_driver_that_only_ran_during_a_gap_is_not_named()
    {
        var gaps = Gaps(10);
        var brief = gaps.Select(g => new Run(g.From + Ms, g.From + Ms + Ms / 5, Gpu)).ToList(); // 0.2 ms of a 3 ms lateness
        var (explained, names) = Explain(gaps, brief, Images, F);
        Assert.Equal(0, explained);
        Assert.Empty(names);
    }

    [Fact]
    public void One_gap_alone_names_nobody()
    {
        var gaps = Gaps(10);
        var (_, names) = Explain(gaps, [new Run(gaps[0].From, gaps[0].To, Gpu)], Images, F);
        Assert.Empty(names);
    }

    [Fact]
    public void A_driver_needs_one_in_twenty_of_the_late_reports()
    {
        var gaps = Gaps(100);
        var (_, few) = Explain(gaps, gaps.Take(4).Select(g => new Run(g.From, g.To, Gpu)).ToList(), Images, F);
        Assert.Empty(few);
        var (_, enough) = Explain(gaps, gaps.Take(5).Select(g => new Run(g.From, g.To, Gpu)).ToList(), Images, F);
        Assert.Single(enough);
    }

    [Fact]
    public void Time_outside_the_gap_does_not_count()
    {
        var gaps = Gaps(10);
        // A long run that ends just inside each gap.
        var runs = gaps.Select(g => new Run(g.From - 50 * Ms, g.From + Ms / 10, Gpu)).ToList();
        Assert.Empty(Explain(gaps, runs, Images, F).Names);
    }

    [Fact]
    public void What_interrupts_a_run_comes_off_its_own_time()
    {
        var own = OwnTime([new Run(0, 10 * Ms, Gpu), new Run(2 * Ms, 8 * Ms, Net)], 0, 10 * Ms, Name);
        Assert.Equal(6 * Ms, own["rt640x64.sys"]);
        Assert.Equal(4 * Ms, own["nvlddmkm.sys"]);
    }

    [Fact]
    public void A_nested_driver_with_the_most_own_time_is_the_one_named_not_the_one_it_broke_into()
    {
        var gaps = Gaps(10);
        var runs = gaps.SelectMany(g => new[] { new Run(g.From, g.To, Gpu), new Run(g.From + Ms / 2, g.To - Ms / 2, Net) }).ToList();
        Assert.Equal("rt640x64.sys", Explain(gaps, runs, Images, F).Names.Single().File);
    }

    [Fact]
    public void Addresses_land_in_their_own_driver_and_nowhere_else()
    {
        var sorted = Images.OrderBy(i => i.Base).ToList();
        Assert.Equal("nvlddmkm.sys", FileAt(sorted, Gpu));
        Assert.Equal("nvlddmkm.sys", FileAt(sorted, Gpu + 0xFFFFF));
        Assert.Null(FileAt(sorted, Gpu + 0x100000));
        Assert.Null(FileAt(sorted, 0x1000));
    }

    [Fact]
    public void Usb_work_is_marked_as_the_mouses_own_path()
    {
        var gaps = Gaps(10);
        var named = Explain(gaps, gaps.Select(g => new Run(g.From, g.To, Usb)).ToList(), Images, F).Names.Single();
        Assert.True(named.Path);
    }

    [Fact]
    public void The_reader_keeps_only_this_cpus_runs_that_touch_a_gap()
    {
        var gaps = Gaps(3);
        var reader = new RecordingReader(4, gaps);
        var g = gaps[1];
        reader.Take(RecordingReader.PerfInfo, 68, 4, g.From + Ms, Dpc(g.From, Gpu));          // on it
        reader.Take(RecordingReader.PerfInfo, 67, 4, g.From + 2 * Ms, Dpc(g.From + Ms, Net)); // an interrupt, on it
        reader.Take(RecordingReader.PerfInfo, 68, 5, g.From + Ms, Dpc(g.From, Gpu));          // another CPU
        reader.Take(RecordingReader.PerfInfo, 68, 4, g.To + 5 * Ms, Dpc(g.To + 4 * Ms, Gpu)); // outside any gap
        reader.Take(RecordingReader.PerfInfo, 46, 4, g.From + Ms, Dpc(g.From, Gpu));          // not a DPC
        Assert.Equal(2, reader.Finish(true, 0).Runs.Count);
    }

    [Fact]
    public void The_reader_takes_drivers_images_and_not_programs()
    {
        var gaps = Gaps(3);
        var reader = new RecordingReader(4, gaps);
        reader.Take(RecordingReader.ImageLoad, 3, 0, 1, ImagePayload(Gpu, 0x1000, @"\SystemRoot\System32\drivers\nvlddmkm.sys"));
        reader.Take(RecordingReader.ImageLoad, 10, 0, 1, ImagePayload(0x7FF6_0000_0000, 0x1000, @"\Device\HarddiskVolume3\Games\game.exe"));
        var image = Assert.Single(reader.Finish(true, 0).Images);
        Assert.Equal((ulong)0x1000, image.Size);
        Assert.EndsWith("nvlddmkm.sys", image.File);
    }

    [Fact]
    public void A_recording_that_does_not_cover_the_gaps_is_off_the_clock()
    {
        var gaps = Gaps(3);
        var covered = new RecordingReader(4, gaps);
        covered.Take(RecordingReader.PerfInfo, 68, 4, gaps[^1].To + Ms, Dpc(gaps[0].From - Ms, Gpu));
        Assert.True(covered.Finish(true, 0).OnClock);
        Assert.False(covered.Finish(false, 0).OnClock);
        var elsewhere = new RecordingReader(4, gaps);
        elsewhere.Take(RecordingReader.PerfInfo, 68, 4, 2000, Dpc(1000, Gpu)); // another clock's numbers
        Assert.False(elsewhere.Finish(true, 0).OnClock);
    }

    [Fact]
    public void Nobody_is_named_from_a_recording_that_cannot_be_trusted()
    {
        var folder = Directory.CreateTempSubdirectory("exo-mousedrivers-").FullName;
        try
        {
            var core = new MouseCore(new NoDevices(), new NoWriter(), Path.Combine(folder, "b.json"), () => DateTimeOffset.UtcNow.AddHours(-1), () => DateTimeOffset.UtcNow);
            var sample = Test();
            var gaps = LateGaps(sample.Reports, F);
            Assert.NotEmpty(gaps);
            var runs = gaps.Select(g => new Run(g.From, g.To, Gpu)).ToList();
            static (string?, string?) None(string _) => (null, null);

            Assert.Contains("didn't record", core.Name(sample, null, None).Problem);
            Assert.Contains("line the recording up", core.Name(sample, new Recording(runs, Images, false, 0), None).Problem);
            Assert.Contains("dropped", core.Name(sample, new Recording(runs, Images, true, 3), None).Problem);
            var named = core.Name(sample, new Recording(runs, Images, true, 0), None);
            Assert.Null(named.Problem);
            Assert.Equal("nvlddmkm.sys", named.Names.Single().File);
        }
        finally
        {
            DeletionGate.DeleteDirectory(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Users\Ann\AppData\Local\ExoLauncher\mouse-recording", true)]
    [InlineData(@"C:\Users\R&D Team\AppData\Local\ExoLauncher\mouse-recording", true)] // inside its quotes, cmd reads & as itself
    [InlineData(@"C:\Users\100%\AppData", false)]
    [InlineData(@"C:\Users\a""b\AppData", false)]
    [InlineData(@"C:\Users\a^b\AppData", false)]
    public void Only_a_plain_folder_reaches_the_administrators_command_line(string path, bool plain) => Assert.Equal(plain, Recorder.Plain(path));

    [Fact]
    public void The_recording_uses_the_stopwatchs_clock_and_stops_itself_after_the_test()
    {
        var lines = Recorder.Commands("logman", "ping", "f.etl", "mark", TimeSpan.FromSeconds(10));
        Assert.Contains("-ct perf", lines[0]);
        Assert.Contains("(dpc,isr,img)", lines[0]);
        Assert.Contains("&& type nul > \"mark\"", lines[0]); // the test starts only if the logger did
        Assert.Contains("-n 14", lines[1]);
        Assert.Contains("stop", lines[2]);
    }

    // ---- Builders ----

    private static string? Name(ulong address) => FileAt(Images.OrderBy(i => i.Base).ToList(), address);

    /// <summary>Gaps of 4 ms at 1 kHz, a second apart.</summary>
    private static List<Late> Gaps(int count) => Enumerable.Range(1, count).Select(i => new Late(i * 100L * F, i * 100L * F + 4 * Ms, Ms)).ToList();

    private static byte[] Dpc(long from, ulong routine)
    {
        var data = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(data, from);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), routine);
        return data;
    }

    /// <summary>Image_Load as the kernel writes it on x64: base, size, then fields to byte 56, then the file's name.</summary>
    private static byte[] ImagePayload(ulong address, ulong size, string file)
    {
        var name = Encoding.Unicode.GetBytes(file + "\0");
        var data = new byte[56 + name.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(data, address);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), size);
        name.CopyTo(data, 56);
        return data;
    }

    /// <summary>A moving mouse at 1 kHz, a 4 ms gap every 200 reports, with its CPU known from the counters.</summary>
    private static Sample Test()
    {
        var reports = new List<Report>();
        var at = F;
        for (var i = 0; i < 9000; i++)
        {
            at += i % 200 == 199 ? 4 * Ms : Ms;
            // The hand slows and speeds, so reports a second vary and the CPU that follows them can be read.
            var slow = (i / 1000) % 2 == 1 && i % 2 == 0;
            if (!slow) reports.Add(new Report(at, 12, 0));
        }
        var windows = new List<Window>();
        for (var w = 0; w < 36; w++)
        {
            var from = F + w * F / 4;
            var to = from + F / 4;
            var rate = reports.Count(r => r.At >= from && r.At < to) * 4.0;
            windows.Add(new Window(from, to, [100, rate, 100, 100], [0.1, 0.1, 0.1, 0.1]));
        }
        return new Sample(reports, "HID\\X", F, 0, windows, new Topology([], [], 4));
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

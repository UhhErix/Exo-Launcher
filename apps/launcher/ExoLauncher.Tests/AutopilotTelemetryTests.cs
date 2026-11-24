using System.Buffers.Binary;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Autopilot's telemetry and its report. Ways it could fail silently:
/// - the process list is read at the wrong offsets, so every app's processor time or name is another field;
/// - a name pointer outside the buffer, or a next offset that loops, reads garbage or never ends;
/// - a slow scene (every frame slower) is counted as stutters, or a 400 fps game's small spike is;
/// - the first seconds, before there is a typical frame time, count as stutters;
/// - a background app that is always busy is blamed, and one that jumps is not;
/// - a stutter with the card's memory full, the card hot, the processor slowed, the game paging or a driver busy is unexplained;
/// - a report is kept for a few seconds of play, or lost when the file is held;
/// - the sampler stays open while the game is behind or after it closes;
/// - the reports file grows without end.
/// </summary>
public sealed class AutopilotTelemetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-autopilot-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) DeletionGate.DeleteDirectory(_dir, recursive: true);
    }

    // ---------------------------------------------------------------- the process list

    private static byte[] Entry(int pid, int parent, string name, long cpu, long write, long faults, int nameAt, nint baseAddress, int next)
    {
        var e = new byte[ProcessSnapshot.EntrySize];
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(ProcessSnapshot.NextAt), next);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(ProcessSnapshot.ThreadsAt), 7);
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(ProcessSnapshot.PrivateWorkingSetAt), 123_456);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(ProcessSnapshot.HardFaultsAt), (uint)faults);
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(ProcessSnapshot.CreateAt), 1000 + pid);
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(ProcessSnapshot.UserAt), cpu / 2);
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(ProcessSnapshot.KernelAt), cpu - cpu / 2);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(ProcessSnapshot.NameLengthAt), (ushort)(name.Length * 2));
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(ProcessSnapshot.NamePointerAt), name.Length == 0 ? 0 : baseAddress + nameAt);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(ProcessSnapshot.BasePriorityAt), 8);
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(ProcessSnapshot.PidAt), pid);
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(ProcessSnapshot.ParentAt), parent);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(ProcessSnapshot.HandlesAt), 42);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(ProcessSnapshot.SessionAt), 1);
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(ProcessSnapshot.PrivateBytesAt), 999);
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(ProcessSnapshot.ReadBytesAt), 5);
        BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(ProcessSnapshot.WriteBytesAt), write);
        return e;
    }

    /// <summary>Two entries, each followed by its name, as the kernel lays them out.</summary>
    private static byte[] Buffer(nint baseAddress, int nameBreak = 0)
    {
        const int First = ProcessSnapshot.EntrySize + 32;
        var a = Entry(4, 0, "System", 10_000, 0, 0, ProcessSnapshot.EntrySize, baseAddress, First);
        var b = Entry(1234, 4, "game.exe", 20_000_000, 1 << 20, 300, First + ProcessSnapshot.EntrySize + nameBreak, baseAddress, 0);
        var data = new byte[First + ProcessSnapshot.EntrySize + 32];
        a.CopyTo(data, 0);
        Encoding.Unicode.GetBytes("System").CopyTo(data, ProcessSnapshot.EntrySize);
        b.CopyTo(data, First);
        Encoding.Unicode.GetBytes("game.exe").CopyTo(data, First + ProcessSnapshot.EntrySize);
        return data;
    }

    [Fact]
    public void TheProcessListIsReadAtItsOwnOffsets()
    {
        var rows = ProcessSnapshot.Parse(Buffer(0x10000), 0x10000);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new ProcessRow(4, 0, "System", 7, 42, 1, 1004, 10_000, 123_456, 999, 5, 0, 8, 0), rows[0]);
        Assert.Equal("game.exe", rows[1].Name);
        Assert.Equal(1234, rows[1].Pid);
        Assert.Equal(4, rows[1].Parent);
        Assert.Equal(20_000_000, rows[1].CpuTime);
        Assert.Equal(1 << 20, rows[1].WriteBytes);
        Assert.Equal(300, rows[1].HardFaults);
    }

    [Fact]
    public void ANameOutsideTheBufferIsNoNameAndALoopEnds()
    {
        var data = Buffer(0x10000, nameBreak: 10_000);
        var rows = ProcessSnapshot.Parse(data, 0x10000);
        Assert.Equal("", rows[1].Name);
        // A next offset of zero or less, or one past the buffer end, stops safely.
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), -ProcessSnapshot.EntrySize);
        Assert.Single(ProcessSnapshot.Parse(data, 0x10000));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), int.MaxValue);
        Assert.Single(ProcessSnapshot.Parse(data, 0x10000));
        Assert.Empty(ProcessSnapshot.Parse(new byte[10], 0));
    }

    [Fact]
    public void TheLiveListHasThisProcess()
    {
        // Read-only: the kernel's list, nothing opened.
        var self = ProcessSnapshot.Read().Single(row => row.Pid == Environment.ProcessId);
        Assert.False(string.IsNullOrEmpty(self.Name));
        Assert.True(self.Threads > 0);
        Assert.True(self.CpuTime > 0);
        Assert.True(self.PrivateBytes > 0);
    }

    [Fact]
    public void TheLiveSamplerReadsThisPcWithoutRights()
    {
        // Read-only, as a standard user: processor, memory, Windows' counters and the process list.
        using var sampler = AutopilotSampler.Open()!;
        Assert.Null(sampler.Read([Environment.ProcessId]));
        Thread.Sleep(400);
        var second = sampler.Read([Environment.ProcessId]);
        Assert.NotNull(second);
        Assert.InRange(second.CpuPercent, 0, 100);
        Assert.True(second.RamTotal > second.RamUsed && second.RamUsed > 0);
        Assert.NotNull(second.Standby);
        Assert.NotNull(second.Free);
        Assert.NotNull(second.DpcPercent);
        Assert.True(second.GameCores >= 0);
    }

    // ---------------------------------------------------------------- stutters

    private static SystemSecond Calm(params AppLoad[] background) =>
        new(30, 12, 80, 60, 4L << 30, 8L << 30, 12L << 30, 32L << 30, 8L << 30, 6L << 30, 0.5, 0.3, 105, null, 3, 0, 0, background);

    private static List<double> Frames(double ms, int count, params double[] spikes) => [.. Enumerable.Repeat(ms, count), .. spikes];

    private static StutterAnalyzer Warm(int seconds = 10, SystemSecond? system = null)
    {
        var analyzer = new StutterAnalyzer();
        for (var i = 0; i < seconds; i++) analyzer.Add(Frames(7, 140), system ?? Calm(new AppLoad("chrome", 0.1)));
        return analyzer;
    }

    [Fact]
    public void TheLineIsTwoAndAHalfTimesTypicalAndEightMsOver()
    {
        Assert.Equal(17.5, StutterAnalyzer.Line(7)); // 144 fps: 2.5 x 7 = 17.5, over 7 + 8
        Assert.Equal(10.5, StutterAnalyzer.Line(2.5)); // 400 fps: 2.5 x 2.5 = 6.25 is under 2.5 + 8
        Assert.Equal(41.75, StutterAnalyzer.Line(16.7));
    }

    [Fact]
    public void TheFirstSecondsCountNoStutters()
    {
        var analyzer = new StutterAnalyzer();
        analyzer.Add(Frames(7, 140, 80), Calm());
        Assert.Equal(0, analyzer.Stutters);
    }

    [Fact]
    public void ASlowSceneIsNotAStutterAndASpikeIs()
    {
        var analyzer = Warm();
        // The whole second slower (a heavy scene): no frame stands out against the last 30 seconds.
        analyzer.Add(Frames(12, 80), Calm());
        Assert.Equal(0, analyzer.Stutters);
        analyzer.Add(Frames(7, 130, 40, 45), Calm());
        Assert.Equal(2, analyzer.Stutters);
    }

    [Fact]
    public void AFastGamesSmallSpikeIsNotAStutter()
    {
        var analyzer = new StutterAnalyzer();
        for (var i = 0; i < 10; i++) analyzer.Add(Frames(2.5, 400), Calm());
        analyzer.Add(Frames(2.5, 390, 7, 8), Calm());
        Assert.Equal(0, analyzer.Stutters);
    }

    [Fact]
    public void AnAppThatJumpsIsBlamedAndOneAlwaysBusyIsNot()
    {
        var analyzer = Warm(10, Calm(new AppLoad("encoder", 2.0), new AppLoad("updater", 0.05)));
        analyzer.Add(Frames(7, 130, 60), Calm(new AppLoad("encoder", 2.1), new AppLoad("updater", 1.5)));
        var finding = Assert.Single(analyzer.Findings("Game"));
        Assert.Equal("background", finding.Kind);
        Assert.Equal("updater", finding.App);
        Assert.Contains("updater", finding.Suggestion);
        Assert.Empty(analyzer.Learned()); // one stutter: not yet learned
        analyzer.Add(Frames(7, 130, 60), Calm(new AppLoad("updater", 1.4), new AppLoad("backup", 0.7)));
        Assert.Equal(["updater"], analyzer.Learned());
        var findings = analyzer.Findings("Game");
        Assert.Contains(findings, f => f.App == "updater" && f.Detail.Contains("1.5 cores"));
        Assert.Contains(findings, f => f.App == "backup" && f.Detail.Contains("0.7 cores"));
    }

    [Fact]
    public void ASpikeInTheSecondBeforeCounts()
    {
        var analyzer = Warm();
        analyzer.Add(Frames(7, 140), Calm(new AppLoad("defender", 3)));
        analyzer.Add(Frames(7, 130, 50), Calm());
        Assert.Equal("defender", Assert.Single(analyzer.Findings("Game")).App);
    }

    [Theory]
    [InlineData("vram")]
    [InlineData("gpuHeat")]
    [InlineData("cpuThrottle")]
    [InlineData("paging")]
    [InlineData("dpc")]
    [InlineData("shaders")]
    public void EachCauseIsFound(string kind)
    {
        var analyzer = Warm();
        var calm = Calm();
        var second = kind switch
        {
            "vram" => calm with { VramUsed = (long)(7.9 * (1L << 30)) },
            "gpuHeat" => calm with { GpuTemperature = 89 },
            "cpuThrottle" => calm with { CpuPercent = 70, CpuPerformance = 80 },
            "paging" => calm with { GameHardFaults = 400 },
            "dpc" => calm with { DpcPercent = 35 },
            _ => calm with { GameWriteBytes = 4 << 20, GameCores = 5 },
        };
        analyzer.Add(Frames(7, 130, 60), second);
        var finding = Assert.Single(analyzer.Findings("Game"));
        Assert.Equal(kind, finding.Kind);
        Assert.False(string.IsNullOrWhiteSpace(finding.Suggestion));
    }

    [Fact]
    public void AStutterWithNothingSeenIsUnexplained()
    {
        var analyzer = Warm();
        analyzer.Add(Frames(7, 130, 60), Calm());
        Assert.Equal("unexplained", Assert.Single(analyzer.Findings("Game")).Kind);
        analyzer.Add(Frames(7, 130, 60), null);
        Assert.Equal(2, Assert.Single(analyzer.Findings("Game")).Stutters);
    }

    [Fact]
    public void IslcsRuleIsCountedNotActedOn()
    {
        var analyzer = Warm();
        analyzer.Add(Frames(7, 130, 60), Calm() with { Free = 300L << 20, Standby = 6L << 30 });
        Assert.Equal(1, analyzer.IslcSeconds);
        Assert.Equal(1, analyzer.IslcStutters);
    }

    // ---------------------------------------------------------------- the watch and its reports

    private sealed class FakeSampler : ISystemSampler
    {
        internal int Opened, Disposed;
        public SystemSecond? Read(IReadOnlyCollection<int> gameTree) => Calm(new AppLoad("chrome", 0.1));
        public void Dispose() => Disposed++;
    }

    [Fact]
    public void ASessionOfAMinuteGetsAReportAndShorterNone()
    {
        var sampler = new FakeSampler();
        var kept = new List<AutopilotReport>();
        var watch = new AutopilotWatch(() => { sampler.Opened++; return sampler; }, r => { kept.Add(r); return true; }, () => DateTime.UtcNow);
        for (var i = 0; i < 30; i++) watch.Second("g1", "Game One", true, Frames(7, 143), [1]);
        watch.Second(null, null, false, [], []);
        Assert.Empty(kept);
        for (var i = 0; i < 70; i++) watch.Second("g1", "Game One", true, Frames(7, 143), [1]);
        watch.End();
        var report = Assert.Single(kept);
        Assert.Equal("Game One", report.Title);
        Assert.InRange(report.AverageFps, 142, 143);
        Assert.Equal(2, sampler.Opened);
        Assert.Equal(2, sampler.Disposed);
    }

    [Fact]
    public void TheSamplerGoesWhileTheGameIsBehind()
    {
        var sampler = new FakeSampler();
        var watch = new AutopilotWatch(() => { sampler.Opened++; return sampler; }, _ => true, () => DateTime.UtcNow);
        watch.Second("g1", "Game", true, Frames(7, 10), [1]);
        watch.Second("g1", "Game", false, [], [1]);
        Assert.Equal(1, sampler.Disposed);
        Assert.NotNull(watch.Live);
        watch.End();
        Assert.Null(watch.Live);
    }

    [Fact]
    public void AReportThatCouldNotBeKeptIsKeptAtTheNextEnd()
    {
        var sampler = new FakeSampler();
        var can = false;
        var kept = new List<AutopilotReport>();
        var watch = new AutopilotWatch(() => sampler, r => { if (can) kept.Add(r); return can; }, () => DateTime.UtcNow);
        for (var i = 0; i < 70; i++) watch.Second("g1", "Game", true, Frames(7, 143), [1]);
        watch.End();
        Assert.Empty(kept);
        can = true;
        watch.End();
        Assert.Single(kept);
    }

    [Fact]
    public void TheReportsFileKeepsTheNewestFifty()
    {
        var path = Path.Combine(_dir, AutopilotReports.FileName);
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        AutopilotReport Report(int i, DateTime ended) => new("g" + (i % 3), "Game", ended.AddHours(-1), ended, 3600, 500_000, 140, 90, 60, i,
            [], null, [], i == 59 ? ["updater"] : [], 0, 0);
        Assert.True(AutopilotReports.Add(path, Report(0, now.AddDays(-100)), now));
        for (var i = 1; i < 60; i++) Assert.True(AutopilotReports.Add(path, Report(i, now.AddMinutes(i)), now));
        var reports = AutopilotReports.Load(path);
        Assert.Equal(AutopilotReports.MaxReports, reports.Count);
        Assert.Equal(59, reports[0].Stutters);
        Assert.DoesNotContain(reports, r => r.Stutters == 0);
        Assert.Equal(["updater"], AutopilotReports.LearnedFor(reports, "g" + (59 % 3)));
        Assert.True(new FileInfo(path).Length < 256 * 1024);
    }

    [Fact]
    public void Watch_purges_standby_memory_on_islc_threshold_with_cooldown_only_in_front()
    {
        var clock = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        var currentSecond = Calm() with { Free = 2L << 30, Standby = 4L << 30 };
        var purges = 0;
        var watch = new AutopilotWatch(
            () => new CustomSampler(() => currentSecond),
            _ => true,
            () => clock,
            () => { purges++; return true; });

        watch.Second("g1", "Game", true, Frames(7, 10), [1]);
        Assert.Equal(0, purges);

        currentSecond = Calm() with { Free = 512L << 20, Standby = 2L << 30 };
        watch.Second("g1", "Game", false, [], [1]);
        Assert.Equal(0, purges);

        watch.Second("g1", "Game", true, Frames(7, 10), [1]);
        Assert.Equal(1, purges);

        clock = clock.AddSeconds(30);
        watch.Second("g1", "Game", true, Frames(7, 10), [1]);
        Assert.Equal(1, purges);

        clock = clock.AddSeconds(35);
        watch.Second("g1", "Game", true, Frames(7, 10), [1]);
        Assert.Equal(2, purges);
        watch.End();

        var disabledPurges = 0;
        var disabledWatch = new AutopilotWatch(
            () => new CustomSampler(() => currentSecond),
            _ => true,
            () => clock,
            () => { disabledPurges++; return true; },
            () => false);
        disabledWatch.Second("g1", "Game", true, Frames(7, 10), [1]);
        Assert.Equal(0, disabledPurges);
        disabledWatch.End();
    }

    [Fact]
    public void StandbyPurgeControllerReportsUnavailableAndRetriesAfterElevationChanges()
    {
        var fake = new FakeStandbyPurgeSystem();
        var controller = new StandbyPurgeController(fake);

        var unavailable = controller.Status();
        Assert.False(unavailable.Available);
        Assert.False(string.IsNullOrWhiteSpace(unavailable.Reason));
        Assert.False(controller.TryPurge());
        Assert.Equal(0, fake.EnableCalls);
        Assert.Equal(0, fake.PurgeCalls);
        Assert.Equal(0, fake.ElevatedCalls);

        fake.CanElevateSilently = true;
        var silentStatus = controller.Status();
        Assert.True(silentStatus.Available);
        Assert.True(silentStatus.SilentElevated);
        Assert.Null(silentStatus.Reason);
        Assert.True(controller.TryPurge());
        Assert.Equal(1, fake.ElevatedCalls);

        fake.CanElevateSilently = false;
        fake.PrivilegeHeld = true;
        var inProcStatus = controller.Status();
        Assert.True(inProcStatus.Available);
        Assert.True(inProcStatus.InProcess);
        Assert.Null(inProcStatus.Reason);
        Assert.True(controller.TryPurge());
        Assert.Equal(1, fake.EnableCalls);
        Assert.Equal(1, fake.PurgeCalls);

        fake.PurgeStatus = unchecked((int)0xC0000061);
        Assert.False(controller.TryPurge());
        fake.PurgeStatus = 0;
        Assert.True(controller.TryPurge());
        Assert.Equal(3, fake.PurgeCalls);
    }

    private sealed class FakeStandbyPurgeSystem : IStandbyPurgeSystem
    {
        public bool PrivilegeHeld { get; set; }
        public bool CanElevateSilently { get; set; }
        public int PurgeStatus { get; set; }
        public int EnableCalls { get; private set; }
        public int PurgeCalls { get; private set; }
        public int ElevatedCalls { get; private set; }

        public bool HasPrivilege() => PrivilegeHeld;
        public bool EnablePrivilege() { EnableCalls++; return PrivilegeHeld; }
        public int PurgeMemoryList() { PurgeCalls++; return PurgeStatus; }
        public bool CanSilentElevate() => CanElevateSilently;
        public bool RunSilentElevatedPurge() { ElevatedCalls++; return CanElevateSilently; }
    }

    private sealed class CustomSampler(Func<SystemSecond?> read) : ISystemSampler
    {
        public SystemSecond? Read(IReadOnlyCollection<int> gameTree) => read();
        public void Dispose() { }
    }
}

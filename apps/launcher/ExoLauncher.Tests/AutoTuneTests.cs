using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Auto tune could fail, all against fakes, since nothing here may change a voltage or a clock:
/// - a value goes on before its marker is on disk, so a crash on it leaves no trace;
/// - a marker found at the next start doesn't fail that value, or a later search tries it again (a crash loop);
/// - a torn marker reads as no crash;
/// - a depth past the limits, or a positive Curve Optimizer margin, reaches the hardware;
/// - a test runs with a game open or on battery, or carries on when one starts, or a game starting counts against the value;
/// - one logged hardware error fails every step after it;
/// - the result isn't backed off, or a failed final value stays on;
/// - a value that didn't take, or broke the last measurement, is reported as tuned;
/// - a run at stock that already fails goes on to tune;
/// - the sign-in script applies after a crash, applies before it marks itself, or takes anything but integers;
/// - Undo leaves the value or the task;
/// - the gain shown isn't what was measured;
/// - a part Exo can't tune is offered (X3D, APUs, unknown CPUs), or the first run shows no warning.
/// </summary>
public sealed class AutoTuneTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-tune-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TuneStore _store;
    private static readonly TuneLimits Limits = new(30, 5, 5, 85, 10, 60, 10);

    public AutoTuneTests() => _store = new TuneStore(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) DeletionGate.DeleteDirectory(_folder, true);
    }

    private sealed class FakePart(TuneStore store, TuneLimits limits) : ITunePart
    {
        public string Id => "part";
        public TuneLimits Limits { get; } = limits;
        public int Now;
        public int? Answers;
        public readonly List<string> Log = [];
        public readonly List<int?> MarkerAtApply = [];
        public void Settle(TuneRun stock) => Log.Add("settle");
        public void Apply(int depth)
        {
            Limits.Check(depth);
            MarkerAtApply.Add(store.Marker()?.Depth);
            Now = depth;
            Log.Add("apply " + depth);
        }
        public int? Read() => Answers ?? (int?)Now;
        public void Stock()
        {
            Now = 0;
            Log.Add("stock");
        }
        public int[]? SignIn(int depth) => [depth];
    }

    /// <summary>Fails at <paramref name="failAt"/> and deeper; each run calls check once, as the real ones do each second.</summary>
    private sealed class FakeTest(FakePart part, int failAt = int.MaxValue) : ITuneTest
    {
        public int Runs;
        public double Temperature = 60;
        public Action<int>? During;
        public Func<int, double> Score = _ => 100;
        public TuneRun Run(TimeSpan length, Func<string?> check)
        {
            Runs++;
            During?.Invoke(part.Now);
            if (check() is { } reason) return new TuneRun(Score(part.Now), 2000, reason);
            return new TuneRun(Score(part.Now), 2000, part.Now >= failAt ? "A wrong answer." : null);
        }
        public double? TemperatureC() => Temperature - part.Now / 5.0;
    }

    private sealed class FakeHost : ITuneHost
    {
        public string? Refused;
        public int Errors;
        public bool Stop;
        public int Reapplied;
        public string? Refusal() => Refused;
        public int HardwareErrors() => Errors;
        public bool StopAsked() => Stop;
        public void Reapply() => Reapplied++;
    }

    private (FakePart Part, FakeTest Test, FakeHost Host, TuneRunner Runner) Make(int failAt = int.MaxValue, TuneLimits? limits = null)
    {
        var part = new FakePart(_store, limits ?? Limits);
        var test = new FakeTest(part, failAt);
        var host = new FakeHost();
        return (part, test, host, new TuneRunner(part, test, host, _store, "cooler"));
    }

    [Fact]
    public void FindsTheDeepestThatHeldAndBacksOff()
    {
        var (part, _, _, runner) = Make(failAt: 20);
        var state = runner.Run();
        Assert.Equal("done", state.Phase);
        Assert.Equal(10, state.Applied);
        Assert.Equal(new[] { 5, 10, 15 }, state.Passed);
        Assert.Contains(20, state.Failed);
        Assert.Equal(10, part.Now);
        Assert.Null(_store.Marker());
    }

    [Fact]
    public void StopsAtTheLimitWithNoFailure()
    {
        var (part, _, _, runner) = Make();
        var state = runner.Run();
        Assert.Equal(25, state.Applied);
        Assert.DoesNotContain("apply 35", part.Log);
    }

    [Fact]
    public void TheMarkerIsOnDiskBeforeEachValueGoesOn()
    {
        var (part, _, _, runner) = Make(failAt: 20);
        runner.Run();
        Assert.Equal(new int?[] { 5, 10, 15, 20, 10 }, part.MarkerAtApply);
    }

    [Fact]
    public void FailingGoesToStockBeforeTheMarkerGoes()
    {
        var (part, test, _, runner) = Make(failAt: 10);
        var markerAtFailure = (int?)null;
        test.During = depth => { if (depth == 10) markerAtFailure = _store.Marker()?.Depth; };
        var state = runner.Run();
        Assert.Equal(10, markerAtFailure);
        Assert.Equal(0, state.Applied);
        Assert.Equal(0, part.Now);
        Assert.Equal("stopped", state.Phase);
    }

    [Fact]
    public void AMarkerFoundAtStartFailsThatValueAndTheSearchStaysShallower()
    {
        // The PC went down testing 20: the marker and the record are all that's left.
        _store.Save("part", new TunePartState { Phase = "testing", Testing = 20, Passed = [5, 10, 15] });
        _store.WriteMarker("part", 20);
        var (part, _, _, runner) = Make();
        var state = runner.Run();
        Assert.Contains(20, state.Failed);
        Assert.DoesNotContain("apply 20", part.Log);
        Assert.DoesNotContain("apply 25", part.Log);
        Assert.Equal(10, state.Applied);
        Assert.Null(_store.Marker());
    }

    [Fact]
    public void CrashingAgainOnTheFinalValueBacksOffAgainNeverTheSameValue()
    {
        _store.Save("part", new TunePartState { Passed = [5, 10, 15], Failed = [20] });
        _store.WriteMarker("part", 10);
        var (part, _, _, runner) = Make();
        var state = runner.Run();
        Assert.Contains(10, state.Failed);
        Assert.DoesNotContain("apply 10", part.Log);
        Assert.Equal(0, state.Applied);
    }

    [Fact]
    public void RecoverIsPureAndTakesWhatWasAppliedAway()
    {
        var saved = new TunePartState { Applied = 10, SignIn = [10], Passed = [5, 10, 15] };
        var seen = TuneRunner.Recover("part", saved, new TuneMarker("part", 15), held: false);
        Assert.Equal("crashed", seen.Phase);
        Assert.Equal(0, seen.Applied);
        Assert.Null(seen.SignIn);
        Assert.Equal(new[] { 15 }, seen.Failed);
        Assert.Equal(new[] { 5, 10 }, seen.Passed);
        Assert.Equal(10, saved.Applied);
    }

    [Fact]
    public void AnotherPartsMarkerDoesntFailThisOne()
    {
        var saved = new TunePartState { Applied = 10 };
        Assert.Same(saved, TuneRunner.Recover("part", saved, new TuneMarker("other", 15), held: false));
    }

    [Fact]
    public void ATornMarkerStillMeansACrash()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "testing.json"), "{\"Par");
        Assert.Equal(new TuneMarker("", 0), _store.Marker());
        var seen = TuneRunner.Recover("part", new TunePartState { Applied = 10 }, _store.Marker(), held: false);
        Assert.Equal("crashed", seen.Phase);
        Assert.Equal(0, seen.Applied);
        Assert.Empty(seen.Failed);
    }

    [Fact]
    public void ReconcileWritesACrashForThePartThatHadIt()
    {
        _store.Save("gpu", new TunePartState { Applied = 30, SignIn = [30, 0] });
        _store.Save("cpu", new TunePartState { Phase = "testing", Testing = 15 });
        _store.WriteMarker("cpu", 15);
        TuneRunner.Reconcile(_store);
        Assert.Equal(30, _store.Part("gpu").Applied);
        Assert.Contains(15, _store.Part("cpu").Failed);
        Assert.Null(_store.Marker());
    }

    [Fact]
    public void HeldBySignInTurnsOffWhatWasAppliedAndFailsIt()
    {
        var seen = TuneRunner.Recover("part", new TunePartState { Applied = 10, Passed = [5, 10] }, null, held: true);
        Assert.Equal(0, seen.Applied);
        Assert.Contains(10, seen.Failed);
        Assert.DoesNotContain(10, seen.Passed);
        Assert.Contains("crashed", seen.Note);
    }

    [Fact]
    public void AWorkerThatWentAwayWithNoMarkerIsStoppedNotCrashed()
    {
        var seen = TuneRunner.Seen("part", new TunePartState { Phase = "testing", Testing = 5 }, null, false, workerAlive: false);
        Assert.Equal("stopped", seen.Phase);
        Assert.Empty(seen.Failed);
    }

    [Fact]
    public void ARunningTestsOwnMarkerIsntACrash()
    {
        var saved = new TunePartState { Phase = "testing", Testing = 5 };
        Assert.Equal("testing", TuneRunner.Seen("part", saved, new TuneMarker("part", 5), false, workerAlive: true).Phase);
        Assert.Equal("crashed", TuneRunner.Seen("part", saved, new TuneMarker("part", 5), false, workerAlive: false).Phase);
    }

    [Fact]
    public void AWorkerAliveForAnotherPartDoesntHideACrashOnThisOne()
    {
        var saved = new TunePartState { Applied = 10 };
        Assert.Equal("crashed", TuneRunner.Seen("part", saved, null, held: true, workerAlive: true).Phase);
    }

    [Fact]
    public void NoTemperatureReadingMeansNoTest()
    {
        var part = new FakePart(_store, Limits);
        var state = new TuneRunner(part, new ColdTest(), new FakeHost(), _store, "cooler").Run();
        Assert.DoesNotContain(part.Log, line => line.StartsWith("apply", StringComparison.Ordinal));
        Assert.Contains("temperature", state.Note);
    }

    private sealed class ColdTest : ITuneTest
    {
        public TuneRun Run(TimeSpan length, Func<string?> check) => new(100, null, check());
        public double? TemperatureC() => null;
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void TheLimitsRefuseWhatsOutside(int depth) => Assert.Throws<ArgumentOutOfRangeException>(() => Limits.Check(depth));

    [Fact]
    public void DeepestStopsShortOfTheShallowestFailure()
    {
        Assert.Equal(30, Limits.Deepest([]));
        Assert.Equal(15, Limits.Deepest([20, 25]));
        Assert.Equal(0, Limits.Deepest([5]));
        Assert.Equal(30, Limits.Deepest([100]));
    }

    [Theory]
    [InlineData(1000, 240)] // The RTX 3070's driver reads -1000 to +1000: Exo's own limit holds.
    [InlineData(100, 90)]
    [InlineData(29, 0)]
    public void AGeforceNeverGoesPastExosLimitOrItsDrivers(int driverMax, int furthest) => Assert.Equal(furthest, TuneNvidia.For(driverMax, 93).Furthest);

    [Fact]
    public void AGeforceStopsFiveDegreesShortOfItsMaximum() => Assert.Equal(88, TuneNvidia.For(1000, 93).StopC);

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(5, 0xFFFBu)]
    [InlineData(30, 0xFFE2u)]
    public void AMarginIsAlwaysNegativeOnTheWire(int depth, uint wire)
    {
        Assert.Equal(wire, TuneRyzen.MarginArgument(depth));
        Assert.True((short)(ushort)TuneRyzen.MarginArgument(depth) <= 0);
    }

    [Fact]
    public void AMarginCantBePositive() => Assert.Throws<ArgumentOutOfRangeException>(() => TuneRyzen.MarginArgument(-5));

    [Fact]
    public void ARyzenStaysAtThirtyOnEveryGeneration() => Assert.Equal(30, TuneRyzen.For(16).Furthest);

    [Fact]
    public void AGameRunningRefusesAndTouchesNothing()
    {
        _store.Save("part", new TunePartState { Applied = 10, SignIn = [10], Phase = "done" });
        var (part, test, host, runner) = Make();
        host.Refused = "A game started.";
        var state = runner.Run();
        Assert.Empty(part.Log);
        Assert.Equal(0, test.Runs);
        Assert.Equal(10, state.Applied);
        Assert.Equal("A game started.", state.Note);
    }

    [Fact]
    public void AGameStartingMidTestGoesToStockAndDoesntFailTheValue()
    {
        var (part, test, host, runner) = Make();
        test.During = depth => { if (depth == 15) host.Refused = "A game started."; };
        var state = runner.Run();
        Assert.Equal(0, part.Now);
        Assert.Equal(0, state.Applied);
        Assert.Empty(state.Failed);
        Assert.Equal("A game started.", state.Note);
        Assert.Null(_store.Marker());
    }

    [Fact]
    public void StopGoesToStock()
    {
        var (part, test, host, runner) = Make();
        test.During = depth => { if (depth == 10) host.Stop = true; };
        var state = runner.Run();
        Assert.Equal(0, part.Now);
        Assert.Equal("Stopped.", state.Note);
        Assert.Empty(state.Failed);
    }

    [Fact]
    public void AHardwareErrorFailsItsStepAndNotTheNextOnes()
    {
        var (part, test, host, runner) = Make();
        test.During = depth => { if (depth == 20) host.Errors++; };
        var state = runner.Run();
        Assert.Contains(20, state.Failed);
        Assert.Equal(10, state.Applied);
        Assert.Equal(10, part.Now);
    }

    [Fact]
    public void ReachingTheHeatLimitFailsTheStep()
    {
        var (_, test, _, runner) = Make();
        test.During = depth => test.Temperature = depth >= 20 ? 200 : 60;
        var state = runner.Run();
        Assert.Contains(20, state.Failed);
        Assert.Equal(10, state.Applied);
    }

    [Fact]
    public void AnErrorAtStockStopsBeforeAnythingIsApplied()
    {
        var (part, test, host, runner) = Make();
        test.During = _ => host.Errors++;
        var state = runner.Run();
        Assert.DoesNotContain(part.Log, line => line.StartsWith("apply", StringComparison.Ordinal));
        Assert.Equal(0, state.Applied);
        Assert.Contains("At stock", state.Note);
    }

    [Fact]
    public void AValueThatDidntTakeGoesToStock()
    {
        var (part, _, _, runner) = Make();
        part.Answers = 0;
        var state = runner.Run();
        Assert.Equal(0, state.Applied);
        Assert.Equal("stock", part.Log[^1]);
        Assert.Contains("didn't take", state.Note);
        Assert.Null(_store.Marker());
    }

    [Fact]
    public void AFinalValueThatFailsTriesShallower()
    {
        var part = new FakePart(_store, Limits);
        var failedOnce = false;
        var test = new FakeTest(part);
        var host = new FakeHost();
        test.During = depth =>
        {
            if (depth == 25 && !failedOnce && part.Log.Count(line => line == "apply 25") == 2)
            {
                failedOnce = true;
                host.Errors++;
            }
        };
        var state = new TuneRunner(part, test, host, _store, "cooler").Run();
        Assert.Contains(25, state.Failed);
        Assert.Equal(20, state.Applied);
    }

    [Fact]
    public void NothingHoldingMeansStockAndNoTask()
    {
        var (part, _, host, runner) = Make(failAt: 5);
        var state = runner.Run();
        Assert.Equal(0, state.Applied);
        Assert.Equal(0, part.Now);
        Assert.Null(state.SignIn);
        Assert.True(host.Reapplied > 0);
    }

    [Fact]
    public void AVendorsOneStepTuningNeedsNoBackOff()
    {
        var (part, _, _, runner) = Make(limits: new TuneLimits(1, 1, 0, 95, 60, 600, 180));
        var state = runner.Run();
        Assert.Equal(1, state.Applied);
        Assert.Equal(1, part.Now);
    }

    [Fact]
    public void ADriverRefusingAtTheFirstCallSaysSoInItsOwnWords()
    {
        var part = new RefusingPart();
        var state = new TuneRunner(part, new FakeTest(new FakePart(_store, Limits)), new FakeHost(), _store, "cooler").Run();
        Assert.Equal("stopped", state.Phase);
        Assert.Equal("This driver says no.", state.Note);
    }

    private sealed class RefusingPart : ITunePart
    {
        public string Id => "part";
        public TuneLimits Limits => AutoTuneTests.Limits;
        public void Settle(TuneRun stock) { }
        public void Apply(int depth) => throw new TuneDriverException("This driver says no.");
        public int? Read() => null;
        public void Stock() => throw new TuneDriverException("This driver says no.");
        public int[]? SignIn(int depth) => null;
    }

    [Fact]
    public void WhatsShownIsWhatWasMeasured()
    {
        var (_, test, _, runner) = Make(failAt: 20);
        test.Score = depth => depth == 0 ? 100 : 97;
        var state = runner.Run();
        Assert.Equal(100, state.Before!.Score);
        Assert.Equal(97, state.After!.Score);
        Assert.Equal(60, state.Before.PeakC);
        Assert.Equal(58, state.After.PeakC);
    }

    [Fact]
    public void UndoPutsStockBackAndKeepsWhatFailed()
    {
        var (part, _, host, runner) = Make(failAt: 20);
        runner.Run();
        var before = host.Reapplied;
        var state = runner.Undo();
        Assert.Equal(0, part.Now);
        Assert.Equal(0, state.Applied);
        Assert.Null(state.SignIn);
        Assert.Contains(20, state.Failed);
        Assert.True(host.Reapplied > before);
    }

    [Theory]
    [InlineData("AMD64 Family 25 Model 33 Stepping 2", "AMD Ryzen 5 5600X 6-Core Processor", 3)]
    [InlineData("AMD64 Family 25 Model 97 Stepping 2", "AMD Ryzen 7 7800X3D 8-Core Processor", 4)]
    [InlineData("AMD64 Family 26 Model 68 Stepping 0", "AMD Ryzen 9 9950X 16-Core Processor", 5)]
    public void SupportedRyzens(string identifier, string name, int zen) => Assert.Equal(zen, (int?)TuneRyzen.Generation(identifier, name));

    [Theory]
    [InlineData("AMD64 Family 25 Model 33 Stepping 2", "AMD Ryzen 7 5800X3D 8-Core Processor")] // AMD locks it.
    [InlineData("AMD64 Family 25 Model 80 Stepping 0", "AMD Ryzen 7 5700G")] // An APU's mailboxes are others.
    [InlineData("AMD64 Family 23 Model 113 Stepping 0", "AMD Ryzen 5 3600")]
    [InlineData("Intel64 Family 6 Model 183 Stepping 1", "Intel Core i9-13900K")]
    [InlineData(null, null)]
    [InlineData("nonsense", "")]
    public void EverythingElseIsNot(string? identifier, string? name) => Assert.Null(TuneRyzen.Generation(identifier, name));

    [Fact]
    public void TheTemperatureRegisterReadsAsLinuxReadsIt()
    {
        Assert.Equal(50, TuneRyzen.Temperature(400u << 21));
        Assert.Equal(1, TuneRyzen.Temperature((400u << 21) | (1u << 19)));
    }

    [Fact]
    public void TheScriptChecksBeforeItApplies()
    {
        var script = TuneSignIn.Script([120, 1935], [15], @"C:\Program Files\ExoLauncher Tune", @"C:\Users\O'Neil\Exo.exe");
        var apply = script.IndexOf("[ExoTune]::Nvidia(120, 1935)", StringComparison.Ordinal);
        Assert.True(apply > 0);
        Assert.Contains("[ExoTune]::Ryzen(15,", script);
        Assert.Contains("O''Neil", script);
        Assert.Contains("ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 41", script);
        Assert.DoesNotContain("'Microsoft-Kernel-Power'", script);
        Assert.Contains("last-run.txt", script);
        var addType = script.IndexOf("Add-Type", StringComparison.Ordinal);
        var markApplying = script.IndexOf("Set-Content -LiteralPath \"$d\\applying\"", StringComparison.Ordinal);
        Assert.InRange(addType, 0, markApplying);
        foreach (var check in new[] { "testing.json", "\\held\")", "\\applying\")", "Id = 41", "Id = 18", "Set-Content -LiteralPath \"$d\\applying\"" })
            Assert.InRange(script.IndexOf(check, StringComparison.Ordinal), 0, apply);
        Assert.True(script.IndexOf("Start-Sleep -Seconds 120", StringComparison.Ordinal) > apply);
    }

    [Fact]
    public void ACrashOrFatalErrorSinceTheApplyHoldsButACorrectedOneDoesNot()
    {
        var script = TuneSignIn.Script([120, 0], null, @"C:\t", @"C:\e.exe");
        Assert.Contains("ProviderName = 'Microsoft-Windows-WHEA-Logger'; Id = 18", script);
        Assert.Contains("'power-loss'", script);
        Assert.Contains("'whea-fatal'", script);
        Assert.DoesNotContain("Id = @(", script);
        Assert.DoesNotContain("Id = 19", script);
        Assert.DoesNotContain("Id = 20", script);
    }

    [Fact]
    public void AScriptErrorNotesItAndNeverHolds()
    {
        var script = TuneSignIn.Script([120, 0], null, @"C:\t", @"C:\e.exe");
        var catchAt = script.LastIndexOf("} catch {", StringComparison.Ordinal);
        var exitAt = script.IndexOf("exit", catchAt, StringComparison.Ordinal);
        Assert.True(catchAt > 0 && exitAt > catchAt);
        var handler = script[catchAt..exitAt];
        Assert.Contains("Remove-Item -LiteralPath \"$d\\applying\"", handler);
        Assert.Contains("Note ('error: '", handler);
        Assert.DoesNotContain("held", handler);
    }

    [Fact]
    public void AnNvidiaApplyKeepsItsOffsetFromBeingDroppedAndLogsEachPutBack()
    {
        var nvidia = TuneSignIn.Script([120, 1935], null, @"C:\t", @"C:\e.exe");
        Assert.Contains("[ExoTune]::Keep(120, 1935, \"$d\\keep\", \"$d\\last-run.txt\")", nvidia);
        Assert.True(nvidia.IndexOf("[ExoTune]::Keep(", StringComparison.Ordinal) > nvidia.IndexOf("Note 'applied'", StringComparison.Ordinal));
        var ryzenOnly = TuneSignIn.Script(null, [10], @"C:\t", @"C:\e.exe");
        Assert.DoesNotContain("[ExoTune]::Keep(", ryzenOnly);
    }

    [Fact]
    public void TheScriptLeavesOutAPartWithNothingApplied()
    {
        var script = TuneSignIn.Script(null, [10], "C:\\t", "C:\\e.exe");
        Assert.DoesNotContain("[ExoTune]::Nvidia(", script);
    }

    [Fact]
    public void TheScriptRefusesTheWrongShape() => Assert.Throws<ArgumentException>(() => TuneSignIn.Script([1], null, "C:\\t", "C:\\e.exe"));

    [Fact]
    public void TheTaskRunsAsSystemOnlyAtSignInAndOnWake()
    {
        var xml = TuneSignIn.TaskXml(@"C:\Program Files\ExoLauncher Tune\apply.ps1");
        Assert.Contains("S-1-5-18", xml);
        Assert.Contains("<LogonTrigger>", xml);
        Assert.Contains("Power-Troubleshooter", xml);
        Assert.Contains("EventID=1]", xml);
        Assert.DoesNotContain("<BootTrigger>", xml);
        Assert.Contains("IgnoreNew", xml);
        Assert.Contains("<ExecutionTimeLimit>PT5M</ExecutionTimeLimit>", xml);
    }

    [Fact]
    public void TheNvidiaHolderIsTheOnlyTaskWithNoTimeLimit()
    {
        var xml = TuneSignIn.TaskXml(@"C:\t\apply.ps1", keepsRunning: true);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        Assert.DoesNotContain("PT5M", xml);
    }

    [Fact]
    public void ReleasingTheHolderRemovesItsFlagBeforeAnythingTouchesTheCard()
    {
        Directory.CreateDirectory(_folder);
        var keep = Path.Combine(_folder, TuneSignIn.KeepFile);
        File.WriteAllText(keep, "");
        TuneSignIn.ReleaseKeep(_folder, TimeSpan.Zero);
        Assert.False(File.Exists(keep));
        TuneSignIn.ReleaseKeep(_folder, TimeSpan.Zero);
        Assert.False(File.Exists(keep));
    }

    [Fact]
    public void TheAppLogLineIsTheSignInScriptsLastLine()
    {
        Directory.CreateDirectory(_folder);
        Assert.Null(TuneSignIn.LastRun(_folder));
        File.WriteAllText(Path.Combine(_folder, TuneSignIn.LastRunFile), "2026-11-22T09:00:00 applied\n2026-11-23T09:00:00 held: the PC lost power or crashed since the apply\n\n");
        Assert.Equal("2026-11-23T09:00:00 held: the PC lost power or crashed since the apply", TuneSignIn.LastRun(_folder));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WindowsAcceptsTheEventQuery(bool gpu) => Assert.True(TuneEvents.Parses(TuneEvents.Query(gpu)));

    [Fact]
    public void TheCpuLoadGivesTheSameAnswerEachTime()
    {
        var load = new CpuLoad(8, 1 << 8);
        Assert.Equal(load.Pass(), load.Pass());
        Assert.NotEqual(load.Pass(), new CpuLoad(9, 1 << 8).Pass());
    }

    [Fact]
    public void TheGpuLoadWorksOnSoftwareDirect3D()
    {
        // WARP: the real calls, shader and hash, with no GPU touched. One tiny pass.
        using var load = new GpuLoad(0, 2, 4);
        Assert.Equal(load.Pass(), load.Pass());
    }

    [Theory]
    [InlineData("nvidia")]
    [InlineData("radeon")]
    [InlineData("ryzen")]
    public void TheFirstRunWarnsOfWarrantyAndCrashes(string id)
    {
        var warning = TuneView.Warning(id, 40);
        Assert.Contains("warranty", warning);
        Assert.Contains("crash", warning);
        Assert.Contains("save your work", warning);
        Assert.Contains("40 minutes", warning);
    }

    [Fact]
    public void TheDriverRowSaysWhatItCostsUpFront()
    {
        Assert.Contains("FACEIT", TuneView.NeedsDriver);
        Assert.Contains("stays loaded", TuneView.NeedsDriver);
    }

    [Fact]
    public void PreviewPartsStaysVisibleWithoutTouchingDrivers()
    {
        Assert.Equal(["nvidia", "ryzen"], AutoTuneService.PreviewParts(
            ["NVIDIA GeForce RTX 3070"],
            "AMD64 Family 25 Model 33 Stepping 2",
            "AMD Ryzen 5 5600X 6-Core Processor",
            isLaptop: false));

        Assert.Equal(["radeon"], AutoTuneService.PreviewParts(
            ["AMD Radeon RX 7800 XT"],
            "Intel64 Family 6 Model 183 Stepping 1",
            "Intel Core i9-13900K",
            isLaptop: false));

        Assert.Empty(AutoTuneService.PreviewParts(
            ["Intel(R) Arc(TM) A770 Graphics"],
            "AMD64 Family 25 Model 33 Stepping 2",
            "AMD Ryzen 7 5800X3D 8-Core Processor",
            isLaptop: false));

        Assert.Empty(AutoTuneService.PreviewParts(
            ["NVIDIA GeForce RTX 4070 Laptop GPU"],
            "AMD64 Family 25 Model 97 Stepping 2",
            "AMD Ryzen 9 7945HX",
            isLaptop: true));
    }

    [Fact]
    public void TuneViewBlocksStartWhileGameRunsAndForcesFasterMode()
    {
        var saved = new TunePartState { Phase = "idle", Mode = "cooler" };
        var gpu = System.Text.Json.JsonSerializer.SerializeToElement(TuneView.Of("nvidia", saved, driverReady: true, cores: 0, gameRunning: true));
        Assert.Equal("ready", gpu.GetProperty("state").GetString());
        Assert.False(gpu.GetProperty("modes").GetBoolean());
        Assert.Equal("faster", gpu.GetProperty("mode").GetString());
        Assert.Equal("Close your game first.", gpu.GetProperty("blocked").GetString());

        var cpu = System.Text.Json.JsonSerializer.SerializeToElement(TuneView.Of("ryzen", saved, driverReady: false, cores: 6, gameRunning: true));
        Assert.Equal("needs-driver", cpu.GetProperty("state").GetString());
        Assert.False(cpu.GetProperty("modes").GetBoolean());
        Assert.Equal("faster", cpu.GetProperty("mode").GetString());
        Assert.Equal("Close your game first.", cpu.GetProperty("blocked").GetString());
    }
}

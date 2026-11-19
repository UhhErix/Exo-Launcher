using System.IO.Compression;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the FPS counter can fail silently:
/// - it counts more than a second, or reads a slow frame rate as fast because only the frames after a hitch are in view;
/// - its digits jitter on a steady game, or crawl down after a real drop;
/// - a stalled game keeps showing its last number instead of 0;
/// - ETW's frames arrive out of order or the ring wraps, and the count goes wrong or throws;
/// - 1% low is the average, or claims a number from a handful of frames;
/// - another process's presents are counted, or two of the game's processes are added together;
/// - a frame is counted twice: DXGI and the kernel both report it, or the kernel reports one flip per plane;
/// - a Vulkan or OpenGL game (kernel events only) reads 0;
/// - DXGI_PRESENT_TEST, which shows no frame, counts as one; the wrong event ids count;
/// - the callback allocates, on a path run for every present on the PC;
/// - the native structs don't match Windows' layout, which would crash or read garbage;
/// - the elevated line takes anything that isn't a SID, or gives more than this session and its providers;
/// - the kernel's events stay on for a game that doesn't need them, or never come on for one that does;
/// - 1% low costs anything with nothing showing it;
/// - an item shows a value Windows didn't give, shows out of order, or makes the line run off the screen;
/// - the game's tree takes in anti-cheat, a store, or Exo; or loses the game after a launcher hands off;
/// - the capsule shows over something that isn't the game, jitters in width, or leaves a gap at the screen's edge at some
///   display scale, or rounds a corner that touches the edge;
/// - its pixels aren't premultiplied (a layered window then draws them too bright).
/// </summary>
public sealed class FpsCounterTests
{
    private const long Frequency = 10_000_000;

    private static long Seconds(double seconds) => (long)(seconds * Frequency);

    private static FpsWindow Frames(double fps, double seconds, out long last, double start = 100)
    {
        var window = new FpsWindow(Frequency);
        last = 0;
        for (var i = 0; i < fps * seconds; i++) window.Add(last = Seconds(start + i / fps));
        return window;
    }

    // ---------------------------------------------------------------- the window

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void SteadyFrameRateReadsExactly(int fps)
    {
        var window = Frames(fps, 5, out var last);
        Assert.Equal(fps, window.Read(last).Fps);
    }

    [Fact]
    public void OnlyTheLastSecondCounts()
    {
        var window = new FpsWindow(Frequency);
        long t = 0;
        for (var i = 0; i < 600; i++) window.Add(t = Seconds(100 + i / 200.0));
        for (var i = 1; i <= 120; i++) window.Add(t += Seconds(1.0 / 60));
        Assert.Equal(60, window.Read(t).Fps);
    }

    [Fact]
    public void AHitchInsideTheSecondCounts()
    {
        var window = new FpsWindow(Frequency);
        long t = Seconds(100);
        for (var i = 0; i < 300; i++) window.Add(t += Seconds(1.0 / 100));
        t += Seconds(0.9);
        for (var i = 0; i < 10; i++) window.Add(t += Seconds(1.0 / 100));
        // Eleven frames over the second, not the 100 the last ten alone would say.
        Assert.InRange(window.Read(t).Fps, 10, 12);
    }

    [Fact]
    public void WobbleHoldsTheDigitsStill()
    {
        var window = new FpsWindow(Frequency);
        long t = Seconds(100);
        var shown = new HashSet<int>();
        for (var tick = 0; tick < 40; tick++)
        {
            // Half a second at a rate wobbling either side of 144, with each frame's timing a little off.
            var fps = tick % 2 == 0 ? 143.0 : 145.0;
            for (var i = 0; i < fps / 2; i++) window.Add(t += Seconds((1 + (i % 2 == 0 ? 0.03 : -0.03)) / fps));
            if (tick > 3) shown.Add(window.Read(t).Fps);
        }
        Assert.Single(shown);
        Assert.InRange(shown.Single(), 143, 145);
    }

    [Fact]
    public void ARealDropLandsAtOnce()
    {
        var window = Frames(144, 5, out var t);
        Assert.Equal(144, window.Read(t).Fps);
        for (var i = 0; i < 120; i++) window.Add(t += Seconds(1.0 / 60));
        Assert.Equal(60, window.Read(t).Fps);
    }

    [Fact]
    public void AStalledGameReadsZeroNotItsLastNumber()
    {
        var window = Frames(144, 5, out var last);
        Assert.Equal(144, window.Read(last).Fps);
        Assert.Equal(144, window.Read(last + Seconds(1.5)).Fps);
        var stalled = window.Read(last + Seconds(FpsWindow.StallSeconds + 0.1));
        Assert.Equal(0, stalled.Fps);
        Assert.True(stalled.Seen);
        // And it comes back at once when frames do.
        var t = last + Seconds(5);
        for (var i = 0; i < 120; i++) window.Add(t += Seconds(1.0 / 60));
        Assert.Equal(60, window.Read(t).Fps);
    }

    [Fact]
    public void NoFramesIsNotSeenAndALoneFrameSaysNothing()
    {
        var window = new FpsWindow(Frequency);
        Assert.Equal(new FpsReading(0, null, false), window.Read(Seconds(1)));
        window.Add(Seconds(1));
        var one = window.Read(Seconds(1));
        Assert.Equal(0, one.Fps);
        Assert.True(one.Seen);
    }

    [Fact]
    public void FramesOutOfOrderReadTheSame()
    {
        var window = new FpsWindow(Frequency);
        long newest = 0;
        for (var i = 0; i < 360; i += 2)
        {
            window.Add(Seconds(100 + (i + 1) / 60.0));
            window.Add(Seconds(100 + i / 60.0));
            newest = Seconds(100 + (i + 1) / 60.0);
        }
        Assert.Equal(60, window.Read(newest).Fps);
    }

    [Fact]
    public void TheRingWrapsWithoutLosingTheCount()
    {
        var window = new FpsWindow(Frequency);
        long t = Seconds(100);
        for (var i = 0; i < FpsWindow.Capacity * 3 + 17; i++) window.Add(t += Seconds(1.0 / 500));
        Assert.Equal(500, window.Read(t).Fps);
    }

    [Fact]
    public void OnePercentLowIsTheSlowestFramesNotTheAverage()
    {
        var window = new FpsWindow(Frequency);
        long t = Seconds(100);
        // 990 frames at 10 ms, 10 at 50 ms: 1% of them run at 20 FPS.
        for (var i = 0; i < 1000; i++) window.Add(t += Seconds(i % 100 == 0 ? 0.050 : 0.010));
        Assert.Equal(20, window.Read(t, withLow: true).Low);
    }

    [Fact]
    public void OnePercentLowWaitsForEnoughFrames()
    {
        var window = Frames(60, 1, out var last);
        Assert.Null(window.Read(last, withLow: true).Low);
        Assert.NotNull(Frames(60, 5, out last).Read(last, withLow: true).Low);
    }

    [Fact]
    public void OnePercentLowCostsNothingWithNothingShowingIt()
    {
        var window = Frames(144, 5, out var last);
        Assert.Null(window.Read(last).Low);
        window.Read(last);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) window.Read(last);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---------------------------------------------------------------- which presents count

    [Theory]
    [InlineData(0xCA11C036u, (ushort)42, (int)PresentKind.Api)]
    [InlineData(0xCA11C036u, (ushort)55, (int)PresentKind.Api)]
    [InlineData(0xCA11C036u, (ushort)43, (int)PresentKind.None)]
    [InlineData(0x783ACA0Au, (ushort)1, (int)PresentKind.Api)]
    [InlineData(0x783ACA0Au, (ushort)2, (int)PresentKind.None)]
    [InlineData(0x802EC45Au, (ushort)168, (int)PresentKind.Kernel)]
    [InlineData(0x802EC45Au, (ushort)166, (int)PresentKind.Kernel)]
    [InlineData(0x802EC45Au, (ushort)252, (int)PresentKind.Kernel)]
    [InlineData(0x802EC45Au, (ushort)42, (int)PresentKind.None)]
    [InlineData(0x68FDD900u, (ushort)0, (int)PresentKind.None)]
    public void EventsCountByProviderAndId(uint provider, ushort id, int kind) => Assert.Equal((PresentKind)kind, PresentTrace.Classify(provider, id));

    [Fact]
    public void ATestPresentIsNoFrame()
    {
        Assert.True(PresentTrace.OnlyTests(0x1));
        Assert.False(PresentTrace.OnlyTests(0x0));
        Assert.False(PresentTrace.OnlyTests(0x200));
    }

    private static PresentSlots Slots(params int[] pids)
    {
        var slots = new PresentSlots(Frequency);
        slots.Watch(pids);
        return slots;
    }

    private static long Feed(PresentSlots slots, int pid, PresentKind kind, double fps, double seconds = 3, double start = 100)
    {
        long last = 0;
        for (var i = 0; i < fps * seconds; i++) slots.Record(pid, kind, last = Seconds(start + i / fps));
        return last;
    }

    [Fact]
    public void AnotherProcessIsNotCounted()
    {
        var slots = Slots(10);
        var last = Feed(slots, 99, PresentKind.Api, 500);
        Assert.Equal(new FpsReading(0, null, false), slots.Read(last, 10));
    }

    [Fact]
    public void ApiPresentsAreNotCountedTwiceByTheKernel()
    {
        var slots = Slots(10);
        long t = Seconds(100);
        for (var i = 0; i < 360; i++)
        {
            t += Seconds(1.0 / 120);
            slots.Record(10, PresentKind.Api, t);
            slots.Record(10, PresentKind.Kernel, t + Seconds(0.001));
        }
        Assert.Equal(120, slots.Read(t, 10).Fps);
    }

    [Fact]
    public void AVulkanGameCountsByTheKernel()
    {
        var slots = Slots(10);
        var last = Feed(slots, 10, PresentKind.Kernel, 90);
        Assert.Equal(90, slots.Read(last, 10).Fps);
    }

    [Fact]
    public void OneFlipPerPlaneIsOneFrame()
    {
        var slots = Slots(10);
        long t = Seconds(100);
        for (var i = 0; i < 360; i++)
        {
            t += Seconds(1.0 / 120);
            slots.Record(10, PresentKind.Kernel, t);
            slots.Record(10, PresentKind.Kernel, t + 10);
        }
        Assert.Equal(120, slots.Read(t, 10).Fps);
    }

    [Fact]
    public void TheGameInFrontWinsAndRatesAreNeverAdded()
    {
        var slots = Slots(10, 11);
        Feed(slots, 10, PresentKind.Api, 30);
        var last = Feed(slots, 11, PresentKind.Api, 60);
        Assert.Equal(30, slots.Read(last, 10).Fps);
        Assert.Equal(60, slots.Read(last, 11).Fps);
        // Nothing of the game in front: whichever presents the most, never their sum.
        Assert.Equal(60, slots.Read(last, 12).Fps);
    }

    [Fact]
    public void AfterAPauseFramesFromBeforeItAreForgotten()
    {
        var slots = Slots(10);
        var last = Feed(slots, 10, PresentKind.Api, 60);
        Assert.True(slots.ApiLately(last));
        slots.Forget();
        Assert.Equal(new FpsReading(0, null, false), slots.Read(last, 10));
        Assert.False(slots.ApiLately(last));
    }

    [Fact]
    public void AProcessKeptKeepsItsFrames()
    {
        var slots = Slots(10);
        var last = Feed(slots, 10, PresentKind.Api, 60);
        slots.Watch([10, 11]);
        Assert.Equal(60, slots.Read(last, 10).Fps);
        slots.Watch([11]);
        Assert.False(slots.Read(last, 11).Seen);
    }

    [Fact]
    public void RecordingAllocatesNothing()
    {
        var slots = Slots(10, 11, 12);
        Feed(slots, 12, PresentKind.Api, 10);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            slots.Record(99, PresentKind.Api, i);
            slots.Record(12, PresentKind.Api, Seconds(200) + i);
            slots.Record(11, PresentKind.Kernel, Seconds(200) + i * 1000L);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public unsafe void NativeLayoutsMatchWindows()
    {
        Assert.Equal(448, sizeof(PresentTrace.EventTraceLogfile));
        Assert.Equal(112, sizeof(PresentTrace.EventRecord));
        Assert.Equal(120, PresentTrace.TraceProperties.NameOffset);
        Assert.Equal(424, (int)System.Runtime.InteropServices.Marshal.OffsetOf<PresentTrace.EventTraceLogfile>(nameof(PresentTrace.EventTraceLogfile.EventRecordCallback)));
        Assert.Equal(28, (int)System.Runtime.InteropServices.Marshal.OffsetOf<PresentTrace.EventTraceLogfile>(nameof(PresentTrace.EventTraceLogfile.ProcessTraceMode)));
        Assert.Equal(96, (int)System.Runtime.InteropServices.Marshal.OffsetOf<PresentTrace.EventRecord>(nameof(PresentTrace.EventRecord.UserData)));
    }

    // ---------------------------------------------------------------- permission

    [Fact]
    public void TheFallbackNamesTheGroupAndUserBySidOnly()
    {
        var line = PresentTrace.AddToGroupCommand("S-1-5-21-1-2-3-1001")!;
        Assert.Contains("Add-LocalGroupMember -SID S-1-5-32-559 -Member S-1-5-21-1-2-3-1001", line);
        Assert.Contains("powershell.exe", line);
    }

    [Fact]
    public void TheRightsGoToThisUserOnThisSessionAndTheseProvidersOnly()
    {
        var script = PresentTrace.AddRightsScript("S-1-5-21-1-2-3-1001");
        Assert.Contains("S-1-5-21-1-2-3-1001", script);
        Assert.Contains(PresentTrace.SessionGuid.ToString(), script);
        foreach (var provider in (string[])["ca11c036-0102-4a2d-a6ad-f03cfed5d3c9", "783aca0a-790e-4d7f-8451-aa850511c6b9", "802ec45a-1e99-4b83-9920-87c98277ba9d"])
            Assert.Contains(provider, script, StringComparison.OrdinalIgnoreCase);
        // It adds to what is there (EventSecurityAddDACL, 2); 0 would replace it.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(script, @"EventAccessControl\(\[ref\]\$id, 2, \$bytes").Count);
        Assert.DoesNotContain("0811c1af", script, StringComparison.OrdinalIgnoreCase);
        // The elevated line carries exactly that script.
        var line = PresentTrace.AddRightsCommand("S-1-5-21-1-2-3-1001")!;
        Assert.Equal(script, System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(line[(line.LastIndexOf(' ') + 1)..])));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Erix")]
    [InlineData("S-1-5-21 & calc")]
    [InlineData("S-1-5-21\"; rm x")]
    [InlineData("S-1-5-21-1\n")]
    public void TheElevatedLinesTakeNothingButASid(string? user)
    {
        Assert.Null(PresentTrace.AddToGroupCommand(user));
        Assert.Null(PresentTrace.AddRightsCommand(user));
    }

    [Theory]
    [InlineData(false, 3.0, true)]
    [InlineData(false, 1.0, false)]
    [InlineData(true, 3.0, false)]
    [InlineData(true, 1.0, false)]
    public void TheKernelsEventsComeOnOnlyForAGameThatNeedsThem(bool apiLately, double seconds, bool wanted) => Assert.Equal(wanted, PresentTrace.WantsKernel(apiLately, seconds));

    // ---------------------------------------------------------------- what can be added

    [Fact]
    public void ItemsAreOnlyWhatExoKnowsOnceEachInTheirOrder()
    {
        Assert.Equal([FpsItems.Low, FpsItems.GpuUse, FpsItems.Clock], FpsItems.Clean([FpsItems.Clock, "nonsense", FpsItems.Low, FpsItems.GpuUse, FpsItems.Low]));
        Assert.Empty(FpsItems.Clean(null));
        Assert.Empty(FpsItems.Clean([]));
    }

    private static readonly DateTime Noon = new(2026, 11, 2, 14, 32, 0);
    private static readonly FpsItems.Sample Sample = new(98, 71, 34, 6.1, 18.2);

    [Fact]
    public void NothingSwitchedOnIsJustTheFps() => Assert.Null(FpsItems.Text([], 142, 96, Sample, Noon));

    [Fact]
    public void EveryItemHasItsShortLabelInOrder()
    {
        var all = FpsItems.Clean([FpsItems.Low, FpsItems.FrameTime, FpsItems.GpuUse, FpsItems.GpuTemperature, FpsItems.CpuUse, FpsItems.Vram, FpsItems.Ram]);
        Assert.Equal(" \u00B7 96 LOW \u00B7 7.0 MS \u00B7 98% GPU \u00B7 71\u00B0C \u00B7 34% CPU \u00B7 6.1 VRAM \u00B7 18.2 RAM", FpsItems.Text(all, 142, 96, Sample, Noon));
    }

    [Fact]
    public void AValueWindowsDidntGiveIsLeftOutNotGuessed()
    {
        var all = FpsItems.Clean([FpsItems.Low, FpsItems.GpuUse, FpsItems.GpuTemperature, FpsItems.CpuUse, FpsItems.Vram, FpsItems.Ram]);
        Assert.Null(FpsItems.Text(all, 142, null, default, Noon));
        Assert.Null(FpsItems.Text([FpsItems.FrameTime], 0, null, default, Noon));
        Assert.Equal(" \u00B7 71\u00B0C", FpsItems.Text(all, 142, null, new(null, 71, null, null, null), Noon));
    }

    [Fact]
    public void OnlyTheItemsThatNeedTheCardOpenIt()
    {
        Assert.False(FpsItems.NeedsGpu([FpsItems.Low, FpsItems.FrameTime, FpsItems.CpuUse, FpsItems.Ram, FpsItems.Clock]));
        Assert.True(FpsItems.NeedsGpu([FpsItems.GpuUse]));
        Assert.True(FpsItems.NeedsGpu([FpsItems.GpuTemperature]));
        Assert.True(FpsItems.NeedsGpu([FpsItems.Vram]));
    }

    [Theory]
    [InlineData(true, false, 500u)]
    [InlineData(true, true, 2000u)]
    [InlineData(false, false, 0u)]
    [InlineData(false, true, 0u)]
    public void AwayFromTheGameItWaitsForTheFrontToChange(bool front, bool exclusive, uint ms) => Assert.Equal(ms, FpsCounter.LookAfter(front, exclusive));

    // ---------------------------------------------------------------- the game's tree

    [Fact]
    public void TheTreeFollowsWhatTheGameStartedAndLeavesOutTheRest()
    {
        (int, int, string)[] table =
        [
            (100, 1, "launcher_game.exe"),
            (101, 100, "game.exe"),
            (102, 101, "EasyAntiCheat.exe"),
            (103, 102, "under_anticheat.exe"),
            (104, 101, "steam.exe"),
            (105, 101, "exo.exe"),
            (200, 1, "other.exe"),
        ];
        Assert.Equal([100, 101], GameProcessRegistry.GameTree([100], table, self: 105).Order());
    }

    [Fact]
    public void AGameWhoseLauncherLeftIsItsOwnRoot()
    {
        (int, int, string)[] table = [(101, 100, "game.exe"), (102, 101, "gamehelper.exe")];
        Assert.Equal([101, 102], GameProcessRegistry.GameTree([100, 101], table, self: 1).Order());
    }

    // ---------------------------------------------------------------- the capsule

    [Theory]
    [InlineData("left", 0)]
    [InlineData("middle", 910)]
    [InlineData("right", 1820)]
    public void WithNoMarginItTouchesTheTopAndItsEdgeOfTheScreen(string position, int x) => Assert.Equal((x, 0), FpsOverlay.Place(position, 0, 0, 1920, 100));

    [Theory]
    [InlineData("left", 8)]
    [InlineData("middle", 910)]
    [InlineData("right", 1812)]
    public void ItSitsInOffTheCornerByItsMargin(string position, int x) => Assert.Equal((x, 8), FpsOverlay.Place(position, 0, 0, 1920, 100, FpsOverlay.MarginAt(1)));

    [Theory]
    [InlineData(1.0, 8)]
    [InlineData(1.25, 10)]
    [InlineData(1.5, 12)]
    [InlineData(2.0, 16)]
    public void TheMarginGrowsWithTheScreensScale(double scale, int margin) => Assert.Equal(margin, FpsOverlay.MarginAt(scale));

    [Fact]
    public void ItFollowsTheScreensRealBounds()
    {
        Assert.Equal((2560, 0), FpsOverlay.Place("left", 2560, 0, 6400, 200));
        Assert.Equal((6400 - 200, 0), FpsOverlay.Place("right", 2560, 0, 6400, 200));
        Assert.Equal((-1920 + 860, -200), FpsOverlay.Place("middle", -1920, -200, 0, 200));
    }

    [Theory]
    [InlineData(null, "right")]
    [InlineData("", "right")]
    [InlineData("nonsense", "right")]
    [InlineData("right", "right")]
    [InlineData("left", "left")]
    [InlineData("middle", "middle")]
    public void APositionIsOneOfTheThree(string? value, string position) => Assert.Equal(position, FpsOverlay.PositionOf(value));

    [Fact]
    public void ItsWidthOnlyGrowsUntilTheTextsPartsChange()
    {
        FpsOverlay.Line was = new(144, " \u00B7 100% GPU");
        Assert.Equal(90, FpsOverlay.MinWidth(90, was, new(99, " \u00B7 7% GPU")));
        Assert.Equal(90, FpsOverlay.MinWidth(90, new(144, null), new(99, null)));
        Assert.Equal(0, FpsOverlay.MinWidth(90, was, new(99, " \u00B7 7% CPU")));
        Assert.Equal(0, FpsOverlay.MinWidth(90, was, new(99, null)));
        Assert.Equal(0, FpsOverlay.MinWidth(90, null, new(99, null)));
    }

    [Fact]
    public void WhatDoesntFitGoesWholeFromTheEnd()
    {
        static int Width(string? text) => text?.Length ?? 0;
        const string Items = " \u00B7 96 LOW \u00B7 7.0 MS \u00B7 98% GPU";
        Assert.Equal(Items, FpsOverlay.Fit(Items, Width, 100));
        Assert.Equal(" \u00B7 96 LOW \u00B7 7.0 MS", FpsOverlay.Fit(Items, Width, 18));
        Assert.Equal(" \u00B7 96 LOW", FpsOverlay.Fit(Items, Width, 9));
        Assert.Null(FpsOverlay.Fit(Items, Width, 3));
        Assert.Null(FpsOverlay.Fit(null, Width, 3));
    }

    [Fact]
    public void ALineThatWontFitNeverRunsOffTheScreen()
    {
        FpsOverlay.AddFont(Font());
        const string Items = " \u00B7 96 LOW \u00B7 7.0 MS \u00B7 98% GPU \u00B7 71\u00B0C \u00B7 34% CPU \u00B7 6.1 VRAM \u00B7 18.2 RAM \u00B7 14:32";
        using var full = FpsOverlay.Draw(new(142, Items), 2);
        using var narrow = FpsOverlay.Draw(new(142, Items), 2, 0, 400);
        using var tiny = FpsOverlay.Draw(new(142, Items), 2, 900, 300);
        Assert.True(full.Width > 400);
        Assert.InRange(narrow.Width, 1, 400);
        Assert.InRange(tiny.Width, 1, 300);
    }

    [Fact]
    public void EveryDigitInTheLineHoldsItsWidth()
    {
        FpsOverlay.AddFont(Font());
        var widths = new HashSet<int>();
        foreach (var (fps, gpu) in new[] { (111, 11), (888, 88), (147, 40), (900, 71) })
        {
            using var capsule = FpsOverlay.Draw(new(fps, $" \u00B7 {gpu}% GPU"), 1);
            widths.Add(capsule.Width);
        }
        Assert.Single(widths);
    }

    private static unsafe uint[] Pixels(FpsOverlay.Capsule capsule) => new ReadOnlySpan<uint>(capsule.Bits, capsule.Width * capsule.Height).ToArray();

    private static string Font() => Repo.PathOf("ExoLauncher", "Assets", "Fonts", "Exoline-Variable.ttf");

    [Theory]
    [InlineData(1.0, 24)]
    [InlineData(1.25, 30)]
    [InlineData(1.5, 36)]
    [InlineData(1.75, 42)]
    [InlineData(2.0, 48)]
    public void ThePixelsArePremultipliedOverItsBlackPlateAndItIsAPill(double scale, int height)
    {
        FpsOverlay.AddFont(Font());
        using var capsule = FpsOverlay.Draw(new(142, null), scale);
        Assert.Equal(height, capsule.Height);
        var pixels = Pixels(capsule);
        var (w, h) = (capsule.Width, capsule.Height);
        foreach (var pixel in pixels)
        {
            var (a, r, g, b) = (pixel >> 24, pixel >> 16 & 0xFF, pixel >> 8 & 0xFF, pixel & 0xFF);
            Assert.True(r <= a && g <= a && b <= a, $"not premultiplied: {pixel:X8}");
            Assert.True(r == g && g == b, $"not white on black: {pixel:X8}");
        }
        // A pill: all four corners are clear, and its top and bottom rows are whole between the round ends.
        foreach (var corner in (int[])[0, w - 1, (h - 1) * w, h * w - 1]) Assert.Equal(0u, pixels[corner] >> 24);
        for (var x = h / 2; x < w - h / 2; x++) Assert.True(pixels[x] >> 24 >= 199 && pixels[(h - 1) * w + x] >> 24 >= 199, $"gap in the plate at {x}");
        // Its plain body is black at 78%; the number is full white somewhere.
        Assert.Equal(0xC7000000u, pixels[w + w / 2]);
        Assert.Contains(pixels, pixel => (pixel & 0xFF) > 230);
    }

    [Theory]
    [InlineData(0, 0, 0.0)]
    [InlineData(99, 0, 0.0)]
    [InlineData(0, 23, 0.0)]
    [InlineData(99, 23, 0.0)]
    [InlineData(50, 0, 1.0)]
    [InlineData(50, 23, 1.0)]
    [InlineData(1, 12, 1.0)]
    [InlineData(98, 11, 1.0)]
    [InlineData(12, 0, 1.0)]
    public void EveryCornerIsRoundedIntoAPill(int column, int row, double cover) =>
        Assert.Equal(cover, FpsOverlay.Cover(100, 24, column, row), 3);

    [Fact]
    public void ADigitsWidthNeverChangesTheCapsule()
    {
        FpsOverlay.AddFont(Font());
        var widths = new HashSet<int>();
        foreach (var fps in (int[])[100, 111, 144, 188, 777, 999])
        {
            using var capsule = FpsOverlay.Draw(new(fps, null), 1);
            widths.Add(capsule.Width);
        }
        Assert.Single(widths);
    }

    [Fact]
    public void ItNeverDrawsNarrowerThanAskedAndALongerLineIsWider()
    {
        FpsOverlay.AddFont(Font());
        using var plain = FpsOverlay.Draw(new(142, null), 1);
        using var longer = FpsOverlay.Draw(new(142, " \u00B7 96 LOW"), 1);
        using var held = FpsOverlay.Draw(new(99, " \u00B7 96 LOW"), 1, longer.Width);
        Assert.True(longer.Width > plain.Width);
        Assert.Equal(longer.Width, held.Width);
    }

    /// <summary>With EXO_FPS_PREVIEW_DIR set: the capsule at all three positions, short and long, over light, dark and busy screens, as PNGs, each line on a screen of its own.</summary>
    [Fact]
    public void PreviewsToPngsWhenAsked()
    {
        var folder = Environment.GetEnvironmentVariable("EXO_FPS_PREVIEW_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "EXO_FPS_PREVIEW_DIR isn't set");
        FpsOverlay.AddFont(Font());
        Directory.CreateDirectory(folder!);
        foreach (var (name, background) in new (string, Func<int, int, uint>)[]
        {
            ("light", (_, _) => 0xF2F2F2),
            ("dark", (_, _) => 0x0E0E12),
            ("white", (_, _) => 0xFFFFFF),
            ("busy", (x, y) => (uint)((x * 255 / 640) << 16 | (y * 3 % 256) << 8 | ((x + y) * 5 % 256))),
        })
        foreach (var scale in (double[])[1, 1.5, 2])
        {
            var width = (int)(640 * scale);
            var screenHeight = (int)(44 * scale);
            var gap = (int)(10 * scale);
            FpsOverlay.Line[] lines = [new(142, null), new(1240, null), new(142, " \u00B7 96 LOW \u00B7 7.0 MS \u00B7 98% GPU \u00B7 71\u00B0C")];
            var height = (screenHeight + gap) * lines.Length;
            var canvas = new uint[width * height];
            // The blue between the screens is outside them, so an edge shows where each screen starts.
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    canvas[y * width + x] = y % (screenHeight + gap) < gap ? 0x2060FF : background(x, y);
            for (var row = 0; row < lines.Length; row++)
                // The long line alone on its screen: at three places it would run into itself.
                foreach (var position in lines[row].Items is null ? (string[])["left", "middle", "right"] : ["right"])
                {
                    using var capsule = FpsOverlay.Draw(lines[row], scale);
                    var (left, top) = FpsOverlay.Place(position, 0, row * (screenHeight + gap) + gap, width, capsule.Width, FpsOverlay.MarginAt(scale));
                    Over(canvas, width, Pixels(capsule), capsule.Width, capsule.Height, left, top);
                }
            WritePng(Path.Combine(folder!, $"fps-{name}-{(int)(scale * 100)}.png"), canvas, width, height);
        }
    }

    private static void Over(uint[] canvas, int width, uint[] pixels, int w, int h, int left, int top)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var pixel = pixels[y * w + x];
                var a = pixel >> 24;
                var at = (top + y) * width + left + x;
                var under = canvas[at];
                uint Mix(int shift) => Math.Min(255, (pixel >> shift & 0xFF) + (under >> shift & 0xFF) * (255 - a) / 255) << shift;
                canvas[at] = Mix(16) | Mix(8) | Mix(0);
            }
    }

    private static void WritePng(string path, uint[] pixels, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                for (var x = 0; x < width; x++)
                {
                    var pixel = pixels[y * width + x];
                    zlib.WriteByte((byte)(pixel >> 16));
                    zlib.WriteByte((byte)(pixel >> 8));
                    zlib.WriteByte((byte)pixel);
                }
            }
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var body = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type, body);
            data.CopyTo(body, 4);
            Span<byte> number = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
            file.Write(number);
            file.Write(body);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, Crc32.Of(body));
            file.Write(number);
        }
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk("IHDR", header);
        Chunk("IDAT", raw.ToArray());
        Chunk("IEND", []);
    }
}

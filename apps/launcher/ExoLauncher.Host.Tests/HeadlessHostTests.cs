using System.Text.Json.Nodes;
using ExoLauncher;
using ExoLauncher.Services;
using ExoLauncher.Services.DiscordClient;
using Microsoft.UI.Dispatching;

namespace ExoLauncher.Host.Tests;

public sealed class HeadlessHostTests
{
    [Fact]
    public async Task Win32DispatcherQueueExecutesOnDedicatedStaThreadAndRespectsPriorities()
    {
        var ct = TestContext.Current.CancellationToken;
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        var queue = controller.DispatcherQueue;
        try
        {
            Assert.False(queue.HasThreadAccess);

            var gate = new ManualResetEventSlim(false);
            var order = new List<string>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var threadInfo = new TaskCompletionSource<(bool HasAccess, bool MatchesCurrent, ApartmentState Apartment)>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Assert.True(queue.TryEnqueue(() =>
            {
                threadInfo.SetResult((
                    queue.HasThreadAccess,
                    ReferenceEquals(DispatcherQueue.GetForCurrentThread(), queue),
                    Thread.CurrentThread.GetApartmentState()));
                gate.Wait(TimeSpan.FromSeconds(5));
            }));

            var info = await threadInfo.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.True(info.HasAccess);
            Assert.True(info.MatchesCurrent);
            Assert.Equal(ApartmentState.STA, info.Apartment);

            Assert.True(queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                order.Add("low");
                done.TrySetResult();
            }));
            Assert.True(queue.TryEnqueue(DispatcherQueuePriority.Normal, () => order.Add("normal")));
            Assert.True(queue.TryEnqueue(DispatcherQueuePriority.High, () => order.Add("high")));

            gate.Set();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            Assert.Equal(["high", "normal", "low"], order);
        }
        finally
        {
            await controller.ShutdownQueueAsync();
            Assert.False(queue.TryEnqueue(() => { }));
        }
    }

    [Fact]
    public async Task Win32DispatcherQueueTimerFiresOnDispatcherThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        var queue = controller.DispatcherQueue;
        try
        {
            var timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(20);
            timer.IsRepeating = false;

            var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            timer.Tick += (_, _) => fired.TrySetResult(queue.HasThreadAccess);

            timer.Start();
            var onDispatcher = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.True(onDispatcher);
            Assert.False(timer.IsRunning);
        }
        finally
        {
            await controller.ShutdownQueueAsync();
        }
    }

    [Fact]
    public async Task Win32DispatcherQueueSynchronizationContextPostsToQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        var queue = controller.DispatcherQueue;
        try
        {
            var ctx = new DispatcherQueueSynchronizationContext(queue);
            var tcs = new TaskCompletionSource<(bool Access, string State)>(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.Post(state => tcs.TrySetResult((queue.HasThreadAccess, (string)state!)), "ok");
            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.True(result.Access);
            Assert.Equal("ok", result.State);
        }
        finally
        {
            await controller.ShutdownQueueAsync();
        }
    }

    [Fact]
    public async Task Win32SingleInstanceArbitratesPrimaryAndForwardsActivationArgs()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = "exo-test-" + Guid.NewGuid().ToString("N");
        using var primary = Win32SingleInstance.Acquire(key);
        Assert.True(primary.IsPrimary);

        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.Activated += args => received.TrySetResult(args);

        using var secondary = Win32SingleInstance.Acquire(key);
        Assert.False(secondary.IsPrimary);

        string[] expected = ["--open-game", "steam:12345", "quoted \"arg\""];
        Assert.True(secondary.TryForwardActivation(expected, timeoutMs: 3000));

        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void HostAssemblyHasZeroWinUiOrWindowsAppSdkReferences()
    {
        var referenced = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        string[] forbiddenPrefixes =
        [
            "Microsoft.UI.Xaml",
            "Microsoft.WindowsAppRuntime",
            "Microsoft.Graphics.Canvas",
            "Microsoft.Windows.AppLifecycle",
            "Microsoft.Windows.ApplicationModel",
            "WinRT.Host",
        ];

        foreach (var prefix in forbiddenPrefixes)
        {
            Assert.DoesNotContain(referenced, name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void HeadlessShimContractsMatchExpectedBehavior()
    {
        Assert.True(MainWindow.WantsHdr("on", supports: true));
        Assert.False(MainWindow.WantsHdr("on", supports: false));
        Assert.False(MainWindow.WantsHdr("off", supports: true));

        Assert.True(MainWindow.CaptureHoldsCall(JsonNode.Parse("""{"recording":true}""")));
        Assert.False(MainWindow.CaptureHoldsCall(JsonNode.Parse("""{"recording":false}""")));
        Assert.False(MainWindow.CaptureHoldsCall(null));

        Assert.Equal("top-left", FpsOverlay.PositionOf("top-left"));
        Assert.Equal("bottom-right", FpsOverlay.PositionOf("bottomright"));
        Assert.Equal("top-right", FpsOverlay.PositionOf(null));

        Assert.Equal(NotifyRoute.None, DiscordNotifier.Route(true, "c1", "c1", false, false, true));
        Assert.Equal(NotifyRoute.Windows, DiscordNotifier.Route(false, null, "c1", false, false, true));
        Assert.True(StoreAuthWindow.IsIdentityProvider(new Uri("https://accounts.google.com/o/oauth2")));
        Assert.False(StoreAuthWindow.IsIdentityProvider(new Uri("https://example.invalid/")));
    }

    [Fact]
    public void RuneHostSessionTryParseFallsBackWithoutThrowingInHeadlessHost()
    {
        var warnings = new List<string>();
        Assert.Null(RuneHostSession.TryParse(["--host-pipe", "../escape"], _ => null, _ => false, warnings.Add));
        Assert.Single(warnings);
        Assert.Null(RuneHostSession.TryParse(["--host-pipe"], _ => null, _ => false));
        Assert.Null(RuneHostSession.TryParse(["--host-pipe=bad value"], _ => null, _ => true));
        Assert.Null(RuneHostSession.TryParse(null, _ => null, _ => false));
    }

    [Fact]
    public void SettingsJsonContextRoundTripsNullableIntSettingsInHeadlessHost()
    {
        var original = new ExoLauncher.Models.AppSettings
        {
            AppVersion = "4.0.0-host",
            RtxVideoSuperResolution = 5,
            DownloadHoursStart = 2,
            DownloadHoursEnd = 6,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(original, SettingsJsonContext.Default.AppSettings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
        Assert.NotNull(loaded);
        Assert.Equal("4.0.0-host", loaded!.AppVersion);
        Assert.Equal(5, loaded.RtxVideoSuperResolution);
        Assert.Equal(2, loaded.DownloadHoursStart);
        Assert.Equal(6, loaded.DownloadHoursEnd);
    }
}

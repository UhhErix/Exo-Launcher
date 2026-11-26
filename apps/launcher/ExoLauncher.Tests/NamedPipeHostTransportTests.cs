using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class NamedPipeHostTransportTests
{
    private sealed class StubWebTransport : IHostTransport
    {
        public event Action<string>? MessageReceived { add { } remove { } }
        public event Action? SessionReset { add { } remove { } }
        public void PostJson(string json) { }
        public void PostBinaryFrame(string metaJson, byte[] frame) { }
        public void Detach() { }
        public void Dispose() { }
    }

    [Fact]
    public void PipeSecurityGrantsAccessOnlyToCurrentUserSid()
    {
        var currentSid = WindowsIdentity.GetCurrent().User!;
        var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var authSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        var security = NamedPipeHostTransport.CreateCurrentUserPipeSecurity();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        Assert.Single(rules);
        var rule = rules[0];
        Assert.Equal(currentSid, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.True((rule.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl);
        Assert.DoesNotContain(rules, r => Equals(r.IdentityReference, worldSid) || Equals(r.IdentityReference, authSid));
    }

    [Fact]
    public async Task RoundTripsNdjsonMessagesAndBinaryFramesOverNamedPipe()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-pipe-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.MessageReceived += msg => received.TrySetResult(msg);
        transport.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        await writer.WriteLineAsync("""{"id":"1","method":"system.about"}""".AsMemory(), ct);
        var gotInbound = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("""{"id":"1","method":"system.about"}""", gotInbound);

        transport.PostJson("""{"id":"1","result":{"ok":true}}""");
        var gotOutbound = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("""{"id":"1","result":{"ok":true}}""", gotOutbound);

        byte[] frame = [1, 2, 3, 4, 250, 251];
        transport.PostBinaryFrame("""{"channel":"discord:1"}""", frame);
        var gotFrame = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.NotNull(gotFrame);

        using var doc = JsonDocument.Parse(gotFrame!);
        Assert.Equal("binaryFrame", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("discord:1", doc.RootElement.GetProperty("meta").GetProperty("channel").GetString());
        Assert.Equal(frame, Convert.FromBase64String(doc.RootElement.GetProperty("data").GetString()!));
    }

    [Fact]
    public async Task StartDelaysListeningUntilCalledAndDropsZeroStartupMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-pipe-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);

        using (var earlyClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await Assert.ThrowsAsync<TimeoutException>(() => earlyClient.ConnectAsync(100, ct));
        }

        var firstMessage = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.MessageReceived += msg =>
        {
            firstMessage.TrySetResult(msg);
            transport.PostJson("""{"version":1,"id":"probe-0","ok":true}""");
        };

        var connectTask = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await client.ConnectAsync(150, ct);
                    return client;
                }
                catch (TimeoutException)
                {
                    client.Dispose();
                    await Task.Delay(20, ct);
                }
            }
            throw new TimeoutException("Client failed to connect after Start().");
        }, ct);

        await Task.Delay(60, ct);
        transport.Start();
        transport.Start();

        using var connected = await connectTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
        using var writer = new StreamWriter(connected, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(connected, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        await writer.WriteLineAsync("""{"version":1,"id":"probe-0","method":"shell.heartbeat"}""".AsMemory(), ct);
        var inbound = await firstMessage.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var reply = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Equal("""{"version":1,"id":"probe-0","method":"shell.heartbeat"}""", inbound);
        Assert.Equal("""{"version":1,"id":"probe-0","ok":true}""", reply);

        transport.Detach();
        transport.Start();
    }

    [Fact]
    public async Task SlowSynchronousMessageHandlerDoesNotBlockConcurrentHeartbeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-pipe-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);

        using var slowEntered = new ManualResetEventSlim(false);
        using var releaseSlow = new ManualResetEventSlim(false);

        transport.MessageReceived += msg =>
        {
            using var doc = JsonDocument.Parse(msg);
            var id = doc.RootElement.GetProperty("id").GetString();
            var method = doc.RootElement.GetProperty("method").GetString();
            if (method == "settings.get")
            {
                slowEntered.Set();
                releaseSlow.Wait(TimeSpan.FromSeconds(5), ct);
                transport.PostJson($"{{\"version\":1,\"id\":\"{id}\",\"ok\":true}}");
                return;
            }
            transport.PostJson($"{{\"version\":1,\"id\":\"{id}\",\"ok\":true}}");
        };
        transport.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        await writer.WriteLineAsync("""{"version":1,"id":"slow-1","method":"settings.get"}""".AsMemory(), ct);
        Assert.True(slowEntered.Wait(TimeSpan.FromSeconds(5), ct));

        var sw = Stopwatch.StartNew();
        await writer.WriteLineAsync("""{"version":1,"id":"hb-1","method":"shell.heartbeat"}""".AsMemory(), ct);
        var firstReply = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
        sw.Stop();

        Assert.NotNull(firstReply);
        using (var doc = JsonDocument.Parse(firstReply!))
            Assert.Equal("hb-1", doc.RootElement.GetProperty("id").GetString());
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Heartbeat waited {sw.ElapsedMilliseconds} ms behind blocked handler.");

        releaseSlow.Set();
        var secondReply = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.NotNull(secondReply);
        using (var doc = JsonDocument.Parse(secondReply!))
            Assert.Equal("slow-1", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task BatchedOutboundWritesPreserveFifoOrderAcrossSmallAndLargeFrames()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-pipe-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);
        transport.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var largePayload = new string('x', 80 * 1024);
        var expected = new List<string>(82);
        for (var i = 0; i < 40; i++)
            expected.Add($"{{\"version\":1,\"id\":\"s-{i}\",\"ok\":true}}");
        expected.Add($"{{\"version\":1,\"id\":\"big-1\",\"payload\":\"{largePayload}\"}}");
        for (var i = 40; i < 80; i++)
            expected.Add($"{{\"version\":1,\"id\":\"s-{i}\",\"ok\":true}}");
        expected.Add($"{{\"version\":1,\"id\":\"big-2\",\"payload\":\"{largePayload}\"}}");

        foreach (var line in expected)
            transport.PostJson(line);

        for (var i = 0; i < expected.Count; i++)
        {
            var actual = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(expected[i], actual);
        }
    }

    [Fact]
    public async Task OversizedFrameDisconnectsClientAndAllowsCleanReconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-pipe-{Guid.NewGuid():N}";
        const int testLimit = 64 * 1024;
        using var transport = new NamedPipeHostTransport(pipeName, testLimit);

        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SessionReset += () => reset.TrySetResult();
        transport.Start();

        using (var client1 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client1.ConnectAsync(5000, ct);
            var oversize = new byte[testLimit + 128];
            Array.Fill(oversize, (byte)'a');
            oversize[^1] = (byte)'\n';
            try { await client1.WriteAsync(oversize, ct); } catch (IOException) { }
            await reset.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.MessageReceived += msg => received.TrySetResult(msg);

        using var client2 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client2.ConnectAsync(5000, ct);
        using var writer2 = new StreamWriter(client2, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer2.WriteLineAsync("""{"id":"2","method":"ping"}""".AsMemory(), ct);

        var msg2 = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("""{"id":"2","method":"ping"}""", msg2);
    }

    [Fact]
    public async Task LargeFrameAboveFourMegabytesSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-pipe-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.MessageReceived += msg => received.TrySetResult(msg);
        transport.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        var largePayload = "{\"id\":\"big\",\"method\":\"capture.saveImage\",\"data\":\"" + new string('x', 5 * 1024 * 1024) + "\"}";
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(largePayload.AsMemory(), ct);

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(largePayload.Length, got.Length);
    }

    [Fact]
    public async Task DetachClosesPipeCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-pipe-{Guid.NewGuid():N}";
        var transport = new NamedPipeHostTransport(pipeName);
        transport.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);

        transport.Detach();
        transport.Detach();
        transport.Dispose();
    }

    [Fact]
    public async Task ReliableJsonRepliesAreNeverDroppedUnderBinaryFrameFlood()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-pipe-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);
        transport.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        byte[] dummyFrame = [1, 2, 3, 4];
        for (var i = 0; i < 300; i++)
            transport.PostBinaryFrame("""{"channel":"discord:stream"}""", dummyFrame);

        const int reliableCount = 25;
        for (var i = 0; i < reliableCount; i++)
            transport.PostJson($"{{\"version\":1,\"id\":\"req-{i}\",\"ok\":true}}");

        var seenIds = new HashSet<string>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (seenIds.Count < reliableCount && DateTime.UtcNow < deadline)
        {
            var line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.NotNull(line);
            using var doc = JsonDocument.Parse(line!);
            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.GetString() is { } id)
                seenIds.Add(id);
        }

        Assert.Equal(reliableCount, seenIds.Count);
    }

    [Fact]
    public void EarlyCancelTrackerCancelsOutOfOrderRequestsExpiresStaleEntriesAndClearsOnReset()
    {
        var now = 1_000L;
        var tracker = new WebHostBridge.EarlyCancelTracker(ttlMs: 500, clockMs: () => now);

        tracker.Record("req-1");
        Assert.True(tracker.Consume("req-1"));
        Assert.False(tracker.Consume("req-1"));

        tracker.Record("req-expired");
        now += 501;
        Assert.False(tracker.Consume("req-expired"));

        tracker.Record("req-reset");
        tracker.Clear();
        Assert.False(tracker.Consume("req-reset"));

        for (var i = 0; i < 260; i++)
            tracker.Record($"old-{i}");
        now += 600;
        tracker.Record("fresh-1");
        Assert.True(tracker.Consume("fresh-1"));
        Assert.False(tracker.Consume("old-0"));
    }

    [Fact]
    public void NamedPipeTransportSkipsPageTokenRewriteWhileOtherTransportsKeepIt()
    {
        var raw = "{\"cover\":\"https://covers.exo-launcher.local/steam_620.jpg\"}";
        var tokened = WebHostBridge.ToPage(raw);
        Assert.Contains(LocalFileHost.Token, tokened);

        using var pipeTransport = new NamedPipeHostTransport($"exo-test-pipe-{Guid.NewGuid():N}");
        using var webTransport = new StubWebTransport();

        Assert.Same(raw, WebHostBridge.OutboundForTransport(pipeTransport, raw));
        Assert.Same(tokened, WebHostBridge.InboundForTransport(pipeTransport, tokened));

        Assert.Equal(tokened, WebHostBridge.OutboundForTransport(webTransport, raw));
        Assert.Equal(raw, WebHostBridge.InboundForTransport(webTransport, tokened));
    }

    [Fact]
    public void ResolveFastFrameGenGpuUsesFallbackBeforeWarmupAndWarmedValueAfter()
    {
        var warmedCalls = 0;
        var fallbackCalls = 0;

        var cold = SystemSpecs.ResolveFastFrameGenGpu(
            warmed: false,
            () => { warmedCalls++; return "NVIDIA GeForce RTX 4090"; },
            () => { fallbackCalls++; return "NVIDIA GeForce RTX 4090"; });

        Assert.Equal("NVIDIA GeForce RTX 4090", cold);
        Assert.Equal(0, warmedCalls);
        Assert.Equal(1, fallbackCalls);

        var warm = SystemSpecs.ResolveFastFrameGenGpu(
            warmed: true,
            () => { warmedCalls++; return "AMD Radeon RX 7900 XTX"; },
            () => { fallbackCalls++; return "fallback"; });

        Assert.Equal("AMD Radeon RX 7900 XTX", warm);
        Assert.Equal(1, warmedCalls);
        Assert.Equal(1, fallbackCalls);
    }

    [Fact]
    public void RuneHostSessionParsesCliAndRejectsUnsafePipeNames()
    {
        var pipeOnly = RuneHostSession.TryParse(["--host-pipe", "exo-dev-123"], _ => null, _ => false);
        Assert.NotNull(pipeOnly);
        Assert.Equal("exo-dev-123", pipeOnly!.PipeName);
        Assert.False(pipeOnly.SpawnUi);

        var uiMode = RuneHostSession.TryParse(["--ui=rune"], _ => null, _ => false);
        Assert.NotNull(uiMode);
        Assert.True(uiMode!.SpawnUi);

        var envMode = RuneHostSession.TryParse([], key => key == "EXO_UI" ? "rune" : null, _ => false);
        Assert.NotNull(envMode);
        Assert.True(envMode!.SpawnUi);

        var defaultWithBundled = RuneHostSession.TryParse([], _ => null, path => path.EndsWith("exo-ui.exe", StringComparison.OrdinalIgnoreCase), readClassicPref: () => false);
        Assert.NotNull(defaultWithBundled);
        Assert.True(defaultWithBundled!.SpawnUi);

        var defaultWithoutBundled = RuneHostSession.TryParse([], _ => null, _ => false, readClassicPref: () => false);
        Assert.Null(defaultWithoutBundled);

        var webRollbackFlag = RuneHostSession.TryParse(["--ui=web"], _ => null, _ => true, readClassicPref: () => false);
        Assert.Null(webRollbackFlag);

        var webRollbackEnv = RuneHostSession.TryParse([], key => key == "EXO_UI" ? "web" : null, _ => true, readClassicPref: () => false);
        Assert.Null(webRollbackEnv);

        var smokeBypass = RuneHostSession.TryParse(["--smoke-test"], _ => null, _ => true, readClassicPref: () => false);
        Assert.Null(smokeBypass);

        var classicPref = RuneHostSession.TryParse([], _ => null, _ => true, readClassicPref: () => true);
        Assert.Null(classicPref);

        var cliNativeOverridesPref = RuneHostSession.TryParse(["--ui=native"], _ => null, _ => true, readClassicPref: () => true);
        Assert.NotNull(cliNativeOverridesPref);
        Assert.True(cliNativeOverridesPref!.SpawnUi);

        var envNativeOverridesPref = RuneHostSession.TryParse([], key => key == "EXO_UI" ? "native" : null, _ => true, readClassicPref: () => true);
        Assert.NotNull(envNativeOverridesPref);
        Assert.True(envNativeOverridesPref!.SpawnUi);

        var warnings = new List<string>();
        Assert.Null(RuneHostSession.TryParse(["--host-pipe", "../escape"], _ => null, _ => false, warnings.Add));
        Assert.Single(warnings);

        var fakeExe = Path.Combine(Path.GetTempPath(), "exo-app-dir", "exo-ui.exe");
        var psi = RuneHostSession.BuildUiStartInfo(fakeExe, "exo-dev-123");
        Assert.Equal(PathHelper.AppDataDir, psi.WorkingDirectory);
        Assert.NotEqual(Path.GetDirectoryName(fakeExe), psi.WorkingDirectory);
        Assert.Equal(["--pipe", "exo-dev-123"], psi.ArgumentList);
        Assert.True(psi.RedirectStandardError);

        Assert.Equal("ExoLauncher.Main", PackagedSmoke.ResolveInstanceKey([], isDisposable: false, @"C:\Users\Test\AppData\Local\ExoLauncher"));
        var dev = PackagedSmoke.ResolveInstanceKey([], isDisposable: true, @"C:\Users\Test\Exo-Rune-Local\data");
        Assert.StartsWith("ExoLauncher.Dev.data.", dev);
        Assert.Equal(dev, PackagedSmoke.ResolveInstanceKey([], isDisposable: true, @"C:\USERS\Test\Exo-Rune-Local\data\"));
        Assert.NotEqual(dev, PackagedSmoke.ResolveInstanceKey([], isDisposable: true, @"C:\Users\Test\Other\data"));
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher");
        Assert.Throws<InvalidOperationException>(() => PackagedSmoke.ResolveInstanceKey([], isDisposable: true, installed));
        Assert.Throws<InvalidOperationException>(() => PackagedSmoke.ResolveInstanceKey([], isDisposable: true, Path.Combine(installed, "test")));
    }

    [Fact]
    public void RuneHostSessionTryParseNeverThrowsOnInvalidOrForeignInputs()
    {
        string[]?[] badPipeCases =
        [
            ["--host-pipe", "../escape"],
            ["--host-pipe", @"\\.\pipe\exo-dev"],
            ["--host-pipe", ""],
            ["--host-pipe", "   "],
            ["--host-pipe", "bad pipe name"],
            ["--host-pipe", "--ui=rune"],
            ["--host-pipe", new string('a', 121)],
            ["--host-pipe"],
            ["--host-pipe="],
            ["--host-pipe=../escape"],
            ["--host-pipe=bad=value"],
        ];

        foreach (var args in badPipeCases)
        {
            var warnings = new List<string>();
            var parsed = RuneHostSession.TryParse(args, _ => "rune", _ => true, warnings.Add);
            Assert.Null(parsed);
            Assert.NotEmpty(warnings);
        }

        Assert.Null(RuneHostSession.TryParse(null, _ => null, _ => false));
        Assert.Null(RuneHostSession.TryParse([null!, "", "--unknown", "exolauncher://library", "-Embedding"], _ => null, _ => false));
        Assert.Null(RuneHostSession.TryParse(["--ui"], _ => null, _ => false));
        Assert.Null(RuneHostSession.TryParse(["--rune-ui-path"], _ => null, _ => false));
        Assert.Null(RuneHostSession.TryParse([], key => key == "EXO_UI" ? "unexpected-value" : null, _ => false));

        var envWarnings = new List<string>();
        Assert.Null(RuneHostSession.TryParse([], _ => throw new InvalidOperationException("env failure"), _ => false, envWarnings.Add));
        Assert.NotEmpty(envWarnings);

        var fsWarnings = new List<string>();
        Assert.Null(RuneHostSession.TryParse([], key => key == "EXO_RUNE_UI_PATH" ? "C:\\bad|path\\exo-ui.exe" : null, _ => throw new ArgumentException("invalid path"), fsWarnings.Add));
        Assert.NotEmpty(fsWarnings);

        var overrideEq = RuneHostSession.TryParse(["--ui=rune", "--rune-ui-path=C:\\exo\\exo-ui.exe"], _ => null, _ => false);
        Assert.NotNull(overrideEq);
        Assert.True(overrideEq!.SpawnUi);
        Assert.Equal(@"C:\exo\exo-ui.exe", overrideEq.UiExecutableOverride);
    }

    [Fact]
    public void ReliableQueueIsBoundedAgainstStalledClient()
    {
        using var transport = new NamedPipeHostTransport($"exo-test-bound-{Guid.NewGuid():N}");
        var payload = System.Text.Encoding.UTF8.GetBytes("{\"event\":\"ping\"}");
        for (var i = 0; i < NamedPipeHostTransport.ReliableQueueCapacity + 64; i++)
            transport.PostJsonUtf8(payload);
    }

    [Fact]
    public async Task ConcurrentHeartbeatsStayFastWhileStreaming60Fps64KbVideoFrames()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-video-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);

        transport.MessageReceived += msg =>
        {
            using var doc = JsonDocument.Parse(msg);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            transport.PostJson(ExoBridgeProtocol.SerializeResponse(id, ok: true, result: new { ok = true }, error: null));
        };
        transport.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        using var stopVideo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        var framesSent = 0;
        var producer = Task.Run(async () =>
        {
            var meta = """{"source":"stream","userId":"200002","timestamp":1234.5,"keyframe":false,"codec":"h264"}""";
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
            while (!stopVideo.IsCancellationRequested && await timer.WaitForNextTickAsync(stopVideo.Token))
            {
                transport.PostBinaryFrame(meta, payload);
                Interlocked.Increment(ref framesSent);
            }
        }, CancellationToken.None);

        var rtts = new List<double>(60);
        var pending = new Dictionary<string, long>();
        var pendingLock = new Lock();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var framesReceived = 0;

        var readTask = Task.Run(async () =>
        {
            while (!stopVideo.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(stopVideo.Token);
                if (line is null) break;
                var now = Stopwatch.GetTimestamp();
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetString()!;
                    long sentAt;
                    lock (pendingLock)
                    {
                        if (!pending.Remove(id, out sentAt)) continue;
                    }
                    var ms = Stopwatch.GetElapsedTime(sentAt, now).TotalMilliseconds;
                    rtts.Add(ms);
                    if (rtts.Count >= 60) done.TrySetResult();
                }
                else if (doc.RootElement.TryGetProperty("event", out var evEl) && evEl.GetString() == "binaryFrame")
                {
                    Interlocked.Increment(ref framesReceived);
                }
            }
        }, CancellationToken.None);

        for (var i = 0; i < 60; i++)
        {
            var id = $"hb-{i}";
            lock (pendingLock) pending[id] = Stopwatch.GetTimestamp();
            await writer.WriteLineAsync($"{{\"version\":1,\"id\":\"{id}\",\"method\":\"shell.heartbeat\"}}".AsMemory(), ct);
            await Task.Delay(12, ct);
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        stopVideo.Cancel();
        try { await producer; } catch { }
        try { await readTask; } catch { }

        rtts.Sort();
        var p50 = rtts[rtts.Count / 2];
        var p95 = rtts[(int)Math.Ceiling(rtts.Count * 0.95) - 1];
        var max = rtts[^1];
        Console.WriteLine($"VIDEO_60FPS_SHARED_PIPE framesSent={framesSent} framesReceived={framesReceived} hb_n={rtts.Count} p50={p50:F3}ms p95={p95:F3}ms max={max:F3}ms");
        Assert.Equal(60, rtts.Count);
        Assert.True(framesReceived > 0);
    }

    [Fact]
    public void ExoBridgeProtocolUtf8MatchesStringSerializationAndHandlesNulls()
    {
        var okUtf8 = ExoBridgeProtocol.SerializeResponseUtf8("42", true, new { count = 3, label = "ok" }, null);
        Assert.Equal(ExoBridgeProtocol.SerializeResponse("42", true, new { count = 3, label = "ok" }, null), Encoding.UTF8.GetString(okUtf8));

        var errUtf8 = ExoBridgeProtocol.SerializeResponseUtf8("43", false, null, "boom");
        using (var errDoc = JsonDocument.Parse(errUtf8))
        {
            Assert.Equal("43", errDoc.RootElement.GetProperty("id").GetString());
            Assert.False(errDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("boom", errDoc.RootElement.GetProperty("error").GetString());
            Assert.False(errDoc.RootElement.TryGetProperty("result", out _));
        }

        var evNullUtf8 = ExoBridgeProtocol.SerializeEventUtf8("autopilot.live", null);
        using var evDoc = JsonDocument.Parse(evNullUtf8);
        Assert.Equal("autopilot.live", evDoc.RootElement.GetProperty("event").GetString());
        Assert.Equal(JsonValueKind.Null, evDoc.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task DedicatedMediaPipeStreamsRawBinaryFramesWithoutBlockingControlPipe()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-media-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);
        transport.MessageReceived += raw =>
        {
            using var doc = JsonDocument.Parse(raw);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            transport.PostJsonUtf8(ExoBridgeProtocol.SerializeResponseUtf8(id, true, new { ok = true }, null));
        };
        transport.Start();

        using var control = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await control.ConnectAsync(5000, ct);
        using var media = new NamedPipeClientStream(".", transport.MediaPipeName, PipeDirection.In, PipeOptions.Asynchronous);
        await media.ConnectAsync(5000, ct);

        using var writer = new StreamWriter(control, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(control, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        await Task.Delay(50, ct);

        var expectedMeta = """{"source":"stream","userId":"200002","timestamp":1234.5,"keyframe":true,"codec":"h264"}""";
        var expectedPayload = new byte[64 * 1024];
        for (var i = 0; i < expectedPayload.Length; i++) expectedPayload[i] = (byte)(i & 0xFF);

        using var stopVideo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var mediaFrames = 0;
        var verifiedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var mediaReader = Task.Run(async () =>
        {
            var header = new byte[8];
            while (!stopVideo.IsCancellationRequested)
            {
                await media.ReadExactlyAsync(header, stopVideo.Token);
                var metaLen = BitConverter.ToInt32(header, 0);
                var frameLen = BitConverter.ToInt32(header, 4);
                var metaBuf = new byte[metaLen];
                await media.ReadExactlyAsync(metaBuf, stopVideo.Token);
                var frameBuf = new byte[frameLen];
                if (frameLen > 0) await media.ReadExactlyAsync(frameBuf, stopVideo.Token);
                if (Interlocked.Increment(ref mediaFrames) == 1)
                {
                    Assert.Equal(expectedMeta, Encoding.UTF8.GetString(metaBuf));
                    Assert.Equal(expectedPayload, frameBuf);
                    verifiedFirst.TrySetResult();
                }
            }
        }, CancellationToken.None);

        var producer = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
            while (!stopVideo.IsCancellationRequested && await timer.WaitForNextTickAsync(stopVideo.Token))
            {
                transport.PostBinaryFrame(expectedMeta, expectedPayload);
            }
        }, CancellationToken.None);

        await verifiedFirst.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var leakedBinaryOnControl = 0;
        for (var i = 0; i < 20; i++)
        {
            var id = $"mhb-{i}";
            await writer.WriteLineAsync($"{{\"version\":1,\"id\":\"{id}\",\"method\":\"shell.heartbeat\"}}".AsMemory(), ct);
            var line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.NotNull(line);
            using var doc = JsonDocument.Parse(line!);
            if (doc.RootElement.TryGetProperty("event", out var ev) && ev.GetString() == "binaryFrame")
                leakedBinaryOnControl++;
            else
                Assert.Equal(id, doc.RootElement.GetProperty("id").GetString());
        }

        stopVideo.Cancel();
        try { await producer; } catch { }
        try { await mediaReader; } catch { }

        Assert.Equal(0, leakedBinaryOnControl);
        Assert.True(mediaFrames > 0);
    }

    [Fact]
    public void HeadlessMusicTransitionHelperSuppressesDuplicateIdleAndEmitsRealChanges()
    {
        var lastHadPlayback = false;
        Assert.False(HeadlessMusicService.ShouldPublishPlaybackTransition(false, ref lastHadPlayback));
        Assert.False(lastHadPlayback);
        Assert.True(HeadlessMusicService.ShouldPublishPlaybackTransition(true, ref lastHadPlayback));
        Assert.True(lastHadPlayback);
        Assert.True(HeadlessMusicService.ShouldPublishPlaybackTransition(true, ref lastHadPlayback));
        Assert.True(HeadlessMusicService.ShouldPublishPlaybackTransition(false, ref lastHadPlayback));
        Assert.False(lastHadPlayback);
        Assert.False(HeadlessMusicService.ShouldPublishPlaybackTransition(false, ref lastHadPlayback));
        Assert.Equal("spotify", HeadlessMusicService.DetectProvider("Spotify.exe"));
        Assert.Equal("apple", HeadlessMusicService.DetectProvider("AppleInc.AppleMusicWin"));
        Assert.Equal("youtube", HeadlessMusicService.DetectProvider("ytmusic.exe"));
        Assert.Equal("system", HeadlessMusicService.DetectProvider("vlc.exe"));
    }

    [Fact]
    public void HeadlessCaptureForwardsStateAndReelEventsWhenAttachedAndSuppressesAfterDetach()
    {
        using var capture = new HeadlessCaptureService(null!);
        var events = new List<(string Name, object? Data)>();
        capture.Attach((name, data) => events.Add((name, data)));

        capture.HandleAddonEvent("state", System.Text.Json.Nodes.JsonNode.Parse("""{"replay":true,"buffering":true,"recording":false}"""));
        capture.HandleAddonEvent("concat", System.Text.Json.Nodes.JsonNode.Parse("""{"state":"working","percent":42,"wait":false}"""));

        Assert.Equal(2, events.Count);
        Assert.Equal("capture.state", events[0].Name);
        Assert.Equal("capture.reel", events[1].Name);

        capture.Detach();
        capture.HandleAddonEvent("state", System.Text.Json.Nodes.JsonNode.Parse("""{"replay":false,"buffering":false,"recording":false}"""));

        Assert.Equal(2, events.Count);
    }
}


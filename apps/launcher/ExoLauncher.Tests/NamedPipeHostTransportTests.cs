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
        using var transport = new NamedPipeHostTransport(pipeName);

        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SessionReset += () => reset.TrySetResult();
        transport.Start();

        using (var client1 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client1.ConnectAsync(5000, ct);
            var oversize = new byte[NamedPipeHostTransport.MaxMessageBytes + 128];
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

        var defaultWithBundled = RuneHostSession.TryParse([], _ => null, path => path.EndsWith("exo-ui.exe", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(defaultWithBundled);
        Assert.True(defaultWithBundled!.SpawnUi);

        var defaultWithoutBundled = RuneHostSession.TryParse([], _ => null, _ => false);
        Assert.Null(defaultWithoutBundled);

        var webRollbackFlag = RuneHostSession.TryParse(["--ui=web"], _ => null, _ => true);
        Assert.Null(webRollbackFlag);

        var webRollbackEnv = RuneHostSession.TryParse([], key => key == "EXO_UI" ? "web" : null, _ => true);
        Assert.Null(webRollbackEnv);

        var smokeBypass = RuneHostSession.TryParse(["--smoke-test"], _ => null, _ => true);
        Assert.Null(smokeBypass);

        Assert.Throws<ArgumentException>(() => RuneHostSession.TryParse(["--host-pipe", "../escape"], _ => null, _ => false));
    }
}

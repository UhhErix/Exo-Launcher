using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class NamedPipeHostTransportTests
{
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
    public async Task OversizedFrameDisconnectsClientAndAllowsCleanReconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = $"exo-test-pipe-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);

        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SessionReset += () => reset.TrySetResult();

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

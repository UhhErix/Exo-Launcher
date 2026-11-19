using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// The friend link end to end: two or three in-process <see cref="FriendLinks"/> through the real relay
/// (relay/src/worker.js) running on this PC under relay/local/server.mjs, with the Durable Object thrown away after every
/// event as Cloudflare's hibernation does. Needs Node 22+.
///
/// Ways it could fail between two Exo apps, none of which shows as an error:
/// - two friends never meet, or a game never arrives, or arrives for the wrong person;
/// - someone connecting later hears nothing until the next game, or never hears a game older than the freshness limit;
/// - a stranger on the pair's topic (the relay, or anyone who guesses it) reads it or gets something accepted;
/// - a game that ended stays, a drop takes a game away at once, or a friend who left is shown forever;
/// - hellos answer each other for ever;
/// - more friends than the cap connect, or a link stays up after they go;
/// - the relay is down and the client spins.
/// </summary>
public sealed class FriendLinkEndToEndTests : IAsyncLifetime
{
    private const string A = "111111111111111111", B = "222222222222222222", C = "333333333333333333";
    private readonly List<FriendLinks> _links = [];
    private readonly List<ECDiffieHellman> _keys = [];
    private Process? _relay;
    private Uri? _endpoint;

    public async ValueTask InitializeAsync()
    {
        var node = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, "node.exe")).FirstOrDefault(File.Exists);
        if (node is null) return;
        _relay = Process.Start(new ProcessStartInfo(node, ["relay/local/server.mjs", "--port", "0", "--evict"])
        {
            WorkingDirectory = Repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var line = await _relay.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        _endpoint = new Uri("ws://127.0.0.1:" + line!["relay listening ".Length..]);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var links in _links) await links.StopAsync();
        foreach (var key in _keys) key.Dispose();
        if (_relay is null) return;
        _relay.StandardInput.Close();
        if (!_relay.WaitForExit(5000)) _relay.CloseMainWindow();
        _relay.Dispose();
    }

    private sealed record Side(FriendLinks Links, string Key, ChannelReader<(string Friend, FriendLinkFormat.Message? Message)> Heard, ECDiffieHellman Private);

    private Side NewSide(string me, int graceMs = 1000, long answerAfterMs = FriendLinks.AnswerAfterMs)
    {
        Assert.SkipWhen(_endpoint is null, "Node isn't installed here (the local relay needs it)");
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _keys.Add(key);
        var links = new FriendLinks(me, key, _endpoint, graceMs, answerAfterMs);
        _links.Add(links);
        var heard = Channel.CreateUnbounded<(string, FriendLinkFormat.Message?)>();
        links.Heard += (friend, message) => heard.Writer.TryWrite((friend, message));
        return new(links, FriendLinkFormat.EncodeKey(key), heard.Reader, key);
    }

    // A wait is on what the test is about, with a limit only so a broken build ends: a loaded PC takes seconds, not milliseconds.
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static async Task<(string Friend, FriendLinkFormat.Message? Message)> Next(Side side, int seconds = 30, string what = "a friend's message")
    {
        try { return await side.Heard.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(seconds)); }
        catch (TimeoutException) { throw Xunit.Sdk.FailException.ForFailure($"Waited {seconds} s for {what}, and it never came."); }
    }

    // Nothing may happen for a while: the one wait that is time itself (a longer one only makes the check stricter).
    private static Task Quiet(TimeSpan span) => Task.Delay(span, TestContext.Current.CancellationToken);

    /// <summary>
    /// Waits until the relay holds a message of <paramref name="from"/>'s for the pair's topic, so that whoever joins next
    /// has it replayed: a passive listener on the topic sees it, and leaves before the real friend comes.
    /// </summary>
    private async Task UntilTheRelayHolds(Side from, Side to, Func<FriendLinkFormat.Message, bool>? which = null, string toId = B, string fromId = A)
    {
        var keys = FriendLinkFormat.Derive(to.Private, toId, fromId, from.Key)!;
        var seen = new TaskCompletionSource();
        await using var listener = new CallRelayClient(_endpoint!, keys.Topic, "Listener");
        listener.Received += frame =>
        {
            if (!frame.Gone && FriendLinkFormat.Open(keys, frame.Payload) is { } message && (which is null || which(message))) seen.TrySetResult();
        };
        listener.Start();
        try { await seen.Task.WaitAsync(Generous, TestContext.Current.CancellationToken); }
        catch (TimeoutException) { throw Xunit.Sdk.FailException.ForFailure($"Waited {Generous.TotalSeconds:0} s for the relay to hold {fromId}'s message, and it never did."); }
    }

    private static JsonObject Game(string title) => FriendGame.Write(new("steam:1", title, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))!;

    private static string? GameIn(FriendLinkFormat.Message? message) =>
        FriendGame.Read(message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())?.Title;

    [Fact]
    public async Task TwoFriendsMeetAndAGameArrivesAndEnds()
    {
        var a = NewSide(A);
        var b = NewSide(B);
        a.Links.Set(FriendGame.Section, Game("Hades II"));
        a.Links.SetPeers([(B, b.Key)]);
        b.Links.SetPeers([(A, a.Key)]);
        // B hears A's state whichever connected first.
        var heard = await Next(b);
        while (GameIn(heard.Message) is null) heard = await Next(b);
        Assert.Equal(A, heard.Friend);
        Assert.Equal("Hades II", GameIn(heard.Message));
        a.Links.Set(FriendGame.Section, null);
        do heard = await Next(b); while (GameIn(heard.Message) is not null);
        Assert.Equal(A, heard.Friend);
        Assert.NotNull(heard.Message); // said, not lost
    }

    [Fact]
    public async Task AGameChangeAfterBothAreUpArrivesWithinASecond()
    {
        var a = NewSide(A);
        var b = NewSide(B);
        a.Links.SetPeers([(B, b.Key)]);
        b.Links.SetPeers([(A, a.Key)]);
        await Next(b, what: "B to hear A once both are up");
        // The claim is "within a second", so it is held to 2 s on an idle PC; the longest stall this process itself had while
        // waiting (a PC under load stops everything for a while) is added, so only Exo's own slowness can fail it.
        var stall = TimeSpan.Zero;
        using var watching = new CancellationTokenSource();
        var ticks = Task.Run(async () =>
        {
            var last = Stopwatch.GetTimestamp();
            while (!watching.IsCancellationRequested)
            {
                await Task.Delay(10, CancellationToken.None);
                var now = Stopwatch.GetTimestamp();
                var gap = Stopwatch.GetElapsedTime(last, now) - TimeSpan.FromMilliseconds(10);
                if (gap > stall) stall = gap;
                last = now;
            }
        }, CancellationToken.None);
        var at = Stopwatch.StartNew();
        a.Links.Set(FriendGame.Section, Game("Hades II"));
        (string, FriendLinkFormat.Message?) heard;
        do heard = await Next(b, what: "B to hear A's game"); while (GameIn(heard.Item2) is null);
        var took = at.Elapsed;
        await watching.CancelAsync();
        await ticks;
        Assert.True(took <= TimeSpan.FromSeconds(2) + stall, $"A's game took {took.TotalMilliseconds:0} ms to reach B (this PC stalled {stall.TotalMilliseconds:0} ms meanwhile).");
    }

    [Fact]
    public async Task SomeoneConnectingLaterHearsWhatIsPlaying()
    {
        var a = NewSide(A);
        var b = NewSide(B);
        a.Links.Set(FriendGame.Section, Game("Hades II"));
        a.Links.SetPeers([(B, b.Key)]);
        await UntilTheRelayHolds(a, b, message => GameIn(message) is not null);
        b.Links.SetPeers([(A, a.Key)]);
        var heard = await Next(b, what: "B to hear what A is playing");
        Assert.Equal("Hades II", GameIn(heard.Message));
    }

    [Fact]
    public async Task AFriendWhoLeavesGoesAfterTheGraceNotAtOnce()
    {
        var a = NewSide(A);
        var b = NewSide(B, graceMs: 1500);
        a.Links.Set(FriendGame.Section, Game("Hades II"));
        a.Links.SetPeers([(B, b.Key)]);
        b.Links.SetPeers([(A, a.Key)]);
        while (GameIn((await Next(b)).Message) is null) { }
        var left = Stopwatch.StartNew();
        await a.Links.StopAsync();
        var heard = await Next(b);
        Assert.Null(heard.Message);
        Assert.InRange(left.ElapsedMilliseconds, 1000, 8000);
    }

    [Fact]
    public async Task HellosDoNotAnswerEachOtherForEver()
    {
        var a = NewSide(A);
        var b = NewSide(B);
        a.Links.SetPeers([(B, b.Key)]);
        b.Links.SetPeers([(A, a.Key)]);
        await Next(b, what: "B to hear A");
        await Quiet(TimeSpan.FromSeconds(2));
        var count = 0;
        while (b.Heard.TryRead(out _)) count++;
        Assert.InRange(count, 0, 2);
    }

    [Fact]
    public async Task AThirdFriendsLinkIsItsOwnAndAStrangerGetsNothing()
    {
        var a = NewSide(A);
        var b = NewSide(B);
        var c = NewSide(C);
        a.Links.Set(FriendGame.Section, Game("Hades II"));
        // A links with B only. C also tries for A: its topic is another one, where A never is.
        a.Links.SetPeers([(B, b.Key)]);
        b.Links.SetPeers([(A, a.Key)]);
        c.Links.SetPeers([(A, a.Key)]);
        while (GameIn((await Next(b, what: "B to hear A's game")).Message) is null) { }
        await Quiet(TimeSpan.FromSeconds(2));
        Assert.False(c.Heard.TryRead(out _));
    }

    [Fact]
    public async Task ARelayPeerOnTheTopicCantBeHeardAndCantReadIt()
    {
        var a = NewSide(A);
        var b = NewSide(B);
        a.Links.Set(FriendGame.Section, Game("Hades II"));
        var keys = FriendLinkFormat.Derive(_keys[1], B, A, a.Key)!;
        a.Links.SetPeers([(B, b.Key)]);
        // Someone on the topic with no key: what they send doesn't open; what they hear is sealed.
        var heardBySpy = new TaskCompletionSource<byte[]>();
        await using var spy = new CallRelayClient(_endpoint!, keys.Topic, "Spy");
        spy.Received += frame => { if (!frame.Gone) heardBySpy.TrySetResult(frame.Payload); };
        spy.Connected += () => _ = spy.SendAsync(new byte[1565]);
        spy.Start();
        var seen = await heardBySpy.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Hades", System.Text.Encoding.UTF8.GetString(seen));
        using var wrong = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(FriendLinkFormat.Open(FriendLinkFormat.Derive(wrong, B, A, a.Key)!, seen));
        await Quiet(TimeSpan.FromSeconds(1));
        Assert.False(a.Heard.TryRead(out _));
    }

    // The other side, played by hand: what it sends is sealed with the friend's own keys.
    private async Task<(CallRelayClient Relay, FriendLinkFormat.Keys Keys, Channel<byte[]> Heard)> Friend(Side a)
    {
        var friendKey = _keys[^1];
        var keys = FriendLinkFormat.Derive(friendKey, B, A, a.Key)!;
        var relay = new CallRelayClient(_endpoint!, keys.Topic, "Friend");
        var heard = Channel.CreateUnbounded<byte[]>();
        var connected = new TaskCompletionSource();
        relay.Received += frame => { if (!frame.Gone) heard.Writer.TryWrite(frame.Payload); };
        relay.Connected += () => connected.TrySetResult();
        relay.Start();
        await connected.Task.WaitAsync(Generous);
        return (relay, keys, heard);
    }

    [Fact]
    public async Task OldReflectedOrOlderMessagesThroughTheRelayAreRefused()
    {
        var a = NewSide(A);
        var b = NewSide(B); // B's key, played by hand below
        a.Links.SetPeers([(B, b.Key)]);
        var (relay, keys, _) = await Friend(a);
        await using var _1 = relay;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var game = new Dictionary<string, JsonNode?> { [FriendGame.Section] = Game("Hades II") };
        // Sealed in the other direction, as a replay of A's own would be.
        var reflected = keys with { Direction = (byte)(1 - keys.Direction) };
        await relay.SendAsync(FriendLinkFormat.Seal(reflected, now, false, game)!);
        // Older than the freshness limit.
        await relay.SendAsync(FriendLinkFormat.Seal(keys, now - FriendLinkFormat.FreshMs - 60_000, false, game)!);
        await Quiet(TimeSpan.FromSeconds(1));
        Assert.False(a.Heard.TryRead(out _));
        // A good one is heard, and one from before it isn't.
        await relay.SendAsync(FriendLinkFormat.Seal(keys, now, false, game)!);
        Assert.Equal("Hades II", GameIn((await Next(a, what: "A to accept the good message")).Message));
        await relay.SendAsync(FriendLinkFormat.Seal(keys, now - 5000, false, [])!);
        await Quiet(TimeSpan.FromSeconds(1));
        Assert.False(a.Heard.TryRead(out _));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(FriendLinks.AnswerAfterMs, false)]
    public async Task AHelloGetsAFreshMessageOnlyWhenTheLastOneIsOld(long answerAfterMs, bool answered)
    {
        var a = NewSide(A, answerAfterMs: answerAfterMs);
        var b = NewSide(B);
        a.Links.SetPeers([(B, b.Key)]);
        await UntilTheRelayHolds(a, b); // A is up
        // A's game goes out in one message after its short delay; once the relay holds that, A has nothing more to send, and the
        // message is the one a joining friend gets, and "old" when the limit is 0.
        a.Links.Set(FriendGame.Section, Game("Hades II"));
        await UntilTheRelayHolds(a, b, message => GameIn(message) is not null);
        var (relay, keys, heard) = await Friend(a);
        await using var _1 = relay;
        var held = await heard.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Generous, TestContext.Current.CancellationToken);
        Assert.NotNull(FriendLinkFormat.Open(keys, held));
        await relay.SendAsync(FriendLinkFormat.Seal(keys, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hello: true, [])!);
        await Next(a, what: "A to hear the hello");
        if (answered)
        {
            var fresh = FriendLinkFormat.Open(keys, await heard.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Generous, TestContext.Current.CancellationToken))!;
            Assert.False(fresh.Hello); // an answer asks nothing back, so it can't loop
            Assert.Equal("Hades II", GameIn(fresh));
        }
        else
        {
            await Quiet(TimeSpan.FromSeconds(1));
            Assert.False(heard.Reader.TryRead(out _));
        }
    }

    [Fact]
    public void ThePeersAreCappedAndThoseAlreadyUpStay()
    {
        var a = NewSide(A);
        var friends = Enumerable.Range(0, FriendLinks.MaxLinks + 4).Select(i => (Id: "9" + i.ToString("D17"), Key: FriendKey())).ToList();
        a.Links.SetPeers(friends);
        Assert.Equal(FriendLinks.MaxLinks, a.Links.Count);
        // The first one goes and a place opens for the next; a key that isn't one is skipped.
        a.Links.SetPeers(friends.Skip(1).Prepend(("777777777777777777", "not a key")).ToList());
        Assert.Equal(FriendLinks.MaxLinks, a.Links.Count);
        a.Links.SetPeers([]);
        Assert.Equal(0, a.Links.Count);
    }

    private string FriendKey()
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _keys.Add(key);
        return FriendLinkFormat.EncodeKey(key);
    }

    [Fact]
    public async Task WithTheRelayRefusingALinkBacksOffAndDoesntSpin()
    {
        // A socket that takes the connection and drops it at once, counting each try.
        using var refuser = new TcpListener(IPAddress.Loopback, 0);
        refuser.Start();
        var tries = 0;
        var counting = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await refuser.AcceptTcpClientAsync();
                Interlocked.Increment(ref tries);
            }
        }, TestContext.Current.CancellationToken);
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _keys.Add(key);
        var down = new FriendLinks(A, key, new Uri("ws://127.0.0.1:" + ((IPEndPoint)refuser.LocalEndpoint).Port));
        _links.Add(down);
        var since = Stopwatch.StartNew();
        down.SetPeers([(B, FriendKey())]);
        // It does try again (the second try is the proof) ...
        var limit = Stopwatch.StartNew();
        while (Volatile.Read(ref tries) < 2)
        {
            if (limit.Elapsed > Generous) throw Xunit.Sdk.FailException.ForFailure($"Waited {Generous.TotalSeconds:0} s for the link to try the refusing relay a second time, and it never did.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        await Quiet(TimeSpan.FromMilliseconds(600));
        // ... and it backs off: tries come at 0, 1, 3, 7, 15 ... s, so by any time there are at most as many as that table allows.
        var counted = Volatile.Read(ref tries);
        var seconds = since.Elapsed.TotalSeconds;
        var allowed = 1 + new[] { 1, 3, 7, 15, 31, 63 }.Count(at => at <= seconds);
        Assert.InRange(counted, 2, allowed);
        refuser.Stop();
        await Assert.ThrowsAnyAsync<Exception>(() => counting);
    }

    [Fact]
    public void WhatGoesThroughTheRelayIsSmallerThanItsLimit()
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var keys = FriendLinkFormat.Derive(a, A, B, FriendLinkFormat.EncodeKey(b))!;
        Assert.True(FriendLinkFormat.Seal(keys, 1, false, [])!.Length <= 2048);
    }
}

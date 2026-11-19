using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using ExoLauncher.Services.DiscordClient;
using ExoLauncher.Services.DiscordClient.Voice;
using Song = ExoLauncher.Services.DiscordClient.CallRelayFormat.Song;

namespace ExoLauncher.Tests;

/// <summary>
/// The call relay end to end: Exo's own <see cref="CallSongs"/> (relay client, keys, checks) as two or three members of
/// a call, through the real relay (relay/src/worker.js) running on this PC under relay/local/server.mjs, with the
/// Durable Object thrown away after every event as Cloudflare's hibernation does. Needs Node 22+.
///
/// Ways it could fail between two Exo apps, none of which shows as an error:
/// - a song never arrives, arrives for the wrong person, or with a position off by the time the relay held it;
/// - someone joining later sees nothing until the next song;
/// - a member of another call (another epoch's secret), or someone not in the call, gets a song through;
/// - the relay (or anyone watching the topic) can read the song;
/// - a pause, a stop, or leaving the call doesn't reach the others, so a stale song stays on screen;
/// - a new epoch drops everyone's song (the Jam box blinks, listen along pauses), or a member who stayed behind in an
///   old epoch keeps showing forever;
/// - the dave.dll Exo ships has no exporter, so no call ever gets a key.
/// </summary>
public sealed class CallRelayEndToEndTests : IAsyncLifetime
{
    private const string A = "111111111111111111", B = "222222222222222222", C = "333333333333333333";
    private static readonly Song Playing = new("Get Lucky", ["Daft Punk"], null, 369_000, 42_000, true, "spotify", "spotify:track:69kOkLUCkxIZYexIgSG8rq");
    private readonly List<CallSongs> _members = [];
    private Process? _relay;
    private Uri? _endpoint;

    public async ValueTask InitializeAsync()
    {
        var node = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, "node.exe")).FirstOrDefault(File.Exists);
        if (node is null) return;
        var start = new ProcessStartInfo(node, ["relay/local/server.mjs", "--port", "0", "--evict"])
        {
            WorkingDirectory = Repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _relay = Process.Start(start)!;
        var line = await _relay.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        _endpoint = new Uri("ws://127.0.0.1:" + line!["relay listening ".Length..]);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var member in _members) await member.StopAsync();
        if (_relay is null) return;
        _relay.StandardInput.Close();
        if (!_relay.WaitForExit(5000)) _relay.CloseMainWindow();
        _relay.Dispose();
    }

    [Fact]
    public async Task ASongReachesTheOtherMemberWhereItIsNow()
    {
        var secret = Secret(1);
        var (a, _) = Member(A, A, B);
        var (b, heardByB) = Member(B, A, B);
        var set = Environment.TickCount64;
        a.SetMine(Playing, set);
        a.UseSecret(secret);
        b.UseSecret(secret);
        var (user, heard) = await Next(heardByB, what: "B to hear A's song");
        Assert.Equal(A, user);
        Assert.Equal(Playing.Title, heard!.Song.Title);
        Assert.Equal(Playing.Id, heard.Song.Id);
        // Where A is now, on B's clock: 42 s plus the time it took, never behind and never more than the time that passed.
        var position = heard.Song.PositionMs + (Environment.TickCount64 - heard.At);
        Assert.InRange(position, 42_000, 42_000 + (Environment.TickCount64 - set) + 1000);
    }

    [Fact]
    public async Task ALatecomerHearsWhatIsPlaying()
    {
        var secret = Secret(2);
        var (a, _) = Member(A, A, B);
        var set = Environment.TickCount64;
        a.SetMine(Playing, set);
        a.UseSecret(secret);
        await Until(() => a.Connected, "A to connect to the relay");
        // The time the relay holds the song is what this test is about: B comes 1.5 s after A is up.
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        var (b, heardByB) = Member(B, A, B);
        b.UseSecret(secret);
        var (user, heard) = await Next(heardByB, what: "B to hear what A is playing");
        Assert.Equal(A, user);
        Assert.InRange(heard!.Song.PositionMs + (Environment.TickCount64 - heard.At), 43_000, 42_000 + (Environment.TickCount64 - set) + 1000);
    }

    [Fact]
    public async Task AnotherEpochOrSomeoneOutsideTheCallGetsNothingThrough()
    {
        var secret = Secret(3);
        var (b, heardByB) = Member(B, A, B);
        var (other, heardByOther) = Member(A, A, B);
        var (outsider, _) = Member(C, A, B, C);
        b.UseSecret(secret);
        other.UseSecret(Secret(4));
        outsider.SetMine(Playing, Environment.TickCount64);
        outsider.UseSecret(secret);
        b.SetMine(Playing, Environment.TickCount64);
        await Until(() => b.Connected && outsider.Connected && other.Connected, "everyone to connect to the relay");
        await Quiet(TimeSpan.FromSeconds(2));
        Assert.False(heardByB.TryRead(out _));
        Assert.False(heardByOther.TryRead(out _));
    }

    [Fact]
    public async Task TheRelayAndAnyoneOnTheTopicSeeOnlyPaddedCiphertext()
    {
        var secret = Secret(5);
        var (a, _) = Member(A, A, B);
        a.SetMine(Playing with { Title = "A title nobody else should read" }, Environment.TickCount64);
        a.UseSecret(secret);
        using var watcher = new ClientWebSocket();
        await watcher.ConnectAsync(new Uri($"{_endpoint}v1/{CallRelayFormat.Derive(secret).Topic}"), TestContext.Current.CancellationToken);
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var got = await watcher.ReceiveAsync(buffer, timeout.Token);
        var frame = CallRelayFormat.ParseFrame(buffer.AsSpan(0, got.Count))!;
        Assert.False(frame.Gone);
        Assert.Contains(frame.Payload.Length - 29, new[] { 256, 512, 1024, 1536 });
        var text = Encoding.UTF8.GetString(frame.Payload) + Encoding.Latin1.GetString(frame.Payload);
        Assert.DoesNotContain("nobody", text, StringComparison.Ordinal);
        Assert.DoesNotContain(A, text, StringComparison.Ordinal);
        Assert.DoesNotContain("spotify", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PauseStopAndLeavingReachTheOthers()
    {
        var secret = Secret(6);
        var (a, _) = Member(A, A, B);
        var (b, heardByB) = Member(B, A, B);
        a.SetMine(Playing, Environment.TickCount64);
        a.UseSecret(secret);
        b.UseSecret(secret);
        Assert.True((await Next(heardByB)).Heard!.Song.Playing);

        a.SetMine(Playing with { Playing = false }, Environment.TickCount64);
        Assert.False((await Next(heardByB)).Heard!.Song.Playing);

        a.SetMine(null, Environment.TickCount64);
        Assert.Null((await Next(heardByB)).Heard);

        a.SetMine(Playing, Environment.TickCount64);
        Assert.NotNull((await Next(heardByB)).Heard);
        await a.StopAsync();
        var (user, heard) = await Next(heardByB);
        Assert.Equal(A, user);
        Assert.Null(heard);
    }

    [Fact]
    public async Task ANewEpochMovesEveryoneWithoutDroppingTheSong()
    {
        // The old epoch's song stays this long for its sender to say it again; long enough that moving A across, on a PC
        // that is busy, cannot be mistaken for A having gone. The last step below waits this out for real.
        const int sweepMs = 3000;
        var (a, _) = MemberWithSweep(sweepMs, A, A, B);
        var (b, heardByB) = MemberWithSweep(sweepMs, B, A, B);
        a.SetMine(Playing, Environment.TickCount64);
        a.UseSecret(Secret(7));
        b.UseSecret(Secret(7));
        Assert.NotNull((await Next(heardByB, what: "B to hear A's song")).Heard);
        // Said once: the song set before connecting goes with the connection, not again after it.
        await Quiet(TimeSpan.FromSeconds(1.5));
        Assert.False(heardByB.TryRead(out _));

        // Someone joined: both move to the next epoch. A's song is said again there, and never goes in between.
        b.UseSecret(Secret(8));
        await Until(() => b.Connected, "B to connect on the next epoch");
        a.UseSecret(Secret(8));
        var (user, heard) = await Next(heardByB, what: "A's song, said again on the next epoch");
        Assert.Equal(A, user);
        Assert.NotNull(heard);
        // Longer than the sweep: a song said again on the new epoch must survive it.
        await Quiet(TimeSpan.FromMilliseconds(sweepMs + 1500));
        Assert.False(heardByB.TryRead(out _));

        // A is left behind in an old epoch (its Exo missed the change): after the sweep B stops showing its song.
        a.UseSecret(Secret(9));
        b.UseSecret(Secret(10));
        Assert.Null((await Next(heardByB, what: "B to stop showing A's song after the sweep")).Heard);
    }

    [Fact]
    public void TheShippedDaveLibraryHasTheExporter()
    {
        var shipped = Repo.PathOf("voice", "out", "dave.dll");
        Assert.SkipUnless(File.Exists(shipped), "dave.dll isn't built here (voice\\build.ps1)");
        // Equal secrets for every member and a new one per epoch are libdave's own test (voice\dave-exporter.patch,
        // MlsSessionTests.SessionExportSecretIsSharedPerEpoch); here: the build Exo ships, and the one it loads, have it.
        foreach (var path in new[] { shipped, Path.Combine(AppContext.BaseDirectory, "dave.dll") })
        {
            var library = NativeLibrary.Load(path);
            try { Assert.True(NativeLibrary.TryGetExport(library, "daveSessionExportSecret", out _), path); }
            finally { NativeLibrary.Free(library); }
        }
        using var session = new DaveSession(A, 1, (_, _) => { }, (_, _) => { });
        Assert.Null(session.ExportCallSecret());
    }

    private (CallSongs Songs, ChannelReader<(string User, CallSongs.Heard? Heard)> Heard) Member(string me, params string[] call) =>
        MemberWithSweep(1000, me, call);

    private (CallSongs Songs, ChannelReader<(string User, CallSongs.Heard? Heard)> Heard) MemberWithSweep(int sweepMs, string me, params string[] call)
    {
        Assert.SkipWhen(_endpoint is null, "Node isn't installed here (the local relay needs it)");
        var songs = new CallSongs(me, _endpoint, user => call.Contains(user)) { SweepMs = sweepMs };
        var heard = Channel.CreateUnbounded<(string, CallSongs.Heard?)>();
        songs.Changed += (user, h) => heard.Writer.TryWrite((user, h));
        _members.Add(songs);
        return (songs, heard.Reader);
    }

    private static byte[] Secret(byte seed) => [.. Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + seed))];

    // A wait is on what the test is about, with a limit only so a broken build ends: a loaded PC takes seconds, not milliseconds.
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static async Task<(string User, CallSongs.Heard? Heard)> Next(ChannelReader<(string User, CallSongs.Heard? Heard)> reader, TimeSpan? wait = null, string what = "a member's song to change")
    {
        using var timeout = new CancellationTokenSource(wait ?? Generous);
        try { return await reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw Xunit.Sdk.FailException.ForFailure($"Waited {(wait ?? Generous).TotalSeconds:0} s for {what}, and it never came.");
        }
    }

    private static async Task Until(Func<bool> condition, string what = "the member to connect to the relay")
    {
        var limit = Stopwatch.StartNew();
        while (!condition())
        {
            if (limit.Elapsed > Generous) throw Xunit.Sdk.FailException.ForFailure($"Waited {Generous.TotalSeconds:0} s for {what}, and it never happened.");
            await Task.Delay(20);
        }
    }

    // Nothing may happen for a while: the one wait that is time itself (a longer one only makes the check stricter).
    private static Task Quiet(TimeSpan span) => Task.Delay(span, TestContext.Current.CancellationToken);
}

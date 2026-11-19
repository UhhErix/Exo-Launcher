using ExoLauncher.Services;
using static ExoLauncher.Services.CaptureReel;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a highlight reel could cost the user a clip, a game its frames, or a share its quality:
/// - the clips come out in the wrong order, or a name Windows refuses, or over a file that is there;
/// - clips that differ are joined as they are (a broken reel), or clips of one kind are encoded again (a slow, lossy reel);
/// - it starts during a game or a recording, beside another reel, or on a drive too full for it;
/// - HDR clips of different kinds are squeezed into an SDR picture;
/// - a stopped, failed or refused join touches a source clip, or asks the add-on anything first;
/// - a copy the add-on finds impossible after all fails the user instead of being encoded.
/// The join itself (the add-on's muxer, on real files) is implemented in capture/ExoCapture/Edit/Clips.cs and checked by capture/ExoCapture.Tests/MuxerTests.cs.
/// </summary>
public sealed class CaptureReelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-reel-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Noon = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    public CaptureReelTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { DeletionGate.DeleteDirectory(_dir, true); } catch (IOException) { } }

    private Clip ClipOn(string folder, string name, double minutes, long bytes = 1000, string? gameId = null)
    {
        var path = Path.Combine(_dir, folder, name + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name);
        return new Clip(path, Noon.AddMinutes(minutes), bytes, folder, gameId);
    }

    private static Facts Info(string signature = "h264", int width = 1920, int height = 1080, bool hdr = false) =>
        new(signature, width, height, 60, 3, hdr, 30_000);

    /// <summary>An add-on that records what it was asked and answers as told.</summary>
    private sealed class FakeEngine : IReelEngine
    {
        public List<string[]> FactsAsked { get; } = [];
        public List<JoinJob> Joins { get; } = [];
        public Func<IReadOnlyList<string>, IReadOnlyList<Facts?>> Reads { get; set; } = paths => [.. paths.Select(_ => (Facts?)Info())];
        public Func<JoinJob, CancellationToken, JoinResult> Join { get; set; } = (job, _) => new JoinResult(job.Output, null, null);

        public Task<IReadOnlyList<Facts?>> FactsAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            FactsAsked.Add([.. paths]);
            return Task.FromResult(Reads(paths));
        }

        public Task<JoinResult> JoinAsync(JoinJob job, CancellationToken ct)
        {
            Joins.Add(job);
            return Task.FromResult(Join(job, ct));
        }
    }

    private Task<Outcome> Make(IReadOnlyList<Clip> clips, FakeEngine engine, long free = long.MaxValue) =>
        MakeAsync(clips, _dir, free, engine, TestContext.Current.CancellationToken);

    // ---------------------------------------------------------------- order and names

    [Fact]
    public void ClipsAreJoinedOldestFirstWhateverOrderTheyWerePicked()
    {
        var late = ClipOn("Hades", "late", 90);
        var early = ClipOn("Hades", "early", 0);
        var middle = ClipOn("Hades", "middle", 30);
        Assert.Equal([early.Path, middle.Path, late.Path], Order([late, early, middle]).Select(c => c.Path));
    }

    [Fact]
    public void ClipsMadeAtTheSameMomentKeepTheOrderOfTheirNames()
    {
        var b = ClipOn("Hades", "b", 5);
        var a = ClipOn("Hades", "A", 5);
        Assert.Equal([a.Path, b.Path], Order([b, a]).Select(c => c.Path));
    }

    [Fact]
    public async Task TheAddOnIsAskedForTheClipsInTimeOrder()
    {
        var engine = new FakeEngine();
        var late = ClipOn("Hades", "late", 90);
        var early = ClipOn("Hades", "early", 0);
        var result = await Make([late, early], engine);
        Assert.True(result.Ok);
        Assert.Equal([early.Path, late.Path], engine.FactsAsked.Single());
        Assert.Equal([early.Path, late.Path], engine.Joins.Single().Paths);
    }

    [Fact]
    public void AReelOfOneGameIsNamedForItAndTheDayOfItsFirstClip()
    {
        var (folder, name) = NameFor(Order([ClipOn("Hades", "a", 0), ClipOn("Hades", "b", 20)]));
        Assert.Equal("Hades", folder);
        Assert.Equal("Hades reel 2026-10-28", name);
    }

    [Fact]
    public void AReelOfSeveralGamesGoesInTheReelsFolder()
    {
        var (folder, name) = NameFor(Order([ClipOn("Hades", "a", 0), ClipOn("Elden Ring", "b", 20)]));
        Assert.Equal(MixedFolder, folder);
        Assert.Equal("Reel 2026-10-28", name);
    }

    [Fact]
    public void AGameFolderThatDiffersOnlyInCaseIsOneGame()
    {
        var (folder, _) = NameFor(Order([ClipOn("Hades", "a", 0), ClipOn("hades", "b", 20)]));
        Assert.Equal("Hades", folder);
    }

    [Fact]
    public void TheReelBelongsToTheGameAllItsClipsDo()
    {
        Assert.Equal("steam:1", GameOf(Order([ClipOn("A", "a", 0, gameId: "steam:1"), ClipOn("A", "b", 1, gameId: "steam:1")])));
        Assert.Null(GameOf(Order([ClipOn("A", "a", 0, gameId: "steam:1"), ClipOn("B", "b", 1, gameId: "steam:2")])));
        Assert.Null(GameOf(Order([ClipOn("A", "a", 0), ClipOn("A", "b", 1)])));
    }

    [Fact]
    public async Task TheReelIsAskedForInsideTheCapturesFolderUnderItsName()
    {
        var engine = new FakeEngine();
        var result = await Make([ClipOn("Hades", "a", 0), ClipOn("Hades", "b", 20)], engine);
        Assert.Equal(Path.Combine(_dir, "Hades", "Hades reel 2026-10-28.mp4"), engine.Joins.Single().Output);
        Assert.Equal(engine.Joins.Single().Output, result.Path);
    }

    // ---------------------------------------------------------------- how they join

    [Fact]
    public async Task ClipsOfOneKindJoinAsTheyAreWithoutEncoding()
    {
        var engine = new FakeEngine();
        await Make([ClipOn("A", "a", 0), ClipOn("A", "b", 1), ClipOn("A", "c", 2)], engine);
        var job = engine.Joins.Single();
        Assert.Equal(JoinMode.Copy, job.Mode);
        Assert.Equal(3, job.Paths.Count);
    }

    [Theory]
    [InlineData("hevc 1080p60", "h264 1080p60")]       // another codec
    [InlineData("h264 1080p60", "h264 720p60")]        // another size
    [InlineData("h264 1080p60", "h264 1080p30")]       // another frame rate
    [InlineData("h264 1080p60 3 tracks", "h264 1080p60 2 tracks")] // another sound layout
    [InlineData("h264 sps a", "h264 sps b")]           // the same codec set up another way
    public void ClipsThatDifferAreEncodedAgainNeverCopied(string first, string second)
    {
        Assert.Equal(JoinMode.Reencode, Decide([Info(first), Info(second)]).Mode);
    }

    [Fact]
    public void ClipsOfOneSignatureAreCopied()
    {
        Assert.Equal(JoinMode.Copy, Decide([Info("x"), Info("x"), Info("x")]).Mode);
        Assert.Null(Decide([Info("x"), Info("x")]).Refusal);
    }

    [Fact]
    public void AnEncodedReelTakesTheLargestClipsSizeMadeEven()
    {
        var decision = Decide([Info("a", 1280, 720), Info("b", 2561, 1441), Info("c", 1920, 1080)]);
        Assert.Equal(JoinMode.Reencode, decision.Mode);
        Assert.Equal((2560, 1440), (decision.Width, decision.Height));
    }

    [Fact]
    public async Task ClipsThatDifferAreEncodedAtTheLargestSize()
    {
        var engine = new FakeEngine { Reads = paths => [Info("a", 1280, 720), Info("b", 1920, 1080)] };
        await Make([ClipOn("A", "a", 0), ClipOn("A", "b", 1)], engine);
        var job = engine.Joins.Single();
        Assert.Equal((JoinMode.Reencode, 1920, 1080), (job.Mode, job.Width, job.Height));
    }

    [Fact]
    public async Task HdrClipsThatDifferAreRefusedNotSquashedToSdr()
    {
        var engine = new FakeEngine { Reads = paths => [Info("a", hdr: true), Info("b")] };
        var result = await Make([ClipOn("A", "a", 0), ClipOn("A", "b", 1)], engine);
        Assert.False(result.Ok);
        Assert.Contains("HDR", result.Message);
        Assert.Empty(engine.Joins);
    }

    [Fact]
    public async Task HdrClipsOfOneKindJoinAsTheyAre()
    {
        var engine = new FakeEngine { Reads = paths => [Info("a", hdr: true), Info("a", hdr: true)] };
        Assert.True((await Make([ClipOn("A", "a", 0), ClipOn("A", "b", 1)], engine)).Ok);
        Assert.Equal(JoinMode.Copy, engine.Joins.Single().Mode);
    }

    [Fact]
    public async Task ACopyTheAddOnRefusesIsEncodedInstead()
    {
        var engine = new FakeEngine
        {
            Reads = paths => [Info("a", 1280, 720), Info("a", 1920, 1080)],
            Join = (job, _) => job.Mode == JoinMode.Copy ? new JoinResult(null, "incompatible", "the clips are not of one kind") : new JoinResult(job.Output, null, null),
        };
        var result = await Make([ClipOn("A", "a", 0), ClipOn("A", "b", 1)], engine);
        Assert.True(result.Ok);
        Assert.Equal([JoinMode.Copy, JoinMode.Reencode], engine.Joins.Select(j => j.Mode));
        Assert.Equal((1920, 1080), (engine.Joins[1].Width, engine.Joins[1].Height));
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public async Task FewerThanTwoClipsIsNoReelAndAsksNothing()
    {
        var engine = new FakeEngine();
        var result = await Make([ClipOn("A", "a", 0)], engine);
        Assert.False(result.Ok);
        Assert.Empty(engine.FactsAsked);
        Assert.Empty(engine.Joins);
    }

    [Fact]
    public async Task TooManyClipsAreRefusedBeforeAnythingIsRead()
    {
        var engine = new FakeEngine();
        var clips = Enumerable.Range(0, MostClips + 1).Select(i => ClipOn("A", "c" + i, i)).ToList();
        var result = await Make(clips, engine);
        Assert.False(result.Ok);
        Assert.Empty(engine.FactsAsked);
    }

    [Fact]
    public async Task ADriveTooFullForTheReelIsRefusedBeforeAnythingIsRead()
    {
        var engine = new FakeEngine();
        var clips = new[] { ClipOn("A", "a", 0, bytes: 5_000_000_000), ClipOn("A", "b", 1, bytes: 5_000_000_000) };
        var result = await Make(clips, engine, free: 9_000_000_000);
        Assert.False(result.Ok);
        Assert.Contains("room", result.Message);
        Assert.Empty(engine.FactsAsked);
        Assert.True((await Make(clips, engine, free: 12_000_000_000)).Ok);
    }

    [Fact]
    public async Task AClipTheAddOnCannotReadIsNamedAndNothingIsJoined()
    {
        var engine = new FakeEngine { Reads = paths => [Info(), null] };
        var result = await Make([ClipOn("A", "a", 0), ClipOn("A", "b", 1)], engine);
        Assert.False(result.Ok);
        Assert.Empty(engine.Joins);
    }

    [Theory]
    [InlineData(false, false, false, false, "Start")]
    [InlineData(true, false, false, false, "Refuse")]   // another reel is being made
    [InlineData(false, true, false, false, "Refuse")]   // a game is running: refused, and the page may offer to wait
    [InlineData(false, true, false, true, "Wait")]      // asked to wait: held until the game ends
    [InlineData(false, false, true, false, "Refuse")]   // a recording is being made
    [InlineData(false, false, true, true, "Refuse")]    // waiting does not help a recording
    [InlineData(true, true, false, true, "Refuse")]     // a second reel never waits beside the first
    public void ANewReelStartsOnlyWhenNothingElseHoldsThePc(bool reel, bool game, bool recording, bool wait, string expected)
    {
        Assert.Equal(expected, Admit(reel, game, recording, wait).Go.ToString());
    }

    [Fact]
    public void AGameRefusalOffersToWaitAndTheOthersDoNot()
    {
        var game = Admit(false, true, false, false);
        Assert.True(game.Waits);
        Assert.Contains("game", game.Message);
        Assert.False(Admit(false, false, true, false).Waits);
        Assert.False(Admit(true, false, false, false).Waits);
        Assert.Null(Admit(false, false, false, false).Message);
        Assert.Null(Admit(false, true, false, true).Message);
    }

    // ---------------------------------------------------------------- stopped, failed, and the sources

    [Fact]
    public async Task AStoppedJoinIsStoppedNotAFailureAndHasNoReel()
    {
        var engine = new FakeEngine { Join = (_, _) => new JoinResult(null, "cancelled", null) };
        var result = await Make([ClipOn("A", "a", 0), ClipOn("A", "b", 1)], engine);
        Assert.True(result.Cancelled);
        Assert.False(result.Ok);
        Assert.Null(result.Path);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task ACancellationBeforeTheAddOnAnswersIsAStop()
    {
        var engine = new FakeEngine { Join = (_, ct) => throw new OperationCanceledException(ct) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var result = await MakeAsync([ClipOn("A", "a", 0), ClipOn("A", "b", 1)], _dir, long.MaxValue, engine, cts.Token);
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task AFailureSaysSoWithoutPathsAndKeepsTheDetailForTheLog()
    {
        var engine = new FakeEngine { Join = (_, _) => new JoinResult(null, "failed", @"write sample (0x80004005) in C:\Users\x\Videos\a.mp4") };
        var result = await Make([ClipOn("A", "a", 0), ClipOn("A", "b", 1)], engine);
        Assert.False(result.Ok);
        Assert.False(result.Cancelled);
        Assert.DoesNotContain("Users", result.Message);
        Assert.Contains("Users", result.Detail);
    }

    [Fact]
    public async Task AnAddOnThatDiesMidJoinIsAFailureNotACrash()
    {
        var engine = new FakeEngine { Join = (_, _) => throw new InvalidOperationException("Exo Capture stopped.") };
        var result = await Make([ClipOn("A", "a", 0), ClipOn("A", "b", 1)], engine);
        Assert.False(result.Ok);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task TheSourceClipsAreNeverChangedOrMovedWhateverHappens()
    {
        var clips = new[] { ClipOn("A", "a", 0), ClipOn("A", "b", 1) };
        var before = clips.Select(c => (File.ReadAllBytes(c.Path), File.GetLastWriteTimeUtc(c.Path))).ToList();
        foreach (var join in new Func<JoinJob, CancellationToken, JoinResult>[]
        {
            (job, _) => new JoinResult(job.Output, null, null),
            (_, _) => new JoinResult(null, "cancelled", null),
            (_, _) => new JoinResult(null, "failed", "boom"),
            (_, _) => new JoinResult(null, "incompatible", null),
        })
            await Make(clips, new FakeEngine { Join = join });
        Assert.All(clips, (c, i) =>
        {
            Assert.Equal(before[i].Item1, File.ReadAllBytes(c.Path));
            Assert.Equal(before[i].Item2, File.GetLastWriteTimeUtc(c.Path));
        });
    }
}

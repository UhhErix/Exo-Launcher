using ExoCapture.Audio;
using ExoCapture.Capture;

namespace ExoCapture.Tests;

/// <summary>
/// Instant replay's budget. Ways it could fail: it keeps more than the replay's seconds (memory grows with play time)
/// or less (a save is short); it starts on a frame that isn't a key frame (the clip's first second is grey); it keeps
/// going past its memory budget on a high bit rate; a save of N seconds takes the wrong key frame; what it reports as
/// held is not what a save would hold; the sound ring puts a packet in the wrong place, so voices drift off the picture.
/// </summary>
public sealed class ReplayTests
{
    private const long Second = 10_000_000;

    /// <summary>A minute at 60 fps, a key frame each second, <paramref name="bytes"/> a frame, and sound every 21 ms.</summary>
    private static IEnumerable<Packet> Stream(int seconds, int bytes = 1000)
    {
        for (var f = 0; f < seconds * 60; f++)
        {
            var time = f * Second / 60;
            yield return new Packet(Packet.Video, new byte[bytes], time, f % 60 == 0);
            if (f % 1 == 0) yield return new Packet(Tracks.Mix, new byte[100], time, true);
        }
    }

    [Fact]
    public void KeepsTheReplaysSecondsFromAKeyFrame()
    {
        var replay = new ReplayBuffer(10, 1L << 30);
        foreach (var p in Stream(60)) replay.Add(p);
        var kept = replay.Take();
        Assert.True(kept[0].IsVideo && kept[0].Key);
        var span = (kept.Last(p => p.IsVideo).Time - kept[0].Time) / (double)Second;
        // At least the 10 seconds asked for, and less than one more key-frame group past it.
        Assert.InRange(span, 10, 11);
        Assert.InRange(replay.RetainedMs, 10_000, 11_000);
    }

    [Fact]
    public void NothingBeforeTheFirstKeyFrame()
    {
        var replay = new ReplayBuffer(10, 1L << 30);
        replay.Add(new Packet(Packet.Video, new byte[10], 0, false));
        replay.Add(new Packet(Tracks.Mix, new byte[10], 0, true));
        Assert.Empty(replay.Take());
        replay.Add(new Packet(Packet.Video, new byte[10], Second / 60, true));
        Assert.Single(replay.Take());
    }

    [Fact]
    public void AMemoryBudgetCutsWholeSecondsFromTheFront()
    {
        // 60 KB a second of video against a 16 MB floor would hold 270 s: a 64 MB budget with 600 KB frames holds about 1.7 s.
        var budget = 64L << 20;
        var replay = new ReplayBuffer(600, budget);
        foreach (var p in Stream(30, bytes: 600_000)) replay.Add(p);
        Assert.InRange(replay.Bytes, 1, budget);
        var kept = replay.Take();
        Assert.True(kept[0].IsVideo && kept[0].Key);
        Assert.True(replay.RetainedMs < 30_000);
    }

    [Fact]
    public void ASaveOfFewerSecondsStartsOnTheLastKeyFrameThatCoversThem()
    {
        var replay = new ReplayBuffer(30, 1L << 30);
        foreach (var p in Stream(30)) replay.Add(p);
        var last5 = replay.Take(5);
        Assert.True(last5[0].IsVideo && last5[0].Key);
        var span = (last5.Last(p => p.IsVideo).Time - last5[0].Time) / (double)Second;
        Assert.InRange(span, 5, 6);
    }

    [Theory]
    // 128 MB at 20 Mbit/s of video (+0.64 for sound): about 51 s.
    [InlineData(60, 128L << 20, 20_000_000, 50, 52)]
    [InlineData(30, 1L << 30, 20_000_000, 30, 30)]
    [InlineData(600, 16L << 20, 150_000_000, 5, 5)] // never under five seconds
    public void TheLengthABudgetHolds(int asked, long bytes, long bitrate, int low, int high) =>
        Assert.InRange(ReplayBuffer.SecondsFor(asked, bytes, bitrate), low, high);

    [Fact]
    public void ClearEmptiesIt()
    {
        var replay = new ReplayBuffer(10, 1L << 30);
        foreach (var p in Stream(3)) replay.Add(p);
        replay.Clear();
        Assert.Equal(0, replay.Bytes);
        Assert.Empty(replay.Take());
    }

    [Fact]
    public void SoundLandsWhereItWasHeard()
    {
        var epoch = 1_000_000_000L;
        var ring = new SoundRing(epoch);
        var packet = Enumerable.Repeat(0.5f, 480 * 2).ToArray(); // 10 ms
        // Heard 100 ms after the epoch: sample 4800.
        ring.Write(packet, 480, epoch + Second / 10);
        var into = new float[1024 * 2];
        ring.Take(4800 - 100, 1024, into);
        Assert.Equal(0f, into[0]);
        Assert.Equal(0.5f, into[100 * 2]);
        Assert.Equal(0.5f, into[(100 + 479) * 2 + 1]);
        Assert.Equal(0f, into[(100 + 480) * 2]);
    }

    [Fact]
    public void APacketAFewMillisecondsOffJoinsTheOneBefore()
    {
        var epoch = 0L;
        var ring = new SoundRing(epoch);
        var packet = Enumerable.Repeat(0.25f, 480 * 2).ToArray();
        ring.Write(packet, 480, 0);
        // Stamped 3 ms late (a jittery driver): it follows on at sample 480, no gap and no overlap.
        ring.Write(packet, 480, 10 * Second / 1000 + 3 * Second / 1000);
        var into = new float[1024 * 2];
        ring.Take(0, 1024, into);
        for (var i = 0; i < 960; i++) Assert.Equal(0.25f, into[i * 2]);
        Assert.Equal(0f, into[960 * 2]);
    }

    [Fact]
    public void TakenSoundIsSilenceTheNextTimeRound()
    {
        var ring = new SoundRing(0, seconds: 1);
        ring.Write(Enumerable.Repeat(1f, 100 * 2).ToArray(), 100, 0);
        var first = new float[200];
        ring.Take(0, 100, first);
        var again = new float[200];
        ring.Take(48_000, 100, again); // the same slot a second later
        Assert.All(again, v => Assert.Equal(0f, v));
    }
}

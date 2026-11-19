using System.Diagnostics;
using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

// How a key change could hurt a call: a frame waits on the MLS group's work (a new group's key package, a commit, a
// welcome: public-key crypto, milliseconds) and goes out late; a frame sealed while the keys swap comes out wrong or not
// at all; the swap and a frame race on the encryptor and corrupt it; the session closing under a frame crashes Exo.
public sealed class DaveKeyChangeTests
{
    private const string Me = "100000000000000001";
    private const ulong Group = 400000000000000001;

    [Fact]
    public void AKeyChangeNeverHoldsUpAFrameBeingSealed()
    {
        Repo.RequireVoice("dave.dll");
        using var session = new DaveSession(Me, Group, (_, _) => { }, (_, _) => { });
        session.AssignSsrc(5);
        session.OnSessionDescription(0); // not end-to-end encrypted: every frame goes as it is, before, during and after
        var frame = Enumerable.Range(0, 160).Select(i => (byte)i).ToArray();
        long worst = 0, frames = 0, wrong = 0;
        var stop = false;
        var sealer = new Thread(() =>
        {
            var output = new byte[frame.Length + 64];
            while (!Volatile.Read(ref stop))
            {
                var started = Stopwatch.GetTimestamp();
                var length = session.EncryptFrame(5, frame, output);
                var took = Stopwatch.GetTimestamp() - started;
                if (length != frame.Length || !output.AsSpan(0, length).SequenceEqual(frame)) wrong++;
                worst = Math.Max(worst, took);
                frames++;
            }
        }) { IsBackground = true };
        sealer.Start();
        // Key changes while frames go: a new group each time (SessionInit and a fresh key package), then a transition
        // back to none, which swaps the encryptor's keys mid-stream.
        var changes = new List<long>();
        for (ushort i = 1; i <= 40; i++)
        {
            var started = Stopwatch.GetTimestamp();
            session.OnPrepareEpoch(1, 1);
            session.OnPrepareTransition(i, 0);
            session.OnExecuteTransition(i);
            changes.Add(Stopwatch.GetTimestamp() - started);
        }
        Volatile.Write(ref stop, true);
        sealer.Join();
        changes.Sort();
        var change = changes[changes.Count / 2];
        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        TestContext.Current.TestOutputHelper?.WriteLine($"{frames} frames sealed during 40 key changes; key change {Ms(change):F3} ms (median), slowest frame {Ms(worst):F3} ms");
        Assert.Equal(0, wrong);
        Assert.True(frames > 40, $"only {frames} frames went during the key changes");
        // A frame never waited out a whole key change: the slowest is under the median change (a frame that took the
        // group's lock waited the change's full length). Not half of it: a key change is under half a millisecond here, and a
        // busy PC's scheduler alone can stall one of 300,000 frames for 0.25 ms (4 of 4 runs on 2026-11-03, code unchanged since 3.18.0).
        Assert.True(worst < change, $"a frame took {Ms(worst):F3} ms while a key change takes {Ms(change):F3} ms");
    }

    [Fact]
    public void ClosingUnderFramesBeingSealedAndOpenedEndsThemQuietly()
    {
        Repo.RequireVoice("dave.dll");
        var session = new DaveSession(Me, Group, (_, _) => { }, (_, _) => { });
        session.AssignSsrc(5);
        session.OnSessionDescription(0);
        var frame = new byte[160];
        var failed = 0;
        var running = new CountdownEvent(2);
        var stop = false;
        Thread Worker(bool seal) => new(() =>
        {
            var output = new byte[frame.Length + 64];
            running.Signal();
            try
            {
                while (!Volatile.Read(ref stop))
                    if (seal) session.EncryptFrame(5, frame, output);
                    else session.DecryptFrame("100000000000000002", frame, output);
            }
            catch (Exception) { Interlocked.Increment(ref failed); }
        }) { IsBackground = true };
        var workers = new[] { Worker(true), Worker(false) };
        foreach (var w in workers) w.Start();
        running.Wait(TestContext.Current.CancellationToken);
        Thread.Sleep(20);
        session.Dispose();
        var output = new byte[frame.Length + 64];
        Assert.Equal(-1, session.EncryptFrame(5, frame, output));
        Volatile.Write(ref stop, true);
        foreach (var w in workers) w.Join();
        Assert.Equal(0, failed);
    }
}

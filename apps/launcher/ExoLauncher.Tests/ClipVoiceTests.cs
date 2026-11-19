using System.Text.Json.Nodes;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Tests;

/// <summary>
/// A replay's microphone is held raw and cleaned when it is saved. How that could fail, and the tests for it:
/// - Mic cleaning still runs while only a replay is held (the cost this exists to remove), or stops where it must go on:
///   a recording, an add-on that can't leave it raw, Exo's noise suppression missing, a call.
/// - The voice lands off the picture: the chain's sound comes out late and must be put back where it went in.
/// - The clip's first or last moments are lost: what the chain and the late gate still hold at the end, or the warm-up
///   left in the file.
/// - A span already clean (a call, a recording) is cleaned twice, or lands anywhere but where it was.
/// - The pauses come out loud (the late gate not run), or Input Volume is forgotten.
/// - The chain runs light on a slow frame: nothing here has a frame to keep up with, and light is worse sound.
/// </summary>
public sealed class ClipVoiceTests
{
    private const int Samples = Opus.FrameSamples, FrameBytes = Samples * 4;

    [Theory]
    // recording, holding, add-on cleans, Exo can clean: a chain of Exo's own?
    [InlineData(false, true, true, true, false)]    // only holding a replay: nothing runs
    [InlineData(true, false, true, true, true)]     // a recording is written as it goes
    [InlineData(true, true, true, true, true)]      // a recording during a replay
    [InlineData(false, true, false, true, true)]    // an add-on that can't leave it raw
    [InlineData(false, true, true, false, true)]    // Exo's noise suppression missing: Windows' own only works live
    [InlineData(false, false, true, true, false)]   // nothing at all
    public void ExoRunsItsOwnChainOnlyWhereCleaningLaterCant(bool recording, bool holding, bool addonCleans, bool canClean, bool own) =>
        Assert.Equal(own, RecordedVoice.OwnChain(recording, holding, addonCleans, canClean));

    [Theory]
    [InlineData(true, false, false, false)]         // holding a replay alone: no feed, the add-on's raw microphone stands
    [InlineData(true, true, false, true)]           // a recording
    [InlineData(true, false, true, true)]           // a call: its chain runs anyway, so the track takes its voice
    [InlineData(false, false, true, false)]         // a call with nothing captured
    [InlineData(false, false, false, false)]
    public void TheTrackTakesAVoiceOnlyWhereOneIsBeingMade(bool capturing, bool ownChain, bool call, bool feeds) =>
        Assert.Equal(feeds, RecordedVoice.Feeds(capturing, ownChain, call));

    // A voice that says where it is: each sample a distinct value.
    private static byte[] Voice(int frames, int extraSamples = 0)
    {
        var bytes = new byte[frames * FrameBytes + extraSamples * 4];
        for (var i = 0; i < bytes.Length / 4; i++)
        {
            var v = (short)(1000 + i % 20000);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), v);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4 + 2), v);
        }
        return bytes;
    }

    private static short[] Left(byte[] bytes)
    {
        var left = new short[bytes.Length / 4];
        for (var i = 0; i < left.Length; i++) left[i] = BitConverter.ToInt16(bytes, i * 4);
        return left;
    }

    private static byte[] Run(byte[] raw, IReadOnlyList<(long From, long To)> fed, Func<float[], bool> process, Func<int>? delay = null, bool gated = true, float gain = 1f)
    {
        using var input = new MemoryStream(raw);
        using var output = new MemoryStream();
        ClipVoice.Run(input, output, fed, process, delay ?? (() => 0), gated, gain);
        return output.ToArray();
    }

    // A chain that only delays, as noise suppression does, by some samples.
    private sealed class Late(int samples)
    {
        private readonly Queue<float> _held = new(Enumerable.Repeat(0f, samples * 2));

        internal bool Process(float[] frame)
        {
            for (var i = 0; i < frame.Length; i++)
            {
                _held.Enqueue(frame[i]);
                frame[i] = _held.Dequeue();
            }
            return true;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(480)]
    [InlineData(960)]
    [InlineData(1920)]
    public void TheVoiceLandsSampleForSampleWhereItWentIn(int delay)
    {
        var raw = Voice(700);
        var late = new Late(delay);
        var clean = Run(raw, [], late.Process, () => delay);
        Assert.Equal(raw.Length, clean.Length);
        // All of it, first sample to last: no warm-up left in, nothing lost to the chain or the late gate.
        Assert.Equal(Left(raw), Left(clean));
    }

    [Fact]
    public void TheVoiceLandsInPlaceWithoutTheGateToo()
    {
        var raw = Voice(100);
        var late = new Late(480);
        Assert.Equal(Left(raw), Left(Run(raw, [], late.Process, () => 480, gated: false)));
    }

    [Fact]
    public void AClipUnderAFrameLongAtItsEndKeepsEverySample()
    {
        var raw = Voice(60, extraSamples: 123);
        Assert.Equal(Left(raw), Left(Run(raw, [], _ => true)));
    }

    [Fact]
    public void AnEmptyClipIsNoTrouble() => Assert.Empty(Run([], [], _ => true));

    [Fact]
    public void ItHearsTheClipsOwnFirstSecondsBeforeCleaningIt()
    {
        var raw = Voice(ClipVoice.WarmFrames + 100);
        var firstSamples = new List<float>();
        Run(raw, [], frame => { firstSamples.Add(frame[0]); return true; });
        Assert.Equal(ClipVoice.WarmFrames + 100 + ClipVoice.WarmFrames + RecordingGate.Ahead, firstSamples.Count);
        // From the top twice: once to hear, once to clean.
        Assert.Equal(firstSamples[0], firstSamples[ClipVoice.WarmFrames]);
    }

    [Fact]
    public void AShortClipIsHeardOnceThroughFirst()
    {
        var raw = Voice(50);
        var runs = 0;
        Run(raw, [], _ => { runs++; return true; });
        Assert.Equal(50 + 50 + RecordingGate.Ahead, runs);
    }

    [Fact]
    public void ASpanAlreadyCleanStaysAsItIsAndTheChainNeverHearsIt()
    {
        var raw = Voice(800);
        var runs = 0;
        var clean = Run(raw, [(300, 500)], frame =>
        {
            runs++;
            for (var i = 0; i < frame.Length; i++) frame[i] *= 0.5f;
            return true;
        }, gated: false);
        var (rawLeft, cleanLeft) = (Left(raw), Left(clean));
        for (var i = 0; i < rawLeft.Length; i++)
        {
            var fed = i >= 300 * Samples && i < 500 * Samples;
            if (fed) Assert.Equal(rawLeft[i], cleanLeft[i]);
            else Assert.InRange(cleanLeft[i], rawLeft[i] / 2 - 1, rawLeft[i] / 2 + 1);
        }
        // Each side of the span and the warm-up, and not a frame of the span.
        Assert.Equal(ClipVoice.WarmFrames + 600, runs);
    }

    [Fact]
    public void AClipAllAlreadyCleanIsCopied()
    {
        var raw = Voice(200);
        var runs = 0;
        var clean = Run(raw, [(0, 200)], _ => { runs++; return true; });
        Assert.Equal(0, runs);
        Assert.Equal(raw, clean);
    }

    [Fact]
    public void TheVoiceBeforeACleanSpanIsNotCutOffByTheChainsDelay()
    {
        var raw = Voice(600);
        var late = new Late(960);
        var clean = Run(raw, [(550, 600)], late.Process, () => 960);
        Assert.Equal(Left(raw), Left(clean));
    }

    [Fact]
    public void ThePausesComeOutTurnedDownAsARecordingsDo()
    {
        var raw = Voice(100);
        var clean = Run(raw, [], _ => false);
        var (rawLeft, cleanLeft) = (Left(raw), Left(clean));
        var floor = MathF.Pow(10, RecordingGate.FloorDb / 20);
        // Past the first frame's glide down, every sample at the gate's floor.
        for (var i = Samples; i < rawLeft.Length; i++) Assert.InRange(cleanLeft[i], rawLeft[i] * floor - 2, rawLeft[i] * floor + 2);
    }

    [Fact]
    public void InputVolumeIsTheChainsToHear()
    {
        var raw = Voice(10);
        var seen = 0f;
        Run(raw, [], frame => { if (seen == 0) seen = frame[0]; return true; }, gain: 0.5f);
        Assert.Equal(1000f / 32767 * 0.5f, seen, 5);
    }

    [Fact]
    public void TheSpansInTheEventBecomeTheFramesTheyTouch()
    {
        var spans = JsonNode.Parse("[[0,20],[30,70],[1000.5,1000.6]]") as JsonArray;
        Assert.Equal([(0L, 1L), (1L, 4L), (50L, 51L)], ClipVoice.FedFrames(spans));
        Assert.Empty(ClipVoice.FedFrames(null));
    }

    [Fact]
    public void ACleanedClipNeverRunsLight()
    {
        NoiseSuppressorTests.UseTestPlugin();
        var processing = VoiceProcessing.ExoVoice;
        using var chain = new VoiceChain { Recording = true, Offline = true };
        Assert.True(chain.Prepare(processing));
        var random = new Random(4);
        var frame = new float[VoiceAudio.FrameFloats];
        for (var f = 0; f < 40; f++)
        {
            for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = (float)(random.NextDouble() * 2 - 1) * 0.02f;
            chain.Process(frame, processing, null, systemGain: false);
            Assert.Equal("on", chain.Stages.Suppression);
        }
    }

    [Fact]
    public void ALiveChainStillCanRunLight()
    {
        NoiseSuppressorTests.UseTestPlugin();
        using var suppressor = NoiseSuppressor.Create();
        Assert.NotNull(suppressor);
        Assert.False(suppressor.NeverLight);
    }
}

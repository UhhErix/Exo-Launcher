using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class VoiceAnalysisTests
{
    [Fact]
    public void AKeptTuningReadsBackTheSame()
    {
        var tuning = new VoiceTuning(15, -3.5f, [-6, 1, 0, -5, -9, -12, -15]);
        var back = VoiceTune.Parse(VoiceTune.Format(tuning))!;
        Assert.Equal((tuning.SuppressionLimitDb, tuning.GainDb), (back.SuppressionLimitDb, back.GainDb));
        Assert.Equal(tuning.Spectrum, back.Spectrum);
        Assert.Null(VoiceTune.Parse("limit=x;gain=0"));
        // A key given twice (a hand-edited settings file) takes the last instead of throwing on the audio thread.
        Assert.Equal(12, VoiceTune.Parse("limit=18;gain=0;limit=12")!.SuppressionLimitDb);
        // One kept before 3.11: its gate, low cut and hum are let go of, and "all the way" is read as the most.
        var old = VoiceTune.Parse("hp=70;gate=-52.5;limit=100;hum=60;gain=-3;spec=-6,1,0,-5,-9,-12,-15")!;
        Assert.Equal((NoiseSuppressor.MostDb, -3f), (old.SuppressionLimitDb, old.GainDb));
    }

    // A call with no kept tuning: three seconds of voice and one of quiet set the limit, then it stops.
    [Fact]
    public void ALiveListenSetsTheLimitOnceFromTheVoiceAndTheQuiet()
    {
        var live = new LiveTune();
        for (var i = 0; i < LiveTune.QuietFrames; i++)
            Assert.Null(Hear(live, "mic", -65, speaking: false, shut: true));
        for (var i = 0; i < LiveTune.SpeechFrames - 1; i++)
            Assert.Null(Hear(live, "mic", -20, speaking: true, shut: false));
        Assert.Equal(27, Hear(live, "mic", -20, speaking: true, shut: false));
        Assert.Null(Hear(live, "mic", -20, speaking: true, shut: false));

        var noisy = new LiveTune();
        int? made = null;
        for (var i = 0; i < LiveTune.QuietFrames; i++) Hear(noisy, "mic", -30, speaking: false, shut: true);
        for (var i = 0; i < LiveTune.SpeechFrames; i++) made = Hear(noisy, "mic", -10, speaking: true, shut: false);
        Assert.Equal(NoiseSuppressor.MostDb, made);
    }

    private static int? Hear(LiveTune live, string mic, float db, bool speaking, bool shut)
    {
        var frame = new float[Opus.FrameSamples * 2];
        Array.Fill(frame, MathF.Pow(10, db / 20));
        live.Sample(frame);
        return live.Classify(mic, speaking, shut);
    }
}

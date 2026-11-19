using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

// Ways reading the microphone by its sound could fail silently: a kept Voice Tune gain is added again on a mic already
// turned up to it (twice, where keeping it set Windows' level: the voice reached the chain 20 dB hot), or before the
// voice is heard at all; one peak touching full scale is called clipping, or real clipping isn't; a mic's own gate
// (digital silence between words) is taken for a quiet room; a quiet voice isn't said.
public sealed class MicSenseTests
{
    private const int Frame = 960;

    // A 20 ms stereo frame of a 200 Hz tone whose level is db (dBFS, RMS), and, if asked, samples at full scale.
    private static float[] Tone(float db, int fullScale = 0)
    {
        var frame = new float[Frame * 2];
        var peak = MathF.Sqrt(2) * MathF.Pow(10, db / 20);
        for (var i = 0; i < Frame; i++) frame[2 * i] = frame[2 * i + 1] = peak * MathF.Sin(2 * MathF.PI * 200 * i / 48000f);
        for (var i = 0; i < fullScale; i++) frame[i] = 1f;
        return frame;
    }

    private static void Talk(MicSense sense, float db, int frames, int fullScale = 0)
    {
        var frame = Tone(db, fullScale);
        for (var i = 0; i < frames; i++) { sense.Hear(frame); sense.Judge(speaking: true, shut: false); }
    }

    private static void Pause(MicSense sense, float[] frame, int frames)
    {
        for (var i = 0; i < frames; i++) { sense.Hear(frame); sense.Judge(speaking: false, shut: true); }
    }

    [Fact]
    public void NothingIsAddedBeforeTheVoiceIsHeard()
    {
        var sense = new MicSense();
        Talk(sense, -40f, MicSense.TrimFrom - 1);
        Assert.Equal(0f, sense.Missing(12f, VoiceChain.SpeakingDb));
        Talk(sense, -40f, 1);
        Assert.Equal(12f, sense.Missing(12f, VoiceChain.SpeakingDb), 0.5f);
        Assert.Null(sense.Reading(0f).SpeechDb);
    }

    [Fact]
    public void AKeptGainIsNotAddedWhereTheMicAlreadyReachesTheVoicesLevel()
    {
        var sense = new MicSense();
        Talk(sense, VoiceChain.SpeakingDb, 300);
        Assert.Equal(0f, sense.Missing(12f, VoiceChain.SpeakingDb), 0.5f);
        Assert.Equal(0f, sense.Missing(-6f, VoiceChain.SpeakingDb), 0.5f);
    }

    [Theory]
    [InlineData(-30f, 12f, 8f)]  // short of the level: adds what it is short of
    [InlineData(-40f, 12f, 12f)] // far short: never more than the tuning's own
    [InlineData(-14f, 12f, 0f)]  // turned up past it: adds nothing
    [InlineData(-14f, -6f, -6f)] // a hot mic's cut, as far as the tuning's
    [InlineData(-19f, -6f, -3f)] // and no further than it still needs
    [InlineData(-30f, -6f, 0f)]  // a cut never lifts
    public void AKeptGainAddsOnlyWhatTheVoiceStillNeeds(float speechDb, float tunedDb, float added)
    {
        var sense = new MicSense();
        Talk(sense, speechDb, 300);
        Assert.Equal(added, sense.Missing(tunedDb, VoiceChain.SpeakingDb), 0.5f);
    }

    [Fact]
    public void ClippingIsReadFromFullScaleSamplesNotOnePeak()
    {
        var touched = new MicSense();
        Talk(touched, -20f, 300, fullScale: 2); // one moment on both channels, every frame
        Assert.False(touched.Reading(0f).Clipping);

        var clipping = new MicSense();
        Talk(clipping, -20f, 290);
        Talk(clipping, -20f, 6, fullScale: 6);
        Assert.True(clipping.Reading(0f).Clipping);
        Assert.Equal("clipping", clipping.Reading(0f).Verdict);
    }

    [Fact]
    public void DigitalSilenceBetweenWordsIsAGateBeforeExo()
    {
        var gated = new MicSense();
        for (var i = 0; i < 4; i++) { Talk(gated, -24f, 100); Pause(gated, new float[Frame * 2], 150); }
        var reading = gated.Reading(0f);
        Assert.True(reading.Gated);
        Assert.Null(reading.QuietDb);
        Assert.Equal("gated", reading.Verdict);

        var room = new MicSense();
        for (var i = 0; i < 4; i++) { Talk(room, -24f, 100); Pause(room, Tone(-60f), 150); }
        reading = room.Reading(0f);
        Assert.False(reading.Gated);
        Assert.False(reading.Cleaned);
        Assert.Equal(-60f, reading.QuietDb!.Value, 1f);
        Assert.Equal("good", reading.Verdict);
    }

    [Fact]
    public void QuietBetweenWordsUnderAMicsOwnNoiseIsAlreadyCleaned()
    {
        var sense = new MicSense();
        for (var i = 0; i < 4; i++) { Talk(sense, -24f, 100); Pause(sense, Tone(-95f), 150); }
        Assert.True(sense.Reading(0f).Cleaned);
    }

    [Fact]
    public void AQuietVoiceIsSaid()
    {
        var sense = new MicSense();
        Talk(sense, -46f, 300);
        Assert.True(sense.Reading(0f).Quiet);
        Assert.Equal("quiet", sense.Reading(0f).Verdict);
    }

    [Fact]
    public void AQuietMicTheGateCutsIsLiftedOnItsOwn()
    {
        var sense = new MicSense();
        // The room at -80 dBFS between words, then a -55 dBFS voice the gate held shut every frame: 21 dB short, capped.
        Pause(sense, Tone(-80f), 300);
        Assert.Equal(0f, sense.AutoMissing());
        Pause(sense, Tone(-55f), MicSense.Learned);
        Assert.Equal(MicSense.AutoGainCapDb, sense.AutoMissing(), 0.5f);
    }

    [Fact]
    public void ALoudEnoughMicGetsNothingAdded()
    {
        var sense = new MicSense();
        Pause(sense, Tone(-80f), 300);
        Talk(sense, -28f, MicSense.Learned);
        Assert.Equal(0f, sense.AutoMissing());
        // A voice just under the leveler's reach gets only what it needs.
        var quiet = new MicSense();
        Pause(quiet, Tone(-80f), 300);
        Talk(quiet, -45f, MicSense.Learned);
        Assert.Equal(MicSense.QuietVoiceDb + 6f + 45f, quiet.AutoMissing(), 0.5f);   // 11 dB
    }
}

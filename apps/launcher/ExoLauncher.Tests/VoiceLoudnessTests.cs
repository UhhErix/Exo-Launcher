using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.Account;
using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class LoudnessMeterTests
{
    private static float[] GenerateMonoSine(double freqHz, double rmsDbFs, int sampleRate, double seconds)
    {
        var count = (int)Math.Round(sampleRate * seconds);
        var amp = Math.Pow(10.0, rmsDbFs / 20.0) * Math.Sqrt(2.0);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
            samples[i] = (float)(amp * Math.Sin(2.0 * Math.PI * freqHz * i / sampleRate));
        return samples;
    }

    private static float[] GenerateStereoSineFromPeak(double freqHz, double peakDbFs, int sampleRate, double seconds)
    {
        var frames = (int)Math.Round(sampleRate * seconds);
        var amp = Math.Pow(10.0, peakDbFs / 20.0);
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var s = (float)(amp * Math.Sin(2.0 * Math.PI * freqHz * i / sampleRate));
            samples[i * 2] = s;
            samples[i * 2 + 1] = s;
        }
        return samples;
    }

    [Fact]
    public void Mono1kHzAtMinus23DbfsReadsMinus23Lufs()
    {
        var meter = new LoudnessMeter(48000, 1, integratedCapacity: 512);
        var signal = GenerateMonoSine(1000.0, -23.0, 48000, 4.0);
        meter.Feed(signal);

        Assert.InRange(meter.MomentaryLufs, -23.1f, -22.9f);
        Assert.InRange(meter.ShortTermLufs, -23.1f, -22.9f);
        Assert.InRange(meter.IntegratedLufs, -23.1f, -22.9f);
    }

    [Theory]
    [InlineData(-23.0)]
    [InlineData(-33.0)]
    [InlineData(-18.0)]
    public void Ebu3341StereoPeakSinesReadExpectedLufs(double peakDbFs)
    {
        var meter = new LoudnessMeter(48000, 2, integratedCapacity: 512);
        var signal = GenerateStereoSineFromPeak(1000.0, peakDbFs, 48000, 4.0);
        meter.Feed(signal);

        Assert.InRange(meter.MomentaryLufs, (float)(peakDbFs - 0.1), (float)(peakDbFs + 0.1));
        Assert.InRange(meter.ShortTermLufs, (float)(peakDbFs - 0.1), (float)(peakDbFs + 0.1));
        Assert.InRange(meter.IntegratedLufs, (float)(peakDbFs - 0.1), (float)(peakDbFs + 0.1));
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(24000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void KWeightingCoefficientsWorkAcrossSampleRates(int sampleRate)
    {
        var meter = new LoudnessMeter(sampleRate, 1, integratedCapacity: 512);
        var signal = GenerateMonoSine(1000.0, -23.0, sampleRate, 4.0);
        meter.Feed(signal);

        Assert.InRange(meter.IntegratedLufs, -23.1f, -22.9f);
    }

    [Fact]
    public void KWeightingAppliesHighShelfAndRlbHighPass()
    {
        var lowMeter = new LoudnessMeter(48000, 1, integratedCapacity: 256);
        var midMeter = new LoudnessMeter(48000, 1, integratedCapacity: 256);
        var highMeter = new LoudnessMeter(48000, 1, integratedCapacity: 256);

        lowMeter.Feed(GenerateMonoSine(30.0, -23.0, 48000, 4.0));
        midMeter.Feed(GenerateMonoSine(1000.0, -23.0, 48000, 4.0));
        highMeter.Feed(GenerateMonoSine(4000.0, -23.0, 48000, 4.0));

        Assert.True(lowMeter.IntegratedLufs < midMeter.IntegratedLufs - 3.0f);
        Assert.InRange(highMeter.IntegratedLufs - midMeter.IntegratedLufs, 3.0f, 4.5f);
    }

    [Fact]
    public void AbsoluteGateExcludesBlocksBelowMinus70Lufs()
    {
        var quietOnly = new LoudnessMeter(48000, 1, integratedCapacity: 256);
        quietOnly.Feed(GenerateMonoSine(1000.0, -74.0, 48000, 2.0));
        Assert.False(quietOnly.HasShortTerm);
        Assert.Equal(LoudnessMeter.SilenceLufs, quietOnly.IntegratedLufs);

        var mixed = new LoudnessMeter(48000, 1, integratedCapacity: 1024);
        mixed.Feed(GenerateMonoSine(1000.0, -23.0, 48000, 10.0));
        mixed.Feed(GenerateMonoSine(1000.0, -75.0, 48000, 10.0));
        Assert.InRange(mixed.IntegratedLufs, -23.1f, -22.9f);
    }

    [Fact]
    public void RelativeGateMatchesEbu3341GatingBehavior()
    {
        var gatedOut = new LoudnessMeter(48000, 2, integratedCapacity: 1024);
        gatedOut.Feed(GenerateStereoSineFromPeak(1000.0, -36.0, 48000, 10.0));
        gatedOut.Feed(GenerateStereoSineFromPeak(1000.0, -23.0, 48000, 60.0));
        gatedOut.Feed(GenerateStereoSineFromPeak(1000.0, -36.0, 48000, 10.0));
        Assert.InRange(gatedOut.IntegratedLufs, -23.1f, -22.9f);

        var bothPass = new LoudnessMeter(48000, 2, integratedCapacity: 1024);
        bothPass.Feed(GenerateStereoSineFromPeak(1000.0, -20.0, 48000, 10.0));
        bothPass.Feed(GenerateStereoSineFromPeak(1000.0, -26.0, 48000, 10.0));
        var expected = (float)(10.0 * Math.Log10((Math.Pow(10.0, -2.0) + Math.Pow(10.0, -2.6)) * 0.5));
        Assert.InRange(bothPass.IntegratedLufs, expected - 0.1f, expected + 0.1f);
    }

    [Fact]
    public void MomentaryBlockUses400msWith75PercentOverlapAndShortTermUses3sGatedWindow()
    {
        var meter = new LoudnessMeter(48000, 1);
        var hop = GenerateMonoSine(1000.0, -23.0, 48000, 0.1);

        meter.Feed(hop);
        meter.Feed(hop);
        meter.Feed(hop);
        Assert.False(meter.HasShortTerm);

        meter.Feed(hop);
        Assert.True(meter.HasShortTerm);
        Assert.InRange(meter.MomentaryLufs, -23.1f, -22.9f);

        var quietHop = GenerateMonoSine(1000.0, -36.0, 48000, 0.1);
        for (var i = 0; i < 15; i++) meter.Feed(hop);
        for (var i = 0; i < 15; i++) meter.Feed(quietHop);
        var expectedTransition = (float)(-23.0 + 10.0 * Math.Log10((12.0 + 0.75 + 0.50 + 0.25) / 15.0));
        Assert.InRange(meter.ShortTermLufs, expectedTransition - 0.1f, expectedTransition + 0.1f);
    }

    [Fact]
    public void FeedAllocatesZeroBytesPerFrame()
    {
        var meter = new LoudnessMeter(48000, 2);
        var frame = new float[VoiceAudio.FrameFloats];
        for (var i = 0; i < Opus.FrameSamples; i++)
        {
            var s = 0.08f * MathF.Sin(2f * MathF.PI * 1000f * i / Opus.SampleRate);
            frame[i * 2] = s;
            frame[i * 2 + 1] = s;
        }
        for (var f = 0; f < 30; f++) meter.Feed(frame);

        GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 0; f < 100; f++) meter.Feed(frame);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }
}

public sealed class VoiceLoudnessLevelingTests
{
    private static void FillStereoTone(float[] frame, double targetLufs, int frameIndex, double freqHz = 1000.0, double noiseLufs = -100.0, Random? rng = null)
    {
        var toneAmp = Math.Pow(10.0, targetLufs / 20.0);
        var noiseAmp = noiseLufs > -95.0 ? Math.Pow(10.0, (noiseLufs - 3.0103) / 20.0) * Math.Sqrt(3.0) : 0.0;
        var baseSample = frameIndex * Opus.FrameSamples;
        for (var i = 0; i < Opus.FrameSamples; i++)
        {
            var s = toneAmp * Math.Sin(2.0 * Math.PI * freqHz * (baseSample + i) / Opus.SampleRate);
            if (noiseAmp > 0.0 && rng is not null) s += noiseAmp * (rng.NextDouble() * 2.0 - 1.0);
            frame[i * 2] = (float)s;
            frame[i * 2 + 1] = (float)s;
        }
    }

    private static void FillStereoNoise(float[] frame, double noiseLufs, Random rng)
    {
        var noiseAmp = Math.Pow(10.0, (noiseLufs - 3.0103) / 20.0) * Math.Sqrt(3.0);
        for (var i = 0; i < Opus.FrameSamples; i++)
        {
            var s = (float)(noiseAmp * (rng.NextDouble() * 2.0 - 1.0));
            frame[i * 2] = s;
            frame[i * 2 + 1] = s;
        }
    }

    [Theory]
    [InlineData(-26f)]
    [InlineData(-20f)]
    [InlineData(-14f)]
    public void CustomTargetLufsLevelsSpeakersAccurately(float targetLufs)
    {
        var leveling = new VoiceLeveling();
        var frame = new float[VoiceAudio.FrameFloats];
        var outMeter = new LoudnessMeter(Opus.SampleRate, Opus.Channels);

        for (var f = 0; f < 600; f++)
        {
            FillStereoTone(frame, targetLufs - 8.0, f);
            leveling.Process(1, frame, targetLufs);
            if (f >= 450) outMeter.Feed(frame);
        }

        Assert.InRange(outMeter.ShortTermLufs, targetLufs - 1.0f, targetLufs + 1.0f);
    }

    [Fact]
    public void GainSlewsSlowlyAttackFasterThanReleaseAndHoldsInSilence()
    {
        var attackLeveler = new VoiceLeveling();
        var releaseLeveler = new VoiceLeveling();
        var frame = new float[VoiceAudio.FrameFloats];

        for (var f = 0; f < 70; f++)
        {
            FillStereoTone(frame, -10.0, f);
            attackLeveler.Process(1, frame, -20f);
            FillStereoTone(frame, -30.0, f);
            releaseLeveler.Process(2, frame, -20f);
        }

        var attackGainAfter1s = attackLeveler.GainDb(1);
        var releaseGainAfter1s = releaseLeveler.GainDb(2);

        Assert.InRange(attackGainAfter1s, -7.8f, -4.5f);
        Assert.InRange(releaseGainAfter1s, 1.8f, 4.2f);
        Assert.True(MathF.Abs(attackGainAfter1s) > MathF.Abs(releaseGainAfter1s) * 1.5f);

        for (var f = 0; f < 250; f++)
        {
            Array.Clear(frame);
            releaseLeveler.Process(2, frame, -20f);
        }
        Assert.Equal(releaseGainAfter1s, releaseLeveler.GainDb(2), 4);
    }

    [Fact]
    public void GainRangeIsClampedToPlusMinus12Db()
    {
        var leveling = new VoiceLeveling();
        var frame = new float[VoiceAudio.FrameFloats];

        for (var f = 0; f < 1000; f++)
        {
            FillStereoTone(frame, -45.0, f);
            leveling.Process(1, frame, -20f);
            FillStereoTone(frame, -2.0, f);
            leveling.Process(2, frame, -20f);
        }

        Assert.InRange(leveling.GainDb(1), 11.5f, 12.01f);
        Assert.InRange(leveling.GainDb(2), -12.01f, -11.5f);
    }

    [Fact]
    public void SnrGateBlocksBoostForNoisyQuietSpeakerButAllowsCleanQuietSpeakerAndStillCutsLoudNoisySpeaker()
    {
        var leveling = new VoiceLeveling();
        var rngNoisy = new Random(11);
        var rngClean = new Random(12);
        var rngLoud = new Random(13);
        var frame = new float[VoiceAudio.FrameFloats];

        for (var f = 0; f < 50; f++)
        {
            FillStereoNoise(frame, -40.0, rngNoisy);
            leveling.Process(1, frame, -20f);
            FillStereoNoise(frame, -68.0, rngClean);
            leveling.Process(2, frame, -20f);
            FillStereoNoise(frame, -22.0, rngLoud);
            leveling.Process(3, frame, -20f);
        }

        for (var sentence = 0; sentence < 20; sentence++)
        {
            for (var f = 0; f < 35; f++)
            {
                if (f < 20)
                {
                    FillStereoTone(frame, -32.0, sentence * 35 + f, 1000.0, -40.0, rngNoisy);
                    leveling.Process(1, frame, -20f);

                    FillStereoTone(frame, -32.0, sentence * 35 + f, 1000.0, -68.0, rngClean);
                    leveling.Process(2, frame, -20f);

                    FillStereoTone(frame, -11.0, sentence * 35 + f, 1000.0, -22.0, rngLoud);
                    leveling.Process(3, frame, -20f);
                }
                else
                {
                    FillStereoNoise(frame, -40.0, rngNoisy);
                    leveling.Process(1, frame, -20f);

                    FillStereoNoise(frame, -68.0, rngClean);
                    leveling.Process(2, frame, -20f);

                    FillStereoNoise(frame, -22.0, rngLoud);
                    leveling.Process(3, frame, -20f);
                }
            }
        }

        Assert.InRange(leveling.GainDb(1), -0.5f, 0.1f);
        Assert.True(leveling.GainDb(2) > 7.5f, $"clean quiet speaker gain was {leveling.GainDb(2):0.00} dB");
        Assert.True(leveling.GainDb(3) < -5.0f, $"loud noisy speaker gain was {leveling.GainDb(3):0.00} dB");
    }

    [Fact]
    public void LevelingAllocatesZeroBytesPerFrameAfterSpeakerRegistration()
    {
        var leveling = new VoiceLeveling();
        var frame = new float[VoiceAudio.FrameFloats];
        for (var f = 0; f < 30; f++)
        {
            FillStereoTone(frame, -28.0, f);
            leveling.Process(42, frame, -20f);
        }

        GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 30; f < 130; f++)
        {
            FillStereoTone(frame, -28.0, f);
            leveling.Process(42, frame, -20f);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }
}

public sealed class SoftKneeLimiterTests
{
    [Fact]
    public void TransferCurveMatchesMinus1DbfsCeilingAnd6DbSoftKnee()
    {
        var limiter = new SoftKneeLimiter(48000, -1f, 6f, 2f);

        Assert.Equal(1f, limiter.TargetGain(MathF.Pow(10f, -10f / 20f)), 5);
        Assert.Equal(1f, limiter.TargetGain(MathF.Pow(10f, -4f / 20f)), 5);

        var atCeilingInput = MathF.Pow(10f, -1f / 20f);
        var expectedAtCeilingGain = MathF.Pow(10f, -0.75f / 20f);
        Assert.Equal(expectedAtCeilingGain, limiter.TargetGain(atCeilingInput), 4);

        var atKneeTop = MathF.Pow(10f, 2f / 20f);
        Assert.Equal(limiter.CeilingLinear / atKneeTop, limiter.TargetGain(atKneeTop), 4);
    }

    [Fact]
    public void LimiterNeverExceedsMinus1DbfsCeilingOnLoudSinesAndSuddenTransients()
    {
        var limiter = new SoftKneeLimiter(48000, -1f, 6f, 2f);
        var frame = new float[VoiceAudio.FrameFloats];
        var ceiling = MathF.Pow(10f, -1f / 20f);
        var maxPeak = 0f;

        for (var f = 0; f < 25; f++)
        {
            for (var i = 0; i < Opus.FrameSamples; i++)
            {
                var n = f * Opus.FrameSamples + i;
                var s = f == 5 && i == 100
                    ? 8.0f
                    : 2.0f * MathF.Sin(2f * MathF.PI * 1000f * n / Opus.SampleRate);
                frame[i * 2] = s;
                frame[i * 2 + 1] = s;
            }
            limiter.Process(frame);
            foreach (var sample in frame) maxPeak = MathF.Max(maxPeak, MathF.Abs(sample));
        }

        Assert.True(maxPeak <= ceiling + 1e-4f, $"peak {maxPeak} exceeded -1 dBFS ceiling {ceiling}");
        Assert.True(maxPeak > ceiling * 0.95f, $"peak {maxPeak} was unexpectedly far below ceiling {ceiling}");
    }

    [Fact]
    public void LimitedSineHasLowHarmonicDistortionComparedToHardClip()
    {
        var limiter = new SoftKneeLimiter(48000, -1f, 6f, 2f);
        var frame = new float[VoiceAudio.FrameFloats];
        var settled = new float[Opus.FrameSamples];

        for (var f = 0; f < 15; f++)
        {
            for (var i = 0; i < Opus.FrameSamples; i++)
            {
                var n = f * Opus.FrameSamples + i;
                var s = 2.0f * MathF.Sin(2f * MathF.PI * 1000f * n / Opus.SampleRate);
                frame[i * 2] = s;
                frame[i * 2 + 1] = s;
            }
            limiter.Process(frame);
            if (f == 14)
                for (var i = 0; i < Opus.FrameSamples; i++) settled[i] = frame[i * 2];
        }

        double totalEnergy = 0, cosSum = 0, sinSum = 0;
        for (var i = 0; i < settled.Length; i++)
        {
            double x = settled[i];
            totalEnergy += x * x;
            var phase = 2.0 * Math.PI * 1000.0 * i / Opus.SampleRate;
            cosSum += x * Math.Cos(phase);
            sinSum += x * Math.Sin(phase);
        }
        var fundamentalEnergy = 2.0 * (cosSum * cosSum + sinSum * sinSum) / settled.Length;
        var distortionRatio = Math.Sqrt(Math.Max(0.0, totalEnergy - fundamentalEnergy) / Math.Max(fundamentalEnergy, 1e-12));

        Assert.True(distortionRatio < 0.01, $"THD {distortionRatio:P3} exceeded 1%");
    }

    [Fact]
    public void BelowKneeSignalPassesThroughWithUnityGainAndZeroAllocations()
    {
        var limiter = new SoftKneeLimiter(48000, -1f, 6f, 2f);
        var frame = new float[VoiceAudio.FrameFloats];
        var amp = MathF.Pow(10f, -10f / 20f);

        for (var f = 0; f < 5; f++)
        {
            for (var i = 0; i < Opus.FrameSamples; i++)
            {
                var s = amp * MathF.Sin(2f * MathF.PI * 1000f * (f * Opus.FrameSamples + i) / Opus.SampleRate);
                frame[i * 2] = s;
                frame[i * 2 + 1] = s;
            }
            limiter.Process(frame);
        }

        var peak = 0f;
        foreach (var s in frame) peak = MathF.Max(peak, MathF.Abs(s));
        Assert.InRange(peak, amp * 0.99f, amp * 1.01f);

        GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 0; f < 100; f++) limiter.Process(frame);
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, after - before);
    }
}

public sealed class WebRtcAgc2Tests
{
    [Fact]
    public void MicAutoGainDefaultsOffInSettingsAndProcessing()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.DiscordMicAutoGain);
        Assert.False(VoiceProcessing.From(defaults).MicAutoGain);
    }

    [Fact]
    public void Agc2BoostsQuietSpeechAndWiresIntoVoiceChain()
    {
        Repo.RequireVoice("exo_apm.dll");
        using var agc2 = WebRtcAgc2.Create();
        Assert.NotNull(agc2);

        var frame = new float[VoiceAudio.FrameFloats];
        var inputAmp = MathF.Pow(10f, -38f / 20f) * MathF.Sqrt(2f);
        var lastLevel = -100f;
        var peak = 0f;
        for (var f = 0; f < 200; f++)
        {
            for (var i = 0; i < Opus.FrameSamples; i++)
            {
                var n = f * Opus.FrameSamples + i;
                var env = 0.5f + 0.5f * MathF.Sin(2f * MathF.PI * 4f * n / Opus.SampleRate);
                var s = inputAmp * env * (MathF.Sin(2f * MathF.PI * 180f * n / Opus.SampleRate)
                    + 0.5f * MathF.Sin(2f * MathF.PI * 540f * n / Opus.SampleRate)
                    + 0.25f * MathF.Sin(2f * MathF.PI * 1260f * n / Opus.SampleRate));
                frame[i * 2] = s;
                frame[i * 2 + 1] = s;
            }
            agc2!.Process(frame);
            foreach (var s in frame) peak = MathF.Max(peak, MathF.Abs(s));
            if (f == 199) lastLevel = SpeechLeveler.Level(frame);
        }

        Assert.True(lastLevel > -34f, $"AGC2 output level {lastLevel:0.0} dBFS did not lift quiet speech");
        Assert.True(peak <= 1.001f, $"AGC2 clipped peak {peak}");

        using var chain = new VoiceChain { StepsAside = false };
        var processing = new VoiceProcessing("custom", false, false, false, false, -60, 0, false, MicAutoGain: true);
        chain.Process(frame, processing, null, systemGain: false);
        Assert.Equal("agc2", chain.Stages.Gain);
    }
}

public sealed class VoiceLoudnessSettingsTests
{
    [Fact]
    public void VoiceLoudnessSettingsRoundTripAndClampBounds()
    {
        var settings = new AppSettings();
        Assert.True(settings.DiscordEvenVoices);
        Assert.Equal(-20, settings.DiscordVoiceTargetLufs);
        Assert.False(settings.DiscordMicAutoGain);
        Assert.Contains("DiscordVoiceTargetLufs", AccountSyncMap.DeviceSettings);
        Assert.Contains("DiscordMicAutoGain", AccountSyncMap.DeviceSettings);

        using (var doc = JsonDocument.Parse("""{"evenVoices":false,"voiceTargetLufs":-24,"micAutoGain":true}"""))
            WebHostBridge.ApplyClientSettings(settings, doc.RootElement, true, null, null);
        Assert.False(settings.DiscordEvenVoices);
        Assert.Equal(-24, settings.DiscordVoiceTargetLufs);
        Assert.True(settings.DiscordMicAutoGain);

        using (var lowDoc = JsonDocument.Parse("""{"voiceTargetLufs":-40}"""))
            WebHostBridge.ApplyClientSettings(settings, lowDoc.RootElement, true, null, null);
        Assert.Equal(-26, settings.DiscordVoiceTargetLufs);

        using (var highDoc = JsonDocument.Parse("""{"voiceTargetLufs":-6}"""))
            WebHostBridge.ApplyClientSettings(settings, highDoc.RootElement, true, null, null);
        Assert.Equal(-14, settings.DiscordVoiceTargetLufs);

        var cloned = SettingsService.Clone(settings);
        Assert.Equal(settings.DiscordEvenVoices, cloned.DiscordEvenVoices);
        Assert.Equal(settings.DiscordVoiceTargetLufs, cloned.DiscordVoiceTargetLufs);
        Assert.Equal(settings.DiscordMicAutoGain, cloned.DiscordMicAutoGain);

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(settings.DiscordEvenVoices, restored.DiscordEvenVoices);
        Assert.Equal(settings.DiscordVoiceTargetLufs, restored.DiscordVoiceTargetLufs);
        Assert.Equal(settings.DiscordMicAutoGain, restored.DiscordMicAutoGain);
    }
}

public sealed class VoiceInboundBenchmarkTests
{
    private const int Speakers = 8;
    private const int WarmupFrames = 50;
    private const int MeasureFrames = 500;

    private static float[][] BuildSpeakerSources()
    {
        var sources = new float[Speakers][];
        for (var s = 0; s < Speakers; s++)
        {
            var buf = new float[VoiceAudio.FrameFloats];
            var db = -32.0 + s * 2.5;
            var amp = Math.Pow(10.0, db / 20.0);
            var freq = 140.0 + s * 45.0;
            for (var i = 0; i < Opus.FrameSamples; i++)
            {
                var sample = (float)(amp * Math.Sin(2.0 * Math.PI * freq * i / Opus.SampleRate));
                buf[i * 2] = sample;
                buf[i * 2 + 1] = sample;
            }
            sources[s] = buf;
        }
        return sources;
    }

    private static double MeasureLegacyChainUsPerFrame(float[][] sources)
    {
        var levelers = Enumerable.Range(0, Speakers).Select(_ => new SpeechLeveler()).ToArray();
        var quiets = Enumerable.Range(0, Speakers).Select(_ => new QuietFloor()).ToArray();
        var knocks = Enumerable.Range(0, Speakers).Select(_ => new KnockDetector()).ToArray();
        var guards = Enumerable.Range(0, Speakers).Select(_ => new ShoutGuard()).ToArray();
        var work = new float[VoiceAudio.FrameFloats];
        var mix = new float[VoiceAudio.FrameFloats];

        void RunOnce()
        {
            Array.Clear(mix);
            for (var s = 0; s < Speakers; s++)
            {
                sources[s].CopyTo(work, 0);
                var db = SpeechLeveler.Level(work);
                var quiet = quiets[s].Db;
                quiets[s].Add(db);
                var knock = knocks[s].Hit(work);
                levelers[s].Process(work, db > -50f && db > quiet + 10f && !knock && !guards[s].Shouting(db, levelers[s].SpeechDb + VoiceLeveling.ShoutOverDb));
                guards[s].Process(work, SpeechLeveler.TargetDb + VoiceLeveling.ShoutOverDb);
                for (var i = 0; i < mix.Length; i++) mix[i] += work[i];
            }
            for (var i = 0; i < mix.Length; i++) mix[i] = MathF.Max(-1f, MathF.Min(1f, mix[i]));
        }

        for (var f = 0; f < WarmupFrames; f++) RunOnce();
        var sw = Stopwatch.StartNew();
        for (var f = 0; f < MeasureFrames; f++) RunOnce();
        sw.Stop();
        return sw.Elapsed.TotalMicroseconds / MeasureFrames;
    }

    private static double MeasureNewChainUsPerFrame(float[][] sources)
    {
        var leveling = new VoiceLeveling();
        var limiter = new SoftKneeLimiter();
        var work = new float[VoiceAudio.FrameFloats];
        var mix = new float[VoiceAudio.FrameFloats];

        void RunOnce()
        {
            Array.Clear(mix);
            for (var s = 0; s < Speakers; s++)
            {
                sources[s].CopyTo(work, 0);
                leveling.Process((uint)(s + 1), work, VoiceLeveling.DefaultTargetLufs);
                for (var i = 0; i < mix.Length; i++) mix[i] += work[i];
            }
            limiter.Process(mix);
        }

        for (var f = 0; f < WarmupFrames; f++) RunOnce();
        var sw = Stopwatch.StartNew();
        for (var f = 0; f < MeasureFrames; f++) RunOnce();
        sw.Stop();
        return sw.Elapsed.TotalMicroseconds / MeasureFrames;
    }

    [Fact]
    public void EightSpeakerInboundChainStaysWithinHalfPercentCoreDelta()
    {
        var sources = BuildSpeakerSources();
        double beforeUs = double.MaxValue, afterUs = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            beforeUs = Math.Min(beforeUs, MeasureLegacyChainUsPerFrame(sources));
            afterUs = Math.Min(afterUs, MeasureNewChainUsPerFrame(sources));
        }

        var beforePct = beforeUs / 20_000.0 * 100.0;
        var afterPct = afterUs / 20_000.0 * 100.0;
        var deltaPct = afterPct - beforePct;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"8-speaker inbound (20 ms frame): before {beforeUs:F1} us ({beforePct:F3}% core), after {afterUs:F1} us ({afterPct:F3}% core), delta {deltaPct:+0.000;-0.000}% core");

        Assert.True(deltaPct < 0.5, $"delta {deltaPct:F3}% core exceeded 0.5% budget (before {beforeUs:F1} us, after {afterUs:F1} us)");
    }
}

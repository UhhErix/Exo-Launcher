using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Tests;

public class VoiceLabChainTests
{
    [Fact]
    public void VoiceTierPolicySelectsDefaultAutoTierAndRespectsGameGpuBudget()
    {
        Assert.Equal(VoiceModelTier.DpdfNetBaseline, VoiceTierPolicy.SelectTier(
            "exo", "auto", baselineReady: true, hqReady: false, voxLockReady: false,
            onlyMyVoiceWithIsolation: false, gameInForeground: false, gpuHeadroomMs: 1.0, cpuLogicalCores: 12));

        Assert.Equal(VoiceModelTier.DpdfNet48kHr, VoiceTierPolicy.SelectTier(
            "exo", "auto", baselineReady: true, hqReady: true, voxLockReady: false,
            onlyMyVoiceWithIsolation: false, gameInForeground: false, gpuHeadroomMs: 1.0, cpuLogicalCores: 12));

        Assert.Equal(VoiceModelTier.DpdfNetBaseline, VoiceTierPolicy.SelectTier(
            "exo", "auto", baselineReady: true, hqReady: true, voxLockReady: false,
            onlyMyVoiceWithIsolation: false, gameInForeground: true, gpuHeadroomMs: 0.15, cpuLogicalCores: 16));

        Assert.Equal(VoiceModelTier.DpdfNetBaseline, VoiceTierPolicy.SelectTier(
            "exo", "hq", baselineReady: true, hqReady: true, voxLockReady: false,
            onlyMyVoiceWithIsolation: false, gameInForeground: true, gpuHeadroomMs: 0.25, cpuLogicalCores: 16));

        Assert.Equal(VoiceModelTier.DpdfNetBaseline, VoiceTierPolicy.SelectTier(
            "exo", "cpu", baselineReady: true, hqReady: true, voxLockReady: false,
            onlyMyVoiceWithIsolation: false, gameInForeground: false, gpuHeadroomMs: 2.0, cpuLogicalCores: 16));

        Assert.Equal(VoiceModelTier.VoxLockTse, VoiceTierPolicy.SelectTier(
            "exo", "auto", baselineReady: true, hqReady: true, voxLockReady: true,
            onlyMyVoiceWithIsolation: true, gameInForeground: false, gpuHeadroomMs: 1.0, cpuLogicalCores: 12));

        Assert.Equal(VoiceModelTier.FallbackDfn3, VoiceTierPolicy.SelectTier(
            "exo-dfn3", "auto", baselineReady: true, hqReady: true, voxLockReady: true,
            onlyMyVoiceWithIsolation: false, gameInForeground: false, gpuHeadroomMs: 1.0, cpuLogicalCores: 12));

        Assert.Equal(VoiceModelTier.FallbackDfn3, VoiceTierPolicy.SelectTier(
            "exo", "auto", baselineReady: false, hqReady: false, voxLockReady: false,
            onlyMyVoiceWithIsolation: false, gameInForeground: false, gpuHeadroomMs: 1.0, cpuLogicalCores: 12));

        Assert.True(VoiceTierPolicy.MeetsCpuBudget(1.68));
        Assert.True(VoiceTierPolicy.MeetsCpuBudget(2.00));
        Assert.False(VoiceTierPolicy.MeetsCpuBudget(2.50));
    }

    [Fact]
    public void VoiceProcessingMapsTierIsolationAndLoopbackEcho()
    {
        var def = VoiceProcessing.From(new AppSettings
        {
            DiscordNoiseSuppression = true,
            DiscordNoiseSuppressionKind = "exo",
            DiscordVoiceTier = "auto",
            DiscordVoiceIsolation = true,
            DiscordLoopbackEchoCancellation = true,
        });
        Assert.Equal("exo", def.Profile);
        Assert.Equal("exo", def.Suppressor);
        Assert.Equal("auto", def.VoiceTier);
        Assert.True(def.VoiceIsolation);
        Assert.True(def.LoopbackEcho);

        var dfn = VoiceProcessing.From(new AppSettings
        {
            DiscordNoiseSuppression = true,
            DiscordNoiseSuppressionKind = "exo-dfn3",
            DiscordVoiceTier = "cpu",
        });
        Assert.Equal("exo", dfn.Profile);
        Assert.Equal("exo-dfn3", dfn.Suppressor);
        Assert.Equal("cpu", dfn.VoiceTier);
    }

    [Fact]
    public void LoopbackEchoReferenceDetectsActiveAndSilentFrames()
    {
        var silence = new float[VoiceAudio.FrameFloats];
        Assert.False(LoopbackEchoReference.HasActiveReference(silence));
        Assert.True(LoopbackEchoReference.FrameDb(silence) <= -99f);

        var active = new float[VoiceAudio.FrameFloats];
        for (var i = 0; i < active.Length; i++) active[i] = 0.05f * MathF.Sin(i * 0.1f);
        Assert.True(LoopbackEchoReference.HasActiveReference(active));
        Assert.InRange(LoopbackEchoReference.FrameDb(active), -35f, -20f);
    }

    [Fact]
    public void VoiceModelsShipBesideExoAndAreNeverFetched()
    {
        Assert.Equal(3, VoiceModels.Bundled.Count);
        Assert.Contains(VoiceModels.TitaNetAsset, VoiceModels.Bundled);
        Assert.Equal(VoiceModels.ModelPath(VoiceModels.TitaNetAsset), SpeakerModel.ModelPath);
        foreach (var asset in VoiceModels.Bundled)
        {
            Assert.True(asset.ExpectedSize > 1_000_000);
            Assert.Equal(64, asset.ExpectedSha256.Length);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "voice", "models", asset.AssetName), VoiceModels.BundledPath(asset));
            Assert.DoesNotContain(ExoComponents.All, c => string.Equals(Path.GetFileName(c.Location()), asset.AssetName, StringComparison.OrdinalIgnoreCase));
        }
        Assert.Equal(VoiceModels.Bundled.Select(a => a.AssetName).Distinct().Count(), VoiceModels.Bundled.Count);
#if !DEBUG
        Assert.Equal(VoiceModels.BundledPath(VoiceModels.DpdfNetBaselineAsset), VoiceModels.ModelPath(VoiceModels.DpdfNetBaselineAsset));
        var retired = VoiceModels.RetiredDownloads().ToList();
        Assert.Equal(VoiceModels.Bundled.Select(a => Path.Combine(VoiceModels.VoiceDir, a.AssetName)).Concat(VoiceModels.RetiredModelDownloads.Select(n => Path.Combine(VoiceModels.VoiceDir, n))), retired);
        Assert.Equal(VoiceModels.BundledPath(VoiceModels.TitaNetAsset), SpeakerModel.ModelPath);
#endif
    }

    [Fact]
    public void NeuralNoiseSuppressorHandlesMissingModelGracefully()
    {
        var prev = VoiceModels.BaselineModelPath;
        try
        {
            VoiceModels.BaselineModelPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.onnx");
            Assert.False(VoiceModels.BaselineReady());
            Assert.Null(NeuralNoiseSuppressor.Create(VoiceModelTier.DpdfNetBaseline));
        }
        finally
        {
            VoiceModels.BaselineModelPath = prev;
        }
    }

    [Fact]
    public void ExoVoiceMlProcessesRealAudioWhenLabOnnxProvided()
    {
        var path = Environment.GetEnvironmentVariable("EXO_LAB_TEST_ONNX");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var prev = VoiceModels.BaselineModelPath;
        try
        {
            VoiceModels.BaselineModelPath = path;
            if (!VoiceModels.BaselineReady()) Assert.Null(NeuralNoiseSuppressor.Create(VoiceModelTier.DpdfNetBaseline));
            using var suppressor = NeuralNoiseSuppressor.CreateUnverifiedForTests(path);
            Assert.NotNull(suppressor);
            var rng = new Random(42);
            double inEnergy = 0, outEnergy = 0;
            var frame = new float[VoiceAudio.FrameFloats];
            for (var f = 0; f < 40; f++)
            {
                for (var i = 0; i < Opus.FrameSamples; i++)
                {
                    var n = (float)((rng.NextDouble() * 2.0 - 1.0) * 0.08);
                    frame[2 * i] = frame[2 * i + 1] = n;
                    if (f >= 10) inEnergy += n * n;
                }
                suppressor.Process(frame, 100);
                if (f >= 10)
                    for (var i = 0; i < Opus.FrameSamples; i++)
                        outEnergy += frame[2 * i] * frame[2 * i];
            }
            var attenDb = 10.0 * Math.Log10((inEnergy + 1e-12) / (outEnergy + 1e-12));
            Assert.True(attenDb > 20.0, $"Expected >20 dB noise attenuation, got {attenDb:F1} dB");
            Assert.NotNull(suppressor.SpeechDb);
        }
        finally
        {
            VoiceModels.BaselineModelPath = prev;
        }
    }
}

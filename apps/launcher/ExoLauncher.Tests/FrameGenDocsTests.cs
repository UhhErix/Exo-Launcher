using static ExoLauncher.Services.DlssSwapService;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the words about frame generation could drift from what the code does:
/// - HELP promises RTX 20 and 30 NVIDIA's own frame generation while DLSSG SM86 has no pin, so no game gets it;
/// - PRIVACY leaves out a file Exo keeps on the PC (learned.json), or still describes a request for the mods' newest release
///   that Exo no longer makes;
/// - the GPU profile decisions name MFG Unlocker as the mod that reads the preset, which Exo only takes out now.
/// </summary>
public sealed class FrameGenDocsTests
{
    [Fact]
    public void HelpPromisesNvidiasOwnFrameGenerationOnRtx20And30OnlyOnceItIsOffered()
    {
        var help = File.ReadAllText(Repo.PathOf("docs", "HELP.md"));
        var line = help.Split('\n').Single(text => text.StartsWith("- **Frame generation.**", StringComparison.Ordinal));
        if (Unoffered(DlssgSm86) is null) return;
        Assert.DoesNotContain("NVIDIA's own frame generation", line, StringComparison.Ordinal);
        Assert.Contains("OptiScaler", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivacyNamesWhatExoLearnsAndTheNewestReleaseCheck()
    {
        var privacy = File.ReadAllText(Repo.PathOf("PRIVACY.md"));
        Assert.Contains(@"frame-gen\learned.json", privacy, StringComparison.Ordinal);
        var row = privacy.Split('\n').Single(text => text.StartsWith("| Frame generation mods", StringComparison.Ordinal));
        Assert.Contains("newest release", row, StringComparison.Ordinal);
        Assert.Contains("api.github.com", row, StringComparison.Ordinal);
        Assert.DoesNotContain("six hours", row, StringComparison.Ordinal);
        Assert.Contains("SHA-256", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGpuProfileDecisionsNameTheModThatReadsThePreset()
    {
        var decision = File.ReadAllLines(Repo.PathOf("docs", "architecture", "gpu-profiles.md")).Single(text => text.StartsWith("12. ", StringComparison.Ordinal));
        Assert.DoesNotContain("MFG Unlocker reads", decision, StringComparison.Ordinal);
        Assert.Contains("DLSSG SM86", decision, StringComparison.Ordinal);
    }
}

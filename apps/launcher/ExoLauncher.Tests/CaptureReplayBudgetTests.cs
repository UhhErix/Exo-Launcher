using System.Text.Json.Nodes;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

// Old add-ons should never be sent an unsupported byte limit, and an absent or stale
// duration must not be presented as time the player can actually save.
public sealed class CaptureReplayBudgetTests
{
    [Theory]
    [InlineData(0, 600, 33)]
    [InlineData(4, 60, 33)]
    [InlineData(8, 60, 60)]
    [InlineData(8, 600, 67)]
    [InlineData(64, 600, 100)]
    [InlineData(64, 30, 30)]
    public void LegacyAddOnGetsOnlyTheShorterOfRequestedAndSizedWindow(int gib, int requested, int expected)
    {
        var policy = CaptureReplayBudget.For(requested, (ulong)gib << 30, supportsBudget: false);
        Assert.Equal(expected, policy.Seconds);
        Assert.Null(policy.MaxBytes);
    }

    [Theory]
    [InlineData(0, 512)]
    [InlineData(4, 512)]
    [InlineData(8, 1024)]
    [InlineData(64, 1536)]
    public void BudgetAwareAddOnGetsTimeAndBytes(int gib, int mib)
    {
        var policy = CaptureReplayBudget.For(600, (ulong)gib << 30, supportsBudget: true);
        Assert.Equal(600, policy.Seconds);
        Assert.Equal((long)mib << 20, policy.MaxBytes);
    }

    [Theory]
    [InlineData("{\"buffering\":true,\"replayRetainedMs\":42100.5}", 42100)]
    [InlineData("{\"buffering\":true,\"replayRetainedMs\":0}", 0)]
    [InlineData("{\"buffering\":false,\"replayRetainedMs\":42100}", null)]
    [InlineData("{\"buffering\":true,\"replayRetainedMs\":\"42100\"}", null)]
    [InlineData("{\"buffering\":true,\"replayRetainedMs\":-1}", null)]
    [InlineData("{\"buffering\":true,\"replayRetainedMs\":1e99}", null)]
    [InlineData("{\"buffering\":true}", null)]
    public void DisplaysOnlyReportedReplayTimeWhileBuffering(string json, int? expected) =>
        Assert.Equal(expected, CaptureReplayBudget.RetainedMs(JsonNode.Parse(json)));
}

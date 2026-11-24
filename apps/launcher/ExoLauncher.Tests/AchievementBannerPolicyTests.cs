namespace ExoLauncher.Tests;

public sealed class AchievementBannerPolicyTests
{
    [Fact]
    public void PrewarmAndIdleDisposalRespectGameRunningState()
    {
        Assert.True(AchievementBanner.ShouldPrewarm(bannersEnabled: true, gameRunning: true, hasOverlay: false));
        Assert.False(AchievementBanner.ShouldPrewarm(bannersEnabled: false, gameRunning: true, hasOverlay: false));
        Assert.False(AchievementBanner.ShouldPrewarm(bannersEnabled: true, gameRunning: false, hasOverlay: false));
        Assert.False(AchievementBanner.ShouldPrewarm(bannersEnabled: true, gameRunning: true, hasOverlay: true));
        Assert.False(AchievementBanner.ShouldDisposeOnIdle(gameRunning: true));
        Assert.True(AchievementBanner.ShouldDisposeOnIdle(gameRunning: false));
    }
}

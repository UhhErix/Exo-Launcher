using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// How a followed Steam download ends. Ways it goes wrong: Steam's own failed or cancelled transfer reads as Completed, so
/// the Update badge clears and Friends see "Ready" for a game with no files; a manifest that can't be read reads as done;
/// a stopped download with bytes missing reads as done.
/// </summary>
public sealed class SteamFollowOutcomeTests
{
    [Fact]
    public void AFinishedDownloadIsCompleted()
    {
        var result = SteamAdapter.FollowOutcome(manifestReadable: true, updateResult: 0, toDownload: 1_000, downloaded: 1_000);
        Assert.Equal(SteamFollowOutcome.Completed, result.Outcome);
    }

    [Fact]
    public void NothingToDownloadIsCompleted()
    {
        Assert.Equal(SteamFollowOutcome.Completed, SteamAdapter.FollowOutcome(true, 0, toDownload: 0, downloaded: 0).Outcome);
    }

    [Fact]
    public void AnUnreadableManifestIsFailedNotCompleted()
    {
        var result = SteamAdapter.FollowOutcome(manifestReadable: false, updateResult: 0, toDownload: 1_000, downloaded: 1_000);
        Assert.Equal(SteamFollowOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void AMissingUpdateResultIsNotProvenDone()
    {
        Assert.Equal(SteamFollowOutcome.Failed, SteamAdapter.FollowOutcome(true, null, 1_000, 1_000).Outcome);
    }

    [Fact]
    public void AnErrorIsFailedWithSteamsMessage()
    {
        var result = SteamAdapter.FollowOutcome(true, 12, 1_000, 0);
        Assert.Equal(SteamFollowOutcome.Failed, result.Outcome);
        Assert.Equal("Not enough disk space for this download.", result.Message);
    }

    [Fact]
    public void AnUnknownErrorSaysWhereToResumeIt()
    {
        var result = SteamAdapter.FollowOutcome(true, 99, 1_000, 0);
        Assert.Equal("Steam stopped the download (error 99). Resume it in Steam's Downloads.", result.Message);
    }

    [Theory]
    [InlineData(false, null)]  // the record can't be read
    [InlineData(true, null)]   // Steam did not say how it ended
    public void AnUnreadableEndSaysExoLostTrack(bool manifestReadable, int? updateResult) =>
        Assert.Equal("Exo lost track of Steam's download. Check Steam's Downloads.", SteamAdapter.FollowOutcome(manifestReadable, updateResult, 1_000, 1_000).Message);

    [Theory]
    [InlineData(2)]  // paused
    [InlineData(3)]  // canceled
    public void APausedOrCanceledDownloadIsIdle(int updateResult) =>
        Assert.Equal(SteamFollowOutcome.Idle, SteamAdapter.FollowOutcome(true, updateResult, 1_000, 400).Outcome);

    [Fact]
    public void ABrokenDownloadWithBytesMissingIsFailed()
    {
        var result = SteamAdapter.FollowOutcome(true, 0, toDownload: 1_000, downloaded: 400);
        Assert.Equal(SteamFollowOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Message);
    }
}

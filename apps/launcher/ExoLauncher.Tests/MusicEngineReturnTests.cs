using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways coming back to a music page could fail, each silent until someone presses something:
/// - a read or press while the page is still loading gives up at once, so the room shows "not answering" over a player
///   that is two seconds from working (the idle release's first read);
/// - the wait for a page that can't come (another service chosen, the window closing) runs out its whole limit;
/// - a wait never ends, because the page never came or Exo is closing, and the call hangs;
/// - a page that doesn't answer holds the volume slider for seconds at a time, once per value it was dragged through;
/// - the write that never settles (Spotify's setVolume, while its player isn't up) comes back as a page that didn't answer.
/// </summary>
public sealed class MusicEngineReturnTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task APageStillLoadingIsWaitedFor()
    {
        var at = Environment.TickCount64;
        Assert.True(await MusicWebPolicy.WaitUntilAsync(() => Environment.TickCount64 - at > 250, () => false, TimeSpan.FromSeconds(5), None));
    }

    [Fact]
    public async Task APageThatCantComeEndsTheWaitAtOnce()
    {
        var at = Environment.TickCount64;
        Assert.False(await MusicWebPolicy.WaitUntilAsync(() => false, () => true, TimeSpan.FromMinutes(1), None));
        Assert.True(Environment.TickCount64 - at < 1000);
    }

    [Fact]
    public async Task TheWaitEndsAtItsLimitAndWhenExoCloses()
    {
        Assert.False(await MusicWebPolicy.WaitUntilAsync(() => false, () => false, TimeSpan.FromMilliseconds(250), None));
        using var closing = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        Assert.False(await MusicWebPolicy.WaitUntilAsync(() => false, () => false, TimeSpan.FromMinutes(1), closing.Token));
    }

    [Fact]
    public async Task APageNoticedAsItGoesEndsTheWait()
    {
        var gone = false;
        var waiting = MusicWebPolicy.WaitUntilAsync(() => false, () => gone, TimeSpan.FromMinutes(1), None);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        gone = true;
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APageThatDoesntAnswerHoldsTheSliderASecondAtMost()
    {
        TimeSpan asked = default;
        using var volume = MusicAppVolume.ForWebPage("spotify", (_, timeout, _) =>
        {
            asked = timeout;
            return Task.FromException<JsonElement?>(new TimeoutException());
        }, () => true);

        Assert.Null(await volume.WriteAsync(0.5, TestContext.Current.CancellationToken));
        Assert.True(asked <= TimeSpan.FromSeconds(1), "A write may wait " + asked);
    }

    [Fact]
    public async Task AWriteAnswersWithTheLevelTheScriptGives()
    {
        string? sent = null;
        using var volume = MusicAppVolume.ForWebPage("spotify", (expression, _, _) =>
        {
            sent = expression;
            using var answer = JsonDocument.Parse("0.25");
            return Task.FromResult<JsonElement?>(answer.RootElement.Clone());
        }, () => true);

        Assert.Equal(0.25, await volume.WriteAsync(0.25, TestContext.Current.CancellationToken));
        Assert.EndsWith(".write(0.25)", sent);
    }

    /// The script runs in the page, so no test here can run it: what it must not wait on is pinned instead.
    [Fact]
    public void TheWriteDoesNotWaitOnTheApp()
    {
        Assert.DoesNotContain("await adapter.write", MusicAppVolume.Wrapper);
        Assert.Contains("Promise.resolve(adapter.write(h, c)).catch", MusicAppVolume.Wrapper);
    }
}

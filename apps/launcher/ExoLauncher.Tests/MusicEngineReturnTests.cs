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

    [Fact]
    public async Task VolumeReusesWindowSentinelAndReinstallsAfterNavigation()
    {
        var expressions = new List<string>();
        var call = 0;
        using var volume = MusicAppVolume.ForWebPage("spotify", (expression, _, _) =>
        {
            expressions.Add(expression);
            call++;
            var json = call == 3 ? "\"__exo_need_install\"" : "0.5";
            using var answer = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(answer.RootElement.Clone());
        }, () => true);

        Assert.Equal(0.5, await volume.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0.5, await volume.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0.5, await volume.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(4, expressions.Count);
        Assert.True(expressions[0].Length > 500);
        Assert.True(expressions[1].Length < 150);
        Assert.Contains("window.__exoVolume", expressions[1]);
        Assert.True(expressions[2].Length < 150);
        Assert.True(expressions[3].Length > 500);
    }

    [Fact]
    public void MusicWebRetryBackoffEscalatesAndGatesAutoRetries()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), MusicEngineService.RetryBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(5), MusicEngineService.RetryBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(15), MusicEngineService.RetryBackoff(3));
        Assert.Equal(TimeSpan.FromSeconds(60), MusicEngineService.RetryBackoff(4));
        Assert.Equal(TimeSpan.FromSeconds(60), MusicEngineService.RetryBackoff(10));

        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.True(MusicEngineService.CanAutoRetry(now, null));
        Assert.False(MusicEngineService.CanAutoRetry(now, now.AddSeconds(5)));
        Assert.True(MusicEngineService.CanAutoRetry(now.AddSeconds(5), now.AddSeconds(5)));
    }

    /// The script runs in the page, so no test here can run it: what it must not wait on is pinned instead.
    [Fact]
    public void TheWriteDoesNotWaitOnTheApp()
    {
        Assert.DoesNotContain("await adapter.write", MusicAppVolume.Wrapper);
        Assert.Contains("Promise.resolve(adapter.write(h, c)).catch", MusicAppVolume.Wrapper);
    }

    [Fact]
    public void AppleMusicControlDoesNotAwaitMusicKitPromises()
    {
        var script = MusicNowPlaying.AppleMusicAdapterScript;
        Assert.DoesNotContain("return Promise.resolve(m.", script);
        Assert.Contains("Promise.resolve(m.play()).catch", script);
        Assert.Contains("Promise.resolve(m.pause()).catch", script);
        Assert.Contains("Promise.resolve(m.skipToNextItem()).catch", script);
        Assert.Contains("Promise.resolve(m.skipToPreviousItem()).catch", script);
        Assert.Contains("Promise.resolve(m.seekToTime(ms / 1000)).catch", script);
    }

    [Fact]
    public void AppleMusicNativeCachedExpressionsStaySmall()
    {
        var fullView = AppleMusicNative.View("player", null, null, 0, installed: false);
        var fastView = AppleMusicNative.View("player", null, null, 0, installed: true);
        var fullAct = AppleMusicNative.Act("shuffle", null, null, null, true, null, null, null, installed: false);
        var fastAct = AppleMusicNative.Act("shuffle", null, null, null, true, null, null, null, installed: true);

        Assert.True(fullView.Length > 50_000);
        Assert.True(fastView.Length < 200);
        Assert.Contains("window.__exoAppleMusic", fastView);
        Assert.Contains(AppleMusicNative.NeedInstallProperty, fastView);
        Assert.True(fullAct.Length > 50_000);
        Assert.True(fastAct.Length < 250);
        Assert.Contains("window.__exoAppleMusic", fastAct);
    }

    [Fact]
    public async Task AppleMusicNativeInvokeCachedUsesFastPathAndFallsBackOnMiss()
    {
        var calls = new List<string>();
        var installed = true;

        var okReply = await AppleMusicNative.InvokeCachedAsync(
            cached => AppleMusicNative.View("status", null, null, 0, cached),
            expr =>
            {
                calls.Add(expr);
                using var doc = JsonDocument.Parse("{\"ready\":true}");
                return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
            },
            () => installed,
            v => installed = v);

        Assert.NotNull(okReply);
        Assert.Single(calls);
        Assert.True(calls[0].Length < 200);
        Assert.True(installed);

        calls.Clear();
        var fallbackReply = await AppleMusicNative.InvokeCachedAsync(
            cached => AppleMusicNative.View("player", null, null, 0, cached),
            expr =>
            {
                calls.Add(expr);
                var json = calls.Count == 1 ? "{\"" + AppleMusicNative.NeedInstallProperty + "\":true}" : "{\"shuffle\":false}";
                using var doc = JsonDocument.Parse(json);
                return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
            },
            () => installed,
            v => installed = v);

        Assert.NotNull(fallbackReply);
        Assert.Equal(2, calls.Count);
        Assert.True(calls[0].Length < 200);
        Assert.True(calls[1].Length > 50_000);
        Assert.True(installed);

        calls.Clear();
        installed = false;
        var nullReply = await AppleMusicNative.InvokeCachedAsync(
            cached => AppleMusicNative.View("queue", null, null, 0, cached),
            expr =>
            {
                calls.Add(expr);
                return Task.FromResult<JsonElement?>(null);
            },
            () => installed,
            v => installed = v);

        Assert.Null(nullReply);
        Assert.Single(calls);
        Assert.True(calls[0].Length > 50_000);
        Assert.False(installed);
    }

    [Fact]
    public async Task MusicNowPlayingControlCachesPerNavigationAndFallsBackImmediately()
    {
        var sent = new List<string>();
        using var now = MusicNowPlaying.ForWebPage(
            "apple",
            (_, _, _) => Task.FromResult<JsonElement?>(null),
            () => true,
            control: (expr, _, _) =>
            {
                sent.Add(expr);
                using var doc = JsonDocument.Parse("true");
                return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
            },
            preinstalled: false);

        Assert.True(await now.ControlAsync("pause", null));
        Assert.True(await now.ControlAsync("play", null));
        now.MarkNavigationStarting();
        Assert.True(await now.ControlAsync("next", null));
        Assert.True(await now.ControlAsync("seek", 12345));

        Assert.Equal(4, sent.Count);
        Assert.True(sent[0].Length > 5000);
        Assert.True(sent[1].Length < 200);
        Assert.True(sent[2].Length > 5000);
        Assert.True(sent[3].Length < 200);

        var fallbackSent = new List<string>();
        using var pre = MusicNowPlaying.ForWebPage(
            "apple",
            (_, _, _) => Task.FromResult<JsonElement?>(null),
            () => true,
            control: (expr, _, _) =>
            {
                fallbackSent.Add(expr);
                var json = fallbackSent.Count == 1 ? "\"" + MusicNowPlaying.NeedInstallSentinel + "\"" : "true";
                using var doc = JsonDocument.Parse(json);
                return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
            },
            preinstalled: true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await pre.ControlAsync("toggle", null));
        sw.Stop();
        Assert.Equal(2, fallbackSent.Count);
        Assert.True(fallbackSent[0].Length < 200);
        Assert.True(fallbackSent[1].Length > 5000);
        Assert.True(sw.ElapsedMilliseconds < 400);
    }
}

using ExoLauncher.Services;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Tests;

public sealed class MusicEngineDietTests
{
    private static readonly string[] FailureModes =
    [
        "A non-selected music service opens a second hidden WebView2 engine alongside or instead of the selected service.",
        "Idle suspend fires while audio is playing, while a media session is active, while a popout is open, or during sign-in.",
        "Idle suspend gives up permanently when temporarily blocked by a popout or sign-in instead of deferring and retrying.",
        "Asleep release fires before the 15-minute idle rule or while the engine is awake, playing, or holding a sign-in window.",
        "Hidden window sizing shrinks Spotify below a layout where MusicPress can click the play button, or keeps Apple/YouTube at full 1280x800.",
        "Resource diet blocks playback-critical audio/DRM/API/watchtime endpoints, blocks resources inside visible sign-in popups, or leaves ads, trackers, fonts, images, or Canvas/EditorialVideo unblocked on the hidden engine.",
        "WebResourceRequested filter patterns register wildcard Media for audio streams, adding C# IPC roundtrips to every audio chunk.",
    ];

    [Fact]
    public void IdleTimingsMatchTheDietAndAgentsIdleRule()
    {
        Assert.NotEmpty(FailureModes);
        Assert.Equal(TimeSpan.FromSeconds(15), MusicEngineDiet.SuspendDelay);
        Assert.Equal(TimeSpan.FromMinutes(15), MusicEngineDiet.ReleaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(15), MusicEngineDiet.DeferredRetryDelay);
    }

    [Theory]
    [InlineData("apple", "apple", true)]
    [InlineData("spotify", "spotify", true)]
    [InlineData("youtube", "youtube", true)]
    [InlineData(null, "apple", true)]
    [InlineData("apple", "spotify", false)]
    [InlineData("spotify", "youtube", false)]
    [InlineData("apple", "tidal", false)]
    [InlineData(null, "", false)]
    [InlineData("apple", null, false)]
    public void ServiceSelectionAllowsOnlyTheSelectedKnownService(string? selected, string? requested, bool expected)
    {
        Assert.Equal(expected, MusicEngineDiet.ServiceMatchesSelection(selected, requested));
    }

    [Fact]
    public void HiddenWindowSizeKeepsSpotifyClickableAndShrinksAppleAndYouTube()
    {
        Assert.Equal((1024, 720), MusicEngineDiet.HiddenWindowSize("spotify"));
        Assert.Equal((320, 240), MusicEngineDiet.HiddenWindowSize("apple"));
        Assert.Equal((320, 240), MusicEngineDiet.HiddenWindowSize("youtube"));
    }

    [Fact]
    public void CanSuspendRequiresIdleHostWithNoAudioSessionPopoutsOrSignIn()
    {
        Assert.True(MusicEngineDiet.CanSuspend(hasHost: true, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: false, signInActive: false));
        Assert.False(MusicEngineDiet.CanSuspend(hasHost: false, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: false, signInActive: false));
        Assert.False(MusicEngineDiet.CanSuspend(hasHost: true, isPlayingAudio: true, isMediaSessionPlaying: false, hasPopouts: false, signInActive: false));
        Assert.False(MusicEngineDiet.CanSuspend(hasHost: true, isPlayingAudio: false, isMediaSessionPlaying: true, hasPopouts: false, signInActive: false));
        Assert.False(MusicEngineDiet.CanSuspend(hasHost: true, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: true, signInActive: false));
        Assert.False(MusicEngineDiet.CanSuspend(hasHost: true, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: false, signInActive: true));
    }

    [Fact]
    public void ShouldDeferIdleRetryOnlyWhenTemporarilyBlockedWithoutLiveAudio()
    {
        Assert.True(MusicEngineDiet.ShouldDeferIdleRetry(hasHost: true, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: true, signInActive: false));
        Assert.True(MusicEngineDiet.ShouldDeferIdleRetry(hasHost: true, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: false, signInActive: true));
        Assert.True(MusicEngineDiet.ShouldDeferIdleRetry(hasHost: true, isPlayingAudio: false, isMediaSessionPlaying: true, hasPopouts: false, signInActive: false));
        Assert.False(MusicEngineDiet.ShouldDeferIdleRetry(hasHost: true, isPlayingAudio: true, isMediaSessionPlaying: true, hasPopouts: true, signInActive: true));
        Assert.False(MusicEngineDiet.ShouldDeferIdleRetry(hasHost: false, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: true, signInActive: false));
        Assert.False(MusicEngineDiet.ShouldDeferIdleRetry(hasHost: true, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: false, signInActive: false));
    }

    [Fact]
    public void CanReleaseRequiresAsleepHostWithNoPlaybackOrPopouts()
    {
        Assert.True(MusicEngineDiet.CanRelease(hasHost: true, isAsleep: true, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: false, hasSignInWindow: false));
        Assert.False(MusicEngineDiet.CanRelease(hasHost: false, isAsleep: true, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: false, hasSignInWindow: false));
        Assert.False(MusicEngineDiet.CanRelease(hasHost: true, isAsleep: false, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: false, hasSignInWindow: false));
        Assert.False(MusicEngineDiet.CanRelease(hasHost: true, isAsleep: true, isPlayingAudio: true, isMediaSessionPlaying: false, hasPopouts: false, hasSignInWindow: false));
        Assert.False(MusicEngineDiet.CanRelease(hasHost: true, isAsleep: true, isPlayingAudio: false, isMediaSessionPlaying: true, hasPopouts: false, hasSignInWindow: false));
        Assert.False(MusicEngineDiet.CanRelease(hasHost: true, isAsleep: true, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: true, hasSignInWindow: false));
        Assert.False(MusicEngineDiet.CanRelease(hasHost: true, isAsleep: true, isPlayingAudio: false, isMediaSessionPlaying: false, hasPopouts: false, hasSignInWindow: true));
    }

    [Theory]
    [InlineData("apple", "https://is1-ssl.mzstatic.com/image/thumb/Music/cover.jpg", CoreWebView2WebResourceContext.Image)]
    [InlineData("apple", "https://music.apple.com/assets/sf-pro.woff2", CoreWebView2WebResourceContext.Font)]
    [InlineData("apple", "https://xp.apple.com/report/2/xp_amp_web", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("apple", "https://metrics.apple.com/b/ss/applemusic", CoreWebView2WebResourceContext.XmlHttpRequest)]
    [InlineData("apple", "https://mvod.itunes.apple.com/itunes-assets/HLSVideo/motion.m3u8", CoreWebView2WebResourceContext.Media)]
    [InlineData("apple", "https://mvod-ssl.itunes.apple.com/itunes-assets/HLSVideo/motion.m3u8", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("apple", "https://audio-ssl.itunes.apple.com/itunes-assets/EditorialVideo/promo.mp4", CoreWebView2WebResourceContext.Media)]
    [InlineData("apple", "https://audio-ssl.itunes.apple.com/itunes-assets/EditorialVideo/playlist.m3u8", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("spotify", "https://i.scdn.co/image/ab67616d0000b2731234", CoreWebView2WebResourceContext.Image)]
    [InlineData("spotify", "https://open.spotifycdn.com/cdn/fonts/CircularSp.woff2", CoreWebView2WebResourceContext.Font)]
    [InlineData("spotify", "https://canvaz.scdn.co/upload/artist/video.mp4", CoreWebView2WebResourceContext.Media)]
    [InlineData("spotify", "https://video-fa.scdn.co/v/clip.mp4", CoreWebView2WebResourceContext.Media)]
    [InlineData("spotify", "https://video-ak.scdn.co/v/clip.mp4", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("spotify", "https://pixel.spotify.com/event", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("spotify", "https://open.spotify.com/ads/v1/slot", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("spotify", "https://open.spotifycdn.com/cdn/js/retargeting-pixels.js", CoreWebView2WebResourceContext.Script)]
    [InlineData("youtube", "https://lh3.googleusercontent.com/cover=w544-h544", CoreWebView2WebResourceContext.Image)]
    [InlineData("youtube", "https://fonts.gstatic.com/s/roboto/v30/font.woff2", CoreWebView2WebResourceContext.Font)]
    [InlineData("youtube", "https://googleads.g.doubleclick.net/pagead/id", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("youtube", "https://music.youtube.com/pagead/interaction/", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("youtube", "https://music.youtube.com/youtubei/v1/player/ad_break", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("youtube", "https://music.youtube.com/api/stats/qoe?docid=abc", CoreWebView2WebResourceContext.XmlHttpRequest)]
    [InlineData("youtube", "https://music.youtube.com/youtubei/v1/log_event", CoreWebView2WebResourceContext.Fetch)]
    public void ShouldBlockResourceBlocksHeavyAndTrackerResourcesOnHiddenEngine(
        string service,
        string uri,
        CoreWebView2WebResourceContext context)
    {
        Assert.True(MusicEngineDiet.ShouldBlockResource(service, uri, context, popup: false));
        Assert.False(MusicEngineDiet.ShouldBlockResource(service, uri, context, popup: true));
    }

    [Theory]
    [InlineData("apple", "https://aod-ssl.itunes.apple.com/itunes-assets/Audio/track.m4a", CoreWebView2WebResourceContext.Media)]
    [InlineData("apple", "https://play.itunes.apple.com/WebObjects/MZPlay.woa/wa/webPlayback", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("apple", "https://amp-api.music.apple.com/v1/me/library/songs", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("apple", "https://music.apple.com/assets/index.js", CoreWebView2WebResourceContext.Script)]
    [InlineData("spotify", "https://audio-fa.scdn.co/audio/123456.mp4", CoreWebView2WebResourceContext.Media)]
    [InlineData("spotify", "https://audio4-fa.spotifycdn.com/audio/123456", CoreWebView2WebResourceContext.Media)]
    [InlineData("spotify", "https://spclient.wg.spotify.com/connect-state/v1/devices/hobs", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("spotify", "https://open.spotify.com/get_access_token", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("youtube", "https://rr3---sn-ab5l6nzy.googlevideo.com/videoplayback?mime=audio%2Fwebm", CoreWebView2WebResourceContext.Media)]
    [InlineData("youtube", "https://music.youtube.com/api/stats/watchtime?docid=abc", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("youtube", "https://music.youtube.com/api/stats/playback?docid=abc", CoreWebView2WebResourceContext.Fetch)]
    [InlineData("youtube", "https://music.youtube.com/youtubei/v1/player", CoreWebView2WebResourceContext.Fetch)]
    public void ShouldBlockResourceAllowsPlaybackCriticalAndApiRequests(
        string service,
        string uri,
        CoreWebView2WebResourceContext context)
    {
        Assert.False(MusicEngineDiet.ShouldBlockResource(service, uri, context, popup: false));
    }

    [Theory]
    [InlineData("apple")]
    [InlineData("spotify")]
    [InlineData("youtube")]
    public void FilterPatternsNeverRegisterWildcardMediaFilter(string service)
    {
        var patterns = MusicEngineDiet.FilterPatternsForService(service);
        Assert.Contains(patterns, p => p.Pattern == "*" && p.Context == CoreWebView2WebResourceContext.Image);
        Assert.Contains(patterns, p => p.Pattern == "*" && p.Context == CoreWebView2WebResourceContext.Font);
        Assert.DoesNotContain(patterns, p => p.Pattern == "*" && p.Context == CoreWebView2WebResourceContext.Media);
        Assert.DoesNotContain(patterns, p => p.Pattern == "*://*.scdn.co/*.mp4*");
    }
}

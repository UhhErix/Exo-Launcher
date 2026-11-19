using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Apple's sign-in, as far as it can be held without an account. Ways it could fail:
/// - the token comes through the opener and the page drops it a microtask later, because its MusicKit reads it back from
///   a cookie it no longer writes (app.log: "authorize done [3,0]"), so the window closes and the room stays signed out;
/// - the cookie goes on a host the player can't read (Apple's window's own), or lasts only the session;
/// - the script hands the token to Exo: SignIn's answer and the trace reach the host and app.log.
/// </summary>
public sealed class AppleSignInTests
{
    [Fact]
    public void TheTokenIsSavedWhereThePlayerReadsIt()
    {
        Assert.Contains("status !== 3 || !m.musicUserToken", AppleMusicNative.SignIn);
        Assert.Contains("'media-user-token=' + m.musicUserToken", AppleMusicNative.SignIn);
        Assert.Contains("domain=music.apple.com; path=/; secure", AppleMusicNative.SignIn);
        Assert.Contains("180 * 864e5", AppleMusicNative.SignIn);
    }

    [Fact]
    public void NeitherAnswerCarriesTheToken()
    {
        Assert.DoesNotContain("return m.musicUserToken", AppleMusicNative.SignIn);
        Assert.DoesNotContain("t.token", AppleMusicNative.SignIn);
        Assert.DoesNotContain("musicUserToken", AppleMusicNative.SignInTrace);
        // The trace looks for the cookie's name; its value never leaves the page.
        Assert.DoesNotContain("document.cookie.match", AppleMusicNative.SignInTrace);
        Assert.Contains("indexOf('media-user-token=') >= 0 ? 'there' : 'gone'", AppleMusicNative.SignInTrace);
    }
}

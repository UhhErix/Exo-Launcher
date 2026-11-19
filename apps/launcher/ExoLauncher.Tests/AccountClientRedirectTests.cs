using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// The account client never follows a redirect: the service answers in place, and a redirect could carry the sign-in token to
/// another host. Ways this could fail: the default handler follows redirects, so a 302 from the service sends the request (with
/// its token) elsewhere.
/// </summary>
public sealed class AccountClientRedirectTests
{
    [Fact]
    public void TheDefaultHandlerNeverFollowsARedirect() =>
        Assert.False(ExoAccountClient.NewHandler().AllowAutoRedirect);
}

using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// What the account screens are told (CLIENT.md 4.1 and 4.2). What would go wrong silently:
/// - an unreachable service shown as a wrong password, instead of the offline path;
/// - a lock countdown that does not match Retry-After;
/// - a client bug shown with its error code, or a code the spec says never to show;
/// - a password or token in the answer the screen receives.
/// </summary>
public sealed class ExoAccountOutcomeTests
{
    [Fact]
    public void AnUnreachableServiceGoesToTheOfflinePath()
    {
        var outcome = ExoAccountOutcome.From(new AccountReply(0, null, "network", null));

        Assert.False(outcome.Ok);
        Assert.True(outcome.Offline);
        Assert.Equal("Can't reach Exo's account service.", outcome.Message);
    }

    [Fact]
    public void AServerErrorAlsoGoesToTheOfflinePath()
    {
        var outcome = ExoAccountOutcome.From(new AccountReply(503, null, "not-configured", null));

        Assert.True(outcome.Offline);
    }

    [Fact]
    public void AWrongPasswordSaysNothingAboutWhetherTheAddressHasAnAccount()
    {
        var outcome = ExoAccountOutcome.From(new AccountReply(401, null, "wrong-credentials", null));

        Assert.False(outcome.Offline);
        Assert.Equal("That email and password don't match. Check both and try again.", outcome.Message);
    }

    [Fact]
    public void ALockedAnswerCountsDownFromRetryAfter()
    {
        var outcome = ExoAccountOutcome.From(new AccountReply(429, null, "locked", 75));

        Assert.Equal(75, outcome.RetryAfterSeconds);
        Assert.Equal("Too many tries for this email from this PC. Wait 1:15 and try again.", outcome.Message);
    }

    [Fact]
    public void AClientBugIsShownGenericallyAndNeverAsItsCode()
    {
        var outcome = ExoAccountOutcome.From(new AccountReply(400, null, "bad-json", null));

        Assert.False(outcome.Offline);
        Assert.Equal(AccountMessages.Generic, outcome.Message);
        Assert.DoesNotContain("bad-json", outcome.Message);
    }

    [Fact]
    public void ACreatedAccountReturnsTheRecoveryCodeAndNoSecret()
    {
        var outcome = ExoAccountOutcome.Done("K7Q2-9TMA-XB4R-LZ8P-WH3D-6NVC");

        Assert.True(outcome.Ok);
        Assert.Equal("K7Q2-9TMA-XB4R-LZ8P-WH3D-6NVC", outcome.RecoveryCode);
        Assert.Empty(outcome.Message);
    }
}

using System.Net;
using System.Net.Http;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Which failures earn a crash report: an Exo Capture exit that Exo did not ask for with a non-zero code; a store launch
/// or install the user is shown, not a stop, a dependency or offline prompt, a cancel or a pause; and an update download
/// reason that names only the HTTP status it carried.
/// </summary>
public sealed class FailureReportRulesTests
{
    [Theory]
    [InlineData(1, false, true)]
    [InlineData(-1073741819, false, true)]
    [InlineData(0, false, false)]
    [InlineData(FailureReportRules.CaptureGpuUnavailableExitCode, false, false)]
    [InlineData(1, true, false)]
    [InlineData(0, true, false)]
    public void CaptureCrashOnlyWhenNonZeroAndNotStoppedByExo(int exitCode, bool stoppedByExo, bool expected) =>
        Assert.Equal(expected, FailureReportRules.IsCaptureCrash(exitCode, stoppedByExo));

    [Fact]
    public void StoreLaunchFailureIsAFailedLaunchTheUserSees()
    {
        Assert.True(FailureReportRules.IsStoreLaunchFailure(ok: false, stopRequested: false, needsDependencies: false, offlinePrompt: false));
        Assert.False(FailureReportRules.IsStoreLaunchFailure(ok: true, stopRequested: false, needsDependencies: false, offlinePrompt: false));
        Assert.False(FailureReportRules.IsStoreLaunchFailure(ok: false, stopRequested: true, needsDependencies: false, offlinePrompt: false));
        Assert.False(FailureReportRules.IsStoreLaunchFailure(ok: false, stopRequested: false, needsDependencies: true, offlinePrompt: false));
        Assert.False(FailureReportRules.IsStoreLaunchFailure(ok: false, stopRequested: false, needsDependencies: false, offlinePrompt: true));
    }

    [Fact]
    public void StoreInstallFailureExcludesCancelsAndPauses()
    {
        Assert.True(FailureReportRules.IsStoreInstallFailure(ok: false, message: "The store action did not finish.", cancelRequested: false));
        Assert.False(FailureReportRules.IsStoreInstallFailure(ok: true, message: "Installed.", cancelRequested: false));
        Assert.False(FailureReportRules.IsStoreInstallFailure(ok: false, message: "Canceled.", cancelRequested: false));
        Assert.False(FailureReportRules.IsStoreInstallFailure(ok: false, message: "Paused.", cancelRequested: false));
        Assert.False(FailureReportRules.IsStoreInstallFailure(ok: false, message: "Failed.", cancelRequested: true));
    }

    [Fact]
    public void UpdateDownloadReasonCarriesHttpStatusAndSafeHostPath()
    {
        var withStatus = new HttpRequestException("Response status code does not indicate success.", null, HttpStatusCode.NotFound);
        Assert.Equal("Update download failed (HTTP 404)", FailureReportRules.UpdateDownloadReason(withStatus));
        Assert.Equal(
            "Update download failed (HTTP 404) on updates.getexo.app/v1/launcher/download/v3.29.0/ExoLauncher-Setup.exe",
            FailureReportRules.UpdateDownloadReason(withStatus, new Uri("https://updates.getexo.app/v1/launcher/download/v3.29.0/ExoLauncher-Setup.exe?token=secret#frag")));
        Assert.Equal(
            "Update download failed (HTTP 404) on updates.getexo.app/v1/launcher/download/v3.30.3/ExoLauncher-Setup.exe",
            FailureReportRules.UpdateDownloadReason(withStatus, new Uri("https://user:secret@updates.getexo.app/v1/launcher/download/v3.30.3/ExoLauncher-Setup.exe?sig=abc")));
        Assert.Equal(
            "Update download failed (HTTP 404)",
            FailureReportRules.UpdateDownloadReason(withStatus, new Uri("/v1/launcher/download/v3.30.3/ExoLauncher-Setup.exe?token=secret", UriKind.Relative)));

        Assert.Equal(
            "updates.getexo.app/v1/launcher/download/v3.30.3/files.pack",
            FailureReportRules.SafeUpdateUrl("https://updates.getexo.app/v1/launcher/download/v3.30.3/files.pack?sig=secret"));
        Assert.Null(FailureReportRules.SafeUpdateUrl((Uri?)null));
        Assert.Null(FailureReportRules.SafeUpdateUrl("/relative/path?token=secret"));

        Assert.Equal("Update download failed", FailureReportRules.UpdateDownloadReason(new HttpRequestException("network")));
        Assert.Equal("Update download failed", FailureReportRules.UpdateDownloadReason(new IOException("disk")));
    }

    [Fact]
    public void StoreFailuresExcludeExpectedUserOfflineAndAuthMessages()
    {
        Assert.False(FailureReportRules.IsStoreLaunchFailure(false, false, false, false, "Riot's launch service did not become ready. Riot Client has been opened; check sign-in and try Play again."));
        Assert.False(FailureReportRules.IsStoreLaunchFailure(false, false, false, false, "Riot Client did not answer the launch request."));
        Assert.False(FailureReportRules.IsStoreLaunchFailure(false, false, false, false, "Riot is updating VALORANT (42%)."));
        Assert.False(FailureReportRules.IsStoreLaunchFailure(false, false, false, false, "VALORANT is not installed in Riot (unknown)."));
        Assert.True(FailureReportRules.IsStoreLaunchFailure(false, false, false, false, "Riot reports this game cannot launch right now."));

        Assert.False(FailureReportRules.IsStoreInstallFailure(false, "Not enough free space on D:.", false));
        Assert.False(FailureReportRules.IsStoreInstallFailure(false, "Sign in to Epic Games first.", false));
        Assert.True(FailureReportRules.IsStoreInstallFailure(false, "Manifest verification failed.", false));
    }

    [Fact]
    public void DiscordGatewayReportSkipsNetworkDropsWithoutCloseCode()
    {
        Assert.False(FailureReportRules.IsDiscordGatewayReportable(null, networkFailure: true));
        Assert.False(FailureReportRules.IsDiscordGatewayReportable(null, networkFailure: false));
        Assert.False(FailureReportRules.IsDiscordGatewayReportable(4000, networkFailure: true));
        Assert.True(FailureReportRules.IsDiscordGatewayReportable(4000, networkFailure: false));
    }

    [Fact]
    public void DiscordRestIsIdAcceptsOnlyNumericSnowflakes()
    {
        Assert.True(ExoLauncher.Services.DiscordClient.DiscordRest.IsId("123456789012345678"));
        Assert.False(ExoLauncher.Services.DiscordClient.DiscordRest.IsId("@me"));
        Assert.False(ExoLauncher.Services.DiscordClient.DiscordRest.IsId("friends"));
        Assert.False(ExoLauncher.Services.DiscordClient.DiscordRest.IsId(""));
        Assert.False(ExoLauncher.Services.DiscordClient.DiscordRest.IsId(null));
        Assert.False(ExoLauncher.Services.DiscordClient.DiscordRest.IsId("123456789012345678901"));
    }

    [Fact]
    public void AHostCallIsABugOnlyWhenNobodyPlannedForItsFailure()
    {
        // A refusal meant for the person, a cancel, a timeout and another server's failure are answers, not bugs.
        Assert.False(FailureReportRules.IsHostCallBug(new UserFacingException("Unknown method: app.info")));
        Assert.False(FailureReportRules.IsHostCallBug(new InvalidOperationException("Sign in to Steam first.")));
        Assert.False(FailureReportRules.IsHostCallBug(new InvalidDataException("That file isn't a backup.")));
        Assert.False(FailureReportRules.IsHostCallBug(new OperationCanceledException()));
        Assert.False(FailureReportRules.IsHostCallBug(new TaskCanceledException()));
        Assert.False(FailureReportRules.IsHostCallBug(new TimeoutException()));
        Assert.False(FailureReportRules.IsHostCallBug(new HttpRequestException("network")));
        // ObjectDisposedException is an InvalidOperationException in .NET: it stays an answer, by the same rule.
        Assert.True(FailureReportRules.IsHostCallBug(new NullReferenceException()));
        Assert.True(FailureReportRules.IsHostCallBug(new KeyNotFoundException()));
        Assert.True(FailureReportRules.IsHostCallBug(new ArgumentException("bad")));
        Assert.True(FailureReportRules.IsHostCallBug(new IOException("disk")));
        Assert.True(FailureReportRules.IsHostCallBug(new UnauthorizedAccessException()));
    }

    [Fact]
    public void AHostCallReportNamesTheMethodAndTheTypeNeverTheMessage()
    {
        var reason = FailureReportRules.HostCallReason("library.get", new IOException(@"C:\Users\Erix\secret.txt was locked"));
        Assert.Equal("library.get: IOException", reason);
    }

    [Fact]
    public void APageErrorReportIsItsContextAndOneShortLine()
    {
        Assert.Equal("BackupSection.now: Cannot read properties of undefined", FailureReportRules.PageErrorReason("BackupSection.now", "Cannot read properties of undefined"));
        Assert.Equal("first second", FailureReportRules.PageErrorReason(null, "first\r\nsecond"));
        Assert.Equal(200 + "ctx: ".Length, FailureReportRules.PageErrorReason("ctx", new string('x', 5000)).Length);
    }

    [Fact]
    public void UnknownRpcMethodAndUserFacingExceptionsAreNotReportedAsHostCallBugs()
    {
        Assert.False(FailureReportRules.IsHostCallBug(new UserFacingException("Unknown method: app.info")));
        Assert.True(FailureReportRules.IsHostCallBug(new NullReferenceException("boom")));
    }
}

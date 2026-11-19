using System.Net;
using System.Net.Http;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The XeSS release rule and the transient-or-refused split for a download (DlssSwapService.cs, DlssSwapService.SwapperCatalog.cs).
/// XeSS's SDK tag and its DLL differ in major (tag 3.0.2 ships libxess.dll 2.0.2.68), so the rule checks the DLL and the tag each against what
/// Exo knows. Ways this could fail: a genuine newer XeSS is refused because its DLL's major differs from the tag; a version already pinned is
/// accepted again; an older tag is accepted; a server error or a timeout is remembered as a refusal for 24 hours; a 404 is retried forever.
/// </summary>
public sealed class DlssSwapperRulesTests
{
    [Fact]
    public void XessTag302IsAcceptedWhenItsDllIsNewerThanKnownAndTheTagNewerThanKnown()
    {
        // Real numbers: XeSS 3.0.2 ships libxess.dll 2.0.2.68, and the newest tag Exo pins before it is 2.1.1.
        Assert.Null(DlssSwapService.DllTagRejection("3.0.2", "2.0.2.68", [], ["2.1.1"]));
    }

    [Fact]
    public void XessTag302IsRefusedWhenItsDllIsAlreadyKnown()
    {
        // Pinned already: the same 2.0.2.68 with tag 3.0.2 is not newer than what Exo has.
        Assert.Contains("isn't newer than", DlssSwapService.DllTagRejection("3.0.2", "2.0.2.68", ["2.0.2.68"], ["3.0.2"]));
    }

    [Fact]
    public void AnXessTagOlderThanTheKnownTagIsRefusedEvenWithANewerDll()
    {
        Assert.Contains("isn't newer than 3.0.2", DlssSwapService.DllTagRejection("2.1.1", "2.0.2.69", [], ["3.0.2"]));
    }

    [Fact]
    public void AGenuinelyNewerXessIsAcceptedThroughTheMajorMismatch()
    {
        Assert.Null(DlssSwapService.DllTagRejection("3.0.3", "2.0.2.69", ["2.0.2.68"], ["3.0.2"]));
    }

    [Fact]
    public void AnUnreadableDllVersionOrTagIsRefused()
    {
        Assert.NotNull(DlssSwapService.DllTagRejection("3.0.2", "not-a-version", [], []));
        Assert.NotNull(DlssSwapService.DllTagRejection("v3", "2.0.2.69", [], []));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public void AServerAnswerIsTransientOnlyFor5xx(HttpStatusCode status, bool transient) =>
        Assert.Equal(transient, DlssSwapService.IsTransientDownloadFailure(new HttpRequestException("answer", null, status)));

    [Fact]
    public void ANetworkFailureAndATimeoutAreTransient()
    {
        Assert.True(DlssSwapService.IsTransientDownloadFailure(new HttpRequestException("no network")));
        Assert.True(DlssSwapService.IsTransientDownloadFailure(new TimeoutException("stalled")));
        Assert.True(DlssSwapService.IsTransientDownloadFailure(new TaskCanceledException("timed out")));
        Assert.True(DlssSwapService.IsTransientDownloadFailure(new IOException("disk")));
    }

    [Fact]
    public void AnInvalidArchiveIsARefusalNotATransientFailure() =>
        Assert.False(DlssSwapService.IsTransientDownloadFailure(new InvalidDataException("The archive has no nvngx_dlss.dll.")));
}

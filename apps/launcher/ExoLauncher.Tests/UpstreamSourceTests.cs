using System.Text.Json.Nodes;
using ExoLauncher.Services;
using static ExoLauncher.Services.DlssSwapService;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways asking upstream first (UpstreamSource) could go wrong: an Exo cache path maps to a host Exo never named, or to the
/// wrong GitHub path; Exo's own content (the DLSS Swapper catalog it builds) is asked of someone else; the cache is not the
/// last source; a redirect off GitHub's release hosts or to plain http is followed; a GitHub release with no digest, a
/// prerelease, two matching files, a file too large or an older tag than the pin is taken.
/// </summary>
public sealed class UpstreamSourceTests
{
    private const string Cache = "https://updates.getexo.app/v1/cache/";
    private const string Commit = "374959484e79a640feaba44c93ac8cfb0a03f5b5";

    [Theory]
    [InlineData("NVIDIA/DLSS/releases/latest", "https://api.github.com/repos/NVIDIA/DLSS/releases/latest")]
    [InlineData("NVIDIA/DLSS/commits/v310.9.1", "https://api.github.com/repos/NVIDIA/DLSS/commits/v310.9.1")]
    [InlineData("NVIDIA/DLSS/contents/lib/Windows_x86_64/rel?ref=" + Commit, "https://api.github.com/repos/NVIDIA/DLSS/contents/lib/Windows_x86_64/rel?ref=" + Commit)]
    [InlineData("NVIDIA/DLSS/" + Commit + "/lib/Windows_x86_64/rel/nvngx_dlss.dll", "https://raw.githubusercontent.com/NVIDIA/DLSS/" + Commit + "/lib/Windows_x86_64/rel/nvngx_dlss.dll")]
    [InlineData("optiscaler/OptiScaler/releases/download/v0.9.5/Optiscaler_0.9.5.7z", "https://github.com/optiscaler/OptiScaler/releases/download/v0.9.5/Optiscaler_0.9.5.7z")]
    [InlineData("dlss-swapper-downloads.beeradmoore.com/dlss/dlss_v310.9.16.0_C77D.zip", "https://dlss-swapper-downloads.beeradmoore.com/dlss/dlss_v310.9.16.0_C77D.zip")]
    public void ACachePathMapsToItsUpstream(string path, string upstream) =>
        Assert.Equal(upstream, UpstreamSource.UpstreamOf(Cache + path)?.AbsoluteUri);

    [Theory]
    [InlineData(Cache + "exo/dlss-swapper/catalog.json")]
    [InlineData(Cache + "files.pythonhosted.org/packages/x.whl")]
    [InlineData(Cache + "NVIDIA/DLSS/contents/lib?ref=main")]
    [InlineData(Cache + "NVIDIA/DLSS/../x/releases/latest")]
    [InlineData(Cache + "NVIDIA/DLSS/notacommit/lib/nvngx_dlss.dll")]
    [InlineData("https://evil.example/v1/cache/NVIDIA/DLSS/releases/latest")]
    public void ExosOwnOrUnknownPathsHaveNoUpstream(string url) => Assert.Null(UpstreamSource.UpstreamOf(url));

    [Fact]
    public void TheCacheIsAlwaysTheLastSource()
    {
        var url = Cache + "NVIDIA/DLSS/releases/latest";
        Assert.Equal(["https://api.github.com/repos/NVIDIA/DLSS/releases/latest", url], UpstreamSource.SourcesFor(url).ToArray());
        var own = Cache + "exo/dlss-swapper/catalog.json";
        Assert.Equal([own], UpstreamSource.SourcesFor(own).ToArray());
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://github.com/a/b/releases/download/t/f", true)]
    [InlineData("http://release-assets.githubusercontent.com/x", false)]
    [InlineData("https://release-assets.githubusercontent.com:8443/x", false)]
    [InlineData("https://user@objects.githubusercontent.com/x", false)]
    [InlineData("https://githubusercontent.com.evil.example/x", false)]
    public void OnlyGitHubsOwnHostsAreHops(string url, bool allowed) => Assert.Equal(allowed, UpstreamSource.IsUpstreamHop(new Uri(url)));

    private static JsonNode Release(string tag, params (string Name, long Size, string? Digest)[] assets) => new JsonObject
    {
        ["tag_name"] = tag,
        ["draft"] = false,
        ["prerelease"] = false,
        ["assets"] = new JsonArray(assets.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["size"] = a.Size, ["digest"] = a.Digest }).ToArray()),
    };

    private static readonly string Digest = "sha256:" + new string('b', 64);

    [Fact]
    public void AReleaseWithOneMatchingFileAndADigestIsTaken()
    {
        var release = ParseUpstreamRelease(MfgUnlocker, Release("v1.7.1", ("MFGUnlocker-v1.7.1.zip", 3487244, Digest), ("SHA256SUMS.txt", 171, Digest)));
        Assert.NotNull(release);
        Assert.Equal("v1.7.1", release!.Tag);
        Assert.Equal("MFGUnlocker-v1.7.1.zip", release.Asset);
        Assert.Equal(new string('b', 64), release.Sha256);
        Assert.NotNull(ParseUpstreamRelease(DlssgSm86, Release("0.4.2", ("dlssg-release-0.4.2-x64.zip", 89769758, Digest))));
    }

    [Fact]
    public void AReleaseExoCannotCheckIsRefused()
    {
        Assert.Null(ParseUpstreamRelease(MfgUnlocker, Release("v1.7.1", ("MFGUnlocker-v1.7.1.zip", 3487244, null))));
        Assert.Null(ParseUpstreamRelease(MfgUnlocker, Release("v1.7.1", ("MFGUnlocker-v1.7.1.zip", 3487244, "sha1:abc"))));
        Assert.Null(ParseUpstreamRelease(MfgUnlocker, Release("v1.7.1", ("MFGUnlocker-v1.7.1.zip", MfgUnlocker.MaxSize + 1, Digest))));
        Assert.Null(ParseUpstreamRelease(MfgUnlocker, Release("v1.7.1", ("MFGUnlocker-v1.7.1.zip", 1, Digest), ("MFGUnlocker-v1.7.1-b.zip", 1, Digest))));
        Assert.Null(ParseUpstreamRelease(MfgUnlocker, Release("v1.4.0", ("MFGUnlocker-v1.4.0.zip", 1, Digest))));
        Assert.Null(ParseUpstreamRelease(MfgUnlocker, Release("../v1.7.1", ("MFGUnlocker-v1.7.1.zip", 1, Digest))));
        var pre = Release("v1.8.0", ("MFGUnlocker-v1.8.0.zip", 1, Digest));
        pre["prerelease"] = true;
        Assert.Null(ParseUpstreamRelease(MfgUnlocker, pre));
    }
}

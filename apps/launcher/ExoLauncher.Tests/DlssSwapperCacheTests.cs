using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// DLSS Swapper's archive zips are mirrored in Exo's cache, so no Launcher request goes to DLSS Swapper's own host.
/// Ways this could fail: a pinned zip still points at dlss-swapper-downloads.beeradmoore.com; a pinned DLL is fetched from
/// another host; a beeradmoore host is added to the network destinations or to code again. The release list is the pins
/// only: the manifest is not fetched.
/// </summary>
public sealed class DlssSwapperCacheTests
{
    private const string CacheHost = "updates.getexo.app";
    private const string CachePrefix = "/v1/cache/dlss-swapper-downloads.beeradmoore.com/";

    private static readonly Regex BeeradmooreRequest = new(@"https?://[A-Za-z0-9.-]*beeradmoore\.", RegexOptions.IgnoreCase);

    [Fact]
    public void EveryPinnedArchiveZipIsServedFromExosCacheAtTheSamePath()
    {
        var zips = DlssSwapService.PinnedFiles.Where(file => file.Zip is not null).ToList();

        Assert.Equal(3, zips.Count);
        foreach (var file in zips)
        {
            var uri = new Uri(file.Zip!.Url);
            Assert.Equal(CacheHost, uri.Host);
            Assert.StartsWith(CachePrefix, uri.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith(".zip", uri.AbsolutePath, StringComparison.Ordinal);
            Assert.True(file.Zip.Size > 0);
        }
    }

    [Fact]
    public void EveryPinnedFileIsDownloadedFromExosCache()
    {
        Assert.All(DlssSwapService.PinnedFiles, file => Assert.Equal(CacheHost, new Uri(file.Url).Host));
    }

    [Fact]
    public void NoRuntimeRequestGoesToBeeradmoore()
    {
        Assert.DoesNotContain(NetworkDestinations.Hosts, host => host.Host.Contains("beeradmoore", StringComparison.OrdinalIgnoreCase));

        var offenders = Repo.HostSources()
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(entry => !entry.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && BeeradmooreRequest.IsMatch(entry.line))
            .Select(entry => $"{Path.GetRelativePath(Repo.Root, entry.path)}:{entry.index + 1}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Code that requests DLSS Swapper's host: " + string.Join(", ", offenders));
    }
}

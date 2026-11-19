using ExoLauncher.Adapters.Cli;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways download tuning could fail without a sign:
/// - a failed or timed-out CDN probe ranks first (as zero time) and Legendary is sent to a dead host;
/// - the slowest host wins because the ranking sorts the wrong way, or a host probed twice counts twice;
/// - a ranking older than 6 hours keeps steering every install, or an empty one replaces a good one;
/// - workers or shared memory leave Legendary's documented range (16 workers; 2 GiB default), or a PC with little free
///   memory gets one worker per thread and pages while the user plays;
/// - the CDN option is passed empty, or a manifest list host Exo doesn't list (or plain http) gets probed.
/// </summary>
public sealed class DownloadTuningTests
{
    [Fact]
    public void RankingPutsTheFastestFirstAndDropsFailures()
    {
        var ranking = CdnRanking.Rank([
            ("cloudflare.epicgamescdn.com", TimeSpan.FromMilliseconds(455)),
            ("fastly-download.epicgames.com", null),
            ("epicgames-download1.akamaized.net", TimeSpan.FromMilliseconds(92)),
            ("download.epicgames.com", TimeSpan.FromMilliseconds(123)),
            ("EPICGAMES-DOWNLOAD1.akamaized.net", TimeSpan.FromMilliseconds(300)),
        ]);
        Assert.Equal(["epicgames-download1.akamaized.net", "download.epicgames.com", "cloudflare.epicgamescdn.com"], ranking);
    }

    [Fact]
    public void RankingOfOnlyFailuresIsEmpty() =>
        Assert.Empty(CdnRanking.Rank([("download.epicgames.com", null), ("", TimeSpan.FromMilliseconds(1))]));

    [Fact]
    public void CachedRankingExpiresAfterItsLifetime()
    {
        var now = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        var cache = new CdnRankingCache(TimeSpan.FromHours(6), () => now);
        Assert.Null(cache.Best());

        cache.Store(["a.example", "b.example"]);
        now += TimeSpan.FromHours(5.9);
        Assert.Equal("a.example", cache.Best());

        cache.Store([]);
        Assert.Equal("a.example", cache.Best());

        now += TimeSpan.FromHours(0.2);
        Assert.Null(cache.Best());
    }

    [Theory]
    [InlineData(12, 8192, 12)]   // this PC: one per thread
    [InlineData(32, 65536, 16)]  // Legendary's own cap
    [InlineData(12, 768, 6)]     // little free memory: one per 128 MiB
    [InlineData(2, 100, 2)]      // never below two
    public void WorkersFollowThreadsAndFreeMemory(int threads, long freeMiB, int expected) =>
        Assert.Equal(expected, DownloadTuning.Workers(threads, freeMiB));

    [Theory]
    [InlineData(8192, 1024)]
    [InlineData(100, 512)]
    [InlineData(65536, 2048)]
    [InlineData(5000, 576)]  // 625 MiB, in 64 MiB steps
    public void SharedMemoryStaysInLegendarysRange(long freeMiB, int expected) =>
        Assert.Equal(expected, DownloadTuning.LegendarySharedMemoryMiB(freeMiB));

    [Fact]
    public void LegendaryOptionsAppendAfterTheCommand()
    {
        string[] args = ["install", "app", "-y", "--update-only"];
        Assert.Equal(["install", "app", "-y", "--update-only", "--max-workers", "12", "--max-shared-memory", "1024",
                "--preferred-cdn", "epicgames-download1.akamaized.net"],
            LegendaryCli.WithDownloadOptions(args, "epicgames-download1.akamaized.net", 12, 1024));
        Assert.DoesNotContain("--preferred-cdn", LegendaryCli.WithDownloadOptions(args, null, 12, 1024));
        Assert.DoesNotContain("--preferred-cdn", LegendaryCli.WithDownloadOptions(args, " ", 12, 1024));
    }

    [Fact]
    public void SharedMemoryFailureIsRecognized()
    {
        Assert.True(LegendaryCli.SharedMemoryTooSmall(
            "MemoryError: Current shared memory cache is smaller than required: 512.0 MiB < 1100.0 MiB. Try running legendary with"));
        Assert.False(LegendaryCli.SharedMemoryTooSmall("Disk space is insufficient"));
    }

    [Fact]
    public void ManifestListKeepsListedHttpsHostsWithTheirQuery()
    {
        const string json = """
            {"elements":[{"hash":"x","manifests":[
              {"uri":"https://epicgames-download1.akamaized.net/Builds/Org/a/b/default.manifest"},
              {"uri":"https://fastly-download.epicgames.com/Builds/Org/a/b/default.manifest","queryParams":[{"name":"f_token","value":"t"},{"name":"x","value":"1"}]},
              {"uri":"https://epicgames-download1.akamaized.net/Builds/Org/a/b/other.manifest"},
              {"uri":"http://download.epicgames.com/Builds/Org/a/b/default.manifest"},
              {"uri":"https://cdn.example.com/default.manifest"}
            ]}]}
            """;
        var urls = EpicCdn.ParseManifestUrls(json).Select(url => url.AbsoluteUri).ToArray();
        Assert.Equal([
            "https://epicgames-download1.akamaized.net/Builds/Org/a/b/default.manifest",
            "https://fastly-download.epicgames.com/Builds/Org/a/b/default.manifest?f_token=t&x=1",
        ], urls);
        Assert.Empty(EpicCdn.ParseManifestUrls("""{"elements":[]}"""));
        Assert.Empty(EpicCdn.ParseManifestUrls("not json"));
    }
}

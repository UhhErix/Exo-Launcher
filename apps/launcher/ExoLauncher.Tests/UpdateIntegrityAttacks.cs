using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Attack 4 — update and download integrity, driven through the real classes
/// with a stubbed HttpMessageHandler (no network). The upscaler/tool path is
/// <see cref="VerifiedGitHubReleaseDownloader"/>, which reads Exo's cache only;
/// the app updater's host/version gates are <see cref="AppUpdateService"/>.
/// Ways it could fail: bytes that differ from the pin are promoted; a redirect
/// sends the download elsewhere; a host other than the cache is asked; a file
/// that fails its executable check stays at the destination.
/// </summary>
public class UpdateIntegrityAttacks
{
    // A cache stub that serves whatever body each URL asks for.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Requested = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }

    private static readonly byte[] GoodBytes = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
    private static string GoodSha => Convert.ToHexString(SHA256.HashData(GoodBytes)).ToLowerInvariant();

    private static GitHubReleaseAsset Asset(string sha, long size) =>
        new("vendorX", "repoY", "v1.0.0", "tool.exe", size, sha);

    private static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string TempDest() => Path.Combine(Path.GetTempPath(), "exo-upd-" + Guid.NewGuid().ToString("N"), "tool.exe");

    [Fact]
    public async Task Verified_download_accepts_matching_pin_from_the_cache_only()
    {
        var asset = Asset(GoodSha, GoodBytes.Length);
        var handler = new StubHandler(_ => Bytes(GoodBytes));
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            var path = await dl.DownloadPinnedAsync(asset, dest, _ => true, CancellationToken.None);
            Assert.True(File.Exists(path));
            Assert.Equal(GoodSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
            // One request, to the cache at the release's own path; no GitHub host is asked.
            Assert.Equal(["https://updates.getexo.app/v1/cache/vendorX/repoY/releases/download/v1.0.0/tool.exe"], handler.Requested);
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Verified_download_rejects_wrong_sha256()
    {
        // Pin a SHA that does not match the served bytes.
        var asset = Asset(new string('a', 64), GoodBytes.Length);
        var handler = new StubHandler(_ => Bytes(GoodBytes));
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => dl.DownloadPinnedAsync(asset, dest, _ => true, CancellationToken.None));
            Assert.False(File.Exists(dest), "a hash-mismatched asset was promoted to its destination");
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Verified_download_rejects_bytes_of_the_wrong_size()
    {
        // The pin says 10 bytes more than the cache serves: the download is refused, nothing promoted.
        var asset = Asset(GoodSha, GoodBytes.Length + 10);
        var handler = new StubHandler(_ => Bytes(GoodBytes));
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => dl.DownloadPinnedAsync(asset, dest, _ => true, CancellationToken.None));
            Assert.False(File.Exists(dest));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Verified_download_refuses_a_redirect_instead_of_following_it()
    {
        // The cache answers 302 to an attacker host: the download stops, no second request is made.
        var asset = Asset(GoodSha, GoodBytes.Length);
        var handler = new StubHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.example.com/tool.exe");
            return r;
        });
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            await Assert.ThrowsAnyAsync<SecurityException>(() => dl.DownloadPinnedAsync(asset, dest, _ => true, CancellationToken.None));
            Assert.Single(handler.Requested);
            Assert.False(File.Exists(dest));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Verified_download_rejects_executable_validation_failure()
    {
        // Bytes + sha match but the executable validator says no (e.g. wrong PE
        // machine). The file must not be promoted.
        var asset = Asset(GoodSha, GoodBytes.Length);
        var handler = new StubHandler(_ => Bytes(GoodBytes));
        using var dl = new VerifiedGitHubReleaseDownloader(handler);
        var dest = TempDest();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => dl.DownloadPinnedAsync(asset, dest, _ => false, CancellationToken.None));
            Assert.False(File.Exists(dest));
        }
        finally { Cleanup(dest); }
    }

    // ---- URL predicates ----

    [Theory]
    [InlineData("https://updates.getexo.app/v1/cache/vendorX/repoY/releases/download/v1.0.0/tool.exe", true)]
    [InlineData("https://updates.getexo.app/v1/cache/vendorX/repoY/releases/download/v1.0.0/other.exe", false)]
    [InlineData("https://updates.getexo.app/v1/cache/evil/repoY/releases/download/v1.0.0/tool.exe", false)]
    [InlineData("https://updates.getexo.app/v1/cache/vendorX/repoY/releases/download/v1.0.0/tool.exe?x=1", false)]
    [InlineData("http://updates.getexo.app/v1/cache/vendorX/repoY/releases/download/v1.0.0/tool.exe", false)]
    [InlineData("https://updates.getexo.app.evil.com/v1/cache/vendorX/repoY/releases/download/v1.0.0/tool.exe", false)]
    [InlineData("https://github.com/vendorX/repoY/releases/download/v1.0.0/tool.exe", false)]
    public void IsExpectedAssetDownloadUri_is_strict(string url, bool expected)
    {
        var asset = Asset(GoodSha, 1);
        Assert.Equal(expected, VerifiedGitHubReleaseDownloader.IsExpectedAssetDownloadUri(new Uri(url), asset));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("md5:abc", false)]
    [InlineData("sha256:xyz", false)]
    [InlineData("sha256:" + "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", true)]
    public void ParseSha256Digest_only_accepts_valid_sha256(string? digest, bool valid) =>
        Assert.Equal(valid, VerifiedGitHubReleaseDownloader.ParseSha256Digest(digest) is not null);

    // ---- app updater host + version gates ----

    [Fact]
    public async Task App_updater_refuses_downgrade()
    {
        // Updater picks up a lower remote version than local: it reports
        // AlreadyLatest and offers no update. Served through the real class.
        var handler = new StubHandler(_ =>
        {
            var json = new { app = "launcher", tag = "v1.0.0" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(json)) };
        });
#pragma warning disable RS0030 // Stub handler: no request leaves the process.
        using var http = new HttpClient(handler);
#pragma warning restore RS0030
        var updatesDir = Path.Combine(Path.GetTempPath(), "exo-updtest-" + Guid.NewGuid().ToString("N"));
        var svc = new AppUpdateService(http, updatesDir, TimeProvider.System);
        try
        {
            var result = await svc.CheckAsync("9.9.9", TestContext.Current.CancellationToken);
            Assert.False(result.UpdateAvailable, "updater offered a downgrade");
            Assert.True(result.AlreadyLatest);
        }
        finally { try { DeletionGate.DeleteDirectory(updatesDir, true); } catch { } }
    }

    private static void Cleanup(string dest)
    {
        try
        {
            var dir = Path.GetDirectoryName(dest);
            if (dir is not null && Directory.Exists(dir)) DeletionGate.DeleteDirectory(dir, true);
        }
        catch { }
    }
}

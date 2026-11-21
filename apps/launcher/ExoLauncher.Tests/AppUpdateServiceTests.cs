using System.Net;
using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways update metadata parsing can fail:
/// - an asset with another name (a .zip, a .sha256, a look-alike) is taken as the installer;
/// - a download URL on another host, port, user-info or repository, or for another tag, is trusted;
/// - a .sha256 file naming a different asset, or with extra tokens, is accepted;
/// - a digest that is not 64 hex characters is accepted, or a "sha256:" prefix or upper case is rejected;
/// - release JSON without assets, or with a bad digest, throws instead of returning incomplete metadata;
/// - a later verified asset loses to an earlier unverified one;
/// - the update address's answer for another app, or with a tag that isn't a plain vX.Y.Z (a suffix, no "v", a path,
///   a trailing newline, non-ASCII digits), is taken, or a body that isn't JSON throws;
/// - file URLs in the address's answer are followed instead of the ones Exo builds on the address itself;
/// - an address that is down or answers wrong sends the check to a host other than Exo's, or makes it throw instead of failing cleanly;
/// - a download of Exo's files is followed to a GitHub host;
/// - an up-to-date check still asks for the release's files.
/// </summary>
public sealed class AppUpdateServiceTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Address = "https://updates.getexo.app/v1/launcher/";

    [Theory]
    [InlineData("ExoLauncher.exe", true)]
    [InlineData("ExoLauncher-Setup.exe", true)]
    [InlineData("exolauncher-setup.EXE", true)]
    [InlineData("ExoLauncher-Setup.exe.sha256", false)]
    [InlineData("ExoLauncher-Setup.zip", false)]
    [InlineData("ExoLauncher-Setup.exe ", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    public void UpdateAssetNames(string name, bool expected) =>
        Assert.Equal(expected, AppUpdateService.IsUpdateAssetName(name));

    [Theory]
    [InlineData(Address + "download/v3.17.1/ExoLauncher-Setup.exe", true)]
    [InlineData("https://UPDATES.GETEXO.APP/x", true)]
    [InlineData("https://github.com/x", false)]
    [InlineData("https://objects.githubusercontent.com/x", false)]
    [InlineData("https://release-assets.githubusercontent.com/x", false)]
    [InlineData("http://updates.getexo.app/x", false)]
    [InlineData("https://exo-relay.exo-erix.workers.dev/x", false)]
    [InlineData("https://exo-erix.workers.dev/x", false)]
    [InlineData("https://updates.getexo.app.evil.example/x", false)]
    [InlineData("https://evil.workers.dev/x", false)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    [InlineData("https://example.com/x", false)]
    public void DownloadRedirectHosts(string url, bool expected) =>
        Assert.Equal(expected, AppUpdateService.IsAllowedDownloadUri(new Uri(url)));

    private static string AddressReply(string app = "launcher", string tag = "v3.17.1", string files = "") =>
        $$"""{"app":"{{app}}","tag":"{{tag}}","version":"3.17.1","source":"github"{{files}}}""";

    [Fact]
    public void ParsesTheUpdateAddressReply()
    {
        var release = AppUpdateService.ParseUpdateAddressReply(AddressReply());
        Assert.NotNull(release);
        Assert.Equal("v3.17.1", release.Tag);
        Assert.Equal(Address + "download/v3.17.1/ExoLauncher-Setup.exe", release.DownloadUrl);
        Assert.Equal("ExoLauncher-Setup.exe", release.AssetName);
        Assert.Equal(Address + "download/v3.17.1/whats-new.json", release.WhatsNewUrl);
        Assert.Null(release.Size);
        Assert.Null(release.Sha256);
    }

    [Fact]
    public void TakesTheInstallerCheckFromTheManifest()
    {
        var sha = new string('a', 64);
        var release = AppUpdateService.ParseUpdateAddressReply(AddressReply(files:
            ",\"checks\":{\"setup\":{\"size\":123456,\"sha256\":\"" + sha + "\"}}"));
        Assert.NotNull(release);
        Assert.Equal(123456L, release.Size);
        Assert.Equal(sha, release.Sha256);
    }
    [Theory]
    [InlineData("\"size\":0,\"sha256\":\"AAAA\"")]
    [InlineData("\"size\":123,\"sha256\":\"xyz\"")]
    [InlineData("\"size\":123")]
    [InlineData("\"size\":600000000,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("\"size\":\"123\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    public void IgnoresAMalformedInstallerCheck(string setup)
    {
        var release = AppUpdateService.ParseUpdateAddressReply(AddressReply(files: $",\"checks\":{{\"setup\":{{{setup}}}}}"));
        Assert.NotNull(release);
        Assert.Null(release.Size);
        Assert.Null(release.Sha256);
    }
    [Fact]
    public void BuildsTheFileUrlsOnTheAddressNotFromTheReply()
    {
        var release = AppUpdateService.ParseUpdateAddressReply(AddressReply(files: """
            ,"files":{"setup":"https://evil.example/v1/launcher/download/v3.17.1/ExoLauncher-Setup.exe",
            "sha256":"https://evil.example/x.sha256","whatsNew":"https://evil.example/whats-new.json"}
            """));
        Assert.NotNull(release);
        Assert.Equal(Address + "download/v3.17.1/ExoLauncher-Setup.exe", release.DownloadUrl);
        Assert.Equal(Address + "download/v3.17.1/whats-new.json", release.WhatsNewUrl);
    }

    [Theory]
    [InlineData("cinema", "v3.17.1")]
    [InlineData("browser", "v3.17.1")]
    [InlineData("Launcher", "v3.17.1")]
    [InlineData("launcher ", "v3.17.1")]
    [InlineData("launcher", "v3.17.1-beta")]
    [InlineData("launcher", "3.17.1")]
    [InlineData("launcher", "v1.2.3/../x")]
    [InlineData("launcher", "v1.2.3\\n")]
    [InlineData("launcher", "v3.17")]
    [InlineData("launcher", "v3.17.1.0")]
    [InlineData("launcher", "V3.17.1")]
    [InlineData("launcher", "v٣.17.1")] // an Arabic-Indic three
    [InlineData("launcher", "v1234567890.0.0")]
    [InlineData("launcher", "")]
    public void RejectsAnotherAppOrTag(string app, string tag) =>
        Assert.Null(AppUpdateService.ParseUpdateAddressReply(AddressReply(app, tag)));

    [Theory]
    [InlineData("<html>Not found</html>")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"v3.17.1\"")]
    [InlineData("{\"tag\":\"v3.17.1\"}")]
    [InlineData("{\"app\":\"launcher\"}")]
    [InlineData("{\"app\":\"launcher\",\"tag\":3}")]
    [InlineData("{\"app\":[\"launcher\"],\"tag\":\"v3.17.1\"}")]
    [InlineData("{\"app\":\"launcher\",\"tag\":\"v3.17.1\"")]
    public void RejectsRepliesThatAreNotTheAnswer(string body) =>
        Assert.Null(AppUpdateService.ParseUpdateAddressReply(body));

    /// <summary>Answers each URL from the table, records what was asked, and fails anything it doesn't know.</summary>
    private sealed class Stub(Func<Uri, HttpResponseMessage?> respond) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Asked) Asked.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8) };

    private static HttpResponseMessage RedirectTo(string url) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(url) } };

    private static async Task<(AppUpdateService.CheckResult Result, List<Uri> Asked)> CheckWith(string local, Func<Uri, HttpResponseMessage?> respond)
    {
        var stub = new Stub(respond);
#pragma warning disable RS0030 // Stub handler: no request leaves the process.
        using var http = new HttpClient(stub);
#pragma warning restore RS0030
        var updates = Path.Combine(Path.GetTempPath(), "exo-updtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new AppUpdateService(http, updates, TimeProvider.System).CheckAsync(local, TestContext.Current.CancellationToken);
            return (result, stub.Asked);
        }
        finally { try { DeletionGate.DeleteDirectory(updates, true); } catch { /* never created */ } }
    }

    [Fact]
    public async Task OffersTheReleaseTheAddressNamesThroughTheAddress()
    {
        var (result, asked) = await CheckWith("3.16.0", uri => uri.AbsoluteUri switch
        {
            Address + "latest" => Text(AddressReply(files: ",\"files\":{\"setup\":\"https://evil.example/x.exe\"}")),
            Address + "download/v3.17.1/ExoLauncher-Setup.exe.sha256" => Text(Hash + "  ExoLauncher-Setup.exe\n"),
            _ => null,
        });
        Assert.True(result.UpdateAvailable, result.Message);
        Assert.Equal("3.17.1", result.RemoteVersion);
        Assert.Equal(Address + "download/v3.17.1/ExoLauncher-Setup.exe", result.DownloadUrl);
        Assert.Equal(Hash, result.Sha256);
        Assert.Null(result.DownloadSize);
        Assert.All(asked, uri => Assert.Equal("updates.getexo.app", uri.Host));
    }

    [Fact]
    public async Task AnUpToDateCheckAsksOnlyForTheLatestTag()
    {
        var (result, asked) = await CheckWith("3.17.1", uri => uri.AbsoluteUri == Address + "latest" ? Text(AddressReply()) : null);
        Assert.True(result.AlreadyLatest, result.Message);
        Assert.Equal(Address + "latest", Assert.Single(asked).AbsoluteUri);
    }

    [Fact]
    public async Task AnUnavailableAddressFailsCleanlyAndAsksNoOtherHost()
    {
        // The address is the only source: when it doesn't answer, no GitHub (or any other) host is asked.
        var (result, asked) = await CheckWith("3.16.0", uri => uri.AbsoluteUri == Address + "latest"
            ? Text("No release found", HttpStatusCode.ServiceUnavailable)
            : null);
        Assert.False(result.UpdateAvailable);
        Assert.False(result.AlreadyLatest);
        Assert.Contains("Could not check", result.Message);
        Assert.All(asked, uri => Assert.Equal("updates.getexo.app", uri.Host));
    }

    [Theory]
    [InlineData(Hash, Hash)]
    [InlineData("sha256:" + Hash, Hash)]
    [InlineData("SHA256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", Hash)]
    [InlineData("  " + Hash + "  ", Hash)]
    [InlineData("0123", null)]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizesDigests(string? digest, string? expected) =>
        Assert.Equal(expected, AppUpdateService.NormalizeSha(digest));

    [Theory]
    [InlineData(Hash, Hash)]
    [InlineData(Hash + "\n", Hash)]
    [InlineData(Hash + "  ExoLauncher-Setup.exe\n", Hash)]
    [InlineData(Hash + " *ExoLauncher-Setup.exe", Hash)]
    [InlineData(Hash + "  ExoLauncher.exe", null)]
    [InlineData(Hash + "  ExoLauncher-Setup.exe extra", null)]
    [InlineData("", null)]
    [InlineData("nothex  ExoLauncher-Setup.exe", null)]
    public void ParsesSha256Files(string text, string? expected) =>
        Assert.Equal(expected, AppUpdateService.ParseSha256File(text, "ExoLauncher-Setup.exe"));

    [Fact]
    public async Task CleansUpUnstagedInstallersAndOldTempsImmediately()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-updclean-" + Guid.NewGuid().ToString("N"));
        var updates = Path.Combine(root, "updates");
        Directory.CreateDirectory(updates);
        try
        {
            var stub = new Stub(_ => null);
#pragma warning disable RS0030
            using var http = new HttpClient(stub);
#pragma warning restore RS0030
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            var service = new AppUpdateService(http, updates, clock) { AppFolder = Path.Combine(root, "app") };
            await service.WaitForCleanupForTestAsync();

            var activeFile = $"ExoLauncher-Setup-{Guid.NewGuid():N}.exe";
            var staleFile = $"ExoLauncher-Setup-{Guid.NewGuid():N}.exe";
            var partialFile = $"ExoLauncher-Setup-{Guid.NewGuid():N}.partial";
            var oldTmp = "staged-update.json.tmp-" + Guid.NewGuid().ToString("N");
            var freshTmp = "staged-update.json.tmp-" + Guid.NewGuid().ToString("N");

            File.WriteAllText(Path.Combine(updates, activeFile), "staged");
            File.WriteAllText(Path.Combine(updates, staleFile), "stale");
            File.WriteAllText(Path.Combine(updates, partialFile), "partial");
            File.WriteAllText(Path.Combine(updates, oldTmp), "old");
            File.WriteAllText(Path.Combine(updates, freshTmp), "fresh");

            var now = clock.GetUtcNow().UtcDateTime;
            File.SetLastWriteTimeUtc(Path.Combine(updates, staleFile), now - TimeSpan.FromSeconds(5));
            File.SetLastWriteTimeUtc(Path.Combine(updates, oldTmp), now - TimeSpan.FromMinutes(3));
            File.SetLastWriteTimeUtc(Path.Combine(updates, freshTmp), now - TimeSpan.FromSeconds(30));

            File.WriteAllText(
                Path.Combine(updates, "staged-update.json"),
                $$"""{"schema":1,"version":"3.28.0","sha256":"{{Hash}}","size":6,"file":"{{activeFile}}","starts":0}""");

            service.CleanupUpdateArtifactsNow();

            Assert.True(File.Exists(Path.Combine(updates, activeFile)));
            Assert.False(File.Exists(Path.Combine(updates, staleFile)));
            Assert.False(File.Exists(Path.Combine(updates, partialFile)));
            Assert.False(File.Exists(Path.Combine(updates, oldTmp)));
            Assert.True(File.Exists(Path.Combine(updates, freshTmp)));
        }
        finally
        {
            try { ExoLauncher.Helpers.DeletionGate.DeleteDirectory(root, true); } catch { }
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

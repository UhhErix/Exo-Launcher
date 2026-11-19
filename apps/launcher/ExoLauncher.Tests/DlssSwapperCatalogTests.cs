using System.IO.Compression;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// DLSS Swapper's newer archives from Exo's cache (DlssSwapService.SwapperCatalog.cs). The catalog carries no hashes, so the DLL
/// inside is the check: it is taken only through the same signature, identity and version checks a vendor release gets, and only
/// when its size is the catalog's. An entry is downloaded only for a watched file a scanned game ships and only when newer than
/// every known version. Ways this could fail: a malformed catalog lists archives; an entry for an unshipped, unwatched (FSR 3.1) or
/// off-cache file is fetched; an archive with ".." in its path is downloaded; a DLL of another size is extracted; an unsigned DLL,
/// a version not the release's, or one not newer than known, becomes a pin. Fixture bytes are made up; the signature and version
/// are injected for the accept path (a fake DLL cannot carry NVIDIA's signature).
/// </summary>
public sealed class DlssSwapperCatalogTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ArchiveUrl = "https://updates.getexo.app/v1/cache/dlss-swapper-downloads.beeradmoore.com/dlss/dlss_v310.9.17.0_AB.zip";

    private static HashSet<string> Used(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    private static string CatalogJson(string file = "nvngx_dlss.dll", string version = "310.9.17.0", string url = ArchiveUrl, long dllSize = 9, long zipSize = 4) =>
        "{\"schema\":2,\"archives\":[{\"file\":\"" + file + "\",\"version\":\"" + version + "\",\"dllSize\":" + dllSize +
        ",\"zip\":{\"url\":\"" + url + "\",\"size\":" + zipSize + ",\"sha256\":\"" + Hash + "\"}}]}";

    [Fact]
    public void AWellFormedCatalogEntryIsKept()
    {
        var entry = Assert.Single(DlssSwapService.ParseSwapperCatalog(CatalogJson()));

        Assert.Equal(new DlssSwapService.SwapperEntry("nvngx_dlss.dll", "310.9.17.0", 9, ArchiveUrl, 4, Hash), entry);
    }

    [Theory]
    [InlineData("libxess.dll")]
    [InlineData("libxell.dll")]
    public void AXessFileIsWatched(string file) =>
        Assert.Single(DlssSwapService.ParseSwapperCatalog(CatalogJson(file: file)));

    [Theory]
    [InlineData("amd_fidelityfx_dx12.dll", "310.9.17.0", ArchiveUrl, 9, 4)]
    [InlineData("nvngx_dlss.dll", "not-a-version", ArchiveUrl, 9, 4)]
    [InlineData("nvngx_dlss.dll", "310.9.17.0", "https://example.com/dlss/x.zip", 9, 4)]
    [InlineData("nvngx_dlss.dll", "310.9.17.0", "https://updates.getexo.app/v1/cache/dlss-swapper-downloads.beeradmoore.com/dlss/../x.zip", 9, 4)]
    [InlineData("nvngx_dlss.dll", "310.9.17.0", ArchiveUrl, 0, 4)]
    [InlineData("nvngx_dlss.dll", "310.9.17.0", ArchiveUrl, 9, 0)]
    public void AnEntryOutsideTheWatchedFilesOrOffTheCachePathIsDropped(string file, string version, string url, long dllSize, long zipSize) =>
        Assert.Empty(DlssSwapService.ParseSwapperCatalog(CatalogJson(file, version, url, dllSize, zipSize)));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"archives\":\"nope\"}")]
    public void AMalformedCatalogListsNothing(string json) =>
        Assert.Empty(DlssSwapService.ParseSwapperCatalog(json));

    [Fact]
    public void AnEntryNewerThanEveryKnownVersionIsAccepted()
    {
        var entry = Assert.Single(DlssSwapService.ParseSwapperCatalog(CatalogJson()));

        Assert.Null(DlssSwapService.SwapperRejection(entry, Used("nvngx_dlss.dll"), ["310.9.16.0", "310.7.0.0"]));
    }

    [Fact]
    public void AnEntryNotNewerThanKnownIsRefused()
    {
        var entry = Assert.Single(DlssSwapService.ParseSwapperCatalog(CatalogJson(version: "310.9.16.0")));

        Assert.Contains("is not newer than", DlssSwapService.SwapperRejection(entry, Used("nvngx_dlss.dll"), ["310.9.16.0"]));
    }

    [Fact]
    public void AnEntryForAFileNoScannedGameShipsIsNotDownloaded()
    {
        var entry = Assert.Single(DlssSwapService.ParseSwapperCatalog(CatalogJson(file: "nvngx_dlssd.dll")));

        Assert.Equal("no scanned game ships it", DlssSwapService.SwapperRejection(entry, Used("nvngx_dlss.dll"), ["310.9.16.0"]));
    }

    [Theory]
    [InlineData("https://updates.getexo.app/v1/cache/dlss-swapper-downloads.beeradmoore.com/dlss/x.zip", true)]
    [InlineData("https://updates.getexo.app/v1/cache/dlss-swapper-downloads.beeradmoore.com/dlss/../x.zip", false)]
    [InlineData("https://updates.getexo.app/v1/cache/other.example.com/dlss/x.zip", false)]
    [InlineData("http://updates.getexo.app/v1/cache/dlss-swapper-downloads.beeradmoore.com/dlss/x.zip", false)]
    [InlineData("https://updates.getexo.app/v1/cache/dlss-swapper-downloads.beeradmoore.com/", false)]
    public void OnlyArchivesOnTheSwapperCachePathAreAllowed(string url, bool allowed) =>
        Assert.Equal(allowed, DlssSwapService.IsSwapperArchiveUrl(url));

    private static string MakeZip(string dir, string innerName, byte[] inner)
    {
        var path = Path.Combine(dir, "archive.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var stream = zip.CreateEntry(innerName).Open()) stream.Write(inner);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-swapper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task AnArchiveWithoutTheNamedDllAtItsTopIsRefused()
    {
        var dir = TempDir();
        try
        {
            var archive = MakeZip(dir, "other.dll", [1, 2, 3]);
            var entry = new DlssSwapService.SwapperEntry("nvngx_dlss.dll", "310.9.17.0", 3, ArchiveUrl, new FileInfo(archive).Length, Sha(archive));

            var (pin, reason) = await DlssSwapService.BuildSwapperPinAsync(archive, entry, Path.Combine(dir, "dll"), ["310.9.16.0"], (_, _) => true, _ => "310.9.17.0", TestContext.Current.CancellationToken);

            Assert.Null(pin);
            Assert.Equal("the archive has no nvngx_dlss.dll at its top", reason);
        }
        finally { DeletionGate.DeleteDirectory(dir, recursive: true); }
    }

    [Fact]
    public async Task ADllOfAnotherSizeThanTheCatalogStatesIsNotExtracted()
    {
        var dir = TempDir();
        try
        {
            var archive = MakeZip(dir, "nvngx_dlss.dll", [1, 2, 3, 4]);
            var entry = new DlssSwapService.SwapperEntry("nvngx_dlss.dll", "310.9.17.0", 9, ArchiveUrl, new FileInfo(archive).Length, Sha(archive));

            var (pin, reason) = await DlssSwapService.BuildSwapperPinAsync(archive, entry, Path.Combine(dir, "dll"), ["310.9.16.0"], (_, _) => true, _ => "310.9.17.0", TestContext.Current.CancellationToken);

            Assert.Null(pin);
            Assert.Contains("is not the 9 bytes the catalog states", reason);
        }
        finally { DeletionGate.DeleteDirectory(dir, recursive: true); }
    }

    [Fact]
    public async Task AnUnsignedDllIsRefusedBecauseNoHashStandsInItsPlace()
    {
        var dir = TempDir();
        try
        {
            var archive = MakeZip(dir, "nvngx_dlss.dll", [1, 2, 3, 4, 5, 6, 7, 8, 9]);
            var entry = new DlssSwapService.SwapperEntry("nvngx_dlss.dll", "310.9.17.0", 9, ArchiveUrl, new FileInfo(archive).Length, Sha(archive));

            // No injected trust: the real vendor signature check reads the fake bytes and refuses them.
            var (pin, reason) = await DlssSwapService.BuildSwapperPinAsync(archive, entry, Path.Combine(dir, "dll"), ["310.9.16.0"], ct: TestContext.Current.CancellationToken);

            Assert.Null(pin);
            Assert.Contains("is not signed by its vendor", reason);
        }
        finally { DeletionGate.DeleteDirectory(dir, recursive: true); }
    }

    [Fact]
    public async Task ADllWhoseVersionIsNotTheReleasesIsRefused()
    {
        var dir = TempDir();
        try
        {
            var archive = MakeZip(dir, "nvngx_dlss.dll", [1, 2, 3, 4, 5, 6, 7, 8, 9]);
            var entry = new DlssSwapService.SwapperEntry("nvngx_dlss.dll", "310.9.17.0", 9, ArchiveUrl, new FileInfo(archive).Length, Sha(archive));

            var (pin, reason) = await DlssSwapService.BuildSwapperPinAsync(archive, entry, Path.Combine(dir, "dll"), ["310.9.16.0"], (_, _) => true, _ => "310.9.18.0", TestContext.Current.CancellationToken);

            Assert.Null(pin);
            Assert.Contains("isn't the release's", reason);
        }
        finally { DeletionGate.DeleteDirectory(dir, recursive: true); }
    }

    [Fact]
    public async Task ADllNotNewerThanKnownIsRefused()
    {
        var dir = TempDir();
        try
        {
            var archive = MakeZip(dir, "nvngx_dlss.dll", [1, 2, 3, 4, 5, 6, 7, 8, 9]);
            var entry = new DlssSwapService.SwapperEntry("nvngx_dlss.dll", "310.9.17.0", 9, ArchiveUrl, new FileInfo(archive).Length, Sha(archive));

            var (pin, reason) = await DlssSwapService.BuildSwapperPinAsync(archive, entry, Path.Combine(dir, "dll"), ["310.9.17.0"], (_, _) => true, _ => "310.9.17.0", TestContext.Current.CancellationToken);

            Assert.Null(pin);
            Assert.Contains("isn't newer than", reason);
        }
        finally { DeletionGate.DeleteDirectory(dir, recursive: true); }
    }

    [Fact]
    public async Task ADllThatPassesTheChecksBecomesAPinWhoseArchiveHasNoHash()
    {
        var dir = TempDir();
        try
        {
            var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            var archive = MakeZip(dir, "nvngx_dlss.dll", bytes);
            var zipSize = new FileInfo(archive).Length;
            var entry = new DlssSwapService.SwapperEntry("nvngx_dlss.dll", "310.9.17.0", 9, ArchiveUrl, zipSize, Sha(archive));

            var (pin, reason) = await DlssSwapService.BuildSwapperPinAsync(archive, entry, Path.Combine(dir, "dll"), ["310.9.16.0"], (_, _) => true, _ => "310.9.17.0", TestContext.Current.CancellationToken);

            Assert.Null(reason);
            Assert.NotNull(pin);
            Assert.Equal("dlss-310.9.17", pin.Release);
            Assert.Equal("310.9.17.0", pin.Version);
            Assert.Equal("310.9.17", pin.Display);
            Assert.Equal(9, pin.Size);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), pin.Sha256);
            Assert.Equal(ArchiveUrl, pin.Zip!.Url);
            Assert.Equal(zipSize, pin.Zip.Size);
            Assert.Equal(Sha(archive), pin.Zip.Sha256);
        }
        finally { DeletionGate.DeleteDirectory(dir, recursive: true); }
    }

    [Fact]
    public async Task ParseCatalogResponseAsync_Handles404CleanlyWithoutThrowing()
    {
        using var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        var entries = await DlssSwapService.ParseCatalogResponseAsync(response, TestContext.Current.CancellationToken);
        Assert.NotNull(entries);
        Assert.Empty(entries);
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The upscaler security review fixes (2026-11-09): which DLSS Swapper archive is tried and in what order, what a zip must look like before
/// it is opened, the vendor certificate's organization, the cap on the catalog read, the zip hash a pin must carry, the revision in a
/// release's version check, which download hosts and redirects are allowed, and that automatic upscaler updates are off by default.
/// Ways this could fail: a refused archive is tried again within 24 hours; the newest archive is not tried first; a zip64 or >1,000-entry
/// archive is opened; a lookalike common name under another organization is accepted; a catalog past its cap is read; a pinned archive
/// without its hash is trusted; a 4th version part that differs is accepted; a redirect or host off the allowlist is fetched; background
/// downloads run without the user's opt-in. Fixture certificates and zips are made here; no network is used.
/// </summary>
public sealed class DlssSwapperSecurityTests
{
    private const string Url = "https://updates.getexo.app/v1/cache/dlss-swapper-downloads.beeradmoore.com/dlss/";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static DlssSwapService.SwapperEntry Entry(string version, string path, string file = "nvngx_dlss.dll") =>
        new(file, version, 9, Url + path, 4, Hash);

    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-swapper-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static void CleanUp(string path) => DeletionGate.DeleteDirectory(Path.GetDirectoryName(path)!, recursive: true);

    [Fact]
    public void AnArchiveRefusedWithinTheLastDayIsNotTriedAgain()
    {
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var newest = Entry("310.9.18.0", "dlss_v310.9.18.0_B.zip");
        var older = Entry("310.9.17.0", "dlss_v310.9.17.0_A.zip");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nvngx_dlss.dll" };

        var refusedRecently = new Dictionary<string, DateTime> { [newest.ZipUrl] = now.AddHours(-1) };
        Assert.Equal([older], DlssSwapService.SwapperChoices([newest, older], used, ["310.9.16.0"], refusedRecently, now, "nvngx_dlss.dll"));

        var refusedLongAgo = new Dictionary<string, DateTime> { [newest.ZipUrl] = now.AddHours(-25) };
        Assert.Equal([newest, older], DlssSwapService.SwapperChoices([older, newest], used, ["310.9.16.0"], refusedLongAgo, now, "nvngx_dlss.dll"));
    }

    [Fact]
    public void TheNewestArchiveIsTriedFirstAndAnotherFileIsNotMixedIn()
    {
        var now = DateTime.UtcNow;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nvngx_dlss.dll" };
        var entries = new[]
        {
            Entry("310.9.17.0", "a.zip"), Entry("310.9.18.0", "b.zip"), Entry("310.9.16.0", "c.zip"),
            Entry("2.0.2.69", "x.zip", "libxess.dll"),
        };

        var choices = DlssSwapService.SwapperChoices(entries, used, ["310.9.16.0"], new Dictionary<string, DateTime>(), now, "nvngx_dlss.dll");

        Assert.Equal(["310.9.18.0", "310.9.17.0"], choices.Select(c => c.Version).ToArray());
    }

    [Fact]
    public void AZipWithOneThousandEntriesOrFewerIsAnOrdinaryZip()
    {
        var path = TempFile("small.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                for (var i = 0; i < 3; i++) zip.CreateEntry($"f{i}.txt");

            Assert.Null(DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void AZipWithMoreThanOneThousandEntriesIsRefusedBeforeItIsOpened()
    {
        var path = TempFile("many.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                for (var i = 0; i < 1001; i++) zip.CreateEntry($"f{i}.txt");

            Assert.Contains("more than 1000", DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void AZip64ArchiveIsRefused()
    {
        var path = TempFile("zip64.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("f.txt");
            // The end-of-central-directory record's entry count (offset 10 from its start, at the end of the file) set to 0xFFFF: zip64's marker.
            var bytes = File.ReadAllBytes(path);
            var eocd = bytes.Length - 22;
            bytes[eocd + 10] = 0xFF;
            bytes[eocd + 11] = 0xFF;
            File.WriteAllBytes(path, bytes);

            Assert.Equal("it is zip64", DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void AFileWithNoEndOfCentralDirectoryOrTooShortIsRefused()
    {
        var path = TempFile("junk.zip");
        try
        {
            File.WriteAllBytes(path, new byte[64]);
            Assert.Equal("it has no end of central directory", DlssSwapService.ZipShapeRefusal(path));
            File.WriteAllBytes(path, new byte[5]);
            Assert.Equal("it is not a zip", DlssSwapService.ZipShapeRefusal(path));
        }
        finally { CleanUp(path); }
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void ACertificateNamedByTheVendorUnderItsOwnOrganizationIsAccepted()
    {
        using var certificate = SelfSigned("CN=NVIDIA Corporation, O=NVIDIA Corporation, C=US");

        Assert.True(DlssSwapService.OrganizationAccepted("nvngx_dlss.dll", certificate));
    }

    [Fact]
    public void ACertificateWithTheVendorsCommonNameUnderAnotherOrganizationIsRefused()
    {
        using var lookalike = SelfSigned("CN=NVIDIA Corporation, O=Someone Else, C=US");
        using var noOrganization = SelfSigned("CN=NVIDIA Corporation");

        Assert.False(DlssSwapService.OrganizationAccepted("nvngx_dlss.dll", lookalike));
        Assert.False(DlssSwapService.OrganizationAccepted("nvngx_dlss.dll", noOrganization));
        Assert.False(DlssSwapService.OrganizationAccepted("unknown.dll", lookalike));
    }

    [Fact]
    public async Task AnUnsignedDllFailsClosedWhenRevocationIsChecked()
    {
        var path = TempFile("nvngx_dlss.dll");
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

            Assert.False(DlssSwapService.IsTrustedReplacementDll(path, "nvngx_dlss.dll", revocationChecked: true));
            Assert.False(DlssSwapService.IsTrustedReplacementDll(path, "nvngx_dlss.dll"));
            await Task.CompletedTask;
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public async Task TheCatalogIsNotReadPastItsCap()
    {
        using var over = new MemoryStream(new byte[10]);
        using var under = new MemoryStream(new byte[10]);

        Assert.Null(await DlssSwapService.ReadCappedTextAsync(over, 5, CancellationToken.None));
        Assert.Equal(10, (await DlssSwapService.ReadCappedTextAsync(under, 10, CancellationToken.None))!.Length);
    }

    [Fact]
    public void APinnedArchiveWithoutItsHashIsRefusedAndACatalogArchiveIsNot()
    {
        var pinned = DlssSwapService.FindPinned("nvngx_dlss.dll", "dlss-310.9.16")!;
        var withoutHash = pinned with { Zip = pinned.Zip! with { Sha256 = null } };
        var catalog = pinned with { Zip = new DlssSwapService.ZipSource(Url + "x.zip", 4, null), Release = "dlss-310.9.99" };

        Assert.NotNull(DlssSwapService.ZipHashRefusal(withoutHash, withoutHash.Zip!));
        Assert.Null(DlssSwapService.ZipHashRefusal(pinned, pinned.Zip!));
        Assert.Null(DlssSwapService.ZipHashRefusal(catalog, catalog.Zip!));
    }

    [Fact]
    public void AReleaseWithAFourthVersionPartChecksIt()
    {
        // XeSS's release 2.0.2.68 ships a DLL with the same four parts; a different fourth part is not the release's.
        Assert.NotNull(DlssSwapService.VendorReleaseRejection("2.0.2.68", "2.0.2.69", []));
        Assert.Null(DlssSwapService.VendorReleaseRejection("2.0.2.68", "2.0.2.68", []));
        // A three-part release claim carries no fourth part to check.
        Assert.Null(DlssSwapService.VendorReleaseRejection("310.9.17", "310.9.17.0", []));
    }

    [Theory]
    [InlineData("https://updates.getexo.app/v1/cache/dlss-swapper-downloads.beeradmoore.com/dlss/x.zip", true)]
    [InlineData("https://updates.getexo.app/v1/cache/evil.example.com/x.zip", false)]
    [InlineData("https://evil.example/x.zip", false)]
    [InlineData("https://updates.getexo.app/v1/cache/dlss-swapper-downloads.beeradmoore.com/dlss/../../x.zip", false)]
    public void ADownloadIsAllowedOnlyForAPinnedFileAVendorFileOrAnArchiveOnTheSwapperPath(string url, bool allowed) =>
        Assert.Equal(allowed, DlssSwapService.DownloadAllowed(url));

    [Fact]
    public void APinnedVendorFileIsStillAllowed() =>
        Assert.True(DlssSwapService.DownloadAllowed(DlssSwapService.FindPinned("nvngx_dlss.dll", "dlss-310.9.1")!.Url));

    [Fact]
    public void AutomaticUpscalerUpdatesAreOffUntilTheUserTurnsThemOn() =>
        Assert.False(new AppSettings().AutoUpdateUpscalers);
}

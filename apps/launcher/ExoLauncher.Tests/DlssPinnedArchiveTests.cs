using System.Security.Cryptography;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The DLSS Swapper archives Exo mirrors. Each is pinned by its own SHA-256 (not only the DLL inside it), and the DLL is copied
/// out through a stream capped at its pinned size, whatever the zip's header says. Ways this could fail: an archive swapped at
/// the mirror is extracted because only the DLL's size is checked; a zip entry whose header lies about its length is written in
/// full. Fixture bytes are made up, and the pins are the ones in DlssSwapService.Releases.cs.
/// </summary>
public sealed class DlssPinnedArchiveTests
{
    [Theory]
    [InlineData("dlss-310.9.16", "nvngx_dlss.dll", "6c15c2d5e106379175671a95d8c387eae2d58f322a071b9b2d045eee2f271fc3")]
    [InlineData("dlss-310.9.16", "nvngx_dlssd.dll", "c48ac4fcbf5e99acddcfea9c591af82192efd350d2e06248bad863dc7c1c2963")]
    [InlineData("dlss-310.9.16", "nvngx_dlssg.dll", "3a249b94bb2961a17a151ad2634eab0d05defda7df5467de85a75ef411261285")]
    public void EachArchiveIsPinnedBySha256(string release, string fileName, string sha256) =>
        Assert.Equal(sha256, DlssSwapService.FindPinned(fileName, release)!.Zip!.Sha256);

    [Fact]
    public void AnArchiveThatIsNotThePinnedOneIsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-dlss-archive-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            File.WriteAllText(path, "not the pinned archive");

            Assert.False(DlssSwapService.ArchiveMatches(path, "6c15c2d5e106379175671a95d8c387eae2d58f322a071b9b2d045eee2f271fc3"));
            Assert.True(DlssSwapService.ArchiveMatches(path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));
        }
        finally { DeletionGate.DeleteFile(path); }
    }

    [Fact]
    public async Task ACopyStopsAtThePinnedSize()
    {
        using var source = new MemoryStream(new byte[10]);
        using var target = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => DlssSwapService.CopyCappedAsync(source, target, 5, CancellationToken.None));
    }

    [Fact]
    public async Task ACopyOfExactlyThePinnedSizeIsWhole()
    {
        using var source = new MemoryStream(new byte[10]);
        using var target = new MemoryStream();

        await DlssSwapService.CopyCappedAsync(source, target, 10, CancellationToken.None);

        Assert.Equal(10, target.Length);
    }
}

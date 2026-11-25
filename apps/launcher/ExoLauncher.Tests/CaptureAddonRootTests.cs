using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo could run the wrong Exo Capture without saying so:
/// - an old copy beside Exo's data keeps winning over the newer one each update installs (it did, for days);
/// - a development build beside Exo's data, newer than the app's, is passed over;
/// - with only one copy, that copy isn't used.
/// </summary>
public sealed class CaptureAddonRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-capture-root-" + Guid.NewGuid().ToString("N"));
    private string Local => Path.Combine(_root, "local");
    private string Bundled => Path.Combine(_root, "bundled");

    public CaptureAddonRootTests()
    {
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(Bundled);
    }

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, recursive: true); } catch (IOException) { }
    }

    private static void Exe(string folder, DateTime written)
    {
        var path = Path.Combine(folder, "ExoCapture.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        File.SetLastWriteTimeUtc(path, written);
    }

    [Fact]
    public void AnOldCopyBesideExosDataGivesWayToTheNewerOneInTheApp()
    {
        Exe(Local, new DateTime(2026, 10, 25, 5, 4, 13, DateTimeKind.Utc));
        Exe(Bundled, new DateTime(2026, 10, 29, 9, 4, 24, DateTimeKind.Utc));
        Assert.Equal(Bundled, CaptureAddon.Newer(Local, Bundled));
    }

    [Fact]
    public void ANewerDevelopmentBuildBesideExosDataWins()
    {
        Exe(Local, new DateTime(2026, 10, 29, 18, 10, 16, DateTimeKind.Utc));
        Exe(Bundled, new DateTime(2026, 10, 29, 9, 4, 24, DateTimeKind.Utc));
        Assert.Equal(Local, CaptureAddon.Newer(Local, Bundled));
    }

    [Fact]
    public void WithOneCopyThatOneIsUsed()
    {
        Exe(Bundled, DateTime.UtcNow);
        Assert.Equal(Bundled, CaptureAddon.Newer(Local, Bundled));
        DeletionGate.DeleteFile(Path.Combine(Bundled, "ExoCapture.exe"));
        Exe(Local, DateTime.UtcNow);
        Assert.Equal(Local, CaptureAddon.Newer(Local, Bundled));
    }

    [Fact]
    public void PrefersNativeRustCaptureExecutableWhenPresentAndNewer()
    {
        var legacyTime = new DateTime(2026, 10, 29, 9, 4, 24, DateTimeKind.Utc);
        var nativeTime = new DateTime(2026, 11, 24, 15, 0, 0, DateTimeKind.Utc);
        Exe(Bundled, legacyTime);
        var nativePath = Path.Combine(Bundled, "exo-capture.exe");
        File.WriteAllBytes(nativePath, [0x4D, 0x5A]);
        File.SetLastWriteTimeUtc(nativePath, nativeTime);
        Assert.Equal(nativePath, CaptureAddon.ResolveExe(Local, Bundled));
    }
}

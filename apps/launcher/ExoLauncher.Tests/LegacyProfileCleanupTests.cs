using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the one-way cleanup of retired account and profile data can fail:
/// - a retired key's write-in-progress or clear-pending sidecar stays on disk with its secret;
/// - a retired WebView folder (xbox-webview) keeps its cookies after the sign-in is gone;
/// - a file that is not on the list, or a folder the cleanup did not name, is deleted.
/// The folder is a temp folder of this test's own.
/// </summary>
public sealed class LegacyProfileCleanupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-legacy-cleanup-" + Guid.NewGuid().ToString("N"));

    public LegacyProfileCleanupTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch { /* temp */ }
    }

    [Theory]
    [InlineData("steam-web-api.bin")]
    [InlineData("itch-api.bin")]
    [InlineData("steamgriddb.bin")]
    [InlineData("openxbl.bin")]
    [InlineData("igdb-twitch-client-id.bin")]
    [InlineData("igdb-twitch-client-secret.bin")]
    [InlineData("auth.bin")]
    public void EveryRetiredKeyGoesWithItsSidecars(string name)
    {
        foreach (var file in new[] { name, name + ".tmp", name + ".clear-pending" })
            File.WriteAllText(Path.Combine(_folder, file), "secret");

        LegacyProfileDataCleanup.CleanupRoot(_folder);

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void TheRetiredXboxWebViewFolderIsRemoved()
    {
        var webview = Path.Combine(_folder, "xbox-webview");
        Directory.CreateDirectory(Path.Combine(webview, "EBWebView"));
        File.WriteAllText(Path.Combine(webview, "EBWebView", "Cookies"), "cookie");

        LegacyProfileDataCleanup.CleanupRoot(_folder);

        Assert.False(Directory.Exists(webview));
    }

    [Fact]
    public void AFileNotOnTheListIsKept()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_folder, "playtime.json"), "{}");

        LegacyProfileDataCleanup.CleanupRoot(_folder);

        Assert.True(File.Exists(Path.Combine(_folder, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_folder, "playtime.json")));
    }
}

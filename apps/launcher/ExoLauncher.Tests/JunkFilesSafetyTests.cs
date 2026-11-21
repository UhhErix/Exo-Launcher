using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class JunkFilesSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("exo-junk-safe-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_root, recursive: true);

    private string MakeFile(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "data");
        return Path.GetDirectoryName(path)!;
    }

    [Fact]
    public void Browser_cache_folders_include_only_disposable_caches_and_never_cookies_or_logins()
    {
        var local = Path.Combine(_root, "Local");
        var roaming = Path.Combine(_root, "Roaming");

        var expected = new[]
        {
            MakeFile("Local", "BraveSoftware", "Brave-Browser", "User Data", "Default", "Cache", "Cache_Data", "f_0001"),
            MakeFile("Local", "BraveSoftware", "Brave-Browser", "User Data", "Profile 1", "Code Cache", "js", "a.js"),
            MakeFile("Local", "Google", "Chrome", "User Data", "Default", "GPUCache", "data_0"),
            MakeFile("Local", "imput", "Helium", "User Data", "Default", "DawnGraphiteCache", "data_0"),
            MakeFile("Local", "Microsoft", "Edge", "User Data", "Default", "Code Cache", "wasm", "a.wasm"),
            MakeFile("Local", "Vivaldi", "User Data", "ShaderCache", "data_0"),
            MakeFile("Roaming", "Opera Software", "Opera GX Stable", "GPUCache", "data_0"),
        };

        var sensitive = new[]
        {
            MakeFile("Local", "BraveSoftware", "Brave-Browser", "User Data", "Default", "Cookies"),
            MakeFile("Local", "BraveSoftware", "Brave-Browser", "User Data", "Default", "Login Data"),
            MakeFile("Local", "Google", "Chrome", "User Data", "Default", "Local Storage", "leveldb", "0001.ldb"),
            MakeFile("Local", "Google", "Chrome", "User Data", "Default", "Preferences"),
            MakeFile("Local", "imput", "Helium", "User Data", "Default", "Network", "Cookies"),
            MakeFile("Roaming", "Opera Software", "Opera GX Stable", "Local Extension Settings", "ext.ldb"),
        };

        var found = JunkFiles.BrowserCacheFolders(local, roaming);
        foreach (var path in expected) Assert.Contains(path, found);
        foreach (var path in sensitive) Assert.DoesNotContain(path, found);
        Assert.DoesNotContain(found, f => f.Contains("Cookies", StringComparison.OrdinalIgnoreCase)
            || f.Contains("Login Data", StringComparison.OrdinalIgnoreCase)
            || f.Contains("Local Storage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Launcher_cache_folders_exclude_games_userdata_and_discord_storage()
    {
        var local = Path.Combine(_root, "Local");
        var roaming = Path.Combine(_root, "Roaming");
        var pf86 = Path.Combine(_root, "PF86");
        var common = Path.Combine(_root, "Common");

        var expected = new[]
        {
            MakeFile("Local", "Steam", "htmlcache", "Cache", "Cache_Data", "f_001"),
            MakeFile("Local", "Steam", "htmlcache", "Default", "Cache", "Cache_Data", "f_002"),
            MakeFile("Local", "Steam", "htmlcache", "GrShaderCache", "data_0"),
            MakeFile("PF86", "Steam", "appcache", "httpcache", "f_002"),
            MakeFile("Local", "EpicGamesLauncher", "Saved", "webcache_4430", "Cache", "Cache_Data", "data_0"),
            MakeFile("Local", "EpicGamesLauncher", "Saved", "Logs", "Epic.log"),
            MakeFile("Local", "Battle.net", "BrowserCache", "GPUCache", "data_0"),
            MakeFile("Common", "Blizzard Entertainment", "Battle.net", "Cache", "c.bin"),
            MakeFile("Local", "Ubisoft Game Launcher", "cache", "http2", "h.bin"),
            MakeFile("Roaming", "discord", "Cache", "Cache_Data", "f_003"),
        };

        var sensitive = new[]
        {
            MakeFile("Local", "Steam", "htmlcache", "Local State"),
            MakeFile("Local", "Steam", "htmlcache", "Default", "Login Data"),
            MakeFile("Local", "Steam", "htmlcache", "Default", "Network", "Cookies"),
            MakeFile("Local", "EpicGamesLauncher", "Saved", "webcache_4430", "Cookies"),
            MakeFile("Local", "Battle.net", "BrowserCache", "Cookies"),
            MakeFile("PF86", "Steam", "steamapps", "appmanifest_730.acf"),
            MakeFile("PF86", "Steam", "userdata", "123", "config.vdf"),
            MakeFile("Roaming", "discord", "Local Storage", "leveldb", "0001.ldb"),
            MakeFile("Local", "ExoLauncher", "Cache", "Cache_Data", "keep.bin"),
        };

        var found = JunkFiles.LauncherCacheFolders(local, roaming, pf86, common);
        foreach (var path in expected) Assert.Contains(path, found);
        foreach (var path in sensitive) Assert.DoesNotContain(path, found);
        Assert.DoesNotContain(found, f => f.Contains("ExoLauncher", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, f => f.EndsWith(@"Steam\htmlcache", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, f => f.EndsWith("webcache_4430", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, f => f.EndsWith("BrowserCache", StringComparison.OrdinalIgnoreCase));
    }
}

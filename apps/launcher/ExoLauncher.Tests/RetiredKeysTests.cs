namespace ExoLauncher.Tests;

/// <summary>
/// Optional keys are retired. Their DPAPI blobs are deleted on upgrade by LegacyProfileDataCleanup and never read:
/// - only that cleanup list names a retired key file, so no reader can come back for one;
/// - the cleanup does delete each of them, with its clear marker.
/// Ways this fails: a reader is added back; a file is dropped from the allow-list.
/// </summary>
public sealed class RetiredKeysTests
{
    private static readonly string[] RetiredFiles =
    [
        "steam-web-api.bin",
        "itch-api.bin",
        "steamgriddb.bin",
        "openxbl.bin",
        "igdb-twitch-client-id.bin",
        "igdb-twitch-client-secret.bin",
    ];

    [Fact]
    public void NothingButTheCleanupNamesARetiredKeyFile()
    {
        var root = Path.Combine(Repo.Root, "ExoLauncher");
        var named = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(path => path.Replace('\\', '/'))
            .Where(path => !path.Contains("/bin/", StringComparison.Ordinal) && !path.Contains("/obj/", StringComparison.Ordinal))
            .Where(path => !path.EndsWith("/LegacyProfileDataCleanup.cs", StringComparison.Ordinal))
            // The registry of Exo's files names each retired one as retired (PlayerData.NotPlayerData); it reads none.
            .Where(path => !path.EndsWith("/Services/PlayerData.cs", StringComparison.Ordinal))
            .Where(path => { var text = File.ReadAllText(path); return RetiredFiles.Any(file => text.Contains(file, StringComparison.Ordinal)); })
            .ToList();
        Assert.Empty(named);
    }

    [Fact]
    public void TheCleanupDeletesEachRetiredFile()
    {
        var text = File.ReadAllText(Path.Combine(Repo.Root, "ExoLauncher", "Services", "LegacyProfileDataCleanup.cs"));
        foreach (var file in RetiredFiles)
            Assert.Contains($"\"{file}\"", text);
    }
}

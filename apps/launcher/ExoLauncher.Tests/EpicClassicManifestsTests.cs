using ExoLauncher.Adapters;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public sealed class EpicClassicManifestsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-epic-classic-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_dir, recursive: true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReadsClassicItemManifestsAndFiltersIncompleteDlcAndEngineEntries()
    {
        var gameDir = Path.Combine(_dir, "Games", "RocketLeague");
        Write(Path.Combine("Games", "RocketLeague", "Binaries", "Win64", "RocketLeague.exe"), "");

        var manifestsDir = Path.Combine(_dir, "Manifests");
        Write(Path.Combine("Manifests", "rocket.item"), $$"""
            {
              "AppName": "Sugar",
              "DisplayName": "Rocket League",
              "InstallLocation": "{{gameDir.Replace("\\", "\\\\")}}",
              "LaunchExecutable": "Binaries/Win64/RocketLeague.exe",
              "AppVersionString": "60.1",
              "CatalogNamespace": "9773aa1aa54f4f7b80e44bef04986cea",
              "CatalogItemId": "530d",
              "MainGameAppName": "Sugar",
              "AppCategories": ["games", "public"],
              "bIsApplication": true,
              "bIsIncompleteInstall": false
            }
            """);
        Write(Path.Combine("Manifests", "incomplete.item"), $$"""
            {
              "AppName": "PendingGame",
              "DisplayName": "Pending",
              "InstallLocation": "{{gameDir.Replace("\\", "\\\\")}}",
              "bIsApplication": true,
              "bIsIncompleteInstall": true
            }
            """);
        Write(Path.Combine("Manifests", "dlc.item"), $$"""
            {
              "AppName": "SugarDlc",
              "DisplayName": "Rocket League DLC",
              "InstallLocation": "{{gameDir.Replace("\\", "\\\\")}}",
              "MainGameAppName": "Sugar",
              "bIsApplication": true,
              "bIsIncompleteInstall": false
            }
            """);
        Write(Path.Combine("Manifests", "ue.item"), $$"""
            {
              "AppName": "UE_5.4",
              "DisplayName": "Unreal Engine 5.4",
              "InstallLocation": "{{gameDir.Replace("\\", "\\\\")}}",
              "CatalogNamespace": "ue",
              "AppCategories": ["engines"],
              "bIsApplication": true,
              "bIsIncompleteInstall": false
            }
            """);

        var rows = EpicAdapter.ReadClassicLauncher(
            hasLegendary: false,
            ownedForTitles: [],
            latest: EpicAdapter.LatestBuilds.Parse("{}", DateTime.UtcNow),
            manifestsDir: manifestsDir,
            launcherInstalledPath: Path.Combine(_dir, "missing.dat")).ToList();

        var only = Assert.Single(rows);
        Assert.Equal("epic:Sugar", only.Id);
        Assert.Equal("Rocket League", only.Title);
        Assert.Equal(gameDir, only.Path);
        Assert.True(only.Installed);
    }

    [Fact]
    public void FallsBackToLauncherInstalledDatWhenManifestItemIsMissing()
    {
        var gameDir = Path.Combine(_dir, "Games", "AlanWake2");
        Directory.CreateDirectory(gameDir);

        var datPath = Write("LauncherInstalled.dat", $$"""
            {
              "InstallationList": [
                {
                  "InstallLocation": "{{gameDir.Replace("\\", "\\\\")}}",
                  "NamespaceId": "c4763f236d08423eb47b4c3008779c84",
                  "ItemId": "abc",
                  "ArtifactId": "AlanWake2App",
                  "AppVersion": "1.0.16",
                  "AppName": "AlanWake2App"
                },
                {
                  "InstallLocation": "{{gameDir.Replace("\\", "\\\\")}}",
                  "NamespaceId": "ue",
                  "ItemId": "ue5",
                  "ArtifactId": "UE_5.5",
                  "AppVersion": "5.5",
                  "AppName": "UE_5.5"
                }
              ]
            }
            """);

        var rows = EpicAdapter.ReadClassicLauncher(
            hasLegendary: false,
            ownedForTitles: [],
            latest: EpicAdapter.LatestBuilds.Parse("{}", DateTime.UtcNow),
            manifestsDir: Path.Combine(_dir, "empty-manifests"),
            launcherInstalledPath: datPath).ToList();

        var only = Assert.Single(rows);
        Assert.Equal("epic:AlanWake2App", only.Id);
        Assert.Equal("AlanWake2", only.Title);
    }

    [Fact]
    public void EmptyEpicLayoutIsRecognizedAndDoesNotWarn()
    {
        var emptyManifests = Path.Combine(_dir, "EmptyManifests");
        Directory.CreateDirectory(emptyManifests);
        var emptyDat = Write("EmptyLauncherInstalled.dat", """{"InstallationList":[]}""");
        var v2Dir = Path.Combine(_dir, "v2");
        Write(Path.Combine("v2", "installations.jsonl"), """{"version":1}""");

        Assert.True(EpicClassicManifests.IsLayoutRecognized(v2Dir, emptyManifests, emptyDat));
        Assert.True(EpicClassicManifests.IsLayoutRecognized(null, null, null));
        Assert.False(LibraryService.IsLayoutUnrecognized(clientPresent: true, scanSucceeded: true, games: 0, layoutRecognized: true));
    }

    [Fact]
    public void UnrecognizedEpicSchemaIsFlagged()
    {
        var brokenManifests = Path.Combine(_dir, "BrokenManifests");
        Write(Path.Combine("BrokenManifests", "unknown.item"), """{"differentSchema":true}""");

        Assert.False(EpicClassicManifests.IsLayoutRecognized(null, brokenManifests, null));
        Assert.True(LibraryService.IsLayoutUnrecognized(clientPresent: true, scanSucceeded: true, games: 0, layoutRecognized: false));
    }
}

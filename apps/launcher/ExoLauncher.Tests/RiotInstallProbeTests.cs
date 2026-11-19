using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Riot;

namespace ExoLauncher.Tests;

/// <summary>
/// Riot installs found by Riot's own product_settings.yaml or by product markers in a root. Fixtures follow the
/// shapes on a real install (product_install_full_path with forward slashes, "live" subfolders, a bootstrap stub).
/// Ways this fails: a moved library on a second drive is missed; a stub folder counts as installed; VALORANT's
/// "live" folder is reported as the product; Riot Client is not found when Exo only has the registry hint.
/// </summary>
public sealed class RiotInstallProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-riot-" + Guid.NewGuid().ToString("N"));

    public RiotInstallProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, recursive: true); } catch { }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void ParsesTheForwardSlashInstallPathOfAValorantSettingsFile()
    {
        const string yaml = """
            auto_patching_enabled_by_player: false
            dependencies:
                DirectX:
                    phase: "Succeeded"
                vanguard: true
            patching_policy: "manual"
            product_install_full_path: "C:/Riot Games/VALORANT/live"
            """;

        var settings = RiotProductMetadata.ParseSettings(yaml);

        Assert.Equal("C:\\Riot Games\\VALORANT\\live", settings.InstallFullPath);
        Assert.False(settings.ShouldRepair);
    }

    [Fact]
    public void ValorantOnAnotherDriveIsFoundThroughItsLiveFolderAndReportedAsTheProductFolder()
    {
        Touch("VALORANT", "live", "VALORANT.exe");
        var settings = new RiotProductMetadata.ProductSettings(
            Path.Combine(_root, "VALORANT", "live"), null, false);

        var found = RiotInstallProbe.FromSettings("valorant", settings);

        Assert.Equal(Path.Combine(_root, "VALORANT"), found);
    }

    [Fact]
    public void ALeftoverBootstrapFolderIsNotAnInstall()
    {
        // Riot leaves a folder with only patch stubs while it downloads: no product marker, so not installed.
        Touch("League of Legends", "stub.dll");

        Assert.Null(RiotInstallProbe.FindInstalledProduct("league_of_legends", [_root]));
    }

    [Fact]
    public void LeagueIsFoundInASecondRootWhenTheFirstHasNoIt()
    {
        var first = Path.Combine(_root, "empty-drive");
        var second = Path.Combine(_root, "games-drive");
        Directory.CreateDirectory(first);
        Touch("games-drive", "League of Legends", "LeagueClient.exe");

        var found = RiotInstallProbe.FindInstalledProduct("league_of_legends", [first, second]);

        Assert.Equal(Path.Combine(second, "League of Legends"), found);
    }

    [Fact]
    public void TheTwoTwoXkoFolderNameIsRecognisedOnItsOwnMarker()
    {
        Touch("2XKO", "2XKO.exe");

        Assert.Equal(Path.Combine(_root, "2XKO"), RiotInstallProbe.FindInstalledProduct("lion", [_root]));
    }

    [Fact]
    public void RiotClientIsFoundUnderARoot()
    {
        var exe = Touch("Riot Games", "Riot Client", "RiotClientServices.exe");

        Assert.Equal(exe, RiotInstallProbe.FindRiotClientServices([Path.Combine(_root, "Riot Games")]));
    }

    [Fact]
    public void AnUninstalledProductIsNotReported()
    {
        Assert.Null(RiotInstallProbe.FindInstalledProduct("bacon", [_root]));
    }
}

using ExoLauncher.Models;
using static ExoLauncher.Services.DependencyService;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Easy Anti-Cheat as a dependency could be silently wrong: a game's setup isn't found where games keep it (the root,
/// or beside the executable as Binaries\Win64\EasyAntiCheat), the EOS and older setups are mixed up (each installs its own
/// service), a product id that isn't one reaches the setup's command line, or the page gets a repair Exo never found.
/// </summary>
public sealed class AntiCheatDependencyTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("exo-anticheat-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_game, recursive: true);

    private const string Product = "e6bcca5b37d0457ca881aec508205542";

    private string Ship(string relative, string setup, string settings)
    {
        var folder = Path.Combine(_game, relative);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, setup), [0x4d, 0x5a]);
        File.WriteAllText(Path.Combine(folder, "Settings.json"), settings);
        return folder;
    }

    private static string Settings(string product) => "{\n\t\"title\" : \"Rocket League\",\n\t\"productid\" : \"" + product + "\",\n}";

    [Fact]
    public void TheEosSetupBesideTheExecutableIsFoundWithItsProduct()
    {
        var folder = Ship(Path.Combine("Binaries", "Win64", "EasyAntiCheat"), "EasyAntiCheat_EOS_Setup.exe", Settings(Product));
        var found = AntiCheatOf(_game);
        Assert.NotNull(found);
        Assert.Equal("EasyAntiCheat_EOS", found.Service);
        Assert.Equal(Path.Combine(folder, "EasyAntiCheat_EOS_Setup.exe"), found.Setup);
        Assert.Equal(Product, found.ProductId);
    }

    [Fact]
    public void TheOlderSetupAtTheRootInstallsTheOlderService()
    {
        Ship("EasyAntiCheat", "EasyAntiCheat_Setup.exe", Settings(Product));
        Assert.Equal("EasyAntiCheat", AntiCheatOf(_game)?.Service);
    }

    [Fact]
    public void NoSetupOrNoRealProductIsNothingToRepair()
    {
        Assert.Null(AntiCheatOf(_game));
        Assert.Null(AntiCheatOf(null));
        Ship("EasyAntiCheat", "EasyAntiCheat_EOS_Setup.exe", Settings("e6bcca5b & calc.exe"));
        Assert.Null(AntiCheatOf(_game));
        File.WriteAllText(Path.Combine(_game, "EasyAntiCheat", "Settings.json"), "not json");
        Assert.Null(AntiCheatOf(_game));
        DeletionGate.DeleteFile(Path.Combine(_game, "EasyAntiCheat", "Settings.json"));
        Assert.Null(AntiCheatOf(_game));
    }

    [Fact]
    public void ASetupDeeperThanAGameKeepsOneIsNotFound()
    {
        Ship(Path.Combine("a", "b", "c", "d", "EasyAntiCheat"), "EasyAntiCheat_EOS_Setup.exe", Settings(Product));
        Assert.Null(AntiCheatOf(_game));
    }

    [Fact]
    public async Task ARepairExoNeverFoundIsRefusedWithoutRunningAnything()
    {
        var result = await RepairAntiCheatAsync(AntiCheatPrefix + "epic:not-scanned", CancellationToken.None);
        Assert.Equal("failed", result.Outcome);
    }

    [Fact]
    public void AGameNotInstalledNeedsNothing()
    {
        Ship(Path.Combine("Binaries", "Win64", "EasyAntiCheat"), "EasyAntiCheat_EOS_Setup.exe", Settings(Product));
        Assert.Null(MissingAntiCheat(new GameEntry { Id = "epic:Sugar", Title = "Rocket League", Store = StoreKind.Epic, Installed = false, Path = _game }));
    }
}

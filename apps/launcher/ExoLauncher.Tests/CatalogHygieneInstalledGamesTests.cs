using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// A game the player has installed is never hidden from the Library by an ordinary word in its name. Ways this could fail:
/// - "Blueprint Tycoon", "Shader Wars" or "Editor Simulator" from GOG, Epic or a local folder vanish because the Unreal
///   Marketplace list holds the words blueprint, shader and editor;
/// - the exemption is too wide and Unreal Engine itself, or an uninstalled marketplace asset, appears among the games.
/// </summary>
public sealed class CatalogHygieneInstalledGamesTests
{
    [Theory]
    [InlineData(StoreKind.Gog)]
    [InlineData(StoreKind.Epic)]
    [InlineData(StoreKind.Local)]
    [InlineData(StoreKind.Steam)]
    public void AnInstalledGameWithAMarkerWordInItsNameStaysInTheLibrary(StoreKind store)
    {
        foreach (var title in new[] { "Blueprint Tycoon", "Shader Wars", "Editor Simulator", "Template Wars" })
        {
            var game = new GameEntry { Id = store + ":" + title, Title = title, Store = store, Installed = true, Owned = true };
            Assert.True(CatalogHygiene.KeepLibraryEntry(game, showTools: false), store + " " + title);
        }
    }

    [Fact]
    public void AnEpicRowThatIsInstalledIsKeptAndAnUninstalledMarketplaceAssetIsNot()
    {
        Assert.True(CatalogHygiene.KeepEpicRow("Blueprint Tycoon", ["games"], installed: true));
        Assert.False(CatalogHygiene.KeepEpicRow("Blueprint Starter Kit", ["games"], installed: false));
    }

    [Fact]
    public void TheEnginesOwnNamesAreHiddenEvenWhenInstalled()
    {
        var engine = new GameEntry { Id = "epic:ue", Title = "Unreal Engine 5.4", Store = StoreKind.Epic, Installed = true, Owned = true };
        Assert.False(CatalogHygiene.KeepLibraryEntry(engine, showTools: false));
        Assert.False(CatalogHygiene.KeepEpicRow("Unreal Engine 5.4", ["engines"], installed: true));
    }

    [Fact]
    public void AnUninstalledOwnedMarketplaceAssetStaysOutOfTheLibrary()
    {
        var asset = new GameEntry { Id = "epic:asset", Title = "Blueprint Starter Kit", Store = StoreKind.Epic, Installed = false, Owned = true };
        Assert.False(CatalogHygiene.KeepLibraryEntry(asset, showTools: false));
    }
}

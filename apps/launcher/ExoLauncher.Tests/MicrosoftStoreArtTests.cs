using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Microsoft Store's wide art can fail (Roblox's Continue banner):
/// - a screenshot, the poster or the square box art is taken as the banner;
/// - the titled key art wins over the clean one;
/// - a protocol-relative "//store-images..." address is dropped instead of made https;
/// - a game the Microsoft Store has no art for is looked for there.
/// </summary>
public sealed class MicrosoftStoreArtTests
{
    [Theory]
    [InlineData(StoreKind.Minecraft, true)]
    [InlineData(StoreKind.Roblox, true)]
    [InlineData(StoreKind.Epic, false)]
    public void Only_the_games_it_has_art_for_ask_the_Microsoft_Store(StoreKind store, bool asks) =>
        Assert.Equal(asks, MicrosoftStoreArt.ProductIdFor(new GameEntry { Id = "title:x", Title = "x", Store = store }) is not null);

    private const string Catalog = """
        {"Product":{"LocalizedProperties":[{"Images":[
          {"ImagePurpose":"Screenshot","Width":1920,"Height":1080,"Uri":"//store-images.s-microsoft.com/shot"},
          {"ImagePurpose":"BoxArt","Width":1080,"Height":1080,"Uri":"//store-images.s-microsoft.com/box"},
          {"ImagePurpose":"TitledHeroArt","Width":1920,"Height":1080,"Uri":"//store-images.s-microsoft.com/titled"},
          {"ImagePurpose":"SuperHeroArt","Width":1920,"Height":1080,"Uri":"//store-images.s-microsoft.com/hero"},
          {"ImagePurpose":"Poster","Width":1440,"Height":2160,"Uri":"//store-images.s-microsoft.com/poster"}
        ]}]}}
        """;

    [Fact]
    public void WideArtIsTheKeyArtCleanFirst() =>
        Assert.Equal(["https://store-images.s-microsoft.com/hero", "https://store-images.s-microsoft.com/titled"], MicrosoftStoreArt.WideUrlsFromCatalog(Catalog));

    [Fact]
    public void PortraitsStayPortraits() =>
        Assert.Equal(["https://store-images.s-microsoft.com/poster", "https://store-images.s-microsoft.com/box"], MicrosoftStoreArt.PortraitUrlsFromCatalog(Catalog));
}

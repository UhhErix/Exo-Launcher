using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Microsoft Store's wide art can fail (Roblox's Continue banner):
/// - a screenshot, the poster or the square box art is taken as the banner;
/// - the titled key art wins over the clean one;
/// - a protocol-relative "//store-images..." address is dropped instead of made https;
/// - a friend's Minecraft or Roblox, known only by name, is looked for on Steam and Epic, which don't have it;
/// - a modpack's name with Minecraft in it misses because it isn't Minecraft's exact name;
/// - a game of its own that has the word in its name (Minecraft Dungeons), or only looks like it, gets Minecraft's banner.
/// </summary>
public sealed class MicrosoftStoreArtTests
{
    [Theory]
    [InlineData("Minecraft", StoreKind.Minecraft)]
    [InlineData("  MINECRAFT ", StoreKind.Minecraft)]
    [InlineData("Minecraft: Java Edition", StoreKind.Minecraft)]
    [InlineData("Roblox", StoreKind.Roblox)]
    [InlineData("Medieval Minecraft [FABRIC] 1.19.2", StoreKind.Minecraft)]
    [InlineData("MINECRAFT with RTX", StoreKind.Minecraft)]
    [InlineData("Medieval MC [FABRIC] 1.19.2", StoreKind.Epic)]
    [InlineData("Minecrafter's Tale", StoreKind.Epic)]
    [InlineData("Minecraft Dungeons", StoreKind.Epic)]
    [InlineData("Minecraft Legends", StoreKind.Epic)]
    [InlineData("Minecraft: Story Mode", StoreKind.Epic)]
    [InlineData("Fortnite", StoreKind.Epic)]
    public void A_name_gets_the_store_that_has_its_art(string title, StoreKind store)
    {
        Assert.Equal(store, CoverArtService.StoreOfTitle(title));
        Assert.Equal(store != StoreKind.Epic, MicrosoftStoreArt.ProductIdFor(new GameEntry { Id = "title:x", Title = title, Store = store }) is not null);
    }

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

using System.Text.Json;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Epic art from the 0.113 launcher's catalog cache (cache\prod\catalog-cache.json). Ways this fails: the cache is
/// ignored and an Epic game gets no cover; the landscape box art (DieselGameBox, 2560x1440) is not taken as the wide
/// banner; a catalog record with no key images counts as art; the first lookup misses a title with a replacement char.
/// </summary>
public sealed class EpicCatCacheArtTests
{
    // Shape from the 0.113 launcher's own cache on a real PC (ids, URLs and personal data removed).
    private const string Catalog = """
        {
          "items": {
            "530145df28a24424923f5828cc9031a1": {
              "item": {
                "id": "530145df28a24424923f5828cc9031a1",
                "title": "Rocket League\uFFFD",
                "entitlementName": "Sugar",
                "keyImages": [
                  { "type": "DieselGameBoxTall", "url": "https://cdn.example/rl/1200x1600.jpg" },
                  { "type": "DieselGameBox", "url": "https://cdn.example/rl/2560x1440.jpg" }
                ]
              },
              "expiresAt": 1, "cachedAt": 1
            },
            "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6": {
              "item": { "title": "Nothing To Show", "keyImages": [] },
              "expiresAt": 1, "cachedAt": 1
            }
          },
          "schemaVersion": 1,
          "locale": "en-US"
        }
        """;

    [Fact]
    public void RocketLeagueGetsItsPortraitAndItsLandscapeBoxArtFromTheCatalog()
    {
        var tall = new Dictionary<string, string>(StringComparer.Ordinal);
        var wide = new Dictionary<string, string>(StringComparer.Ordinal);

        using var doc = JsonDocument.Parse(Catalog);
        EpicCatCacheArt.IndexCatalog(doc.RootElement, tall, wide);

        Assert.Equal("https://cdn.example/rl/1200x1600.jpg?h=900&w=600&resize=1&quality=high", tall["rocket league"]);
        Assert.Equal("https://cdn.example/rl/2560x1440.jpg?h=1080&w=1920&resize=1&quality=high", wide["rocket league"]);
    }

    [Fact]
    public void ARecordWithNoKeyImagesIsNotArt()
    {
        var tall = new Dictionary<string, string>(StringComparer.Ordinal);
        var wide = new Dictionary<string, string>(StringComparer.Ordinal);

        using var doc = JsonDocument.Parse(Catalog);
        EpicCatCacheArt.IndexCatalog(doc.RootElement, tall, wide);

        Assert.False(tall.ContainsKey("nothing to show"));
        Assert.False(wide.ContainsKey("nothing to show"));
    }
}

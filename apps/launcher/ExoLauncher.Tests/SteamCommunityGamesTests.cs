using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the public Steam profile games list can mislead the library: a private profile read as "no games" (every owned
/// title would drop out), hours read in the wrong unit or with the thousands comma lost, a hostile DTD, an app id that
/// is not one. Shape from Steam's <c>games?xml=1</c> answer; the file is written in one place (Parse).
/// </summary>
public sealed class SteamCommunityGamesTests
{
    private const string Public = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gamesList>
          <steamID64>76561197960265728</steamID64>
          <games>
            <game>
              <appID>10</appID>
              <name><![CDATA[Counter-Strike]]></name>
              <hoursOnRecord>1,234.5</hoursOnRecord>
            </game>
            <game>
              <appID>20</appID>
              <name>Never Played</name>
            </game>
            <game>
              <appID>abc</appID>
              <name>Not an app id</name>
              <hoursOnRecord>3</hoursOnRecord>
            </game>
          </games>
        </gamesList>
        """;

    [Fact]
    public void PublicListGivesOwnedAppsNamesAndMinutes()
    {
        var result = SteamCommunityGames.Parse(Public);

        Assert.True(result.Authoritative);
        Assert.Equal(["10", "20"], result.AppIds.Order(StringComparer.Ordinal));
        Assert.Equal(74_070, result.PlaytimeMinutes["10"]);
        Assert.False(result.PlaytimeMinutes.ContainsKey("20"));
        Assert.Equal("Counter-Strike", result.Names["10"]);
    }

    [Fact]
    public void PrivateProfileIsNotAnEmptyLibrary()
    {
        const string Private = """
            <gamesList><steamID64>76561197960265728</steamID64><error>This profile is private.</error></gamesList>
            """;

        Assert.False(SteamCommunityGames.Parse(Private).Authoritative);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Steam is down</html>")]
    [InlineData("not xml at all")]
    public void AnythingElseIsUnavailable(string body) =>
        Assert.False(SteamCommunityGames.Parse(body).Authoritative);

    [Fact]
    public void DtdIsRefusedSoNoEntityIsExpanded()
    {
        const string Hostile = """
            <?xml version="1.0"?>
            <!DOCTYPE gamesList [ <!ENTITY x "boom"> ]>
            <gamesList><games><game><appID>10</appID><name>&x;</name></game></games></gamesList>
            """;

        Assert.False(SteamCommunityGames.Parse(Hostile).Authoritative);
    }

    [Fact]
    public void PublicProfileWithNoGamesIsAnAuthoritativeEmptyList()
    {
        var result = SteamCommunityGames.Parse("<gamesList><steamID64>1</steamID64><games/></gamesList>");

        Assert.True(result.Authoritative);
        Assert.Empty(result.AppIds);
    }
}

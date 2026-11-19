using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a session's mode can come out wrong:
/// - a League lobby or menu (no queue yet) is named as a queue, or a queue no name is known for is named as one;
/// - another player's Valorant presence is read as this player's, so the mode is someone else's queue;
/// - a Valorant presence whose private field is not base64 JSON, or has no queue, takes the session's mode down with it;
/// - tracker.gg's playlist names and the clients' queue names do not meet, so a seed's per-mode hours never add to a session's.
/// The client answers are fixtures in the shape each client sends (the League gameflow session, the Riot Client's
/// session and presences, and the base64 private presence). They are not recorded from a live client: no game runs here.
/// </summary>
public sealed class RiotModesTests
{
    // League client, in a ranked solo queue: /lol-gameflow/v1/session.
    private const string LeagueRankedSolo = """
        {"gameData":{"queue":{"id":420,"type":"RANKED_SOLO_5x5","gameMode":"CLASSIC","isRanked":true}},"phase":"Matchmaking"}
        """;

    // League client, in the lobby before any queue: no gameData queue at all.
    private const string LeagueLobby = """{"phase":"Lobby","gameData":{}}""";

    private const string LeagueUnknownQueue = """{"gameData":{"queue":{"id":9999,"type":"CUSTOM"}}}""";

    private const string RiotSession = """{"puuid":"self-puuid-0001","game_name":"Fixture","tag_line":"EUW"}""";

    // The Riot Client's presences: this player's Valorant presence (queued unrated), another player's, and this player's
    // League presence. Only the first is this player's Valorant mode.
    private static readonly string ValorantPresences =
        "{\"presences\":[" +
        "{\"puuid\":\"other-puuid-0002\",\"product\":\"valorant\",\"private\":\"" + Base64("{\"queueId\":\"competitive\"}") + "\"}," +
        "{\"puuid\":\"self-puuid-0001\",\"product\":\"valorant\",\"private\":\"" + Base64("{\"queueId\":\"unrated\",\"sessionLoopState\":\"MENUS\"}") + "\"}," +
        "{\"puuid\":\"self-puuid-0001\",\"product\":\"league_of_legends\",\"private\":\"" + Base64("{\"queueId\":\"ranked\"}") + "\"}" +
        "]}";

    private static string Base64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ALeagueQueueIsItsModeAndALobbyIsUnknown()
    {
        Assert.Equal("ranked solo", RiotModes.ParseLeagueSession(LeagueRankedSolo));
        Assert.Equal("unknown", RiotModes.ParseLeagueSession(LeagueLobby));
        Assert.Equal("unknown", RiotModes.ParseLeagueSession(null));
        Assert.Equal("unknown", RiotModes.ParseLeagueSession("not json"));
    }

    [Fact]
    public void ALeagueQueueNoNameIsKnownForIsOther()
    {
        Assert.Equal("other", RiotModes.ParseLeagueSession(LeagueUnknownQueue));
    }

    [Fact]
    public void AValorantPresenceIsReadForThisPlayerOnly()
    {
        Assert.Equal("unrated", RiotModes.ParseValorantPresence(RiotSession, ValorantPresences));
    }

    [Fact]
    public void AValorantPresenceWithoutAQueueOrAPrivateFieldIsUnknown()
    {
        var noQueue = "{\"presences\":[{\"puuid\":\"self-puuid-0001\",\"product\":\"valorant\",\"private\":\"" + Base64("{\"sessionLoopState\":\"MENUS\"}") + "\"}]}";
        var noPrivate = "{\"presences\":[{\"puuid\":\"self-puuid-0001\",\"product\":\"valorant\"}]}";
        var notBase64 = "{\"presences\":[{\"puuid\":\"self-puuid-0001\",\"product\":\"valorant\",\"private\":\"%%%\"}]}";
        var onlyLeague = "{\"presences\":[{\"puuid\":\"self-puuid-0001\",\"product\":\"league_of_legends\",\"private\":\"" + Base64("{\"queueId\":\"x\"}") + "\"}]}";

        Assert.Equal("unknown", RiotModes.ParseValorantPresence(RiotSession, noQueue));
        Assert.Equal("unknown", RiotModes.ParseValorantPresence(RiotSession, noPrivate));
        Assert.Equal("unknown", RiotModes.ParseValorantPresence(RiotSession, notBase64));
        Assert.Equal("unknown", RiotModes.ParseValorantPresence(RiotSession, onlyLeague));
        Assert.Equal("unknown", RiotModes.ParseValorantPresence(null, ValorantPresences));
        Assert.Equal("unknown", RiotModes.ParseValorantPresence(RiotSession, "{}"));
    }

    [Theory]
    [InlineData("competitive", "competitive")]
    [InlineData("spikerush", "spike rush")]
    [InlineData("ggteam", "team deathmatch")]
    [InlineData("hurm", "other")]
    [InlineData("", "unknown")]
    public void AValorantQueueIdNamesItsMode(string queueId, string mode) =>
        Assert.Equal(mode, RiotModes.FromValorantQueue(queueId));

    [Theory]
    [InlineData("riot:league_of_legends", "Ranked Solo/Duo", "ranked solo")]
    [InlineData("riot:league_of_legends", "Normal Draft", "normal draft")]
    [InlineData("riot:league_of_legends", "ARAM", "aram")]
    [InlineData("riot:league_of_legends", "Arena", "arena")]
    [InlineData("riot:league_of_legends", "Some New Queue", "other")]
    [InlineData("riot:valorant", "Competitive", "competitive")]
    [InlineData("riot:valorant", "Spike Rush", "spike rush")]
    [InlineData("riot:valorant", "Team Deathmatch", "team deathmatch")]
    [InlineData("riot:valorant", "Ranked", "competitive")]
    [InlineData("riot:rainbow_six", "Competitive", "other")]
    public void ATrackerPlaylistAndAClientQueueNameTheSameMode(string game, string playlist, string mode) =>
        Assert.Equal(mode, RiotModes.FromTrackerPlaylist(game, playlist));

    [Fact]
    public void AClientsQueueAndTrackersPlaylistMeetInOneName()
    {
        // The seed's "Ranked Solo/Duo" hours and a session the League client reported as queue 420 are one mode.
        Assert.Equal(RiotModes.ParseLeagueSession(LeagueRankedSolo), RiotModes.FromTrackerPlaylist("riot:league_of_legends", "Ranked Solo/Duo"));
    }
}

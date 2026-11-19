using ExoLauncher.Models;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways sharing the game you opened from Exo can go wrong, each of them something a friend sees (the link itself:
/// FriendLinkTests, FriendLinkEndToEndTests):
/// - a game started from its store (seen running, not opened from Exo) is said as if Exo had opened it;
/// - a game stays said after it closed, or a launcher handing over to the game drops it and says it again;
/// - the game that ended was the older of two, and the one still open goes with it;
/// - a game you hid, or a title that came from a file on this PC, is named to friends;
/// - a link stays up, or a key stays in the presence, with the switch off or while invisible.
/// </summary>
public sealed class DiscordGameShareTests
{
    [Theory]
    [InlineData(true, false, true, "online", true, true)]
    [InlineData(true, false, true, "dnd", true, true)]
    [InlineData(true, false, true, "idle", true, true)]
    [InlineData(true, false, true, "invisible", true, false)]
    [InlineData(true, false, false, "online", true, false)] // the switch is off
    [InlineData(false, false, true, "online", true, false)] // Discord isn't connected
    [InlineData(true, true, true, "online", true, false)]   // Discord refused the key this run
    [InlineData(true, false, true, "online", false, false)] // no relay
    public void TheLinkRunsOnlyWithEveryGateOpen(bool gatewayUp, bool keyRefused, bool shares, string status, bool hasRelay, bool allowed) =>
        Assert.Equal(allowed, DiscordSession.LinksAllowed(gatewayUp, keyRefused, shares, status, hasRelay));

    [Theory]
    [InlineData(StoreKind.Steam, false, true)]
    [InlineData(StoreKind.Riot, false, true)] // its name only: nothing here touches the game
    [InlineData(StoreKind.Steam, true, false)]
    [InlineData(StoreKind.Local, false, false)]
    public void HiddenAndAddedByHandStayPrivate(StoreKind store, bool hidden, bool shared) =>
        Assert.Equal(shared, DiscordSession.MayShare(store, hidden));

    [Fact]
    public void AGameOnlySeenRunningIsNeverShared()
    {
        var games = new DiscordGamesOpened();
        Assert.False(games.RunState("steam:1", running: true));
        Assert.Null(games.Current);
        Assert.False(games.RunState("steam:1", running: false)); // not Exo's: nothing to check back on
    }

    [Fact]
    public void AGameGoesWhenItsProcessStaysGone()
    {
        var games = new DiscordGamesOpened();
        games.RunState("steam:1", running: true);
        Assert.True(games.Opened("steam:1", "Hades II", 1));
        Assert.True(games.RunState("steam:1", running: false));
        Assert.True(games.CloseUnlessRunning("steam:1"));
        Assert.Null(games.Current);
    }

    [Fact]
    public void AHandoverWithinTheGraceKeepsIt()
    {
        var games = new DiscordGamesOpened();
        games.RunState("steam:1", running: true);
        games.Opened("steam:1", "Hades II", 1);
        Assert.True(games.RunState("steam:1", running: false));
        games.RunState("steam:1", running: true);
        Assert.False(games.CloseUnlessRunning("steam:1"));
        Assert.Equal("Hades II", games.Current?.Title);
    }

    [Fact]
    public void WatchEndingWhileTheProcessLivesKeepsIt()
    {
        var games = new DiscordGamesOpened();
        games.RunState("steam:1", running: true);
        games.Opened("steam:1", "Hades II", 1);
        Assert.False(games.CloseUnlessRunning("steam:1")); // Exo's session ended; the run-state poll ends it when it exits
        Assert.Equal("Hades II", games.Current?.Title);
    }

    [Fact]
    public void AGameThatNeverRanGoesWithItsSession()
    {
        var games = new DiscordGamesOpened();
        games.Opened("epic:1", "Alan Wake 2", 1);
        Assert.True(games.CloseUnlessRunning("epic:1"));
        Assert.Null(games.Current);
    }

    [Fact]
    public void TheLatestOpenedShowsAndTheOtherComesBack()
    {
        var games = new DiscordGamesOpened();
        games.Opened("a", "First", 1);
        Assert.True(games.Opened("b", "Second", 2));
        Assert.False(games.Opened("b", "Second", 3)); // a second notice of the same launch: nothing to send
        Assert.Equal(2, games.Current?.StartedMs);
        Assert.False(games.CloseUnlessRunning("a")); // the older one: what shows didn't change
        Assert.Equal("Second", games.Current?.Title);
        games.Opened("a", "First", 4);
        Assert.True(games.CloseUnlessRunning("a"));
        Assert.Equal("Second", games.Current?.Title);
    }
}

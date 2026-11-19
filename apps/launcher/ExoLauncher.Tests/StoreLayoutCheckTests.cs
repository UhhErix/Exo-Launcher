using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the "installed but no games" warning could mislead or stay silent: it fires on a client Exo can see
/// while its library read finds nothing (a store that moved its data), not on a client that is absent, not on a
/// read that failed (that already shows a scan error), and not when games were found.
/// </summary>
public sealed class StoreLayoutCheckTests
{
    [Fact]
    public void AnInstalledClientWithNoGamesIsFlagged()
    {
        Assert.True(LibraryService.IsLayoutUnrecognized(clientPresent: true, scanSucceeded: true, games: 0));
    }

    [Fact]
    public void AnAbsentClientWithNoGamesIsNotFlagged()
    {
        Assert.False(LibraryService.IsLayoutUnrecognized(clientPresent: false, scanSucceeded: true, games: 0));
    }

    [Fact]
    public void AnInstalledClientWithGamesIsNotFlagged()
    {
        Assert.False(LibraryService.IsLayoutUnrecognized(clientPresent: true, scanSucceeded: true, games: 1));
    }

    [Fact]
    public void AFailedReadIsNotFlaggedBecauseItsScanErrorAlreadyShows()
    {
        Assert.False(LibraryService.IsLayoutUnrecognized(clientPresent: true, scanSucceeded: false, games: 0));
    }
}

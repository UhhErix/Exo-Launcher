using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Settings' one-time allow for upscaler files in games with anti-cheat (AppSettings.UpscalerAntiCheatEverywhere). Ways it
/// could go wrong: it reaches a Riot game, Vanguard or an install Exo couldn't read; a per-game consent for one anti-cheat
/// starts covering another; it is on before the player turns it on.
/// </summary>
public sealed class UpscalerAntiCheatEverywhereTests
{
    private static GameEntry Game(string id = "steam:1", string title = "A game") => new() { Id = id, Title = title, Store = StoreKind.Steam };

    [Fact]
    public void ItIsOffUntilThePlayerTurnsItOn() => Assert.False(new AppSettings().UpscalerAntiCheatEverywhere);

    [Fact]
    public void TheOneTimeAllowCoversEveryAntiCheatExoMayTouch()
    {
        Assert.True(DlssSwapService.Accepted(Game(), "Easy Anti-Cheat", DlssSwapService.AnyAntiCheat));
        Assert.True(DlssSwapService.Accepted(Game(), "BattlEye", DlssSwapService.AnyAntiCheat));
        Assert.Equal("given", DlssSwapService.ConsentState(Game(), "Easy Anti-Cheat", DlssSwapService.AnyAntiCheat));
    }

    [Fact]
    public void RiotVanguardAndUnreadableInstallsStayRefused()
    {
        Assert.False(DlssSwapService.Accepted(Game(), DlssSwapService.RiotVanguard, DlssSwapService.AnyAntiCheat));
        Assert.False(DlssSwapService.Accepted(Game(), DlssSwapService.UnreadableInstall, DlssSwapService.AnyAntiCheat));
        Assert.Equal("never", DlssSwapService.ConsentState(Game(), DlssSwapService.RiotVanguard, DlssSwapService.AnyAntiCheat));
    }

    [Fact]
    public void APerGameConsentStillNamesItsOwnAntiCheat()
    {
        Assert.True(DlssSwapService.Accepted(Game(), "BattlEye", "BattlEye"));
        Assert.False(DlssSwapService.Accepted(Game(), "Easy Anti-Cheat", "BattlEye"));
    }
}

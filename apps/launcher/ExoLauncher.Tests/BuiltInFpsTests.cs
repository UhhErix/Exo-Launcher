using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>Exo's FPS counter stays off over a game that draws its own, unless it would add items to the FPS.</summary>
public class BuiltInFpsTests
{
    [Theory]
    [InlineData("VALORANT", true)]
    [InlineData("Counter-Strike 2", true)]
    [InlineData("Call of Duty: Black Ops 6", true)]
    [InlineData("Hades", false)]
    [InlineData("", false)]
    public void A_game_with_its_own_counter_is_known_by_its_title(string title, bool known) =>
        Assert.Equal(known, BuiltInFps.Has(new GameEntry { Id = "x:" + title, Title = title, Store = StoreKind.Steam }));
}

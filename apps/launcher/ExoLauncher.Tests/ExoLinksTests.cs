using ExoLauncher.Services;

namespace ExoLauncher.Tests;

public class ExoLinksTests
{
    [Theory]
    [InlineData("exolauncher://pc", "pc", "overview")]
    [InlineData("exolauncher://pc/best", "pc", "performance")]
    [InlineData("EXOLAUNCHER://pc/updates/", "pc", "drivers")]
    [InlineData("exolauncher://apps?from=elsewhere", "pc", "apps")]
    [InlineData("exolauncher://somewhere-new", "library", null)]
    public void A_link_opens_its_place(string link, string room, string? part)
    {
        var place = ExoLinks.Find(["--other", link]);
        Assert.NotNull(place);
        Assert.Equal(room, place.Room);
        Assert.Equal(part, place.Part);
    }

    [Fact]
    public void No_link_opens_nothing() => Assert.Null(ExoLinks.Find(["--note", "C:\\a.txt"]));

    [Theory]
    [InlineData(null, "SwapEffectUpgradeEnable=1;")]
    [InlineData("VRROptimizeEnable=0;", "VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;")]
    [InlineData("SwapEffectUpgradeEnable=0;", null)]
    [InlineData("SwapEffectUpgradeEnable=1;AutoHDREnable=1;", null)]
    public void Windowed_game_optimizations_join_the_global_value(string? before, string? after) =>
        Assert.Equal(after, GpuPreference.WithSwapUpgrade(before));
}

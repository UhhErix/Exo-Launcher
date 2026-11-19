using ExoLauncher.Helpers;
using Xunit;

namespace ExoLauncher.Tests;

// PC › Graphics colours a GeForce's temperature from NVML's limits, silently: a wrong pick turns it amber at idle or
// never red. Ways it could fail: hot taken from the slowdown point while the card's maximum is known; warm missing when
// the driver has no temperature target; a target at or above hot (a raised target) making warm unreachable; a card
// with no limits getting made-up ones.
public sealed class GpuSensorsTests
{
    [Fact]
    public void An_RTX_3070_is_warm_at_its_target_and_hot_at_its_maximum() =>
        Assert.Equal(new HeatLimit(false, 83, 93), Nvml.Limit(gpuMax: 93, slowdown: 95, target: 83));

    [Fact]
    public void Without_a_maximum_hot_is_where_it_slows_down() =>
        Assert.Equal(new HeatLimit(false, 83, 95), Nvml.Limit(gpuMax: null, slowdown: 95, target: 83));

    [Theory]
    [InlineData(null)]
    [InlineData(93)]
    [InlineData(95)]
    public void Without_a_target_under_hot_warm_is_ten_short_of_it(int? target) =>
        Assert.Equal(new HeatLimit(false, 83, 93), Nvml.Limit(gpuMax: 93, slowdown: 95, target: target));

    [Fact]
    public void A_card_without_limits_has_none() =>
        Assert.Null(Nvml.Limit(gpuMax: null, slowdown: null, target: 83));
}

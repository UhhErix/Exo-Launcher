using ExoLauncher.Services.DiscordClient;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways this fails: a gateway that drops right after each READY comes back every second or two (the backoff used to
/// start over on READY), a failing gateway waits longer than a minute or never grows, Discord's 4008 waits less than
/// a minute, and the jitter stacks up from one attempt to the next.
/// </summary>
public sealed class DiscordGatewayBackoffTests
{
    [Fact]
    public void Failures_double_from_a_second_and_stop_at_the_cap()
    {
        var backoff = GatewayBackoff.Start;
        var waits = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            var (wait, next) = GatewayBackoff.Plan(backoff, limited: false, stable: false, jitterMs: 0);
            waits.Add(wait.TotalSeconds);
            backoff = next;
        }
        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 32, 60, 60 }, waits);
    }

    [Fact]
    public void A_connection_that_dropped_before_a_minute_keeps_growing_the_wait()
    {
        // READY, then a drop at once, again and again: the wait must keep growing, not return to a second.
        var (wait, next) = GatewayBackoff.Plan(TimeSpan.FromSeconds(16), limited: false, stable: false, jitterMs: 0);
        Assert.Equal(TimeSpan.FromSeconds(16), wait);
        Assert.Equal(TimeSpan.FromSeconds(32), next);
    }

    [Fact]
    public void A_connection_that_stayed_up_starts_over_at_a_second()
    {
        var (wait, next) = GatewayBackoff.Plan(TimeSpan.FromSeconds(60), limited: false, stable: true, jitterMs: 0);
        Assert.Equal(TimeSpan.FromSeconds(1), wait);
        Assert.Equal(TimeSpan.FromSeconds(2), next);
    }

    [Fact]
    public void Rate_limited_waits_a_full_minute_whatever_came_before()
    {
        var (wait, next) = GatewayBackoff.Plan(TimeSpan.FromSeconds(1), limited: true, stable: true, jitterMs: 0);
        Assert.Equal(TimeSpan.FromSeconds(60), wait);
        Assert.Equal(TimeSpan.FromSeconds(60), next);
    }

    [Fact]
    public void Jitter_adds_under_a_second_to_this_wait_only()
    {
        var (wait, next) = GatewayBackoff.Plan(TimeSpan.FromSeconds(8), limited: false, stable: false, jitterMs: 999);
        Assert.Equal(TimeSpan.FromMilliseconds(8999), wait);
        Assert.Equal(TimeSpan.FromSeconds(16), next);
    }
}

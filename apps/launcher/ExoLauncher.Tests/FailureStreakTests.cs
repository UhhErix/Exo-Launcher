using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the failure streak can go wrong:
/// - reaching the threshold reports more than once (or never);
/// - a success does not re-arm the report for the next run;
/// - failures spread wider than the window still report;
/// - two watched connections share one count and report for each other's failures.
/// </summary>
public sealed class FailureStreakTests
{
    private static readonly DateTime Start = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ReachingThresholdReportsOnce()
    {
        var streak = new FailureStreak(5, TimeSpan.FromMinutes(10));
        var reports = 0;
        for (var i = 0; i < 9; i++)
            if (streak.Record(false, Start.AddSeconds(i * 30))) reports++;
        Assert.Equal(1, reports);
    }

    [Fact]
    public void FourFailuresDoNotReport()
    {
        var streak = new FailureStreak(5, TimeSpan.FromMinutes(10));
        for (var i = 0; i < 4; i++) Assert.False(streak.Record(false, Start.AddSeconds(i)));
    }

    [Fact]
    public void SuccessResetsTheRunAndRearmsTheReport()
    {
        var streak = new FailureStreak(5, TimeSpan.FromMinutes(10));
        for (var i = 0; i < 4; i++) Assert.False(streak.Record(false, Start.AddSeconds(i)));
        Assert.False(streak.Record(true, Start.AddSeconds(5)));
        for (var i = 0; i < 4; i++) Assert.False(streak.Record(false, Start.AddSeconds(10 + i)));
        Assert.True(streak.Record(false, Start.AddSeconds(14)));

        // A success after a report re-arms it: the next run reports once more.
        Assert.False(streak.Record(true, Start.AddMinutes(1)));
        var second = 0;
        for (var i = 0; i < 6; i++)
            if (streak.Record(false, Start.AddMinutes(2).AddSeconds(i))) second++;
        Assert.Equal(1, second);
    }

    [Fact]
    public void FailuresSpreadWiderThanTheWindowDoNotReport()
    {
        var streak = new FailureStreak(5, TimeSpan.FromMinutes(10));
        for (var i = 0; i < 10; i++)
            Assert.False(streak.Record(false, Start.AddMinutes(i * 3)));
    }

    [Fact]
    public void FailuresInsideTheWindowAfterSpreadOnesStillReport()
    {
        var streak = new FailureStreak(5, TimeSpan.FromMinutes(10));
        Assert.False(streak.Record(false, Start));
        Assert.False(streak.Record(false, Start.AddMinutes(4)));
        Assert.False(streak.Record(false, Start.AddMinutes(8)));
        Assert.False(streak.Record(false, Start.AddMinutes(12)));
        Assert.False(streak.Record(false, Start.AddMinutes(16)));
        // The last five span 16 minutes, then 14, then 11; at 19.5 they are 12, 16, 18, 19, 19.5: 7.5 minutes, so it reports.
        Assert.False(streak.Record(false, Start.AddMinutes(18)));
        Assert.False(streak.Record(false, Start.AddMinutes(19)));
        Assert.True(streak.Record(false, Start.AddMinutes(19.5)));
    }

    [Fact]
    public void InstancesDoNotShareACount()
    {
        var gateway = new FailureStreak(5, TimeSpan.FromMinutes(10));
        var voice = new FailureStreak(5, TimeSpan.FromMinutes(10));
        for (var i = 0; i < 4; i++)
        {
            Assert.False(gateway.Record(false, Start.AddSeconds(i)));
            Assert.False(voice.Record(false, Start.AddSeconds(i)));
        }
        Assert.True(gateway.Record(false, Start.AddSeconds(5)));
        Assert.False(voice.Record(true, Start.AddSeconds(5)));
        Assert.False(voice.Record(false, Start.AddSeconds(6)));
    }
}

using ExoLauncher.Services;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// How many minutes one synced Riot line, or one synced playtime entry, may carry. A seed is a tracker.gg total for a whole
/// game (up to 10,000,000); a play line or a playtime entry is one session (a week at most, 10,080). Ways this could fail: a
/// single line claims 20,000 minutes and is counted; a seed of a real lifetime (more than 100,000 minutes) is refused.
/// Fixture lines match no real account.
/// </summary>
public sealed class RiotLineCapsTests
{
    private static string Line(string kind, int minutes) =>
        "{\"schema\":1,\"kind\":\"" + kind + "\",\"key\":\"k\",\"game\":\"riot:valorant\",\"minutes\":" + minutes + ",\"startUtc\":\"2026-11-08T10:00:00Z\",\"importedAt\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}";

    [Theory]
    [InlineData("play", 1, true)]
    [InlineData("play", 10080, true)]
    [InlineData("play", 10081, false)]
    [InlineData("play", 20000, false)]
    [InlineData("seed", 100001, true)]
    [InlineData("seed", 10000000, true)]
    [InlineData("seed", 10000001, false)]
    [InlineData("play", 0, false)]
    public void TheMinuteCapDependsOnTheKind(string kind, int minutes, bool accepted) =>
        Assert.Equal(accepted, RiotLedger.IsWellFormedLine(Line(kind, minutes)));

    [Fact]
    public void ASyncedPlaytimeEntryLongerThanAWeekIsNotCredited()
    {
        var entry = "{\"game\":\"steam:999999951\",\"minutes\":20000,\"start\":\"2026-11-08T10:00:00Z\",\"end\":\"2026-11-22T10:00:00Z\"}";

        Assert.Throws<FormatException>(() => AccountSyncApply.CheckPlaytimeMinutes(entry));
    }

    [Fact]
    public void AXboxSeedOverTenMillionMinutesIsRefused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-seed-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prior = Environment.GetEnvironmentVariable(ExoLauncher.Helpers.PathHelper.DataDirOverrideVariable);
        Environment.SetEnvironmentVariable(ExoLauncher.Helpers.PathHelper.DataDirOverrideVariable, dir);
        try
        {
            PlaytimeService.ForgetLoaded();
            var seed = "{\"title\":\"t1\",\"name\":\"Game\",\"minutes\":10000001}";

            Assert.Throws<FormatException>(() => PlaytimeService.AddSyncedSeed(seed));
        }
        finally
        {
            PlaytimeService.ForgetLoaded();
            Environment.SetEnvironmentVariable(ExoLauncher.Helpers.PathHelper.DataDirOverrideVariable, prior);
            try { ExoLauncher.Helpers.DeletionGate.DeleteDirectory(dir, recursive: true); } catch { }
        }
    }
}

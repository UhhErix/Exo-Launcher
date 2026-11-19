using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// What Exo cleans by itself and when (JunkAutoClean), fed values only: no folder is read or deleted. Ways it could fail:
/// the whole Recycle Bin going without a press, or old shader caches going daily rather than weekly; a kind added to
/// JunkFiles later cleaning by itself without anyone deciding it should; a clean running more than once a day, or never
/// again after the clock was set back; a big scan not bringing the clean forward, or Exo's backup inside its three days
/// bringing it forward on every scan; a backup that just became one (the way back from a bad update) going at once
/// because its folder is old, or an update that never finished staying forever.
/// </summary>
public sealed class JunkAutoCleanTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Daily_kinds_weekly_kinds_and_never_the_whole_recycle_bin()
    {
        Assert.True(JunkAutoClean.Auto("temp"));
        Assert.True(JunkAutoClean.Auto("crash"));
        Assert.True(JunkAutoClean.Auto("thumbs"));
        Assert.True(JunkAutoClean.Auto("exo"));
        // Weekly: only what has sat in the Recycle Bin a month. Shader caches are Exo Shaders' alone, never cleaned by itself.
        Assert.False(JunkAutoClean.Auto("shaders"));
        Assert.True(JunkAutoClean.Auto("bin-old"));
        Assert.False(JunkAutoClean.Auto("bin"));
        Assert.False(JunkAutoClean.Auto("downloads"));
        Assert.False(JunkAutoClean.Auto(""));
        Assert.DoesNotContain(JunkAutoClean.Kinds, kind => kind.Contains("shader", StringComparison.OrdinalIgnoreCase) || kind.StartsWith("bin", StringComparison.Ordinal));
        Assert.DoesNotContain(JunkAutoClean.WeeklyKinds, kind => kind is "bin");
    }

    [Fact]
    public void Due_once_a_day_or_after_a_big_find()
    {
        Assert.True(JunkAutoClean.Due(null, big: false, Now));
        Assert.False(JunkAutoClean.Due(Now.AddHours(-23), big: false, Now));
        Assert.True(JunkAutoClean.Due(Now.AddHours(-24), big: false, Now));
        Assert.True(JunkAutoClean.Due(Now.AddMinutes(-5), big: true, Now));
        // The clock set back before the last clean: not stuck waiting for that day to come again.
        Assert.True(JunkAutoClean.Due(Now.AddDays(3), big: false, Now));
    }

    [Fact]
    public void A_big_find_counts_only_what_goes_by_itself_and_not_exos_backups()
    {
        static JunkFiles.Found Found(string id, long bytes) => new(id, id, "", bytes, 1);
        Assert.True(JunkAutoClean.BigFind([Found("temp", 700_000_000), Found("crash", 400_000_000)]));
        Assert.False(JunkAutoClean.BigFind([Found("temp", 900_000_000)]));
        Assert.False(JunkAutoClean.BigFind([Found("shaders", 36_000_000_000), Found("bin", 5_000_000_000), Found("temp", 100_000_000)]));
        Assert.False(JunkAutoClean.BigFind([Found("exo", 2_000_000_000), Found("thumbs", 200_000_000)]));
    }

    [Fact]
    public void A_backup_goes_three_days_after_it_became_one()
    {
        // app.previous: its folder was made long ago; it became a backup when today's app arrived.
        var arrived = Now.AddHours(-2);
        Assert.False(JunkAutoClean.BackupOld(made: Now.AddDays(-40), arrived, Now));
        Assert.True(JunkAutoClean.BackupOld(made: Now.AddDays(-40), Now.AddDays(-3), Now));
        // app.incoming: an update that never finished, made after the running app.
        Assert.False(JunkAutoClean.BackupOld(made: Now.AddDays(-1), Now.AddDays(-10), Now));
        Assert.True(JunkAutoClean.BackupOld(made: Now.AddDays(-4), Now.AddDays(-10), Now));
    }
}

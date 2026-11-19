using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// The tray-icon registry is read when Windows says it changed, not on every ask. Ways this could fail: every call walks
/// the registry again (the old cost), a change is never seen so a new icon is missed, a missing key is kept as "empty"
/// for good, a change during a read is lost.
/// </summary>
public sealed class NotifyIconEntriesTests
{
    private static List<(string?, string?, int?)> One(string path) => [(path, null, 1)];

    [Fact]
    public void Many_asks_read_once_and_a_change_reads_again()
    {
        var reads = 0;
        var entries = new NotifyIconEntries(() => { reads++; return One(@"C:\a.exe"); });
        for (var i = 0; i < 50; i++) entries.Get();
        Assert.Equal(1, reads);
        entries.Changed();
        entries.Get();
        entries.Get();
        Assert.Equal(2, reads);
    }

    [Fact]
    public void An_old_read_is_made_again_without_a_signal()
    {
        var clock = 0L;
        var reads = 0;
        var entries = new NotifyIconEntries(() => { reads++; return One("x"); }, () => clock, maxAgeMs: 1000);
        entries.Get();
        clock = 1000;
        entries.Get();
        Assert.Equal(1, reads);
        clock = 1001;
        entries.Get();
        Assert.Equal(2, reads);
    }

    [Fact]
    public void A_missing_key_is_not_remembered_as_empty()
    {
        var present = false;
        var entries = new NotifyIconEntries(() => present ? One("later") : null);
        Assert.Empty(entries.Get());
        present = true;
        Assert.Single(entries.Get());
    }

    [Fact]
    public void A_change_that_lands_during_a_read_is_not_lost()
    {
        var reads = 0;
        NotifyIconEntries? entries = null;
        entries = new NotifyIconEntries(() => { if (reads++ == 0) entries!.Changed(); return One("x"); });
        entries.Get();
        entries.Get();
        Assert.Equal(2, reads);
        entries.Get();
        Assert.Equal(2, reads);
    }
}

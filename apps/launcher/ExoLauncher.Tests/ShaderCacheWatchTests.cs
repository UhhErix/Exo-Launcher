using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The shader lock and the outside-wipe detection, fed values only (no cache is read or deleted). Ways it could fail: a wipe
/// missed when the DirectX folder is gone or made again, or most of a large cache is gone; Exo's own compile or delete read as
/// a wipe; a small drop read as a wipe; a game stays marked after it compiled again, or a mark not reaching the games it names;
/// a cleaner deleting a shader cache, or an Exo-owned delete in one while a compile runs.
/// </summary>
public sealed class ShaderCacheWatchTests
{
    private static readonly DateTime Then = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    private static ShaderCacheWatch.Snapshot Snap(long bytes, bool exists = true, long created = 1, DateTime? at = null) =>
        new(bytes, exists, created, at ?? Then);

    [Fact]
    public void A_wipe_is_seen_when_the_directx_folder_is_gone_with_no_exo_activity()
    {
        var previous = Snap(10 << 20);
        Assert.True(ShaderCacheWatch.Wiped(previous, Snap(0, exists: false, created: 0, at: Then.AddMinutes(10)), lastExoActivity: Then.AddHours(-1)));
    }

    [Fact]
    public void A_wipe_is_seen_when_the_directx_folder_was_made_again()
    {
        var previous = Snap(10 << 20, created: 100);
        Assert.True(ShaderCacheWatch.Wiped(previous, Snap(10 << 20, created: 200, at: Then.AddMinutes(10)), lastExoActivity: Then.AddHours(-1)));
    }

    [Fact]
    public void A_wipe_is_seen_when_most_of_a_significant_cache_is_gone()
    {
        var previous = Snap(2L * ShaderCacheWatch.Significant);
        Assert.True(ShaderCacheWatch.Wiped(previous, Snap(ShaderCacheWatch.Significant / 4, at: Then.AddMinutes(10)), lastExoActivity: Then.AddHours(-1)));
    }

    [Fact]
    public void Exos_own_compile_or_delete_is_never_read_as_a_wipe()
    {
        var previous = Snap(2L * ShaderCacheWatch.Significant);
        // Exo worked on the caches after the last look: its own work, so no wipe, and the next look starts again.
        Assert.False(ShaderCacheWatch.Wiped(previous, Snap(0, exists: false, created: 0, at: Then.AddMinutes(10)), lastExoActivity: Then.AddMinutes(5)));
    }

    [Fact]
    public void A_small_drop_or_no_earlier_look_is_not_a_wipe()
    {
        Assert.False(ShaderCacheWatch.Wiped(null, Snap(0, exists: false, created: 0), lastExoActivity: Then.AddHours(-1)));
        // Ordinary churn: most of the cache is still there.
        var previous = Snap(2L * ShaderCacheWatch.Significant);
        Assert.False(ShaderCacheWatch.Wiped(previous, Snap(ShaderCacheWatch.Significant, at: Then.AddMinutes(10)), lastExoActivity: Then.AddHours(-1)));
        // A cache too small to matter dropping to nothing, with the folder still there, is no wipe.
        Assert.False(ShaderCacheWatch.Wiped(Snap(1 << 20), Snap(0, at: Then.AddMinutes(10)), lastExoActivity: Then.AddHours(-1)));
    }

    [Fact]
    public void A_wipe_marks_the_games_it_names_and_only_those()
    {
        var marks = ShaderCacheWatch.Mark(new Dictionary<string, DateTime>(), ["hades", "celeste"], Then);
        Assert.Equal(Then, marks["hades"]);
        Assert.Equal(Then, marks["celeste"]);
        Assert.False(ShaderWipeMarks.Marked(marks, "portal", null));
    }

    [Fact]
    public void A_mark_holds_until_the_game_compiles_after_it()
    {
        var marks = new Dictionary<string, DateTime> { ["hades"] = Then };
        Assert.True(ShaderWipeMarks.Marked(marks, "hades", null));
        Assert.True(ShaderWipeMarks.Marked(marks, "hades", Then.AddHours(-2)));
        Assert.False(ShaderWipeMarks.Marked(marks, "hades", Then.AddMinutes(1)));
    }

    [Fact]
    public void A_compile_in_progress_refuses_an_exo_owned_delete_in_a_shader_cache_until_it_ends()
    {
        var policy = new DeletionGate.Policy(
            KeptRoots: [],
            ExoData: null,
            ShaderRoots: [Path.Combine("Q:\\", "Local", "D3DSCache")],
            Running: () => [],
            ShaderBusy: () => ShaderCacheLock.Held);
        var cache = Path.Combine("Q:\\", "Local", "D3DSCache", "game", "cache.db");
        var before = ShaderCacheLock.LastActivity;

        using (ShaderCacheLock.Hold())
        {
            Assert.True(ShaderCacheLock.Held);
            Assert.NotNull(DeletionGate.Refusal(cache, clean: false, owners: null, policy));
            Assert.True(ShaderCacheLock.LastActivity >= before);
        }

        Assert.False(ShaderCacheLock.Held);
        Assert.Null(DeletionGate.Refusal(cache, clean: false, owners: null, policy));
    }

    [Fact]
    public void A_cleaner_never_deletes_a_shader_cache_even_when_nothing_is_compiling()
    {
        var policy = new DeletionGate.Policy(
            KeptRoots: [],
            ExoData: null,
            ShaderRoots: [Path.Combine("Q:\\", "Local", "D3DSCache")],
            Running: () => [],
            ShaderBusy: () => false);
        var cache = Path.Combine("Q:\\", "Local", "D3DSCache", "game", "cache.db");
        var steam = Path.Combine("Q:\\", "Steam", "steamapps", "shadercache", "570");
        Assert.NotNull(DeletionGate.Refusal(cache, clean: true, owners: null, policy));
        Assert.NotNull(DeletionGate.Refusal(steam, clean: true, owners: null, policy));
        Assert.False(DeletionGate.Clean(cache, null, out _, policy));
    }

    [Fact]
    public void An_engine_delete_is_never_read_as_a_wipe()
    {
        // The engine reports a cache delete under way: its folder going is the delete, not an outside wipe.
        ShaderCacheLock.ReportEngineDeleting(true, Then);
        try
        {
            Assert.False(ShaderCacheWatch.Wiped(Snap(2L * ShaderCacheWatch.Significant, at: Then.AddMinutes(-10)),
                Snap(0, exists: false, created: 0, at: Then.AddMinutes(5)), ShaderCacheLock.LastActivity));
        }
        finally { ShaderCacheLock.ReportEngineDeleting(false, Then.AddMinutes(6)); }
    }

    [Fact]
    public void The_check_right_after_an_engine_delete_ends_is_skipped_and_the_one_after_is_not()
    {
        ShaderCacheLock.ReportEngineDeleting(true, Then);
        ShaderCacheLock.ReportEngineDeleting(false, Then.AddMinutes(1));
        // A look taken before the delete ended: the delete's own effect, so skipped.
        Assert.False(ShaderCacheWatch.Wiped(Snap(2L * ShaderCacheWatch.Significant, at: Then.AddMinutes(-10)),
            Snap(0, exists: false, created: 0, at: Then.AddMinutes(5)), ShaderCacheLock.LastActivity));
        // The next look starts after the delete: a real wipe from then on is seen.
        Assert.True(ShaderCacheWatch.Wiped(Snap(2L * ShaderCacheWatch.Significant, at: Then.AddMinutes(2)),
            Snap(0, exists: false, created: 0, at: Then.AddMinutes(12)), ShaderCacheLock.LastActivity));
    }

    [Fact]
    public void A_stale_compiling_flag_releases_itself_two_minutes_after_the_engine_last_said_so()
    {
        ShaderCacheLock.ReportEngineCompiling(true, Then);
        try
        {
            Assert.True(ShaderCacheLock.HeldAt(Then.AddMinutes(1)));
            Assert.False(ShaderCacheLock.HeldAt(Then.AddMinutes(2).AddSeconds(1)));
        }
        finally { ShaderCacheLock.ReportEngineCompiling(false, Then.AddMinutes(3)); }
    }

    [Fact]
    public void A_compiling_flag_releases_at_once_when_the_engine_stops_compiling()
    {
        ShaderCacheLock.ReportEngineCompiling(true, Then);
        ShaderCacheLock.ReportEngineCompiling(false, Then.AddMinutes(1));
        Assert.False(ShaderCacheLock.HeldAt(Then.AddMinutes(1)));
    }

    [Fact]
    public void A_delete_flag_releases_itself_when_the_engine_goes_quiet()
    {
        ShaderCacheLock.ReportEngineDeleting(true, Then);
        try
        {
            Assert.True(ShaderCacheLock.HeldAt(Then.AddMinutes(1)));
            Assert.False(ShaderCacheLock.HeldAt(Then.AddMinutes(3)));
        }
        finally { ShaderCacheLock.ReportEngineDeleting(false, Then.AddMinutes(4)); }
    }

    [Fact]
    public void A_cache_delete_mutex_held_by_another_thread_is_seen_and_released_when_it_lets_go()
    {
        var name = @"Local\ExoTest.CacheDelete." + Guid.NewGuid().ToString("N");
        using var holding = new ManualResetEventSlim();
        using var let = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(true, name);
            holding.Set();
            let.Wait();
        });
        owner.Start();
        holding.Wait(TestContext.Current.CancellationToken);
        Assert.True(ShaderCacheWatch.MutexHeld(name));
        let.Set();
        owner.Join();
        Assert.False(ShaderCacheWatch.MutexHeld(name));
    }

    [Fact]
    public void A_compile_lock_held_by_the_engine_is_seen_and_released_when_it_lets_go()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-compile-" + Guid.NewGuid().ToString("N") + ".lock");
        File.WriteAllText(path, "");
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.True(ShaderCacheWatch.FileHeld(path));
            Assert.False(ShaderCacheWatch.FileHeld(path));
        }
        finally { DeletionGate.DeleteFile(path); }
    }

    [Fact]
    public void A_warmed_report_after_a_mark_clears_it_and_one_before_keeps_it()
    {
        var marks = new Dictionary<string, DateTime> { ["hades"] = Then };
        Assert.True(ShaderWipeMarks.ClearedBy(marks, "hades", Then.AddMinutes(1)));
        Assert.False(ShaderWipeMarks.ClearedBy(marks, "hades", Then.AddMinutes(-1)));
        Assert.False(ShaderWipeMarks.ClearedBy(marks, "hades", null));
        Assert.False(ShaderWipeMarks.ClearedBy(marks, "celeste", Then.AddMinutes(1)));
    }
}

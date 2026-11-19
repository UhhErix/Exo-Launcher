using ExoLauncher.Adapters;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways game start detection can fail, each silently (a game shows as stopped, or Exo works through every poll):
/// - an unrelated process's window is matched and wakes a scan;
/// - a game folder's sibling (C:\Games\Foo vs C:\Games\FooBar) is matched by a bare string prefix;
/// - a store client, overlay or anti-cheat process under a game folder is matched;
/// - a redistributable or installer under a game folder is matched as the game;
/// - an empty or missing image path (access denied, a system process) is matched;
/// - a game in a second library folder is missed because only the first folder is checked;
/// - every control and tooltip show counts as a window start, so a scan runs on each one;
/// - a process's image is opened again on every show, not once per process;
/// - a protected process that can't be opened is retried on every show;
/// - the cache grows without bound, or a full cache keeps resolving a process it no longer holds;
/// - the installed-folder set is rebuilt on every window event instead of once per library change;
/// - a library change is not seen, so a newly installed game is missed with the old folder set;
/// - the window hooks fail and nothing polls, so starts are missed for the rest of the session;
/// - the fallback runs at the old 5 s rate or stops once a game is running unwatched.
/// </summary>
public sealed class GameStartDetectionTests
{
    private const string Game = @"C:\Games\Foo";

    private static HashSet<string> Roots(params string[] folders) =>
        new(folders.Select(folder => ProcessHelper.RootPrefix(folder)!), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_game_executable_under_its_folder_is_a_game_start()
    {
        Assert.True(GameStartMatcher.IsGameProcess(@"C:\Games\Foo\bin\foo.exe", "foo", Roots(Game)));
    }

    [Fact]
    public void An_unrelated_process_is_not_a_game_start()
    {
        Assert.False(GameStartMatcher.IsGameProcess(@"C:\Windows\System32\notepad.exe", "notepad", Roots(Game)));
    }

    [Fact]
    public void A_sibling_folder_sharing_the_name_prefix_is_not_the_game()
    {
        Assert.False(GameStartMatcher.IsGameProcess(@"C:\Games\FooBar\foobar.exe", "foobar", Roots(Game)));
    }

    [Fact]
    public void A_store_client_under_a_game_folder_is_not_a_game_start()
    {
        Assert.False(GameStartMatcher.IsGameProcess(@"C:\Games\Foo\steam.exe", "steam", Roots(Game)));
        Assert.False(GameStartMatcher.IsGameProcess(@"C:\Games\Foo\bin\gameoverlayui.exe", "gameoverlayui", Roots(Game)));
    }

    [Fact]
    public void An_anti_cheat_process_under_a_game_folder_is_not_a_game_start()
    {
        Assert.False(GameStartMatcher.IsGameProcess(@"C:\Games\Foo\vgc.exe", "vgc", Roots(Game)));
    }

    [Fact]
    public void A_redistributable_under_a_helper_folder_is_not_a_game_start()
    {
        Assert.False(GameStartMatcher.IsGameProcess(@"C:\Games\Foo\_CommonRedist\vcredist\setup2.exe", "setup2", Roots(Game)));
    }

    [Fact]
    public void A_missing_or_blank_image_path_is_not_a_game_start()
    {
        Assert.False(GameStartMatcher.IsGameProcess(null, "foo", Roots(Game)));
        Assert.False(GameStartMatcher.IsGameProcess("   ", "foo", Roots(Game)));
    }

    [Fact]
    public void No_installed_games_match_nothing()
    {
        Assert.False(GameStartMatcher.IsGameProcess(@"C:\Games\Foo\bin\foo.exe", "foo", Roots()));
    }

    [Fact]
    public void A_game_in_a_second_library_folder_is_a_game_start()
    {
        Assert.True(GameStartMatcher.IsGameProcess(@"D:\Games\Bar\bar.exe", "bar", Roots(Game, @"D:\Games\Bar")));
    }

    [Fact]
    public void A_top_level_window_showing_is_a_start()
    {
        Assert.True(WindowStarts.IsTopLevelShow(WindowStarts.EventObjectShow, WindowStarts.ObjidWindow, 0, topLevel: true));
    }

    [Fact]
    public void A_child_control_or_caret_showing_is_not_a_start()
    {
        // A control's own window: idChild names the child, so it is not the window itself.
        Assert.False(WindowStarts.IsTopLevelShow(WindowStarts.EventObjectShow, WindowStarts.ObjidWindow, 3, topLevel: true));
        // The caret and cursor objects are not windows.
        Assert.False(WindowStarts.IsTopLevelShow(WindowStarts.EventObjectShow, -8, 0, topLevel: true));
    }

    [Fact]
    public void An_owned_or_child_window_showing_is_not_a_start()
    {
        Assert.False(WindowStarts.IsTopLevelShow(WindowStarts.EventObjectShow, WindowStarts.ObjidWindow, 0, topLevel: false));
    }

    [Fact]
    public void Other_window_events_are_not_a_start()
    {
        // EVENT_SYSTEM_FOREGROUND is the other source; its own handler checks the front, so this filter says no.
        Assert.False(WindowStarts.IsTopLevelShow(0x0003, WindowStarts.ObjidWindow, 0, topLevel: true));
    }

    [Fact]
    public void A_process_is_resolved_once_however_many_shows_it_has()
    {
        var resolved = new List<uint>();
        var cache = new ProcessImageCache(pid => { resolved.Add(pid); return @"C:\Games\Foo\bin\foo.exe"; });

        Assert.Equal(@"C:\Games\Foo\bin\foo.exe", cache.ImageOf(42));
        Assert.Equal(@"C:\Games\Foo\bin\foo.exe", cache.ImageOf(42));
        Assert.Equal(@"C:\Games\Foo\bin\foo.exe", cache.ImageOf(42));
        Assert.Equal([42u], resolved);
    }

    [Fact]
    public void A_failed_resolve_is_remembered_so_a_protected_process_is_not_reopened_each_show()
    {
        var calls = 0;
        var cache = new ProcessImageCache(_ => { calls++; return null; });

        Assert.Null(cache.ImageOf(7));
        Assert.Null(cache.ImageOf(7));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Different_processes_are_each_resolved()
    {
        var resolved = new List<uint>();
        var cache = new ProcessImageCache(pid => { resolved.Add(pid); return $"C:\\p{pid}.exe"; });

        Assert.Equal(@"C:\p1.exe", cache.ImageOf(1));
        Assert.Equal(@"C:\p2.exe", cache.ImageOf(2));
        Assert.Equal([1u, 2u], resolved);
    }

    [Fact]
    public void A_full_cache_clears_and_resolves_a_process_it_no_longer_holds()
    {
        var calls = 0;
        var cache = new ProcessImageCache(pid => { calls++; return $"C:\\p{pid}.exe"; }, capacity: 2);

        cache.ImageOf(1);
        cache.ImageOf(2);
        Assert.Equal(2, cache.Count);
        cache.ImageOf(3); // full: the cache clears, then holds only 3
        Assert.Equal(1, cache.Count);
        cache.ImageOf(1); // no longer held, so it is resolved again
        Assert.Equal(4, calls);
    }

    [Fact]
    public void Two_window_events_with_no_library_change_build_the_folder_set_once()
    {
        var loads = 0;
        var folders = new GameFolderSet(() => { loads++; return [Game]; });
        var images = new Dictionary<uint, string> { [1] = @"C:\Games\Foo\bin\foo.exe", [2] = @"C:\Windows\explorer.exe" };
        var watch = new GameWindowWatch(pid => images.GetValueOrDefault(pid), folders);

        Assert.True(watch.IsGameWindow(1));
        Assert.False(watch.IsGameWindow(2));
        Assert.True(watch.IsGameWindow(1));
        Assert.Equal(1, loads);
        Assert.Equal(1, folders.Builds);
    }

    [Fact]
    public void After_a_library_change_the_next_event_sees_the_new_folders()
    {
        var current = new[] { Game };
        var folders = new GameFolderSet(() => current);
        var images = new Dictionary<uint, string> { [1] = @"D:\Games\Bar\bar.exe" };
        var watch = new GameWindowWatch(pid => images.GetValueOrDefault(pid), folders);

        Assert.False(watch.IsGameWindow(1));
        current = [Game, @"D:\Games\Bar"];
        watch.LibraryChanged();
        Assert.True(watch.IsGameWindow(1));
        Assert.Equal(2, folders.Builds);
    }

    [Fact]
    public void A_window_whose_image_cannot_be_read_builds_no_folder_set()
    {
        var folders = new GameFolderSet(() => [Game]);
        var watch = new GameWindowWatch(_ => null, folders);

        Assert.False(watch.IsGameWindow(9));
        Assert.Equal(0, folders.Builds);
    }

    [Fact]
    public void The_fallback_polls_every_15_seconds_when_window_hooks_cannot_be_set()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), RunStatePlan.PeriodicEvery(eventsAvailable: false, anyRunningGameUnwatched: false));
        Assert.Equal(TimeSpan.FromSeconds(15), RunStatePlan.PeriodicEvery(eventsAvailable: false, anyRunningGameUnwatched: true));
    }

    [Fact]
    public void With_hooks_and_every_running_game_watched_nothing_polls()
    {
        Assert.Null(RunStatePlan.PeriodicEvery(eventsAvailable: true, anyRunningGameUnwatched: false));
    }

    [Fact]
    public void A_running_game_whose_exit_cannot_be_watched_keeps_the_15_second_poll()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), RunStatePlan.PeriodicEvery(eventsAvailable: true, anyRunningGameUnwatched: true));
    }
}

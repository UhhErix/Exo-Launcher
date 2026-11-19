using System.Diagnostics;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// A store manifest rewritten with the same bytes (the Riot Client's RiotClientInstalls.json, every half minute) must not
/// rescan the library. Ways this fails: a real change taken for a rewrite, so a new install never shows; a file first
/// seen or unreadable taken as unchanged; the remembered bytes not updated, so every later write looks new or old.
/// </summary>
public sealed class LibraryWatchersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-watch-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_dir, recursive: true);

    // Ways this fails: a finished 0.113 install never rescans because its install list is filtered out; a telemetry or
    // cache file of the same launcher rescans the library every few seconds.
    [Fact]
    public void The_0113_install_list_is_a_library_change_and_its_siblings_are_not()
    {
        Assert.True(LibraryWatchers.IsLibraryManifestChange("installations.jsonl"));
        Assert.False(LibraryWatchers.IsLibraryManifestChange("telemetry-seg.jsonl"));
        Assert.False(LibraryWatchers.IsLibraryManifestChange("installations.jsonl.tmp"));
    }

    // Ways this fails: the 0.113 launcher installed while Exo runs, so its folder is missing at start and the root is
    // never watched until a later refresh: its installs are missed for minutes. The root's appearance must attach
    // its watcher and rescan its store.
    [Fact]
    public async Task A_store_folder_created_after_start_is_attached_and_its_store_rescans()
    {
        var parent = Directory.CreateTempSubdirectory("exo-watch-parent-").FullName;
        try
        {
            var root = Path.Combine(parent, "com.epicgames.eglv2");
            using var watchers = new LibraryWatchers(() => new[] { (Path: root, Store: (StoreKind?)StoreKind.Epic) });
            watchers.RefreshRoots();
            Assert.False(watchers.IsWatching(root));

            var fired = new TaskCompletionSource<StoreKind?>(TaskCreationOptions.RunContinuationsAsynchronously);
            watchers.Changed += store => fired.TrySetResult(store);
            Directory.CreateDirectory(root);

            Assert.Equal(StoreKind.Epic, await fired.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.True(watchers.IsWatching(root));
        }
        finally
        {
            DeletionGate.DeleteDirectory(parent, recursive: true);
        }
    }

    [Fact]
    public void A_junction_root_is_not_watched()
    {
        // A junction (a reparse point) can point at another drive or folder than the store's: Exo does not watch through it.
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(_dir, "junction");
        using (var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            made.WaitForExit();
            Assert.Equal(0, made.ExitCode);
        }
        try
        {
            using var watchers = new LibraryWatchers(() => [(Path: junction, Store: (StoreKind?)StoreKind.Epic)]);
            watchers.RefreshRoots();

            Assert.Equal(0, watchers.WatchedCount);
        }
        finally
        {
            DeletionGate.DeleteDirectory(junction);
        }
    }

    [Fact]
    public void The_remembered_bytes_are_capped_and_the_least_recently_used_goes_first()
    {
        using var watchers = new LibraryWatchers(() => []);
        var files = Enumerable.Range(0, 2005).Select(i => Path.Combine(_dir, $"m{i}.json")).ToArray();
        foreach (var file in files)
        {
            File.WriteAllText(file, "bytes " + file);
            watchers.SameAsBefore(file);
        }

        Assert.True(watchers.RememberedCount <= 2000);
        // The first file was dropped, so it reads as changed again; the last one is still remembered as the same.
        Assert.False(watchers.SameAsBefore(files[0]));
        Assert.True(watchers.SameAsBefore(files[2004]));
    }

    [Fact]
    public void Only_a_rewrite_with_the_same_bytes_counts_as_unchanged()
    {
        using var watchers = new LibraryWatchers(() => []);
        var file = Path.Combine(_dir, "RiotClientInstalls.json");

        File.WriteAllText(file, """{"rc_default":"C:/Riot Games/Riot Client/RiotClientServices.exe"}""");
        Assert.False(watchers.SameAsBefore(file));
        File.WriteAllText(file, """{"rc_default":"C:/Riot Games/Riot Client/RiotClientServices.exe"}""");
        Assert.True(watchers.SameAsBefore(file));
        File.WriteAllText(file, """{"rc_default":"D:/Riot Games/Riot Client/RiotClientServices.exe"}""");
        Assert.False(watchers.SameAsBefore(file));
        Assert.True(watchers.SameAsBefore(file));

        Assert.False(watchers.SameAsBefore(Path.Combine(_dir, "missing.json")));
    }
}

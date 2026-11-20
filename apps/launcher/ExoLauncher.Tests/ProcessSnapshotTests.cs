using System.Diagnostics;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Unit tests for <see cref="ProcessSnapshot"/> query helpers, extension normalization, and short-TTL caching.
/// </summary>
public sealed class ProcessSnapshotTests
{
    [Fact]
    public void TryRead_and_Exists_find_current_process_with_commit_bytes()
    {
        Assert.True(ProcessSnapshot.TryRead(out var rows));
        var current = Assert.Single(rows, row => row.Pid == Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(current.Name));
        Assert.True(current.PrivateBytes > 0);
        Assert.True(ProcessSnapshot.Exists(Environment.ProcessId));
        Assert.False(ProcessSnapshot.Exists(0));
        Assert.False(ProcessSnapshot.Exists(-1));
    }

    [Theory]
    [InlineData("steam.exe", "steam")]
    [InlineData("STEAM.EXE", "STEAM")]
    [InlineData("steam", "steam")]
    [InlineData("my.game.bin", "my.game.bin")]
    public void StripExe_removes_only_trailing_exe_extension(string input, string expected)
    {
        Assert.Equal(expected, ProcessSnapshot.StripExe(input));
    }

    [Theory]
    [InlineData("steam.exe", "steam", true)]
    [InlineData("steam.exe", "STEAM.EXE", true)]
    [InlineData("steam", "steam.exe", true)]
    [InlineData("steam.exe", "epic", false)]
    [InlineData("steam.exe", "", false)]
    public void NameEquals_compares_stems_case_insensitively(string left, string right, bool expected)
    {
        Assert.Equal(expected, ProcessSnapshot.NameEquals(left, right));
    }

    [Fact]
    public void IsRunning_PidsByName_and_Running_match_current_process_with_or_without_exe()
    {
        using var self = Process.GetCurrentProcess();
        var stem = self.ProcessName;

        Assert.True(ProcessSnapshot.IsRunning(stem));
        Assert.True(ProcessSnapshot.IsRunning(stem + ".exe"));
        Assert.False(ProcessSnapshot.IsRunning("__nonexistent_exo_process_999__"));

        var pids = ProcessSnapshot.PidsByName(stem + ".EXE");
        Assert.Contains(self.Id, pids);

        var setWithExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stem + ".exe" };
        var hits = ProcessSnapshot.Running(setWithExe);
        Assert.Contains(hits, h => h.Pid == self.Id && h.Name.Equals(stem, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadCached_and_InvalidateCache_return_current_process_safely()
    {
        ProcessSnapshot.InvalidateCache();
        var first = ProcessSnapshot.ReadCached(TimeSpan.FromSeconds(2));
        var second = ProcessSnapshot.ReadCached(TimeSpan.FromSeconds(2));

        Assert.Contains(first, r => r.Pid == Environment.ProcessId);
        Assert.Same(first, second);

        ProcessSnapshot.InvalidateCache();
        var refreshed = ProcessSnapshot.ReadCached(TimeSpan.FromSeconds(2));
        Assert.NotSame(first, refreshed);
        Assert.Contains(refreshed, r => r.Pid == Environment.ProcessId);
    }
}

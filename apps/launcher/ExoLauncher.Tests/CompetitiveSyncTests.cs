using System.Text.Json;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways taking G-SYNC per game (CompetitiveSync.cs) away could go wrong, silently: a game left with the cap and V-Sync Exo
/// set in its own profile; a value the driver refused forgotten, so it never goes back; the record kept once it is done, or
/// a broken record stopping Exo's start.
/// </summary>
public sealed class CompetitiveSyncTests
{
    private static string Record(string text)
    {
        var folder = Path.Combine(TestDataDirectory.Root, "sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "sync.json");
        File.WriteAllText(path, text);
        return path;
    }

    private static readonly string TwoGames = JsonSerializer.Serialize(new Dictionary<string, CompetitiveSync.Uncapped>
    {
        ["steam:1"] = new(@"C:\Games\a.exe", new() { [0x10835002] = null, [0x00A879CF] = 5u }),
        ["steam:730"] = new(@"C:\Games\b.exe", new() { [0x10835002] = 141u }),
    });

    [Fact]
    public void EachGameGoesBackToWhatItHeldAndTheRecordGoes()
    {
        var before = CompetitiveSync.StatePath;
        try
        {
            CompetitiveSync.StatePath = Record(TwoGames);
            var back = new Dictionary<string, CompetitiveSync.Uncapped>();
            CompetitiveSync.Restore((id, game) => { back[id] = game; return true; });
            Assert.Equal(@"C:\Games\a.exe", back["steam:1"].Exe);
            Assert.Null(back["steam:1"].Before[0x10835002]);
            Assert.Equal(5u, back["steam:1"].Before[0x00A879CF]);
            Assert.Equal(141u, back["steam:730"].Before[0x10835002]);
            Assert.False(File.Exists(CompetitiveSync.StatePath));
        }
        finally { CompetitiveSync.StatePath = before; }
    }

    [Fact]
    public void WhatTheDriverRefusedStaysForTheNextStart()
    {
        var before = CompetitiveSync.StatePath;
        try
        {
            CompetitiveSync.StatePath = Record(TwoGames);
            CompetitiveSync.Restore((id, _) => id == "steam:730");
            var left = new List<string>();
            CompetitiveSync.Restore((id, _) => { left.Add(id); return true; });
            Assert.Equal(["steam:1"], left);
            Assert.False(File.Exists(CompetitiveSync.StatePath));
        }
        finally { CompetitiveSync.StatePath = before; }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void AnEmptyOrBrokenRecordJustGoes(string text)
    {
        var before = CompetitiveSync.StatePath;
        try
        {
            CompetitiveSync.StatePath = Record(text);
            CompetitiveSync.Restore((_, _) => throw new InvalidOperationException("nothing to put back"));
            Assert.False(File.Exists(CompetitiveSync.StatePath));
        }
        finally { CompetitiveSync.StatePath = before; }
    }
}

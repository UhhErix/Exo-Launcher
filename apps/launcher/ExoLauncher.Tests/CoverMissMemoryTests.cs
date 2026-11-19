using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// CoverMissMemory: the 24-hour per-game pause on art searches. Ways it could fail silently: a miss suppresses past 24
/// hours; a corrupt file throws; a saved miss is lost on restart; "Find cover" (Forget) leaves the game suppressed.
/// </summary>
public sealed class CoverMissMemoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-cover-miss-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) DeletionGate.DeleteDirectory(_dir, recursive: true);
    }

    private string Path_ => Path.Combine(_dir, "cover-misses.json");

    [Fact]
    public void AMissIsNotAskedAgainWithinADay()
    {
        var memory = new CoverMissMemory(Path_);
        Assert.False(memory.Suppressed("local:osu", T0));
        memory.Remember("local:osu", T0);
        Assert.True(memory.Suppressed("local:osu", T0.AddHours(23)));
        Assert.False(memory.Suppressed("local:osu", T0.AddHours(24)));
    }

    [Fact]
    public void AMissSurvivesARestart()
    {
        new CoverMissMemory(Path_).Remember("local:osu", T0);
        Assert.True(new CoverMissMemory(Path_).Suppressed("local:osu", T0.AddHours(1)));
    }

    [Fact]
    public void FindCoverForgetsTheGameAtOnce()
    {
        var memory = new CoverMissMemory(Path_);
        memory.Remember("local:osu", T0);
        memory.Forget("local:osu");
        Assert.False(memory.Suppressed("local:osu", T0.AddMinutes(1)));
        Assert.False(new CoverMissMemory(Path_).Suppressed("local:osu", T0.AddMinutes(1)));
    }

    [Fact]
    public void AnotherGameIsNotSuppressed()
    {
        var memory = new CoverMissMemory(Path_);
        memory.Remember("local:osu", T0);
        Assert.False(memory.Suppressed("steam:1145360", T0.AddHours(1)));
    }

    [Fact]
    public void ACorruptFileReadsAsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{not json");
        var memory = new CoverMissMemory(Path_);
        Assert.False(memory.Suppressed("local:osu", T0));
        memory.Remember("local:osu", T0); // and the next save replaces it
        Assert.True(new CoverMissMemory(Path_).Suppressed("local:osu", T0.AddHours(1)));
    }
}

using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a per-game NVIDIA profile could fail where the global one carries on:
/// - one setting the driver does not have fails the whole game's profile, so the DLSS preset it does have is never written;
/// - a refusal is taken for a write, so the group reads on with a value the driver never took;
/// - every setting skipped is reported as done, when the driver does not have them at all;
/// - any other refusal is skipped too, and a profile that failed reads as applied.
/// Pure, with a fake driver answer: no driver on this PC is touched.
/// </summary>
public sealed class NvidiaGameWriteTests
{
    private const uint SrPreset = 0x10E41DF3, FgPreset = 0x10E41DF1;

    private static GameSettings.NvidiaValue Value(uint id, uint? value) => new(id, value, $"Setting 0x{id:X8}", value is { } v ? $"0x{v:X}" : "default");

    private static GameSettings.Profile Game() => new("t", "T", "", _ => [], [], Nvidia: new("", _ => []), Program: _ => @"C:\Games\T\t.exe");

    private static GameEntry Entry() => new() { Id = "local:nvidia-test", Title = "T", Store = StoreKind.Local, Installed = true, Path = @"C:\Games\T" };

    private static GameSettings.SettingsFile AFile() => GameSettings.Parse(Encoding.UTF8.GetBytes("A=1\n"));

    /// <summary>A fake driver answering one game's profile, and what it is asked.</summary>
    private sealed class Fake
    {
        internal Dictionary<uint, uint?> Held { get; } = [];
        internal List<uint> Skipped { get; } = [];
        internal IReadOnlyDictionary<uint, uint?>? LastWrites { get; private set; }
        internal bool Fails { get; set; }
        internal string? Failure { get; set; } = "The NVIDIA driver could not change the game's setting.";

        internal NvidiaProfiles.GameState Answer(IReadOnlyCollection<uint> ids, IReadOnlyDictionary<uint, uint?>? writes)
        {
            LastWrites = writes;
            if (Fails)
                return new NvidiaProfiles.GameState(false, Failure, null, new Dictionary<uint, uint?>(), new Dictionary<uint, uint?>(), []);
            var after = ids.ToDictionary(id => id, id => Held.GetValueOrDefault(id));
            return new NvidiaProfiles.GameState(true, null, "Exo: T", new Dictionary<uint, uint?>(), after, Skipped);
        }
    }

    /// <summary>Runs the test's flow with the fake answering every game's profile, and a game-settings folder of its own.</summary>
    private static IDisposable Rig(Fake fake, out string folder)
    {
        var previous = NvidiaProfiles.ForGameSeam;
        NvidiaProfiles.ForGameSeam = (_, ids, writes) => fake.Answer(ids, writes);
        var made = Path.Combine(Path.GetTempPath(), "exo-nvidia-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(made);
        folder = made;
        return new Seam(() =>
        {
            NvidiaProfiles.ForGameSeam = previous;
            try { DeletionGate.DeleteDirectory(made, recursive: true); } catch (IOException) { }
        });
    }

    private sealed class Seam(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public void APerGameSettingTheDriverRefusesIsSkippedAndTheRestIsWritten()
    {
        // The pure decision first: the two statuses a driver answers for a setting it does not have are skipped, nothing else.
        Assert.Equal(NvidiaProfiles.GameWrite.Write, NvidiaProfiles.Decide(0));
        Assert.Equal(NvidiaProfiles.GameWrite.Skip, NvidiaProfiles.Decide(-104)); // NVAPI_NOT_SUPPORTED
        Assert.Equal(NvidiaProfiles.GameWrite.Skip, NvidiaProfiles.Decide(-160)); // NVAPI_SETTING_NOT_FOUND
        Assert.Equal(NvidiaProfiles.GameWrite.Fail, NvidiaProfiles.Decide(-5));
        Assert.Equal(NvidiaProfiles.GameWrite.Fail, NvidiaProfiles.Decide(-137));
        Assert.Equal(NvidiaProfiles.GameWrite.Fail, NvidiaProfiles.Decide(-166));

        // The group: one of the two settings skipped, the other written and held, reads on.
        var fake = new Fake();
        fake.Skipped.Add(FgPreset);
        fake.Held[SrPreset] = 11;
        using var rig = Rig(fake, out _);
        var state = NvidiaProfiles.ForGameLive(
            new NvidiaProfiles.GameProgram(@"C:\Games\T\t.exe", "T", "", (name, _) => name.Contains("T", StringComparison.Ordinal)),
            [SrPreset, FgPreset], new Dictionary<uint, uint?> { [SrPreset] = 11, [FgPreset] = 1 });
        Assert.True(state.Ok);
        Assert.Equal([FgPreset], state.Skipped);
        Assert.Equal(11u, state.After.GetValueOrDefault(SrPreset));
        Assert.Null(state.After.GetValueOrDefault(FgPreset));
    }

    [Fact]
    public void AGameWhoseEverySettingIsSkippedSaysSo()
    {
        // Every setting skipped: not done, and the sentence says why in the player's words.
        var fake = new Fake();
        fake.Skipped.Add(SrPreset);
        fake.Skipped.Add(FgPreset);
        using var rig = Rig(fake, out _);
        var game = Game();
        Assert.Equal("This driver doesn't have these settings.", GameSettings.SetNvidia(Entry(), game with { Nvidia = new("", _ => [Value(SrPreset, 11)]) }, AFile(), on: true));
        Assert.Equal("This driver doesn't have these settings.", GameSettings.SetNvidia(Entry(), game with { Nvidia = new("", _ => [Value(SrPreset, 11), Value(FgPreset, 1)]) }, AFile(), on: true));

        // One of the two taken is a set: no sentence, and the write went in for both (the driver skipped one).
        fake.Skipped.Remove(SrPreset);
        fake.Held[SrPreset] = 11;
        Assert.Null(GameSettings.SetNvidia(Entry(), game with { Nvidia = new("", _ => [Value(SrPreset, 11), Value(FgPreset, 1)]) }, AFile(), on: true));
        Assert.Equal(2, fake.LastWrites!.Count);

        // Putting back (off) when the driver skips everything is not "this driver doesn't have these settings": nothing is wanted.
        Assert.Null(GameSettings.SetNvidia(Entry(), game with { Nvidia = new("", _ => [Value(SrPreset, 11), Value(FgPreset, 1)]) }, AFile(), on: false));
    }

    [Fact]
    public void AnyOtherDriverFailureStillFailsTheWholeProfile()
    {
        var fake = new Fake { Fails = true };
        using var rig = Rig(fake, out var folder);
        var game = Game() with { Nvidia = new("", _ => [Value(SrPreset, 11), Value(FgPreset, 1)]) };

        // A refusal that is not "this driver doesn't have it" fails the set, as it always did, and keeps the driver's sentence.
        Assert.Equal("The NVIDIA driver could not change the game's setting.", GameSettings.SetNvidia(Entry(), game, AFile(), on: true));

        // And no backup is kept for a profile that did not take: Optimize never reads it as set.
        Assert.False(File.Exists(Path.Combine(folder, "t.nvidia.json")));
    }

    [Fact]
    public void OptimizeAppliesPerGamePreferMaximumPerformance()
    {
        var fake = new Fake();
        fake.Held[GpuProfile.PowerManagement] = 1u;
        fake.Held[SrPreset] = 11u;
        using var rig = Rig(fake, out _);
        var game = Game() with { Nvidia = new("", _ => [Value(SrPreset, 11)]) };
        Assert.Null(GameSettings.SetNvidia(Entry(), game, AFile(), on: true, includePerGamePower: true));
        Assert.Equal(1u, fake.LastWrites![GpuProfile.PowerManagement]);
        Assert.Equal(11u, fake.LastWrites[SrPreset]);
    }
}


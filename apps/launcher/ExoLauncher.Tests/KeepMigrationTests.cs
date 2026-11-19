using ExoLauncher.Helpers;
using ExoLauncher.Services;
using static ExoLauncher.Tests.KeepKit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways bringing an older Exo's files into the one record could fail:
/// - what the old Exo changed is lost, so Put back gives the game's defaults and not the player's own;
/// - the old files are changed or removed, so the older Exo can no longer undo;
/// - a migrated value can never be taken back by the group it belongs to, because the old files don't name the group;
/// - the record is migrated over while another program holds it, and what that program holds is lost;
/// - the first look with the game's file reads an edit the older Exo made as the game rebuilding the file.
/// </summary>
public sealed class KeepMigrationTests
{
    private const string Vsync = "VSync@0;9203;8071";

    /// <summary>Modern Warfare 4, fresh: the first look after this brings its old files in.</summary>
    private static KeepKit.Game FreshGame() => Fresh();

    /// <summary>A record path of the test's own, so nothing else in the run shares what it lands in.</summary>
    private static IDisposable OwnRecord() => Keep.ForcePathForTest(Path.Combine(NewFolder(), "keep.json"));

    /// <summary>
    /// The old Exo's files for this game: kept.json's line (what it held before, what Exo set) and {id}.on.json's groups and
    /// the lines that are the player's, in the shape 3.17 and 3.18 wrote them.
    /// </summary>
    private static void OldFiles(string profileId, string was, string set, bool theirs)
    {
        var folder = Path.Combine(PathHelper.AppDataDir, "game-settings");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "kept.json"),
            """{"PID":{"VSYNC":{"Was":"WAS","Set":"SET"}}}""".Replace("PID", profileId).Replace("VSYNC", Vsync).Replace("WAS", was).Replace("SET", set));
        File.WriteAllText(Path.Combine(folder, profileId + ".on.json"),
            """{"Groups":["latency"],"Theirs":THEIRS,"Marks":{},"Program":null,"Built":null,"Again":null}"""
                .Replace("THEIRS", theirs ? """["VSYNC"]""".Replace("VSYNC", Vsync) : "[]"));
    }

    [Fact]
    public void AnOlderExosFilesBecomeTheRecordAndPutBackRestoresTheOriginal()
    {
        var game = FreshGame();
        using var _ = OwnRecord();
        var original = File.ReadAllText(game.Main);
        OldFiles(game.Profile.Id, was: "100%", set: "disabled", theirs: false);
        Rewrite(game.Main, ("VSync@0;9203;8071 = 100%", "VSync@0;9203;8071 = disabled"));

        // The first look brings the old files in, fills in the group they don't name, and writes nothing.
        Assert.False(GameSettings.KeepOn(game.Profile, game.Program, afterSession: false));
        var record = Keep.Load().Games[game.Profile.Id];
        Assert.True(record.Migrated);
        Assert.Equal("100%", record.Entries[Vsync].Before);
        Assert.Equal("disabled", record.Entries[Vsync].Intended);
        Assert.Equal(game.Profile.Id + "/latency", record.Entries[Vsync].Owner);
        // The file the older Exo wrote is not read as one the game rebuilt: its shape was taken as this look found it.
        Assert.True(record.Shapes.ContainsKey(""));

        // Put back gives what the line held before Exo, byte for byte.
        Assert.True(GameSettings.Set(game.Profile, null, false, game.Program));
        Assert.Equal(original, File.ReadAllText(game.Main));
    }

    [Fact]
    public void TheOldFilesAreLeftInPlace()
    {
        var game = FreshGame();
        using var _ = OwnRecord();
        OldFiles(game.Profile.Id, was: "100%", set: "disabled", theirs: false);
        Rewrite(game.Main, ("VSync@0;9203;8071 = 100%", "VSync@0;9203;8071 = disabled"));
        var folder = Path.Combine(PathHelper.AppDataDir, "game-settings");
        var keptPath = Path.Combine(folder, "kept.json");
        var onPath = Path.Combine(folder, game.Profile.Id + ".on.json");
        var kept = File.ReadAllBytes(keptPath);
        var on = File.ReadAllBytes(onPath);

        Assert.False(GameSettings.KeepOn(game.Profile, game.Program, afterSession: false));
        Assert.True(GameSettings.Set(game.Profile, null, false, game.Program));

        // The older Exo can still read its own undo after this one took the record over.
        Assert.Equal(kept, File.ReadAllBytes(keptPath));
        Assert.Equal(on, File.ReadAllBytes(onPath));
    }

    [Fact]
    public void AMigratedValueCanBeTakenBackByPressingItsGroup()
    {
        var game = FreshGame();
        using var _ = OwnRecord();
        OldFiles(game.Profile.Id, was: "100%", set: "disabled", theirs: true);
        // The player changed it in the game, so the old Exo left it as theirs.
        Rewrite(game.Main, ("VSync@0;9203;8071 = 100%", "VSync@0;9203;8071 = 50%"));

        // The look fills in the group the old files could not name; nothing is written over the player's value.
        Assert.False(GameSettings.KeepOn(game.Profile, game.Program, afterSession: false));
        var entry = Keep.Load().Games[game.Profile.Id].Entries[Vsync];
        Assert.Equal(KeepState.Yielded, entry.State);
        Assert.Equal(game.Profile.Id + "/latency", entry.Owner);
        Assert.Contains("VSync@0;9203;8071 = 50%", File.ReadAllText(game.Main), StringComparison.Ordinal);

        // Pressing its group takes it back.
        Assert.True(GameSettings.Set(game.Profile, "latency", true, game.Program));
        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(game.Main), StringComparison.Ordinal);
        Assert.Equal(KeepState.Held, Keep.Load().Games[game.Profile.Id].Entries[Vsync].State);
    }

    [Fact]
    public void ARecordLockedForAMomentIsNotReplacedByTheOldFiles()
    {
        var game = FreshGame();
        using var _ = OwnRecord();
        OldFiles(game.Profile.Id, was: "100%", set: "disabled", theirs: false);
        Rewrite(game.Main, ("VSync@0;9203;8071 = 100%", "VSync@0;9203;8071 = disabled"));
        var path = Keep.Path;
        // The player's own record is there already, and differs from the old files: this line did not hold 100%.
        Assert.True(Keep.Update(doc => doc.Games[game.Profile.Id] = new Keep.KeepGame
        {
            Entries = { [Vsync] = new Keep.KeepEntry { Target = Vsync, Intended = "50%", Before = "25%" } },
            Keeping = new Keep.Keeping { Groups = ["latency"] },
        }));

        // Another program holds the record for the first read only: the game then looks absent, and the old files are ready.
        using (Keep.FailReadsForTest(1))
            Assert.Equal("25%", GameSettings.Kept(game.Profile.Id)[Vsync].Was);

        // The one read that decided was the one the write used, so the record's own undo is what is held, not the old files'.
        Assert.Equal("25%", Keep.Load().Games[game.Profile.Id].Entries[Vsync].Before);
        Assert.Equal("50%", Keep.Load().Games[game.Profile.Id].Entries[Vsync].Intended);
        Assert.False(Keep.Load().Games[game.Profile.Id].Migrated);
    }

    [Fact]
    public void ALockedRecordIsNotMigratedOver()
    {
        var game = FreshGame();
        using var _ = OwnRecord();
        OldFiles(game.Profile.Id, was: "100%", set: "disabled", theirs: false);
        Rewrite(game.Main, ("VSync@0;9203;8071 = 100%", "VSync@0;9203;8071 = disabled"));
        var path = Keep.Path;
        Assert.True(Keep.Update(doc => doc.Games["other"] = new Keep.KeepGame()));
        var before = File.ReadAllBytes(path);

        // Another program holds the record: what it holds is unknown, so nothing is brought in and nothing is saved over it.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(GameSettings.Kept(game.Profile.Id));
            Assert.False(Keep.Available());
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(["other"], Keep.Load().Games.Keys.ToArray());
    }
}

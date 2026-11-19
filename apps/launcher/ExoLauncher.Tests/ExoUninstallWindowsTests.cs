using ExoLauncher.Helpers;
using ExoLauncher.Services;
using static ExoLauncher.Tests.WindowsKit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the uninstaller could leave the Windows side behind: Game Mode, Game Bar's values and link handlers stay as Exo wrote
/// them (above all when Exo's own record cannot be saved, and nothing is left to put them back), a program's graphics choice
/// stays, or a choice is left that stops a reinstall from working.
/// </summary>
public sealed class ExoUninstallWindowsTests
{
    private static readonly string GameModeKey = WindowsGameMode.KeyPath;
    private static readonly string GameModeName = WindowsGameMode.ValueName;

    /// <summary>Game Mode as the uninstaller finds it: a fake registry and watch, and the PC's record.</summary>
    private sealed class ModeRig
    {
        internal FakeRegistry Registry { get; } = new();
        internal FakeWatch Watch { get; } = new();
        internal PcRecord Record { get; } = new();
        internal List<string> Log { get; } = [];

        internal WindowsGameMode Mode() => new(Registry, Watch, Record.Load, Record.Save, Log.Add);
    }

    [Fact]
    public void TheUninstallerPutsGameBarBack()
    {
        var rig = WindowsGameBarTests.Rig.Mixed();
        var original = rig.Registry.Snapshot();
        Assert.Null(rig.Bar().Start().Message);
        var folder = Directory.CreateTempSubdirectory("exo-uninstall-").FullName;
        try
        {
            var prefs = new GpuPreference(new FakeStore([]), Path.Combine(folder, "gpu-preference.json"), rig.Record.Load, rig.Record.Save);

            ExoUninstall.GiveBackWindowsSettings(rig.Bar(), prefs, new ModeRig().Mode());

            Assert.Equal(original, rig.Registry.Snapshot());
            // No choice is left behind: a reinstall keeps Game Bar off again.
            Assert.Equal(GameBarState.NotKept, rig.Record.Pc.GameBar!.State);
            Assert.Null(rig.Bar().Start().Message);
            rig.AssertKeptOff();
        }
        finally { DeletionGate.DeleteDirectory(folder, recursive: true); }
    }

    [Fact]
    public void TheUninstallerPutsGameBarBackEvenWhenTheRecordCannotBeSaved()
    {
        var rig = WindowsGameBarTests.Rig.Mixed();
        var original = rig.Registry.Snapshot();
        Assert.Null(rig.Bar().Start().Message);
        Assert.NotEqual(original, rig.Registry.Snapshot());
        // Another program holds keep.json now: nothing can be saved, and once Exo is gone nothing else would put Game Bar back.
        rig.Record.FailSaves = true;
        var folder = Directory.CreateTempSubdirectory("exo-uninstall-").FullName;
        try
        {
            var prefs = new GpuPreference(new FakeStore([]), Path.Combine(folder, "gpu-preference.json"), rig.Record.Load, rig.Record.Save);

            ExoUninstall.GiveBackWindowsSettings(rig.Bar(), prefs, new ModeRig().Mode());

            Assert.Equal(original, rig.Registry.Snapshot());
        }
        finally { DeletionGate.DeleteDirectory(folder, recursive: true); }

        // The player's own Off is not the uninstaller: when it cannot save, the values still go back and it says what did not happen.
        var again = WindowsGameBarTests.Rig.Mixed();
        var before = again.Registry.Snapshot();
        Assert.Null(again.Bar().Start().Message);
        again.Record.FailSaves = true;
        var off = again.Bar().TurnOff();
        Assert.Equal(before, again.Registry.Snapshot());
        Assert.Equal("Game Bar is back as it was, but Exo couldn't save that you turned its keeping off.", off.Message);
        // The record still says it is kept, so Exo does not claim a choice it could not keep.
        Assert.Equal(GameBarState.Held, again.Record.Pc.GameBar!.State);
    }

    [Fact]
    public void TheUninstallerPutsGameModeBack()
    {
        // The friend's PC: Game Mode was off, Exo turned it on and keeps it.
        var rig = new ModeRig();
        rig.Registry.Put(GameModeKey, GameModeName, RegValue.DWord(0));
        Assert.Null(rig.Mode().Look());
        Assert.Equal(RegValue.DWord(1), rig.Registry.Get(GameModeKey, GameModeName));
        var bar = WindowsGameBarTests.Rig.Mixed();
        var folder = Directory.CreateTempSubdirectory("exo-uninstall-").FullName;
        try
        {
            var prefs = new GpuPreference(new FakeStore([]), Path.Combine(folder, "gpu-preference.json"), bar.Record.Load, bar.Record.Save);

            ExoUninstall.GiveBackWindowsSettings(bar.Bar(), prefs, rig.Mode());

            // What Exo replaced is back, the watch is let go, and no choice is left: a reinstall starts over.
            Assert.Equal(RegValue.DWord(0), rig.Registry.Get(GameModeKey, GameModeName));
            Assert.False(rig.Watch.Running);
            Assert.Equal(GameModeState.NotKept, rig.Mode().State);
        }
        finally { DeletionGate.DeleteDirectory(folder, recursive: true); }

        // Not set before: the value goes, even though the record cannot be saved (nothing else would put it back).
        var unset = new ModeRig();
        Assert.Null(unset.Mode().Look());
        Assert.Equal(RegValue.DWord(1), unset.Registry.Get(GameModeKey, GameModeName));
        unset.Record.FailSaves = true;
        var bar2 = WindowsGameBarTests.Rig.Mixed();
        var folder2 = Directory.CreateTempSubdirectory("exo-uninstall-").FullName;
        try
        {
            var prefs = new GpuPreference(new FakeStore([]), Path.Combine(folder2, "gpu-preference.json"), bar2.Record.Load, bar2.Record.Save);

            ExoUninstall.GiveBackWindowsSettings(bar2.Bar(), prefs, unset.Mode());

            Assert.Null(unset.Registry.Get(GameModeKey, GameModeName));
        }
        finally { DeletionGate.DeleteDirectory(folder2, recursive: true); }

        // Game Mode the player turned off for good is theirs: the uninstaller writes nothing.
        var yielded = new ModeRig();
        yielded.Record.Pc.GameMode = new Keep.GameModeKeep { State = GameModeState.Yielded };
        yielded.Registry.Put(GameModeKey, GameModeName, RegValue.DWord(0));
        Assert.Null(yielded.Mode().GiveBack());
        Assert.Equal(RegValue.DWord(0), yielded.Registry.Get(GameModeKey, GameModeName));
        Assert.Empty(yielded.Registry.Writes);
    }

    private sealed class FakeStore(Dictionary<string, string> values) : GpuPreference.IStore
    {
        public string? Get(string program) => values.GetValueOrDefault(program);
        public void Set(string program, string data) => values[program] = data;
        public void Delete(string program) => values.Remove(program);
    }

}

using ExoLauncher.Helpers;
using ExoLauncher.Services;
using static ExoLauncher.Tests.WindowsKit;
using Class = ExoLauncher.Services.PowerProfiles.CpuClass;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways looking again after a Windows update could go wrong silently:
/// - Windows resets Game Mode, Game Bar, a program's graphics choice or a value of Exo's power plan in an update, and
///   nothing puts it back;
/// - a look writes at every start, not only after an update, or writes at the first run;
/// - something is written under a running game;
/// - the new build is recorded before the looks ran (so an update is never looked at), or never (so every start looks);
/// - a reset by Windows is taken for the player's choice (Game Mode yielded), or a choice the player made (Game Bar turned
///   off in Exo, Game Mode yielded) is written over.
/// </summary>
public sealed class WindowsKeepTests
{
    private const string Old = "26100.4061";
    private const string New = "26300.9711";

    /// <summary>Every function the class takes, faked, with what it was asked and what the record holds.</summary>
    private sealed class Rig
    {
        internal string? Build { get; set; }
        internal bool GameRunning { get; set; }
        internal PcRecord Record { get; } = new();
        internal List<string> Log { get; } = [];
        internal List<string> Looked { get; } = [];
        internal string? PowerAnswer { get; set; }
        internal string? ModeAnswer { get; set; }
        internal string? BarAnswer { get; set; }
        internal string? GpuAnswer { get; set; }

        internal WindowsKeep Keep => new(
            () => Build,
            () => GameRunning,
            () => { Looked.Add("power plan"); return PowerAnswer; },
            () => { Looked.Add("game mode"); return ModeAnswer; },
            () => { Looked.Add("game bar"); return BarAnswer; },
            () => { Looked.Add("graphics"); return GpuAnswer; },
            Record.Load,
            Record.Save,
            line => { lock (Log) Log.Add(line); });
    }

    private static Rig Held() => new() { Build = Old, Record = { Pc = { Windows = Old } } };

    [Fact]
    public void TheFirstRunOnlyNotesTheBuild()
    {
        var rig = new Rig { Build = Old };

        var outcome = rig.Keep.Look("on start");

        Assert.Equal(WindowsOutcome.Noted, outcome);
        Assert.Empty(rig.Looked);
        Assert.Equal(Old, rig.Record.Pc.Windows);
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void TheSameBuildWritesNothing()
    {
        var rig = Held();

        var outcome = rig.Keep.Look("on start");

        Assert.Equal(WindowsOutcome.Same, outcome);
        Assert.Empty(rig.Looked);
        Assert.Equal(0, rig.Record.Saves);
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void NothingIsWrittenWhileAGameRuns()
    {
        var rig = Held();
        rig.Build = New;
        rig.GameRunning = true;

        var outcome = rig.Keep.Look("on start");

        Assert.Equal(WindowsOutcome.Waiting, outcome);
        Assert.Empty(rig.Looked);
        Assert.Equal(Old, rig.Record.Pc.Windows); // not recorded, so the next start looks
        Assert.Empty(rig.Log);

        rig.GameRunning = false;
        Assert.Equal(WindowsOutcome.Looked, rig.Keep.Look("on start"));
    }

    [Fact]
    public void ABuildThatCannotBeReadIsNeverTakenForAnUpdate()
    {
        var rig = Held();
        rig.Build = null;

        Assert.Equal(WindowsOutcome.Unknown, rig.Keep.Look("on start"));
        Assert.Empty(rig.Looked);
        Assert.Equal(Old, rig.Record.Pc.Windows);
    }

    [Fact]
    public void ANewBuildLooksAtEachOnceLogsOneLineAndThenRecordsTheBuild()
    {
        var rig = Held();
        rig.Build = New;
        rig.ModeAnswer = "Game Mode";
        rig.GpuAnswer = "2 graphics choices";
        string? recordedWhenLooking = null;
        var keep = new WindowsKeep(() => rig.Build, () => false,
            () => { recordedWhenLooking ??= rig.Record.Pc.Windows; return null; },
            () => { recordedWhenLooking ??= rig.Record.Pc.Windows; return rig.ModeAnswer; },
            () => null,
            () => rig.GpuAnswer,
            rig.Record.Load, rig.Record.Save, rig.Log.Add);

        var outcome = keep.Look("on start");

        Assert.Equal(WindowsOutcome.Looked, outcome);
        Assert.Equal(Old, recordedWhenLooking);   // the build is recorded after the looks, not before
        Assert.Equal(New, rig.Record.Pc.Windows);
        var line = Assert.Single(rig.Log);
        Assert.Contains(Old, line, StringComparison.Ordinal);
        Assert.Contains(New, line, StringComparison.Ordinal);
        Assert.Contains("Game Mode", line, StringComparison.Ordinal);
        Assert.Contains("2 graphics choices", line, StringComparison.Ordinal);
        // Nothing to put back is said once, plainly.
        var quiet = Held();
        quiet.Build = New;
        quiet.Keep.Look("on start");
        Assert.Equal(["power plan", "game mode", "game bar", "graphics"], quiet.Looked);
        Assert.Contains("in place", Assert.Single(quiet.Log), StringComparison.Ordinal);
        // And the next start, on the same build, looks at nothing.
        quiet.Looked.Clear();
        quiet.Log.Clear();
        Assert.Equal(WindowsOutcome.Same, quiet.Keep.Look("on start"));
        Assert.Empty(quiet.Looked);
        Assert.Empty(quiet.Log);
    }

    [Fact]
    public void ALookThatFailsIsLoggedAndTheBuildIsStillRecorded()
    {
        var rig = Held();
        rig.Build = New;
        var keep = new WindowsKeep(() => rig.Build, () => false,
            () => throw new IOException("locked"), () => "Game Mode", () => null, () => null, rig.Record.Load, rig.Record.Save, rig.Log.Add);

        Assert.Equal(WindowsOutcome.Looked, keep.Look("on start"));

        // The others still ran, and a look is once per build, not every start.
        Assert.Equal(New, rig.Record.Pc.Windows);
        Assert.Contains(rig.Log, line => line.Contains("Game Mode", StringComparison.Ordinal));
        Assert.Contains(rig.Log, line => line.Contains("power plan", StringComparison.OrdinalIgnoreCase) && line.Contains("IOException", StringComparison.Ordinal));
    }

    // ---- With the four looks real, on fakes ----

    private sealed class Pc
    {
        internal PcRecord Record { get; } = new();
        internal FakeRegistry Registry { get; } = new();
        internal FakeWatch Watch { get; } = new();
        internal FakePowerHolder Power { get; } = new();
        internal Dictionary<string, string> Preferences { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Log { get; } = [];
        internal string Program { get; }
        internal WindowsGameMode Mode { get; }
        internal WindowsGameBar Bar { get; }
        internal GpuPreference Gpu { get; }
        internal PowerPlan Plan { get; }
        internal string? Build { get; set; } = Old;
        internal bool GameRunning { get; set; }
        internal static readonly PowerProfiles.Profile Profile = PowerProfiles.For(Class.AmdSingleCcd, laptop: false);

        internal Pc(string folder)
        {
            Program = Path.Combine(folder, "game.exe");
            File.WriteAllText(Program, "");
            Mode = new WindowsGameMode(Registry, Watch, Record.Load, Record.Save, Log.Add);
            Bar = new WindowsGameBar(Registry, Record.Load, Record.Save, Log.Add);
            Gpu = new GpuPreference(new Store(Preferences), Path.Combine(folder, "gpu-preference.json"), Record.Load, Record.Save);
            Plan = new PowerPlan(Power.Api, Path.Combine(folder, "exo-power-plan.json"), Log.Add, () => GameRunning);
        }

        /// <summary>The same wiring Exo uses (<see cref="WindowsKeep.Over"/>), over these fakes.</summary>
        internal WindowsKeep Keep() => WindowsKeep.Over(() => Build, () => GameRunning, Plan, () => Profile, () => false, Mode, Bar, Gpu, Record.Load, Record.Save, Log.Add);

        /// <summary>Exo has held everything at the old build.</summary>
        internal void HoldEverything()
        {
            Registry.Put(WindowsGameMode.KeyPath, WindowsGameMode.ValueName, RegValue.DWord(0));
            Assert.Null(Mode.Look());
            Assert.Null(Bar.Start().Message);
            Assert.Equal(1, Gpu.Use([Program]));
            Power.Api.ActiveScheme = PowerPlan.Balanced;
            Assert.Null(Plan.Auto(Profile));
            Record.Pc.Windows = Old;
        }

        internal sealed class Store(Dictionary<string, string> values) : GpuPreference.IStore
        {
            public string? Get(string program) => values.GetValueOrDefault(program);
            public void Set(string program, string data) => values[program] = data;
            public void Delete(string program) => values.Remove(program);
        }
    }

    /// <summary>The power plan fake of <see cref="PowerPlanTests"/>, held here so a test can reach what it wrote.</summary>
    private sealed class FakePowerHolder
    {
        internal PowerPlanTests.FakePower Api { get; } = new(PowerPlan.Balanced);
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "exo-windowskeep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void AFeatureUpdatePutsBackWhatWindowsReset()
    {
        var folder = Folder();
        try
        {
            var pc = new Pc(folder);
            pc.HoldEverything();
            var exoPlan = pc.Power.Api.Exo!.Value;
            var boost = Pc.Profile.Values.Single(v => v.Name == "Boost mode");
            // The update: Game Mode off, Game Bar's recording back on, the graphics choice gone, a power plan value changed.
            pc.Registry.Put(WindowsGameMode.KeyPath, WindowsGameMode.ValueName, RegValue.DWord(0));
            pc.Registry.Put(WindowsGameBar.Dwords[0].Key, WindowsGameBar.Dwords[0].Name, RegValue.DWord(1));
            pc.Preferences.Clear();
            pc.Power.Api.Written[(exoPlan, boost.Setting, false)] = 0;
            pc.Build = New;
            pc.Log.Clear();

            Assert.Equal(WindowsOutcome.Looked, pc.Keep().Look("on start"));

            Assert.Equal(RegValue.DWord(1), pc.Registry.Get(WindowsGameMode.KeyPath, WindowsGameMode.ValueName));
            // A reset by an update is Windows, not the player: Game Mode stays held, not yielded.
            Assert.Equal(GameModeState.Held, pc.Mode.State);
            Assert.Equal(RegValue.DWord(0), pc.Registry.Get(WindowsGameBar.Dwords[0].Key, WindowsGameBar.Dwords[0].Name));
            Assert.Equal("GpuPreference=2;", pc.Preferences[pc.Program]);
            Assert.Equal(boost.Ac, pc.Power.Api.Written[(exoPlan, boost.Setting, false)]);
            Assert.Equal(New, pc.Record.Pc.Windows);
            // One line from the look itself, naming what was put back.
            var line = Assert.Single(pc.Log, l => l.StartsWith("Windows:", StringComparison.Ordinal));
            Assert.Contains("Game Mode", line, StringComparison.Ordinal);
            Assert.Contains("Game Bar", line, StringComparison.Ordinal);
            Assert.Contains("graphics choice", line, StringComparison.Ordinal);
            Assert.Contains("Boost mode", line, StringComparison.Ordinal);
        }
        finally { try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void AGpuPreferenceIsRecheckedAfterAFeatureUpdate()
    {
        var folder = Folder();
        try
        {
            var pc = new Pc(folder);
            pc.HoldEverything();
            pc.Preferences[pc.Program] = "GpuPreference=0;"; // Windows reset it to "let Windows decide"
            pc.Build = New;

            Assert.Equal(WindowsOutcome.Looked, pc.Keep().Look("on start"));

            Assert.Equal("GpuPreference=2;", pc.Preferences[pc.Program]);
            // And what it held before Exo's first write is still what Undo puts back.
            Assert.Equal(1, pc.Gpu.Undo());
            Assert.False(pc.Preferences.ContainsKey(pc.Program));
        }
        finally { try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void WhatThePlayerChoseIsNotWrittenOverAfterAFeatureUpdate()
    {
        var folder = Folder();
        try
        {
            var pc = new Pc(folder);
            pc.HoldEverything();
            // The player turned Game Bar's keeping off in Exo, and gave Game Mode to themselves.
            Assert.Null(pc.Bar.TurnOff().Message);
            pc.Registry.Put(WindowsGameMode.KeyPath, WindowsGameMode.ValueName, RegValue.DWord(0));
            pc.Watch.Fire();
            pc.Registry.Put(WindowsGameMode.KeyPath, WindowsGameMode.ValueName, RegValue.DWord(0));
            pc.Watch.Fire();
            Assert.Equal(GameModeState.Yielded, pc.Mode.State);
            var original = pc.Registry.Snapshot();
            pc.Build = New;

            Assert.Equal(WindowsOutcome.Looked, pc.Keep().Look("on start"));

            Assert.Equal(original, pc.Registry.Snapshot());
        }
        finally { try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void NothingOfThePcIsWrittenOnTheSameBuildOrUnderAGame()
    {
        var folder = Folder();
        try
        {
            var pc = new Pc(folder);
            pc.HoldEverything();
            var writes = pc.Registry.Writes.Count;
            Assert.Equal(WindowsOutcome.Same, pc.Keep().Look("on start"));
            pc.Build = New;
            pc.GameRunning = true;
            Assert.Equal(WindowsOutcome.Waiting, pc.Keep().Look("on start"));
            Assert.Equal(writes, pc.Registry.Writes.Count);
            Assert.Equal(Old, pc.Record.Pc.Windows);
        }
        finally { try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch (IOException) { } }
    }

    // ---- The table ----

    [Fact]
    public void EveryRowOfTheWindowsTableIsWalked()
    {
        var rows = WindowsKeep.Table;
        Assert.Equal(Enum.GetValues<WindowsSituation>().Length, rows.Count);
        Assert.Equal(rows.Count, rows.Select(row => row.Situation).Distinct().Count());
        foreach (var row in rows) Assert.Same(row, WindowsKeep.Row(row.Situation));

        foreach (var row in rows)
        {
            var rig = Walk(row.Situation);
            var outcome = rig.Keep.Look("walking");

            Assert.Equal(row.Outcome, outcome);
            Assert.Equal(row.Step.HasFlag(WindowsStep.LookAtAll) ? 4 : 0, rig.Looked.Count);
            // The build is recorded by exactly the rows that say so.
            Assert.Equal(row.Step.HasFlag(WindowsStep.NoteBuild) ? 1 : 0, rig.Record.Saves);
            if (row.Step.HasFlag(WindowsStep.NoteBuild)) Assert.Equal(New, rig.Record.Pc.Windows);
            // A row that does nothing says nothing.
            if (row.Step == WindowsStep.None) Assert.Empty(rig.Log);
        }
    }

    private static Rig Walk(WindowsSituation situation)
    {
        var rig = new Rig();
        switch (situation)
        {
            case WindowsSituation.UnknownBuild: rig.Build = null; rig.Record.Pc.Windows = Old; break;
            case WindowsSituation.GameRunning: rig.Build = New; rig.Record.Pc.Windows = Old; rig.GameRunning = true; break;
            case WindowsSituation.NoBuildRecorded: rig.Build = New; break;
            case WindowsSituation.SameBuild: rig.Build = New; rig.Record.Pc.Windows = New; break;
            case WindowsSituation.BuildChanged: rig.Build = New; rig.Record.Pc.Windows = Old; break;
        }
        return rig;
    }
}

using ExoLauncher.Helpers;
using ExoLauncher.Services;
using static ExoLauncher.Tests.WindowsKit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways keeping Windows Game Mode on could go wrong:
/// - Exo fights the player: it turns Game Mode on again however often Settings or a debloat script turns it off;
/// - the first change is taken for the player's, or a Windows update's reset is (a second feature update would then
///   leave Game Mode off for good);
/// - Off leaves the value Exo wrote, or writes something other than what was there (a number, or "not set" deleted);
/// - the watch cannot be stopped, or cannot be started again once stopped;
/// - the player's choice is lost at a restart (it must be kept, not only in memory), or Exo writes over a recorded one;
/// - the first write has no record of what it replaced (the record is saved before the write), or a write that did not
///   take leaves a record of a write that never was;
/// - a disposable host writes the registry.
/// </summary>
public sealed class WindowsGameModeTests
{
    private static readonly string Key = WindowsGameMode.KeyPath;
    private static readonly string Name = WindowsGameMode.ValueName;

    private sealed class Rig
    {
        internal FakeRegistry Registry { get; } = new();
        internal FakeWatch Watch { get; } = new();
        internal PcRecord Record { get; } = new();
        internal List<string> Log { get; } = [];
        internal long Now { get; set; } = 1_000_000;

        internal WindowsGameMode Mode() => new(Registry, Watch, Record.Load, Record.Save, Log.Add, () => Now);

        internal RegValue? Value => Registry.Get(Key, Name);

        /// <summary>Settings, a debloat script or an optimizer writes the value, and the key says so.</summary>
        internal void Another(RegValue? value)
        {
            Registry.Put(Key, Name, value);
            Watch.Fire();
        }
    }

    /// <summary>A PC where Game Mode is off (the friend's PC) and Exo has just looked once.</summary>
    private static (Rig Rig, WindowsGameMode Mode) Held()
    {
        var rig = new Rig();
        rig.Registry.Put(Key, Name, RegValue.DWord(0));
        var mode = rig.Mode();
        Assert.Null(mode.Look());
        return (rig, mode);
    }

    [Fact]
    public void GameModeOffStopsTheWatch()
    {
        var (rig, mode) = Held();
        Assert.True(rig.Watch.Running);
        Assert.Equal(GameModeState.Held, mode.State);

        Assert.Null(mode.TurnOff());

        Assert.False(rig.Watch.Running);
        Assert.Equal(1, rig.Watch.Stops);
        // A write now is nobody's to put back.
        rig.Another(RegValue.DWord(0));
        Assert.Equal(RegValue.DWord(0), rig.Value);
        // And turning it on in Exo starts the watch again, in the same run (the old code never could).
        Assert.Null(mode.TurnOn());
        Assert.True(rig.Watch.Running);
        Assert.Equal(2, rig.Watch.Starts);
        Assert.Equal(RegValue.DWord(1), rig.Value);
    }

    [Theory]
    [InlineData("0")]      // the friend's PC: Settings or a debloat script turned it off
    [InlineData("-1")]     // a number nobody expects, exactly as it was
    [InlineData("text")]   // not a DWord, which Windows doesn't read: it goes back as that string
    [InlineData(null)]     // never set: Off deletes the value
    public void GameModeOffPutsBackExactlyWhatWasThere(string? was)
    {
        RegValue? before = was switch { null => null, "text" => RegValue.Str("1"), _ => RegValue.DWord(int.Parse(was)) };
        var rig = new Rig();
        rig.Registry.Put(Key, Name, before);
        var mode = rig.Mode();

        Assert.Null(mode.Look());
        Assert.Equal(RegValue.DWord(1), rig.Value);
        Assert.Equal(before, rig.Record.Pc.GameMode!.Before);

        Assert.Null(mode.TurnOff());

        Assert.Equal(before, rig.Value);
        Assert.Equal(GameModeState.Off, rig.Record.Pc.GameMode!.State);
        Assert.Null(rig.Record.Pc.GameMode.Before); // forgotten
        if (before is null) Assert.Contains($"delete {Key}|{Name}", rig.Registry.Writes);
    }

    [Fact]
    public void OffAfterAnOlderExoPutsWindowsDefaultBack()
    {
        // An older Exo already turned Game Mode on, so it reads 1 and there is no record. Exo cannot know whether that 1 was the
        // player's or what an older Exo wrote over, so it does not claim "before = 1" (Off would "restore" 1 and report success
        // while Game Mode stayed exactly as Exo's older build left it). It records that before is unknown, which is Windows' own
        // default: Game Mode on, the value not set.
        var rig = new Rig();
        rig.Registry.Put(Key, Name, RegValue.DWord(1));
        var mode = rig.Mode();
        Assert.Null(mode.Look());
        Assert.Equal(GameModeState.Held, mode.State);
        Assert.Null(rig.Record.Pc.GameMode!.Before);
        Assert.Empty(rig.Registry.Writes);

        Assert.Null(mode.TurnOff());

        // The value Exo's older build set is gone, so Windows' default stands; the answer is true because that is what happened.
        Assert.Null(rig.Value);
        Assert.Contains($"delete {Key}|{Name}", rig.Registry.Writes);
        Assert.Equal(GameModeState.Off, rig.Record.Pc.GameMode!.State);
        Assert.Contains(rig.Log, line => line.Contains("Windows' own default", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameModeChangeFromSettingsIsPutBackOnce()
    {
        var (rig, _) = Held();

        rig.Another(RegValue.DWord(0));

        Assert.Equal(RegValue.DWord(1), rig.Value);
        Assert.Equal(GameModeState.PutBackOnce, rig.Record.Pc.GameMode!.State);
        Assert.True(rig.Watch.Running);
        Assert.Contains(rig.Log, line => line.Contains("turned it off", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameModeChangeFromSettingsIsYieldedTheSecondTime()
    {
        var (rig, _) = Held();
        rig.Another(RegValue.DWord(0));
        var writes = rig.Registry.Writes.Count;
        rig.Now += 3_600_000; // an hour later: no fight, a choice

        rig.Another(RegValue.DWord(0));

        Assert.Equal(RegValue.DWord(0), rig.Value);
        Assert.Equal(writes, rig.Registry.Writes.Count);
        Assert.Equal(GameModeState.Yielded, rig.Record.Pc.GameMode!.State);
        Assert.False(rig.Watch.Running);
        Assert.Contains(rig.Log, line => line.Contains("yours", StringComparison.Ordinal));
    }

    [Fact]
    public void AYieldedGameModeStaysYieldedAfterARestart()
    {
        // The real record, so it is the file that remembers, not this process.
        using var record = Keep.RecordForTest(out _);
        var first = new Rig();
        first.Registry.Put(Key, Name, RegValue.DWord(0));
        WindowsGameMode Mode(Rig rig) => new(rig.Registry, rig.Watch, () => Keep.Load().Pc, change => Keep.Update(doc => change(doc.Pc)), rig.Log.Add, () => rig.Now);
        var mode = Mode(first);
        Assert.Null(mode.Look());
        first.Another(RegValue.DWord(0));
        first.Another(RegValue.DWord(0));
        Assert.Equal(GameModeState.Yielded, Keep.Load().Pc.GameMode!.State);

        // Exo starts again: a new process, the same registry, still off.
        var again = new Rig();
        again.Registry.Put(Key, Name, RegValue.DWord(0));
        var restarted = Mode(again);
        Assert.Null(restarted.Look());

        Assert.Equal(RegValue.DWord(0), again.Value);
        Assert.Empty(again.Registry.Writes);
        Assert.Equal(0, again.Watch.Starts);
        Assert.Equal(GameModeState.Yielded, restarted.State);
    }

    [Theory]
    [InlineData("text")]   // a string, which has to come back from the file as a string
    [InlineData("0")]
    [InlineData(null)]     // not set, which has to come back as not set
    public void GameModeOffAfterARestartPutsBackExactlyWhatWasThere(string? was)
    {
        using var record = Keep.RecordForTest(out _);
        RegValue? before = was switch { null => null, "text" => RegValue.Str("1"), _ => RegValue.DWord(int.Parse(was)) };
        var rig = new Rig();
        rig.Registry.Put(Key, Name, before);
        WindowsGameMode Mode() => new(rig.Registry, rig.Watch, () => Keep.Load().Pc, change => Keep.Update(doc => change(doc.Pc)), rig.Log.Add, () => rig.Now);
        Assert.Null(Mode().Look());
        Assert.Equal(RegValue.DWord(1), rig.Value);

        Assert.Null(Mode().TurnOff());

        Assert.Equal(before, rig.Value);
    }

    [Fact]
    public void TurningItOnInExoTakesItBack()
    {
        var (rig, _) = Held();
        rig.Another(RegValue.DWord(0));
        rig.Another(RegValue.DWord(0));
        Assert.Equal(GameModeState.Yielded, rig.Record.Pc.GameMode!.State);
        Assert.Equal(RegValue.DWord(0), rig.Value);

        var mode = rig.Mode();
        Assert.Null(mode.TurnOn());

        Assert.Equal(RegValue.DWord(1), rig.Value);
        Assert.Equal(GameModeState.Held, rig.Record.Pc.GameMode!.State);
        Assert.True(rig.Watch.Running);
        // What it replaced this time is the player's own 0, which Off puts back.
        Assert.Equal(RegValue.DWord(0), rig.Record.Pc.GameMode.Before);
        Assert.Null(mode.TurnOff());
        Assert.Equal(RegValue.DWord(0), rig.Value);
    }

    [Fact]
    public void OffFromYieldedLeavesThePlayersOwnValue()
    {
        var (rig, _) = Held();
        rig.Another(RegValue.DWord(0));
        rig.Another(RegValue.DWord(0));
        var writes = rig.Registry.Writes.Count;

        Assert.Null(rig.Mode().TurnOff());

        // Not "not set": that would turn on what the player turned off.
        Assert.Equal(RegValue.DWord(0), rig.Value);
        Assert.Equal(writes, rig.Registry.Writes.Count);
        Assert.Equal(GameModeState.Off, rig.Record.Pc.GameMode!.State);
    }

    [Fact]
    public void AWindowsUpdatesResetIsNotThePlayersChange()
    {
        var (rig, mode) = Held();
        rig.Another(RegValue.DWord(0)); // a first change: put back once
        Assert.Equal(GameModeState.PutBackOnce, mode.State);

        // The next feature update resets it again: that is Windows, so it is put back and nothing is yielded.
        rig.Registry.Put(Key, Name, RegValue.DWord(0));
        Assert.Null(rig.Mode().Look(windowsUpdated: true));

        Assert.Equal(RegValue.DWord(1), rig.Value);
        Assert.Equal(GameModeState.PutBackOnce, rig.Record.Pc.GameMode!.State);
    }

    [Fact]
    public void ThreeChangesInTenSecondsAreYieldedAtOnce()
    {
        var (rig, mode) = Held();
        rig.Another(RegValue.DWord(0)); // put back once
        rig.Now += 1_000;
        rig.Another(RegValue.DWord(0)); // the player's
        Assert.Equal(GameModeState.Yielded, mode.State);
        // The player turns it on in Exo, and it is turned off again within the same ten seconds: a fight, not a first time.
        rig.Now += 1_000;
        Assert.Null(mode.TurnOn());
        rig.Now += 1_000;
        rig.Another(RegValue.DWord(0));

        Assert.Equal(RegValue.DWord(0), rig.Value);
        Assert.Equal(GameModeState.Yielded, mode.State);
        Assert.False(rig.Watch.Running);
    }

    [Fact]
    public void TheRecordIsSavedBeforeTheFirstWrite()
    {
        var rig = new Rig();
        rig.Registry.Put(Key, Name, RegValue.DWord(0));
        RegValue? seen = RegValue.Str("never looked");
        GameModeState? state = null;
        rig.Registry.BeforeWrite = () =>
        {
            seen = rig.Record.Pc.GameMode?.Before;
            state = rig.Record.Pc.GameMode?.State;
            rig.Registry.BeforeWrite = null;
        };

        Assert.Null(rig.Mode().Look());

        Assert.Equal(RegValue.DWord(0), seen);
        Assert.Equal(GameModeState.Held, state);
    }

    [Fact]
    public void ARecordThatCannotBeSavedWritesNothing()
    {
        var rig = new Rig();
        rig.Registry.Put(Key, Name, RegValue.DWord(0));
        rig.Record.FailSaves = true;

        var said = rig.Mode().Look();

        Assert.Equal("Exo couldn't save what Game Mode was set to, so nothing changed.", said);
        Assert.Empty(rig.Registry.Writes);
        Assert.Equal(RegValue.DWord(0), rig.Value);
        Assert.False(rig.Watch.Running);
    }

    [Fact]
    public void AWriteThatDidNotTakeIsSaidAndLeavesNoRecord()
    {
        var rig = new Rig();
        rig.Registry.Put(Key, Name, RegValue.DWord(0));
        rig.Registry.Ignored.Add((Key, Name));

        var said = rig.Mode().Look();

        Assert.Equal("Windows didn't turn Game Mode on.", said);
        Assert.Equal(GameModeState.NotKept, rig.Mode().State);
        Assert.Null(rig.Record.Pc.GameMode?.Before);
    }

    [Fact]
    public void APutBackThatDidNotTakeIsSaidAndKeepsTheRecord()
    {
        var (rig, mode) = Held();
        rig.Registry.Ignored.Add((Key, Name));

        Assert.Equal("Windows didn't put Game Mode back.", mode.TurnOff());

        // Still held, with what it replaced, so Off can be tried again.
        Assert.Equal(GameModeState.Held, rig.Record.Pc.GameMode!.State);
        Assert.Equal(RegValue.DWord(0), rig.Record.Pc.GameMode.Before);
    }

    [Fact]
    public void ADisposableHostChangesNothing()
    {
        using var forced = HostMode.ForceForTest(true);
        var rig = new Rig();
        rig.Registry.Put(Key, Name, RegValue.DWord(0));
        var mode = rig.Mode();

        Assert.Equal(HostMode.Refusal, mode.Look());
        Assert.Equal(HostMode.Refusal, mode.TurnOn());
        Assert.Equal(HostMode.Refusal, mode.TurnOff());

        Assert.Empty(rig.Registry.Writes);
        Assert.Equal(0, rig.Record.Saves);
        Assert.False(rig.Watch.Running);
    }

    [Fact]
    public void GameModeIsKeptWhenItIsAlreadyOn()
    {
        var rig = new Rig();
        rig.Registry.Put(Key, Name, RegValue.DWord(1));
        var mode = rig.Mode();

        Assert.Null(mode.Look());

        // Nothing to write, but held and watched: a later change is put back once.
        Assert.Empty(rig.Registry.Writes);
        Assert.Equal(GameModeState.Held, mode.State);
        Assert.True(rig.Watch.Running);
        rig.Another(RegValue.DWord(0));
        Assert.Equal(RegValue.DWord(1), rig.Value);
    }

    // ---- The table ----

    [Fact]
    public void EveryRowOfTheGameModeTableIsWalked()
    {
        var states = Enum.GetValues<GameModeState>();
        var events = Enum.GetValues<GameModeEvent>();
        // One row for every pair, each once.
        Assert.Equal(states.Length * events.Length, WindowsGameMode.Table.Count);
        Assert.Equal(WindowsGameMode.Table.Count, WindowsGameMode.Table.Select(row => (row.State, row.Event)).Distinct().Count());
        // No state without an exit: some event leaves it.
        foreach (var state in states)
            Assert.Contains(WindowsGameMode.Table, row => row.State == state && row.Next != state);
        // Every state can be reached.
        foreach (var state in states.Where(s => s != GameModeState.NotKept))
            Assert.Contains(WindowsGameMode.Table, row => row.Next == state);

        foreach (var row in WindowsGameMode.Table)
        {
            Assert.Same(row, WindowsGameMode.Row(row.State, row.Event));

            // The PC as the row's state leaves it: held states watch and recorded what they replaced (a 0).
            var rig = new Rig();
            rig.Registry.Put(Key, Name, RegValue.DWord(0));
            var recorded = row.State is GameModeState.Held or GameModeState.PutBackOnce;
            if (row.State != GameModeState.NotKept) rig.Record.Pc.GameMode = new Keep.GameModeKeep { State = row.State, Before = recorded ? RegValue.DWord(0) : null };
            var mode = rig.Mode();
            if (recorded) rig.Watch.Start(() => { });
            var writes = rig.Registry.Writes.Count;
            rig.Registry.Put(Key, Name, RegValue.DWord(7)); // not on, and not what was recorded

            var said = mode.Deliver(row.Event);

            var label = $"{row.State} on {row.Event}";
            Assert.True(said is null, label + ": " + said);
            Assert.True(mode.State == row.Next, $"{label} goes to {row.Next}, not {mode.State}");
            var wrote = rig.Registry.Writes.Skip(writes).ToList();
            if (row.Action.HasFlag(GameModeAction.TurnOn)) Assert.True(rig.Value == RegValue.DWord(1), label + " turns it on");
            else if (row.Action.HasFlag(GameModeAction.PutBack)) Assert.True(rig.Value == RegValue.DWord(0), label + " puts back the recorded 0");
            else { Assert.True(wrote.Count == 0, label + " writes nothing"); Assert.True(rig.Value == RegValue.DWord(7), label + " leaves the value"); }
            if (row.Action.HasFlag(GameModeAction.Record)) Assert.True(rig.Record.Pc.GameMode?.Before == RegValue.DWord(7), label + " records what it replaced");
            if (row.Action.HasFlag(GameModeAction.RecordDefault)) Assert.True(rig.Record.Pc.GameMode is { State: GameModeState.Held, Before: null }, label + " records that what was there before is unknown: Windows' own default, not set");
            var keepsRecord = row.Next is GameModeState.Held or GameModeState.PutBackOnce;
            if (!keepsRecord) Assert.True(rig.Record.Pc.GameMode?.Before is null, label + " forgets the record");
            var watching = row.Action.HasFlag(GameModeAction.Watch) || (!row.Action.HasFlag(GameModeAction.Unwatch) && recorded);
            Assert.True(rig.Watch.Running == watching, $"{label} {(watching ? "watches" : "does not watch")}");
        }
    }
}

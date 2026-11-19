using ExoLauncher.Helpers;
using ExoLauncher.Services;
using static ExoLauncher.Tests.WindowsKit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways keeping Game Bar off could go wrong:
/// - a value or a link handler is written with no record of what it replaced, so nothing can put it back (the record has
///   to be saved before the first write, and has to hold the value's kind and the handler's whole key);
/// - Off puts back something other than what was there: a value that was not set is left behind, a handler's other values
///   or keys are lost, a string comes back as a number;
/// - Exo turns Game Bar off again at the next start after the player turned it off in Exo;
/// - a value Windows accepted and did not keep is reported as done;
/// - the uninstaller leaves it all behind, or leaves a choice that stops a reinstall from working;
/// - a disposable host writes the registry.
/// </summary>
public sealed class WindowsGameBarTests
{
    private static readonly string DvrKey = WindowsGameBar.Dwords[0].Key;
    private static readonly string StoreKey = WindowsGameBar.Dwords[1].Key;
    private static readonly string BarKey = WindowsGameBar.Dwords[2].Key;
    private static string Handler(string link) => @"Software\Classes\" + link;

    internal sealed class Rig
    {
        internal FakeRegistry Registry { get; } = new();
        internal PcRecord Record { get; } = new();
        internal List<string> Log { get; } = [];

        internal WindowsGameBar Bar() => new(Registry, Record.Load, Record.Save, Log.Add);

        /// <summary>A PC as people have it: one value on, one not set, one a string, one already off; a handler of someone's own, none, and one already as Exo wants it.</summary>
        internal static Rig Mixed()
        {
            var rig = new Rig();
            var registry = rig.Registry;
            registry.Put(DvrKey, "AppCaptureEnabled", RegValue.DWord(1));
            registry.Put(StoreKey, "GameDVR_FSEBehavior", RegValue.DWord(2)); // the key is Windows', with other values; GameDVR_Enabled is not set
            registry.Put(BarKey, "UseNexusForGameBarEnabled", RegValue.Str("yes"));
            registry.Put(BarKey, "GamepadNexusChordEnabled", RegValue.DWord(0));
            var own = new RegTree { Values = [new RegEntry("", RegValue.Str("URL:their own")), new RegEntry("Custom", RegValue.Bin([1, 2, 3]))] };
            own.Keys["shell"] = new RegTree { Keys = { ["open"] = new RegTree { Keys = { ["command"] = new RegTree { Values = [new RegEntry("", RegValue.Str(@"C:\their\gamebar.exe"))] } } } } };
            registry.PutTree(Handler("ms-gamebar"), own);
            var wanted = new RegTree
            {
                Values = [new RegEntry("", RegValue.Str("URL:ms-gamingoverlay")), new RegEntry("URL Protocol", RegValue.Str("")), new RegEntry("NoOpenWith", RegValue.Str(""))],
                Keys = { ["shell"] = new RegTree { Keys = { ["open"] = new RegTree { Keys = { ["command"] = new RegTree { Values = [new RegEntry("", RegValue.Str(WindowsGameBar.Nothing))] } } } } } },
            };
            registry.PutTree(Handler("ms-gamingoverlay"), wanted);
            return rig;
        }

        /// <summary>The PC after Exo kept Game Bar off: what each value and handler should read.</summary>
        internal void AssertKeptOff()
        {
            foreach (var (key, name) in WindowsGameBar.Dwords) Assert.Equal(RegValue.DWord(0), Registry.Get(key, name));
            foreach (var link in WindowsGameBar.Links)
            {
                Assert.Equal(RegValue.Str("URL:" + link), Registry.Get(Handler(link), ""));
                Assert.Equal(RegValue.Str(""), Registry.Get(Handler(link), "URL Protocol"));
                Assert.Equal(RegValue.Str(""), Registry.Get(Handler(link), "NoOpenWith"));
                Assert.Equal(RegValue.Str(WindowsGameBar.Nothing), Registry.Get(Handler(link) + @"\shell\open\command", ""));
            }
        }
    }

    [Fact]
    public void GameBarKeepsWhatWasThereBeforeItsFirstWrite()
    {
        var rig = Rig.Mixed();
        var original = rig.Registry.Snapshot();
        var ownHandler = rig.Registry.ReadTree(Handler("ms-gamebar"))!;
        var wantedHandler = rig.Registry.ReadTree(Handler("ms-gamingoverlay"))!;
        // What the record held when the first write came.
        Keep.GameBarKeep? atFirstWrite = null;
        rig.Registry.BeforeWrite = () =>
        {
            atFirstWrite = rig.Record.Pc.GameBar is { } kept ? new Keep.GameBarKeep { State = kept.State, Values = [.. kept.Values], Handlers = [.. kept.Handlers] } : null;
            rig.Registry.BeforeWrite = null;
        };

        var result = rig.Bar().Start();

        Assert.Null(result.Message);
        rig.AssertKeptOff();
        Assert.NotNull(atFirstWrite);
        Assert.Equal(GameBarState.Held, atFirstWrite!.State);
        Assert.Equal(4, atFirstWrite.Values.Count);
        Assert.Equal(3, atFirstWrite.Handlers.Count);
        var kept = rig.Record.Pc.GameBar!;
        RegValue? Was(string name) => kept.Values.Single(v => v.Name == name).Before;
        Assert.Equal(RegValue.DWord(1), Was("AppCaptureEnabled"));
        Assert.Null(Was("GameDVR_Enabled"));                       // not set
        Assert.Equal(RegValue.Str("yes"), Was("UseNexusForGameBarEnabled")); // a string, as a string
        Assert.Equal(RegValue.DWord(0), Was("GamepadNexusChordEnabled"));     // already as Exo wants it: still what was there
        Assert.True(ownHandler.SameAs(kept.Handlers.Single(h => h.Link == "ms-gamebar").Before));
        Assert.Null(kept.Handlers.Single(h => h.Link == "ms-gamebarservices").Before);
        Assert.True(wantedHandler.SameAs(kept.Handlers.Single(h => h.Link == "ms-gamingoverlay").Before));
        Assert.NotEqual(original, rig.Registry.Snapshot());
    }

    [Fact]
    public void GameBarOffPutsBackEveryValueAndHandlerExactly()
    {
        var rig = Rig.Mixed();
        var original = rig.Registry.Snapshot();
        var bar = rig.Bar();
        Assert.Null(bar.Start().Message);
        Assert.NotEqual(original, rig.Registry.Snapshot());

        var result = bar.TurnOff();

        Assert.Null(result.Message);
        Assert.Equal(original, rig.Registry.Snapshot());
        // Forgotten, and the player's choice kept.
        Assert.Equal(GameBarState.Off, rig.Record.Pc.GameBar!.State);
        Assert.Empty(rig.Record.Pc.GameBar.Values);
        Assert.Empty(rig.Record.Pc.GameBar.Handlers);
    }

    [Fact]
    public void GameBarIsNotWrittenAgainAfterThePlayerTurnedItOff()
    {
        var rig = Rig.Mixed();
        Assert.Null(rig.Bar().Start().Message);
        Assert.Null(rig.Bar().TurnOff().Message);
        var original = rig.Registry.Snapshot();
        var writes = rig.Registry.Writes.Count;

        // Exo starts again, as many times as it likes.
        var again = rig.Bar().Start();

        Assert.Null(again.Message);
        Assert.Equal(0, again.Changed);
        Assert.Equal(writes, rig.Registry.Writes.Count);
        Assert.Equal(original, rig.Registry.Snapshot());

        // Turning it on in Exo starts over: it records what is there now, and keeps it off again.
        Assert.Null(rig.Bar().TurnOn().Message);
        rig.AssertKeptOff();
        Assert.Equal(GameBarState.Held, rig.Record.Pc.GameBar!.State);
        Assert.Equal(7, rig.Record.Pc.GameBar.Values.Count + rig.Record.Pc.GameBar.Handlers.Count);
    }

    [Fact]
    public void AValueThatDidNotTakeIsNamed()
    {
        var rig = Rig.Mixed();
        rig.Registry.Ignored.Add((DvrKey, "AppCaptureEnabled"));
        rig.Registry.Ignored.Add((Handler("ms-gamebar"), ""));

        var result = rig.Bar().Start();

        Assert.Equal("Windows didn't take every Game Bar setting.", result.Message);
        Assert.Equal(["AppCaptureEnabled", "ms-gamebar"], result.DidNotTake.Order().ToArray());
        // The rest took and is counted: two values and one handler (not the value and the handler that were already as wanted).
        Assert.Equal(2 + 1, result.Changed);
        Assert.Contains(rig.Log, line => line.Contains("AppCaptureEnabled", StringComparison.Ordinal) && line.Contains("didn't take", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyWhatIsNotAlreadyAsExoWantsIsWritten()
    {
        var rig = Rig.Mixed();
        Assert.Null(rig.Bar().Start().Message);
        // The value that was already 0 and the handler that was already right are not written.
        Assert.DoesNotContain(rig.Registry.Writes, w => w == $"set {BarKey}|GamepadNexusChordEnabled");
        Assert.DoesNotContain(rig.Registry.Writes, w => w.Contains("ms-gamingoverlay", StringComparison.Ordinal));
        var writes = rig.Registry.Writes.Count;

        // A second start finds it all in place and writes nothing.
        var again = rig.Bar().Start();
        Assert.Equal(0, again.Changed);
        Assert.Equal(writes, rig.Registry.Writes.Count);
    }

    [Fact]
    public void AnOffThatDidNotTakeIsNamedAndKeepsTheRecord()
    {
        var rig = Rig.Mixed();
        var bar = rig.Bar();
        Assert.Null(bar.Start().Message);
        rig.Registry.Ignored.Add((DvrKey, "AppCaptureEnabled"));

        var result = bar.TurnOff();

        Assert.Equal("Windows didn't put every Game Bar setting back.", result.Message);
        Assert.Contains("AppCaptureEnabled", result.DidNotTake);
        // Still held, with what it replaced, so Off can be tried again.
        Assert.Equal(GameBarState.Held, rig.Record.Pc.GameBar!.State);
        Assert.Equal(7, rig.Record.Pc.GameBar.Values.Count + rig.Record.Pc.GameBar.Handlers.Count);
    }

    [Fact]
    public void AHandlerTooBigToKeepIsLeftAlone()
    {
        var rig = Rig.Mixed();
        rig.Registry.Unreadable.Add(Handler("ms-gamebar"));
        var before = rig.Registry.ReadTree(Handler("ms-gamingoverlay"));

        var result = rig.Bar().Start();

        // Nothing that cannot be recorded is written; it is named, the rest is done.
        Assert.Equal("Windows didn't take every Game Bar setting.", result.Message);
        Assert.Contains("ms-gamebar", result.DidNotTake);
        Assert.DoesNotContain(rig.Registry.Writes, w => w.Contains(@"Classes\ms-gamebar", StringComparison.OrdinalIgnoreCase) && !w.Contains("ms-gamebarservices", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RegValue.Str("URL:their own"), rig.Registry.Get(Handler("ms-gamebar"), ""));
        Assert.True(before!.SameAs(rig.Registry.ReadTree(Handler("ms-gamingoverlay"))));
    }

    [Fact]
    public void GameBarOffAfterARestartPutsBackExactlyWhatWasThere()
    {
        // The real record, on disk: a value's kind, a binary value and a handler's whole key must come back from the file as they went in.
        using var record = Keep.RecordForTest(out _);
        var rig = Rig.Mixed();
        var original = rig.Registry.Snapshot();
        WindowsGameBar Bar() => new(rig.Registry, () => Keep.Load().Pc, change => Keep.Update(doc => change(doc.Pc)), rig.Log.Add);
        Assert.Null(Bar().Start().Message);
        rig.AssertKeptOff();

        // Exo starts again: a new instance, with only the file to go by.
        var result = Bar().TurnOff();

        Assert.Null(result.Message);
        Assert.Equal(original, rig.Registry.Snapshot());
    }

    [Fact]
    public void ARecordThatCannotBeSavedWritesNothing()
    {
        var rig = Rig.Mixed();
        rig.Record.FailSaves = true;
        var original = rig.Registry.Snapshot();

        var result = rig.Bar().Start();

        Assert.Equal("Exo couldn't save what Game Bar was set to, so nothing changed.", result.Message);
        Assert.Equal(original, rig.Registry.Snapshot());
        Assert.Empty(rig.Registry.Writes);
    }

    [Fact]
    public void ADisposableHostChangesNothing()
    {
        using var forced = HostMode.ForceForTest(true);
        var rig = Rig.Mixed();
        var original = rig.Registry.Snapshot();
        var bar = rig.Bar();

        Assert.Equal(HostMode.Refusal, bar.Start().Message);
        Assert.Equal(HostMode.Refusal, bar.TurnOn().Message);
        Assert.Equal(HostMode.Refusal, bar.TurnOff().Message);

        Assert.Equal(original, rig.Registry.Snapshot());
        Assert.Equal(0, rig.Record.Saves);
    }

    // ---- The table ----

    [Fact]
    public void EveryRowOfTheGameBarTableIsWalked()
    {
        var states = Enum.GetValues<GameBarState>();
        var events = Enum.GetValues<GameBarEvent>();
        Assert.Equal(states.Length * events.Length, WindowsGameBar.Table.Count);
        Assert.Equal(WindowsGameBar.Table.Count, WindowsGameBar.Table.Select(row => (row.State, row.Event)).Distinct().Count());
        foreach (var state in states) Assert.Contains(WindowsGameBar.Table, row => row.State == state && row.Next != state);
        foreach (var state in states.Where(s => s != GameBarState.NotKept)) Assert.Contains(WindowsGameBar.Table, row => row.Next == state);

        foreach (var row in WindowsGameBar.Table)
        {
            Assert.Same(row, WindowsGameBar.Row(row.State, row.Event));
            var rig = Rig.Mixed();
            var original = rig.Registry.Snapshot();
            // A state with a record has held the PC as it is now.
            if (row.State == GameBarState.Held)
            {
                Assert.Null(rig.Bar().Start().Message);
                rig.Registry.Put(DvrKey, "AppCaptureEnabled", RegValue.DWord(1)); // Windows turned one back on
            }
            else if (row.State == GameBarState.Off) rig.Record.Pc.GameBar = new Keep.GameBarKeep { State = GameBarState.Off };
            var writes = rig.Registry.Writes.Count;
            var bar = rig.Bar();

            var result = bar.Deliver(row.Event);

            var label = $"{row.State} on {row.Event}";
            Assert.True(result.Message is null, label + ": " + result.Message);
            Assert.True(bar.State == row.Next, $"{label} goes to {row.Next}, not {bar.State}");
            var wrote = rig.Registry.Writes.Count > writes;
            if (row.Action.HasFlag(GameBarAction.PutBack))
            {
                Assert.True(wrote, label + " puts back");
                Assert.True(rig.Registry.Snapshot() == original, label + " puts back exactly what was there");
                Assert.Empty(rig.Record.Pc.GameBar!.Values);
            }
            else if (row.Action.HasFlag(GameBarAction.Write))
            {
                Assert.True(wrote, label + " writes");
                Assert.Equal(RegValue.DWord(0), rig.Registry.Get(DvrKey, "AppCaptureEnabled"));
                Assert.Equal(7, rig.Record.Pc.GameBar!.Values.Count + rig.Record.Pc.GameBar.Handlers.Count);
            }
            else Assert.True(!wrote, label + " writes nothing");
            if (row.Action.HasFlag(GameBarAction.Record) && !row.Action.HasFlag(GameBarAction.Write)) Assert.Fail(label + " records but does not write: the record would hold nothing");
        }
    }
}

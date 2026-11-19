using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Keep's cause rules could fail:
/// - a value Exo wrote and read back is not held;
/// - a player changing one line is fought more than once, or the value found is not remembered;
/// - the same value coming back is put back again instead of yielded;
/// - three lines moved (or every moved line back at its old value) is taken for a reset with no second signal;
/// - a line order change with nothing moved is evidence, or a rebuilt file with two moved lines is not;
/// - a fingerprint change or the game's marker at the session's start is not enough, or the cloud is fought;
/// - the same line put back three sessions running with evidence is not refused, or a back off happens without evidence;
/// - a verdict maps to the wrong event.
/// </summary>
public sealed class KeepRulesTests
{
    private static KeepRules.Signals None => new(false, false, false, false);

    [Fact]
    public void AValueThatReadsWhatExoWroteIsHeld()
    {
        Assert.Equal(KeepRules.Verdict.Held, KeepRules.Decide("1", "1", null, None, 0));
        // Every signal on, many sessions of evidence: a value that reads what Exo wrote is still held.
        Assert.Equal(KeepRules.Verdict.Held, KeepRules.Decide("1", "1", null, new(true, true, true, true), 9));
    }

    [Fact]
    public void ADriftOfOneLineIsPutBackOnceAndRemembered()
    {
        Assert.Equal(KeepRules.Verdict.Drift, KeepRules.Decide("1", "0", null, None, 0));
        var row = KeepTable.Step(KeepState.Held, KeepRules.EventOf(KeepRules.Verdict.Drift));
        Assert.Equal(KeepState.PutBack, row.To);
        Assert.Equal(KeepAction.WriteAndRemember, row.Action);
    }

    [Fact]
    public void ALineThatComesBackToTheSameValueIsYielded()
    {
        Assert.Equal(KeepRules.Verdict.DriftAgain, KeepRules.Decide("1", "0", "0", None, 0));
        var row = KeepTable.Step(KeepState.PutBack, KeepRules.EventOf(KeepRules.Verdict.DriftAgain));
        Assert.Equal(KeepState.Yielded, row.To);
        Assert.Equal(KeepAction.None, row.Action);
    }

    [Fact]
    public void ALineMovedASecondTimeWithNoEvidenceIsThePlayersWhateverTheValue() =>
        // It came back to 2, not to the 0 Exo found last time: moved a second time with no evidence is the player's, whatever the value.
        Assert.Equal(KeepRules.Verdict.DriftAgain, KeepRules.Decide("1", "2", "0", None, 0));

    [Fact]
    public void ThreeOrMoreLinesMovedWithNoOtherSignalIsNotAReset()
    {
        (string Found, string Before)[] moved = [("2", "1"), ("3", "1"), ("4", "1")];
        Assert.False(KeepRules.Rebuilt(shapeChanged: false, moved));
        // Without a second signal, three lines moved is a drift, never a reset.
        var verdict = KeepRules.Decide("1", "0", null, None, 0);
        Assert.Equal(KeepRules.Verdict.Drift, verdict);
        Assert.NotEqual(KeepRules.Verdict.Reset, verdict);
    }

    [Fact]
    public void ThreeOrMoreLinesMovedInAFileWhoseLineOrderChangedIsAReset()
    {
        (string Found, string Before)[] moved = [("2", "1"), ("3", "1"), ("4", "1")];
        Assert.True(KeepRules.Rebuilt(shapeChanged: true, moved));
        Assert.Equal(KeepRules.Verdict.Reset, KeepRules.Decide("1", "0", null, new(false, false, true, false), 0));
    }

    [Fact]
    public void ValuesBackAtBeforeWithNoOtherSignalAreNotAReset() =>
        Assert.False(KeepRules.Rebuilt(shapeChanged: false, [("1", "1")]));

    [Fact]
    public void ValuesBackAtBeforeInAFileWhoseLineOrderChangedAreAReset()
    {
        Assert.True(KeepRules.Rebuilt(shapeChanged: true, [("1", "1")]));
        // One line back at its old value and another not: only a second signal would make this a reset, and there is none.
        Assert.False(KeepRules.Rebuilt(shapeChanged: true, [("1", "1"), ("9", "5")]));
    }

    [Fact]
    public void ALineOrderChangeWithNothingMovedIsNoEvidence() =>
        Assert.False(KeepRules.Rebuilt(shapeChanged: true, []));

    [Fact]
    public void AFingerprintChangeAloneIsAReset()
    {
        Assert.Equal(KeepRules.Verdict.Reset, KeepRules.Decide("1", "0", null, new(true, false, false, false), 0));
        var row = KeepTable.Step(KeepState.Held, KeepRules.EventOf(KeepRules.Verdict.Reset));
        Assert.Equal(KeepState.PutBack, row.To);
        Assert.Equal(KeepAction.Write, row.Action);
    }

    [Fact]
    public void AMarkerAtSessionStartAloneIsAReset() =>
        Assert.Equal(KeepRules.Verdict.Reset, KeepRules.Decide("1", "0", null, new(false, true, false, false), 0));

    [Fact]
    public void ACloudRestoreIsYieldedNotFought()
    {
        // The cloud wins over a fingerprint change, a marker and a rebuilt file all at once.
        Assert.Equal(KeepRules.Verdict.Cloud, KeepRules.Decide("1", "0", null, new(true, true, true, true), 0));
        var row = KeepTable.Step(KeepState.Held, KeepRules.EventOf(KeepRules.Verdict.Cloud));
        Assert.Equal(KeepState.Yielded, row.To);
        Assert.Equal(KeepAction.None, row.Action);
    }

    [Fact]
    public void TheSameLinePutBackThreeSessionsIsRefused()
    {
        var fingerprint = new KeepRules.Signals(true, false, false, false);
        Assert.Equal(KeepRules.Verdict.Reset, KeepRules.Decide("1", "0", null, fingerprint, 2));
        Assert.Equal(KeepRules.Verdict.BackOff, KeepRules.Decide("1", "0", null, fingerprint, 3));
        Assert.Equal(KeepRules.Verdict.BackOff, KeepRules.Decide("1", "0", null, fingerprint, 4));
        var row = KeepTable.Step(KeepState.PutBack, KeepRules.EventOf(KeepRules.Verdict.BackOff));
        Assert.Equal(KeepState.Refused, row.To);
        Assert.Equal(KeepAction.None, row.Action);
    }

    [Fact]
    public void BackOffNeverHappensWithoutEvidence() =>
        // Three sessions running is only a back off with evidence each time; without it, the same value stays a drift.
        Assert.Equal(KeepRules.Verdict.Drift, KeepRules.Decide("1", "0", null, None, 99));

    [Fact]
    public void EveryVerdictMapsToItsOwnEvent()
    {
        var expected = new Dictionary<KeepRules.Verdict, KeepEvent>
        {
            [KeepRules.Verdict.Held] = KeepEvent.SeenHeld,
            [KeepRules.Verdict.Reset] = KeepEvent.Reset,
            [KeepRules.Verdict.Drift] = KeepEvent.Drift,
            [KeepRules.Verdict.DriftAgain] = KeepEvent.DriftAgain,
            [KeepRules.Verdict.Cloud] = KeepEvent.CloudRestore,
            [KeepRules.Verdict.BackOff] = KeepEvent.BackOff,
        };
        Assert.Equal(6, expected.Count);
        foreach (var (verdict, ev) in expected)
            Assert.Equal(ev, KeepRules.EventOf(verdict));
        Assert.Equal(expected.Count, Enum.GetValues<KeepRules.Verdict>().Length);
        // Every verdict has its own event, and no two share one.
        Assert.Equal(expected.Count, expected.Values.Distinct().Count());
    }
}

/// <summary>
/// Ways Keep's one table could fail: a state and event with no row, or two; a state with no way out; a state nothing
/// reaches; a row that disagrees with the table (docs/architecture/keep.md); evidence writing over a value the player owns or
/// the game refuses; Optimize not taking a yielded or refused value back; an event writing that should not (or one that
/// should and does not); a refused write that still ends held.
/// </summary>
public sealed class KeepTableTests
{
    // The table from docs/architecture/keep.md, written out here as data and not built by KeepTable.
    private static readonly KeepState[] States =
    [
        KeepState.Held, KeepState.Undone, KeepState.PutBack, KeepState.Yielded, KeepState.Refused, KeepState.Waiting, KeepState.Skipped,
    ];

    private static readonly KeepState[] Live =
        [KeepState.Held, KeepState.PutBack, KeepState.Undone, KeepState.Waiting, KeepState.Skipped];

    /// <summary>The whole table as the doc draws it; a state not in From stays where it is with nothing done.</summary>
    private static List<KeepTable.Row> ExpectedRows()
    {
        var rows = new List<KeepTable.Row>();
        void Add(KeepEvent ev, KeepState[] from, KeepState to, KeepAction action = KeepAction.None)
        {
            foreach (var state in States)
                rows.Add(from.Contains(state) ? new KeepTable.Row(state, ev, to, action) : new KeepTable.Row(state, ev, state, KeepAction.None));
        }
        Add(KeepEvent.Pressed, States, KeepState.Held, KeepAction.Write);
        Add(KeepEvent.PressedRefused, States, KeepState.Refused);
        Add(KeepEvent.SeenHeld, States, KeepState.Held);
        Add(KeepEvent.Reset, Live, KeepState.PutBack, KeepAction.Write);
        Add(KeepEvent.Drift, Live, KeepState.PutBack, KeepAction.WriteAndRemember);
        Add(KeepEvent.DriftAgain, Live, KeepState.Yielded);
        Add(KeepEvent.CloudRestore, Live, KeepState.Yielded);
        Add(KeepEvent.BackOff, Live, KeepState.Refused);
        Add(KeepEvent.PutBackFailed, [KeepState.PutBack, KeepState.Undone], KeepState.Undone);
        Add(KeepEvent.PutBackRefused, [KeepState.PutBack], KeepState.Refused);
        Add(KeepEvent.GroupFailsClosed, Live, KeepState.Waiting);
        Add(KeepEvent.LineGone, Live, KeepState.Skipped);
        return rows;
    }

    [Fact]
    public void EveryStateHasExactlyOneRowForEveryEvent()
    {
        var states = Enum.GetValues<KeepState>().Length;
        var events = Enum.GetValues<KeepEvent>().Length;
        Assert.Equal(states * events, KeepTable.Rows.Count);
        Assert.Equal(KeepTable.Rows.Count, KeepTable.Rows.Select(row => (row.From, row.Event)).Distinct().Count());
    }

    [Fact]
    public void NoStateLacksAnExit()
    {
        foreach (var state in Enum.GetValues<KeepState>())
            Assert.True(KeepTable.Rows.Any(row => row.From == state && row.To != state), $"{state} has no way out");
    }

    [Fact]
    public void EveryStateCanBeReachedFromHeld()
    {
        var seen = new HashSet<KeepState> { KeepState.Held };
        var queue = new Queue<KeepState>();
        queue.Enqueue(KeepState.Held);
        while (queue.Count > 0)
        {
            var from = queue.Dequeue();
            foreach (var to in KeepTable.Rows.Where(row => row.From == from).Select(row => row.To))
                if (seen.Add(to)) queue.Enqueue(to);
        }
        Assert.Equal(Enum.GetValues<KeepState>().Length, seen.Count);
        Assert.All(Enum.GetValues<KeepState>(), state => Assert.Contains(state, seen));
    }

    [Fact]
    public void EveryRowIsWhatTheTableBelowSays()
    {
        var expected = ExpectedRows();
        var actual = KeepTable.Rows;
        Assert.Equal(expected.Count, actual.Count);
        var byKey = actual.ToDictionary(row => (row.From, row.Event));
        foreach (var want in expected)
        {
            Assert.True(byKey.TryGetValue((want.From, want.Event), out var got), $"no row for {want.Event} from {want.From}");
            Assert.True(want.To == got.To && want.Action == got.Action,
                $"{want.Event} from {want.From}: expected {want.To}/{want.Action}, got {got.To}/{got.Action}");
        }
    }

    [Fact]
    public void AYieldedOrRefusedValueIsNeverWrittenOverByEvidence()
    {
        KeepEvent[] evidence =
        [
            KeepEvent.Reset, KeepEvent.Drift, KeepEvent.DriftAgain, KeepEvent.CloudRestore, KeepEvent.BackOff,
            KeepEvent.GroupFailsClosed, KeepEvent.LineGone,
        ];
        foreach (var from in new[] { KeepState.Yielded, KeepState.Refused })
            foreach (var ev in evidence)
            {
                var row = KeepTable.Step(from, ev);
                Assert.Equal(from, row.To);
                Assert.Equal(KeepAction.None, row.Action);
            }
    }

    [Fact]
    public void PressingOptimizeTakesBackAYieldedOrRefusedValue()
    {
        var yielded = KeepTable.Step(KeepState.Yielded, KeepEvent.Pressed);
        Assert.Equal(KeepState.Held, yielded.To);
        Assert.Equal(KeepAction.Write, yielded.Action);
        var refused = KeepTable.Step(KeepState.Refused, KeepEvent.Pressed);
        Assert.Equal(KeepState.Held, refused.To);
        Assert.Equal(KeepAction.Write, refused.Action);
    }

    [Fact]
    public void OnlyTheseEventsEverWrite()
    {
        string[] EventsWith(KeepAction action) =>
            [.. KeepTable.Rows.Where(row => row.Action == action).Select(row => row.Event.ToString()).Distinct().Order()];
        Assert.Equal(["Pressed", "Reset"], EventsWith(KeepAction.Write));
        Assert.Equal(["Drift"], EventsWith(KeepAction.WriteAndRemember));
    }

    [Fact]
    public void ARefusedWriteNeverEndsHeld()
    {
        foreach (var state in Enum.GetValues<KeepState>())
            Assert.Equal(KeepState.Refused, KeepTable.Step(state, KeepEvent.PressedRefused).To);
        Assert.Equal(KeepState.Refused, KeepTable.Step(KeepState.PutBack, KeepEvent.PutBackRefused).To);
        Assert.Equal(KeepState.Undone, KeepTable.Step(KeepState.PutBack, KeepEvent.PutBackFailed).To);
        Assert.Equal(KeepState.Undone, KeepTable.Step(KeepState.Undone, KeepEvent.PutBackFailed).To);
    }
}

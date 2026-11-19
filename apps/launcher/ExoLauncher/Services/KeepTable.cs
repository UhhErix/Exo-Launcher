namespace ExoLauncher.Services;

/// <summary>What one value Keep holds is doing now (docs/architecture/keep.md has the table drawn out).</summary>
internal enum KeepState
{
    /// <summary>Exo's value is in place and was read back.</summary>
    Held,
    /// <summary>Found not Exo's and not yet put back: the put back could not be written (the file is locked).</summary>
    Undone,
    /// <summary>Exo wrote its value again after finding it moved, and read it back.</summary>
    PutBack,
    /// <summary>The player's: Exo stopped writing this value; Optimize takes it back.</summary>
    Yielded,
    /// <summary>The target won't take it or the game keeps undoing it: Exo stopped, and says so once; Optimize tries again.</summary>
    Refused,
    /// <summary>Nothing to hold yet: the game has not written its file, or a group failed closed after an update.</summary>
    Waiting,
    /// <summary>The target does not list this value (a line the file no longer holds, a driver id it does not know).</summary>
    Skipped,
}

/// <summary>Everything that can happen to a value Keep holds.</summary>
internal enum KeepEvent
{
    /// <summary>The player pressed Optimize and the value was written and read back.</summary>
    Pressed,
    /// <summary>It was written and a fresh read says something else.</summary>
    PressedRefused,
    /// <summary>A look found Exo's value in place.</summary>
    SeenHeld,
    /// <summary>Found moved, with evidence of a reset (a fingerprint changed, a marker at the session's start, a rebuilt file).</summary>
    Reset,
    /// <summary>Found moved with no evidence, to a value not seen before: put back once and remember the value found.</summary>
    Drift,
    /// <summary>Found moved a second time with no evidence, to any value: the player's, put back once already.</summary>
    DriftAgain,
    /// <summary>Found moved while the game's cloud was the likely writer: yielded to the cloud.</summary>
    CloudRestore,
    /// <summary>Put back three sessions running, with reset evidence each time: the game keeps undoing it.</summary>
    BackOff,
    /// <summary>The put back could not be written (the file is locked, the write failed): still not Exo's.</summary>
    PutBackFailed,
    /// <summary>The put back was written and a fresh read says something else.</summary>
    PutBackRefused,
    /// <summary>After an update a key went missing or its allowed values changed: the whole group waits, nothing is replayed.</summary>
    GroupFailsClosed,
    /// <summary>The file no longer holds the line, with no update to explain it.</summary>
    LineGone,
}

/// <summary>What the engine does when a row of <see cref="KeepTable"/> is taken.</summary>
internal enum KeepAction
{
    None,
    /// <summary>Write Exo's value (worked out again from the file as it is now) and read it back.</summary>
    Write,
    /// <summary>Write it, and remember the value that was found, so the same value again is the player's.</summary>
    WriteAndRemember,
}

/// <summary>
/// The one table that moves every value Keep holds: a state, an event, the state it goes to and what is done. Every state has
/// a row for every event (a test walks them all, and proves no state lacks an exit); a value never changes state any other
/// way. Rows are written by event, then by the states they apply to.
/// </summary>
internal static class KeepTable
{
    internal sealed record Row(KeepState From, KeepEvent Event, KeepState To, KeepAction Action);

    private static readonly KeepState[] All = Enum.GetValues<KeepState>();
    /// <summary>States a look judges: a value the player owns (yielded) or the game won't keep (refused) is not written over by evidence.</summary>
    private static readonly KeepState[] Live = [KeepState.Held, KeepState.PutBack, KeepState.Undone, KeepState.Waiting, KeepState.Skipped];

    internal static readonly IReadOnlyList<Row> Rows = Build();

    private static List<Row> Build()
    {
        var rows = new List<Row>();
        // To a new state from some states; from the rest the value stays where it is and nothing is done.
        void On(KeepEvent ev, KeepState[] from, KeepState to, KeepAction action = KeepAction.None)
        {
            foreach (var state in All)
                rows.Add(from.Contains(state) ? new Row(state, ev, to, action) : new Row(state, ev, state, KeepAction.None));
        }
        void Always(KeepEvent ev, KeepState to, KeepAction action = KeepAction.None) => On(ev, All, to, action);

        Always(KeepEvent.Pressed, KeepState.Held, KeepAction.Write);
        Always(KeepEvent.PressedRefused, KeepState.Refused);
        Always(KeepEvent.SeenHeld, KeepState.Held);
        On(KeepEvent.Reset, Live, KeepState.PutBack, KeepAction.Write);
        On(KeepEvent.Drift, Live, KeepState.PutBack, KeepAction.WriteAndRemember);
        On(KeepEvent.DriftAgain, Live, KeepState.Yielded);
        On(KeepEvent.CloudRestore, Live, KeepState.Yielded);
        On(KeepEvent.BackOff, Live, KeepState.Refused);
        On(KeepEvent.PutBackFailed, [KeepState.PutBack, KeepState.Undone], KeepState.Undone);
        On(KeepEvent.PutBackRefused, [KeepState.PutBack], KeepState.Refused);
        On(KeepEvent.GroupFailsClosed, Live, KeepState.Waiting);
        On(KeepEvent.LineGone, Live, KeepState.Skipped);
        return rows;
    }

    private static readonly Dictionary<(KeepState, KeepEvent), Row> Index = Rows.ToDictionary(row => (row.From, row.Event));

    /// <summary>The row for a state and an event; every pair has one.</summary>
    internal static Row Step(KeepState state, KeepEvent ev) => Index[(state, ev)];
}

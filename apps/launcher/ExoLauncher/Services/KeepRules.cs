namespace ExoLauncher.Services;

/// <summary>
/// Telling a reset from the player's own change, as pure logic. For a value found not at what Exo wrote, in this order:
/// 1. a cloud signal (Activision's cloud flag on and its .b files changed with no session on this PC): yield to the cloud;
/// 2. reset evidence: a fingerprint changed since the value was last held (game build, a launcher's content, the file's
///    own mark of the defaults it was built from, the driver the game detected), Call of Duty's marker at the session's
///    start, or a rebuilt file (its line order changed AND at least three lines moved, or every moved line is back at what
///    it was before Exo). A reset is put back, however few lines moved; the profile is worked out again from the file as it
///    is, never replayed. Put back three sessions running with evidence each time, the game keeps undoing it: refused;
/// 3. no evidence, however many lines (3 or more, or back at their old values, are a reset only with a second signal): the
///    first time Exo puts it back and remembers the value it found; moved a second time with no evidence, to any value, is the
///    player's (yielded).
/// Ways it could fail: fighting a player who changed one line (rule 3 stops after one put back); a cloud on two PCs that
/// take turns (rule 1); a game that rewrites on purpose (the back-off ends it); a preset picked in the menu counted as a
/// reset (three lines alone are no evidence).
/// </summary>
internal static class KeepRules
{
    /// <summary>How many sessions in a row a value may be put back with reset evidence before Keep stops.</summary>
    internal const int Sessions = 3;

    /// <summary>What a look found around one file: which evidence there is.</summary>
    internal sealed record Signals(bool Fingerprint, bool Marker, bool Rebuilt, bool Cloud)
    {
        internal bool Evidence => Fingerprint || Marker || Rebuilt;
    }

    internal enum Verdict { Held, Reset, Drift, DriftAgain, Cloud, BackOff }

    /// <summary>
    /// The verdict on one value: what Exo wrote, what the file holds, the value Exo found last time it put this one back
    /// (null if never), the signals, and the sessions running that Keep put values back with evidence.
    /// </summary>
    internal static Verdict Decide(string intended, string found, string? lastFound, Signals signals, int evidenceSessions)
    {
        if (found == intended) return Verdict.Held;
        if (signals.Cloud) return Verdict.Cloud;
        if (signals.Evidence) return evidenceSessions >= Sessions ? Verdict.BackOff : Verdict.Reset;
        return lastFound is not null ? Verdict.DriftAgain : Verdict.Drift;
    }

    /// <summary>
    /// A rebuilt file: the order of its lines changed (<paramref name="shapeChanged"/>) and either 3 or more of Exo's lines
    /// moved or every moved line is back at what it was before Exo. Line order alone is no evidence: an update adds keys.
    /// </summary>
    internal static bool Rebuilt(bool shapeChanged, IReadOnlyCollection<(string Found, string Before)> moved) =>
        shapeChanged && moved.Count > 0 && (moved.Count >= 3 || moved.All(move => move.Found == move.Before));

    internal static KeepEvent EventOf(Verdict verdict) => verdict switch
    {
        Verdict.Held => KeepEvent.SeenHeld,
        Verdict.Reset => KeepEvent.Reset,
        Verdict.Drift => KeepEvent.Drift,
        Verdict.DriftAgain => KeepEvent.DriftAgain,
        Verdict.Cloud => KeepEvent.CloudRestore,
        _ => KeepEvent.BackOff,
    };
}

using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The driver side's watch: a <see cref="DriverKeep"/> look every ten minutes (one registry read when nothing changed) and
/// one the moment a driver install that is not Exo's own restoring path says it is done. Exo's own installs put their
/// settings back themselves (<see cref="DriverRestore.AfterInstallAsync"/> notes the new identity); for every other install
/// (Windows Update, the vendor's app, by hand) this is what puts Exo's settings back. Nothing is written while a game runs
/// or an install does: <see cref="DriverKeep"/> answers Waiting and the next look tries again.
/// Ways it could fail: writing Exo's settings a second time during Exo's own restoring step (the steps of that path are
/// told apart by <see cref="IsExosOwn"/> and never looked at, only noted once by <see cref="DriverRestore.AfterInstallAsync"/>);
/// a look that throws (caught and logged, and the next tick tries again); the timer outliving Exo (<see cref="Stop"/>
/// disposes it at shutdown, with the other timers).
/// </summary>
internal static class DriverKeepWatch
{
    internal static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    /// <summary>How long to the next look while a changed driver identity waits to be seen standing a second time.</summary>
    internal static readonly TimeSpan WhilePending = TimeSpan.FromMinutes(2);

    /// <summary>When the next look is: soon while a change is pending, otherwise at the usual time.</summary>
    internal static TimeSpan NextLook(DriverOutcome? last) => last == DriverOutcome.Pending ? WhilePending : Every;

    private static Timer? _tick;
    private static int _started;
    private static bool _exosOwn;

    /// <summary>Starts the watch once, and listens for a driver install saying it is done.</summary>
    internal static void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        DriverInstallSignal.Changed += OnStep;
        _tick = new Timer(_ => Look("every ten minutes", ct), null, Every, Every);
        ct.Register(Stop);
    }

    /// <summary>Stops the watch and lets go of the install signal; safe twice, and from any thread.</summary>
    internal static void Stop()
    {
        DriverInstallSignal.Changed -= OnStep;
        _tick?.Dispose();
        _tick = null;
        Interlocked.Exchange(ref _started, 0);
        _exosOwn = false;
    }

    /// <summary>
    /// Whether a step belongs to Exo's own install, which puts the settings back itself: every step from its first
    /// ("waiting", "installing", "reloading", "restoring") to its final "done" carrying the outcome, or its "failed". A
    /// "done" in between (the installer's own progress marker, carrying no outcome) is Exo's too, so no look races Exo's
    /// own restore. Such a step is never looked at; the identity is noted once, by
    /// <see cref="DriverRestore.AfterInstallAsync"/>. <paramref name="own"/> is the watch's memory of the install.
    /// </summary>
    internal static bool IsExosOwn(DriverInstallSignal.Step step, ref bool own)
    {
        if (step.Phase is "failed") { own = false; return true; }
        if (step.Phase is not "done") { own = true; return true; }
        // A "done" inside Exo's own install is its own: the installer's progress marker (no outcome) leaves the memory
        // alone, and the final one, carrying the outcome of the restoring step, ends the path.
        var was = own;
        if (was && (step.Restored is not null || step.NotRestored is not null)) own = false;
        return was;
    }

    /// <summary>An install reported a step: only a "done" that is not part of Exo's own install is looked at, off the reporter's thread.</summary>
    private static void OnStep(DriverInstallSignal.Step step)
    {
        if (IsExosOwn(step, ref _exosOwn)) return;
        // Off the reporter's thread: a look may write a driver profile for a few seconds, and the install's caller waits on
        // its own step, never on this.
        _ = Task.Run(() => Look("when the driver install finished", CancellationToken.None));
    }

    /// <summary>One look; a failure is logged and the next one tries again. A change that waits to be seen standing is looked at again in two minutes.</summary>
    internal static void Look(string when, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        DriverOutcome? outcome = null;
        try { outcome = DriverKeep.Shared.Look(when); }
        catch (Exception ex) { AppLog.Warn($"Driver: the {when} look failed: {ex.Message}"); }
        try { _tick?.Change(NextLook(outcome), Every); }
        catch (ObjectDisposedException) { }
    }
}

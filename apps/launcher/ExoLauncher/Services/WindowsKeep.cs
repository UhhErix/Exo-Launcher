using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>Where one look at the Windows side finds the PC (docs/architecture/keep.md, "The Windows side").</summary>
internal enum WindowsSituation
{
    /// <summary>Windows' build and update revision cannot be read: never taken for an update.</summary>
    UnknownBuild,
    /// <summary>A game runs: nothing is written while it does.</summary>
    GameRunning,
    /// <summary>The record has never held a build: the build is recorded and nothing is written.</summary>
    NoBuildRecorded,
    /// <summary>The build is the one the settings were last seen in place under.</summary>
    SameBuild,
    /// <summary>A newer build: Windows resets settings when it updates.</summary>
    BuildChanged,
}

/// <summary>What one row of the table does.</summary>
[Flags]
internal enum WindowsStep
{
    None = 0,
    /// <summary>Record the build as the one Exo's settings are held under.</summary>
    NoteBuild = 1,
    /// <summary>Look at the four Windows-side settings once and put back what the rules allow.</summary>
    LookAtAll = 2,
}

/// <summary>What a look decided, as the log says it.</summary>
internal enum WindowsOutcome
{
    /// <summary>The build could not be read: nothing was decided.</summary>
    Unknown,
    /// <summary>A game holds it: nothing was written, and the next start tries again.</summary>
    Waiting,
    /// <summary>The build is recorded and nothing was written.</summary>
    Noted,
    /// <summary>The build is the one the settings were last seen under: nothing was written.</summary>
    Same,
    /// <summary>A new build: the four looks ran once and the build is recorded.</summary>
    Looked,
}

/// <summary>One row: in <see cref="Situation"/>, do <see cref="Step"/>, answer <see cref="Outcome"/>.</summary>
internal sealed record WindowsRow(WindowsSituation Situation, WindowsStep Step, WindowsOutcome Outcome);

/// <summary>
/// The Windows side of Keep after a Windows update. A feature update, and sometimes a monthly one, resets settings Exo wrote
/// (the power plan's values, Game Mode, Game Bar, a program's graphics choice), and nothing else looks again. At Exo's start
/// this compares the Windows build and update revision (<see cref="PcAbout.BuildAndRevision"/>) with the one Keep's record says
/// the settings were last seen in place under, and decides by <see cref="Table"/>: the first run only records it; the same
/// build does nothing; a different build, with no game running, looks at each of the four once (each does what its own rules
/// allow: a value the player yielded stays theirs, a Game Bar the player turned off stays off), logs one line, then records
/// the new build. Every call into Windows is a function given to the constructor, so the table is tested with no Windows.
/// Ways it could fail: writing under a running game; looking at every start, not at an update; recording a build before the
/// looks ran (so an update is never looked at); a Windows reset taken for the player's choice (the looks are told it was an
/// update); a first run that writes.
/// </summary>
/// <param name="build">Windows' build and update revision now, or null when it cannot be read.</param>
/// <param name="gameRunning">A library game runs now.</param>
/// <param name="powerPlan">Looks at the values of Exo's power plan; what it put back, or null when nothing needed it.</param>
/// <param name="gameMode">Looks at Game Mode (as after an update); what it put back, or null.</param>
/// <param name="gameBar">Looks at Game Bar; what it put back, or null.</param>
/// <param name="gpuPreferences">Looks at the graphics choice of each program Exo set; what it put back, or null.</param>
/// <param name="load">What the record holds for this PC.</param>
/// <param name="save">Changes what the record holds for this PC and saves it; false when it could not be saved.</param>
/// <param name="log">One line for app.log.</param>
internal sealed class WindowsKeep(
    Func<string?> build,
    Func<bool> gameRunning,
    Func<string?> powerPlan,
    Func<string?> gameMode,
    Func<string?> gameBar,
    Func<string?> gpuPreferences,
    Func<Keep.KeepPc> load,
    Func<Action<Keep.KeepPc>, bool> save,
    Action<string> log)
{
    /// <summary>Every situation and what it does. A test walks every row, and that no situation is missing one.</summary>
    internal static readonly IReadOnlyList<WindowsRow> Table =
    [
        new(WindowsSituation.UnknownBuild, WindowsStep.None, WindowsOutcome.Unknown),
        new(WindowsSituation.GameRunning, WindowsStep.None, WindowsOutcome.Waiting),
        new(WindowsSituation.NoBuildRecorded, WindowsStep.NoteBuild, WindowsOutcome.Noted),
        new(WindowsSituation.SameBuild, WindowsStep.None, WindowsOutcome.Same),
        new(WindowsSituation.BuildChanged, WindowsStep.LookAtAll | WindowsStep.NoteBuild, WindowsOutcome.Looked),
    ];

    private static readonly Dictionary<WindowsSituation, WindowsRow> Index = Table.ToDictionary(row => row.Situation);

    /// <summary>The row for a situation; every situation has one.</summary>
    internal static WindowsRow Row(WindowsSituation situation) => Index[situation];

    /// <summary>
    /// The one Exo uses, with the real calls: the build is one registry read; a game running is the run-state poll's own answer
    /// (<see cref="DriverKeep.AnyGameRunning"/>); the power plan's values by <see cref="PowerPlan.Recheck"/> (not while a Setup
    /// holds Exo's plan), Game Mode, Game Bar and the graphics choices by their own looks.
    /// </summary>
    internal static WindowsKeep Shared => SharedOnce.Value;

    private static readonly Lazy<WindowsKeep> SharedOnce = new(() => Over(
        PcAbout.BuildAndRevision, DriverKeep.AnyGameRunning, PowerPlan.Shared, PcHealth.ThisPcProfile, Setups.HoldsPlan,
        WindowsGameMode.Shared, WindowsGameBar.Shared, GpuPreference.Shared,
        () => Keep.Load().Pc, change => Keep.Update(doc => change(doc.Pc)), AppLog.Info));

    /// <summary>
    /// The four looks wired to the real services (or fakes of them): each says what it put back, or null. The power plan is
    /// not looked at while a Setup holds Exo's plan (the Setup puts the player's own back itself), and Game Mode is told a
    /// Windows update explains what it finds.
    /// </summary>
    internal static WindowsKeep Over(Func<string?> build, Func<bool> gameRunning, PowerPlan plan, Func<PowerProfiles.Profile> profile, Func<bool> setupHoldsPlan,
        WindowsGameMode mode, WindowsGameBar bar, GpuPreference preferences, Func<Keep.KeepPc> load, Func<Action<Keep.KeepPc>, bool> save, Action<string> log) =>
        new(build, gameRunning,
            () => setupHoldsPlan() ? null : PowerPhrase(plan.RecheckReport(profile())),
            () => ModePhrase(mode.LookReport(windowsUpdated: true)),
            () => BarPhrase(bar.Start()),
            () => GraphicsPhrase(preferences.Recheck()),
            load, save, log);

    private static string? PowerPhrase(PowerPlan.Rechecked rechecked)
    {
        List<string> parts = [];
        if (rechecked.PutBack.Count > 0) parts.Add("Exo's power plan values (" + string.Join(", ", rechecked.PutBack) + ")");
        if (rechecked.DidNotTake.Count > 0) parts.Add("Windows didn't take " + string.Join(", ", rechecked.DidNotTake) + " in Exo's power plan");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? ModePhrase(GameModeLook look) =>
        look.Message is not null ? "Windows didn't take Game Mode" : look.Wrote ? "Game Mode" : null;

    private static string? BarPhrase(GameBarResult result)
    {
        List<string> parts = [];
        if (result.Changed > 0) parts.Add($"{result.Changed} Game Bar setting{(result.Changed == 1 ? "" : "s")}");
        if (result.DidNotTake.Count > 0) parts.Add("Windows didn't take Game Bar's " + string.Join(", ", result.DidNotTake));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? GraphicsPhrase(int putBack) => putBack > 0 ? $"{putBack} graphics choice{(putBack == 1 ? "" : "s")}" : null;

    /// <summary>Where the PC stands, in the table's order: the build, then a game, then the record.</summary>
    internal WindowsSituation Situation()
    {
        if (string.IsNullOrEmpty(build())) return WindowsSituation.UnknownBuild;
        if (gameRunning()) return WindowsSituation.GameRunning;
        var recorded = load().Windows;
        if (string.IsNullOrEmpty(recorded)) return WindowsSituation.NoBuildRecorded;
        return recorded == build() ? WindowsSituation.SameBuild : WindowsSituation.BuildChanged;
    }

    /// <summary>Decides and acts: the table's row for where the PC stands, its steps (the looks, one log line, then the build), then what it says happened.</summary>
    internal WindowsOutcome Look(string when)
    {
        var row = Row(Situation());
        if (build() is not { Length: > 0 } now) return row.Outcome;
        if (row.Step.HasFlag(WindowsStep.LookAtAll)) log(LookAtAll(load().Windows, now, when));
        if (row.Step.HasFlag(WindowsStep.NoteBuild)) save(pc => pc.Windows = now);
        return row.Outcome;
    }

    /// <summary>The four looks, each once and each caught, as one line: what was put back, or that everything of Exo's was in place.</summary>
    private string LookAtAll(string? was, string now, string when)
    {
        List<string> back = [];
        void One(string name, Func<string?> look)
        {
            try { if (look() is { } put) back.Add(put); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { back.Add($"{name} (the look failed: {ex.GetType().Name})"); }
        }
        One("the power plan", powerPlan);
        One("Game Mode", gameMode);
        One("Game Bar", gameBar);
        One("the graphics choices", gpuPreferences);
        return $"Windows: build {was} -> {now} ({when} look): " + (back.Count == 0 ? "everything of Exo's was in place." : "put back " + string.Join(", ", back) + ".");
    }
}

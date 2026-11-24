using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// How far a part may go. Depth is distance from stock, never below 0: MHz of offset on a GeForce, Curve Optimizer points
/// (negative on the wire) on a Ryzen, 1 for "AMD's own tuning" on a Radeon. Each number's source: docs/architecture/auto-tune.md.
/// </summary>
internal sealed record TuneLimits(int Furthest, int Step, int BackOff, int StopC, int StepSeconds, int FinalSeconds, int BaselineSeconds)
{
    /// <summary>Refuses a depth outside the limits; every step on hardware passes through here.</summary>
    internal int Check(int depth) => depth >= 0 && depth <= Furthest ? depth : throw new ArgumentOutOfRangeException(nameof(depth), depth, "Outside this part's limits.");

    /// <summary>How deep a search may go: one step short of the shallowest failure, never past the limit; 0 when the first step failed.</summary>
    internal int Deepest(IEnumerable<int> failed)
    {
        var worst = failed.Where(depth => depth > 0).DefaultIfEmpty(int.MaxValue).Min();
        return worst > Furthest ? Furthest : Math.Max(0, worst - Step);
    }
}

/// <summary>The part being tuned. Each call writes to or reads the hardware; the search is <see cref="TuneRunner"/>'s.</summary>
internal interface ITunePart
{
    string Id { get; }
    TuneLimits Limits { get; }
    /// <summary>What the stock run found, before anything is applied (a GeForce keeps its peak clock as Cooler's cap).</summary>
    void Settle(TuneRun stock);
    /// <summary>Applies <paramref name="depth"/>; returns once it's on.</summary>
    void Apply(int depth);
    /// <summary>What the hardware says is on, where it can say.</summary>
    int? Read();
    void Stock();
    /// <summary>The integers the sign-in script applies this depth with; null where the driver keeps it itself.</summary>
    int[]? SignIn(int depth);
}

/// <summary>A run of the part's load. <paramref name="check"/>, about once a second, answers why to stop, or null.</summary>
internal interface ITuneTest
{
    TuneRun Run(TimeSpan length, Func<string?> check);
    double? TemperatureC();
}

/// <summary>What one run measured. Error: a wrong answer, a lost device or what stopped it.</summary>
internal sealed record TuneRun(double Score, int? PeakMhz, string? Error);

/// <summary>What surrounds the test: whether it may run at all, and what Windows logged.</summary>
internal interface ITuneHost
{
    /// <summary>Why nothing may run right now (a game, a battery, Exo closed), or null.</summary>
    string? Refusal();
    /// <summary>Hardware errors logged since the worker started: WHEA, and a driver reset for a GPU.</summary>
    int HardwareErrors();
    bool StopAsked();
    /// <summary>Makes the sign-in task match the record: what holds gets re-applied, and with nothing left the task goes.</summary>
    void Reapply();
}

/// <summary>A measured run, for the page: score from Exo's own test, the hottest it read.</summary>
internal sealed record TuneMeasure(double Score, double? PeakC);

/// <summary>One part's record on disk.</summary>
internal sealed record TunePartState
{
    /// <summary>idle, testing, done, stopped, crashed.</summary>
    public string Phase { get; init; } = "idle";
    public int Applied { get; init; }
    public int? Testing { get; init; }
    public string Mode { get; init; } = "";
    public List<int> Passed { get; init; } = [];
    public List<int> Failed { get; init; } = [];
    public TuneMeasure? Before { get; init; }
    public TuneMeasure? After { get; init; }
    public string? Note { get; init; }
    public bool Accepted { get; init; }
    public int? SecondsLeft { get; init; }
    /// <summary>What re-applies <see cref="Applied"/> at sign-in: see <see cref="ITunePart.SignIn"/>.</summary>
    public int[]? SignIn { get; init; }
    public DateTimeOffset? AppliedAtUtc { get; init; }
}

internal enum TuneReapplyDecision { Apply, PauseAfterCrash, Skip }

internal sealed record TuneCrashEvent(string Provider, int EventId, DateTimeOffset TimestampUtc);

internal sealed record TuneReapplyVerdict(TuneReapplyDecision Decision, string? Reason);

internal sealed record TuneVendorRules(
    bool SurvivesExoClose,
    bool SurvivesReboot,
    bool SurvivesSleep,
    bool SurvivesDriverReset,
    bool NeedsResidentProcess,
    bool UsesScheduledTask);

internal static class TunePersistence
{
    internal const int CrashWindowMinutes = 10;
    internal static readonly TimeSpan DefaultCrashWindow = TimeSpan.FromMinutes(CrashWindowMinutes);
    internal const string PausedNote = "Tuning paused after a crash.";

    internal static TuneVendorRules RulesFor(string partId) => partId switch
    {
        "nvidia" => new(SurvivesExoClose: true, SurvivesReboot: false, SurvivesSleep: false, SurvivesDriverReset: false, NeedsResidentProcess: false, UsesScheduledTask: true),
        "radeon" => new(SurvivesExoClose: true, SurvivesReboot: true, SurvivesSleep: true, SurvivesDriverReset: false, NeedsResidentProcess: false, UsesScheduledTask: false),
        "ryzen" => new(SurvivesExoClose: true, SurvivesReboot: false, SurvivesSleep: false, SurvivesDriverReset: true, NeedsResidentProcess: false, UsesScheduledTask: true),
        "fans" => new(SurvivesExoClose: false, SurvivesReboot: false, SurvivesSleep: false, SurvivesDriverReset: false, NeedsResidentProcess: true, UsesScheduledTask: false),
        _ => new(false, false, false, false, false, false),
    };

    internal static bool IsRelevantCrashEvent(string partId, TuneCrashEvent ev)
    {
        if (ev.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && ev.EventId == 41) return true;
        if (ev.Provider.Equals("Microsoft-Windows-WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) && ev.EventId == 1001) return true;
        if (ev.Provider.Equals("EventLog", StringComparison.OrdinalIgnoreCase) && ev.EventId == 6008) return true;
        if (ev.Provider.Equals("Microsoft-Windows-WHEA-Logger", StringComparison.OrdinalIgnoreCase)) return true;
        if (partId is "nvidia" or "radeon" && ev.Provider.Equals("Display", StringComparison.OrdinalIgnoreCase) && ev.EventId == 4101) return true;
        if (partId == "nvidia" && ev.Provider.Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase) && ev.EventId is 13 or 14 or 153) return true;
        if (partId == "radeon" && (ev.Provider.Equals("amdkmdag", StringComparison.OrdinalIgnoreCase) || ev.Provider.Equals("amduw23g", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    internal static TuneReapplyVerdict EvaluateReapply(
        string partId,
        string trigger,
        bool testingMarkerPresent,
        bool alreadyPaused,
        bool uncleanApplyPending,
        DateTimeOffset? lastAppliedUtc,
        IReadOnlyList<TuneCrashEvent> recentEvents,
        TimeSpan? crashWindow = null)
    {
        if (testingMarkerPresent) return new(TuneReapplyDecision.Skip, "Testing is in progress.");
        if (string.Equals(trigger, "manual", StringComparison.OrdinalIgnoreCase)) return new(TuneReapplyDecision.Apply, null);
        if (alreadyPaused) return new(TuneReapplyDecision.PauseAfterCrash, PausedNote);
        if (uncleanApplyPending) return new(TuneReapplyDecision.PauseAfterCrash, PausedNote);

        var window = crashWindow ?? DefaultCrashWindow;
        if (lastAppliedUtc is { } appliedAt)
        {
            foreach (var ev in recentEvents)
            {
                if (!IsRelevantCrashEvent(partId, ev)) continue;
                if (ev.TimestampUtc >= appliedAt && ev.TimestampUtc <= appliedAt + window)
                    return new(TuneReapplyDecision.PauseAfterCrash, PausedNote);
            }
        }

        var rules = RulesFor(partId);
        if (rules.NeedsResidentProcess) return new(TuneReapplyDecision.Skip, "Requires Exo to stay open.");
        if (trigger.Equals("logon", StringComparison.OrdinalIgnoreCase) && rules.SurvivesReboot) return new(TuneReapplyDecision.Skip, "Driver persists across reboots.");
        if (trigger.Equals("wake", StringComparison.OrdinalIgnoreCase) && rules.SurvivesSleep) return new(TuneReapplyDecision.Skip, "Driver persists across sleep.");
        if (trigger.Equals("tdr", StringComparison.OrdinalIgnoreCase) && rules.SurvivesDriverReset) return new(TuneReapplyDecision.Skip, "Unaffected by GPU driver reset.");
        return new(TuneReapplyDecision.Apply, null);
    }
}

internal sealed record TuneMarker(string Part, int Depth);

/// <summary>
/// The folder that holds the record, the crash marker and the sign-in script: %ProgramFiles%\ExoLauncher Tune in the app,
/// which only an administrator can write, so what runs as SYSTEM at sign-in reads nothing a normal program could change.
/// Only the elevated worker writes it; Exo reads it. The marker reaches the disk before the value goes on, so a PC that goes
/// down on a value still has it.
/// </summary>
internal sealed class TuneStore(string folder)
{
    internal const string HeldFile = "held", ApplyingFile = "applying", AppliedAtFile = "applied-at.txt";
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private string StatePath => Path.Combine(folder, "state.json");
    private string MarkerPath => Path.Combine(folder, "testing.json");
    private string AppliedAtPath => Path.Combine(folder, AppliedAtFile);

    internal string Folder => folder;

    internal Dictionary<string, TunePartState> Load()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, TunePartState>>(File.ReadAllText(StatePath), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Auto tune: its record didn't read: " + ex.GetType().Name);
            return [];
        }
    }

    internal TunePartState Part(string id) => Load().GetValueOrDefault(id) ?? new TunePartState();

    internal void Save(string id, TunePartState part)
    {
        var all = Load();
        all[id] = part;
        Directory.CreateDirectory(folder);
        var next = StatePath + ".new";
        WriteThrough(next, JsonSerializer.Serialize(all, Json));
        File.Move(next, StatePath, overwrite: true);
    }

    internal TuneMarker? Marker()
    {
        try
        {
            return File.Exists(MarkerPath) ? JsonSerializer.Deserialize<TuneMarker>(File.ReadAllText(MarkerPath), Json) ?? new TuneMarker("", 0) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Torn by the crash it was there to record: unreadable still means something went down mid-step.
            AppLog.Warn("Auto tune: its marker didn't read: " + ex.GetType().Name);
            return new TuneMarker("", 0);
        }
    }

    internal void WriteMarker(string part, int depth)
    {
        Directory.CreateDirectory(folder);
        WriteThrough(MarkerPath, JsonSerializer.Serialize(new TuneMarker(part, depth), Json));
    }

    internal void ClearMarker() => DeletionGate.DeleteFile(MarkerPath);

    /// <summary>The sign-in script's own mark that it stopped applying (a crash while applying, or one logged since).</summary>
    internal bool Held() => File.Exists(Path.Combine(folder, HeldFile));

    internal void ClearHeld()
    {
        DeletionGate.DeleteFile(Path.Combine(folder, HeldFile));
        DeletionGate.DeleteFile(Path.Combine(folder, ApplyingFile));
    }

    internal void WriteAppliedAt(DateTimeOffset utc)
    {
        Directory.CreateDirectory(folder);
        WriteThrough(AppliedAtPath, utc.ToUniversalTime().ToString("O"));
    }

    internal DateTimeOffset? ReadAppliedAt()
    {
        try
        {
            if (!File.Exists(AppliedAtPath)) return null;
            return DateTimeOffset.TryParse(File.ReadAllText(AppliedAtPath).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal void ClearAppliedAt() => DeletionGate.DeleteFile(AppliedAtPath);

    private static void WriteThrough(string path, string text)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        file.Write(System.Text.Encoding.UTF8.GetBytes(text));
        file.Flush(flushToDisk: true);
    }
}

/// <summary>
/// The search (docs/architecture/auto-tune.md). Stock first, measured; each deeper step with its marker on disk before the
/// value goes on and its load run under the limits; on the first failure back to stock, then one back-off short of the
/// last that passed, proved by the long run, then measured as stock was. A game, a battery or Stop at any point goes back
/// to stock at once. The marker stays up as long as a value under test is on, so a PC that goes down at any moment
/// leaves it behind. What failed is never tried again; a later search stops short of it.
/// </summary>
internal sealed class TuneRunner(ITunePart part, ITuneTest test, ITuneHost host, TuneStore store, string mode)
{
    private TunePartState _state = new();
    private double? _peak;
    private int _errorBase;
    private string _stopped = "Stopped.";
    private bool _touched;

    /// <summary>What a record and any marker say happened: pure, so Exo can show it without being able to write it.</summary>
    internal static TunePartState Recover(string id, TunePartState state, TuneMarker? marker, bool held)
    {
        if (marker is { } mark && (mark.Part == id || mark.Part.Length == 0))
        {
            var depth = mark.Depth;
            return state with
            {
                Phase = "crashed",
                Applied = 0,
                SignIn = null,
                Testing = null,
                SecondsLeft = null,
                Failed = depth > 0 && !state.Failed.Contains(depth) ? [.. state.Failed, depth] : state.Failed,
                Passed = [.. state.Passed.Where(passed => depth == 0 || passed < depth)],
                Note = "Your PC restarted while Exo tested this. That value is marked failed, and you're back at stock.",
            };
        }
        if (held && state.Applied > 0)
            return state with
            {
                Phase = "paused",
                Testing = null,
                SecondsLeft = null,
                Note = TunePersistence.PausedNote,
            };
        return state;
    }

    /// <summary>
    /// What Exo shows: a worker that's alive is still testing, and its marker is up by design. Once it's gone, a marker means
    /// a crash, and a record still saying testing with no marker means it was closed; nothing stays on either way.
    /// </summary>
    internal static TunePartState Seen(string id, TunePartState saved, TuneMarker? marker, bool held, bool workerAlive)
    {
        if (workerAlive && saved.Phase == "testing") return saved;
        var state = Recover(id, saved, marker, held);
        return state.Phase == "testing"
            ? state with { Phase = "stopped", Testing = null, SecondsLeft = null, Note = "Testing stopped before it finished." }
            : state;
    }

    /// <summary>Writes down what a crash left, for every part on record, and takes the marks away. The worker's first act.</summary>
    internal static void Reconcile(TuneStore store)
    {
        var marker = store.Marker();
        var held = store.Held();
        if (marker is null && !held) return;
        var ids = store.Load().Keys.Concat(marker is { Part.Length: > 0 } ? [marker.Part] : []).Distinct().ToList();
        foreach (var id in ids) store.Save(id, Recover(id, store.Part(id), marker, held));
        store.ClearMarker();
        store.ClearHeld();
    }

    internal TunePartState Run()
    {
        Reconcile(store);
        host.Reapply();
        _state = store.Part(part.Id);
        if (host.Refusal() is { } refused)
        {
            // Nothing was touched, so what was on stays on and on record.
            _state = _state with { Note = refused };
            store.Save(part.Id, _state);
            return _state;
        }
        var limits = part.Limits;
        _state = _state with { Mode = mode, Accepted = true, Note = null, Before = null, After = null };

        try
        {
            part.Stock();
            _state = _state with { Applied = 0, SignIn = null };
            var deepest = limits.Deepest(_state.Failed);
            var good = _state.Passed.Where(passed => passed <= deepest).DefaultIfEmpty(0).Max();
            Update(0, limits.BaselineSeconds * 2 + (deepest - good) / limits.Step * limits.StepSeconds + limits.FinalSeconds);
            host.Reapply();

            var stock = Load(limits.BaselineSeconds);
            if (Halted()) return BackToStock(_stopped);
            if (stock.Error is { } broken) return BackToStock("Exo didn't tune this. At stock already: " + broken);
            // No reading means no heat limit, so no test.
            if (_peak is null) return BackToStock("Exo can't read this part's temperature, so it didn't tune it.");
            _state = _state with { Before = new TuneMeasure(stock.Score, _peak) };
            part.Settle(stock);

            for (var depth = good + limits.Step; depth <= deepest; depth += limits.Step)
            {
                var step = Step(depth, limits.StepSeconds, limits.StepSeconds * ((deepest - depth) / limits.Step) + limits.FinalSeconds + limits.BaselineSeconds);
                if (step == Outcome.Stopped) return BackToStock(_stopped);
                if (step == Outcome.Failed) break;
                good = depth;
                _state = _state with { Passed = [.. _state.Passed, depth] };
            }

            var settled = good - limits.BackOff;
            for (; settled > 0; settled -= Math.Max(limits.BackOff, limits.Step))
            {
                var step = Step(settled, limits.FinalSeconds, limits.BaselineSeconds);
                if (step == Outcome.Stopped) return BackToStock(_stopped);
                if (step == Outcome.Passed) break;
            }
            if (settled <= 0) return BackToStock("No lower setting held, so you're at stock.");

            // Measured as stock was, the value on and the marker up: this is still a test.
            var tuned = Load(limits.BaselineSeconds);
            if (Halted()) return BackToStock(_stopped);
            if (tuned.Error is { } error)
            {
                part.Stock();
                _state = _state with { Failed = [.. _state.Failed, settled], Passed = [.. _state.Passed.Where(passed => passed < settled)] };
                return BackToStock("The last measurement stopped: " + error);
            }
            var appliedAt = DateTimeOffset.UtcNow;
            _state = _state with { After = new TuneMeasure(tuned.Score, _peak), Applied = settled, SignIn = part.SignIn(settled), AppliedAtUtc = appliedAt, Testing = null, SecondsLeft = null, Phase = "done" };
            store.Save(part.Id, _state);
            store.WriteAppliedAt(appliedAt);
            store.ClearMarker();
            host.Reapply();
            return _state;
        }
        catch (TuneDriverException ex)
        {
            AppLog.Warn("Auto tune: stopped: " + ex.Message);
            return BackToStock(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn("Auto tune: stopped: " + ex.Message);
            return BackToStock("Exo couldn't finish, so you're back at stock.");
        }
    }

    internal TunePartState Reapply()
    {
        var saved = store.Part(part.Id);
        if (saved.Applied <= 0) return saved;
        if (host.Refusal() is { } refused)
        {
            var blocked = saved with { Note = refused };
            store.Save(part.Id, blocked);
            return blocked;
        }
        try
        {
            store.ClearHeld();
            if (part is TuneNvidia nvidia && saved.SignIn is [_, > 0 and var cap]) nvidia.RestoreCap(cap);
            part.Apply(saved.Applied);
            var appliedAt = DateTimeOffset.UtcNow;
            var next = saved with { Phase = "done", Note = null, AppliedAtUtc = appliedAt, SignIn = saved.SignIn ?? part.SignIn(saved.Applied) };
            store.Save(part.Id, next);
            store.WriteAppliedAt(appliedAt);
            host.Reapply();
            return next;
        }
        catch (TuneDriverException ex)
        {
            AppLog.Warn("Auto tune: re-apply failed: " + ex.Message);
            var failed = saved with { Phase = "paused", Note = ex.Message };
            store.Save(part.Id, failed);
            return failed;
        }
    }

    /// <summary>Back to stock in one press. The failed list stays, so a later run stays shallower.</summary>
    internal TunePartState Undo()
    {
        Reconcile(store);
        store.ClearHeld();
        store.ClearAppliedAt();
        part.Stock();
        var next = store.Part(part.Id) with { Phase = "idle", Applied = 0, SignIn = null, AppliedAtUtc = null, Testing = null, SecondsLeft = null, Passed = [], Before = null, After = null, Note = null };
        store.Save(part.Id, next);
        host.Reapply();
        return next;
    }

    private enum Outcome { Passed, Failed, Stopped }

    private Outcome Step(int depth, int seconds, int afterThis)
    {
        if (Halted()) return Outcome.Stopped;
        part.Limits.Check(depth);
        Update(depth, seconds + afterThis);
        store.WriteMarker(part.Id, depth);
        _touched = true;
        part.Apply(depth);
        if (part.Read() is { } on && on != depth)
        {
            _stopped = "Your hardware didn't take that value, so you're back at stock.";
            return Outcome.Stopped;
        }
        var run = Load(seconds);
        if (Halted()) return Outcome.Stopped;
        if (run.Error is null) return Outcome.Passed;
        // Stock before the marker goes: a value failing its test is the one most likely to take the PC down.
        part.Stock();
        store.ClearMarker();
        _state = _state with { Failed = _state.Failed.Contains(depth) ? _state.Failed : [.. _state.Failed, depth] };
        return Outcome.Failed;
    }

    /// <summary>One run of the load, with what Windows logged during it counted as its error.</summary>
    private TuneRun Load(int seconds)
    {
        _peak = null;
        _errorBase = host.HardwareErrors();
        var run = test.Run(TimeSpan.FromSeconds(seconds), Check);
        return run.Error is null && host.HardwareErrors() > _errorBase ? run with { Error = "Windows logged a hardware error." } : run;
    }

    /// <summary>Whether something outside the part stopped the run: no verdict on the value.</summary>
    private bool Halted()
    {
        if (host.StopAsked()) { _stopped = "Stopped."; return true; }
        if (host.Refusal() is { } refused) { _stopped = refused; return true; }
        return false;
    }

    /// <summary>Why the load must stop: read about once a second through the whole run.</summary>
    private string? Check()
    {
        if (test.TemperatureC() is { } hot)
        {
            _peak = Math.Max(_peak ?? hot, hot);
            if (hot >= part.Limits.StopC) return $"It reached {hot:0} °C, Exo's limit for this part.";
        }
        if (host.StopAsked()) return "Stopped.";
        if (host.Refusal() is { } refused) return refused;
        return host.HardwareErrors() > _errorBase ? "Windows logged a hardware error." : null;
    }

    private void Update(int depth, int secondsLeft)
    {
        _state = _state with { Phase = "testing", Testing = depth, SecondsLeft = secondsLeft };
        store.Save(part.Id, _state);
    }

    private TunePartState BackToStock(string note)
    {
        // Nothing was applied yet: the driver refusing its first call is no reason to ask it again.
        if (_touched)
            try { part.Stock(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Warn("Auto tune: stock didn't go back: " + ex.Message);
                note += " Exo couldn't confirm stock is back, so restart your PC.";
            }
        _state = _state with { Phase = "stopped", Applied = 0, SignIn = null, Testing = null, SecondsLeft = null, Note = note };
        store.Save(part.Id, _state);
        store.ClearMarker();
        host.Reapply();
        return _state;
    }
}

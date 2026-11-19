using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services;

/// <summary>Where Game Mode stands, kept in Keep's record for this PC (so it survives a restart).</summary>
internal enum GameModeState
{
    /// <summary>Exo never wrote it and holds nothing.</summary>
    NotKept,
    /// <summary>Exo set it on and watches it.</summary>
    Held,
    /// <summary>Something else turned it off once after Exo set it; Exo turned it on again and remembers that.</summary>
    PutBackOnce,
    /// <summary>Something else turned it off again, so it is the player's choice: Exo does not write it until the player turns it on in Exo.</summary>
    Yielded,
    /// <summary>The player turned Exo's keeping off in Exo: what was there is back, and Exo does not write it.</summary>
    Off,
}

internal enum GameModeEvent
{
    /// <summary>Exo starts (or a Windows update was noticed) and Game Mode is not on: nothing explains it but Windows or the first run.</summary>
    Start,
    /// <summary>The watch (or a look) found it turned off, by something that is not Exo and not a Windows update.</summary>
    ChangedByAnother,
    /// <summary>A look or the watch's own notice found it on.</summary>
    ReadsOn,
    PlayerTurnedOnInExo,
    PlayerTurnedOffInExo,
    /// <summary>Three changes found within ten seconds: something turns it off as fast as Exo turns it on.</summary>
    FoughtThreeTimesInTenSeconds,
    /// <summary>Exo's uninstaller: what was there comes back, and a reinstall starts over (a value that is the player's, or that Exo stopped keeping, stays as it is).</summary>
    Uninstalling,
}

/// <summary>What a row of <see cref="WindowsGameMode.Table"/> does, in this order: record, write, put back, then the watch.</summary>
[Flags]
internal enum GameModeAction
{
    None = 0,
    /// <summary>Record what the value holds now (a number, or not set) before the first write.</summary>
    Record = 1,
    /// <summary>Write 1 and read it back.</summary>
    TurnOn = 2,
    /// <summary>Watch the key.</summary>
    Watch = 4,
    /// <summary>Stop watching the key.</summary>
    Unwatch = 8,
    /// <summary>Put back exactly what was recorded (deleting the value when it was not set), read it back, and forget the record.</summary>
    PutBack = 16,
    /// <summary>
    /// Record Windows' own default (the value not set) as what it held before. Used where the value was already on and Exo has
    /// no record: an older Exo, the player or Windows may have set it, and what was there before is unknown.
    /// </summary>
    RecordDefault = 32,
}

/// <summary>One row: in <see cref="State"/>, on <see cref="Event"/>, go to <see cref="Next"/> and do <see cref="Action"/>.</summary>
internal sealed record GameModeRow(GameModeState State, GameModeEvent Event, GameModeState Next, GameModeAction Action);

/// <summary>What a look came to: the sentence for the page (null when fine) and whether Exo turned Game Mode on.</summary>
internal sealed record GameModeLook(string? Message, bool Wrote);

/// <summary>The change notification for one registry key, behind a seam so Game Mode's watch is tested with none.</summary>
internal interface IKeyWatch
{
    /// <summary>Calls <paramref name="changed"/> on a pool thread for each write to the key until <see cref="Stop"/>; true when it is watching (also when it already was).</summary>
    bool Start(Action changed);

    /// <summary>Stops watching; a later <see cref="Start"/> starts again.</summary>
    void Stop();
}

/// <summary>
/// Windows Game Mode, kept on, by one table (<see cref="Table"/>, docs/architecture/keep.md, "The Windows side"). Windows keeps
/// it in the user's own registry (<c>HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled</c>, what Settings › Gaming › Game
/// Mode writes; no administrator), and Game Mode is part of Windows itself, so it works with the Xbox Game Bar app removed.
/// Before its first write Exo records what the value held (a number, or not set) in Keep's record; Off puts exactly that
/// back and forgets it. The first time something else turns it off after Exo set it, Exo turns it on again and remembers;
/// the second time, it is the player's choice, and Exo stops writing it (kept across restarts) until the player turns it on
/// in Exo. Something that turns it off three times in ten seconds goes the same way at once.
/// Ways it could fail: fighting the player for ever; a watch that cannot be stopped or started again; Off leaving a value
/// Exo wrote; a record lost between the first write and a crash (it is saved before the write); a feature update's reset
/// taken for the player's choice; writing while the registry cannot say what it held.
/// </summary>
internal sealed class WindowsGameMode(IUserRegistry registry, IKeyWatch watch, Func<Keep.KeepPc> load, Func<Action<Keep.KeepPc>, bool> save, Action<string> log, Func<long>? ticks = null)
{
    internal const string KeyPath = @"Software\Microsoft\GameBar";
    internal const string ValueName = "AutoGameModeEnabled";

    /// <summary>Changes found within this long count together for the fast way into Yielded.</summary>
    internal const long FightWindowMilliseconds = 10_000;

    /// <summary>Changes found within the window that make it a fight.</summary>
    internal const int FightCount = 3;

    private static GameModeRow R(GameModeState state, GameModeEvent ev, GameModeState next, GameModeAction action = GameModeAction.None) => new(state, ev, next, action);

    private const GameModeAction Hold = GameModeAction.TurnOn | GameModeAction.Watch;
    private const GameModeAction First = GameModeAction.Record | GameModeAction.TurnOn | GameModeAction.Watch;

    /// <summary>Every state and event and what happens. A test walks every row.</summary>
    internal static readonly IReadOnlyList<GameModeRow> Table =
    [
        R(GameModeState.NotKept, GameModeEvent.Start, GameModeState.Held, First),
        R(GameModeState.NotKept, GameModeEvent.ChangedByAnother, GameModeState.NotKept),
        R(GameModeState.NotKept, GameModeEvent.ReadsOn, GameModeState.Held, GameModeAction.RecordDefault | GameModeAction.Watch),
        R(GameModeState.NotKept, GameModeEvent.PlayerTurnedOnInExo, GameModeState.Held, First),
        R(GameModeState.NotKept, GameModeEvent.PlayerTurnedOffInExo, GameModeState.Off, GameModeAction.Unwatch),
        R(GameModeState.NotKept, GameModeEvent.FoughtThreeTimesInTenSeconds, GameModeState.NotKept),
        R(GameModeState.NotKept, GameModeEvent.Uninstalling, GameModeState.NotKept),

        R(GameModeState.Held, GameModeEvent.Start, GameModeState.Held, Hold),
        R(GameModeState.Held, GameModeEvent.ChangedByAnother, GameModeState.PutBackOnce, Hold),
        R(GameModeState.Held, GameModeEvent.ReadsOn, GameModeState.Held, GameModeAction.Watch),
        R(GameModeState.Held, GameModeEvent.PlayerTurnedOnInExo, GameModeState.Held, Hold),
        R(GameModeState.Held, GameModeEvent.PlayerTurnedOffInExo, GameModeState.Off, GameModeAction.Unwatch | GameModeAction.PutBack),
        R(GameModeState.Held, GameModeEvent.FoughtThreeTimesInTenSeconds, GameModeState.Yielded, GameModeAction.Unwatch),
        R(GameModeState.Held, GameModeEvent.Uninstalling, GameModeState.NotKept, GameModeAction.Unwatch | GameModeAction.PutBack),

        R(GameModeState.PutBackOnce, GameModeEvent.Start, GameModeState.PutBackOnce, Hold),
        R(GameModeState.PutBackOnce, GameModeEvent.ChangedByAnother, GameModeState.Yielded, GameModeAction.Unwatch),
        R(GameModeState.PutBackOnce, GameModeEvent.ReadsOn, GameModeState.PutBackOnce, GameModeAction.Watch),
        R(GameModeState.PutBackOnce, GameModeEvent.PlayerTurnedOnInExo, GameModeState.Held, Hold),
        R(GameModeState.PutBackOnce, GameModeEvent.PlayerTurnedOffInExo, GameModeState.Off, GameModeAction.Unwatch | GameModeAction.PutBack),
        R(GameModeState.PutBackOnce, GameModeEvent.FoughtThreeTimesInTenSeconds, GameModeState.Yielded, GameModeAction.Unwatch),
        R(GameModeState.PutBackOnce, GameModeEvent.Uninstalling, GameModeState.NotKept, GameModeAction.Unwatch | GameModeAction.PutBack),

        R(GameModeState.Yielded, GameModeEvent.Start, GameModeState.Yielded),
        R(GameModeState.Yielded, GameModeEvent.ChangedByAnother, GameModeState.Yielded),
        R(GameModeState.Yielded, GameModeEvent.ReadsOn, GameModeState.Yielded),
        R(GameModeState.Yielded, GameModeEvent.PlayerTurnedOnInExo, GameModeState.Held, First),
        R(GameModeState.Yielded, GameModeEvent.PlayerTurnedOffInExo, GameModeState.Off, GameModeAction.Unwatch),
        R(GameModeState.Yielded, GameModeEvent.FoughtThreeTimesInTenSeconds, GameModeState.Yielded),
        R(GameModeState.Yielded, GameModeEvent.Uninstalling, GameModeState.Yielded),

        R(GameModeState.Off, GameModeEvent.Start, GameModeState.Off),
        R(GameModeState.Off, GameModeEvent.ChangedByAnother, GameModeState.Off),
        R(GameModeState.Off, GameModeEvent.ReadsOn, GameModeState.Off),
        R(GameModeState.Off, GameModeEvent.PlayerTurnedOnInExo, GameModeState.Held, First),
        R(GameModeState.Off, GameModeEvent.PlayerTurnedOffInExo, GameModeState.Off),
        R(GameModeState.Off, GameModeEvent.FoughtThreeTimesInTenSeconds, GameModeState.Off),
        R(GameModeState.Off, GameModeEvent.Uninstalling, GameModeState.Off),
    ];

    private static readonly Dictionary<(GameModeState, GameModeEvent), GameModeRow> Index = Table.ToDictionary(row => (row.State, row.Event));

    /// <summary>The row for a state and an event; every pair has one.</summary>
    internal static GameModeRow Row(GameModeState state, GameModeEvent ev) => Index[(state, ev)];

    /// <summary>Exo writes 1 over anything but a DWORD 1: off (0), never set, or another type.</summary>
    internal static bool NeedsWrite(object? value) => value is not int on || on != 1;

    /// <summary>The one Exo uses: HKCU, the real notification, Keep's record for this PC, and app.log.</summary>
    internal static WindowsGameMode Shared { get; } = new(UserRegistry.Shared, new RegistryKeyWatch(KeyPath), () => Keep.Load().Pc, change => Keep.Update(doc => change(doc.Pc)), AppLog.Info);

    /// <summary>At Exo's start and whenever PC › Overview is read: Game Mode on and watched, as the table says.</summary>
    internal static void KeepOn() => Shared.Look();

    /// <summary>The player turns Game Mode on in Exo: null when done, else the sentence to show.</summary>
    internal static string? On() => Shared.TurnOn();

    /// <summary>The player turns Game Mode off in Exo: what was there comes back. Null when done, else the sentence to show.</summary>
    internal static string? Off() => Shared.TurnOff();

    private readonly Lock gate = new();
    private readonly Queue<long> changes = new();
    private readonly Func<long> clock = ticks ?? (() => Environment.TickCount64);

    private const string OnSentence = "Windows didn't turn Game Mode on.";
    private const string OffSentence = "Windows didn't put Game Mode back.";
    private const string SaveSentence = "Exo couldn't save what Game Mode was set to, so nothing changed.";

    private static bool Soft(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    /// <summary>Where Game Mode stands now: what the record says, NotKept when it says nothing.</summary>
    internal GameModeState State => load().GameMode?.State ?? GameModeState.NotKept;

    /// <summary>A look at the value: on, off, or changed. <paramref name="windowsUpdated"/>: a Windows update explains a reset, so it is not the player's change. Null when fine, else the sentence.</summary>
    internal string? Look(bool windowsUpdated = false) => LookReport(windowsUpdated).Message;

    /// <summary><see cref="Look"/>, and whether it wrote the value (turned Game Mode on) in doing it.</summary>
    internal GameModeLook LookReport(bool windowsUpdated = false)
    {
        if (HostMode.IsDisposable) return new(HostMode.Refusal, false);
        lock (gate)
        {
            if (!Read(out var now)) return new(null, false);
            var ev = !NeedsWrite(RegistryValues.Plain(now)) ? GameModeEvent.ReadsOn
                : State == GameModeState.NotKept || windowsUpdated ? GameModeEvent.Start
                : GameModeEvent.ChangedByAnother;
            return Act(ev, now);
        }
    }

    /// <summary>The player turns it on in Exo.</summary>
    internal string? TurnOn() => Deliver(GameModeEvent.PlayerTurnedOnInExo);

    /// <summary>The player turns it off in Exo.</summary>
    internal string? TurnOff() => Deliver(GameModeEvent.PlayerTurnedOffInExo);

    /// <summary>The uninstaller: what Exo replaced comes back and nothing is remembered, so a reinstall starts over. Null when done, else the sentence.</summary>
    internal string? GiveBack() => Deliver(GameModeEvent.Uninstalling);

    /// <summary>One event, by the table: the walk's way in, and the one place an event is acted on.</summary>
    internal string? Deliver(GameModeEvent ev)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        lock (gate)
        {
            if (!Read(out var now)) return ev == GameModeEvent.PlayerTurnedOffInExo ? OffSentence : OnSentence;
            return Act(ev, now).Message;
        }
    }

    /// <summary>The watch's notice: something wrote the key. Found on (Exo's own write signals too) is nothing; found off is a change by another, or a fight.</summary>
    private void OnChanged()
    {
        if (HostMode.IsDisposable) return;
        lock (gate)
        {
            if (!Read(out var now)) return;
            if (!NeedsWrite(RegistryValues.Plain(now)))
            {
                Act(GameModeEvent.ReadsOn, now);
                return;
            }
            var at = clock();
            while (changes.TryPeek(out var then) && at - then > FightWindowMilliseconds) changes.Dequeue();
            changes.Enqueue(at);
            Act(changes.Count >= FightCount ? GameModeEvent.FoughtThreeTimesInTenSeconds : GameModeEvent.ChangedByAnother, now);
        }
    }

    /// <summary>
    /// Does what the table says for the event, in this order: stop watching (so Exo's own put back is never taken for
    /// someone else's change); save the new state and what the value held (before the first write, so a crash leaves the
    /// way back); write 1 and read it back; put back what was recorded and read it back; then watch. A write that did not
    /// take takes the saved state back, so no record stands for a write that never was.
    /// </summary>
    private GameModeLook Act(GameModeEvent ev, RegValue? now)
    {
        var kept = load().GameMode;
        var state = kept?.State ?? GameModeState.NotKept;
        var row = Row(state, ev);
        var action = row.Action;
        if (action.HasFlag(GameModeAction.Unwatch)) watch.Stop();

        var before = action.HasFlag(GameModeAction.Record) ? now
            : action.HasFlag(GameModeAction.RecordDefault) ? null
            : kept?.Before;
        // Only a held value keeps what it replaced: Yielded and Off are the player's, and Off has put it back.
        var keeps = row.Next is GameModeState.Held or GameModeState.PutBackOnce;
        var next = keeps ? before : null;
        var changed = row.Next != state || next != kept?.Before;
        // The uninstaller never waits on the record: once Exo is gone nothing else would put the value back.
        if (changed && !Save(row.Next, next) && ev != GameModeEvent.Uninstalling)
        {
            log("Game Mode: " + SaveSentence);
            return new(SaveSentence, false);
        }
        if (action.HasFlag(GameModeAction.RecordDefault))
            log("Game Mode: already on with nothing recorded, so what it held before is unknown; Off puts Windows' own default back.");

        var wrote = false;
        if (action.HasFlag(GameModeAction.TurnOn) && NeedsWrite(RegistryValues.Plain(now)))
        {
            if (!Put(RegValue.DWord(1)))
            {
                if (changed) Save(state, kept?.Before);
                log($"Game Mode: Windows didn't take turning it on (was {RegistryValues.Describe(now)}).");
                return new(OnSentence, false);
            }
            wrote = true;
        }
        if (action.HasFlag(GameModeAction.PutBack) && !Put(before))
        {
            if (changed) Save(state, kept?.Before);
            log($"Game Mode: Windows didn't take putting it back to {RegistryValues.Describe(before)}.");
            return new(OffSentence, false);
        }
        if (action.HasFlag(GameModeAction.Watch) && !watch.Start(OnChanged)) log("Game Mode: not watched.");

        Say(state, ev, row.Next, before, wrote);
        return new(null, wrote);
    }

    /// <summary>One line in app.log for what a row did, and nothing for what it did not.</summary>
    private void Say(GameModeState state, GameModeEvent ev, GameModeState next, RegValue? before, bool wrote)
    {
        if (ev == GameModeEvent.FoughtThreeTimesInTenSeconds && next == GameModeState.Yielded && state != GameModeState.Yielded)
            log("Game Mode: something turns it off as fast as Exo turns it on, so it is yours now and Exo stops writing it until you turn it on in Exo.");
        else if (ev == GameModeEvent.ChangedByAnother && next == GameModeState.PutBackOnce)
            log("Game Mode: something turned it off, so Exo turned it on again; if it is turned off again, that is your choice.");
        else if (ev == GameModeEvent.ChangedByAnother && next == GameModeState.Yielded && state != GameModeState.Yielded)
            log("Game Mode: turned off again, so it is yours now and Exo stops writing it until you turn it on in Exo.");
        else if (ev == GameModeEvent.PlayerTurnedOffInExo && next == GameModeState.Off && state != GameModeState.Off)
            log($"Game Mode: Exo stops keeping it on{(state is GameModeState.Held or GameModeState.PutBackOnce ? $"; put back to {(before is null ? "Windows' own default (not set)" : RegistryValues.Describe(before))}" : "")}.");
        else if (ev == GameModeEvent.Uninstalling && next == GameModeState.NotKept && state != GameModeState.NotKept)
            log($"Game Mode: put back to {(before is null ? "Windows' own default (not set)" : RegistryValues.Describe(before))} for the uninstall.");
        else if (wrote && state == GameModeState.NotKept)
            log($"Game Mode: turned on (was {RegistryValues.Describe(before)}).");
        else if (wrote)
            log(ev == GameModeEvent.Start ? "Game Mode: turned on again after Windows changed it." : "Game Mode: turned on.");
    }

    private bool Read(out RegValue? value)
    {
        try
        {
            value = registry.Get(KeyPath, ValueName);
            return true;
        }
        catch (Exception ex) when (Soft(ex))
        {
            value = null;
            log($"Game Mode: couldn't be read: {ex.GetType().Name}.");
            return false;
        }
    }

    /// <summary>Sets the value (deletes it when <paramref name="value"/> is null) and reads it back: true when it reads as written.</summary>
    private bool Put(RegValue? value)
    {
        try
        {
            if (value is null) registry.Delete(KeyPath, ValueName);
            else registry.Set(KeyPath, ValueName, value);
            return registry.Get(KeyPath, ValueName) == value;
        }
        catch (Exception ex) when (Soft(ex))
        {
            log("Game Mode: Windows refused: " + ex.Message);
            return false;
        }
    }

    private bool Save(GameModeState state, RegValue? before) =>
        save(pc => pc.GameMode = state == GameModeState.NotKept && before is null ? null : new Keep.GameModeKeep { State = state, Before = before });
}

/// <summary>
/// Registry change notification for one HKCU key. <see cref="Stop"/> lets go of everything, so <see cref="Start"/> starts
/// again in the same run (the old watch unregistered its handle but kept the field, so it could never start again). The
/// callback re-arms the notification before it tells the caller, so nothing written in between is missed; the caller's
/// own write signals once more and finds the value as it should be.
/// </summary>
internal sealed class RegistryKeyWatch(string keyPath) : IKeyWatch
{
    private const uint NotifyLastSet = 0x4;              // REG_NOTIFY_CHANGE_LAST_SET
    private const uint NotifyThreadAgnostic = 0x10000000; // REG_NOTIFY_THREAD_AGNOSTIC: a pool thread that ends doesn't end the watch
    private const int KeyDeleted = 1018;                  // ERROR_KEY_DELETED

    private readonly Lock gate = new();
    private RegistryKey? _key;
    private AutoResetEvent? _changed;
    private RegisteredWaitHandle? _wait;
    private bool _running;

    public bool Start(Action changed)
    {
        lock (gate)
        {
            if (_running) return true;
            try
            {
                _changed = new AutoResetEvent(false);
                if (!Arm())
                {
                    Release();
                    return false;
                }
                _running = true;
                _wait = ThreadPool.RegisterWaitForSingleObject(_changed, (_, _) =>
                {
                    lock (gate)
                    {
                        if (!_running || !Arm()) return;
                    }
                    changed();
                }, null, Timeout.Infinite, executeOnlyOnce: false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ObjectDisposedException)
            {
                AppLog.Warn("Game Mode: not watched: " + ex.Message);
                Release();
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (gate) Release();
    }

    /// <summary>Unregisters the wait and clears every field, so the next <see cref="Start"/> begins clean.</summary>
    private void Release()
    {
        _running = false;
        _wait?.Unregister(null);
        _wait = null;
        _changed?.Dispose();
        _changed = null;
        _key?.Dispose();
        _key = null;
    }

    /// <summary>One notification for the next write to the key; a key deleted since (a debloat script) is opened again.</summary>
    private bool Arm()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            _key ??= Registry.CurrentUser.CreateSubKey(keyPath, writable: false);
            var status = RegNotifyChangeKeyValue(_key.Handle, false, NotifyLastSet | NotifyThreadAgnostic, _changed!.SafeWaitHandle, true);
            if (status == 0) return true;
            _key.Dispose();
            _key = null;
            if (status != KeyDeleted)
            {
                AppLog.Warn($"Game Mode: not watched (Windows said {status}).");
                return false;
            }
        }
        return false;
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, uint filter,
        SafeWaitHandle changed, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}

using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>Where Game Bar's keeping stands, in Keep's record for this PC.</summary>
internal enum GameBarState
{
    /// <summary>Exo never wrote it and holds no record.</summary>
    NotKept,
    /// <summary>Exo keeps it off and holds what each value and handler was.</summary>
    Held,
    /// <summary>The player turned Exo's keeping off in Exo: what was there is back and Exo does not write it again.</summary>
    Off,
}

internal enum GameBarEvent
{
    /// <summary>Exo starts (or a Windows update was noticed): keep what is not already as Exo wants it.</summary>
    Start,
    /// <summary>The player asks Exo to keep Game Bar off.</summary>
    PlayerTurnedOn,
    /// <summary>The player asks Exo to stop: what was there comes back, and Exo does not write it again.</summary>
    PlayerTurnedOff,
    /// <summary>Exo's uninstaller: what was there comes back, and a reinstall starts over.</summary>
    Uninstalling,
}

[Flags]
internal enum GameBarAction
{
    None = 0,
    /// <summary>Record what each of the four values and three handlers holds, before the first write.</summary>
    Record = 1,
    /// <summary>Write what is not already as Exo wants it, and read it back.</summary>
    Write = 2,
    /// <summary>Put back exactly what was recorded, read it back, and forget the record.</summary>
    PutBack = 4,
}

internal sealed record GameBarRow(GameBarState State, GameBarEvent Event, GameBarState Next, GameBarAction Action);

/// <summary>What one look at Game Bar came to: the sentence for the page (null when all took), the parts that did not take, and how many it changed.</summary>
internal sealed record GameBarResult(string? Message, IReadOnlyList<string> DidNotTake, int Changed);

/// <summary>
/// Xbox Game Bar, kept off: a built-in default which records clips and replays itself (Exo Capture). Its background recording
/// and Game DVR, and its Win+G and controller buttons, are off in the user's own registry (what Settings › Gaming writes; no
/// administrator). Its links (ms-gamebar, ms-gamebarservices, ms-gamingoverlay), which games and Windows still open, go to a
/// program that does nothing (Win11Debloat's fix): with the Game Bar app removed, by a debloat or by Exo's own Apps, Windows
/// otherwise asks to look for an app in the Microsoft Store each time. Only settings change, never the app: AMD's 3D V-Cache
/// driver reads Game Bar's game detection to park a dual-CCD X3D's other cores. Game Mode stays on (<see cref="WindowsGameMode"/>).
/// Before its first write Exo records what each of the four values and each link's whole key held (Keep's record for this PC);
/// <see cref="Off"/> puts exactly that back, reads it back, and forgets it, and Exo does not turn it off again at the next
/// start. Written at start, and only where a value differs. The table (<see cref="Table"/>) is the only place a step is decided.
/// Ways it could fail: a write nothing recorded first; a value that did not take reported as done; Off leaving a value or a
/// handler Exo wrote; Exo turning it off again after the player turned the keeping off; the uninstaller leaving it all behind.
/// </summary>
internal sealed class WindowsGameBar(IUserRegistry registry, Func<Keep.KeepPc> load, Func<Action<Keep.KeepPc>, bool> save, Action<string> log)
{
    /// <summary>Game Bar's links that games and Windows open.</summary>
    internal static readonly string[] Links = ["ms-gamebar", "ms-gamebarservices", "ms-gamingoverlay"];

    /// <summary>The DWORDs that turn Game Bar's recording and its buttons off: key, value name.</summary>
    internal static readonly (string Key, string Name)[] Dwords =
    [
        (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
        (@"System\GameConfigStore", "GameDVR_Enabled"),
        (@"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled"),
        (@"Software\Microsoft\GameBar", "GamepadNexusChordEnabled"),
    ];

    /// <summary>What a link's open command is: Windows' own systray.exe, which does nothing when started.</summary>
    internal static string Nothing => Path.Combine(Environment.SystemDirectory, "systray.exe");

    private static GameBarRow R(GameBarState state, GameBarEvent ev, GameBarState next, GameBarAction action = GameBarAction.None) => new(state, ev, next, action);

    /// <summary>Every state and event and what happens. A test walks every row.</summary>
    internal static readonly IReadOnlyList<GameBarRow> Table =
    [
        R(GameBarState.NotKept, GameBarEvent.Start, GameBarState.Held, GameBarAction.Record | GameBarAction.Write),
        R(GameBarState.NotKept, GameBarEvent.PlayerTurnedOn, GameBarState.Held, GameBarAction.Record | GameBarAction.Write),
        R(GameBarState.NotKept, GameBarEvent.PlayerTurnedOff, GameBarState.Off),
        R(GameBarState.NotKept, GameBarEvent.Uninstalling, GameBarState.NotKept),

        R(GameBarState.Held, GameBarEvent.Start, GameBarState.Held, GameBarAction.Write),
        R(GameBarState.Held, GameBarEvent.PlayerTurnedOn, GameBarState.Held, GameBarAction.Write),
        R(GameBarState.Held, GameBarEvent.PlayerTurnedOff, GameBarState.Off, GameBarAction.PutBack),
        R(GameBarState.Held, GameBarEvent.Uninstalling, GameBarState.NotKept, GameBarAction.PutBack),

        R(GameBarState.Off, GameBarEvent.Start, GameBarState.Off),
        R(GameBarState.Off, GameBarEvent.PlayerTurnedOn, GameBarState.Held, GameBarAction.Record | GameBarAction.Write),
        R(GameBarState.Off, GameBarEvent.PlayerTurnedOff, GameBarState.Off),
        R(GameBarState.Off, GameBarEvent.Uninstalling, GameBarState.Off),
    ];

    private static readonly Dictionary<(GameBarState, GameBarEvent), GameBarRow> Index = Table.ToDictionary(row => (row.State, row.Event));

    /// <summary>The row for a state and an event; every pair has one.</summary>
    internal static GameBarRow Row(GameBarState state, GameBarEvent ev) => Index[(state, ev)];

    /// <summary>The one Exo uses: HKCU, Keep's record for this PC, and app.log.</summary>
    internal static WindowsGameBar Shared { get; } = new(UserRegistry.Shared, () => Keep.Load().Pc, change => Keep.Update(doc => change(doc.Pc)), AppLog.Info);

    /// <summary>At Exo's start: what is not already as Exo wants it is written, unless the player turned the keeping off.</summary>
    internal static void KeepOff() => Shared.Start();

    /// <summary>The player asks Exo to keep Game Bar off. Null when done, else the sentence to show.</summary>
    internal static string? On() => Shared.TurnOn().Message;

    /// <summary>The player asks Exo to stop keeping it off: what was there comes back. Null when done, else the sentence to show.</summary>
    internal static string? Off() => Shared.TurnOff().Message;

    private const string DidNotTakeSentence = "Windows didn't take every Game Bar setting.";
    private const string PutBackSentence = "Windows didn't put every Game Bar setting back.";
    private const string SaveSentence = "Exo couldn't save what Game Bar was set to, so nothing changed.";
    private const string OffNotSavedSentence = "Game Bar is back as it was, but Exo couldn't save that you turned its keeping off.";

    private readonly Lock gate = new();

    private static bool Soft(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static string HandlerKey(string link) => @"Software\Classes\" + link;

    /// <summary>What a link's key holds when Exo has kept it off: the key, the value's name ("" is the default) and what it reads.</summary>
    private static (string Key, string Name, RegValue Value)[] Wanted(string link) =>
    [
        (HandlerKey(link), "", RegValue.Str("URL:" + link)),
        (HandlerKey(link), "URL Protocol", RegValue.Str("")),
        (HandlerKey(link), "NoOpenWith", RegValue.Str("")),
        (HandlerKey(link) + @"\shell\open\command", "", RegValue.Str(Nothing)),
    ];

    /// <summary>Where the keeping stands now.</summary>
    internal GameBarState State => load().GameBar?.State ?? GameBarState.NotKept;

    /// <summary>Exo's start, or a Windows update noticed.</summary>
    internal GameBarResult Start() => Deliver(GameBarEvent.Start);

    internal GameBarResult TurnOn() => Deliver(GameBarEvent.PlayerTurnedOn);

    /// <summary>The player's Off, or (<paramref name="player"/> false) the uninstaller's, which forgets the choice so a reinstall starts over.</summary>
    internal GameBarResult TurnOff(bool player = true) => Deliver(player ? GameBarEvent.PlayerTurnedOff : GameBarEvent.Uninstalling);

    /// <summary>
    /// One event, by the table, in this order: record what each value and handler holds (what cannot be read is not recorded
    /// and so not written); save the new state with the record, before the first write; put back what was recorded and read
    /// it back (a part that did not come back takes the saved state back, so Off can be tried again); write what is not
    /// already as Exo wants it and read it back.
    /// </summary>
    internal GameBarResult Deliver(GameBarEvent ev)
    {
        if (HostMode.IsDisposable) return new(HostMode.Refusal, [], 0);
        lock (gate)
        {
            var before = load().GameBar;
            var state = before?.State ?? GameBarState.NotKept;
            var row = Row(state, ev);
            var action = row.Action;
            List<string> failed = [];
            var values = before?.Values ?? [];
            var handlers = before?.Handlers ?? [];
            if (action.HasFlag(GameBarAction.Record)) (values, handlers) = Snapshot(failed);

            var holds = row.Next == GameBarState.Held;
            var next = new Keep.GameBarKeep { State = row.Next, Values = holds ? values : [], Handlers = holds ? handlers : [] };
            var saved = action.HasFlag(GameBarAction.Record) || action.HasFlag(GameBarAction.PutBack) || row.Next != state;
            if (saved && !save(pc => pc.GameBar = next))
            {
                // Giving back never waits on the record: once Exo is gone nothing else would put these back, and a player
                // who asked for Off gets the values back even when the choice itself cannot be kept.
                if (action.HasFlag(GameBarAction.PutBack))
                {
                    var back = PutBack(values, handlers, failed);
                    if (failed.Count > 0)
                    {
                        log($"Game Bar: Windows didn't take putting back {string.Join(", ", failed)}, and Exo's record couldn't be saved.");
                        return new(PutBackSentence, failed, back);
                    }
                    log($"Game Bar: put back ({back} part{(back == 1 ? "" : "s")} as they were); Exo's record couldn't be saved.");
                    return new(ev == GameBarEvent.Uninstalling ? null : OffNotSavedSentence, [], back);
                }
                log("Game Bar: " + SaveSentence);
                return new(SaveSentence, [], 0);
            }

            var count = 0;
            if (action.HasFlag(GameBarAction.PutBack))
            {
                count = PutBack(values, handlers, failed);
                if (failed.Count > 0)
                {
                    save(pc => pc.GameBar = before);
                    log($"Game Bar: Windows didn't take putting back {string.Join(", ", failed)}; Exo keeps what it recorded, so Off can be tried again.");
                    return new(PutBackSentence, failed, count);
                }
            }
            if (action.HasFlag(GameBarAction.Write)) count = Write(values, handlers, failed);

            Say(state, row, count, failed);
            return new(failed.Count == 0 ? null : DidNotTakeSentence, failed, count);
        }
    }

    private void Say(GameBarState state, GameBarRow row, int count, List<string> failed)
    {
        var did = failed.Count == 0 ? "" : $"; Windows didn't take {string.Join(", ", failed)}";
        if (row.Action.HasFlag(GameBarAction.PutBack)) log($"Game Bar: put back ({count} part{(count == 1 ? "" : "s")} as they were){did}.");
        else if (row.Action.HasFlag(GameBarAction.Write) && (count > 0 || failed.Count > 0)) log($"Game Bar: kept off ({count} setting{(count == 1 ? "" : "s")} written){did}.");
        else if (row.Event == GameBarEvent.PlayerTurnedOff && state != GameBarState.Off) log("Game Bar: Exo stops keeping it off.");
    }

    /// <summary>What each of the four values and three handlers holds now; one that cannot be read (or is too big to keep) is named in <paramref name="failed"/> and left out.</summary>
    private (List<Keep.GameBarValueKeep> Values, List<Keep.GameBarHandlerKeep> Handlers) Snapshot(List<string> failed)
    {
        List<Keep.GameBarValueKeep> values = [];
        List<Keep.GameBarHandlerKeep> handlers = [];
        foreach (var (key, name) in Dwords)
        {
            try { values.Add(new Keep.GameBarValueKeep { Key = key, Name = name, Before = registry.Get(key, name) }); }
            catch (Exception ex) when (Soft(ex)) { failed.Add(name); }
        }
        foreach (var link in Links)
        {
            try { handlers.Add(new Keep.GameBarHandlerKeep { Link = link, Before = registry.ReadTree(HandlerKey(link)) }); }
            catch (Exception ex) when (Soft(ex)) { failed.Add(link); }
        }
        return (values, handlers);
    }

    /// <summary>Writes what is not already as Exo wants it, and reads each back; how many took, with the ones that did not named in <paramref name="failed"/>.</summary>
    private int Write(List<Keep.GameBarValueKeep> values, List<Keep.GameBarHandlerKeep> handlers, List<string> failed)
    {
        var done = 0;
        foreach (var value in values)
        {
            try
            {
                if (registry.Get(value.Key, value.Name) is { Kind: Microsoft.Win32.RegistryValueKind.DWord, Number: 0 }) continue;
                registry.Set(value.Key, value.Name, RegValue.DWord(0));
                if (registry.Get(value.Key, value.Name) is { Kind: Microsoft.Win32.RegistryValueKind.DWord, Number: 0 }) done++;
                else failed.Add(value.Name);
            }
            catch (Exception ex) when (Soft(ex)) { failed.Add(value.Name); }
        }
        foreach (var handler in handlers)
        {
            try
            {
                var wanted = Wanted(handler.Link);
                if (wanted.All(w => registry.Get(w.Key, w.Name) == w.Value)) continue;
                foreach (var (key, name, value) in wanted) registry.Set(key, name, value);
                if (wanted.All(w => registry.Get(w.Key, w.Name) == w.Value)) done++;
                else failed.Add(handler.Link);
            }
            catch (Exception ex) when (Soft(ex)) { failed.Add(handler.Link); }
        }
        return done;
    }

    /// <summary>Puts every recorded value and handler back exactly (deleting what was not there) and reads each back; how many came back, the others named in <paramref name="failed"/>.</summary>
    private int PutBack(List<Keep.GameBarValueKeep> values, List<Keep.GameBarHandlerKeep> handlers, List<string> failed)
    {
        var done = 0;
        foreach (var value in values)
        {
            try
            {
                if (value.Before is null) registry.Delete(value.Key, value.Name);
                else registry.Set(value.Key, value.Name, value.Before);
                if (registry.Get(value.Key, value.Name) == value.Before) done++;
                else failed.Add(value.Name);
            }
            catch (Exception ex) when (Soft(ex)) { failed.Add(value.Name); }
        }
        foreach (var handler in handlers)
        {
            try
            {
                var key = HandlerKey(handler.Link);
                if (handler.Before is null) registry.DeleteTree(key);
                else registry.WriteTree(key, handler.Before);
                var now = registry.ReadTree(key);
                if (handler.Before is null ? now is null : handler.Before.SameAs(now)) done++;
                else failed.Add(handler.Link);
            }
            catch (Exception ex) when (Soft(ex)) { failed.Add(handler.Link); }
        }
        return done;
    }
}

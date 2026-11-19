using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// What the hider does to a window, over the seam (<see cref="IStoreWindows"/>, <see cref="IStoreProcesses"/>): a window
/// belongs to a store client by its program's path (<see cref="StoreClientIdentity"/>), its kind is chosen by
/// <see cref="StoreWindowRules"/>, a prompt is never hidden, and the style a shell had is put back exactly. Everything the
/// real hider does to a window goes through here, so it has tests that use a fake window source.
/// </summary>
internal sealed class StoreWindowEngine(
    IStoreWindows windows,
    IStoreProcesses processes,
    Func<string, StoreKind?> storeOfName,
    Func<StoreKind, string, bool> belongsToStore,
    Func<StoreKind, bool> isQuiet,
    Func<StoreKind, bool> isSuspended,
    Action<string> log,
    Func<long>? tickMs = null)
{
    private const long ProgramCacheMs = 5000;

    private sealed record Hidden(long OriginalExStyle, StoreWindowKind Kind);

    private readonly object _gate = new();
    private readonly Func<long> _tick = tickMs ?? (() => Environment.TickCount64);
    private readonly Dictionary<nint, Hidden> _hidden = [];
    private readonly HashSet<nint> _brought = [];
    private readonly HashSet<(StoreKind Store, string Class, StoreWindowKind Kind)> _classesSeen = [];
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (long Until, string? Name, StoreKind? Store)> _programs = [];

    private static readonly string[] AntiCheatPrefixes = ["EasyAntiCheat", "BEService", "BattlEye", "start_protected_game"];

    /// <summary>
    /// Anti-cheat and Riot's games are never touched, whatever folder they sit in (the lists the close paths already use:
    /// <see cref="StoreClientActivity.AntiCheatProcessNames"/>, <c>RiotCli.IsProtectedProcess</c>).
    /// </summary>
    internal static bool IsExcludedProgram(string name) =>
        Cli.RiotCli.IsProtectedProcess(name) ||
        StoreClientActivity.AntiCheatProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        AntiCheatPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
        StoreClientActivity.HostedGameProcessNames(StoreKind.Riot).Contains(name, StringComparer.OrdinalIgnoreCase);

    private (string? Name, StoreKind? Store) Program(int processId)
    {
        var now = _tick();
        lock (_gate)
            if (_programs.TryGetValue(processId, out var cached) && now < cached.Until) return (cached.Name, cached.Store);
        var name = processes.NameOf(processId);
        StoreKind? store = null;
        if (name is not null && !IsExcludedProgram(name) && storeOfName(name) is { } candidate)
        {
            var path = processes.PathOf(processId);
            if (path is null)
            {
                lock (_gate)
                    if (_unreadable.Add(name))
                        log($"Store windows: the program path of {name} cannot be read, so it is left alone.");
            }
            else if (belongsToStore(candidate, path)) store = candidate;
        }
        lock (_gate) _programs[processId] = (now + ProgramCacheMs, name, store);
        return (name, store);
    }

    /// <summary>The program's name, kept for a few seconds: the hook sees every window any program makes.</summary>
    internal string? NameOf(int processId) => Program(processId).Name;

    /// <summary>The store whose client this process is (by path), or null.</summary>
    internal StoreKind? StoreOf(int processId) => Program(processId).Store;

    internal bool IsHidden(nint handle)
    {
        lock (_gate) return _hidden.ContainsKey(handle);
    }

    /// <summary>One sweep: every window of these processes, each decided on its own.</summary>
    internal void Sweep(IReadOnlySet<int> processIds)
    {
        var all = windows.Enumerate(processIds);
        foreach (var window in all) Handle(window);
        Forget(all);
    }

    internal void Handle(nint handle)
    {
        if (windows.Read(handle) is { } window) Handle(window);
    }

    internal void Handle(StoreWindow window)
    {
        if (StoreOf(window.ProcessId) is not { } store) return;
        var kind = StoreWindowRules.Classify(window);
        lock (_gate)
        {
            if (_classesSeen.Add((store, window.ClassName, kind)))
                log($"Store windows: {store} window class \"{window.ClassName}\" is a {kind}.");
            switch (kind)
            {
                case StoreWindowKind.Prompt:
                    ShowPrompt(window, store);
                    break;
                case StoreWindowKind.Shell or StoreWindowKind.Toast:
                    if (window.Visible && isQuiet(store) && !isSuspended(store)) Hide(window, kind);
                    break;
            }
        }
    }

    private void Hide(StoreWindow window, StoreWindowKind kind)
    {
        _hidden.TryAdd(window.Handle, new Hidden(window.ExStyle, kind));
        // Off the taskbar (tray overflow only); the style it had is kept to put back.
        windows.SetExStyle(window.Handle, (window.ExStyle | StoreWindowRules.WsExToolWindow) & ~StoreWindowRules.WsExAppWindow);
        windows.Hide(window.Handle);
    }

    /// <summary>A prompt is never hidden. One an older pass hid comes back with its own style; once per window it is brought forward.</summary>
    private void ShowPrompt(StoreWindow window, StoreKind store)
    {
        if (_hidden.Remove(window.Handle, out var was))
        {
            windows.SetExStyle(window.Handle, was.OriginalExStyle);
            windows.Show(window.Handle);
            _brought.Add(window.Handle);
            windows.BringToFront(window.Handle);
            return;
        }
        if (window.Visible && isQuiet(store) && _brought.Add(window.Handle))
            windows.BringToFront(window.Handle);
    }

    /// <summary>Windows that no longer exist are dropped from what the engine remembers.</summary>
    private void Forget(IReadOnlyList<StoreWindow> seen)
    {
        lock (_gate)
        {
            if (_hidden.Count == 0 && _brought.Count == 0) return;
            var present = seen.Select(window => window.Handle).ToHashSet();
            foreach (var handle in _hidden.Keys.Where(h => !present.Contains(h) && windows.Read(h) is null).ToArray()) _hidden.Remove(handle);
            _brought.RemoveWhere(h => !present.Contains(h) && windows.Read(h) is null);
        }
    }

    /// <summary>
    /// Put a client's windows back (Settings, Open): its main window and what Exo hid come back with their own style;
    /// notifications stay hidden, and a small window the client never showed (Riot's tray window) is not opened.
    /// <paramref name="trusted"/>: the caller already knows these processes are the client (it matched them to the
    /// install it opened), so their program path is not asked again.
    /// </summary>
    internal void Reveal(IReadOnlySet<int> processIds, bool trusted = false)
    {
        foreach (var window in windows.Enumerate(processIds))
        {
            if (!trusted && StoreOf(window.ProcessId) is null) continue;
            var kind = StoreWindowRules.Classify(window);
            if (kind is StoreWindowKind.Toast or StoreWindowKind.Other) continue;
            if (string.IsNullOrWhiteSpace(window.Title)) continue; // untitled surfaces are the client's helpers: blank taskbar entries
            lock (_gate)
            {
                var tracked = _hidden.Remove(window.Handle, out var was);
                if (kind == StoreWindowKind.Prompt && !tracked && !window.Visible) continue;
                var style = tracked ? was!.OriginalExStyle
                    : kind == StoreWindowKind.Shell ? (window.ExStyle & ~StoreWindowRules.WsExToolWindow) | StoreWindowRules.WsExAppWindow
                    : window.ExStyle;
                windows.SetExStyle(window.Handle, style);
                windows.Show(window.Handle);
                windows.BringToFront(window.Handle);
            }
        }
    }

    /// <summary>Every window Exo hid gets its own style back, without changing whether it is visible.</summary>
    internal void RestoreTrackedStyles()
    {
        lock (_gate)
        {
            foreach (var (handle, was) in _hidden.ToArray())
            {
                try
                {
                    if (windows.Read(handle) is not null) windows.SetExStyle(handle, was.OriginalExStyle);
                }
                finally { _hidden.Remove(handle); }
            }
        }
    }

    /// <summary>A prompt of this client is on screen: a launch waiting on the client needs the player.</summary>
    internal bool PromptVisible(StoreKind store, IReadOnlySet<int> processIds) =>
        windows.Enumerate(processIds).Any(window => window.Visible && StoreOf(window.ProcessId) == store &&
                                                     StoreWindowRules.Classify(window) == StoreWindowKind.Prompt);

    /// <summary>The stores (of those asked about) whose main window is on screen now.</summary>
    internal HashSet<StoreKind> VisibleShells(IReadOnlySet<StoreKind> wanted, IReadOnlySet<int> processIds)
    {
        var visible = new HashSet<StoreKind>();
        foreach (var window in windows.Enumerate(processIds))
        {
            if (!window.Visible || string.IsNullOrWhiteSpace(window.Title)) continue;
            if (StoreOf(window.ProcessId) is { } store && wanted.Contains(store) &&
                StoreWindowRules.Classify(window) == StoreWindowKind.Shell)
                visible.Add(store);
        }
        return visible;
    }
}

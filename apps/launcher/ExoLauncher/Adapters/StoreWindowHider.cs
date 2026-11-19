using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// Keeps store chrome hidden while Exo owns the user experience.
/// Vendor clients remain dependencies; Settings → Open store reveals chrome on request.
/// What is done to a window is decided in <see cref="StoreWindowEngine"/> by the client's program path and the kind of
/// window (<see cref="StoreWindowRules"/>); this class owns the Win32 hook, the sweep and its lifetime.
/// </summary>
internal sealed class StoreWindowHider : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private const int ObjidWindow = 0;
    private const uint WmQuit = 0x0012;

    public static readonly string[] SteamProcessNames =
    [
        "steam", "steamwebhelper", "steamerrorreporter", "gameoverlayui", "gameoverlayui64",
    ];

    /// <summary>
    /// Processes that may own the main Steam shell (modern Steam UI is often
    /// steamwebhelper).
    /// </summary>
    public static readonly string[] SteamMainProcessNames = ["steam", "steamwebhelper"];

    public static readonly string[] EpicProcessNames =
    [
        "EpicGamesLauncher", "EpicWebHelper",
    ];

    public static readonly string[] GalaxyProcessNames =
    [
        "GalaxyClient", "GOG Galaxy Notifications",
    ];

    /// <summary>Riot Client chrome only — never League / VALORANT game processes.</summary>
    public static readonly string[] RiotUiProcessNames =
    [
        "Riot Client", "RiotClientUx", "RiotClientUxRender",
    ];

    // These are exact, user-facing official-client processes only. Do not add
    // installers, background services, game executables, overlays, or
    // anti-cheat helpers here: quiet mode may hide client chrome, never a
    // component required to keep a game running safely.
    public static readonly string[] XboxClientProcessNames = ["XboxPcApp", "GamingApp"];
    public static readonly string[] EaClientProcessNames = ["EADesktop"];
    public static readonly string[] UbisoftClientProcessNames = ["UbisoftConnect", "upc", "UplayWebCore"];
    public static readonly string[] BattleNetClientProcessNames = ["Battle.net"];
    public static readonly string[] AmazonClientProcessNames = ["Amazon Games", "AmazonGames", "AmazonGamesUI"];
    // RockstarService and SocialClubHelper are intentionally excluded: they
    // are support components, not launcher chrome, and may be required by a
    // game session.
    public static readonly string[] RockstarClientProcessNames = ["Launcher", "LauncherPatcher"];
    public static readonly string[] ItchClientProcessNames = ["itch"];
    public static readonly string[] MinecraftClientProcessNames = ["MinecraftLauncher"];
    public static readonly string[] RobloxClientProcessNames = ["RobloxPlayerLauncher"];
    public static readonly string[] ParadoxClientProcessNames = ["Paradox Launcher", "ParadoxLauncher"];
    public static readonly string[] WargamingClientProcessNames = ["wgc"];

    /// <summary>The seam the rules run on: the real windows and processes, here and nowhere else.</summary>
    private static readonly IStoreWindows Windows = Win32StoreWindows.Instance;
    private static readonly IStoreProcesses Processes = Win32StoreProcesses.Instance;

    private static readonly StoreWindowEngine Engine = new(
        Windows, Processes,
        HiddenStoreRuntime.StoreForProcess,
        StoreClientIdentity.BelongsTo,
        HiddenStoreRuntime.IsStoreQuiet,
        HiddenStoreRuntime.IsSuspended,
        AppLog.Info);

    // The original-style map is intentionally shared: short install/auth hiders
    // overlap the session-long game hider. Do not let the first one that stops
    // restore and clear styles while another guard is still suppressing chrome.
    private static readonly WindowSuppressionOwnership s_suppressionOwnership = new();

    private readonly string[] _names;
    private readonly HashSet<string> _nameSet;
    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private bool _disposed;
    private int _hasSuppressionOwnership;
    private long _fastSince;

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>False for the all-store guard: it quiets clients, it does not claim them.</summary>
    private readonly bool _owns;

    private StoreWindowHider(string[] processNames, bool owns = true)
    {
        _names = processNames.Length > 0 ? processNames : SteamProcessNames;
        _nameSet = new HashSet<string>(_names, StringComparer.OrdinalIgnoreCase);
        _owns = owns;
    }

    public static StoreWindowHider ForSteam() => new(SteamProcessNames);
    public static StoreWindowHider ForEpic() => new(EpicProcessNames);
    public static StoreWindowHider ForRiot() => new(RiotUiProcessNames);

    internal static StoreWindowHider? For(StoreKind store) =>
        ProcessNamesOf(store) is { Length: > 0 } names ? new(names) : null;

    /// <summary>The processes of a client that draw its windows (never its services, games or anti-cheat).</summary>
    internal static string[] ProcessNamesOf(StoreKind store) => store switch
    {
        StoreKind.Steam => SteamProcessNames,
        StoreKind.Epic => EpicProcessNames,
        StoreKind.Gog => GalaxyProcessNames,
        StoreKind.Riot => RiotUiProcessNames,
        StoreKind.Xbox => XboxClientProcessNames,
        StoreKind.Ea => EaClientProcessNames,
        StoreKind.Ubisoft => UbisoftClientProcessNames,
        StoreKind.BattleNet => BattleNetClientProcessNames,
        StoreKind.Amazon => AmazonClientProcessNames,
        StoreKind.Rockstar => RockstarClientProcessNames,
        StoreKind.Itch => ItchClientProcessNames,
        StoreKind.Minecraft => MinecraftClientProcessNames,
        StoreKind.Roblox => RobloxClientProcessNames,
        StoreKind.Paradox => ParadoxClientProcessNames,
        StoreKind.Wargaming => WargamingClientProcessNames,
        _ => [],
    };

    internal static StoreWindowHider ForAllStoreChrome() => new(
        // The session guard is for client/message chrome only. In-game overlays
        // belong to the game and are intentionally outside this surface set.
        SteamMainProcessNames.Concat(["steamerrorreporter"])
            .Concat(EpicProcessNames)
            .Concat(GalaxyProcessNames)
            .Concat(RiotUiProcessNames)
            .Concat(XboxClientProcessNames)
            .Concat(EaClientProcessNames)
            .Concat(UbisoftClientProcessNames)
            .Concat(BattleNetClientProcessNames)
            .Concat(AmazonClientProcessNames)
            .Concat(RockstarClientProcessNames)
            .Concat(ItchClientProcessNames)
            .Concat(MinecraftClientProcessNames)
            .Concat(RobloxClientProcessNames)
            .Concat(ParadoxClientProcessNames)
            .Concat(WargamingClientProcessNames)
            .ToArray(),
        owns: false);

    /// <param name="duration">How long to keep polling hide.</param>
    /// <remarks>Stopping always restores original window styles and preserves
    /// the current visibility, including a user reopening it.</remarks>
    public void Start(TimeSpan duration)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            StopCore();
            HiddenStoreRuntime.BeginHandoff(_names, _owns);
            _cts = new CancellationTokenSource(HandoffLimit(duration));
            StartCore(_cts.Token);
        }
    }

    /// <summary>
    /// The hook alone, with no time limit, for as long as Exo runs a client:
    /// a window a client Exo owns shows later (after an update, a scan, a
    /// game's exit) is hidden on sight. HiddenStoreRuntime's sweep is its
    /// safety net, so this guard does not poll.
    /// </summary>
    internal void StartSession()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _cts is not null) return;
            _cts = new CancellationTokenSource();
            s_suppressionOwnership.Acquire();
            Volatile.Write(ref _hasSuppressionOwnership, 1);
            try { StartShowHook(_cts.Token); }
            catch { ReleaseSuppressionOwnership(); throw; }
        }
    }

    private void StartCore(CancellationToken token)
    {
        var names = _names;
        s_suppressionOwnership.Acquire();
        Volatile.Write(ref _hasSuppressionOwnership, 1);
        try
        {
            Volatile.Write(ref _fastSince, Environment.TickCount64);
            SweepNow(names);
            StartShowHook(token);
            _pollTask = Task.Run(() => PollAsync(names, token), CancellationToken.None);
        }
        catch
        {
            // Thread start failures are rare, but must not leave a phantom
            // global owner that prevents the next normal scope from restoring.
            ReleaseSuppressionOwnership();
            throw;
        }
    }

    /// <summary>
    /// The safety net under the hook: it catches windows that already existed. Every 250 ms for the first 10 seconds after
    /// the guard started or a client's process did, then every 2 seconds while a client runs; with no client running it
    /// waits, and a window the hook sees wakes it (<see cref="SweepCadence"/>).
    /// </summary>
    private async Task PollAsync(string[] names, CancellationToken token)
    {
        var known = new HashSet<int>();
        while (!token.IsCancellationRequested)
        {
            IReadOnlyList<(int ProcessId, string Name)> running = [];
            try { running = SweepNow(names); }
            catch { /* best-effort */ }
            var ids = running.Select(process => process.ProcessId).ToHashSet();
            if (ids.Any(known.Add)) Volatile.Write(ref _fastSince, Environment.TickCount64);
            known.IntersectWith(ids);

            var since = (Environment.TickCount64 - Volatile.Read(ref _fastSince)) / 1000.0;
            var wait = SweepCadence.Next(since, running.Count > 0);
            try
            {
                if (wait is { } delay) await Task.Delay(delay, token).ConfigureAwait(false);
                else
                {
                    await _wake.WaitAsync(token).ConfigureAwait(false);
                    Volatile.Write(ref _fastSince, Environment.TickCount64);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void WakePoll()
    {
        if (_wake.CurrentCount != 0) return;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already awake */ }
    }

    /// <summary>One pass over the clients' windows: one process snapshot, one read of their windows.</summary>
    private static IReadOnlyList<(int ProcessId, string Name)> SweepNow(IEnumerable<string> names)
    {
        var running = Processes.Running(names);
        if (running.Count > 0)
            Engine.Sweep(running.Select(process => process.ProcessId).ToHashSet());
        return running;
    }

    /// <summary>
    /// Hide store windows the moment they are created or shown.
    /// Polling alone left a window on screen for up to a full poll interval,
    /// which is the flash users saw when a game was launched.
    /// </summary>
    private void StartShowHook(CancellationToken token)
    {
        if (_hookThread is not null) return;
        var ready = new ManualResetEventSlim(false);
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            WinEventProc callback = OnWindowEvent;
            var hook = SetWinEventHook(
                EventObjectCreate, EventObjectShow, IntPtr.Zero, callback,
                0, 0, WineventOutofcontext | WineventSkipownprocess);
            using var cancelHook = token.Register(() => PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero));
            ready.Set();
            try
            {
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch { /* pump torn down */ }
            finally
            {
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                GC.KeepAlive(callback);
                ReleaseSuppressionOwnership();
            }
        })
        {
            IsBackground = true,
            Name = "exo-store-window-guard",
        };
        _hookThread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void OnWindowEvent(IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hWnd == IntPtr.Zero || idObject != ObjidWindow || idChild != 0) return;
        try
        {
            var pid = Windows.ProcessIdOf(hWnd);
            if (pid == 0 || Engine.NameOf(pid) is not { } name || !_nameSet.Contains(name)) return;
            // Reopening a frame Exo already hid belongs to the user/client.
            if (eventType == EventObjectShow && Engine.IsHidden(hWnd) && Windows.Read(hWnd) is { Visible: true } &&
                HiddenStoreRuntime.StoreForProcess(name) is { } store)
            {
                HiddenStoreRuntime.AllowUserOpen(store);
                RevealProcessWindows(name);
                return;
            }
            WakePoll();
            Engine.Handle(hWnd);
        }
        catch { /* never throw across the native boundary */ }
    }

    private void StopCore()
    {
        var cts = _cts;
        var pollTask = _pollTask;
        var hookThread = _hookThread;
        var hookThreadId = _hookThreadId;
        _cts = null;
        _pollTask = null;
        _hookThread = null;
        _hookThreadId = 0;

        try { cts?.Cancel(); } catch { /* */ }
        try { pollTask?.Wait(1500); } catch { /* */ }
        try { cts?.Dispose(); } catch { /* */ }
        if (hookThread is not null)
        {
            try { PostThreadMessage(hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero); } catch { /* */ }
            try { hookThread.Join(1500); } catch { /* */ }
        }
        ReleaseSuppressionOwnership();
        // Preserve visibility while restoring normal taskbar behavior.
    }

    private void ReleaseSuppressionOwnership()
    {
        if (Interlocked.Exchange(ref _hasSuppressionOwnership, 0) == 0) return;
        // Restoration and a new guard acquisition share one gate. Without that
        // atomic handoff, a new overlapping guard can hide a window between the
        // final release and RestoreTrackedStyles(), briefly revealing chrome.
        // Every final guard restores taskbar styles. A quiet handoff keeps the
        // current visibility, so reopening a client works normally afterwards.
        _ = s_suppressionOwnership.Release(true, Engine.RestoreTrackedStyles);
    }

    /// <summary>One-shot hide for named processes (no poll loop). Styles are still tracked.</summary>
    public static void HideOnce(params string[] processNames) => SweepNow(processNames);

    /// <summary>
    /// Put titled store chrome back on screen (Settings → Open Steam / Epic / GOG).
    /// </summary>
    public static void RestoreStoreWindows(params string[] processNames)
    {
        var names = processNames.Length > 0
            ? processNames
            : SteamProcessNames
                .Concat(EpicProcessNames)
                .Concat(GalaxyProcessNames)
                .Concat(RiotUiProcessNames)
                .Concat(XboxClientProcessNames)
                .Concat(EaClientProcessNames)
                .Concat(UbisoftClientProcessNames)
                .Concat(BattleNetClientProcessNames)
                .Concat(AmazonClientProcessNames)
                .Concat(RockstarClientProcessNames)
                .ToArray();
        RevealProcessWindows(names);
    }

    /// <summary>
    /// Put a vendor client back on screen.
    /// Used only when a step cannot complete without it — a Riot sign-in, a Steam login prompt. Hiding those forever
    /// means the launch silently never happens, which is worse than briefly admitting the dependency exists.
    /// </summary>
    public static void RevealProcessWindows(params string[] processNames)
    {
        if (processNames.Length == 0) return;
        var ids = Processes.Running(processNames).Select(process => process.ProcessId).ToHashSet();
        if (ids.Count > 0) Engine.Reveal(ids);
    }

    /// <summary>The stores whose client has a prompt on screen: the launch waiting on it needs the player.</summary>
    internal static bool PromptVisible(StoreKind store)
    {
        var ids = Processes.Running(ProcessNamesOf(store)).Select(process => process.ProcessId).ToHashSet();
        return ids.Count > 0 && Engine.PromptVisible(store, ids);
    }

    internal static TimeSpan HandoffLimit(TimeSpan requested) =>
        requested < HiddenStoreRuntime.HandoffDuration ? requested : HiddenStoreRuntime.HandoffDuration;

    /// <summary>The stores (of those asked) whose client's main window is on screen.</summary>
    internal static HashSet<StoreKind> VisibleMainStores(IEnumerable<StoreKind> stores)
    {
        var wanted = stores.ToHashSet();
        if (wanted.Count == 0) return [];
        var ids = Processes.Running(wanted.SelectMany(ProcessNamesOf)).Select(process => process.ProcessId).ToHashSet();
        return ids.Count == 0 ? [] : Engine.VisibleShells(wanted, ids);
    }

    internal static bool RevealClientWindows(StoreClientLaunchCommand command, IReadOnlyList<string> processNames)
    {
        var names = processNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!command.IsAppx) names.Add(Path.GetFileNameWithoutExtension(command.FileName));
        // The command's own install folder is the identity here: the caller opened exactly this client.
        var matched = Processes.Running(names)
            .Where(process => MatchesClientInstall(command, Processes.PathOf(process.ProcessId)))
            .Select(process => process.ProcessId).ToHashSet();
        if (matched.Count == 0) return false;
        Engine.Reveal(matched, trusted: true);
        return Windows.Enumerate(matched).Any(window => window.Visible && !string.IsNullOrWhiteSpace(window.Title) &&
            !StoreWindowRules.ExcludedClasses.Contains(window.ClassName) && window.Width >= 200 && window.Height >= 100);
    }

    internal static bool MatchesClientInstall(StoreClientLaunchCommand command, string? processPath)
    {
        if (command.IsAppx) return true; // callers already restricted the registered app's process names
        if (string.IsNullOrWhiteSpace(processPath)) return false;
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(command.FileName));
            return folder is not null && Path.GetFullPath(processPath).StartsWith(
                folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void CollapseOrphanSurfaces(params string[] processNames) =>
        CollapseOrphanSurfaces(processNames, pathMustContain: null);

    /// <summary>
    /// Force store chrome off the main taskbar (SW_HIDE + temporary TOOLWINDOW).
    /// Steam’s tray icon stays in the overflow; Settings → Open Steam restores one window.
    /// When <paramref name="pathMustContain"/> is set, processes whose image
    /// path is unknown or does not contain that fragment are left alone.
    /// </summary>
    public static void CollapseOrphanSurfaces(string[] processNames, string? pathMustContain)
    {
        var names = processNames.Length > 0 ? processNames : SteamProcessNames;
        var ids = Processes.Running(names)
            .Where(process => ProcessHelper.MatchesOptionalPath(process.ProcessId, pathMustContain))
            .Select(process => process.ProcessId).ToHashSet();
        if (ids.Count > 0) Engine.Sweep(ids);
    }

    internal sealed record SurfaceSnapshot(IReadOnlyList<(int ProcessId, string Name)> Processes,
        IReadOnlyList<StoreWindow> Windows);

    internal static SurfaceSnapshot CaptureSurfaces(IEnumerable<string> names)
    {
        var processes = Processes.Running(names);
        var ids = processes.Select(process => process.ProcessId).ToHashSet();
        return new(processes, ids.Count == 0 ? [] : Windows.Enumerate(ids));
    }

    internal static void CollapseOrphanSurfaces(SurfaceSnapshot snapshot, string[] names, string? pathMustContain)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = snapshot.Processes.Where(process => wanted.Contains(process.Name) &&
            ProcessHelper.MatchesOptionalPath(process.ProcessId, pathMustContain)).Select(process => process.ProcessId).ToHashSet();
        foreach (var window in snapshot.Windows)
            if (ids.Contains(window.ProcessId)) Engine.Handle(window);
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            StopCore();
        }
    }
}

/// <summary>
/// Coordinates the shared original-style map across independent hiders. A
/// hider's lifetime can overlap a short store operation with a game session;
/// styles are restored only when the final owner requests restoration.
/// </summary>
internal sealed class WindowSuppressionOwnership
{
    private readonly object _gate = new();
    private int _owners;

    internal void Acquire()
    {
        lock (_gate)
            _owners = checked(_owners + 1);
    }

    /// <summary>
    /// Executes <paramref name="restoreTrackedStyles"/> under the ownership
    /// lock when this is the final owner. That makes the final restore and the
    /// next guard's acquisition an atomic handoff.
    /// </summary>
    internal bool Release(bool restoreOnStop, Action restoreTrackedStyles)
    {
        ArgumentNullException.ThrowIfNull(restoreTrackedStyles);
        lock (_gate)
        {
            if (_owners == 0) return false;
            _owners--;
            if (_owners != 0 || !restoreOnStop) return false;
            restoreTrackedStyles();
            return true;
        }
    }
}

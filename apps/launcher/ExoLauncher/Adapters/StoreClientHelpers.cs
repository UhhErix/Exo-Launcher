using System.Diagnostics;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// The processes that only draw a store client's UI: Steam's steamwebhelper
/// (CEF), the Epic launcher's EpicWebHelper or WebView2, the EA app's Qt
/// WebEngine, Ubisoft's UplayWebCore, GOG Galaxy's helper and Battle.net's
/// renderers. While the client's window is hidden, or open but left behind
/// other windows for <see cref="BehindGrace"/>, they hold hundreds of MB and
/// do nothing, so the trim loop lowers their memory priority and empties their
/// working set; Windows pages back whatever they touch next and normal priority
/// returns as soon as the client is shown or brought to the front. (Steam's
/// Small Mode is drawn by the same steamwebhelper, so it saves nothing.)
///
/// Nothing is closed, suspended, injected or reconfigured. The client's main
/// process (downloads, game launch, DRM, Steam Input, cloud saves,
/// achievements, overlay injection) and its services are never members, and
/// neither is anything a game started. Riot is left alone entirely.
/// </summary>
internal static class StoreClientHelpers
{
    /// <param name="HelperHasType">Helpers share the root's file name and are told apart by Chromium's --type= switch.</param>
    private sealed record Spec(StoreKind Store, string[] Roots, string[] Helpers, bool HelperHasType = false);

    private static readonly Spec[] Specs =
    [
        new(StoreKind.Steam, ["steam.exe"], ["steamwebhelper.exe"]),
        new(StoreKind.Epic, ["EpicGamesLauncher.exe"], ["EpicWebHelper.exe", "msedgewebview2.exe"]),
        new(StoreKind.Ea, ["EADesktop.exe"], ["QtWebEngineProcess.exe"]),
        new(StoreKind.Ubisoft, ["upc.exe", "UbisoftConnect.exe"], ["UplayWebCore.exe"]),
        new(StoreKind.Gog, ["GalaxyClient.exe"], ["GalaxyClient Helper.exe"]),
        new(StoreKind.BattleNet, ["Battle.net.exe"], ["Battle.net.exe"], HelperHasType: true),
    ];

    private const string KeyPrefix = "store-";
    private static readonly TimeSpan GroupLifetime = TimeSpan.FromSeconds(30);
    private static readonly object Gate = new();
    private static IReadOnlyList<ManagedProcessGroup> s_groups = [];
    // Half of MinValue: "now - s_groupsAt" must not overflow into a negative age on the first call.
    internal const long NeverBuilt = long.MinValue / 2;
    private static long s_groupsAt = NeverBuilt;

    /// True while groups built at <paramref name="builtAt"/> (Environment.TickCount64) are still current at <paramref name="now"/>.
    internal static bool IsFresh(long now, long builtAt) => now - builtAt < GroupLifetime.TotalMilliseconds;

    internal static bool IsGroupKey(string key) => key.StartsWith(KeyPrefix, StringComparison.Ordinal);

    /// <summary>One group per running client, rebuilt at most every 30 seconds from one process snapshot.</summary>
    internal static IReadOnlyList<ManagedProcessGroup> Groups()
    {
        lock (Gate)
        {
            if (IsFresh(Environment.TickCount64, s_groupsAt)) return s_groups;
            try { s_groups = Build(); }
            catch { s_groups = []; }
            s_groupsAt = Environment.TickCount64;
            return s_groups;
        }
    }

    /// <summary>
    /// The groups whose client is out of sight: no main window on screen, or
    /// one left behind other windows (a game, a browser) for <see cref="BehindGrace"/>;
    /// not opened by the user or a sign-in in the last moments, not in an Exo handoff.
    /// </summary>
    internal static IReadOnlySet<string> QuietGroupKeys(IEnumerable<ManagedProcessGroup> groups)
    {
        var running = groups.Where(group => IsGroupKey(group.Key) && group.Processes.Count > 0)
            .Select(group => (group.Key, Store: Specs.First(spec => KeyOf(spec.Store) == group.Key).Store))
            .ToArray();
        if (running.Length == 0) return new HashSet<string>();
        var visible = StoreWindowHider.VisibleMainStores(running.Select(entry => entry.Store));
        var front = StoreInFront();
        var now = Environment.TickCount64;
        var quiet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, store) in running)
        {
            if (InUse(s_usedAt, store, visible.Contains(store), front == store, now)) continue;
            if (HiddenStoreRuntime.IsSuspended(store) || HiddenStoreRuntime.IsStoreHandoffActive(store)) continue;
            quiet.Add(key);
        }
        return quiet;
    }

    /// <summary>How long a client's open window may sit behind other windows before its helpers are trimmed.</summary>
    internal static readonly TimeSpan BehindGrace = TimeSpan.FromMinutes(2);

    /// Per client, when its window was last in front or first seen open (Environment.TickCount64). Only the trim loop
    /// reads and writes it.
    private static readonly Dictionary<StoreKind, long> s_usedAt = [];

    /// <summary>
    /// Whether <paramref name="store"/> is in use at <paramref name="now"/>: a window of it in front, or its main
    /// window open and in front (or first seen open) less than <see cref="BehindGrace"/> ago. A closed window forgets
    /// it, so opening the client again starts the wait over.
    /// </summary>
    internal static bool InUse(Dictionary<StoreKind, long> usedAt, StoreKind store, bool open, bool inFront, long now)
    {
        if (inFront)
        {
            usedAt[store] = now;
            return true;
        }
        if (!open)
        {
            usedAt.Remove(store);
            return false;
        }
        if (!usedAt.TryGetValue(store, out var at)) usedAt[store] = at = now;
        return now - at < BehindGrace.TotalMilliseconds;
    }

    /// The client whose window is in front (its main window or any dialog of it), if any.
    private static StoreKind? StoreInFront()
    {
        var pid = ForegroundWatcher.ForegroundProcess();
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return HiddenStoreRuntime.StoreForProcess(process.ProcessName);
        }
        catch { return null; /* exited since */ }
    }

    private static string KeyOf(StoreKind store) => KeyPrefix + store.ToString().ToLowerInvariant();

    /// A helper is a process with a helper name whose parent is the client's
    /// root or another of its helpers, started after that parent (a reused
    /// parent pid does not make a stranger a child).
    private static List<ManagedProcessGroup> Build()
    {
        var started = new Dictionary<int, long>();
        return Build(BackgroundTrim.ProcessTable(),
            (child, parent) => BackgroundTrim.StartedBefore(child, parent, started),
            HasTypeSwitch, ManagedProcessResources.Identify);
    }

    /// <param name="startedBefore">True when the child started before its supposed parent (a reused parent pid).</param>
    internal static List<ManagedProcessGroup> Build(IReadOnlyList<(int Pid, int Parent, string Name)> table,
        Func<int, int, bool> startedBefore, Func<int, bool> hasTypeSwitch, Func<int, ManagedProcessIdentity?> identify)
    {
        var children = table.ToLookup(entry => entry.Parent);
        var groups = new List<ManagedProcessGroup>();
        foreach (var spec in Specs)
        {
            var roots = table.Where(entry => Named(entry.Name, spec.Roots) &&
                                             !(spec.HelperHasType && hasTypeSwitch(entry.Pid)))
                .Select(entry => entry.Pid).ToArray();
            if (roots.Length == 0) continue;
            var members = new List<ManagedProcessIdentity>();
            var seen = new HashSet<int>(roots);
            var queue = new Queue<int>(roots);
            while (queue.TryDequeue(out var parent))
            {
                foreach (var child in children[parent])
                {
                    if (!Named(child.Name, spec.Helpers) || !seen.Add(child.Pid)) continue;
                    if (startedBefore(child.Pid, parent)) continue;
                    if (spec.HelperHasType && !hasTypeSwitch(child.Pid)) continue;
                    if (identify(child.Pid) is not { } identity) continue;
                    members.Add(identity);
                    queue.Enqueue(child.Pid);
                }
            }
            if (members.Count > 0) groups.Add(new ManagedProcessGroup(KeyOf(spec.Store), members));
        }
        return groups;
    }

    private static bool Named(string name, string[] names) =>
        names.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));

    private static bool HasTypeSwitch(int pid) =>
        HostedProcessClass.QueryCommandLine(pid)?.Contains("--type=", StringComparison.OrdinalIgnoreCase) == true;
}

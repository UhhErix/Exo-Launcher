using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal sealed class RuneHostTrim : IDisposable
{
    private static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TrimIdleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TrimGameInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TrimSettleTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BackgroundGrace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HiddenGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LeanGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TrimSummaryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetrimInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StoreRetrimInterval = TimeSpan.FromMinutes(5);

    private readonly ManagedProcessIdentity _host = ManagedProcessResources.Identify(Environment.ProcessId) ?? new ManagedProcessIdentity(Environment.ProcessId, 0);
    private readonly ManagedProcessIdentity? _ui;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _loop;
    private bool _uiWindowSeen;
    private volatile bool _steppedBack;

    internal RuneHostTrim(int uiProcessId) => _ui = ManagedProcessResources.Identify(uiProcessId);

    internal void Start()
    {
        LeanSignal.Changed += Wake;
        DiscordClient.Voice.LiveAudio.Changed += Wake;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public void Dispose()
    {
        LeanSignal.Changed -= Wake;
        DiscordClient.Voice.LiveAudio.Changed -= Wake;
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        if (_steppedBack)
        {
            _steppedBack = false;
            BackgroundTrim.SetHostEfficiencyMode(false);
        }
        _stop.Dispose();
        _wake.Dispose();
    }

    internal static bool IsInFront(uint foregroundProcess, int host, int ui) =>
        foregroundProcess != 0 && (foregroundProcess == (uint)host || foregroundProcess == (uint)ui);

    internal static bool Away(DateTime? awaySince, DateTime now, bool hidden, bool lean) =>
        awaySince is { } since && now - since >= (hidden ? HiddenGrace : lean ? LeanGrace : BackgroundGrace);

    internal static TimeSpan NextInterval(bool settling, bool lean, bool steppedBack, bool inFront) =>
        settling ? TrimInterval
            : lean && steppedBack ? TrimGameInterval
            : !inFront ? TrimInterval : TrimIdleInterval;

    internal static IReadOnlyList<ManagedProcessGroup> ManagedGroups(ManagedProcessIdentity host, ManagedProcessIdentity? ui)
    {
        var groups = new List<ManagedProcessGroup>
        {
            new("exo", [host]),
            new("exo-ui", ui is { } identity ? new[] { identity } : Array.Empty<ManagedProcessIdentity>()),
        };
        if (!HostMode.IsDisposable) groups.AddRange(StoreClientHelpers.Groups());
        return ManagedProcessResources.Normalize(groups);
    }

    private void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (Exception e) when (e is SemaphoreFullException or ObjectDisposedException) { }
    }

    private bool UiHiddenOrMinimized()
    {
        if (_ui is not { } ui) return false;
        try
        {
            using var process = Process.GetProcessById(ui.Pid);
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero) return _uiWindowSeen;
            _uiWindowSeen = true;
            return IsIconic(window);
        }
        catch { return false; }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var policy = new ManagedProcessPolicyState();
        var interval = TrimInterval;
        var lastSummary = DateTime.MinValue;
        var lastRetrim = DateTime.MinValue;
        var lastStoreRetrim = DateTime.MinValue;
        DateTime? awaySince = null;
        var seen = (InFront: true, Hidden: false);
        var changedAt = 0L;
        try
        {
            while (true)
            {
                if (await _wake.WaitAsync(interval, stop).ConfigureAwait(false))
                    await Task.Delay(500, stop).ConfigureAwait(false);
                try
                {
                    var now = DateTime.UtcNow;
                    var inFront = IsInFront(ForegroundWatcher.ForegroundProcess(), _host.Pid, _ui?.Pid ?? 0);
                    var hidden = UiHiddenOrMinimized();
                    var lean = LeanSignal.Active;
                    var liveAudio = DiscordClient.Voice.LiveAudio.Active;
                    if (seen != (inFront, hidden))
                    {
                        seen = (inFront, hidden);
                        changedAt = Environment.TickCount64;
                    }
                    awaySince = inFront ? null : awaySince ?? now;
                    var away = Away(awaySince, now, hidden, lean);
                    if (away != _steppedBack)
                    {
                        _steppedBack = away;
                        BackgroundTrim.SetHostEfficiencyMode(away);
                        AppLog.Info(away
                            ? "Trim: background mode (memory priority low, host efficiency on)."
                            : "Trim: foreground mode.");
                    }
                    var groups = ManagedGroups(_host, _ui);
                    policy.Retain(groups.SelectMany(group => group.Processes).ToHashSet());
                    var targets = ManagedProcessResources.TrimTargets(groups, new HashSet<int>(), new HostedTrimPolicy(ShellHidden: hidden && away, LiveAudio: liveAudio)).ToHashSet();
                    var quietStores = StoreClientHelpers.QuietGroupKeys(groups);
                    targets.UnionWith(groups.Where(group => quietStores.Contains(group.Key)).SelectMany(group => group.Processes));
                    var retrim = now - lastRetrim >= RetrimInterval;
                    if (retrim) lastRetrim = now;
                    var storeRetrim = now - lastStoreRetrim >= StoreRetrimInterval;
                    if (storeRetrim) lastStoreRetrim = now;
                    foreach (var group in groups)
                    foreach (var identity in group.Processes)
                    {
                        var eligible = targets.Contains(identity);
                        if (StoreClientHelpers.IsGroupKey(group.Key))
                        {
                            var storeStep = policy.Next(identity, eligible, eligible, storeRetrim && eligible && policy.GrewSinceTrim(identity));
                            if (storeStep.Apply && BackgroundTrim.ApplyManaged(identity, eligible, storeStep.Trim))
                                policy.Accept(identity, eligible, eligible, storeStep.Trim);
                            continue;
                        }
                        var low = away && !(group.Key is "exo" && liveAudio);
                        var step = policy.Next(identity, low, eligible, retrim && eligible && policy.GrewSinceTrim(identity));
                        if (step.Apply && BackgroundTrim.ApplyManaged(identity, low, step.Trim))
                            policy.Accept(identity, low, eligible, step.Trim);
                    }
                    if (now - lastSummary >= TrimSummaryInterval)
                    {
                        lastSummary = now;
                        var snapshot = ManagedProcessResources.Capture(groups);
                        AppLog.Info($"Trim: managed total {snapshot.WorkingSetBytes / 1048576d:N0} MB working set, " +
                            $"{snapshot.PrivateBytes / 1048576d:N0} MB private across {snapshot.ProcessCount} processes" +
                            (snapshot.Complete ? "." : $" ({snapshot.ReadableCount}/{snapshot.ProcessCount} readable).") +
                            " By group (working set/private MB): " + string.Join(", ", snapshot.Groups.Select(group =>
                                $"{group.Key} {group.WorkingSetBytes / 1048576d:N0}/{group.PrivateBytes / 1048576d:N0} across {group.ProcessCount}")));
                    }
                    var settling = Environment.TickCount64 - changedAt < TrimSettleTime.TotalMilliseconds;
                    interval = NextInterval(settling, lean, _steppedBack, inFront);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    AppLog.Info("Background trim tick failed: " + e.GetType().Name + ".");
                    interval = TrimInterval;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { AppLog.Info("Background trim failed: " + e.GetType().Name + "."); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}

using ExoLauncher.Models;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Autopilot's reads and writes of Windows, none needing administrator rights: a process is opened only with the rights its
/// one change needs (query and set information; suspend and resume for the player's own list), checked against its start time
/// before anything is read or written, and closed at once. Priority through Get/SetPriorityClass, efficiency mode through
/// Get/SetProcessInformation(ProcessPowerThrottling) (what Task Manager's Efficiency mode sets, with below normal priority),
/// memory priority through Get/SetProcessInformation(ProcessMemoryPriority), I/O priority through
/// NtQuery/NtSetInformationProcess(ProcessIoPriority) (as <see cref="BackgroundTrim"/> already does for Exo itself), and the
/// active plan through PowerReadACValueIndex / PowerWriteACValueIndex / PowerSetActiveScheme (<see cref="PcHealth.NativePower"/>).
/// </summary>
internal sealed class AutopilotNative : IAutopilotSystem
{
    private readonly PcHealth.IPowerApi _power = new PcHealth.NativePower();

    public IReadOnlyList<ProcessRow> Processes() => ProcessSnapshot.Read();

    public int Front() => (int)ForegroundWatcher.ForegroundProcess();

    public int? Read(Knob knob, int pid, long created)
    {
        var handle = Open(pid, created, QueryLimited | (knob == Knob.Io ? QueryInformation : 0));
        if (handle == 0) return null;
        try
        {
            switch (knob)
            {
                case Knob.Priority:
                    var priority = GetPriorityClass(handle);
                    return priority == 0 ? null : (int)priority;
                case Knob.Eco:
                    var state = new PowerThrottling { Version = 1 };
                    if (!GetProcessInformation(handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottling>())) return null;
                    return ((state.ControlMask & ExecutionSpeed) != 0 ? 1 : 0) | ((state.StateMask & ExecutionSpeed) != 0 ? 2 : 0);
                case Knob.Memory:
                    var memory = new MemoryPriority();
                    return GetProcessInformation(handle, ProcessMemoryPriority, ref memory, Marshal.SizeOf<MemoryPriority>()) ? (int)memory.Value : null;
                case Knob.Io:
                    return NtQueryInformationProcess(handle, ProcessIoPriority, out var io, sizeof(int), out _) >= 0 ? io : null;
                default:
                    return null;
            }
        }
        finally { CloseHandle(handle); }
    }

    public bool Write(Knob knob, int pid, long created, int value)
    {
        // Realtime can starve the mouse, the sound and Windows itself: never written, whatever asked.
        if (knob == Knob.Priority && value is AutopilotRules.Realtime or not (AutopilotRules.Idle or AutopilotRules.BelowNormal or AutopilotRules.Normal or AutopilotRules.AboveNormal or AutopilotRules.High)) return false;
        var handle = Open(pid, created, QueryLimited | (knob == Knob.Suspended ? SuspendResume : SetInformation));
        if (handle == 0) return false;
        try
        {
            switch (knob)
            {
                case Knob.Priority:
                    return SetPriorityClass(handle, (uint)value);
                case Knob.Eco:
                    var state = new PowerThrottling
                    {
                        Version = 1,
                        ControlMask = (value & 1) != 0 ? ExecutionSpeed : 0,
                        StateMask = (value & 2) != 0 ? ExecutionSpeed : 0,
                    };
                    return SetProcessInformation(handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottling>());
                case Knob.Memory:
                    var memory = new MemoryPriority { Value = (uint)value };
                    return SetProcessInformation(handle, ProcessMemoryPriority, ref memory, Marshal.SizeOf<MemoryPriority>());
                case Knob.Io:
                    return NtSetInformationProcess(handle, ProcessIoPriority, ref value, sizeof(int)) >= 0;
                case Knob.Suspended:
                    return (value == 1 ? NtSuspendProcess(handle) : NtResumeProcess(handle)) >= 0;
                default:
                    return false;
            }
        }
        finally { CloseHandle(handle); }
    }

    public uint? PowerRead(Guid scheme, Guid subgroup, Guid setting) => _power.AcValue(scheme, subgroup, setting);

    public bool PowerWrite(Guid scheme, Guid subgroup, Guid setting, uint value) =>
        _power.Write(scheme, subgroup, setting, value, dc: false) && _power.SetActive(scheme) && _power.AcValue(scheme, subgroup, setting) == value;

    public Guid? ActiveScheme() => _power.Active();

    public bool OnBattery() => _power.Battery();

    /// <summary>The process, opened only if it is still the one that started at <paramref name="created"/> (a FILETIME); 0 otherwise.</summary>
    private static nint Open(int pid, long created, uint access)
    {
        var handle = OpenProcess(access, false, pid);
        if (handle == 0) return 0;
        if (GetProcessTimes(handle, out var started, out _, out _, out _) && started == created) return handle;
        CloseHandle(handle);
        return 0;
    }

    private const uint QueryLimited = 0x1000, QueryInformation = 0x0400, SetInformation = 0x0200, SuspendResume = 0x0800;
    private const int ProcessMemoryPriority = 0, ProcessPowerThrottling = 4, ProcessIoPriority = 33;
    private const uint ExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryPriority { public uint Value; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern uint GetPriorityClass(nint process);
    [DllImport("kernel32.dll")] private static extern bool SetPriorityClass(nint process, uint priority);
    [DllImport("kernel32.dll")] private static extern bool GetProcessInformation(nint process, int infoClass, ref PowerThrottling info, int size);
    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(nint process, int infoClass, ref PowerThrottling info, int size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessInformation(nint process, int infoClass, ref MemoryPriority info, int size);
    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(nint process, int infoClass, ref MemoryPriority info, int size);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint process, int infoClass, out int info, int size, out int returned);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationProcess(nint process, int infoClass, ref int info, int size);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(nint process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(nint process);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);
    [DllImport("ntdll.dll")] private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);
    [DllImport("ntdll.dll")] private static extern int NtSetTimerResolution(uint desired, [MarshalAs(UnmanagedType.U1)] bool setResolution, out uint current);

    private readonly object _timerLock = new();
    private bool _highResTimer;
    private uint _ntTimerResolution;

    public void BeginHighResTimer()
    {
        lock (_timerLock)
        {
            if (_highResTimer) return;
            if (NtQueryTimerResolution(out _, out var maxRes, out _) == 0)
            {
                var target = maxRes > 0 ? Math.Max(maxRes, 5000u) : 5000u;
                if (NtSetTimerResolution(target, true, out _) == 0)
                {
                    _ntTimerResolution = target;
                    _highResTimer = true;
                    return;
                }
            }
            if (timeBeginPeriod(1) == 0) _highResTimer = true;
        }
    }

    public void EndHighResTimer()
    {
        lock (_timerLock)
        {
            if (!_highResTimer) return;
            if (_ntTimerResolution != 0)
            {
                _ = NtSetTimerResolution(_ntTimerResolution, false, out _);
                _ntTimerResolution = 0;
            }
            else
            {
                _ = timeEndPeriod(1);
            }
            _highResTimer = false;
        }
    }
}

/// <summary>
/// Autopilot as Exo runs it: the engine on Windows, the journal in Exo's data folder, the switch in settings. Started once Exo's
/// own Game Mode keeper has looked (<see cref="AppServices"/>), followed from the run-state poll, ended when Exo closes.
/// </summary>
internal static class Autopilot
{
    private static readonly AutopilotJournal Journal = new(AutopilotJournal.DefaultPath);
    private static readonly Lazy<PowerProfiles.CpuClass> Cpu = new(() =>
    {
        try { return PcHealth.ThisPcProfile().Class; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return PowerProfiles.CpuClass.Unknown; }
    });

    internal static AutopilotSettings LiveSettings(AutopilotSettings? stored) => (stored ?? new()).Copy();

    internal static AutopilotSettings ApplyPatch(
        AutopilotSettings? stored,
        bool? on = null,
        string? gamePriority = null,
        bool? keepGameMode = null,
        bool? coreParking = null,
        List<string>? alwaysDemote = null,
        List<string>? suspend = null)
    {
        var next = LiveSettings(stored);
        if (on is { } o) next.On = o;
        if (gamePriority is { } gp) next.GamePriority = AutopilotSettings.NormalizePriority(gp);
        if (keepGameMode is { } kgm) next.KeepGameMode = kgm;
        if (coreParking is { } cp) next.CoreParking = cp;
        if (alwaysDemote is not null) next.AlwaysDemote = alwaysDemote;
        if (suspend is not null) next.Suspend = [.. suspend.Where(name => AutopilotSafety.WhyLeftAlone(name) is null)];
        return next.Copy();
    }

    internal static AutopilotSettings Settings => LiveSettings(App.Services.Settings.Current.Autopilot);

    internal static AutopilotEngine Engine { get; } = new(new AutopilotNative(), Journal, () => Settings, () => Cpu.Value,
        look => new Timer(_ => look(), null, AutopilotEngine.LookEveryMs, AutopilotEngine.LookEveryMs));

    internal static AutopilotGameMode GameMode { get; } = new(UserRegistry.Shared, Journal, () => WindowsGameMode.Shared.State, WindowsGameMode.Off, WindowsGameMode.On);

    internal static AutopilotMouseAccel MouseAccel { get; } = new(new NativeMouseAccelSystem(), Journal);

    internal static PowerProfiles.CpuClass CpuClass => Cpu.Value;

    private static readonly Dictionary<string, IReadOnlyList<string>> LearnedCache = new(StringComparer.Ordinal);

    /// <summary>At Exo's start, after Exo's Game Mode keeper: what a crash left goes back, and Game Mode and mouse precision follow Autopilot.</summary>
    internal static void Start()
    {
        AutopilotWatch.Shared.Did = Engine.Did;
        try { Engine.Recover(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("Autopilot: recovery failed: " + ex.GetType().Name); }
        HandGameMode();
        HandMouseAccel();
    }

    /// <summary>1:1 mouse movement without pointer acceleration; null when done, else the sentence.</summary>
    internal static string? HandMouseAccel()
    {
        try { return Settings.On ? MouseAccel.Apply() : MouseAccel.PutBack(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn("Autopilot: Mouse acceleration: " + ex.GetType().Name);
            return Settings.On ? "Windows didn't turn mouse acceleration off." : "Windows didn't put mouse acceleration back.";
        }
    }

    /// <summary>Game Mode as the switch and the processor want it now; null when done, else the sentence.</summary>
    internal static string? HandGameMode()
    {
        try { return GameMode.Hand(AutopilotGameMode.KeptOnBecause(Settings, Cpu.Value) is null); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn("Autopilot: Game Mode: " + ex.GetType().Name);
            return "Windows didn't change Game Mode.";
        }
    }

    /// <summary>From the run-state poll (off the window's thread): the games running now; the one in front is followed.</summary>
    internal static void Follow(LaunchOrchestrator launcher, IEnumerable<Models.GameEntry> running)
    {
        try
        {
            if (!Settings.On)
            {
                Engine.Follow(null);
                return;
            }
            var games = running.ToArray();
            if (games.Length == 0)
            {
                Engine.Follow(null);
                LearnedCache.Clear();
                return;
            }
            var front = (int)ForegroundWatcher.ForegroundProcess();
            var trees = games.Select(game => (game, tree: launcher.GameProcesses(game))).ToArray();
            var (followed, tree) = Array.Find(trees, entry => entry.tree.Contains(front));
            if (followed is null) (followed, tree) = trees[0];
            var mayTouch = !AntiCheatCheck.IsRiot(followed) && !AntiCheatCheck.Cached(followed).AssumeProtected;
            if (!LearnedCache.TryGetValue(followed.Id, out var learned))
                LearnedCache[followed.Id] = learned = AutopilotReports.LearnedFor(AutopilotReports.Load(AutopilotReports.DefaultPath), followed.Id);
            Engine.Follow(new AutopilotGame(followed.Id, followed.Title, tree, mayTouch, learned));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("Autopilot: follow failed: " + ex.GetType().Name); }
    }

    /// <summary>Exo is closing: the session's changes go back now (Game Mode stays as the switch has it).</summary>
    internal static void Shutdown()
    {
        try { Engine.End(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("Autopilot: ending failed: " + ex.GetType().Name); }
    }

    /// <summary>The uninstaller: every change goes back, Game Mode and mouse acceleration included, before Exo's own keeper gives its value back.</summary>
    internal static void GiveBack()
    {
        Shutdown();
        try { Engine.Recover(); } catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("Autopilot: " + ex.GetType().Name); }
        if (GameMode.Held is not null && GameMode.Hand(false) is { } said) AppLog.Warn("Uninstall: Autopilot's Game Mode not put back: " + said);
        if (MouseAccel.Held is not null && MouseAccel.PutBack() is { } maErr) AppLog.Warn("Uninstall: Autopilot's mouse acceleration not put back: " + maErr);
    }
}

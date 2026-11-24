using System.Runtime.InteropServices;
using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

internal sealed record ServiceSnapshot(string Name, int StartType, string State, bool CanPause, bool CanStop);

internal sealed record PausedServiceEntry(string Name, int OriginalStartType, string OriginalState, string Action);

internal interface IGameSessionDownloadPauseSystem
{
    IReadOnlyList<Guid> ListActiveBitsJobs();
    bool SuspendBitsJob(Guid jobId);
    bool ResumeBitsJob(Guid jobId);
    ServiceSnapshot? QueryService(string serviceName);
    bool PauseService(string serviceName);
    bool StopService(string serviceName);
    bool ContinueService(string serviceName);
    bool StartService(string serviceName);
}

internal sealed class GameSessionDownloadPause(
    IGameSessionDownloadPauseSystem system,
    string journalPath,
    Action<string>? logLine = null,
    Func<DateTimeOffset>? now = null,
    TimeSpan? maxPauseDuration = null)
{
    internal const string FileName = "exo-download-pause.json";
    internal static string DefaultPath => Path.Combine(PathHelper.AppDataDir, FileName);
    internal static readonly string[] SessionServices = ["BITS", "DoSvc", "wuauserv", "WSearch"];
    internal static readonly TimeSpan DefaultMaxPauseDuration = TimeSpan.FromHours(6);
    internal const int StartTypeDisabled = 4;

    internal static GameSessionDownloadPause Shared { get; } = new(new NativeSystem(), DefaultPath);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Action<string> _log = logLine ?? AppLog.Info;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan _maxPauseDuration = maxPauseDuration ?? DefaultMaxPauseDuration;
    private readonly Lock _gate = new();
    private bool _active;
    private DateTimeOffset _startedUtc;
    private Timer? _timeoutTimer;
    private nint _powerRegHandle;
    private DeviceNotifyCallback? _powerCallback;

    internal sealed record PauseRecord(DateTimeOffset StartedUtc, List<Guid> BitsJobs, List<PausedServiceEntry> Services);

    internal sealed record Snapshot(bool Active, int PausedBitsJobs, IReadOnlyList<string> PausedServices);

    internal Snapshot Current()
    {
        lock (_gate)
        {
            var rec = Load();
            return new(_active, rec.BitsJobs.Count, [.. rec.Services.Select(s => $"{s.Name}:{s.Action}")]);
        }
    }

    internal static void Hold(bool gaming) => Shared.SetGaming(gaming);

    internal void AttachPowerEvents()
    {
        lock (_gate)
        {
            if (_powerRegHandle != 0) return;
            try
            {
                _powerCallback = HandlePowerBroadcast;
                var recipient = new DeviceNotifySubscribeParameters
                {
                    Callback = Marshal.GetFunctionPointerForDelegate(_powerCallback),
                    Context = IntPtr.Zero,
                };
                if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallbackFlag, ref recipient, out var handle) == 0)
                    _powerRegHandle = handle;
                else
                    _powerCallback = null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _powerCallback = null;
            }
        }
    }

    internal void DetachPowerEvents()
    {
        lock (_gate)
        {
            if (_powerRegHandle == 0) return;
            try { _ = PowerUnregisterSuspendResumeNotification(_powerRegHandle); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            _powerRegHandle = 0;
            _powerCallback = null;
        }
    }

    private uint HandlePowerBroadcast(IntPtr context, uint type, IntPtr setting)
    {
        if (type is PbtApmSuspend or PbtApmResumeAutomatic or PbtApmResumeSuspend)
            OnPowerTransition(type == PbtApmSuspend ? "system sleep" : "system resume");
        return 0;
    }

    private const uint DeviceNotifyCallbackFlag = 2;
    private const uint PbtApmSuspend = 0x0004;
    private const uint PbtApmResumeSuspend = 0x0007;
    private const uint PbtApmResumeAutomatic = 0x0012;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref DeviceNotifySubscribeParameters recipient, out nint registrationHandle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(nint registrationHandle);

    internal int OnPowerTransition(string reason)
    {
        lock (_gate)
        {
            if (!_active && !File.Exists(journalPath)) return 0;
            _active = false;
            DisarmTimerLocked();
            return RestoreLocked(reason);
        }
    }

    internal int CheckTimeout()
    {
        lock (_gate)
        {
            if (!_active) return 0;
            if (_now() - _startedUtc < _maxPauseDuration) return 0;
            _active = false;
            DisarmTimerLocked();
            return RestoreLocked("6-hour safety timeout");
        }
    }

    internal void SetGaming(bool gaming)
    {
        lock (_gate)
        {
            if (gaming)
            {
                if (_active)
                {
                    if (_now() - _startedUtc >= _maxPauseDuration)
                    {
                        _active = false;
                        DisarmTimerLocked();
                        RestoreLocked("6-hour safety timeout");
                    }
                    return;
                }

                var started = _now();
                var candidateJobs = system.ListActiveBitsJobs().Distinct().ToList();
                var candidateServices = new List<PausedServiceEntry>();
                foreach (var name in SessionServices)
                {
                    var snap = system.QueryService(name);
                    if (snap is null || snap.StartType == StartTypeDisabled) continue;
                    if (!string.Equals(snap.State, "running", StringComparison.OrdinalIgnoreCase)) continue;
                    var action = snap.CanPause ? "paused" : snap.CanStop ? "stopped" : null;
                    if (action is null) continue;
                    candidateServices.Add(new(snap.Name, snap.StartType, snap.State, action));
                }

                if (candidateJobs.Count == 0 && candidateServices.Count == 0)
                {
                    _active = true;
                    _startedUtc = started;
                    ArmTimerLocked();
                    return;
                }

                var existing = Load();
                var plannedJobs = existing.BitsJobs.Concat(candidateJobs).Distinct().ToList();
                var plannedServices = MergeServices(existing.Services, candidateServices);
                if (!Save(new PauseRecord(started, plannedJobs, plannedServices)))
                    return;

                var succeededJobs = new List<Guid>(existing.BitsJobs);
                foreach (var jobId in candidateJobs)
                {
                    if (system.SuspendBitsJob(jobId) && !succeededJobs.Contains(jobId))
                        succeededJobs.Add(jobId);
                }

                var succeededServices = new List<PausedServiceEntry>(existing.Services);
                foreach (var entry in candidateServices)
                {
                    var ok = entry.Action == "paused"
                        ? system.PauseService(entry.Name)
                        : system.StopService(entry.Name);
                    if (ok && succeededServices.All(s => !s.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)))
                        succeededServices.Add(entry);
                }

                if (succeededJobs.Count == 0 && succeededServices.Count == 0)
                    DeleteFile();
                else
                    Save(new PauseRecord(started, succeededJobs, succeededServices));

                _active = true;
                _startedUtc = started;
                ArmTimerLocked();
                if (succeededJobs.Count > 0 || succeededServices.Count > 0)
                    _log($"Download pause: held {succeededJobs.Count} BITS job(s) and {succeededServices.Count} service(s) for the game session.");
            }
            else
            {
                if (!_active && !File.Exists(journalPath)) return;
                _active = false;
                DisarmTimerLocked();
                RestoreLocked("game session ended");
            }
        }
    }

    internal int Recover()
    {
        lock (_gate)
        {
            _active = false;
            DisarmTimerLocked();
            return RestoreLocked("startup recovery");
        }
    }

    private static List<PausedServiceEntry> MergeServices(List<PausedServiceEntry> existing, List<PausedServiceEntry> incoming)
    {
        var merged = new List<PausedServiceEntry>(existing);
        foreach (var item in incoming)
            if (merged.All(e => !e.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
                merged.Add(item);
        return merged;
    }

    private void ArmTimerLocked()
    {
        DisarmTimerLocked();
        if (_maxPauseDuration <= TimeSpan.Zero || _maxPauseDuration > TimeSpan.FromDays(1)) return;
        _timeoutTimer = new Timer(_ => CheckTimeout(), null, _maxPauseDuration, Timeout.InfiniteTimeSpan);
    }

    private void DisarmTimerLocked()
    {
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
    }

    private int RestoreLocked(string reason)
    {
        var rec = Load();
        if (rec.BitsJobs.Count == 0 && rec.Services.Count == 0)
        {
            DeleteFile();
            return 0;
        }
        var resumedJobs = 0;
        foreach (var jobId in rec.BitsJobs)
            if (system.ResumeBitsJob(jobId)) resumedJobs++;

        var resumedServices = 0;
        foreach (var entry in rec.Services)
        {
            if (entry.OriginalStartType == StartTypeDisabled) continue;
            var current = system.QueryService(entry.Name);
            if (current is { StartType: StartTypeDisabled }) continue;
            var ok = entry.Action == "paused"
                ? system.ContinueService(entry.Name)
                : system.StartService(entry.Name);
            if (ok) resumedServices++;
        }

        DeleteFile();
        var total = resumedJobs + resumedServices;
        if (total > 0)
            _log($"Download pause: restored {resumedJobs} BITS job(s) and {resumedServices} service(s) ({reason}).");
        return total;
    }

    private PauseRecord Load()
    {
        try
        {
            if (!File.Exists(journalPath)) return new(default, [], []);
            var text = File.ReadAllText(journalPath);
            if (string.IsNullOrWhiteSpace(text)) return new(default, [], []);
            var rec = JsonSerializer.Deserialize<PauseRecord>(text, Json);
            return new(rec?.StartedUtc ?? default, rec?.BitsJobs ?? [], rec?.Services ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(default, [], []);
        }
    }

    private bool Save(PauseRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            var tmp = journalPath + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, Json);
                stream.Flush(true);
            }
            if (File.Exists(journalPath)) File.Replace(tmp, journalPath, null);
            else File.Move(tmp, journalPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Download pause: journal write failed: " + ex.GetType().Name);
            return false;
        }
    }

    private void DeleteFile()
    {
        try
        {
            if (File.Exists(journalPath)) DeletionGate.DeleteFile(journalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal sealed class NativeSystem : IGameSessionDownloadPauseSystem
    {
        private static readonly Guid ClsidBackgroundCopyManager = new("4991D34B-80A1-4291-83B6-3328366B9097");

        internal static bool QueryServiceRunning(string serviceName)
        {
            var scm = OpenSCManagerW(null, null, ScManagerConnect);
            if (scm == 0) return false;
            try
            {
                var svc = OpenServiceW(scm, serviceName, ServiceQueryStatus);
                if (svc == 0) return false;
                try
                {
                    return QueryServiceStatus(svc, out var status) && status.CurrentState == ServiceRunning;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        public IReadOnlyList<Guid> ListActiveBitsJobs()
        {
            if (HostMode.IsDisposable) return [];
            var found = new List<Guid>();
            IBackgroundCopyManager? manager = null;
            IEnumBackgroundCopyJobs? enumerator = null;
            try
            {
                var type = Type.GetTypeFromCLSID(ClsidBackgroundCopyManager);
                if (type is null) return found;
                manager = Activator.CreateInstance(type) as IBackgroundCopyManager;
                if (manager is null) return found;
                if (manager.EnumJobs(0, out enumerator) != 0 || enumerator is null) return found;
                while (enumerator.Next(1, out var job, out var fetched) == 0 && fetched == 1 && job is not null)
                {
                    try
                    {
                        if (job.GetState(out var state) == 0
                            && state is BgJobState.Queued or BgJobState.Connecting or BgJobState.Transferring
                            && job.GetId(out var id) == 0)
                            found.Add(id);
                    }
                    finally
                    {
                        if (Marshal.IsComObject(job)) Marshal.FinalReleaseComObject(job);
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or ExternalException) { }
            finally
            {
                if (enumerator is not null && Marshal.IsComObject(enumerator)) Marshal.FinalReleaseComObject(enumerator);
                if (manager is not null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager);
            }
            return found;
        }

        public bool SuspendBitsJob(Guid jobId) => ControlBitsJob(jobId, suspend: true);

        public bool ResumeBitsJob(Guid jobId) => ControlBitsJob(jobId, suspend: false);

        private static bool ControlBitsJob(Guid jobId, bool suspend)
        {
            if (HostMode.IsDisposable) return false;
            IBackgroundCopyManager? manager = null;
            try
            {
                var type = Type.GetTypeFromCLSID(ClsidBackgroundCopyManager);
                if (type is null) return false;
                manager = Activator.CreateInstance(type) as IBackgroundCopyManager;
                if (manager is null) return false;
                var guid = jobId;
                if (manager.GetJob(ref guid, out var job) != 0 || job is null) return false;
                try
                {
                    if (job.GetState(out var state) != 0) return false;
                    if (suspend)
                        return state is BgJobState.Queued or BgJobState.Connecting or BgJobState.Transferring && job.Suspend() == 0;
                    return state == BgJobState.Suspended && job.Resume() == 0;
                }
                finally
                {
                    if (Marshal.IsComObject(job)) Marshal.FinalReleaseComObject(job);
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or ExternalException) { return false; }
            finally
            {
                if (manager is not null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager);
            }
        }

        public ServiceSnapshot? QueryService(string serviceName)
        {
            var startType = ReadServiceStartType(serviceName);
            if (startType is null) return null;
            var scm = OpenSCManagerW(null, null, ScManagerConnect);
            if (scm == 0) return null;
            try
            {
                var svc = OpenServiceW(scm, serviceName, ServiceQueryStatus);
                if (svc == 0) return null;
                try
                {
                    if (!QueryServiceStatus(svc, out var status)) return null;
                    var state = status.CurrentState switch
                    {
                        ServiceRunning => "running",
                        ServicePaused => "paused",
                        ServiceStopped => "stopped",
                        _ => "other",
                    };
                    return new(serviceName, startType.Value, state,
                        (status.ControlsAccepted & ServiceAcceptPauseContinue) != 0,
                        (status.ControlsAccepted & ServiceAcceptStop) != 0);
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        private static int? ReadServiceStartType(string serviceName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                return key?.GetValue("Start") is int s ? s : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }

        public bool PauseService(string serviceName) => SendServiceControl(serviceName, ServicePauseContinue, ServiceControlPause);

        public bool StopService(string serviceName) => SendServiceControl(serviceName, ServiceStop, ServiceControlStop);

        public bool ContinueService(string serviceName) => SendServiceControl(serviceName, ServicePauseContinue, ServiceControlContinue);

        public bool StartService(string serviceName)
        {
            if (HostMode.IsDisposable) return false;
            var scm = OpenSCManagerW(null, null, ScManagerConnect);
            if (scm == 0) return false;
            try
            {
                var svc = OpenServiceW(scm, serviceName, ServiceQueryStatus | ServiceStart);
                if (svc == 0) return false;
                try
                {
                    if (!QueryServiceStatus(svc, out var status) || status.CurrentState != ServiceStopped) return false;
                    return StartServiceW(svc, 0, IntPtr.Zero);
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        private static bool SendServiceControl(string serviceName, uint access, uint control)
        {
            if (HostMode.IsDisposable) return false;
            var scm = OpenSCManagerW(null, null, ScManagerConnect);
            if (scm == 0) return false;
            try
            {
                var svc = OpenServiceW(scm, serviceName, ServiceQueryStatus | access);
                if (svc == 0) return false;
                try
                {
                    if (!QueryServiceStatus(svc, out var status)) return false;
                    if (control is ServiceControlPause or ServiceControlStop && status.CurrentState != ServiceRunning) return false;
                    if (control == ServiceControlContinue && status.CurrentState != ServicePaused) return false;
                    return ControlService(svc, control, out _);
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceStart = 0x0010;
        private const uint ServiceStop = 0x0020;
        private const uint ServicePauseContinue = 0x0040;
        private const uint ServiceControlStop = 0x0001;
        private const uint ServiceControlPause = 0x0002;
        private const uint ServiceControlContinue = 0x0003;
        private const uint ServiceStopped = 1;
        private const uint ServiceRunning = 4;
        private const uint ServicePaused = 7;
        private const uint ServiceAcceptStop = 0x0001;
        private const uint ServiceAcceptPauseContinue = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint OpenServiceW(nint scManager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(nint service, uint control, out ServiceStatus status);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool StartServiceW(nint service, uint numServiceArgs, IntPtr serviceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(nint handle);

        private enum BgJobState
        {
            Queued = 0,
            Connecting = 1,
            Transferring = 2,
            Suspended = 3,
            Error = 4,
            TransientError = 5,
            Transferred = 6,
            Acknowledged = 7,
            Cancelled = 8,
        }

        [ComImport, Guid("5CE34C0D-0DC9-4C1F-897C-DAA1B78CEE7C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IBackgroundCopyManager
        {
            [PreserveSig] int CreateJob([MarshalAs(UnmanagedType.LPWStr)] string displayName, int type, out Guid jobId, out IntPtr job);
            [PreserveSig] int GetJob(ref Guid jobId, out IBackgroundCopyJob? job);
            [PreserveSig] int EnumJobs(uint flags, out IEnumBackgroundCopyJobs? enumerator);
        }

        [ComImport, Guid("1AF4F612-3B71-466F-8F58-7B6F73AC57AD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumBackgroundCopyJobs
        {
            [PreserveSig] int Next(uint celt, out IBackgroundCopyJob? rgelt, out uint fetched);
            [PreserveSig] int Skip(uint celt);
            [PreserveSig] int Reset();
            [PreserveSig] int Clone(out IEnumBackgroundCopyJobs? ppenum);
            [PreserveSig] int GetCount(out uint count);
        }

        [ComImport, Guid("37668D37-507E-4160-9316-26306D150B12"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IBackgroundCopyJob
        {
            [PreserveSig] int AddFileSet(uint fileCount, IntPtr fileSet);
            [PreserveSig] int AddFile([MarshalAs(UnmanagedType.LPWStr)] string remoteUrl, [MarshalAs(UnmanagedType.LPWStr)] string localName);
            [PreserveSig] int EnumFiles(out IntPtr enumerator);
            [PreserveSig] int Suspend();
            [PreserveSig] int Resume();
            [PreserveSig] int Cancel();
            [PreserveSig] int Complete();
            [PreserveSig] int GetId(out Guid val);
            [PreserveSig] int GetType(out int val);
            [PreserveSig] int GetProgress(IntPtr val);
            [PreserveSig] int GetTimes(IntPtr val);
            [PreserveSig] int GetState(out BgJobState val);
        }
    }
}

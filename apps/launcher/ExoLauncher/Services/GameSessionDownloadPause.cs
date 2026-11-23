using System.Runtime.InteropServices;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal interface IGameSessionDownloadPauseSystem
{
    IReadOnlyList<Guid> SuspendActiveBitsJobs();
    int ResumeBitsJobs(IEnumerable<Guid> jobIds);
    IReadOnlyList<string> PauseRunningServices(IEnumerable<string> serviceNames);
    int ResumeServices(IEnumerable<string> serviceNames);
}

internal sealed class GameSessionDownloadPause(IGameSessionDownloadPauseSystem system, string journalPath, Action<string>? logLine = null)
{
    internal const string FileName = "exo-download-pause.json";
    internal static string DefaultPath => Path.Combine(PathHelper.AppDataDir, FileName);
    internal static readonly string[] SessionServices = ["BITS", "DoSvc", "wuauserv", "WSearch"];

    internal static GameSessionDownloadPause Shared { get; } = new(new NativeSystem(), DefaultPath);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Action<string> _log = logLine ?? AppLog.Info;
    private readonly Lock _gate = new();
    private bool _active;

    internal sealed record PauseRecord(List<Guid> BitsJobs, List<string> Services);

    internal sealed record Snapshot(bool Active, int PausedBitsJobs, IReadOnlyList<string> PausedServices);

    internal Snapshot Current()
    {
        lock (_gate)
        {
            var rec = Load();
            return new(_active, rec.BitsJobs.Count, [.. rec.Services]);
        }
    }

    internal static void Hold(bool gaming) => Shared.SetGaming(gaming);

    internal void SetGaming(bool gaming)
    {
        lock (_gate)
        {
            if (gaming)
            {
                if (_active) return;
                var jobs = system.SuspendActiveBitsJobs();
                var services = system.PauseRunningServices(SessionServices);
                var existing = Load();
                var mergedJobs = existing.BitsJobs.Concat(jobs).Distinct().ToList();
                var mergedServices = existing.Services.Concat(services).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (mergedJobs.Count > 0 || mergedServices.Count > 0)
                    Save(new PauseRecord(mergedJobs, mergedServices));
                _active = true;
                if (jobs.Count > 0 || services.Count > 0)
                    _log($"Download pause: held {jobs.Count} BITS job(s) and {services.Count} service(s) for the game session.");
            }
            else
            {
                if (!_active && !File.Exists(journalPath)) return;
                _active = false;
                RestoreLocked("game session ended");
            }
        }
    }

    internal int Recover()
    {
        lock (_gate)
        {
            _active = false;
            return RestoreLocked("startup recovery");
        }
    }

    private int RestoreLocked(string reason)
    {
        var rec = Load();
        if (rec.BitsJobs.Count == 0 && rec.Services.Count == 0)
        {
            DeleteFile();
            return 0;
        }
        var resumedJobs = rec.BitsJobs.Count > 0 ? system.ResumeBitsJobs(rec.BitsJobs) : 0;
        var resumedServices = rec.Services.Count > 0 ? system.ResumeServices(rec.Services) : 0;
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
            if (!File.Exists(journalPath)) return new([], []);
            var text = File.ReadAllText(journalPath);
            if (string.IsNullOrWhiteSpace(text)) return new([], []);
            var rec = JsonSerializer.Deserialize<PauseRecord>(text, Json);
            return new(rec?.BitsJobs ?? [], rec?.Services ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new([], []);
        }
    }

    private void Save(PauseRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            var tmp = journalPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json));
            File.Move(tmp, journalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Download pause: journal write failed: " + ex.GetType().Name);
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

        public IReadOnlyList<Guid> SuspendActiveBitsJobs()
        {
            if (HostMode.IsDisposable) return [];
            var suspended = new List<Guid>();
            IBackgroundCopyManager? manager = null;
            IEnumBackgroundCopyJobs? enumerator = null;
            try
            {
                var type = Type.GetTypeFromCLSID(ClsidBackgroundCopyManager);
                if (type is null) return suspended;
                manager = Activator.CreateInstance(type) as IBackgroundCopyManager;
                if (manager is null) return suspended;
                if (manager.EnumJobs(0, out enumerator) != 0 || enumerator is null) return suspended;
                while (enumerator.Next(1, out var job, out var fetched) == 0 && fetched == 1 && job is not null)
                {
                    try
                    {
                        if (job.GetState(out var state) == 0 && state is BgJobState.Queued or BgJobState.Connecting or BgJobState.Transferring)
                        {
                            if (job.GetId(out var id) == 0 && job.Suspend() == 0)
                                suspended.Add(id);
                        }
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
            return suspended;
        }

        public int ResumeBitsJobs(IEnumerable<Guid> jobIds)
        {
            if (HostMode.IsDisposable) return 0;
            var resumed = 0;
            IBackgroundCopyManager? manager = null;
            try
            {
                var type = Type.GetTypeFromCLSID(ClsidBackgroundCopyManager);
                if (type is null) return 0;
                manager = Activator.CreateInstance(type) as IBackgroundCopyManager;
                if (manager is null) return 0;
                foreach (var id in jobIds)
                {
                    var guid = id;
                    if (manager.GetJob(ref guid, out var job) != 0 || job is null) continue;
                    try
                    {
                        if (job.GetState(out var state) == 0 && state == BgJobState.Suspended && job.Resume() == 0)
                            resumed++;
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
                if (manager is not null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager);
            }
            return resumed;
        }

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

        public IReadOnlyList<string> PauseRunningServices(IEnumerable<string> serviceNames)
        {
            if (HostMode.IsDisposable) return [];
            var paused = new List<string>();
            var scm = OpenSCManagerW(null, null, ScManagerConnect);
            if (scm == 0) return paused;
            try
            {
                foreach (var name in serviceNames)
                {
                    var svc = OpenServiceW(scm, name, ServiceQueryStatus | ServicePauseContinue | ServiceStop);
                    if (svc == 0) continue;
                    try
                    {
                        if (!QueryServiceStatus(svc, out var status) || status.CurrentState != ServiceRunning) continue;
                        if ((status.ControlsAccepted & ServiceAcceptPauseContinue) != 0 && ControlService(svc, ServiceControlPause, out _))
                            paused.Add(name + ":paused");
                        else if ((status.ControlsAccepted & ServiceAcceptStop) != 0 && ControlService(svc, ServiceControlStop, out _))
                            paused.Add(name + ":stopped");
                    }
                    finally { CloseServiceHandle(svc); }
                }
            }
            finally { CloseServiceHandle(scm); }
            return paused;
        }

        public int ResumeServices(IEnumerable<string> serviceNames)
        {
            if (HostMode.IsDisposable) return 0;
            var restored = 0;
            var scm = OpenSCManagerW(null, null, ScManagerConnect);
            if (scm == 0) return 0;
            try
            {
                foreach (var entry in serviceNames)
                {
                    var colon = entry.IndexOf(':');
                    var name = colon > 0 ? entry[..colon] : entry;
                    var mode = colon > 0 ? entry[(colon + 1)..] : "stopped";
                    var svc = OpenServiceW(scm, name, ServiceQueryStatus | ServicePauseContinue | ServiceStart);
                    if (svc == 0) continue;
                    try
                    {
                        if (!QueryServiceStatus(svc, out var status)) continue;
                        if (mode == "paused" && status.CurrentState == ServicePaused && ControlService(svc, ServiceControlContinue, out _))
                            restored++;
                        else if (status.CurrentState == ServiceStopped && StartServiceW(svc, 0, IntPtr.Zero))
                            restored++;
                    }
                    finally { CloseServiceHandle(svc); }
                }
            }
            finally { CloseServiceHandle(scm); }
            return restored;
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

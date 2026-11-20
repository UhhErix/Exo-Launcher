using ExoLauncher.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace ExoLauncher.Helpers;

/// <summary>
/// Frames counted the way PresentMon counts them: Windows' own event tracing (ETW) reports each present, and Exo keeps
/// those of the game's processes by the process id in the event's header. Nothing is injected or hooked, and the game is
/// never opened. Events (GameTechDev/PresentMon's PresentData/ETW headers and this PC's manifests, wevtutil gp, agree):
/// Microsoft-Windows-DXGI Present start 42 and PresentMultiplaneOverlay start 55, Microsoft-Windows-D3D9 Present start 1,
/// Microsoft-Windows-DxgKrnl Blit 166, Flip 168 and FlipMultiPlaneOverlay 252.
///
/// It costs as little as Windows allows. The session exists only while a game runs and the counter or the session recorder wants it
/// (<see cref="Services.FpsCounter"/>), and sends nothing until the game is in front (<see cref="Run"/>). It then asks by
/// event id, and DXGI and D3D9 only for the game's own processes, so nobody else's present is even written. The kernel's
/// events can't be asked for by process, so they are on only for a game that has made no DXGI or D3D9 present (Vulkan,
/// OpenGL). A real-time session outlives a process that crashes, so one left by name is stopped before a new one starts.
///
/// Windows lets administrators and Performance Log Users start one. Anyone else needs its rights on this session and these
/// providers (<see cref="AddRightsScript"/>), or the group as a last resort (<see cref="AddToGroupCommand"/>).
/// </summary>
internal sealed unsafe partial class PresentTrace : IDisposable, IFrameTrace
{
    internal const string SessionName = "ExoFpsCounter";
    internal const int Ok = 0, AccessDenied = 5, AlreadyExists = 183;
    internal const string PerformanceLogUsers = "S-1-5-32-559";

    /// <summary>This session's own id: what a user is given rights on.</summary>
    internal static readonly Guid SessionGuid = new("7E4C6A5E-93B1-4E3C-8F3A-2B1D6C0F5A91");

    // Each provider's GUID differs in its first field, which is all the callback compares.
    private const uint DxgiFirst = 0xCA11C036, D3D9First = 0x783ACA0A, DxgKrnlFirst = 0x802EC45A;
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const ushort DxgiPresent = 42, DxgiPresentMpo = 55, D3D9Present = 1, KernelBlit = 166, KernelFlip = 168, KernelFlipMpo = 252;
    private static readonly ushort[] DxgiEvents = [DxgiPresent, DxgiPresentMpo], D3D9Events = [D3D9Present], KernelEvents = [KernelFlip, KernelBlit, KernelFlipMpo];

    // The events' own keywords (manifest); the kernel's Performance channel bit stays off, as PresentMon leaves it.
    private const ulong DxgiKeywords = 0x8000000000000002, D3D9Keywords = 0x8000000000000002, KernelKeywords = 0x8000001;
    private const byte LevelInformation = 4;

    private const uint ControlStop = 1, ControlFlush = 3, EnableProvider = 1, DisableProvider = 0, ClockQpc = 1, FlagTracedGuid = 0x00020000;
    private const uint RealTime = 0x00000100, CommonBuffer = 0x10000000;
    private const uint ModeRealTime = 0x00000100, ModeRawTimestamp = 0x00001000, ModeEventRecord = 0x10000000;
    private const uint FilterEventIds = 0x80000200, FilterProcessIds = 0x80000004, IgnoreKeyword0 = 0x00000800;
    private const int MaxFilteredProcesses = 8;
    private const ushort Header32Bit = 0x0020;
    private const ulong InvalidHandle = ulong.MaxValue;
    private const uint DxgiPresentTest = 0x1;
    private const double KernelFallbackSeconds = 1.5;

    private static PresentTrace? _active;

    private readonly PresentSlots _slots = new(Stopwatch.Frequency);
    private readonly Lock _gate = new();
    private ulong _session, _consumer;
    private Thread? _thread;
    private bool _kernel;
    private int _wanted;
    private int[] _pids = [];
    private long _resumedAt, _lastFlushAt;

    // What reaches Windows, behind seams so a test can watch what is asked for without a real session.
    private readonly Action _enableApi, _disableApi, _flush;
    private readonly Action<bool> _switchKernel;
    private readonly Func<long> _clock;

    private PresentTrace()
    {
        _enableApi = EnableApi;
        _disableApi = DisableApi;
        _switchKernel = SwitchKernel;
        _flush = FlushSession;
        _clock = Stopwatch.GetTimestamp;
    }

    private PresentTrace(Action enableApi, Action disableApi, Action<bool> switchKernel, Func<long> clock, Action? flush = null)
    {
        (_enableApi, _disableApi, _switchKernel, _clock, _flush) = (enableApi, disableApi, switchKernel, clock, flush ?? (() => { }));
        _session = 1;
    }

    /// <summary>Tests only: a trace that asks nothing of Windows and says what it would ask, on a clock the test turns (in <see cref="Stopwatch.Frequency"/> ticks). Never disposed.</summary>
    internal static PresentTrace ForTest(Action enableApi, Action disableApi, Action<bool> switchKernel, Func<long> clock, Action? flush = null) =>
        new(enableApi, disableApi, switchKernel, clock, flush);

    /// <summary>Tests only: a present as the trace's callback would record it.</summary>
    internal void RecordForTest(int pid, PresentKind kind, long ticks) => _slots.Record(pid, kind, ticks);

    /// <summary>What a trace event counts as.</summary>
    internal static PresentKind Classify(uint providerFirst, ushort id) => providerFirst switch
    {
        DxgiFirst when id is DxgiPresent or DxgiPresentMpo => PresentKind.Api,
        D3D9First when id == D3D9Present => PresentKind.Api,
        DxgKrnlFirst when id is KernelFlip or KernelBlit or KernelFlipMpo => PresentKind.Kernel,
        _ => PresentKind.None,
    };

    /// <summary>A DXGI present made with DXGI_PRESENT_TEST only asks whether it could present, and shows no frame.</summary>
    internal static bool OnlyTests(uint presentFlags) => (presentFlags & DxgiPresentTest) != 0;

    /// <summary>
    /// The kernel's events, on: only for a game that has had the time to make a DXGI or D3D9 present and hasn't. When it
    /// does, they go off again: a frame needs only one report.
    /// </summary>
    internal static bool WantsKernel(bool apiLately, double secondsInFront) => !apiLately && secondsInFront >= KernelFallbackSeconds;

    /// <summary>Whether a session can start, as Windows answers: <see cref="Ok"/>, <see cref="AccessDenied"/>, or another Win32 error.</summary>
    internal static int Probe()
    {
        var error = StartSession(out _);
        if (error == Ok) StopSession();
        return error;
    }

    /// <summary>The session and its reader, sending nothing yet (<see cref="Run"/>); or null with Windows' answer.</summary>
    internal static PresentTrace? Start(out int error)
    {
        error = StartSession(out var session);
        if (error != Ok) return null;
        var trace = new PresentTrace { _session = session };
        error = trace.Open();
        if (error == Ok) return trace;
        StopSession();
        return null;
    }

    /// <summary>Which processes count from now on. The session asks for theirs alone, so a change of them is told to Windows.</summary>
    internal void Watch(IReadOnlyCollection<int> pids)
    {
        lock (_gate)
        {
            if (pids.Count == _pids.Length && pids.All(pid => _pids.Contains(pid))) return;
            _pids = [.. pids];
            _slots.Watch(pids);
            if (_wanted != 0) _enableApi();
        }
    }

    /// <summary>The overlay's wish to see frames, and the session recorder's (<see cref="Run"/>).</summary>
    internal const int ForOverlay = 1, ForRecorder = 2;

    /// <summary>
    /// Starts or stops what is sent for one of the two things that read it: on while the game is in front, nothing at all
    /// otherwise. What is sent runs while either wants it and stops when neither does. Called on every look; only a change costs.
    /// </summary>
    public void Run(int who, bool on)
    {
        lock (_gate)
        {
            var wanted = on ? _wanted | who : _wanted & ~who;
            if (wanted == _wanted || _session == 0) return;
            var was = _wanted != 0;
            _wanted = wanted;
            if (wanted != 0 && !was)
            {
                // The frames from before the pause say nothing about now, and would read as a stall.
                _slots.Forget();
                _resumedAt = _clock();
                _enableApi();
                return;
            }
            if (wanted != 0) return;
            _disableApi();
            if (_kernel) _switchKernel(false);
            _kernel = false;
        }
    }

    /// <summary>The frame gaps since the last call (<see cref="PresentSlots.Drain"/>): the session recorder's read, from its own thread only.</summary>
    public int DrainFrames(int front, List<double> gapsMs)
    {
        var lost = _slots.Drain(front, gapsMs);
        // The kernel's events are the only frames a Vulkan or OpenGL game gives, so the recorder's own read asks for them too,
        // under the same gate as the overlay's.
        lock (_gate)
        {
            if (_wanted != 0 && _session != 0) UpdateKernel(_clock());
        }
        return lost;
    }

    /// <summary>The kernel's events on or off by <see cref="WantsKernel"/>. Under the gate, while something wants frames.</summary>
    private void UpdateKernel(long now, bool flushIfWaiting = false)
    {
        var want = WantsKernel(_slots.ApiLately(now), (now - _resumedAt) / (double)Stopwatch.Frequency);
        if (want != _kernel)
        {
            _kernel = want;
            _switchKernel(want);
            if (want)
            {
                _lastFlushAt = now;
                _flush();
            }
            return;
        }
        // Flush buffers promptly during initial Vulkan/GL startup instead of waiting for the 1s ETW timer.
        if (flushIfWaiting && _kernel && now - _lastFlushAt >= Stopwatch.Frequency / 3)
        {
            _lastFlushAt = now;
            _flush();
        }
    }

    private void SwitchKernel(bool on)
    {
        if (on) EnableOne(DxgKrnl, KernelKeywords, KernelEvents, []);
        else Disable(DxgKrnl);
    }

    private void DisableApi()
    {
        Disable(Dxgi);
        Disable(D3D9);
    }

    internal FpsReading Read(int front, bool withLow = false)
    {
        var now = _clock();
        var reading = _slots.Read(now, front, withLow);
        lock (_gate)
        {
            if (_wanted == 0 || _session == 0) return reading;
            UpdateKernel(now, flushIfWaiting: !reading.Seen);
        }
        return reading;
    }

    public void Dispose()
    {
        lock (_gate) if (Interlocked.Exchange(ref _session, 0) == 0) return;
        StopSession();
        // Stopping the session ends ProcessTrace.
        if (_thread?.Join(TimeSpan.FromSeconds(3)) == false) AppLog.Warn("FPS counter: the trace's thread didn't end.");
        if (_consumer != 0) CloseTrace(_consumer);
        Volatile.Write(ref _active, null);
    }

    /// <summary>A session Exo left running (it crashed, or was closed by force) goes before anything else runs on it.</summary>
    internal static void StopStale() => StopSession();

    /// <summary>DXGI and D3D9, for the game's own processes.</summary>
    private void EnableApi()
    {
        var pids = _pids.Length <= MaxFilteredProcesses ? _pids : [];
        EnableOne(Dxgi, DxgiKeywords, DxgiEvents, pids);
        EnableOne(D3D9, D3D9Keywords, D3D9Events, pids);
    }

    private void EnableOne(Guid provider, ulong keywords, ushort[] ids, int[] pids)
    {
        // EVENT_FILTER_EVENT_ID: FilterIn (a byte), a reserved byte, the count, then the ids.
        var events = stackalloc ushort[2 + ids.Length];
        events[0] = 1;
        events[1] = (ushort)ids.Length;
        for (var i = 0; i < ids.Length; i++) events[2 + i] = ids[i];
        var descriptors = stackalloc EventFilterDescriptor[2];
        descriptors[0] = new EventFilterDescriptor { Ptr = (ulong)events, Size = (uint)(2 + ids.Length) * 2, Type = FilterEventIds };
        fixed (int* processes = pids)
        {
            descriptors[1] = new EventFilterDescriptor { Ptr = (ulong)processes, Size = (uint)pids.Length * 4, Type = FilterProcessIds };
            var parameters = new EnableTraceParameters { Version = 2, EnableProperty = IgnoreKeyword0, FilterDesc = descriptors, FilterDescCount = pids.Length > 0 ? 2u : 1u };
            var error = EnableTraceEx2(_session, &provider, EnableProvider, LevelInformation, keywords, keywords, 0, &parameters);
            if (error != Ok) AppLog.Warn($"FPS counter: Windows wouldn't send {provider} ({error}).");
        }
    }

    private void Disable(Guid provider) => EnableTraceEx2(_session, &provider, DisableProvider, 0, 0, 0, 0, null);

    private int Open()
    {
        fixed (char* name = SessionName)
        {
            var logfile = new EventTraceLogfile
            {
                LoggerName = name,
                ProcessTraceMode = ModeRealTime | ModeEventRecord | ModeRawTimestamp,
                EventRecordCallback = &OnEvent,
            };
            _consumer = OpenTraceW(&logfile);
        }
        if (_consumer == InvalidHandle)
        {
            _consumer = 0;
            return Marshal.GetLastPInvokeError();
        }
        Volatile.Write(ref _active, this);
        _thread = new Thread(Consume, 256 * 1024) { IsBackground = true, Name = "Exo FPS trace" };
        _thread.Start();
        return Ok;
    }

    private void Consume()
    {
        var handle = _consumer;
        var error = ProcessTrace(&handle, 1, null, null);
        if (error != Ok && Volatile.Read(ref _session) != 0) AppLog.Warn($"FPS counter: the trace ended ({error}).");
    }

    /// <summary>A native callback for every present it is sent: no allocation, nothing that can throw.</summary>
    [UnmanagedCallersOnly]
    private static void OnEvent(EventRecord* record)
    {
        var kind = Classify(record->ProviderFirst, record->Id);
        if (kind == PresentKind.None || Volatile.Read(ref _active) is not { } trace) return;
        if (record->ProviderFirst == DxgiFirst && record->Id == DxgiPresent)
        {
            // Its fields: the swap chain's pointer, then Flags.
            var at = (record->HeaderFlags & Header32Bit) != 0 ? 4 : 8;
            if (record->UserDataLength >= at + 4 && OnlyTests(*(uint*)(record->UserData + at))) return;
        }
        trace._slots.Record((int)record->ProcessId, kind, record->TimeStamp);
    }

    private static int StartSession(out ulong session)
    {
        var properties = NewProperties();
        fixed (char* name = SessionName)
        {
            ulong handle;
            var error = StartTraceW(&handle, name, &properties);
            if (error == AlreadyExists)
            {
                StopSession();
                properties = NewProperties();
                error = StartTraceW(&handle, name, &properties);
            }
            session = error == Ok ? handle : 0;
            return (int)error;
        }
    }

    private static void StopSession()
    {
        var properties = NewProperties();
        fixed (char* name = SessionName) ControlTraceW(0, name, &properties, ControlStop);
    }

    private static void FlushSession()
    {
        var properties = NewProperties();
        fixed (char* name = SessionName) ControlTraceW(0, name, &properties, ControlFlush);
    }

    /// <summary>Few buffers in one common set, not one per processor, so they stay in order; large enough that even at 1000 FPS they wake the reader about as seldom as the one-second flush does.</summary>
    private static TraceProperties NewProperties() => new()
    {
        WnodeBufferSize = (uint)sizeof(TraceProperties),
        WnodeGuid = SessionGuid,
        WnodeClientContext = ClockQpc,
        WnodeFlags = FlagTracedGuid,
        BufferKb = 64,
        MinimumBuffers = 2,
        MaximumBuffers = 16,
        LogFileMode = RealTime | CommonBuffer,
        FlushTimerSeconds = 1,
        LoggerNameOffset = TraceProperties.NameOffset,
    };

    // ---------------------------------------------------------------- permission

    // TRACELOG_CREATE_REALTIME, TRACELOG_ACCESS_REALTIME, WMIGUID_QUERY and WMIGUID_SET on the session; TRACELOG_GUID_ENABLE on each provider.
    private const uint SessionRights = 0x20 | 0x400 | 0x1 | 0x2, ProviderRights = 0x80;
    private const uint AddDacl = 2;

    /// <summary>
    /// What gives one user this one session and these three providers, through ETW's own EventAccessControl. Its rights are
    /// checked against the user's SID each time, which a token already holds, so it works at once: no group, no new sign-in.
    /// </summary>
    internal static string AddRightsScript(string userSid) => string.Join('\n',
        "$etw = Add-Type -PassThru -Name Etw -Namespace Exo -MemberDefinition '[DllImport(\"advapi32.dll\")] public static extern uint EventAccessControl(ref Guid guid, uint operation, byte[] sid, uint rights, [MarshalAs(UnmanagedType.U1)] bool allow);'",
        $"$sid = New-Object Security.Principal.SecurityIdentifier('{userSid}')",
        "$bytes = New-Object byte[] $sid.BinaryLength",
        "$sid.GetBinaryForm($bytes, 0)",
        $"$id = [guid]'{SessionGuid}'",
        $"[void]$etw::EventAccessControl([ref]$id, {AddDacl}, $bytes, {SessionRights}, $true)",
        $"foreach ($provider in '{Dxgi}', '{D3D9}', '{DxgKrnl}') {{ $id = [guid]$provider; [void]$etw::EventAccessControl([ref]$id, {AddDacl}, $bytes, {ProviderRights}, $true) }}");

    /// <summary><see cref="AddRightsScript"/> as one line for <see cref="ElevatedCommands"/>; null for anything that isn't a SID.</summary>
    internal static string? AddRightsCommand(string? userSid) => userSid is null || !SidPattern().IsMatch(userSid) ? null :
        $"\"{PowerShell}\" -NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(AddRightsScript(userSid)))}";

    /// <summary>The line that adds a user to Performance Log Users, by SID on both sides; null for anything that isn't a SID.</summary>
    internal static string? AddToGroupCommand(string? userSid) => userSid is null || !SidPattern().IsMatch(userSid) ? null :
        $"\"{PowerShell}\" -NoProfile -NonInteractive -Command \"Add-LocalGroupMember -SID {PerformanceLogUsers} -Member {userSid}\"";

    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [GeneratedRegex(@"\AS-1-\d+(-\d+)+\z")]
    private static partial Regex SidPattern();

    /// <summary>Whether Windows lists the user in the group now. A process's token learns it only at the next sign-in.</summary>
    internal static bool InPerformanceLogUsers(string userSid)
    {
        try
        {
            var group = new SecurityIdentifier(PerformanceLogUsers).Translate(typeof(NTAccount)).Value;
            group = group[(group.LastIndexOf('\\') + 1)..];
            if (NetLocalGroupGetMembers(null, group, 0, out var buffer, -1, out var read, out _, IntPtr.Zero) != 0) return false;
            try
            {
                var sid = new SecurityIdentifier(userSid);
                for (var i = 0; i < read; i++)
                    if (new SecurityIdentifier(Marshal.ReadIntPtr(buffer, i * IntPtr.Size)) == sid) return true;
                return false;
            }
            finally { NetApiBufferFree(buffer); }
        }
        catch (SystemException ex) // IdentityNotMappedException, or Windows' own error from the translation.
        {
            AppLog.Debug("FPS counter: the group's members weren't read: " + ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- native

    /// <summary>EVENT_TRACE_PROPERTIES (x64), with room for the session's name after it.</summary>
    [StructLayout(LayoutKind.Explicit, Size = NameOffset + 2 * 64)]
    internal struct TraceProperties
    {
        internal const int NameOffset = 120;
        [FieldOffset(0)] public uint WnodeBufferSize;
        [FieldOffset(24)] public Guid WnodeGuid;
        [FieldOffset(40)] public uint WnodeClientContext;
        [FieldOffset(44)] public uint WnodeFlags;
        [FieldOffset(48)] public uint BufferKb;
        [FieldOffset(52)] public uint MinimumBuffers;
        [FieldOffset(56)] public uint MaximumBuffers;
        [FieldOffset(64)] public uint LogFileMode;
        [FieldOffset(68)] public uint FlushTimerSeconds;
        /// <summary>The kernel's event groups a system logger session records (<see cref="LatencyTrace"/>).</summary>
        [FieldOffset(72)] public uint EnableFlags;
        [FieldOffset(88)] public uint EventsLost;
        [FieldOffset(100)] public uint RealTimeBuffersLost;
        [FieldOffset(116)] public uint LoggerNameOffset;
    }

    /// <summary>EVENT_TRACE_LOGFILEW (x64): 448 bytes, only what Exo sets.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    internal struct EventTraceLogfile
    {
        [FieldOffset(8)] public char* LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public delegate* unmanaged<EventRecord*, void> EventRecordCallback;
    }

    /// <summary>EVENT_RECORD (x64): the event's header, then its fields' length and pointer.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 112)]
    internal struct EventRecord
    {
        [FieldOffset(4)] public ushort HeaderFlags;
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;
        [FieldOffset(24)] public uint ProviderFirst;
        [FieldOffset(40)] public ushort Id;
        /// <summary>A classic kernel event's kind (<see cref="LatencyTrace"/>).</summary>
        [FieldOffset(45)] public byte Opcode;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public byte* UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventFilterDescriptor { public ulong Ptr; public uint Size; public uint Type; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnableTraceParameters
    {
        public uint Version, EnableProperty, ControlFlags;
        public Guid SourceId;
        public EventFilterDescriptor* FilterDesc;
        public uint FilterDescCount;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint StartTraceW(ulong* session, char* name, TraceProperties* properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTraceW(ulong session, char* name, TraceProperties* properties, uint code);
    [DllImport("advapi32.dll")] private static extern uint EnableTraceEx2(ulong session, Guid* provider, uint control, byte level, ulong any, ulong all, uint timeout, EnableTraceParameters* parameters);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ulong OpenTraceW(EventTraceLogfile* logfile);
    [DllImport("advapi32.dll")] private static extern uint ProcessTrace(ulong* handles, uint count, void* start, void* end);
    [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong handle);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer, int max, out int read, out int total, IntPtr resume);
    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);
}

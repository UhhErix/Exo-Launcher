using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;
using static ExoLauncher.Helpers.CoreAudioInterop;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Go Live's sound, captured with Windows' process loopback (ActivateAudioInterfaceAsync on
/// VAD\Process_Loopback; Windows 10 2004 and later) as 20 ms frames of 48 kHz stereo float for Opus.
/// A window sends only its app's sound (INCLUDE_TARGET_PROCESS_TREE on the window's process), as Discord does.
/// A screen sends every app that plays sound, each captured on its own process tree and mixed (<see cref="Plan"/>), except
/// Exo itself (the call's voices), every app Exo runs under (File Explorer, a launcher: Exo and the call are in their
/// trees, so each of their other apps is taken on its own instead), and apps that play the call or a microphone again
/// (<see cref="EchoNames"/>; Windows' own "Listen to this device" isn't an app's). Exo's Music room and games Exo started
/// are in it. An app no list names that still plays the call or your mic is heard as such (<see cref="CallEcho"/>) and
/// left out while it does, which the stream's status line says. The apps are looked at again every two seconds; app.log
/// says what is taken and what is left out, and why, whenever that changes. Silence flows as silence.
/// </summary>
/// <param name="app">The shared window's process, or 0 for a screen.</param>
internal sealed unsafe class StreamAudio(uint app = 0) : IDisposable
{
    private const string ProcessLoopback = @"VAD\Process_Loopback";
    private const uint Loopback = 0x00020000, EventCallback = 0x00040000, AutoConvert = 0x80000000 | 0x08000000;
    private const int Frame = VoiceAudio.FrameFloats;
    private const int MostHeld = Frame * 4; // 80 ms an app may run ahead of the stream's clock before its oldest sound goes

    /// <summary>
    /// Executables (lowercase, no ".exe"; a trailing * matches the start) whose own sound is a call's or a microphone's:
    /// voice apps, Discord's apps, mixers and relays that play every app again (each app is taken on its own already),
    /// mic monitors and voice changers, and streaming apps that monitor their sources.
    /// </summary>
    internal static readonly string[] EchoNames = [
        // Exo's own capture; Discord and the apps built on it
        "exocapture", "discord*", "vesktop", "legcord", "armcord", "webcord", "equibop", "dorion",
        // calls
        "ts3client*", "teamspeak", "mumble", "ventrilo", "skype", "skypeapp", "ms-teams*", "teams", "zoom", "ciscocollabhost",
        "webexhost", "ciscowebexstart", "atmgr", "slack", "element", "signal", "ringcentral*", "jitsi meet",
        // mixers and relays, mic monitors, voice changers and noise removers
        "voicemeeter*", "vbaudiomatrix*", "audiorepeater*", "steelseriessonar*", "wavelink*", "rode connect", "fxsound*", "boom3d*",
        "rzsurroundvadstreamingservice", "ss3svc*", "nahimicservice*", "lghub", "lghub_agent", "nvidia broadcast*", "nvidia rtx voice",
        "krisp", "voicemod*", "morphvox*", "clownfishvoicechanger", "voiceai", "voxal", "magicmic", "voicepea", "voicechanger",
        // streaming apps (their monitor plays the mic again)
        "obs64", "obs32", "streamlabs*", "xsplit.core", "prismlivestudio", "meldstudio",
    ];

    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<uint> _failed = [];
    private Thread? _thread;
    // A screen's apps are looked for on a thread of their own (a process snapshot and every sound session, and opening an
    // app's loopback waits on Windows): on the sound's own thread that stalled it every two seconds. What it opens and
    // closes reaches the sound's thread through these, which is the only one that touches a source's capture.
    private readonly ConcurrentQueue<Source> _joining = new();
    private readonly ConcurrentQueue<Source> _leaving = new();
    private Thread? _follower;
    private volatile string? _note;

    /// <summary>A 20 ms stereo frame (1920 floats).</summary>
    internal event Action<float[]>? Captured;

    /// <summary>For the stream's status line: the app left out now because it plays the call or your mic, or null.</summary>
    internal string? Note => _note;

    internal void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Exo stream sound", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        using var mmcss = MmcssTask.Join(MmcssTask.Audio);
        using var ready = new AutoResetEvent(false);
        var sources = new List<Source>();
        try
        {
            // A window of an app Exo runs under (File Explorer's, or a launcher's) has Exo and the call in its tree: its
            // other apps are taken one by one instead, as a screen's are.
            var root = app != 0 && Lineage(Snapshot(), (uint)Environment.ProcessId, Reused()).Contains(app) ? app : 0;
            if (app != 0 && root == 0) Open(app, "the shared app", sources, ready);
            else
            {
                if (root != 0) AppLog.Info("Discord video: the shared window's app runs Exo; its other apps' sound is taken one by one.");
                _follower = new Thread(() => FollowLoop(ready, root)) { IsBackground = true, Name = "Exo stream sound apps", Priority = ThreadPriority.BelowNormal };
                _follower.Start();
            }
            AppLog.Info(app == 0 ? "Discord video: stream sound started (this PC's apps, not the call)." : "Discord video: stream sound started (the shared app's sound).");
            var frame = new float[Frame];
            var clock = Stopwatch.StartNew();
            long sent = 0, checkedAt = 0;
            while (!_stop.IsCancellationRequested)
            {
                ready.WaitOne(10);
                while (_leaving.TryDequeue(out var gone))
                {
                    sources.Remove(gone);
                    gone.Dispose();
                }
                while (_joining.TryDequeue(out var joined)) sources.Add(joined);
                foreach (var source in sources) source.Drain();
                // Four times a second: whether an app's sound is the call's or your mic's (a few microseconds each).
                if (clock.ElapsedMilliseconds - checkedAt >= 250)
                {
                    checkedAt = clock.ElapsedMilliseconds;
                    var now = CallEcho.NowBlock();
                    foreach (var source in sources) source.Check(now);
                    _note = sources.FirstOrDefault(s => s.Carrying) is { } carrying ? $"{carrying.Shown} left out: it plays {carrying.Why.What}" : null;
                }
                // The stream's clock: a frame every 20 ms, each app adding its next 20 ms when it has them.
                var due = clock.ElapsedTicks * 50 / Stopwatch.Frequency;
                if (due - sent > 10) sent = due - 1; // the thread stalled: no burst of old frames
                for (; sent < due; sent++)
                {
                    Array.Clear(frame);
                    foreach (var source in sources) source.MixInto(frame);
                    for (var i = 0; i < frame.Length; i++) frame[i] = MathF.Max(-1f, MathF.Min(1f, frame[i]));
                    Captured?.Invoke(frame); // encoded at once: the frame is filled again after
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or EntryPointNotFoundException or TimeoutException)
        {
            AppLog.Info($"Discord video: stream sound failed ({ex.GetType().Name} 0x{ex.HResult:X8}).");
        }
        finally
        {
            // The follower first (stopped too when this thread ended on a failure), so nothing it opens is left running.
            _stop.Cancel();
            _follower?.Join(6000);
            foreach (var source in sources) source.Dispose();
            while (_leaving.TryDequeue(out var gone)) gone.Dispose();
            while (_joining.TryDequeue(out var joined)) joined.Dispose();
        }
    }

    // A screen (or the apps of a window's app Exo runs under), every two seconds until the stream stops: an app that
    // started playing joins the mix, one that closed leaves it.
    private void FollowLoop(AutoResetEvent ready, uint root)
    {
        var open = new Dictionary<uint, Source>();
        string? said = null;
        do
        {
            try { Follow(open, ready, root, ref said); }
            // ObjectDisposedException: the stream ended while an app was opening (its wake event is gone); nothing more to do.
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
            {
                AppLog.Info($"Discord video: stream sound couldn't look for apps ({ex.GetType().Name} 0x{ex.HResult:X8}).");
            }
        }
        while (!_stop.Token.WaitHandle.WaitOne(2000));
    }

    // The apps to take now against those open (this thread's own list of what it handed the sound's thread).
    private void Follow(Dictionary<uint, Source> open, AutoResetEvent ready, uint root, ref string? said)
    {
        Plan plan;
        try { plan = AppsPlaying(root, _failed); }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return; }
        foreach (var (pid, source) in open.Where(o => !plan.Take.ContainsKey(o.Key)).ToList())
        {
            open.Remove(pid);
            _leaving.Enqueue(source);
        }
        foreach (var (pid, name) in plan.Take)
        {
            if (_stop.IsCancellationRequested) return;
            if (open.ContainsKey(pid) || _failed.Contains(pid)) continue;
            var opened = new List<Source>(1);
            try
            {
                Open(pid, name, opened, ready);
                open[pid] = opened[0];
                _joining.Enqueue(opened[0]);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or TimeoutException)
            {
                // Its own apps are then taken one by one (Plan: a failed app holds nothing of theirs).
                _failed.Add(pid);
                AppLog.Info($"Discord video: stream sound can't take {name} ({ex.GetType().Name} 0x{ex.HResult:X8}).");
            }
        }
        var line = Describe(plan, open.Keys);
        if (line == said) return;
        said = line;
        AppLog.Info(line);
    }

    /// <summary>
    /// app.log's line for what a screen's sound takes (each app, and what else its capture holds) and leaves out, and why:
    /// what to read first when a stream carries the call or misses an app.
    /// </summary>
    internal static string Describe(Plan plan, IEnumerable<uint> open)
    {
        var taken = open.Where(plan.Take.ContainsKey).Select(pid =>
        {
            var with = plan.With.Where(w => w.Value == pid).Select(w => w.Key).ToList();
            return plan.Take[pid] + (with.Count == 0 ? "" : " (with " + string.Join(", ", with.Select(w => plan.Names[w]).Distinct()) + ")");
        }).ToList();
        var left = plan.Left.Select(l => $"{l.Name} ({l.Why})").Distinct().ToList();
        return "Discord video: stream sound from " + (taken.Count == 0 ? "no app" : string.Join(", ", taken))
            + (left.Count == 0 ? "" : "; leaves out " + string.Join(", ", left)) + ".";
    }

    /// <summary>A process as a snapshot had it: its parent's id and its executable (lowercase, no ".exe").</summary>
    internal readonly record struct Proc(uint Parent, string Name);

    /// <summary>
    /// What a screen's sound does with each app that has a sound session: takes it on its own process tree (Take, by pid,
    /// with the name app.log gives it), hears it in a playing app above it whose capture holds it (With: app to that app),
    /// or leaves it out, with why (Left). Names: every app named here.
    /// </summary>
    internal sealed record Plan(Dictionary<uint, string> Take, Dictionary<uint, uint> With, List<(uint Pid, string Name, string Why)> Left, Dictionary<uint, string> Names);

    /// <summary>
    /// Exo and every process up its parent chain: a capture of any of their trees holds Exo, and so the call's voices. A
    /// parent that started after its child is a reused number, not its parent (<paramref name="reused"/>), and the chain
    /// stops there.
    /// </summary>
    internal static HashSet<uint> Lineage(IReadOnlyDictionary<uint, Proc> table, uint self, Func<uint, uint, bool> reused)
    {
        var line = new HashSet<uint> { self };
        for (var child = self; line.Count <= 32 && table.TryGetValue(child, out var entry) && entry.Parent != 0 && entry.Parent != child
             && table.ContainsKey(entry.Parent) && !reused(child, entry.Parent) && line.Add(entry.Parent);)
            child = entry.Parent;
        return line;
    }

    /// <summary>
    /// The rule for a screen's sound (or, with <paramref name="root"/>, the apps of a shared window's app Exo runs under):
    /// every app playing is taken on its own process tree unless it is Exo, an app Exo runs under (<see cref="Lineage"/>),
    /// an app that plays the call or a mic (<see cref="EchoNames"/>) or started by one, or already in the capture of a
    /// playing app above it. Neither Exo, nor an app it runs under, nor one whose capture failed (<paramref name="failed"/>)
    /// holds the apps under it: those are taken on their own.
    /// </summary>
    internal static Plan Decide(IReadOnlySet<uint> playing, IReadOnlyDictionary<uint, Proc> table, uint self, Func<uint, uint, bool> reused,
        IReadOnlySet<uint> failed, uint root = 0)
    {
        var line = Lineage(table, self, reused);
        var plan = new Plan([], [], [], []);
        foreach (var pid in playing)
        {
            if (!table.TryGetValue(pid, out var entry)) continue; // gone since its session was read
            plan.Names[pid] = entry.Name;
            if (pid == self) { plan.Left.Add((pid, entry.Name, "Exo itself")); continue; }
            if (line.Contains(pid)) { plan.Left.Add((pid, entry.Name, "Exo runs under it")); continue; }
            if (Echoes(entry.Name)) { plan.Left.Add((pid, entry.Name, "it plays the call or a mic")); continue; }
            string? under = null;
            uint with = 0;
            bool inRoot = root == 0, exos = false;
            for (var (child, depth) = (pid, 0); depth < 32 && table.TryGetValue(child, out var at) && at.Parent != 0 && at.Parent != child
                 && table.TryGetValue(at.Parent, out var parent) && !reused(child, at.Parent); (child, depth) = (at.Parent, depth + 1))
            {
                if (at.Parent == root) { inRoot = true; break; }
                if (Echoes(parent.Name)) { under = parent.Name; break; }
                exos |= at.Parent == self;
                if (!line.Contains(at.Parent) && playing.Contains(at.Parent) && !failed.Contains(at.Parent)) with = at.Parent;
            }
            if (!inRoot) continue;
            if (under is not null) plan.Left.Add((pid, entry.Name, "started by " + under));
            else if (with != 0) plan.With[pid] = with;
            else plan.Take[pid] = exos ? entry.Name + " (Exo's)" : entry.Name;
        }
        return plan;
    }

    /// <summary>Whether an executable's name (lowercase, no ".exe") is one of <see cref="EchoNames"/>.</summary>
    internal static bool Echoes(string name) =>
        Array.Exists(EchoNames, e => e.EndsWith('*') ? name.StartsWith(e[..^1], StringComparison.Ordinal) : name == e);

    // Every process now, by id.
    private static Dictionary<uint, Proc> Snapshot()
    {
        var table = new Dictionary<uint, Proc>();
        foreach (var (pid, parent, exe) in BackgroundTrim.ProcessTable())
            table[(uint)pid] = new Proc((uint)parent, Path.GetFileNameWithoutExtension(exe).ToLowerInvariant());
        return table;
    }

    // A parent that started after its child (both readable) is a reused number: one snapshot's start times, read once each.
    private static Func<uint, uint, bool> Reused()
    {
        var started = new Dictionary<int, long>();
        return (child, parent) => BackgroundTrim.StartedBefore((int)child, (int)parent, started);
    }

    /// <summary>Every app with a sound session on a speaker that's plugged in, and what a screen's sound does with each (<see cref="Decide"/>).</summary>
    private static Plan AppsPlaying(uint root, IReadOnlySet<uint> failed)
    {
        var pids = new HashSet<uint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            if (enumerator.EnumAudioEndpoints(EDataFlow.ERender, DeviceStateActive, out var devices) != 0) return new Plan([], [], [], []);
            devices.GetCount(out var count);
            var iid = typeof(IAudioSessionManager2).GUID;
            for (var d = 0; d < count; d++)
            {
                if (devices.Item(d, out var device) != 0 || device.Activate(ref iid, ClsctxAll, 0, out var manager) != 0) continue;
                if (((IAudioSessionManager2)manager).GetSessionEnumerator(out var sessions) != 0) continue;
                sessions.GetCount(out var n);
                for (var i = 0; i < n; i++)
                    if (sessions.GetSession(i, out var control) == 0 && control is IAudioSessionControl2 session
                        && session.GetState(out var state) == 0 && state != 2 /* expired */ && session.GetProcessId(out var pid) == 0 && pid != 0)
                        pids.Add(pid);
            }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return Decide(pids, Snapshot(), (uint)Environment.ProcessId, Reused(), failed, root);
    }

    private static void Open(uint pid, string name, List<Source> sources, AutoResetEvent ready)
    {
        var client = Activate(pid);
        try
        {
            var format = VoiceAudio.Format();
            Marshal.ThrowExceptionForHR(client.Initialize(0, Loopback | EventCallback | AutoConvert, 200_000, 0, ref format, 0));
            // One event for every app: each wakes the thread, which looks at them all.
            Marshal.ThrowExceptionForHR(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            var iid = typeof(VoiceAudio.IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var capture));
            Marshal.ThrowExceptionForHR(client.Start());
            sources.Add(new Source(pid, name, client, (VoiceAudio.IAudioCaptureClient)capture));
        }
        catch
        {
            Marshal.ReleaseComObject(client);
            throw;
        }
    }

    // One app's sound, held until the stream's clock takes it 20 ms at a time, and watched for the call in it.
    private sealed class Source(uint pid, string name, VoiceAudio.IAudioClient client, VoiceAudio.IAudioCaptureClient capture) : IDisposable
    {
        private readonly float[] _held = new float[MostHeld];
        private int _count;
        private readonly Envelope _sound = new();
        private readonly EchoWatch _watch = new(CallEcho.NowBlock());
        private float _gain = 1;

        /// <summary>Its name as the status line shows it: its file's own description (Voicemeeter), else the executable's.</summary>
        internal string Shown => _shown ?? name;
        private string? _shown;

        internal bool Carrying => _watch.Carrying;
        internal (string What, float R, int Lag) Why => _watch.Why;

        internal void Drain()
        {
            while (capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
            {
                if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc) != 0) return;
                var played = new ReadOnlySpan<float>((void*)data, (int)frames * Opus.Channels);
                // When it played, on the call's clock: Windows' stamp, else (none, or not near now) now less its length.
                var now = VoiceAudio.QpcNow();
                var at = (long)qpc;
                if (at <= 0 || Math.Abs(now - at) > 10_000_000) at = now - frames * 10_000_000L / Opus.SampleRate;
                if ((flags & 2) != 0) _sound.Quiet((int)frames, at);
                else _sound.Add(played, at);
                if (played.Length > MostHeld) played = played[^MostHeld..];
                if (_count + played.Length > MostHeld)
                {
                    var drop = _count + played.Length - MostHeld;
                    _held.AsSpan(drop, _count - drop).CopyTo(_held);
                    _count -= drop;
                }
                if ((flags & 2) != 0) _held.AsSpan(_count, played.Length).Clear();
                else played.CopyTo(_held.AsSpan(_count));
                _count += played.Length;
                capture.ReleaseBuffer(frames);
            }
        }

        /// <summary>Its sound against the call's; app.log says when it starts or stops carrying it.</summary>
        internal void Check(long now)
        {
            var was = _watch.Why.What;
            if (!_watch.Check(_sound, CallEcho.Heard, CallEcho.Said, now)) return;
            var (what, r, lag) = _watch.Why;
            if (_watch.Carrying)
            {
                _shown = Described(pid);
                AppLog.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Discord video: stream sound leaves out {name} while it plays {what} ({what} in its sound {lag * 10} ms later, r {r:0.00})."));
            }
            else AppLog.Info($"Discord video: stream sound takes {name} again: {was} is gone from it.");
        }

        // Faded out over one frame when it starts carrying the call, back in when it stops.
        internal void MixInto(float[] frame)
        {
            if (_count < Frame) return;
            var target = _watch.Carrying ? 0f : 1f;
            if (_gain != 0 || target != 0)
            {
                var step = (target - _gain) / (Frame / 2);
                for (var i = 0; i < Frame; i += 2, _gain += step)
                {
                    frame[i] += _held[i] * _gain;
                    frame[i + 1] += _held[i + 1] * _gain;
                }
                _gain = target;
            }
            _held.AsSpan(Frame, _count - Frame).CopyTo(_held);
            _count -= Frame;
        }

        private static string? Described(uint pid)
        {
            try
            {
                return Adapters.ProcessHelper.QueryFullProcessImageName((int)pid) is { } path
                    && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 and <= 60 } description ? description.Trim() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }

        public void Dispose()
        {
            client.Stop();
            Marshal.ReleaseComObject(capture);
            Marshal.ReleaseComObject(client);
        }
    }

    // Process loopback on an app's process tree.
    private static VoiceAudio.IAudioClient Activate(uint pid)
    {
        var parameters = new ActivationParams { ActivationType = 1, TargetProcessId = pid, ProcessLoopbackMode = 0 };
        var size = Marshal.SizeOf<ActivationParams>();
        var blob = Marshal.AllocHGlobal(size);
        var variant = Marshal.AllocHGlobal(sizeof(PropVariant));
        try
        {
            Marshal.StructureToPtr(parameters, blob, false);
            *(PropVariant*)variant = new PropVariant { Type = 65 /* VT_BLOB */, BlobSize = (uint)size, BlobData = blob };
            var done = new Completion();
            ActivateAudioInterfaceAsync(ProcessLoopback, typeof(VoiceAudio.IAudioClient).GUID, variant, done, out _);
            if (!done.Finished.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Windows didn't open process loopback.");
            done.Operation!.GetActivateResult(out var hr, out var client);
            Marshal.ThrowExceptionForHR(hr);
            return (VoiceAudio.IAudioClient)client;
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;       // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
        public uint TargetProcessId;
        public int ProcessLoopbackMode;  // 0 INCLUDE_TARGET_PROCESS_TREE, 1 EXCLUDE_TARGET_PROCESS_TREE
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint BlobSize;
        [FieldOffset(16)] public nint BlobData;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, nint activationParams, IActivateCompletion completionHandler, out IActivateOperation operation);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    private interface IActivateOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    private interface IActivateCompletion
    {
        void ActivateCompleted(IActivateOperation operation);
    }

    // IAgileObject: Windows calls the handler on any thread.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    private interface IAgileObject;

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Completion : IActivateCompletion, IAgileObject
    {
        internal readonly ManualResetEventSlim Finished = new();
        internal IActivateOperation? Operation;

        public void ActivateCompleted(IActivateOperation operation)
        {
            Operation = operation;
            Finished.Set();
        }
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Windows' own recording of its drivers' work during a mouse test: logman (in every Windows) runs the NT Kernel Logger's
/// interrupts, DPCs and driver images on the Stopwatch's clock to a file for a few seconds, in one permission prompt, and stops
/// itself even if Exo goes away. Exo reads the file with Windows' trace API, keeps only what it needs and deletes it: it holds
/// the names of what ran on this PC. Some anti-cheat kernel drivers block the logger, and another program may be holding it;
/// then there is no file and Exo says so.
/// </summary>
internal sealed partial class MouseCore
{
    internal static class Recorder
    {
        /// <summary>How long Windows' prompt may wait; the panel's own wait is longer.</summary>
        private static readonly TimeSpan PromptWait = TimeSpan.FromSeconds(100);

        /// <summary>The logger's lines: its file, a mark once it runs (Exo starts the test on it), the wait, and stopping it.</summary>
        internal static IReadOnlyList<string> Commands(string logman, string ping, string file, string running, TimeSpan length) =>
        [
            $"\"{logman}\" start \"NT Kernel Logger\" -p \"Windows Kernel Trace\" \"(dpc,isr,img)\" -o \"{file}\" -ets -ct perf -bs 1024 -nb 32 256 && type nul > \"{running}\"",
            $"\"{ping}\" -n {(int)length.TotalSeconds + 4} 127.0.0.1 > nul",
            $"\"{logman}\" stop \"NT Kernel Logger\" -ets",
        ];

        /// <summary>A folder cmd reads nothing special in: a percent, a quote or a caret in a user's name would change what runs as administrator.</summary>
        internal static bool Plain(string path) => !path.Contains('%') && !path.Contains('"') && !path.Contains('^') && !path.Contains('!');

        /// <summary>A test with the recording running. The sample is null with the line to show when nothing could be read; the recording is null when Windows recorded nothing.</summary>
        internal static (Sample? Sample, Recording? Recording, string? Message) Run(TimeSpan length, CancellationToken ct)
        {
            var folder = Path.Combine(PathHelper.AppDataDir, "mouse-recording");
            if (!Plain(folder)) return (null, null, "Your user folder's name has a character Exo can't pass to Windows safely, so nothing was recorded.");
            var file = Path.Combine(folder, "drivers.etl");
            var running = Path.Combine(folder, "running");
            try
            {
                Clear(folder);
                Directory.CreateDirectory(folder);
                var system = Environment.SystemDirectory;
                var commands = Commands(Path.Combine(system, "logman.exe"), Path.Combine(system, "PING.EXE"), file, running, length);
                var ran = Task.Run(() => ElevatedCommands.Run(commands, "Mouse core", PromptWait), CancellationToken.None);
                // Windows' prompt comes first: the test starts once the logger runs.
                while (!File.Exists(running) && !ran.IsCompleted && !ct.IsCancellationRequested) ct.WaitHandle.WaitOne(100);
                if (ct.IsCancellationRequested) return (null, null, "The test stopped.");
                if (!File.Exists(running))
                {
                    var why = ran.GetAwaiter().GetResult();
                    if (why is not null) return (null, null, why.Replace("nothing changed", "nothing was recorded", StringComparison.Ordinal));
                    return (Sampler.Run(length, ct), null, null);
                }
                var sample = Sampler.Run(length, ct);
                if (sample is null) return (null, null, "Exo couldn't read your mouse.");
                // The logger stops itself; its file is whole only then.
                if (ran.GetAwaiter().GetResult() is { } stopped)
                {
                    AppLog.Warn("Mouse core: the recording didn't finish: " + stopped);
                    return (sample, null, null);
                }
                var cpu = Attribute(sample.Reports, sample.Windows, sample.Frequency).Cpu;
                return (sample, Read(file, cpu ?? -1, LateGaps(sample.Reports, sample.Frequency)), null);
            }
            finally
            {
                Clear(folder);
            }
        }

        private static void Clear(string folder)
        {
            try { if (Directory.Exists(folder)) DeletionGate.DeleteDirectory(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Mouse core: the recording's file couldn't be removed: " + ex.GetType().Name); }
        }

        /// <summary>A driver's name and version from its file, by the name the recording gave it. Nulls when Windows keeps it somewhere else.</summary>
        internal static (string? Product, string? Version) Describe(string name)
        {
            try
            {
                var path = Path.Combine(Environment.SystemDirectory, "drivers", name);
                if (!File.Exists(path)) path = Directory.EnumerateFiles(Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository"), name, SearchOption.AllDirectories).FirstOrDefault() ?? path;
                if (!File.Exists(path)) return (null, null);
                var info = FileVersionInfo.GetVersionInfo(path);
                var product = string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
                var version = string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
                return (product?.Trim(), version?.Trim());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return (null, null); }
        }

        // ---- The file ----

        // EVENT_TRACE_LOGFILEW and its TRACE_LOGFILE_HEADER on x64 (evntrace.h), by offset.
        private const int LogfileSize = 448, ModeAt = 28, HeaderAt = 120, RecordCallbackAt = 424, ContextAt = 440;
        private const int HeaderEventsLost = 48, HeaderPerfFreq = 256, HeaderClock = 272, HeaderBuffersLost = 276;
        private const uint EventRecordMode = 0x10000000, RawTimestampMode = 0x1000;
        private const int PerformanceCounter = 1; // the logger's clock: -ct perf, QueryPerformanceCounter

        /// <summary>The whole file through <see cref="RecordingReader"/>; null when it isn't there or won't open.</summary>
        internal static unsafe Recording? Read(string file, int cpu, IReadOnlyList<Late> gaps)
        {
            if (!File.Exists(file) || new FileInfo(file).Length == 0) return null;
            var reader = new RecordingReader(cpu, gaps);
            var self = GCHandle.Alloc(reader);
            var name = Marshal.StringToHGlobalUni(file);
            var logfile = stackalloc byte[LogfileSize];
            new Span<byte>(logfile, LogfileSize).Clear();
            try
            {
                *(nint*)logfile = name;
                *(uint*)(logfile + ModeAt) = EventRecordMode | RawTimestampMode;
                *(delegate* unmanaged<byte*, void>*)(logfile + RecordCallbackAt) = &OnEvent;
                *(nint*)(logfile + ContextAt) = GCHandle.ToIntPtr(self);
                var handle = OpenTrace((nint)logfile);
                if (handle == ulong.MaxValue)
                {
                    AppLog.Warn($"Mouse core: the recording wouldn't open ({Marshal.GetLastWin32Error()}).");
                    return null;
                }
                try
                {
                    var status = ProcessTrace([handle], 1, 0, 0);
                    if (status != 0) AppLog.Warn($"Mouse core: reading the recording stopped ({status}).");
                    var header = logfile + HeaderAt;
                    var clock = *(uint*)(header + HeaderClock) == PerformanceCounter && *(long*)(header + HeaderPerfFreq) == Stopwatch.Frequency;
                    return reader.Finish(clock, *(uint*)(header + HeaderEventsLost) + (long)*(uint*)(header + HeaderBuffersLost));
                }
                finally
                {
                    CloseTrace(handle);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                self.Free();
            }
        }

        /// <summary>One EVENT_RECORD (evntcons.h, x64): nothing may throw back into Windows.</summary>
        [UnmanagedCallersOnly]
        private static unsafe void OnEvent(byte* record)
        {
            try
            {
                if (GCHandle.FromIntPtr(*(nint*)(record + 104)).Target is not RecordingReader reader) return;
                var length = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(record + 86, 2));
                reader.Take(*(Guid*)(record + 24), record[45], record[80], *(long*)(record + 16), new ReadOnlySpan<byte>(*(void**)(record + 96), length));
            }
            catch (Exception ex)
            {
                // Windows' thread's own boundary: an exception can't cross back into it.
                AppLog.Debug("Mouse core: an event of the recording was skipped: " + ex.GetType().Name);
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
        private static extern ulong OpenTrace(nint logfile);

        [DllImport("advapi32.dll")]
        private static extern uint ProcessTrace([In] ulong[] handles, uint count, nint start, nint end);

        [DllImport("advapi32.dll")]
        private static extern uint CloseTrace(ulong handle);
    }
}

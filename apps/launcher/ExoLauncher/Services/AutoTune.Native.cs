using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>What a tuned part needs at sign-in and on wake: a script and a task, written only by the elevated worker (docs/architecture/auto-tune.md).</summary>
internal static class TuneSignIn
{
    internal const string TaskName = @"Exo\AutoTune";
    internal const string ScriptFile = "apply.ps1", ModuleFile = "RyzenSMU.bin";

    /// <summary>
    /// The script: integers only, in a folder only an administrator can write. It applies nothing after a crash in testing,
    /// one it finds logged since the value was written, or one it was itself part of ("applying" still there); it marks
    /// that "held", which ends it until the user presses Tune or Undo. So one crash at most, never a loop.
    /// </summary>
    internal static string Script(int[]? nvidia, int[]? ryzen, string folder, string exo)
    {
        if (nvidia is { Length: not 2 } || ryzen is { Length: not 1 }) throw new ArgumentException("A part's sign-in values have the wrong shape.");
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$d = '{Quote(folder)}'");
        script.AppendLine($"if (-not (Test-Path -LiteralPath '{Quote(exo)}')) {{ exit }}");
        script.AppendLine("""
            if (Test-Path -LiteralPath "$d\testing.json") { exit }
            if (Test-Path -LiteralPath "$d\held") { exit }
            if (Test-Path -LiteralPath "$d\applying") { Move-Item -LiteralPath "$d\applying" "$d\held" -Force; exit }
            $since = (Get-Item -LiteralPath "$d\apply.ps1").LastWriteTime
            $bad = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Kernel-Power'; Id = 41; StartTime = $since } -MaxEvents 1 -ErrorAction SilentlyContinue
            if (-not $bad) { $bad = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WHEA-Logger'; StartTime = $since } -MaxEvents 1 -ErrorAction SilentlyContinue }
            if ($bad) { Set-Content -LiteralPath "$d\held" 'crash'; exit }
            Set-Content -LiteralPath "$d\applying" ''
            """);
        script.AppendLine("Add-Type -TypeDefinition @'");
        script.AppendLine(Native);
        script.AppendLine("'@");
        if (nvidia is not null) script.AppendLine(string.Create(CultureInfo.InvariantCulture, $"[ExoTune]::Nvidia({nvidia[0]}, {nvidia[1]})"));
        if (ryzen is not null) script.AppendLine(string.Create(CultureInfo.InvariantCulture, $"[ExoTune]::Ryzen({ryzen[0]}, \"$d\\{ModuleFile}\")"));
        script.AppendLine("Start-Sleep -Seconds 120");
        script.AppendLine("Remove-Item -LiteralPath \"$d\\applying\"");
        return script.ToString();
    }

    private static string Quote(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    // Windows PowerShell's C# compiler: C# 5, nothing newer. The same calls as TuneNvidia and TuneRyzen, kept apart because
    // this has to run from a folder only an administrator can write, and Exo's own files sit where any program can.
    private const string Native = """
        using System;
        using System.IO;
        using System.Runtime.InteropServices;
        using System.Threading;
        public static class ExoTune {
          [StructLayout(LayoutKind.Sequential)] struct Offset { public uint V; public int T, P, Mhz, Min, Max; }
          const string N = @"C:\Windows\System32\nvml.dll";
          [DllImport(N)] static extern int nvmlInit_v2();
          [DllImport(N)] static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr d);
          [DllImport(N)] static extern int nvmlDeviceSetClockOffsets(IntPtr d, ref Offset o);
          [DllImport(N)] static extern int nvmlDeviceSetGpuLockedClocks(IntPtr d, uint min, uint max);
          static void Ok(int r) { if (r != 0) throw new Exception("Answered " + r); }
          public static void Nvidia(int mhz, int cap) {
            IntPtr d;
            Ok(nvmlInit_v2());
            Ok(nvmlDeviceGetHandleByIndex_v2(0, out d));
            if (cap > 0) Ok(nvmlDeviceSetGpuLockedClocks(d, 0, (uint)cap));
            Offset o = new Offset(); o.V = 24 | (1u << 24); o.Mhz = mhz;
            Ok(nvmlDeviceSetClockOffsets(d, ref o));
          }
          [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
          static extern IntPtr CreateFileW(string n, uint a, uint s, IntPtr sec, uint c, uint f, IntPtr t);
          [DllImport("kernel32.dll", SetLastError = true)]
          static extern bool DeviceIoControl(IntPtr h, uint code, byte[] i, uint il, byte[] o, uint ol, out uint r, IntPtr ov);
          const uint Load = (41394u << 16) | (0x821u << 2), Run = (41394u << 16) | (0x841u << 2);
          static IntPtr H;
          static long Call(string name, long[] a, bool answer) {
            byte[] i = new byte[32 + a.Length * 8];
            System.Text.Encoding.ASCII.GetBytes(name, 0, name.Length, i, 0);
            Buffer.BlockCopy(a, 0, i, 32, a.Length * 8);
            byte[] o = new byte[8];
            uint r;
            if (!DeviceIoControl(H, Run, i, (uint)i.Length, o, answer ? 8u : 0u, out r, IntPtr.Zero)) throw new Exception(name);
            return BitConverter.ToInt64(o, 0);
          }
          static uint Wait() {
            for (int n = 0; n < 500; n++) {
              long v = Call("ioctl_read_smu_register", new long[] { 0x3B1057C }, true);
              if (v != 0) return (uint)v;
              Thread.Sleep(2);
            }
            throw new Exception("No answer");
          }
          static void Write(uint register, uint value) { Call("ioctl_write_smu_register", new long[] { register, value }, false); }
          public static void Ryzen(int depth, string module) {
            H = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (H == new IntPtr(-1)) throw new Exception("No PawnIO");
            byte[] m = File.ReadAllBytes(module);
            uint r;
            if (!DeviceIoControl(H, Load, m, (uint)m.Length, null, 0, out r, IntPtr.Zero)) throw new Exception("Module");
            using (Mutex bus = new Mutex(false, @"Global\Access_PCI")) {
              bus.WaitOne(5000);
              try {
                Wait();
                Write(0x3B1057C, 0);
                Write(0x3B109C4, (uint)(ushort)(short)(-depth));
                for (uint i = 1; i < 6; i++) Write(0x3B109C4 + i * 4, 0);
                Write(0x3B10530, 0x36);
                if (Wait() != 1) throw new Exception("Refused");
              } finally { bus.ReleaseMutex(); }
            }
          }
        }
        """;

    /// <summary>Runs as SYSTEM at sign-in and on wake (Power-Troubleshooter 1), no window, never two at once.</summary>
    internal static string TaskXml(string script) => $$"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Exo's Auto tune: re-applies a value that passed its test. Undo in Exo removes this.</Description></RegistrationInfo>
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
            <EventTrigger>
              <Enabled>true</Enabled>
              <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="System"&gt;&lt;Select Path="System"&gt;*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
            </EventTrigger>
          </Triggers>
          <Principals><Principal id="Exo"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
          </Settings>
          <Actions Context="Exo">
            <Exec>
              <Command>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe</Command>
              <Arguments>-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{{System.Security.SecurityElement.Escape(script)}}"</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>Makes the script and task match the record: what holds is re-applied, and with nothing left both go.</summary>
    internal static void Renew(TuneStore store)
    {
        var parts = store.Load();
        int[]? Values(string id) => parts.GetValueOrDefault(id) is { Applied: > 0, SignIn: { } values } ? values : null;
        var (nvidia, ryzen) = (Values("nvidia"), Values("ryzen"));
        var script = Path.Combine(store.Folder, ScriptFile);
        if (nvidia is null && ryzen is null)
        {
            Tasks($"/Delete /TN \"{TaskName}\" /F", quiet: true);
            DeletionGate.DeleteFile(script);
            DeletionGate.DeleteFile(Path.Combine(store.Folder, ModuleFile));
            return;
        }
        if (Environment.ProcessPath is not { } exo) return;
        if (ryzen is not null) File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "pawnio", ModuleFile), Path.Combine(store.Folder, ModuleFile), overwrite: true);
        File.WriteAllText(script, Script(nvidia, ryzen, store.Folder, exo), Encoding.UTF8);
        var xml = Path.Combine(store.Folder, "task.xml");
        File.WriteAllText(xml, TaskXml(script), Encoding.Unicode);
        Tasks($"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F", quiet: false);
        DeletionGate.DeleteFile(xml);
    }

    private static void Tasks(string arguments, bool quiet)
    {
        using var run = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
        if (run is null || !run.WaitForExit(TimeSpan.FromSeconds(20)) || (run.ExitCode != 0 && !quiet)) AppLog.Warn("Auto tune: the sign-in task didn't change.");
    }
}

/// <summary>What surrounds the worker's test on a real PC.</summary>
internal sealed class TuneNativeHost(TuneStore store) : ITuneHost
{
    public string? Refusal()
    {
        // Exists only while Exo runs, so no event means Exo closed: nobody is left to say a game started.
        if (!EventWaitHandle.TryOpenExisting(GameRunningSignal.Name, out var signal)) return "Exo closed, so testing stopped and you're back at stock.";
        using (signal)
            if (signal.WaitOne(0)) return "A game started, so testing stopped and you're back at stock.";
        if (new PcHealth.NativePower().Battery()) return "Your PC went on battery, so testing stopped and you're back at stock.";
        // A game outside the library, full screen: QUNS_RUNNING_D3D_FULL_SCREEN.
        return SHQueryUserNotificationState(out var state) == 0 && state == 3 ? "A full-screen game started, so testing stopped and you're back at stock." : null;
    }

    public int HardwareErrors() => TuneEvents.Count;

    public bool StopAsked()
    {
        if (!EventWaitHandle.TryOpenExisting(AutoTuneWorker.StopEvent, out var stop)) return false;
        using (stop) return stop.WaitOne(0);
    }

    public void Reapply() => TuneSignIn.Renew(store);

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}

/// <summary>
/// ExoLauncher.exe --auto-tune start|undo nvidia|radeon|ryzen [cooler|faster], started elevated by one press, so Windows
/// asks once however long the test runs. No window and no WinUI; Exo reads what it writes. Everything it writes goes
/// through <see cref="TuneLimits"/> whatever the arguments said.
/// </summary>
internal static class AutoTuneWorker
{
    internal const string StopEvent = @"Local\ExoLauncher.AutoTuneStop";
    internal const string Flag = "--auto-tune";
    internal const string OneWorker = @"Local\ExoLauncher.AutoTuneWorker";

    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ExoLauncher Tune");

    internal static int Run(string[] args)
    {
        var at = Array.FindIndex(args, arg => arg.Equals(Flag, StringComparison.OrdinalIgnoreCase));
        var action = args.ElementAtOrDefault(at + 1);
        var id = args.ElementAtOrDefault(at + 2) ?? "";
        var mode = args.ElementAtOrDefault(at + 3) is "faster" ? "faster" : "cooler";
        if (action is not ("start" or "undo") || id is not ("nvidia" or "radeon" or "ryzen")) return 2;
        using var one = new Mutex(false, OneWorker, out var first);
        if (!first) return 3;
        Directory.CreateDirectory(Folder);
        var store = new TuneStore(Folder);
        try
        {
            var host = new TuneNativeHost(store);
            var (part, test) = Build(id, mode, host);
            try
            {
                var runner = new TuneRunner(part, test, host, store, mode);
                if (action == "undo")
                {
                    runner.Undo();
                    return 0;
                }
                if (!TuneEvents.Start(gpu: id != "ryzen")) throw new TuneDriverException("Exo couldn't watch Windows' error log, so it didn't test.");
                runner.Run();
                return 0;
            }
            finally { (part as IDisposable)?.Dispose(); }
        }
        catch (Exception ex) when (ex is TuneDriverException or IOException or UnauthorizedAccessException or InvalidOperationException or GpuLoad.Lost)
        {
            AppLog.Warn("Auto tune: " + ex.Message);
            store.Save(id, store.Part(id) with { Phase = "stopped", Testing = null, SecondsLeft = null, Note = ex is TuneDriverException or GpuLoad.Lost ? ex.Message : "Exo couldn't start this." });
            return 1;
        }
    }

    private static (ITunePart Part, ITuneTest Test) Build(string id, string mode, TuneNativeHost host)
    {
        switch (id)
        {
            case "nvidia":
                return (new TuneNvidia(mode, NvidiaSensors.Read()?.Limit?.BadC), new TuneGpuTest(0x10DE, NvidiaSensors.Read));
            case "radeon":
                return (new TuneRadeon(mode, AmdAdlx.ReadSensors()?.Limit?.BadC, () => host.StopAsked() || host.Refusal() is not null), new TuneGpuTest(0x1002, AmdAdlx.ReadSensors));
            default:
                var zen = TuneRyzen.Generation(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), PcHealth.CpuName()) ?? throw new TuneDriverException("Exo doesn't tune this processor.");
                var cores = MouseCore.Sampler.ReadTopology().Cores.Select(core => core.Threads[0]).ToList();
                if (cores.Count == 0) throw new TuneDriverException("Windows didn't list your processor's cores.");
                var ryzen = new TuneRyzen(zen, cores.Count, Path.Combine(AppContext.BaseDirectory, "Assets", "pawnio", TuneSignIn.ModuleFile));
                return (ryzen, new TuneCpuTest(cores, ryzen.TemperatureC));
        }
    }
}

/// <summary>
/// system.tune: what Exo shows of Auto tune and the presses that start the worker. Only reads Program Files; the worker
/// writes it. Off until pressed: nothing here runs by itself, and nothing else in Exo calls it.
/// </summary>
internal static class AutoTuneService
{
    /// <summary>
    /// The owner's switch for the CPU row, docs/architecture/auto-tune.md › The owner's decision. False until PawnIO has been
    /// tried with Vanguard and EAC running: with it off, no part, row or press reaches the Ryzen route.
    /// </summary>
    private const bool RyzenAvailable = false;

    private static string[]? _parts;
    private static readonly Lock Gate = new();
    private static EventWaitHandle? _stop;

    private static string[] Parts()
    {
        lock (Gate)
        {
            if (_parts is { } known) return known;
            // The driver isn't touched during a game; asked again next time.
            if (GameRunningSignal.Running) return [];
            if (PcHealth.ThisPcIsLaptop()) return _parts = [];
            var found = new List<string>();
            if (GraphicsService.Vendor() is "nvidia" && TuneNvidia.Supported()) found.Add("nvidia");
            else if (GraphicsService.Vendor() is "amd" && AmdAdlx.AutoTuneSupported()) found.Add("radeon");
            if (RyzenAvailable && TuneRyzen.Generation(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), PcHealth.CpuName()) is not null) found.Add("ryzen");
            return _parts = [.. found];
        }
    }

    /// <summary>The worker holds its mutex for as long as it lives. An elevated one refuses Exo access, which also says it's there.</summary>
    private static bool WorkerAlive()
    {
        try
        {
            using var held = Mutex.OpenExisting(AutoTuneWorker.OneWorker);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static readonly TuneStore Store = new(AutoTuneWorker.Folder);

    private static object Answer(string? message)
    {
        var marker = Store.Marker();
        var held = Store.Held();
        var alive = WorkerAlive();
        var parts = Parts().Select(id =>
            TuneView.Of(id, TuneRunner.Seen(id, Store.Part(id), marker, held, alive), id != "ryzen" || TuneRyzen.DriverReady(), CoreCount(id), GameRunningSignal.Running)).ToArray();
        return new { ok = message is null, message, parts };
    }

    private static int CoreCount(string id) => id == "ryzen" ? Math.Max(MouseCore.Sampler.ReadTopology().Cores.Count, 1) : 0;

    /// <summary>The view, after <paramref name="action"/> if there is one (start, stop, undo).</summary>
    internal static object Act(string? action, string? part, string? mode)
    {
        if (action is null || part is null || !Parts().Contains(part)) return Answer(null);
        return Answer(action switch
        {
            "start" => Start(part, mode is "faster" ? "faster" : "cooler"),
            "stop" => Stop(),
            "undo" => Undo(part),
            _ => null,
        });
    }

    private static string? Start(string part, string mode)
    {
        if (GameRunningSignal.Running || GameRunning()) return "Close your game first.";
        if (new PcHealth.NativePower().Battery()) return "Plug your PC in first.";
        if (part == "ryzen" && !TuneRyzen.DriverReady()) return "PawnIO 2.1 or newer is needed first.";
        lock (Gate)
        {
            _stop ??= new EventWaitHandle(false, EventResetMode.ManualReset, AutoTuneWorker.StopEvent);
            _stop.Reset();
        }
        return Launch($"{AutoTuneWorker.Flag} start {part} {mode}", wait: false);
    }

    private static bool GameRunning() => App.Services.Launcher.AnyGameRunning || LeanSignal.Active || MainWindow.RunningLibraryGames().Count > 0;

    private static string? Stop()
    {
        lock (Gate) _stop?.Set();
        return null;
    }

    private static string? Undo(string part)
    {
        // A test running: Stop already means stock, and it is the worker's to do.
        lock (Gate)
            if (Store.Part(part) is { Phase: "testing" } && WorkerAlive())
            {
                _stop?.Set();
                return null;
            }
        return Launch($"{AutoTuneWorker.Flag} undo {part}", wait: true);
    }

    private static string? Launch(string arguments, bool wait)
    {
        if (Environment.ProcessPath is not { } exo) return "Exo couldn't start this.";
        try
        {
            using var worker = Process.Start(new ProcessStartInfo(exo, arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.SystemDirectory });
            if (worker is null) return "Windows didn't start this.";
            if (wait && !worker.WaitForExit(TimeSpan.FromSeconds(30))) return "That didn't finish. Restart your PC to be back at stock.";
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Windows' permission was declined, so nothing changed."; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("Auto tune: the worker didn't start: " + ex.Message);
            return "Windows didn't start this.";
        }
    }
}

/// <summary>What the page gets for one part, in its words. Pure.</summary>
internal static class TuneView
{
    private static readonly TuneLimits NvidiaLimits = TuneNvidia.For(240, null);

    /// <summary>How a depth reads on its part.</summary>
    internal static string Say(string id, int depth) => id switch
    {
        "nvidia" => string.Create(CultureInfo.InvariantCulture, $"+{depth} MHz"),
        "ryzen" => string.Create(CultureInfo.InvariantCulture, $"Curve Optimizer -{depth}"),
        _ => "AMD's tuning",
    };

    internal static int Minutes(string id, int cores)
    {
        TuneLimits limits = id switch { "nvidia" => NvidiaLimits, "ryzen" => TuneRyzen.For(cores), _ => new TuneLimits(1, 1, 0, 0, 60, 600, 180) };
        // AMD's own tuning runs inside its first step and doesn't say how long; 15 minutes is a guess, in line with the NVIDIA Scanner's 10 to 20.
        var theirs = id == "radeon" ? 15 * 60 : 0;
        return (limits.Furthest / limits.Step * limits.StepSeconds + limits.FinalSeconds + limits.BaselineSeconds * 2 + theirs + 59) / 60;
    }

    /// <summary>The warning before a first run: what it does, the warranty and the risk, on the row (DESIGN.md › Confirm).</summary>
    internal static string Warning(string id, int minutes)
    {
        const string Risk = "and can crash your PC while it tests, so save your work first. Exo goes back to stock by itself if anything goes wrong.";
        return id switch
        {
            "nvidia" => $"Moves your card's clock up at each voltage and tests each step, about {minutes} minutes. It's outside NVIDIA's specifications, can void your warranty, {Risk}",
            "radeon" => $"Runs AMD's own tuning, then tests it, about {minutes} minutes. It replaces any tuning you've set in AMD Software, and Undo clears that too. It's outside AMD's specifications, can void your warranty, {Risk}",
            _ => $"Lowers your processor's voltage with Curve Optimizer and tests one core at a time, about {minutes} minutes. AMD says running outside its specifications voids your warranty. It {Risk[4..]} What passes comes back at each sign-in.",
        };
    }

    internal const string NeedsDriver = "Needs PawnIO, a signed driver from pawnio.eu that stays loaded until you uninstall it from Windows. FACEIT has blocked it, and other anti-cheats haven't said either way: if a game won't start, uninstall it.";

    internal static object Of(string id, TunePartState state, bool driverReady, int cores, bool gameRunning)
    {
        var minutes = Minutes(id, cores);
        var testing = state.Phase == "testing";
        return new
        {
            id,
            state = !driverReady ? "needs-driver" : testing ? "testing" : state.Applied > 0 ? "tuned" : state.Phase is "crashed" ? "crashed" : "ready",
            modes = id != "ryzen",
            mode = state.Mode.Length > 0 ? state.Mode : "cooler",
            testing = testing ? state.Testing is > 0 ? "Testing " + Say(id, state.Testing.Value) : "Measuring at stock" : null,
            minutesLeft = testing && state.SecondsLeft is { } left ? (left + 59) / 60 : (int?)null,
            applied = state.Applied > 0 ? Say(id, state.Applied) : null,
            before = state.Before is { } before ? new { score = before.Score, peakC = before.PeakC } : null,
            after = state.After is { } after ? new { score = after.Score, peakC = after.PeakC } : null,
            note = state.Note,
            warning = !driverReady ? NeedsDriver : state.Accepted ? null : Warning(id, minutes),
            minutes,
            canUndo = state.Applied > 0 || state.Phase == "crashed" || testing,
            blocked = gameRunning && !testing ? "Close your game first." : null,
        };
    }
}

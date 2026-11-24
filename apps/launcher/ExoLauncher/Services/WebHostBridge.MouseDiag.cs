using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private static readonly MouseDiagnostics.NativeFixApplier MouseDiagApplier = new();
    private static readonly MouseDiagnostics.FixManager MouseDiagFixes = new(
        new InterruptMode.NativeDevices(),
        MouseDiagApplier,
        Path.Combine(PathHelper.AppDataDir, "mouse-diag-fixes.json"),
        InterruptMode.BootTime,
        () => DateTimeOffset.UtcNow);

    private static int _mouseDiagBusy;
    private static readonly Lock MouseDiagLock = new();
    private static CancellationTokenSource? _mouseDiagCts;
    private static string? _mouseDiagStopFile;
    private static MouseDiagnostics.JitterReport? _mouseDiagJitter;
    private static MouseDiagTrace.Result? _mouseDiagEtw;
    private static string? _mouseDiagEtwProblem;
    private static MouseCore.Sample? _mouseDiagSample;
    private static MouseCore.Work? _mouseDiagWork;
    private static string? _mouseDiagControllerId;

    internal static async Task<object> MouseDiagStatusAsync(CancellationToken ct)
    {
        return await Task.Run(() => BuildMouseDiagReply(ok: true, message: null), ct).ConfigureAwait(false);
    }

    internal static object MouseDiagCancel()
    {
        lock (MouseDiagLock)
        {
            try { _mouseDiagCts?.Cancel(); } catch (ObjectDisposedException) { }
            if (_mouseDiagStopFile is { } stop)
            {
                try { File.WriteAllText(stop, "1"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return new { ok = true };
    }

    internal static async Task<object> MouseDiagMeasureAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var withEtw = hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("withEtw", out var el) && el.ValueKind == JsonValueKind.True;
        if (App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0)
        {
            return await Task.Run(() => BuildMouseDiagReply(ok: false, message: "Close your game first."), ct).ConfigureAwait(false);
        }
        if (Interlocked.Exchange(ref _mouseDiagBusy, 1) == 1)
        {
            return await Task.Run(() => BuildMouseDiagReply(ok: false, message: "A diagnostic measurement is already running."), ct).ConfigureAwait(false);
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (MouseDiagLock) _mouseDiagCts = runCts;

        try
        {
            string? message = await Task.Run(() => RunDiagMeasurement(withEtw, runCts.Token), CancellationToken.None).ConfigureAwait(false);
            return await Task.Run(() => BuildMouseDiagReply(ok: message is null, message: message), ct).ConfigureAwait(false);
        }
        finally
        {
            lock (MouseDiagLock)
            {
                if (ReferenceEquals(_mouseDiagCts, runCts)) _mouseDiagCts = null;
                _mouseDiagStopFile = null;
            }
            Volatile.Write(ref _mouseDiagBusy, 0);
        }
    }

    internal static async Task<object> MouseDiagFixAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var id = ReadString(p, hasParams, "id") ?? "";
        var action = ReadString(p, hasParams, "action") ?? "";
        if (App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0)
        {
            return await Task.Run(() => BuildMouseDiagReply(ok: false, message: "Close your game first."), ct).ConfigureAwait(false);
        }
        if (Interlocked.Exchange(ref _mouseDiagBusy, 1) == 1)
        {
            return await Task.Run(() => BuildMouseDiagReply(ok: false, message: "Exo is already running a diagnostic action."), ct).ConfigureAwait(false);
        }

        try
        {
            var message = await Task.Run(() =>
            {
                var ctx = BuildFixContext();
                return action switch
                {
                    "apply" => MouseDiagFixes.Apply(id, ctx),
                    "undo" => MouseDiagFixes.Undo(id),
                    _ => "Unknown fix action.",
                };
            }, ct).ConfigureAwait(false);
            return await Task.Run(() => BuildMouseDiagReply(ok: message is null, message: message), ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _mouseDiagBusy, 0);
        }
    }

    private static string? RunDiagMeasurement(bool withEtw, CancellationToken ct)
    {
        MouseDiagTrace.Result? etwResult = null;
        string? etwProblem = null;
        Process? etwProc = null;
        string? answerPath = null;
        string? readyPath = null;
        string? stopPath = null;

        try
        {
            if (withEtw)
            {
                if (HostMode.IsDisposable || Environment.ProcessPath is not { } exo)
                {
                    etwProblem = "ETW tracing is unavailable in this mode.";
                }
                else
                {
                    var token = Guid.NewGuid().ToString("N");
                    var tmp = Path.GetTempPath();
                    answerPath = Path.Combine(tmp, $"exo-{token}.mouse-diag.json");
                    readyPath = Path.Combine(tmp, $"exo-{token}.ready");
                    stopPath = Path.Combine(tmp, $"exo-{token}.stop");
                    lock (MouseDiagLock) _mouseDiagStopFile = stopPath;

                    try
                    {
                        etwProc = Process.Start(new ProcessStartInfo(exo, $"{MouseDiagTrace.Flag} \"{answerPath}\" \"{readyPath}\" \"{stopPath}\" {(int)MouseCore.TestLength.TotalSeconds}")
                        {
                            UseShellExecute = true,
                            Verb = "runas",
                            WindowStyle = ProcessWindowStyle.Hidden,
                            WorkingDirectory = Environment.SystemDirectory,
                        });
                        if (etwProc is null)
                        {
                            etwProblem = "Could not start the elevated ETW trace.";
                        }
                        else
                        {
                            var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                            while (!File.Exists(readyPath) && !etwProc.HasExited && !ct.IsCancellationRequested && DateTime.UtcNow < readyDeadline)
                            {
                                ct.WaitHandle.WaitOne(50);
                            }
                            if (ct.IsCancellationRequested)
                            {
                                SignalStop(stopPath);
                                return "Measurement canceled.";
                            }
                            if (!File.Exists(readyPath) && etwProc.HasExited)
                            {
                                etwProblem = ReadEtwAnswer(answerPath)?.Problem ?? "Windows didn't start the ETW kernel trace.";
                                etwProc.Dispose();
                                etwProc = null;
                            }
                        }
                    }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                    {
                        etwProblem = "Administrator permission was declined; ran without ETW driver attribution.";
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        etwProblem = "Could not start the elevated ETW trace.";
                        AppLog.Warn("Mouse diag ETW launch: " + ex.Message);
                    }
                }
            }

            var sample = MouseCore.Sampler.Run(MouseCore.TestLength, ct);
            if (ct.IsCancellationRequested)
            {
                SignalStop(stopPath);
                return "Measurement canceled.";
            }

            if (etwProc is not null)
            {
                SignalStop(stopPath);
                etwProc.WaitForExit(15_000);
                var ans = ReadEtwAnswer(answerPath);
                etwResult = ans?.Result;
                etwProblem = ans?.Problem ?? (etwResult is null ? "ETW trace did not return data." : null);
            }

            if (sample is null) return "Exo couldn't read Raw Input from your mouse.";

            var work = MouseCore.Attribute(sample.Reports, sample.Windows, sample.Frequency);
            var devices = new InterruptMode.NativeDevices();
            var pciList = devices.List();
            var (controller, _) = sample.Mouse is null ? ((InterruptMode.Device?)null, (string?)null) : MouseCore.Controller(sample.Mouse, devices, pciList);
            var allocatedCpu = controller?.Affinity is { } aim && System.Numerics.BitOperations.PopCount(aim) == 1
                ? System.Numerics.BitOperations.TrailingZeroCount(aim)
                : (int?)null;
            var bestCpu = work.Cpu ?? allocatedCpu ?? etwResult?.MouseControllerCore;

            if (etwResult is not null && bestCpu is { } mc && etwResult.MouseControllerCore != mc)
            {
                etwResult = etwResult with
                {
                    MouseControllerCore = mc,
                    Cores = etwResult.Cores.Select(c => c with { IsMouseCore = c.Core == mc }).ToList(),
                };
            }

            var effectiveWork = work with { Cpu = bestCpu };
            var jitter = MouseDiagnostics.AnalyzeJitter(sample.Reports, sample.Frequency, sample.RefreshHz, effectiveWork, etwResult);

            _mouseDiagSample = sample;
            _mouseDiagWork = effectiveWork;
            _mouseDiagControllerId = controller?.Id;
            _mouseDiagJitter = jitter;
            if (withEtw)
            {
                _mouseDiagEtw = etwResult;
                _mouseDiagEtwProblem = etwProblem;
            }
            MouseDiagFixes.RecordRetest(jitter, controller?.Id);
            return null;
        }
        finally
        {
            etwProc?.Dispose();
            TryDeleteTemp(answerPath);
            TryDeleteTemp(readyPath);
            TryDeleteTemp(stopPath);
        }
    }

    private static MouseDiagnostics.FixContext BuildFixContext()
    {
        var devices = new InterruptMode.NativeDevices();
        var pciList = devices.List();
        var boot = InterruptMode.BootChain(devices);
        var tree = Peripherals.Tree();
        var cards = Peripherals.List(() => App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0);

        var mouseId = _mouseDiagSample?.Mouse ?? cards.FirstOrDefault(c => c.Kind == "mouse" && c.Source is not null)?.Source;
        InterruptMode.Device? controller = null;
        if (_mouseDiagControllerId is { } cid)
        {
            controller = pciList.FirstOrDefault(d => string.Equals(d.Id, cid, StringComparison.OrdinalIgnoreCase));
        }
        else if (mouseId is not null)
        {
            (controller, _) = MouseCore.Controller(mouseId, devices, pciList);
        }

        var hubIds = MouseDiagnostics.FindMouseHubInstances(mouseId, tree);
        var hubStates = new Dictionary<string, MouseDiagnostics.HubPowerSaved>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hubIds) hubStates[h] = MouseDiagnostics.ReadHubPowerSaved(h);

        var topology = _mouseDiagSample?.Topology ?? MouseCore.Sampler.ReadTopology();
        return new MouseDiagnostics.FixContext(
            _mouseDiagJitter,
            controller,
            topology,
            _mouseDiagWork,
            pciList,
            boot,
            hubIds,
            hubStates,
            MouseDiagApplier.ReadAcUsbSuspend());
    }

    private static object BuildMouseDiagReply(bool ok, string? message)
    {
        var cards = Peripherals.List(() => App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0);
        var usb = MouseDiagnostics.ReadTopology(cards, _mouseDiagJitter?.PollingRateHz);
        var fixes = MouseDiagFixes.Evaluate(BuildFixContext());
        return new
        {
            ok,
            message,
            running = Volatile.Read(ref _mouseDiagBusy) == 1,
            jitter = _mouseDiagJitter,
            usb,
            etw = _mouseDiagEtw,
            etwProblem = _mouseDiagEtwProblem,
            fixes,
        };
    }

    private static void SignalStop(string? stopPath)
    {
        if (stopPath is null) return;
        try { File.WriteAllText(stopPath, "1"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static MouseDiagTrace.Answer? ReadEtwAnswer(string? answerPath)
    {
        if (answerPath is null || !File.Exists(answerPath)) return null;
        try { return JsonSerializer.Deserialize<MouseDiagTrace.Answer>(File.ReadAllText(answerPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void TryDeleteTemp(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try { DeletionGate.DeleteFile(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

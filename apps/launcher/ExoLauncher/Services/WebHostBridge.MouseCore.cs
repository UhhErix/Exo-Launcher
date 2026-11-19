using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// system.mouseCore: Exo Mouse's CPU core panel, relayed as exo.mouseCore by ui/src/lib/mouseRelay.ts, which passes only
/// the action (<see cref="MouseCore"/>). No action reads where Exo's
/// change stands; measure runs the 10-second test (the mouse moving in circles); drivers runs it with Windows recording
/// its drivers (Windows' permission prompt), to name who held the late reports up; apply gives the last test's USB
/// controller the CPU it offered (Windows' permission prompt); undo puts it back. Each answer carries the state after
/// it and this session's last test. React never gets a device instance id.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly MouseCore PcMouseCore = new(new InterruptMode.NativeDevices(), new MouseCore.ElevatedWriter(),
        Path.Combine(Helpers.PathHelper.AppDataDir, "mouse-core-backup.json"), InterruptMode.BootTime, () => DateTimeOffset.UtcNow)
    {
        InputControllers = () =>
        {
            var tree = Peripherals.Tree();
            var parents = tree.ToDictionary(n => n.Id, n => n.Parent, StringComparer.OrdinalIgnoreCase);
            return Peripherals.List(() => App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0).Where(c => c.Kind is "mouse" or "keyboard" && c.Source is not null).Select(c =>
            {
                string? id = c.Source;
                for (var hops = 0; id is not null && !id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) && hops < 32; hops++) id = parents.GetValueOrDefault(id);
                return id;
            }).OfType<string>();
        },
    };

    private static int _mouseCoreBusy;

    private static async Task<object> SystemMouseCoreAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        string? message = null;
        if (action is "measure" or "drivers" or "apply" or "undo" or "spread" or "unspread")
        {
            // A game changes what the test reads and is no moment to change how the mouse reaches the processor.
            if (App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0) message = "Close your game first.";
            // One test or change at a time: a second would read the same mouse or race the same key.
            else if (Interlocked.Exchange(ref _mouseCoreBusy, 1) == 1) message = "Exo is already on it.";
            else
            {
                try
                {
                    message = await Task.Run(() => action switch
                    {
                        "measure" => MouseCore.Sampler.Run(MouseCore.TestLength, ct) is { } sample
                            ? (PcMouseCore.Measure(sample) is { Verdict: MouseCore.Verdict.Retry } retry ? retry.Detail : Examined(sample))
                            : "Exo couldn't read your mouse.",
                        "drivers" => RecordDrivers(ct),
                        "apply" => PcMouseCore.Apply(),
                        "spread" => PcMouseCore.ApplyLayout(),
                        "unspread" => PcMouseCore.UndoLayout(),
                        _ => PcMouseCore.Undo(),
                    }, ct).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _mouseCoreBusy, 0);
                }
            }
        }
        var state = await Task.Run(PcMouseCore.Read, ct).ConfigureAwait(false);
        var last = PcMouseCore.Last;
        var layout = await Task.Run(PcMouseCore.ReadLayout, ct).ConfigureAwait(false);
        return new
        {
            ok = message is null,
            message,
            stage = state.Stage.ToString().ToLowerInvariant(),
            detail = state.Detail,
            cpu = state.Cpu,
            better = state.Better,
            canUndo = state.CanUndo,
            drivers = PcMouseCore.LastDrivers,
            wireless = PcMouseCore.LastDoctor,
            layout = new
            {
                offer = layout.Offer.Select(m => new { name = m.Name, kind = m.Kind, cpu = m.Cpu }).ToList(),
                applied = layout.Applied,
                restart = layout.Restart,
                canUndo = layout.CanUndo,
            },
            // Late reports worth naming a driver for: the recording asks Windows, so it isn't offered for nothing.
            canName = last is { Verdict: not MouseCore.Verdict.Retry, Test.Missed: >= 3 } && !state.Stage.Equals(MouseCore.Stage.Restart),
            before = Dto(state.Before),
            after = Dto(state.After),
            test = last is null ? null : new
            {
                verdict = last.Verdict.ToString().ToLowerInvariant(),
                detail = last.Detail,
                offer = last.Offer,
                result = Dto(last.Test),
            },
        };
    }

    /// <summary>The test with Windows recording its drivers; the line to show when it couldn't run.</summary>
    private static string? RecordDrivers(CancellationToken ct)
    {
        var (sample, recording, message) = MouseCore.Recorder.Run(MouseCore.TestLength, ct);
        if (sample is null) return message ?? "Exo couldn't read your mouse.";
        PcMouseCore.Name(sample, recording, MouseCore.Recorder.Describe);
        return Examined(sample);
    }

    /// <summary>The wireless doctor, for a mouse that isn't wired; nothing to show the user beyond its reading.</summary>
    private static string? Examined(MouseCore.Sample sample)
    {
        PcMouseCore.Examine(sample, mouse => MouseCore.ReadAir(mouse, Peripherals.Tree(),
            Peripherals.List(() => App.Services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0), WifiGameMode.Channel, UsbPorts.ReadMap));
        return null;
    }

    private static object? Dto(MouseCore.Test? t) => t is null ? null : new
    {
        rate = t.Rate,
        @checked = t.Checked,
        missed = t.Missed,
        longest = t.Longest,
        p999 = t.P999,
        refresh = t.Refresh,
        cpu = t.Cpu,
        flow = t.Flow,
    };
}

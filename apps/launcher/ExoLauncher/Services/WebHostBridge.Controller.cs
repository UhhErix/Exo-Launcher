using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The controller's page in Devices (<see cref="ControllerLab"/>), read only: controller.read (its link, a DualSense's
/// firmware against Sony's newest, and what sits between it and games), controller.measure (how it reports, over a few
/// seconds), controller.test (one step of the stick test: at rest, or rolled around the edge) and controller.watch (its
/// sticks, triggers and an Edge's profile, live as controller.state while the page shows). Any controller the lab reads
/// (<see cref="ControllerLab.FamilyOf"/>); nothing is measured while a game runs.
/// </summary>
public sealed partial class WebHostBridge
{
    private CancellationTokenSource? _controllerWatch;

    private bool AnyGame() => _services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0;

    private Peripherals.Peripheral? ControllerCard(JsonElement p, bool hasParams) =>
        ReadString(p, hasParams, "id") is { Length: > 0 } id
            ? Peripherals.List(AnyGame).FirstOrDefault(c => c.Id == id && ControllerLab.FamilyOf(c) is not null)
            : null;

    private async Task<object> ControllerReadAsync(JsonElement p, bool hasParams)
    {
        if (ControllerCard(p, hasParams) is not { } card) return new { ok = false, message = "Exo doesn't read this controller yet." };
        var sony = ControllerLab.Supported(card.VendorId, card.ProductId);
        var firmware = AnyGame() || !sony ? null : ControllerLab.ReadFirmware(card);
        var latest = firmware is null ? null : await ControllerLab.LatestAsync(firmware.Series).ConfigureAwait(false);
        // What it holds right now, asked only when no game is running and nothing else is talking to it.
        ushort[]? held = null;
        IReadOnlyList<ControllerLab.Slot>? slots = null;
        var saved = sony ? ControllerLab.Load(card) : ControllerLab.Saved.Empty;
        if (sony && card.Connection == "usb" && !AnyGame() && _padGate.Wait(0))
        {
            try
            {
                using var pad = ControllerLab.Pad.Open(card);
                if (pad is not null)
                {
                    if (saved.Calibrated is not null) held = ControllerLab.ReadFinetune(pad);
                    slots = ControllerLab.ReadSlots(pad);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { AppLog.Warn("Controller: not read: " + ex.GetType().Name); }
            finally { _padGate.Release(); }
        }
        return new
        {
            ok = true,
            link = card.Connection,
            edge = card.ProductId == ControllerLab.DualSenseEdge,
            firmware = firmware is null ? null : new
            {
                version = ControllerLab.VersionText(firmware.Update),
                built = firmware.Built,
                latest = latest is { } l ? ControllerLab.VersionText(l) : null,
                current = latest is { } n ? firmware.Update >= n : (bool?)null,
            },
            layers = ControllerLab.ReadLayers(),
            calibration = sony ? CalibrationState(saved, held) : null,
            settings = sony ? PadSettingsView(saved.Settings) : null,
            sound = _services.Settings.Current.ControllerSound,
            // What Exo remembers follows this one controller once its serial has read; over Bluetooth it can't be read.
            remembered = card.Unit is not null,
            slots,
            tested = saved.Tested,
        };
    }

    private async Task<object> ControllerMeasureAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (AnyGame()) return new { ok = false, message = "Close the game first: a game changes what the controller sends." };
        if (ControllerCard(p, hasParams) is not { } card) return new { ok = false, message = "Exo doesn't read this controller yet." };
        var seconds = Math.Clamp(ReadDouble(p, hasParams, "seconds") ?? 3, 1, 10);
        var reports = new List<(long, uint?)>();
        await ControllerLab.ListenAsync(card, TimeSpan.FromSeconds(seconds), (at, s) => reports.Add((at, s.Sensor)), ct).ConfigureAwait(false);
        return ControllerLab.Measure(reports, Stopwatch.Frequency) is { } polling
            ? new { ok = true, polling }
            : new { ok = false, message = "The controller sent too little to measure. Move a stick and try again." };
    }

    private async Task<object> ControllerTestAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (AnyGame()) return new { ok = false, message = "Close the game first." };
        if (ControllerCard(p, hasParams) is not { } card) return new { ok = false, message = "Exo doesn't read this controller yet." };
        var rolled = ReadString(p, hasParams, "kind") == "edge";
        var seconds = Math.Clamp(ReadDouble(p, hasParams, "seconds") ?? (rolled ? 6 : 3), 1, 15);
        var left = new List<(byte, byte)>();
        var right = new List<(byte, byte)>();
        await ControllerLab.ListenAsync(card, TimeSpan.FromSeconds(seconds), (_, s) =>
        {
            left.Add((s.LX, s.LY));
            right.Add((s.RX, s.RY));
        }, ct).ConfigureAwait(false);
        object? l = rolled ? ControllerLab.Rolled(left) : ControllerLab.AtRest(left), r = rolled ? ControllerLab.Rolled(right) : ControllerLab.AtRest(right);
        if (ControllerLab.Supported(card.VendorId, card.ProductId)) KeepTest(card, rolled, l, r);
        // The numbers behind a verdict, so a "Needs calibration" can be read back from app.log.
        AppLog.Info($"Controller: stick test ({(rolled ? "edge" : "rest")}): left {l?.ToString() ?? "not measured"}, right {r?.ToString() ?? "not measured"}.");
        return new { ok = true, left = l, right = r };
    }

    /// <summary>
    /// controller.quality: the triggers pulled through their travel (each one's reach and how many values it sent), or every button pressed a few times
    /// (presses and bounces). Read only, nothing kept, and nothing while a game runs.
    /// </summary>
    private async Task<object> ControllerQualityAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (AnyGame()) return new { ok = false, message = "Close the game first." };
        if (ControllerCard(p, hasParams) is not { } card) return new { ok = false, message = "Exo doesn't read this controller yet." };
        var seconds = Math.Clamp(ReadDouble(p, hasParams, "seconds") ?? 6, 1, 15);
        if (ReadString(p, hasParams, "kind") == "triggers")
        {
            var left = new List<byte>();
            var right = new List<byte>();
            await ControllerLab.ListenAsync(card, TimeSpan.FromSeconds(seconds), (_, s) =>
            {
                left.Add(s.L2);
                right.Add(s.R2);
            }, ct).ConfigureAwait(false);
            return new { ok = true, left = ControllerLab.Swept(left), right = ControllerLab.Swept(right) };
        }
        var states = new List<(long, int)>();
        await ControllerLab.ListenAsync(card, TimeSpan.FromSeconds(seconds), (at, s) => states.Add((at, s.Buttons)), ct).ConfigureAwait(false);
        var family = ControllerLab.FamilyOf(card) ?? ControllerLab.Family.Hid;
        return ControllerLab.Bounces(states, Stopwatch.Frequency, bit => ControllerLab.ButtonName(family, bit)) is { } chatter
            ? new { ok = true, buttons = chatter }
            : new { ok = false, message = "The controller sent too little to read. Press its buttons and try again." };
    }

    /// <summary>The stick test's step, kept with the controller so its page opens with what was measured.</summary>
    private static void KeepTest(Peripherals.Peripheral card, bool rolled, object? left, object? right)
    {
        try
        {
            var saved = ControllerLab.Load(card);
            ControllerLab.Store(card, saved with { Tested = ControllerLab.TestWith(saved.Tested, DateTimeOffset.UtcNow, rolled, left as ControllerLab.Rest, right as ControllerLab.Rest, left as ControllerLab.Edge, right as ControllerLab.Edge) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Controller: stick test not kept: " + ex.GetType().Name); }
    }

    /// <summary>Starts (or stops) the live view: at most 60 states a second, for ten minutes at most, one controller at a time.</summary>
    private object ControllerWatch(JsonElement p, bool hasParams)
    {
        // The task that listens owns its token source and disposes it when it ends; here it is only cancelled.
        StopWatch(Interlocked.Exchange(ref _controllerWatch, null));
        if (ReadBool(p, hasParams, "on") != true) return new { ok = true };
        if (ControllerCard(p, hasParams) is not { } card) return new { ok = false, message = "Exo doesn't read this controller yet." };
        var watch = new CancellationTokenSource();
        StopWatch(Interlocked.Exchange(ref _controllerWatch, watch));
        var gap = Stopwatch.Frequency / 60;
        long last = 0;
        // Only what the page shows: a pad at rest sends the same state 250+ times a second (its sensor clock still ticks).
        ControllerLab.State? shown = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await ControllerLab.ListenAsync(card, TimeSpan.FromMinutes(10), (at, s) =>
                {
                    if (at - last < gap) return;
                    var seen = s with { Sensor = null, Buttons = 0 };
                    if (seen == shown) return;
                    last = at;
                    shown = seen;
                    PostEvent("controller.state", new
                    {
                        id = card.Id, lx = s.LX, ly = s.LY, rx = s.RX, ry = s.RY, l2 = s.L2, r2 = s.R2,
                        profile = s.Profile, stopL = s.StopL, stopR = s.StopR, battery = s.Battery, charging = s.Charging, extras = s.Extras,
                    });
                }, watch.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
            {
                Helpers.AppLog.Info("Controller: live view stopped: " + ex.GetType().Name);
            }
            finally
            {
                Interlocked.CompareExchange(ref _controllerWatch, null, watch);
                watch.Dispose();
            }
        });
        return new { ok = true };
    }

    private static void StopWatch(CancellationTokenSource? watch)
    {
        try { watch?.Cancel(); }
        catch (ObjectDisposedException) { /* it ended on its own a moment ago */ }
    }
}

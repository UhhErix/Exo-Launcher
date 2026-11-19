using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// What the controller's page changes on the controller (<see cref="ControllerLab"/> Pad),
/// over USB, never while a game runs or another controller program (DS4Windows, DSX, reWASD) is running, one change at
/// a time:
/// - controller.calibrate: the controller's own stick calibration. center (sticks left alone), range-begin and range-end
///   (both sticks rolled around their edge between them; Exo checks they were before it ends), range-cancel, keep (a
///   DualSense keeps it after unplugging) and undo (the values it had before Exo first calibrated it). An Edge, whose
///   sticks hold their own calibration, gets Exo's back each time it connects.
/// Before its first change to a controller Exo saves what the controller had, so Undo puts it back exactly.
/// </summary>
public sealed partial class WebHostBridge
{
    private readonly SemaphoreSlim _padGate = new(1, 1);
    private RangeRun? _range;
    private readonly HashSet<string> _reapplied = new(StringComparer.OrdinalIgnoreCase);
    // Pads (by source) a game profile changed: they get their usual settings back when the game ends, even the controller's own.
    private readonly HashSet<string> _gameOverridden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A range calibration under way: when it began, and the sticks' positions since, for the check before it ends.</summary>
    private sealed class RangeRun(string id, ushort[]? before)
    {
        internal string Id { get; } = id;
        internal ushort[]? Before { get; } = before;
        internal long Started { get; } = Stopwatch.GetTimestamp();
        internal List<(byte, byte)> Left { get; } = [];
        internal List<(byte, byte)> Right { get; } = [];
        internal CancellationTokenSource Listening { get; } = new();
        /// <summary>On an Edge, the range Exo opened wider to measure in (its own fit, not the controller's routine).</summary>
        internal ushort[]? Wide { get; init; }
    }

    /// <summary>
    /// Why Exo won't change the controller now; null when it may. <paramref name="launching"/>: a game profile on its way
    /// in, just before its game starts, which Exo already counts as running: only a game already playing stops it then.
    /// </summary>
    private string? CantChange(Peripherals.Peripheral card, bool launching = false)
    {
        if (launching ? MainWindow.RunningLibraryGames().Count > 0 : AnyGame()) return "Close the game first: Exo never changes a controller while you play.";
        if (card.Connection != "usb") return "Connect the controller with a cable first: Exo changes it over USB only.";
        var other = ControllerLab.ReadLayers().Tools.FirstOrDefault(t => t.Running && t.Name is "DS4Windows" or "DSX" or "reWASD");
        return other is null ? null : $"Close {other.Name} first: two programs changing one controller can mix up its settings.";
    }

    private async Task<object> WithPad(JsonElement p, bool hasParams, Func<Peripherals.Peripheral, ControllerLab.Pad, object> work, CancellationToken ct)
    {
        if (ControllerCard(p, hasParams) is not { } card || !ControllerLab.Supported(card.VendorId, card.ProductId))
            return new { ok = false, message = "Exo sets up a DualSense and a DualSense Edge; this controller it reads only." };
        if (CantChange(card) is { } why) return new { ok = false, message = why };
        await _padGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var pad = ControllerLab.Pad.Open(card);
            return pad is null ? new { ok = false, message = "Exo couldn't open the controller. Unplug it and plug it back in." } : work(card, pad);
        }
        finally { _padGate.Release(); }
    }

    // ------------------------------------------------------------------ calibration

    private Task<object> ControllerCalibrateAsync(JsonElement p, bool hasParams, CancellationToken ct) => HostMode.IsDisposable
        ? Task.FromResult<object>(new { ok = false, message = (string?)HostMode.Refusal }) : WithPad(p, hasParams, (card, pad) =>
    {
        var step = ReadString(p, hasParams, "step");
        var saved = ControllerLab.Load(card);
        switch (step)
        {
            case "center" or "range-begin":
            {
                // A range left open (the page went before it ended) is closed first, the values from before it put back.
                if (_range is { } open)
                {
                    StopRange(open);
                    if (open.Id == card.Id)
                    {
                        if (open.Wide is null) ControllerLab.EndRange(pad);
                        if (open.Before is { } was) ControllerLab.WriteFinetune(pad, was);
                    }
                }
                // Locked keeps a calibration only until it's unplugged. One left unlocked (Sony's app can leave it so) is
                // locked first, as dualshock-tools does on every connect, so nothing Exo does here is saved by accident.
                var nv = ControllerLab.ReadNv(pad);
                if (nv == "unlocked") nv = ControllerLab.Lock(pad);
                if (nv != "locked")
                    return new { ok = false, message = "Unplug the controller and plug it back in, then try again." };
                var now = ControllerLab.ReadFinetune(pad);
                if (now is null) return new { ok = false, message = "The controller didn't say how its sticks are set. Unplug it and plug it back in." };
                if (saved.Original is null) ControllerLab.Store(card, saved = saved with { Original = now });
                if (step == "center")
                {
                    if (!ControllerLab.CalibrateCenters(pad)) return new { ok = false, message = "The controller didn't take it. Leave the sticks alone and try again." };
                    var refined = RefineCenters(card, pad);
                    // Centers alone keep the ends the controller had: the factory's margin, or one Exo already gave them.
                    return Calibrated(card, pad, saved with { Trimmed = saved.Trimmed || saved.Calibrated is null }, refined);
                }
                // An Edge's range Exo fits itself: opened wider so no push hits the end while it measures, then each end
                // set from how far the stick really went. A DualSense runs its own routine (its scale isn't known yet).
                RangeRun run;
                if (pad.Edge)
                {
                    var wide = ControllerLab.Widened(now, 0.2, 4095);
                    if (!ControllerLab.WriteFinetune(pad, wide)) return new { ok = false, message = "The controller didn't take it. Try again." };
                    run = _range = new RangeRun(card.Id, now) { Wide = wide };
                }
                else
                {
                    if (!ControllerLab.BeginRange(pad)) return new { ok = false, message = "The controller didn't start. Try again." };
                    run = _range = new RangeRun(card.Id, now);
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ControllerLab.ListenAsync(card, TimeSpan.FromMinutes(2), (_, s) =>
                        {
                            lock (run) { run.Left.Add((s.LX, s.LY)); run.Right.Add((s.RX, s.RY)); }
                        }, run.Listening.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException) { }
                });
                return new { ok = true };
            }
            case "range-end":
            {
                if (_range is not { } run || run.Id != card.Id) return new { ok = false, message = "Start the range first." };
                var seconds = Stopwatch.GetElapsedTime(run.Started).TotalSeconds;
                ControllerLab.Edge? left, right;
                lock (run) { left = ControllerLab.Rolled(run.Left); right = ControllerLab.Rolled(run.Right); }
                // Ending before both sticks went all the way round would pin them to the corners: say so instead.
                if (seconds < 8 || left is not { Covered: >= 0.9 } || right is not { Covered: >= 0.9 })
                    return new { ok = false, more = true, message = "Keep rolling both sticks slowly around their edge.", left = left?.Covered ?? 0, right = right?.Covered ?? 0 };
                StopRange(run);
                if (run.Wide is { } opened) return FitRange(card, pad, saved, run, opened);
                if (!ControllerLab.EndRange(pad)) return new { ok = false, message = "The controller didn't take it. Unplug it and plug it back in: it goes back to how it was." };
                // The ends it recorded are the hardest push: brought in a little, so a normal push reaches full output.
                ushort[]? wrote = null;
                if (ControllerLab.ReadSettled(pad) is { } ended && ControllerLab.WithMargin(ended) is { } trimmed && ControllerLab.WriteFinetune(pad, trimmed))
                {
                    saved = saved with { Trimmed = true };
                    wrote = trimmed;
                }
                return Calibrated(card, pad, saved, wrote);
            }
            case "range-cancel":
            {
                if (_range is not { } run || run.Id != card.Id) return new { ok = true };
                StopRange(run);
                // The range can't be left open: end it, then put back the values from before it began.
                if (run.Wide is null) ControllerLab.EndRange(pad);
                if (run.Before is { } before) ControllerLab.WriteFinetune(pad, before);
                return new { ok = true };
            }
            case "keep":
            {
                if (pad.Edge) return new { ok = false, message = "An Edge's sticks keep their own calibration; Exo puts it back each time it connects instead." };
                if (saved.Calibrated is null) return new { ok = false, message = "Calibrate it first." };
                if (Charge(card) is < 30) return new { ok = false, message = "Charge the controller to 30% first: if the cable came out while it saves with a flat battery, the controller could stop working." };
                if (!ControllerLab.Keep(pad)) return new { ok = false, message = "The controller didn't keep it. It still has it until you unplug it." };
                ControllerLab.Store(card, saved with { Kept = true });
                AppLog.Info("Controller: calibration kept on the DualSense.");
                return new { ok = true };
            }
            case "undo":
            {
                if (saved.Original is not { } original) return new { ok = true };
                if (!ControllerLab.WriteFinetune(pad, original)) return new { ok = false, message = "The controller didn't take it. Try again." };
                if (saved.Kept && !ControllerLab.Keep(pad)) return new { ok = false, message = "The controller has its old calibration until you unplug it, but didn't keep it. Try again." };
                ControllerLab.Store(card, saved with { Calibrated = null, Kept = false, Trimmed = false });
                AppLog.Info("Controller: calibration put back as it was.");
                return new { ok = true };
            }
            default:
                return new { ok = false, message = "Not a calibration step." };
        }
    }, ct);

    /// <summary>
    /// Exo's pass after the controller's own center routine: each stick measured at rest, the fine-tune centers moved by
    /// what Exo found, and measured again, up to three times, until every axis rests within half an 8-bit step. Returns the
    /// values it last wrote, if it wrote any.
    /// </summary>
    private static ushort[]? RefineCenters(Peripherals.Peripheral card, ControllerLab.Pad pad)
    {
        ushort[]? wrote = null;
        for (var round = 0; round < 3; round++)
        {
            Thread.Sleep(300);
            var left = new List<(byte, byte)>();
            var right = new List<(byte, byte)>();
            ControllerLab.ListenAsync(card, TimeSpan.FromSeconds(2), (_, s) => { left.Add((s.LX, s.LY)); right.Add((s.RX, s.RY)); }, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (ControllerLab.AtRest(left) is not { } l || ControllerLab.AtRest(right) is not { } r) return wrote;
            double[] offsets = [l.OffsetX, l.OffsetY, r.OffsetX, r.OffsetY];
            if (offsets.All(o => Math.Abs(o) <= ControllerLab.CenterTolerance)) return wrote;
            if (ControllerLab.ReadSettled(pad) is not { } values) return wrote;
            var next = ControllerLab.Recentered(values, offsets);
            if (!ControllerLab.WriteFinetune(pad, next)) return wrote;
            wrote = next;
            AppLog.Info($"Controller: centers refined (round {round + 1}, off by up to {offsets.Max(Math.Abs):0.0}%).");
        }
        return wrote;
    }

    /// <summary>
    /// An Edge's range from Exo's own measurement: how far each stick went toward each end while rolled on the widened
    /// range, each end set just inside that. A reach that hit the end, or never got past half, puts back what it had.
    /// </summary>
    private static object FitRange(Peripherals.Peripheral card, ControllerLab.Pad pad, ControllerLab.Saved saved, RangeRun run, ushort[] opened)
    {
        (byte, byte)[] left, right;
        lock (run) { left = [.. run.Left]; right = [.. run.Right]; }
        if (ControllerLab.Reaches(left, right) is not { } reach)
        {
            if (run.Before is { } before) ControllerLab.WriteFinetune(pad, before);
            return new { ok = false, message = "Exo couldn't measure how far the sticks go. Try again, rolling both firmly all the way round." };
        }
        var fitted = ControllerLab.Fitted(opened, reach.Low, reach.High, ControllerLab.EdgeMargin);
        if (!ControllerLab.WriteFinetune(pad, fitted))
        {
            if (run.Before is { } before) ControllerLab.WriteFinetune(pad, before);
            return new { ok = false, message = "The controller didn't take it. Try again." };
        }
        AppLog.Info("Controller: the Edge's range fitted to how far its sticks go.");
        return Calibrated(card, pad, saved with { Trimmed = true }, fitted);
    }

    /// <summary>
    /// After a calibration: what it left, kept for Undo and, on an Edge, put back each time it connects. What the controller
    /// says it holds, else what Exo just wrote (<paramref name="wrote"/>), and the log says which, and whether they differ.
    /// </summary>
    private static object Calibrated(Peripherals.Peripheral card, ControllerLab.Pad pad, ControllerLab.Saved saved, ushort[]? wrote = null)
    {
        var values = ControllerLab.ReadSettled(pad);
        if (values is null) AppLog.Warn("Controller: it didn't read back after the calibration; Exo keeps what it wrote.");
        else if (wrote is not null && !values.SequenceEqual(wrote)) AppLog.Warn("Controller: it reads back other values than Exo wrote; Exo keeps what it says it holds.");
        ControllerLab.Store(card, saved with { Calibrated = ControllerLab.ToKeep(values, wrote, saved.Calibrated) });
        AppLog.Info("Controller: sticks calibrated (" + (pad.Edge ? "Edge, put back on each connect" : "until unplugged") + ").");
        return new { ok = true };
    }

    private void StopRange(RangeRun run)
    {
        run.Listening.Cancel();
        if (ReferenceEquals(_range, run)) _range = null;
    }

    /// <summary>The controller's charge from one input report, %; null when it doesn't say in time.</summary>
    private static int? Charge(Peripherals.Peripheral card)
    {
        int? charge = null;
        using var stop = new CancellationTokenSource();
        try
        {
            ControllerLab.ListenAsync(card, TimeSpan.FromSeconds(1), (_, s) =>
            {
                if (s.Battery is { } b) { charge = b; stop.Cancel(); }
            }, stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        return charge;
    }

    /// <summary>
    /// What the page shows about calibration: whether Exo calibrated it, whether a DualSense kept it, and, when the
    /// controller was asked, whether it holds Exo's calibration right now (null: not asked or no answer).
    /// </summary>
    private static object CalibrationState(ControllerLab.Saved saved, ushort[]? now) => new
    {
        calibrated = saved.Calibrated is not null,
        kept = saved.Kept,
        canUndo = saved.Original is not null && saved.Calibrated is not null,
        holds = saved.Calibrated is { } mine && now is not null ? now.SequenceEqual(mine) : (bool?)null,
        putBack = saved.PutBack,
    };

    private void ReapplyCalibrationsAfter(Models.GameEntry _) => ReapplyCalibrationsSoon(afterGame: true);

    /// <summary>
    /// Until the library is read, a game already running (Exo started or restarted mid-game) isn't seen, and a controller
    /// would be written during it: this waits for the library, at most a minute (a library with no games stays empty).
    /// </summary>
    private async Task LibraryKnownAsync()
    {
        if (_services.Library.PeekCachedLibrary().Count > 0) return;
        var known = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Read() => known.TrySetResult();
        _services.Library.LibraryUpdated += Read;
        try
        {
            if (_services.Library.PeekCachedLibrary().Count == 0)
                await Task.WhenAny(known.Task, Task.Delay(TimeSpan.FromMinutes(1), _bridgeLifetime.Token)).ConfigureAwait(false);
        }
        finally
        {
            _services.Library.LibraryUpdated -= Read;
        }
    }

    /// <summary>
    /// What a controller forgets when it disconnects, put back: an Edge Exo calibrated gets its calibration (its stick
    /// modules keep their own, so Exo's lasts only until it's unplugged) once per connection, and a DualSense or Edge its
    /// light, sound and vibration settings then and after each game (a game may set its own light). A controller plugged in
    /// while a game runs gets them a few seconds in (<see cref="SendSettingsMidGameAsync"/>). A controller whose serial
    /// hasn't read yet, that wouldn't open or didn't take a write is tried again, five seconds apart, up to five times: Exo
    /// only knows what's saved for it once it knows which controller it is. The calibration is read back to confirm it.
    /// </summary>
    private void ReapplyCalibrationsSoon(bool afterGame = false, int tries = 0) => _ = Task.Run(async () =>
    {
        if (HostMode.IsDisposable) return;
        var again = false;
        await LibraryKnownAsync().ConfigureAwait(false);
        if (AnyGame())
        {
            if (!afterGame) await SendSettingsMidGameAsync().ConfigureAwait(false);
            return;
        }
        await _padGate.WaitAsync(_bridgeLifetime.Token).ConfigureAwait(false);
        try
        {
            var pads = Peripherals.List(AnyGame).Where(c => c is { Connection: "usb" } && ControllerLab.Supported(c.VendorId, c.ProductId) && c.Source is not null).ToList();
            lock (_reapplied) _reapplied.IntersectWith(pads.Select(c => c.Source!));
            foreach (var card in pads)
            {
                bool fresh;
                lock (_reapplied) fresh = !_reapplied.Contains(card.Source!);
                if (!fresh && !afterGame || CantChange(card) is not null) continue;
                // A DualSense too old to say its serial runs out its tries, then goes by its port's file.
                if (card.Unit is null && tries < ReapplyTries) { again = true; continue; }
                if (fresh) ControllerLab.Adopt(card);
                var saved = ControllerLab.Load(card);
                var values = fresh && card.ProductId == ControllerLab.DualSenseEdge && saved is { Calibrated: { } c } ? c : null;
                bool overridden;
                lock (_gameOverridden) overridden = afterGame && _gameOverridden.Remove(card.Source!);
                // After a game profile the usual settings go back, the controller's own included. A light left to games
                // is lit again; its color stays the last one sent until a game sets its own.
                var settings = saved.Settings is { IsDefault: false } s ? s : overridden ? new ControllerLab.PadSettings() : null;
                if (overridden && settings is { Light: null }) settings = settings with { Light = "on" };
                if (values is null && settings is null) { lock (_reapplied) _reapplied.Add(card.Source!); continue; }
                // A range calibrated before the margin came in gets it now, once.
                if (values is not null && !saved.Trimmed && ControllerLab.RangeMoved(values, saved.Original) && ControllerLab.WithMargin(values) is { } trimmed)
                {
                    ControllerLab.Store(card, saved with { Calibrated = trimmed, Trimmed = true });
                    values = trimmed;
                    AppLog.Info("Controller: the Edge's range brought in from its edge.");
                }
                using var pad = ControllerLab.Pad.Open(card);
                if (pad is null) { again = true; continue; }
                if (values is not null && !ControllerLab.WriteFinetune(pad, values)) { again = true; continue; }
                if (values is not null)
                {
                    var held = ControllerLab.ReadSettled(pad);
                    AppLog.Info(held?.SequenceEqual(values) == true ? "Controller: the Edge's calibration put back, and it reads back the same."
                        : held is null ? "Controller: the Edge's calibration put back; it didn't read back." : "Controller: the Edge's calibration put back, but it reads back other values.");
                }
                if (settings is not null && pad.Output(ControllerLab.SettingsReport(settings, pad.OutputLength))) AppLog.Info("Controller: its light, sound and vibration settings put back.");
                ControllerLab.Store(card, ControllerLab.Load(card) with { PutBack = DateTimeOffset.UtcNow });
                lock (_reapplied) _reapplied.Add(card.Source!);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { AppLog.Warn("Controller: settings not put back: " + ex.Message); }
        finally { _padGate.Release(); }
        if (!again || tries >= ReapplyTries) return;
        await Task.Delay(TimeSpan.FromSeconds(5), _bridgeLifetime.Token).ConfigureAwait(false);
        ReapplyCalibrationsSoon(afterGame, tries + 1);
    });

    private const int ReapplyTries = 5;

    /// <summary>
    /// A DualSense or Edge plugged in while a game plays: the game lights it its own way as it finds it (blue, player
    /// one), so a few seconds in, after the game's own, it gets what it gets on any connection: an Edge Exo calibrated its
    /// calibration (nobody is using it yet, so nothing jumps), and its light, sound and vibration settings with the running
    /// game's profile over them. The settings report leaves the game's rumble and trigger effects alone.
    /// </summary>
    private async Task SendSettingsMidGameAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3), _bridgeLifetime.Token).ConfigureAwait(false);
        var profile = ControllerLab.ProfileFor(ControllerLab.LoadGameProfiles(), MainWindow.RunningLibraryGames().Keys);
        await _padGate.WaitAsync(_bridgeLifetime.Token).ConfigureAwait(false);
        try
        {
            var pads = Peripherals.List(AnyGame).Where(c => c is { Connection: "usb" } && ControllerLab.Supported(c.VendorId, c.ProductId) && c.Source is not null).ToList();
            lock (_reapplied) _reapplied.IntersectWith(pads.Select(c => c.Source!));
            foreach (var card in pads)
            {
                bool fresh;
                lock (_reapplied) fresh = !_reapplied.Contains(card.Source!);
                if (!fresh) continue;
                // Its serial isn't asked during a game, so what's saved for it isn't known yet: it all goes on when the game ends.
                if (card.Unit is null) continue;
                if (ControllerLab.ReadLayers().Tools.Any(t => t.Running && t.Name is "DS4Windows" or "DSX" or "reWASD")) return;
                ControllerLab.Adopt(card);
                var saved = ControllerLab.Load(card);
                var values = card.ProductId == ControllerLab.DualSenseEdge && saved is { Calibrated: { } c } ? c : null;
                var settings = profile is not null ? ControllerLab.Merge(saved.Settings, profile.Settings) : saved.Settings is { IsDefault: false } s ? s : null;
                if (values is null && settings is null) { lock (_reapplied) _reapplied.Add(card.Source!); continue; }
                using var pad = ControllerLab.Pad.Open(card);
                if (pad is null) continue;
                if (values is not null && !ControllerLab.WriteFinetune(pad, values)) continue;
                if (values is not null) AppLog.Info("Controller: plugged in mid-game; the Edge's calibration put back.");
                if (settings is not null && pad.Output(ControllerLab.SettingsReport(settings, pad.OutputLength)))
                    AppLog.Info(profile is not null
                        ? $"Controller: plugged in mid-game; game profile \"{profile.Name}\" sent."
                        : "Controller: plugged in mid-game; its light, sound and vibration settings sent.");
                if (profile is not null) lock (_gameOverridden) _gameOverridden.Add(card.Source!);
                lock (_reapplied) _reapplied.Add(card.Source!);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { AppLog.Warn("Controller: settings not sent mid-game: " + ex.Message); }
        finally { _padGate.Release(); }
    }

    // ------------------------------------------------------------------ light, sound and vibration

    private static readonly JsonSerializerOptions PadSettingsJson = new(JsonSerializerDefaults.Web);

    /// <summary>controller.settings: the controller's light, sound and vibration, saved and sent now, and again each time it connects.</summary>
    private Task<object> ControllerSettingsAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (HostMode.IsDisposable) return Task.FromResult<object>(new { ok = false, message = (string?)HostMode.Refusal });
        ControllerLab.PadSettings? settings = null;
        try
        {
            if (hasParams && p.TryGetProperty("settings", out var el) && el.ValueKind == JsonValueKind.Object)
                settings = el.Deserialize<ControllerLab.PadSettings>(PadSettingsJson);
        }
        catch (JsonException) { }
        if (settings is null || !ControllerLab.Valid(settings) || settings.Light == "on") return Task.FromResult<object>(new { ok = false, message = "Not settings Exo knows." });
        return WithPad(p, hasParams, (card, pad) =>
        {
            var saved = ControllerLab.Load(card);
            // Back to games' light from Exo's: the bar is turned on again, its color left to them.
            var send = settings.Light is null && saved.Settings?.Light is not null ? settings with { Light = "on" } : settings;
            if (!pad.Output(ControllerLab.SettingsReport(send, pad.OutputLength))) return new { ok = false, message = "The controller didn't take it. Try again." };
            ControllerLab.Store(card, saved with { Settings = settings.IsDefault ? null : settings });
            AppLog.Info("Controller: light, sound and vibration settings sent.");
            return new { ok = true, settings = PadSettingsView(settings) };
        }, ct);
    }

    private static object PadSettingsView(ControllerLab.PadSettings? s)
    {
        var v = s ?? new();
        return new { light = v.Light, playerLight = v.PlayerLight, micOff = v.MicOff, speakerOff = v.SpeakerOff, vibration = v.Vibration, triggerEffects = v.TriggerEffects };
    }

    /// <summary>controller.sound: every controller's speaker and microphone in Windows on, or off (Exo's default).</summary>
    private async Task<object> ControllerSoundAsync(JsonElement p, bool hasParams)
    {
        var on = ReadBool(p, hasParams, "on") == true;
        if (on != _services.Settings.Current.ControllerSound) _services.Settings.UpdateProfile(s => s.ControllerSound = on);
        var changed = await Task.Run(() => Peripherals.SetControllerSound(on)).ConfigureAwait(true);
        return new { ok = true, on, changed };
    }

    /// <summary>Controllers' speakers and microphones off in Windows (unless the page turned them on): at start and whenever a device comes or goes.</summary>
    private void HideControllerSoundSoon() => _ = Task.Run(() =>
    {
        if (HostMode.IsDisposable) return;
        if (!_services.Settings.Current.ControllerSound) Peripherals.SetControllerSound(false);
    });

    // ------------------------------------------------------------------ game profiles

    /// <summary>controller.gameProfiles: the game profiles Exo keeps (<see cref="ControllerLab.GameProfile"/>).</summary>
    private static object ControllerGameProfiles() => new { ok = true, profiles = ControllerLab.LoadGameProfiles() };

    /// <summary>controller.gameProfilesSave: every game profile, as the page has them now. A game belongs to one profile.</summary>
    private static object ControllerGameProfilesSave(JsonElement p, bool hasParams)
    {
        List<ControllerLab.GameProfile>? profiles = null;
        try
        {
            if (hasParams && p.TryGetProperty("profiles", out var el) && el.ValueKind == JsonValueKind.Array)
                profiles = el.Deserialize<List<ControllerLab.GameProfile>>(PadSettingsJson);
        }
        catch (JsonException) { }
        if (profiles is null || profiles.Count > 100 || !profiles.All(ControllerLab.Valid) || profiles.Select(x => x.Id).Distinct().Count() != profiles.Count)
            return new { ok = false, message = "Not game profiles Exo knows." };
        // A game linked twice keeps the last profile: the one a game start would use anyway.
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles) foreach (var game in profile.Games) owner[game] = profile.Id;
        profiles = [.. profiles.Select(x => x with { Name = x.Name.Trim(), Games = [.. x.Games.Distinct(StringComparer.OrdinalIgnoreCase).Where(g => owner[g] == x.Id)] })];
        try { ControllerLab.StoreGameProfiles(profiles); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Controller: game profiles not saved: {ex.GetType().Name}");
            return new { ok = false, message = "Exo couldn't save them. Try again." };
        }
        return new { ok = true, profiles };
    }

    /// <summary>
    /// Just before a game starts (<see cref="LaunchOrchestrator.GameLaunching"/>): its profile, merged over each connected
    /// DualSense's or Edge's usual settings, sent to each over USB. Never when another game already plays; the usual
    /// settings go back when the game ends (<see cref="ReapplyCalibrationsSoon"/>).
    /// </summary>
    private void ApplyGameProfile(Models.GameEntry game)
    {
        if (HostMode.IsDisposable) return;
        if (ControllerLab.ProfileFor(ControllerLab.LoadGameProfiles(), [game.Id, .. game.Variants.Select(v => v.Id)]) is not { } profile) return;
        if (!_padGate.Wait(TimeSpan.FromSeconds(2))) return;
        try
        {
            foreach (var card in Peripherals.List(AnyGame).Where(c => c is { Connection: "usb" } && ControllerLab.Supported(c.VendorId, c.ProductId) && c.Source is not null))
            {
                if (CantChange(card, launching: true) is not null) continue;
                using var pad = ControllerLab.Pad.Open(card);
                if (pad is null) continue;
                var settings = ControllerLab.Merge(ControllerLab.Load(card).Settings, profile.Settings);
                if (!pad.Output(ControllerLab.SettingsReport(settings, pad.OutputLength))) continue;
                lock (_gameOverridden) _gameOverridden.Add(card.Source!);
                AppLog.Info($"Controller: game profile \"{profile.Name}\" sent for {game.Title}.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { AppLog.Warn("Controller: game profile not sent: " + ex.Message); }
        finally { _padGate.Release(); }
    }
}

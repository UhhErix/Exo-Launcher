using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>What can be added after "142 FPS", in the order it shows. Each is off until switched on, and nothing is read for one that's off.</summary>
internal static class FpsItems
{
    internal const string Low = "low", FrameTime = "frametime", GpuUse = "gpu", GpuTemperature = "gputemp", CpuUse = "cpu", Vram = "vram", Ram = "ram", Battery = "battery", Clock = "clock";
    private static readonly string[] Order = [Low, FrameTime, GpuUse, GpuTemperature, CpuUse, Vram, Ram, Battery, Clock];

    /// <param name="Battery">The wireless device lowest on battery: its kind ("MOUSE", "HEADSET", "CONTROLLER") and percent.</param>
    internal readonly record struct Sample(int? GpuUse, int? GpuTemperature, int? CpuUse, double? VramGb, double? RamGb, (string Kind, int Percent)? Battery = null);

    /// <summary>The kind shown for a device's battery, by the Devices card's kind; a kind Exo doesn't name shows as "DEVICE".</summary>
    internal static string BatteryKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "mouse" => "MOUSE",
        "headset" or "headphones" or "audio" or "output" => "HEADSET",
        "controller" or "gamepad" => "CONTROLLER",
        "keyboard" => "KEYBOARD",
        _ => "DEVICE",
    };

    /// <summary>Only names Exo knows, once each, in their order, whatever a settings file or the page held.</summary>
    internal static List<string> Clean(IEnumerable<string>? items) => items is null ? [] : [.. Order.Where(items.Contains)];

    internal static bool NeedsGpu(IReadOnlyList<string> items) => items.Contains(GpuUse) || items.Contains(GpuTemperature) || items.Contains(Vram);

    /// <summary>The line after the FPS, each item with its short label; an item Windows gave no value for is left out, never guessed. Null with none.</summary>
    internal static string? Text(IReadOnlyList<string> items, int fps, int? low, Sample sample, DateTime now)
    {
        var text = "";
        void Add(string item) => text += " · " + item;
        foreach (var item in items)
        {
            switch (item)
            {
                case Low when low is { } value: Add(Number(value) + " LOW"); break;
                case FrameTime when fps > 0: Add((1000.0 / fps).ToString("0.0", CultureInfo.InvariantCulture) + " MS"); break;
                case GpuUse when sample.GpuUse is { } value: Add(Number(value) + "% GPU"); break;
                case GpuTemperature when sample.GpuTemperature is { } value: Add(Number(value) + "°C"); break;
                case CpuUse when sample.CpuUse is { } value: Add(Number(value) + "% CPU"); break;
                case Vram when sample.VramGb is { } value: Add(value.ToString("0.0", CultureInfo.InvariantCulture) + " VRAM"); break;
                case Ram when sample.RamGb is { } value: Add(value.ToString("0.0", CultureInfo.InvariantCulture) + " RAM"); break;
                case Battery when sample.Battery is { } battery: Add(Number(battery.Percent) + "% " + battery.Kind); break;
                case Clock: Add(now.ToString("t", CultureInfo.CurrentCulture)); break;
            }
        }
        return text.Length == 0 ? null : text;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Settings' FPS counter: while it is on and a game runs, <see cref="PresentTrace"/> counts the game's frames and
/// <see cref="FpsOverlay"/> shows them over it. With no game, or with it off, neither exists. While the game isn't in
/// front nothing is sent, read or drawn: the overlay waits for Windows to say the front changed. The game is whichever
/// the run-state poll verified (<see cref="Follow"/>, every 5 s: the game's process tree, so a launcher handing off is
/// followed), and a game Exo only started to compile its shaders isn't one. Items beyond the FPS are read once a second
/// and only while switched on with the game in front (<see cref="Sampler"/>).
///
/// A trace needs administrator rights or its own permission. Where Exo already has them (UAC off, Exo run as administrator,
/// Performance Log Users) nothing is asked. Otherwise turning it on gives this user the rights on this one session and its
/// providers (<see cref="PresentTrace.AddRightsCommand"/>): that press and Windows' own prompt are the consent, and it
/// works at once. Only if Windows still says no does it fall back on the group, which waits for the next sign-in.
/// Exo itself is never elevated.
/// </summary>
/// <summary>
/// Games that draw an FPS counter of their own (a setting in the game). Exo's counter would be a second overlay, and its trace
/// costs a little: over these it stays off while it would show only the FPS, which theirs shows too. With items added
/// (frame time, 1% lows, GPU, CPU, memory) it adds what theirs can't, and runs.
/// </summary>
internal static class BuiltInFps
{
    private static readonly string[] Titles =
    [
        "valorant", "fortnite", "apex legends", "counter-strike", "call of duty", "warzone", "black ops", "modern warfare",
        "rocket league", "overwatch", "league of legends", "dota 2", "rainbow six", "destiny 2", "marvel rivals", "minecraft",
        "the finals", "pubg", "battlefield", "cyberpunk 2077", "forza horizon", "the witcher 3", "red dead redemption", "gta v",
        "grand theft auto v", "halo infinite", "sea of thieves", "world of warcraft", "path of exile", "deadlock", "street fighter 6",
        "tekken 8", "mortal kombat", "doom", "hitman", "borderlands 3", "assassin's creed", "far cry",
    ];

    internal static bool Has(GameEntry game) =>
        game.Title is { Length: > 0 } title && Titles.Any(known => title.Contains(known, StringComparison.OrdinalIgnoreCase));
}

internal static class FpsCounter
{
    private static readonly Lock Gate = new();

    private static volatile PresentTrace? _trace;
    private static volatile HashSet<int> _tree = [];
    private static volatile string _position = "right";
    private static volatile List<string> _items = [];
    private static FpsOverlay? _overlay;
    private static Sampler? _sampler;
    private static bool _refused;

    /// <summary>When to look again: twice a second over the game; slowly under exclusive full screen, which no window can show over and which sends no word when it ends; away from the game, not until the front changes.</summary>
    internal static uint LookAfter(bool frontIsGame, bool exclusiveFullScreen) => !frontIsGame ? 0u : exclusiveFullScreen ? 2000u : 500u;

    /// <summary>What a followed game's frames are wanted for: the overlay, and the session recorder (which needs no overlay).</summary>
    internal readonly record struct Wanted(bool Overlay, bool Recorder)
    {
        internal bool Trace => Overlay || Recorder;
    }

    /// <summary>
    /// The overlay when its switch is on, except over a game that shows its own FPS and has no extra items; the recorder for every
    /// game it records (<see cref="SessionRecorderHost.Records"/>), whatever the overlay does. The trace runs for either.
    /// </summary>
    internal static Wanted Wants(bool overlaySetting, bool builtInFpsWithNoItems, bool recordsThisGame) => new(overlaySetting && !builtInFpsWithNoItems, recordsThisGame);

    /// <summary>A refused start is asked for again after this long: the rights may have been given since (by turning the counter on).</summary>
    private const long RetryRightsAfterMs = 10 * 60 * 1000;

    private static long _refusedAt;

    /// <summary>At Exo's start: a session a crash left running goes.</summary>
    internal static void Start()
    {
        // The recorder's session needs no switch, so a crash's leftover goes whatever the switch says.
        _ = Task.Run(PresentTrace.StopStale);
    }

    /// <summary>From the run-state poll, off the window's thread: the games verified running, staged ones already left out.</summary>
    internal static void Follow(LaunchOrchestrator launcher, IEnumerable<GameEntry> running)
    {
        var settings = App.Services.Settings.Current;
        var games = running.ToArray();
        lock (Gate)
        {
            if (games.Length == 0)
            {
                EndGame();
                return;
            }
            // A second game runs: the one in front, else the first.
            var front = (int)ForegroundWatcher.ForegroundProcess();
            var trees = games.Select(game => (game, tree: launcher.GameProcesses(game))).ToArray();
            var (followed, tree) = Array.Find(trees, entry => entry.tree.Contains(front));
            if (followed is null) (followed, tree) = trees[0];
            var wanted = Wants(settings.FpsCounter, BuiltInFps.Has(followed) && FpsItems.Clean(settings.FpsCounterItems).Count == 0, SessionRecorderHost.Records(followed));
            if (!wanted.Trace)
            {
                // The game shows its own FPS and ours would add nothing to it, or the switch is off and nothing records it: no trace.
                EndGame();
                return;
            }
            if (_trace is null)
            {
                if (_refused && Environment.TickCount64 - _refusedAt < RetryRightsAfterMs)
                {
                    if (wanted.Recorder) SessionRecorderHost.Shared.NoRights(followed, tree);
                    return;
                }
                _trace = PresentTrace.Start(out var error);
                if (_trace is null)
                {
                    // The same answer until something changes it, which the switch notices, or ten minutes.
                    _refused = true;
                    _refusedAt = Environment.TickCount64;
                    AppLog.Info($"FPS counter: Windows wouldn't start the trace ({error}).");
                    if (wanted.Recorder) SessionRecorderHost.Shared.NoRights(followed, tree);
                    return;
                }
                _refused = false;
                AppLog.Info($"FPS counter: counting {followed.Title}.");
            }
            _trace.Watch(tree);
            var changed = !_tree.SetEquals(tree);
            if (changed) _tree = [.. tree];
            Remember(settings);
            if (!wanted.Overlay) CloseOverlay();
            else if (_overlay is null) _overlay = new FpsOverlay(NextLook, () => _position);
            else if (changed) _overlay.Refresh();
            if (wanted.Recorder) SessionRecorderHost.Shared.Follow(_trace, followed, tree);
            else SessionRecorderHost.Shared.End(_trace);
        }
    }

    /// <summary>
    /// The bridge is going (Exo is closing, or the page was rebuilt): no trace stays behind it, and a game session being recorded
    /// is saved. The recorder is not stopped: the next look after a rebuild records again (<see cref="Shutdown"/> is Exo's end).
    /// </summary>
    internal static void Stop()
    {
        lock (Gate)
        {
            EndGame();
            CloseOverlayIfIdle();
        }
    }

    /// <summary>Exo is really closing: <see cref="Stop"/>, and the recorder takes no more.</summary>
    internal static void Shutdown()
    {
        Stop();
        SessionRecorderHost.Shared.Exit();
    }

    /// <summary>The switch went off: the overlay goes now. The trace goes at the next look unless the recorder still wants it.</summary>
    private static void OverlayOff()
    {
        lock (Gate) CloseOverlay();
    }

    /// <summary>The setting's own state. Off; on and able to count (<c>on</c>); or on and waiting for the next sign-in (<c>signout</c>).</summary>
    internal static (bool On, string State, string? Message) Status()
    {
        if (!App.Services.Settings.Current.FpsCounter) return (false, "off", null);
        lock (Gate)
        {
            var error = _trace is not null ? PresentTrace.Ok : PresentTrace.Probe();
            if (error == PresentTrace.Ok) _refused = false;
            if (error != PresentTrace.AccessDenied) return (true, "on", null);
        }
        if (WindowsIdentity.GetCurrent().User?.Value is { } user && PresentTrace.InPerformanceLogUsers(user)) return (true, "signout", null);
        // Its permission went: it asks again next time it is turned on.
        App.Services.Settings.UpdateProfile(settings => settings.FpsCounter = false);
        return (false, "off", null);
    }

    /// <summary>The switch. It just works where Windows lets Exo count. Elsewhere Windows' prompt shows once; declined, it stays off and says so.</summary>
    internal static (bool On, string State, string? Message) Set(bool on)
    {
        if (!on)
        {
            App.Services.Settings.UpdateProfile(settings => settings.FpsCounter = false);
            OverlayOff();
            return (false, "off", null);
        }
        var error = TryNow();
        if (error == PresentTrace.Ok) return TurnOn("on");
        if (error != PresentTrace.AccessDenied)
        {
            AppLog.Warn($"FPS counter: Windows wouldn't start a trace ({error}).");
            return (false, "off", "Windows wouldn't start the counter.");
        }
        var user = WindowsIdentity.GetCurrent().User?.Value;
        if (user is null || PresentTrace.AddRightsCommand(user) is not { } rights || PresentTrace.AddToGroupCommand(user) is not { } group)
            return (false, "off", "Windows didn't say who you are.");
        if (PresentTrace.InPerformanceLogUsers(user)) return TurnOn("signout");
        if (ElevatedCommands.Run([rights], "FPS counter") is { } refused) return (false, "off", refused);
        if (TryNow() == PresentTrace.Ok)
        {
            AppLog.Info("FPS counter: this user may now run its trace.");
            return TurnOn("on");
        }
        AppLog.Warn("FPS counter: the rights didn't take; trying Performance Log Users.");
        if (ElevatedCommands.Run([group], "FPS counter") is { } declined) return (false, "off", declined);
        if (!PresentTrace.InPerformanceLogUsers(user)) return (false, "off", "Windows didn't make the change.");
        return TurnOn("signout");
    }

    private static int TryNow()
    {
        lock (Gate) return _trace is not null ? PresentTrace.Ok : PresentTrace.Probe();
    }

    private static (bool, string, string?) TurnOn(string state)
    {
        App.Services.Settings.UpdateProfile(settings => settings.FpsCounter = true);
        if (state == "on") lock (Gate) _refused = false;
        return (true, state, null);
    }

    /// <summary>The position or the items changed: a showing counter follows now.</summary>
    internal static void SettingsChanged()
    {
        lock (Gate)
        {
            Remember(App.Services.Settings.Current);
            _overlay?.Refresh();
        }
    }

    private static void Remember(AppSettings settings)
    {
        _position = FpsOverlay.PositionOf(settings.FpsCounterPosition);
        _items = FpsItems.Clean(settings.FpsCounterItems);
    }

    /// <summary>On the overlay's thread; it takes no lock, since closing the overlay waits for this thread.</summary>
    private static FpsOverlay.Look NextLook()
    {
        var items = _items;
        if (_trace is not { } trace) return new(null, 0);
        var front = (int)ForegroundWatcher.ForegroundProcess();
        var frontIsGame = _tree.Contains(front);
        var exclusive = frontIsGame && ExclusiveFullScreen();
        var next = LookAfter(frontIsGame, exclusive);
        if (!frontIsGame || exclusive)
        {
            trace.Run(PresentTrace.ForOverlay, false);
            _sampler?.Dispose();
            _sampler = null;
            return new(null, next);
        }
        trace.Run(PresentTrace.ForOverlay, true);
        var reading = trace.Read(front, items.Contains(FpsItems.Low));
        if (!reading.Seen) return new(null, next);
        if (items.Count == 0) return new(new(reading.Fps, null), next);
        var sample = (_sampler ??= new Sampler()).Next(items);
        return new(new(reading.Fps, FpsItems.Text(items, reading.Fps, reading.Low, sample, DateTime.Now)), next);
    }

    /// <summary>A game holds the screen in exclusive full screen, which no window can show over.</summary>
    private static bool ExclusiveFullScreen() => SHQueryUserNotificationState(out var state) == 0 && state == 3; // QUNS_RUNNING_D3D_FULL_SCREEN

    private static void EndGame()
    {
        // The session recorder reads the last frames and saves before the trace goes.
        SessionRecorderHost.Shared.End(_trace);
        if (_trace is not { } trace) return;
        _trace = null;
        // The overlay first: its thread reads the trace, and it closes the sampler with the game gone.
        CloseOverlayIfIdle();
        trace.Dispose();
        _tree = [];
    }

    private static void CloseOverlayIfIdle()
    {
        if (_trace is not null) return;
        CloseOverlay();
    }

    /// <summary>The overlay and what it reads go; the overlay's thread reads the trace, so it ends first, and the trace stops sending for it.</summary>
    private static void CloseOverlay()
    {
        _overlay?.Dispose();
        _overlay = null;
        _sampler?.Dispose();
        _sampler = null;
        _trace?.Run(PresentTrace.ForOverlay, false);
    }

    /// <summary>What the switched-on items read, at most once a second, opening the graphics card only for an item that needs it.</summary>
    private sealed class Sampler : IDisposable
    {
        private const long EveryMs = 1000;
        private long _next;
        private FpsItems.Sample _last;
        private GpuStats? _gpu;
        private bool _noGpu;
        private long _idle, _kernel, _user;

        internal FpsItems.Sample Next(IReadOnlyList<string> items)
        {
            var now = Environment.TickCount64;
            if (now < _next) return _last;
            _next = now + EveryMs;
            (int? Use, int? Temperature, long? MemoryBytes) gpu = default;
            if (FpsItems.NeedsGpu(items) && !_noGpu)
            {
                _gpu ??= GpuStats.Open();
                _noGpu = _gpu is null;
                if (_gpu is { } card) gpu = card.Read(items.Contains(FpsItems.GpuUse), items.Contains(FpsItems.GpuTemperature), items.Contains(FpsItems.Vram));
            }
            else if (_gpu is not null)
            {
                _gpu.Dispose();
                _gpu = null;
            }
            return _last = new(gpu.Use, gpu.Temperature, items.Contains(FpsItems.CpuUse) ? CpuUse() : null,
                gpu.MemoryBytes / (double)(1L << 30), items.Contains(FpsItems.Ram) ? RamGb() : null,
                items.Contains(FpsItems.Battery) ? LowestBattery() : null);
        }

        private const long BatteryEveryMs = 60_000;
        private long _batteryNext;
        private volatile Box? _battery;
        private int _batteryReading;

        private sealed record Box((string Kind, int Percent)? Value);

        /// <summary>
        /// The wireless device lowest on battery, from the last read; read again on the pool once a minute (the device tree is
        /// too slow for the overlay's thread). Nothing on the first look.
        /// </summary>
        private (string Kind, int Percent)? LowestBattery()
        {
            var now = Environment.TickCount64;
            if (now >= _batteryNext && Interlocked.Exchange(ref _batteryReading, 1) == 0)
            {
                _batteryNext = now + BatteryEveryMs;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var low = Peripherals.List(() => true).Where(card => card.Battery is >= 0 and <= 1).MinBy(card => card.Battery);
                        _battery = new(low is null ? null : (FpsItems.BatteryKind(low.Kind), (int)Math.Round(low.Battery!.Value * 100)));
                    }
                    catch (Exception ex) { AppLog.Debug("FPS counter battery: " + ex.Message); }
                    finally { Volatile.Write(ref _batteryReading, 0); }
                });
            }
            return _battery?.Value;
        }

        /// <summary>Of all processors since the last look; nothing on the first.</summary>
        private int? CpuUse()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
            var seen = _kernel != 0;
            var (idled, all) = (idle - _idle, kernel + user - _kernel - _user);
            (_idle, _kernel, _user) = (idle, kernel, user);
            return seen && all > 0 ? (int)Math.Round(Math.Clamp(1 - idled / (double)all, 0, 1) * 100) : null;
        }

        private static double? RamGb()
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? (status.TotalPhys - status.AvailPhys) / (double)(1L << 30) : null;
        }

        public void Dispose() => _gpu?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

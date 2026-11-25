using System.Runtime.InteropServices;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services.Achievements;
using Microsoft.Web.WebView2.Core;
using WinRT;

namespace ExoLauncher;

/// <summary>
/// Achievement banners over the game (<see cref="AchievementWatch"/>): a small window at the top centre of the game's
/// screen, always on top, never taking focus or a click (input goes to the game), drawn by Windows' compositor with a
/// transparent WebView2 inside (Assets/Overlay/banner.html), so only the banner itself shows.
/// Nothing touches the game: no injection, no hook, safe with anti-cheat. It exists only while banners show and closes
/// 20 s after the last. No window can show over a game in exclusive full screen (Windows says so through
/// SHQueryUserNotificationState); its banners wait and show, in order, once the game leaves exclusive full screen or Exo
/// comes to the front.
/// </summary>
internal static class AchievementBanner
{
    /// <summary>The window, in the page's pixels: the card (banner.html) and the room above it for its entrance.</summary>
    private const int Width = 480, Height = 176;
    private static Overlay? _overlay;
    private static Timer? _close, _wait;
    private static DateTimeOffset _closeAt;
    private static readonly Queue<Unlock> Waiting = new();

    internal static void Start() => AchievementWatch.Unlocked += unlock =>
    {
        if (App.MainAppWindow?.DispatcherQueue.Enqueue(() => Arrive(unlock)) != true) AppLog.Warn("Achievements: no window thread for the banner.");
    };

    /// <summary>On the window thread: shown now, or held while a game is in exclusive full screen.</summary>
    private static void Arrive(Unlock unlock)
    {
        // Settings' Preview shows one even with banners off.
        if (!App.Services.Settings.Current.AchievementBanners && unlock.GameId != "preview") return;
        if (Waiting.Count > 0 || CannotShow())
        {
            Waiting.Enqueue(unlock);
            AppLog.Info($"Achievements: {unlock.Name} waits, the game is in exclusive full screen ({Waiting.Count} waiting).");
            (_wait ??= new Timer(_ => App.MainAppWindow?.DispatcherQueue.Enqueue(Release))).Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            return;
        }
        _ = ShowAsync([unlock]);
    }

    /// <summary>Every 2 s while banners wait: when they can show, all of them, in the order they unlocked.</summary>
    private static void Release()
    {
        if (Waiting.Count == 0 || CannotShow()) return;
        _wait?.Change(Timeout.Infinite, Timeout.Infinite);
        var all = Waiting.ToList();
        Waiting.Clear();
        _ = ShowAsync(all);
    }

    /// <summary>A game holds the screen in exclusive full screen, and Exo isn't in front.</summary>
    private static bool CannotShow() =>
        SHQueryUserNotificationState(out var state) == 0 && state == 3 // QUNS_RUNNING_D3D_FULL_SCREEN
        && !(App.MainAppWindow is { } main && GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(main));

    private static async Task ShowAsync(IReadOnlyList<Unlock> unlocks)
    {
        try
        {
            var settings = App.Services.Settings.Current;
            _overlay ??= await Overlay.CreateAsync(Width, Height).ConfigureAwait(true);
            _overlay.Place();
            foreach (var unlock in unlocks)
            {
                AppLog.Info($"Achievements: banner for {unlock.Name} ({unlock.Rarity}).");
                _overlay.Post(AchievementWatch.Card(unlock, settings.AchievementVolume, settings.ReduceMotion));
            }
            // The page shows them one after another, each up to 11.5 s; the window outlasts the last one queued by a margin.
            var closeAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4 + 12 * unlocks.Count);
            if (closeAt < _closeAt) closeAt = _closeAt.AddSeconds(12 * unlocks.Count);
            _closeAt = closeAt;
            (_close ??= new Timer(_ => App.MainAppWindow?.DispatcherQueue.Enqueue(Close))).Change(closeAt - DateTimeOffset.UtcNow, Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            // Any failure here is the banner's alone: logged with its type, never taken further.
            AppLog.Warn($"Achievements: the banner window didn't open: {ex.GetType().Name}: {ex.Message}");
            _overlay?.Dispose();
            _overlay = null;
        }
    }

    private static void Close()
    {
        _overlay?.Dispose();
        _overlay = null;
    }

    internal static void Shutdown()
    {
        _wait?.Dispose();
        _wait = null;
        _close?.Dispose();
        _close = null;
        Waiting.Clear();
        _overlay?.Dispose();
        _overlay = null;
    }

    /// <summary>A 16-bit WAV (the 44-byte header of tools/make-achievement-sounds.mjs) with its samples at this volume, 0 to 100; the source stays as it was.</summary>
    internal static byte[] AtVolume(byte[] wav, int percent)
    {
        var copy = (byte[])wav.Clone();
        var samples = MemoryMarshal.Cast<byte, short>(copy.AsSpan(44));
        var gain = Math.Clamp(percent, 0, 100) / 100.0;
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)Math.Round(samples[i] * gain);
        return copy;
    }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>The window: a popup with no redirection bitmap, the system compositor's target, the WebView2 its visual.</summary>
    private sealed partial class Overlay : IDisposable
    {
        private const string Host = "overlay.exo-launcher.local";
        private IntPtr _hwnd;
        private IntPtr _ownerHwnd;
        private Windows.UI.Composition.Compositor? _compositor;
        private Windows.UI.Composition.Desktop.DesktopWindowTarget? _target;
        private Windows.UI.Composition.ContainerVisual? _root;
        private CoreWebView2CompositionController? _controller;
        private WebViewEnvironmentRefPool<CoreWebView2Environment>.Lease? _environmentLease;
        private static WndProc? _proc;
        private static bool _registered;
        private readonly int _width, _height;
        private bool _loaded;
        private Timer? _readyFallback;
        private readonly Queue<string> _pending = new();

        private Overlay(int width, int height) { _width = width; _height = height; }

        internal static async Task<Overlay> CreateAsync(int width, int height)
        {
            var overlay = new Overlay(width, height);
            try
            {
                overlay.MakeWindow();
                EnsureSystemDispatcher();
                overlay._compositor = new Windows.UI.Composition.Compositor();
                var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(((IWinRTObject)overlay._compositor).NativeObject.ThisPtr);
                interop.CreateDesktopWindowTarget(overlay._hwnd, true, out var target);
                overlay._target = Windows.UI.Composition.Desktop.DesktopWindowTarget.FromAbi(target);
                overlay._root = overlay._compositor.CreateContainerVisual();
                overlay._target.Root = overlay._root;

                overlay._environmentLease = await WebViewEnvironmentFactory.AcquireAsync().ConfigureAwait(true);
                var environment = overlay._environmentLease.Environment;
                overlay._controller = await environment.CreateCoreWebView2CompositionControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)overlay._hwnd)).AsTask().ConfigureAwait(true);
                overlay._controller.DefaultBackgroundColor = new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 };
                overlay._controller.ShouldDetectMonitorScaleChanges = false;
                overlay._controller.RootVisualTarget = overlay._root;
                var core = overlay._controller.CoreWebView2;
                WebViewEnvironmentLifetime.ApplyLowMemoryTarget(core);
                core.Settings.IsWebMessageEnabled = true;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "Assets", "Overlay"), CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationStarting += (_, e) => UiGuard.Run(() => { if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.Ordinal)) e.Cancel = true; });
                overlay._root.Size = new System.Numerics.Vector2(width, height);
                overlay._controller.Bounds = new Windows.Foundation.Rect(0, 0, width, height);
                overlay._controller.IsVisible = true;
                core.WebMessageReceived += (_, e) => UiGuard.Run(() => overlay.OnWebMessage(e.WebMessageAsJson));
                core.NavigationCompleted += (_, e) => UiGuard.Run(() => overlay.OnNavigationCompleted(e.IsSuccess));
                core.Navigate($"https://{Host}/banner.html");
                return overlay;
            }
            catch
            {
                overlay.Dispose();
                throw;
            }
        }
        /// <summary>
        /// On the game: the top centre of the game's own window (the one in front while it plays; a windowed game's
        /// frame, a full-screen one's whole screen), kept inside its screen, at that screen's scale, shown without activating.
        /// </summary>
        internal void Place()
        {
            var game = GetForegroundWindow();
            var monitor = MonitorFromWindow(game, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return;
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
            int width = (int)Math.Ceiling(_width * scale), height = (int)Math.Ceiling(_height * scale);
            var area = GetWindowRect(game, out var frame) && frame.Right - frame.Left >= width ? frame : info.Monitor;
            var x = Math.Clamp(area.Left + (area.Right - area.Left - width) / 2, info.Monitor.Left, info.Monitor.Right - width);
            var y = Math.Clamp(area.Top, info.Monitor.Top, info.Monitor.Bottom - height);
            SetWindowPos(_hwnd, new IntPtr(-1), x, y, width, height, 0x0010 | 0x0040);
            if (_root is not null) _root.Size = new System.Numerics.Vector2(width, height);
            if (_controller is null) return;
            _controller.RasterizationScale = scale;
            _controller.Bounds = new Windows.Foundation.Rect(0, 0, width, height);
        }

        private void OnWebMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "banner.ready")
                {
                    MarkReady();
                    return;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            PlaySound(json);
        }

        private void OnNavigated(bool isSuccess)
        {
            if (!isSuccess)
            {
                AppLog.Warn("Achievements: banner navigation failed.");
                return;
            }
            if (_loaded) return;
            _readyFallback ??= new Timer(_ => App.MainAppWindow?.DispatcherQueue.Enqueue(MarkReady), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }

        internal void OnNavigationCompleted(bool isSuccess) => OnNavigated(isSuccess);

        private void MarkReady()
        {
            if (_loaded) return;
            _loaded = true;
            _readyFallback?.Dispose();
            _readyFallback = null;
            if (_controller?.CoreWebView2 is not { } core) return;
            while (_pending.Count > 0) core.PostWebMessageAsJson(_pending.Dequeue());
        }

        /// <summary>Plays the sound the page asked for ({"sound":"epic"}, by tier) on Windows' default output, at the volume in Settings.</summary>
        private static void PlaySound(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("sound", out var sound) || sound.GetString() is not { } name || name is not ("common" or "rare" or "epic" or "legendary" or "completion")) return;
                var volume = App.Services.Settings.Current.AchievementVolume;
                if (volume > 0) _ = Task.Run(() => Play(name, volume));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { AppLog.Debug("Achievements: no sound: " + ex.Message); }
        }

        private static void Play(string name, int volume)
        {
            try
            {
                lock (SoundGate)
                {
                    if (!Sounds.TryGetValue(name, out var wav)) Sounds[name] = wav = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Overlay", "sounds", name + ".wav"));
                    // PlaySound reads the bytes while it plays: the buffer stays pinned until the next sound replaces it.
                    if (_sound.IsAllocated) { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); _sound.Free(); }
                    _sound = GCHandle.Alloc(AtVolume(wav, volume), GCHandleType.Pinned);
                    if (!PlaySound(_sound.AddrOfPinnedObject(), IntPtr.Zero, 0x0004 | 0x0001 | 0x0002)) // SND_MEMORY | SND_ASYNC | SND_NODEFAULT
                        AppLog.Info($"Achievements: Windows didn't play the banner's sound ({wav.Length / 1024} KB).");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { AppLog.Debug("Achievements: no sound: " + ex.Message); }
        }

        private static readonly Lock SoundGate = new();
        private static readonly Dictionary<string, byte[]> Sounds = new(StringComparer.Ordinal);
        private static GCHandle _sound;

        [DllImport("winmm.dll")] private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

        internal void Post(string json)
        {
            if (_loaded) _controller?.CoreWebView2.PostWebMessageAsJson(json);
            else _pending.Enqueue(json);
        }

        private void MakeWindow()
        {
            var instance = GetModuleHandle(null);
            if (!_registered)
            {
                _proc = (hwnd, msg, w, l) => msg == 0x0084 ? new IntPtr(-1) : DefWindowProc(hwnd, msg, w, l); // WM_NCHITTEST: HTTRANSPARENT
                var cls = new WndClassEx { Size = Marshal.SizeOf<WndClassEx>(), Proc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = instance, ClassName = "ExoAchievementBanner" };
                if (RegisterClassEx(ref cls) == 0) throw new InvalidOperationException("The banner window class wasn't registered.");
                _registered = true;
            }
            // Owned by an unshown tool window so GetWindow(GW_OWNER) != 0 and .NET Process.MainWindowHandle skips the overlay.
            _ownerHwnd = CreateWindowEx(0x00000080, "ExoAchievementBanner", "", 0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            // Topmost, tool (no taskbar), no activation, click-through, no redirection bitmap (the compositor draws it).
            const uint ex = 0x00000008 | 0x00000080 | 0x08000000 | 0x00000020 | 0x00200000;
            _hwnd = CreateWindowEx(ex, "ExoAchievementBanner", "Exo achievement", 0x80000000, 0, 0, _width, _height, _ownerHwnd, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                if (_ownerHwnd != IntPtr.Zero) { DestroyWindow(_ownerHwnd); _ownerHwnd = IntPtr.Zero; }
                throw new InvalidOperationException("The banner window wasn't made.");
            }
        }

        public void Dispose()
        {
            _readyFallback?.Dispose();
            _readyFallback = null;
            WebViewEnvironmentLifetime.CloseAndRelease(_controller);
            _controller = null;
            _target?.Dispose();
            _target = null;
            _root?.Dispose();
            _root = null;
            _compositor?.Dispose();
            _compositor = null;
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            if (_ownerHwnd != IntPtr.Zero) DestroyWindow(_ownerHwnd);
            _ownerHwnd = IntPtr.Zero;
            Interlocked.Exchange(ref _environmentLease, null)?.Dispose();
        }

        /// <summary>Windows.UI.Composition needs a Windows.System dispatcher queue on its thread; WinUI's own isn't one.</summary>
        private static void EnsureSystemDispatcher()
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
            var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _));
        }

        [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositorDesktopInterop
        {
            void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
            void EnsureOnThread(uint threadId);
        }

        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions { public int Size; public int ThreadType; public int ApartmentType; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public int Size; public uint Style; public IntPtr Proc; public int ClsExtra; public int WndExtra; public IntPtr Instance;
            public IntPtr Icon; public IntPtr Cursor; public IntPtr Background; public string? MenuName; public string ClassName; public IntPtr IconSmall;
        }

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

        [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WndClassEx cls);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    }
}

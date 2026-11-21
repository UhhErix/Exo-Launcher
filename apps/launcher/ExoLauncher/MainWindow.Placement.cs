using ExoLauncher.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    private WindowPlacement? _normalPlacement;
    private WindowPlacement? _persistedPlacement;
    private DispatcherQueueTimer? _placementSaveTimer;

    private void RestoreWindowPlacement()
    {
        if (TestWindowRect() is { } testRect)
        {
            AppWindow.MoveAndResize(testRect);
            RememberWindowPlacement();
            return;
        }
        var saved = WindowPlacementStore.Load();
        if (saved is null)
        {
            TryCenterOnScreen();
            RememberWindowPlacement();
            return;
        }
        try
        {
            var area = DisplayArea.GetFromRect(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.Nearest);
            if (area is null) { TryCenterOnScreen(); RememberWindowPlacement(); return; }
            // Move to the saved monitor before reading its DPI.
            AppWindow.Move(new PointInt32(area.WorkArea.X, area.WorkArea.Y));
            var work = area.WorkArea;
            var restored = WindowPlacementStore.Fit(saved, work.X, work.Y, work.Width, work.Height, GetWindowScale());
            AppWindow.MoveAndResize(new RectInt32(restored.X, restored.Y, restored.Width, restored.Height));
            _normalPlacement = restored;
            _persistedPlacement = saved;
            if (restored.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                // Maximize activates the window. Started quiet (EXO_NO_ACTIVATE, a restart during a game), it fills the
                // screen's work area instead and maximizes the first time the user comes to it. An activation alone isn't
                // that: focus moving inside a window that isn't in front can activate it too, and maximizing then takes the front.
                if (ForegroundWatcher.StartedQuiet)
                {
                    AppWindow.MoveAndResize(new RectInt32(work.X, work.Y, work.Width, work.Height));
                    _maximizeWhenUsed = true;
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    void Once(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
                    {
                        // Never from minimized: parked off every screen, a maximize there can land on the primary one.
                        if (args.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated ||
                            presenter.State == OverlappedPresenterState.Minimized ||
                            !ForegroundWatcher.ExoInFront(hwnd) || !ForegroundWatcher.Wanted()) return;
                        Activated -= Once;
                        _maximizeWhenUsed = false;
                        presenter.Maximize();
                    }
                    Activated += Once;
                }
                else presenter.Maximize();
            }
            AppLog.Info($"Window: placed at {Describe(AppWindow.Position, AppWindow.Size)}" +
                (!restored.Maximized ? "." : _maximizeWhenUsed ? ", maximized once it's used." : ", maximized."));
        }
        catch { TryCenterOnScreen(); RememberWindowPlacement(); }
    }

    // A quiet start's window stands in for the maximized one until the user comes to it.
    private bool _maximizeWhenUsed;

    /// <summary>
    /// A quiet start's first show: no activation, where the placement put the window. A show that lands it anywhere else
    /// is logged and the window goes back, still without activation.
    /// </summary>
    internal void ShowQuietly()
    {
        var (placedAt, placedSize) = (AppWindow.Position, AppWindow.Size);
        AppWindow.Show(activateWindow: false);
        var (at, size) = (AppWindow.Position, AppWindow.Size);
        if ((at.X, at.Y, size.Width, size.Height) == (placedAt.X, placedAt.Y, placedSize.Width, placedSize.Height))
        {
            AppLog.Info($"Window: shown without the focus at {Describe(at, size)}.");
            return;
        }
        AppLog.Info($"Window: shown at {Describe(at, size)}, not {Describe(placedAt, placedSize)}; put back.");
        SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(this), IntPtr.Zero, placedAt.X, placedAt.Y,
            placedSize.Width, placedSize.Height, 0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
    }

    /// <summary>
    /// EXO_WINDOW_RECT="x,y,w,h" (set by tools/test-instance.ps1): where a test instance's window opens, in the units of
    /// System.Windows.Forms.Screen, scaled here to the window's DPI. Used only when the rect lies on a screen other than
    /// the primary one, so a test window never opens on the main screen; otherwise null and the placement goes on as usual.
    /// </summary>
    private RectInt32? TestWindowRect()
    {
        var parts = (Environment.GetEnvironmentVariable("EXO_WINDOW_RECT") ?? "").Split(',');
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, out _))) return null;
        var scale = GetWindowScale();
        var v = parts.Select(p => (int)Math.Round(int.Parse(p) * scale)).ToArray();
        var rect = new RectInt32(v[0], v[1], v[2], v[3]);
        var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Primary);
        var primary = DisplayArea.Primary.WorkArea;
        if (area is null || (area.WorkArea.X, area.WorkArea.Y) == (primary.X, primary.Y)) return null;
        return rect;
    }

    private static string Describe(PointInt32 at, SizeInt32 size) => $"{at.X},{at.Y} {size.Width}x{size.Height}";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private void RememberWindowPlacement()
    {
        if (_maximizeWhenUsed) return;
        if (AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized) return;
        if (presenter.State == OverlappedPresenterState.Restored)
            _normalPlacement = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, false, GetWindowScale());
        else if (_normalPlacement is not null)
            _normalPlacement = _normalPlacement with { Maximized = true };
        if (_normalPlacement is null || _normalPlacement == _persistedPlacement) return;
        _placementSaveTimer ??= CreatePlacementSaveTimer();
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private DispatcherQueueTimer CreatePlacementSaveTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => UiGuard.Run(SaveWindowPlacement);
        return timer;
    }

    private void SaveWindowPlacement()
    {
        _placementSaveTimer?.Stop();
        if (_normalPlacement is not { } placement || placement == _persistedPlacement) return;
        WindowPlacementStore.Save(placement);
        _persistedPlacement = placement;
    }
}

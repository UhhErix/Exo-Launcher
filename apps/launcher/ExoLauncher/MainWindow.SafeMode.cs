using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    /// <summary>
    /// Call of Duty asks to start in safe mode, which resets its settings (Optimize's with them), when the marker of a
    /// session that didn't quit cleanly is still there (<see cref="GameSettings.ClearLeftoverMarker"/>). Exo clears it
    /// as such a game ends, however it was started, so even a start from Steam doesn't ask: it keeps looking until the marker's
    /// process has been gone for 5 seconds (up to 2 minutes, so a game that exits slowly is still cleared); again just before Exo
    /// starts one; and once as Exo starts, for a game that ended while Exo was closed. Each marker it clears is evidence for
    /// Keep that the session after it may have reset the settings.
    /// </summary>
    private Action<Models.GameEntry>? _safeModeLaunchingHandler;
    private Action<string, GameRunState>? _safeModeRunStateHandler;

    private void StartSafeModeGuard()
    {
        _safeModeLaunchingHandler ??= game => ClearSafeModeMarker(game, "starts");
        _safeModeRunStateHandler ??= (gameId, state) =>
        {
            if (state.IsRunning || App.Services.Library.Find(gameId) is not { } game) return;
            _ = GuardMarkerAsync(game, "ended");
        };
        App.Services.Launcher.GameLaunching += _safeModeLaunchingHandler;
        App.Services.Launcher.GameRunStateChanged += _safeModeRunStateHandler;
        _ = Task.Run(async () =>
        {
            try
            {
                // After the start's own work.
                await Task.Delay(TimeSpan.FromSeconds(15), _lifetimeCts.Token).ConfigureAwait(false);
                foreach (var game in await App.Services.Library.GetLibraryAsync().ConfigureAwait(false))
                    if (!_closingWindow && game.Installed && !string.IsNullOrEmpty(game.Path) && File.Exists(Path.Combine(game.Path, "__cod")))
                        await GuardMarkerAsync(game, "was found as Exo started").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_closingWindow) { }
            catch (Exception ex) { AppLog.Warn($"Safe mode: the look as Exo started failed: {ex.GetType().Name}"); }
        });
    }

    private void StopSafeModeGuard()
    {
        if (Interlocked.Exchange(ref _safeModeLaunchingHandler, null) is { } launching)
            App.Services.Launcher.GameLaunching -= launching;
        if (Interlocked.Exchange(ref _safeModeRunStateHandler, null) is { } runState)
            App.Services.Launcher.GameRunStateChanged -= runState;
    }

    private static async Task GuardMarkerAsync(Models.GameEntry game, string when)
    {
        try
        {
            if (await GameSettings.ClearMarkerWhenGoneAsync(game).ConfigureAwait(false))
                AppLog.Info($"Safe mode: {game.Title} {when} after a session that didn't quit cleanly; its marker is cleared, so it won't ask.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Safe mode: {game.Title}'s marker couldn't be cleared: {ex.GetType().Name}");
        }
    }

    private static void ClearSafeModeMarker(Models.GameEntry game, string when)
    {
        try
        {
            if (GameSettings.ClearLeftoverMarkerNoted(game))
                AppLog.Info($"Safe mode: {game.Title} {when} after a session that didn't quit cleanly; its marker is cleared, so it won't ask.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Safe mode: {game.Title}'s marker couldn't be cleared: {ex.GetType().Name}");
        }
    }
}

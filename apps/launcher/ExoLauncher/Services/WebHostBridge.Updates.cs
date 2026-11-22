using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
#if DEBUG
    private const bool DebugBuild = true;
#else
    private const bool DebugBuild = false;
#endif

    private int _updateProblemRead;

    /// <summary>
    /// How the update before this start ended, when it didn't take: one plain sentence, said once (the first ask in this
    /// process), and the version it was for is not offered again by itself.
    /// </summary>
    private string? LastUpdateProblem(string offered)
    {
        if (Interlocked.Exchange(ref _updateProblemRead, 1) == 1) return null;
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (root is null) return null;
        var (said, version) = UpdateOutcome.TakeOutcome(root);
        // A failure that names no version skips nothing: guessing the offered one could hide a good update.
        if (said is not null && version is { } failed) _services.Updater.Skip(failed);
        return said;
    }

    /// <param name="manual">Settings' "Check for update": asks again, bypassing the release cache. Otherwise the page's own
    /// ask as it starts (or after a reload): the launch's one check, whose first answer may open the update sheet.</param>
    private async Task<object> CheckUpdateAsync(bool manual, CancellationToken ct)
    {
        var updater = _services.Updater;
        var check = manual
            ? await updater.CheckAsync(_services.AppVersion, ct, fresh: true).ConfigureAwait(false)
            : await updater.OpenCheck(_services.AppVersion).WaitAsync(ct).ConfigureAwait(false);
        var problem = LastUpdateProblem(check.RemoteVersion);
        var skipped = check.UpdateAvailable && updater.SkippedVersion == AppUpdateService.Normalize(check.RemoteVersion);
        var offer = !manual && updater.OnOpen.Offer(check.UpdateAvailable && !skipped, _services.AppVersion, DebugBuild, lastUpdateFailed: false);
        return new ExoUpdateResponse
        {
            Ok = check.UpdateAvailable || check.AlreadyLatest,
            AlreadyLatest = check.AlreadyLatest,
            UpdateAvailable = check.UpdateAvailable,
            Latest = check.RemoteVersion,
            Current = check.LocalVersion,
            Message = check.Message,
            Upcoming = check.Upcoming,
            // In-app only — no browser URL for install.
            InApp = true,
            OfferOnOpen = offer ? true : null,
            NotNow = updater.OnOpen.NotNow ? true : null,
            Problem = problem,
            State = UpdateStateName(updater.State),
        };
    }

    /// <summary>idle, checking, downloading, waitingForGame, ready, applying, restarting, failed.</summary>
    internal static string UpdateStateName(UpdateFlow.State state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());

    private object ShellReady()
    {
        UpdateStartupHealth.UiReady(_services.AppVersion);
        return new { ok = true };
    }

    private async Task<object> InstallUpdateAsync(CancellationToken ct)
    {
        if (HostMode.IsDisposable) return new { ok = false, message = HostMode.Refusal };
        void Push(string status, double? percent) =>
            PostEvent("app.updateProgress", new { status, percent });

        var progress = new Progress<(string status, double? percent)>(p => Push(p.status, p.percent));
        var result = await _services.Updater
            .InstallAsync(_services.AppVersion, progress, ct)
            .ConfigureAwait(true);
        // Kept for after a game is not a failure: the sheet closes and the notice says when it goes in.
        if (!result.Installed && !result.AlreadyLatest && !result.Staged) _services.Updater.OnOpen.Failed();

        if (result.ShouldExit)
        {
            Push("Restarting…", null);
            // Let the normal Window.Closed path flush active playtime,
            // settings, sync state, tray resources, and bridge services before
            // the installer swaps the application directory.
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(350).ConfigureAwait(false); } catch { /* */ }
                _queue.Enqueue(() =>
                {
                    if (RequestHostShutdown is { } shutdown)
                    {
                        try { shutdown(); } catch { }
                        return;
                    }
                    try { App.MainAppWindow?.CloseWindow(); } catch { /* installer will time out safely */ }
                    // Exo did not close: the installer stops on its own (it can't swap a running app), and the update failed.
                    try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { _services.Updater.Move(UpdateFlow.Step.ApplyFailed); }
                });
            });
        }

        return new ExoUpdateResponse
        {
            Ok = result.Installed || result.AlreadyLatest || result.Staged,
            Staged = result.Staged ? true : null,
            UpdateAvailable = result.UpdateAvailable,
            AlreadyLatest = result.AlreadyLatest,
            Installed = result.Installed,
            ShouldExit = result.ShouldExit,
            Latest = result.RemoteVersion,
            Current = result.LocalVersion,
            Message = result.Message,
            NotNow = _services.Updater.OnOpen.NotNow ? true : null,
            State = UpdateStateName(_services.Updater.State),
        };
    }

}

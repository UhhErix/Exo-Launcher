using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The Game clients card: which Epic Games Launcher layout and Steam branch are installed, and the one Fix button. Steam
/// goes to its Beta client: package\beta is written, Steam is closed with its own -shutdown and started again, and the old
/// file comes back if Steam does not close or start. An old Epic launcher opens Epic's installer (verified by
/// <see cref="EpicNewLauncherInstaller"/>); nothing is uninstalled here, and the installer removes the old launcher itself.
/// Neither fix runs while a game plays.
/// </summary>
public sealed partial class WebHostBridge
{
    private object ClientsStatus() => ClientsStatusDto(StoreClientLayouts.Detect());

    private static object ClientsStatusDto(StoreClientLayouts.Status status) => new
    {
        ok = true,
        needsFix = status.NeedsFix,
        problem = status.Problem,
        epic = new
        {
            layout = status.Epic.Layout.ToString().ToLowerInvariant(),
            version = status.Epic.Version,
            oldStillThere = status.Epic.OldStillThere,
            installing = EpicNewLauncherInstaller.Running,
        },
        steam = new { channel = status.Steam.Channel.ToString().ToLowerInvariant() },
    };

    private async Task<object> ClientsFixAsync(CancellationToken ct)
    {
        if (HostMode.IsDisposable) return new { ok = false, message = "Exo does not change clients in this session." };
        var status = StoreClientLayouts.Detect();
        // A read Exo could not answer is never a fix: it says why (Try again re-reads), and nothing is downloaded or closed.
        if (status.Problem is { } unreadable) return new { ok = false, message = unreadable };
        if (!status.NeedsFix) return new { ok = true, message = "Your clients are up to date." };
        var exoGame = _services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0;
        if (StoreClientLayouts.FixBlockedBy(exoGame, SteamGameRunning()) is { } blocked) return new { ok = false, message = blocked };

        var messages = new List<string>();
        if (status.Steam.Channel == StoreClientLayouts.SteamChannel.Stable && status.Steam.Root is { } root)
        {
            var (steamOk, steamMessage) = await SwitchSteamToBetaAsync(root, ct).ConfigureAwait(false);
            if (!steamOk) return new { ok = false, message = steamMessage };
            messages.Add(steamMessage);
        }
        if (status.Epic.Layout == StoreClientLayouts.EpicLayout.Old)
        {
            var download = await EpicNewLauncherInstaller.DownloadAsync(ct).ConfigureAwait(false);
            if (download.Path is null) return new { ok = false, message = download.Failure };
            if (EpicNewLauncherInstaller.Open(download) is { } failure) return new { ok = false, message = failure };
            messages.Add("Epic's installer is open. Finish it there; Exo checks again when it closes.");
        }
        return new { ok = true, message = string.Join(" ", messages) };
    }

    /// <summary>Steam on its Beta client: the file first, then Steam closed and started again. A failure puts the file back.</summary>
    private async Task<(bool Ok, string Message)> SwitchSteamToBetaAsync(string root, CancellationToken ct)
    {
        var exe = Path.Combine(root, "steam.exe");
        var reason = "";
        try
        {
            // The branch file goes back on any exit that does not reach Beta, a cancellation included (SteamBetaFile.SwitchAsync).
            var onBeta = await SteamBetaFile.SwitchAsync(root, SteamClientUpdate.Beta, async () =>
            {
                if (!await CloseSteamAsync(exe, ct).ConfigureAwait(false))
                {
                    reason = "Steam did not close. Close Steam, then try again.";
                    return false;
                }
                try
                {
                    await StartSteamHiddenAsync(exe, ct).ConfigureAwait(false);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    AppLog.Warn("Steam: did not start on its Beta client: " + ex.GetType().Name);
                    reason = "Steam could not start. Start it again, then try again.";
                    return false;
                }
                return true;
            }).ConfigureAwait(false);
            if (!onBeta) return (false, reason);
        }
        catch (UnauthorizedAccessException)
        {
            AppLog.Warn("Steam: its Beta client couldn't be set: folder not writable.");
            return (false, "Steam's folder isn't writable, so Exo could not switch it to its Beta client.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            AppLog.Warn("Steam: its Beta client couldn't be set: " + ex.GetType().Name);
            return (false, "Exo could not switch Steam to its Beta client. Try again.");
        }
        AppLog.Info("Steam: restarted on its Beta client.");
        return (true, "Steam is on its Beta client.");
    }
}

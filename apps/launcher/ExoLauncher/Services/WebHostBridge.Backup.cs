using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Backups (Settings > Backups, and the first-run restore offer): backup.status (is there a daily backup to restore, and its
/// day), backup.now (back up now), backup.export and backup.import (Windows' pickers, one file), backup.restore (the offered
/// one). The page sees a day and a count, never a path. A restore that succeeds restarts Exo, so every service reads the
/// restored files from the start.
/// </summary>
public sealed partial class WebHostBridge
{
    private static string BackupVersion => typeof(WebHostBridge).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>
    /// The newest daily backup (Settings' "Last backup", from any PC), and the restore offer while setup is open (another PC's
    /// backup, never this install's own). The offer names its day and PC so backup.restore restores exactly what was shown.
    /// </summary>
    private static object BackupStatus()
    {
        var last = LauncherBackup.Latest(LauncherBackup.DefaultFolder);
        var offer = App.Services.Settings.Current.OnboardingComplete ? null : LauncherBackup.OfferFor(PathHelper.AppDataDir, LauncherBackup.DefaultFolder);
        return new
        {
            ok = true,
            last = last is null ? null : new { day = last.Day, at = File.GetLastWriteTimeUtc(last.Path).ToString("O") },
            offer = offer is null ? null : new { day = offer.Day, device = offer.Device, files = offer.Files },
            // Backups that stopped say so in one sentence, with when they stopped (the shell tells it once per stop).
            note = LauncherBackup.LastResult(PathHelper.AppDataDir) is { } done && LauncherBackup.Say(done.Kind) is { Sentence: { } sentence } said
                ? new { sentence, stopped = said.Stopped, since = done.StoppedSince?.ToString("O") }
                : null,
        };
    }

    private void OnBackupStopped(string sentence, DateTimeOffset since) => PostEvent("backup.stopped", new { sentence, since = since.ToString("O") });

    /// <summary>The first-run offer declined: this install is not asked again (Settings' own restore still works).</summary>
    private static object BackupDecline()
    {
        try { LauncherBackup.MarkAnswered(PathHelper.AppDataDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Backup: the answer to the restore offer was not saved: " + ex.GetType().Name);
            return new { ok = false, message = "Exo could not save that answer." };
        }
        return new { ok = true };
    }

    private static object BackupNow()
    {
        var outcome = LauncherBackup.BackUpDaily(PathHelper.AppDataDir, LauncherBackup.DefaultFolder, DateTimeOffset.UtcNow, BackupVersion, replace: true, LauncherBackup.ThisPc(PathHelper.AppDataDir, LucidSettings.FilePath));
        return new { ok = outcome.Ok, message = outcome.Message, files = outcome.Files };
    }

    private async Task<object> BackupExportAsync()
    {
        var picked = await OnUiThread(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow!));
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("Exo Launcher backup", [".json"]);
            picker.SuggestedFileName = $"exo-launcher-export-{DateTime.Now:yyyyMMdd-HHmm}";
            return (await picker.PickSaveFileAsync())?.Path;
        }).ConfigureAwait(true);
        if (picked.Failed) return new { ok = false, message = "The file picker could not open." };
        if (picked.Path is null) return new { ok = false, cancelled = true };
        var path = picked.Path;
        var outcome = await Task.Run(() => LauncherBackup.ExportTo(PathHelper.AppDataDir, path, DateTimeOffset.UtcNow, BackupVersion, LauncherBackup.ThisPc(PathHelper.AppDataDir, LucidSettings.FilePath))).ConfigureAwait(true);
        return new { ok = outcome.Ok, message = outcome.Message, files = outcome.Files };
    }

    private async Task<object> BackupImportAsync()
    {
        var picked = await OnUiThread(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow!));
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");
            return (await picker.PickSingleFileAsync())?.Path;
        }).ConfigureAwait(true);
        if (picked.Failed) return new { ok = false, message = "The file picker could not open." };
        if (picked.Path is null) return new { ok = false, cancelled = true };
        return await RestoreFileAsync(picked.Path).ConfigureAwait(true);
    }

    /// <summary>The offered daily backup, by the day and PC the page was shown, restored. Nothing else is read.</summary>
    private async Task<object> BackupRestoreOfferAsync(JsonElement paramsEl, bool hasParams)
    {
        var path = LauncherBackup.DailyPath(LauncherBackup.DefaultFolder, ReadString(paramsEl, hasParams, "day") ?? "", ReadString(paramsEl, hasParams, "device") ?? "");
        if (path is null || LauncherBackup.Answered(PathHelper.AppDataDir)) return new { ok = false, message = "There is no backup to restore." };
        return await RestoreFileAsync(path).ConfigureAwait(true);
    }

    private static async Task<object> RestoreFileAsync(string path)
    {
        var outcome = await Task.Run(() =>
        {
            string text;
            try
            {
                if (!LauncherBackup.FitsBackupLimit(new FileInfo(path).Length))
                    return new LauncherBackup.Outcome(false, "This backup is too large to restore, so nothing was restored.");
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new LauncherBackup.Outcome(false, "Exo could not read that file, so nothing was restored.");
            }
            // A daily backup's pictures sit beside it in the backups folder; an export holds its own.
            var read = LauncherBackup.Validate(text, Path.GetDirectoryName(Path.GetFullPath(path)));
            if (!read.Ok) return new LauncherBackup.Outcome(false, read.Message);
            return LauncherBackup.Restore(PathHelper.AppDataDir, read.Files, LauncherBackup.DefaultFolder, DateTimeOffset.UtcNow, BackupVersion, madeOn: read.Machine, missingPictures: read.MissingPictures);
        }).ConfigureAwait(true);
        if (!outcome.Ok) return new { ok = false, message = outcome.Message, files = outcome.Files, restarting = false };
        // Answered before the restart, so the run after it never offers the same backup again.
        try { LauncherBackup.MarkAnswered(PathHelper.AppDataDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Backup: the restore was not recorded as answered: " + ex.GetType().Name);
        }
        KeepRestoredFiles();
        return RestoreAnswer(outcome, RestartRefused());
    }

    /// <summary>
    /// The answer to a restore that wrote its files: restarting when the restart went through, else the error the Restore step
    /// shows (the data is restored; Exo has to be reopened by hand).
    /// </summary>
    internal static object RestoreAnswer(LauncherBackup.Outcome outcome, bool restartRefused) => restartRefused
        ? new { ok = false, message = "Restored. Close and reopen Exo to finish.", files = outcome.Files, restarting = false, restored = true }
        : new { ok = true, message = outcome.Message, files = outcome.Files, restarting = true };

    /// <summary>
    /// The files a restore wrote stay as written: this run's own saves (settings, playtime and its open sessions, the Riot
    /// ledger, notes, window placement) are never written over them while Exo closes.
    /// </summary>
    private static void KeepRestoredFiles()
    {
        App.Services.Settings.KeepRestoredFile();
        PlaytimeService.KeepRestoredFile();
        RiotLedger.KeepRestoredFile();
        NoteStore.Shared.KeepRestoredFile();
        WindowPlacementStore.KeepRestoredFile();
    }

    /// <summary>
    /// Asks the SDK to restart Exo, so the new run reads what was restored. A restart that goes through does not return (the
    /// process ends); a return or a throw means it was refused.
    /// </summary>
    private static bool RestartRefused()
    {
        try
        {
            var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
            AppLog.Warn("Backup: restart after restore was refused: " + reason);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warn("Backup: restart after restore failed: " + ex.GetType().Name);
            return true;
        }
    }
}

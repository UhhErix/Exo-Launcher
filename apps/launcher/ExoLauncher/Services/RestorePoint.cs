using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// A System Restore point before several apps are removed at once, as Revo makes one: only when the player leaves "Restore
/// point first" on and presses Uninstall, under Windows' own prompt (only an administrator may make one). Windows' own
/// cmdlet (Checkpoint-Computer, the WMI SystemRestore class underneath) makes it; Windows keeps at most one a day, so a
/// point made in the last 24 hours stands in for a new one. The answer comes back through a file the elevated step writes,
/// one word, since an elevated process's exit code doesn't reach Exo.
/// </summary>
internal static class RestorePoint
{
    internal enum Outcome { Made, Recent, Off, Declined, Failed }

    private const string Script =
        "$s=Get-Date;$o='off';try{Checkpoint-Computer -Description 'Exo Launcher: before removing apps' -RestorePointType APPLICATION_UNINSTALL -ErrorAction Stop -WarningAction SilentlyContinue;" +
        "$r=Get-ComputerRestorePoint|Sort-Object SequenceNumber|Select-Object -Last 1;" +
        "if($r){$t=$r.ConvertToDateTime($r.CreationTime);if($t -ge $s.AddMinutes(-1)){$o='made'}elseif($t -ge $s.AddHours(-24)){$o='recent'}else{$o='failed'}}else{$o='failed'}}catch{$o='off'};" +
        "[IO.File]::WriteAllText({0},$o)";

    /// <summary>The elevated step that makes one and writes its one-word answer to <paramref name="answer"/>.</summary>
    internal static string Step(string answer) =>
        $"{LeftoverScan.PowerShell} -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes(Script.Replace("{0}", "'" + answer.Replace("'", "''") + "'", StringComparison.Ordinal)));

    /// <summary>The elevated step's word as an outcome; anything else (nothing written, a word changed) failed.</summary>
    internal static Outcome Read(string? word) => word?.Trim() switch
    {
        "made" => Outcome.Made,
        "recent" => Outcome.Recent,
        "off" => Outcome.Off,
        _ => Outcome.Failed,
    };

    /// <summary>What the page says when it can't go on: null when there is a restore point to go back to.</summary>
    internal static string? Problem(Outcome outcome) => outcome switch
    {
        Outcome.Made or Outcome.Recent => null,
        Outcome.Off => "System Restore is off on this PC, so no restore point was made. Nothing was removed.",
        Outcome.Declined => "Windows' permission was declined, so no restore point was made. Nothing was removed.",
        _ => "Windows didn't make a restore point. Nothing was removed.",
    };

    internal static async Task<Outcome> MakeAsync(Func<IReadOnlyList<string>, Task<bool>> admin)
    {
        if (HostMode.IsDisposable) return Outcome.Failed;
        var answer = Path.Combine(Path.GetTempPath(), $"exo-{Guid.NewGuid():N}.restore");
        try
        {
            if (!await admin([Step(answer)]).ConfigureAwait(false)) return Outcome.Declined;
            var outcome = Read(File.Exists(answer) ? await File.ReadAllTextAsync(answer).ConfigureAwait(false) : null);
            AppLog.Info($"Apps: restore point before removing apps: {outcome}.");
            return outcome;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Apps: restore point answer unreadable: " + ex.GetType().Name);
            return Outcome.Failed;
        }
        finally
        {
            if (File.Exists(answer)) DeletionGate.DeleteFile(answer);
        }
    }
}

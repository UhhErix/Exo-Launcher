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
        "$s=Get-Date;$o='off';try{Checkpoint-Computer -Description '{1}' -RestorePointType {2} -ErrorAction Stop -WarningAction SilentlyContinue;" +
        "$r=Get-ComputerRestorePoint|Sort-Object SequenceNumber|Select-Object -Last 1;" +
        "if($r){$t=$r.ConvertToDateTime($r.CreationTime);if($t -ge $s.AddMinutes(-1)){$o='made'}elseif($t -ge $s.AddHours(-24)){$o='recent'}else{$o='failed'}}else{$o='failed'}}catch{$o='off'};" +
        "[IO.File]::WriteAllText({0},$o)";

    /// <summary>The elevated step that makes one and writes its one-word answer to <paramref name="answer"/>.</summary>
    internal static string Step(string answer) => Step(answer, "Exo Launcher: before removing apps", "APPLICATION_UNINSTALL");

    /// <summary>
    /// The same step for another change: <paramref name="description"/> is the name Windows lists it by (a fixed text of
    /// Exo's own) and <paramref name="type"/> one of Windows' restore point types (MODIFY_SETTINGS for settings).
    /// </summary>
    internal static string Step(string answer, string description, string type) =>
        $"{LeftoverScan.PowerShell} -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes(Script
            .Replace("{0}", "'" + answer.Replace("'", "''") + "'", StringComparison.Ordinal)
            .Replace("{1}", description.Replace("'", "''"), StringComparison.Ordinal)
            .Replace("{2}", type is "MODIFY_SETTINGS" or "APPLICATION_UNINSTALL" ? type : "MODIFY_SETTINGS", StringComparison.Ordinal)));

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

    // When Exo last made one this run. Windows keeps at most one a day (SystemRestorePointCreationFrequency, 1440 minutes),
    // so within a day of it no prompt is spent on asking for another: that one stands.
    private static DateTime? _made;

    /// <summary>Exo made one within the last day (Windows wouldn't make another): it stands for this run too.</summary>
    internal static bool Standing(DateTime now, DateTime? made) => made is { } at && now >= at && now - at < TimeSpan.FromHours(24);

    internal static bool StandingNow => Standing(DateTime.UtcNow, _made);

    internal static void Noted(Outcome outcome, DateTime now) { if (outcome == Outcome.Made) _made = now; }

    /// <summary>
    /// <paramref name="steps"/> behind a restore point in one elevated run, so Windows asks once: the point's step first, and
    /// each step after it runs only when its answer says there is one to go back to (made, or one from the last day). Nothing
    /// of the rest runs when System Restore is off or the point failed.
    /// </summary>
    internal static List<string> Gated(string answer, IReadOnlyList<string> steps)
    {
        var quoted = answer.Replace("\"", "", StringComparison.Ordinal);
        return [Step(answer), .. steps.Select(step => $"findstr /x \"made recent\" \"{quoted}\" >nul && ({step})")];
    }

    /// <summary>
    /// Runs <paramref name="steps"/> as administrator behind a restore point (<see cref="Gated"/>), or as they are when Exo made
    /// one in the last day. Ran: Windows' prompt was accepted; Point: the restore point's outcome (null: the one made earlier stood).
    /// </summary>
    internal static async Task<(bool Ran, Outcome? Point)> BehindAsync(IReadOnlyList<string> steps, Func<IReadOnlyList<string>, Task<bool>> admin)
    {
        if (StandingNow) return (await admin(steps).ConfigureAwait(false), null);
        var answer = Path.Combine(Path.GetTempPath(), $"exo-{Guid.NewGuid():N}.restore");
        try
        {
            if (!await admin(Gated(answer, steps)).ConfigureAwait(false)) return (false, Outcome.Declined);
            var outcome = Read(File.Exists(answer) ? await File.ReadAllTextAsync(answer).ConfigureAwait(false) : null);
            Noted(outcome, DateTime.UtcNow);
            AppLog.Info($"Apps: restore point before a clear: {outcome}.");
            return (true, outcome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Apps: restore point answer unreadable: " + ex.GetType().Name);
            return (true, Outcome.Failed);
        }
        finally
        {
            if (File.Exists(answer)) DeletionGate.DeleteFile(answer);
        }
    }

    internal static async Task<Outcome> MakeAsync(Func<IReadOnlyList<string>, Task<bool>> admin)
    {
        if (HostMode.IsDisposable) return Outcome.Failed;
        if (StandingNow) return Outcome.Recent;
        var answer = Path.Combine(Path.GetTempPath(), $"exo-{Guid.NewGuid():N}.restore");
        try
        {
            if (!await admin([Step(answer)]).ConfigureAwait(false)) return Outcome.Declined;
            var outcome = Read(File.Exists(answer) ? await File.ReadAllTextAsync(answer).ConfigureAwait(false) : null);
            Noted(outcome, DateTime.UtcNow);
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

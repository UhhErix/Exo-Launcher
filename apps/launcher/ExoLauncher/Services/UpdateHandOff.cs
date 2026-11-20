using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Exo starting while one of its own updates is under way. A launch then must not start the old build mid-swap (its folder
/// is being renamed) and must not lose what it was started for: a text file to open in Notes (--note) or an exolauncher://
/// link. Those arguments are written to launch-handoff.json and the next Exo to start (the new build, which Setup opens)
/// takes them as if it had been started with them.
/// </summary>
internal static class UpdateHandOff
{
    /// <summary>Set by Setup and the installer's helper on the Exo they start: that start is the update's own, never held back.</summary>
    internal const string FromSetupVariable = "EXO_FROM_SETUP";
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);

    internal static string FilePath => Path.Combine(PathHelper.AppDataDir, "launch-handoff.json");

    internal enum Route { ToRunningExo, WaitForUpdate, Start }

    /// <summary>
    /// Where a launch goes: to the Exo already running (always, even while an update runs: a double-click brings it forward),
    /// else it waits for the update under way, else it starts. The update is not even looked for when Exo runs.
    /// </summary>
    internal static Route RouteOf(bool anotherExoRuns, Func<bool> updateUnderWay) =>
        anotherExoRuns ? Route.ToRunningExo : updateUnderWay() ? Route.WaitForUpdate : Route.Start;

    /// <summary>
    /// This launch waits out an update: Exo's Setup runs from the updates folder, and this is not the start that Setup or its
    /// helper made (the new build's health check, its rollback, its final open).
    /// </summary>
    internal static bool HoldsBack(IEnumerable<string?> runningImages, bool engineRunning, string updatesDirectory, string? healthNonce, string? fromSetup)
    {
        if (!string.IsNullOrEmpty(healthNonce) || fromSetup == "1") return false;
        if (engineRunning) return true;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(updatesDirectory));
        foreach (var image in runningImages)
        {
            if (string.IsNullOrEmpty(image)) continue;
            var name = Path.GetFileName(image);
            if (name.StartsWith("ExoLauncher-Setup-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(image)), folder, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Held by Exo's install engine while it runs (tools/ExoLauncher.nsi); Windows drops it when the engine exits, so it never goes stale.</summary>
    internal const string EngineMutex = @"Local\ExoLauncher.Installing";

    internal static bool EngineRunning(string name = EngineMutex)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>The images of the running Exo Setup processes (by their process name, then their real path).</summary>
    internal static IEnumerable<string?> RunningSetupImages()
    {
        foreach (var row in Helpers.ProcessSnapshot.Read())
        {
            if (row.Pid <= 4) continue;
            if (!Helpers.ProcessSnapshot.StripExe(row.Name).StartsWith("ExoLauncher-Setup-", StringComparison.OrdinalIgnoreCase)) continue;
            if (Adapters.ProcessHelper.QueryFullProcessImageName(row.Pid) is { } image) yield return image;
        }
    }

    /// <summary>Keeps the arguments a launch came with, when there is something in them to open; true when written.</summary>
    internal static bool Save(string path, string[] args, DateTimeOffset now)
    {
        if (NoteTextFiles.NoteArgument(args) is null && ExoLinks.Find(args) is null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { schema = 1, savedUtc = now, args }));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Update: what this launch was for could not be kept: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// The arguments to start with: these, or (when these have nothing to open) the ones a launch kept during the update, if
    /// they are fresh. The file goes either way.
    /// </summary>
    internal static string[] Take(string path, string[] args, DateTimeOffset now)
    {
        if (!File.Exists(path)) return args;
        string[]? kept = null;
        try
        {
            if (new FileInfo(path).Length <= 64 * 1024)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var saved = root.GetProperty("savedUtc").GetDateTimeOffset();
                if (root.GetProperty("schema").GetInt32() == 1 && saved <= now && now - saved < Freshness)
                    kept = [.. root.GetProperty("args").EnumerateArray().Select(arg => arg.GetString() ?? "")];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        try { DeletionGate.DeleteFile(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var ownHasSomething = NoteTextFiles.NoteArgument(args) is not null || ExoLinks.Find(args) is not null;
        return ownHasSomething || kept is null ? args : kept;
    }
}

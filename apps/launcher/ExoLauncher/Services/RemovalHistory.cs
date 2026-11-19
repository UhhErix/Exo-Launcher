using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>A file or folder Exo sent to the Recycle Bin, as it was before.</summary>
internal sealed record RemovedPath(string Path, bool Machine);

/// <summary>A scheduled task Exo deleted: its path, the program it ran, and the XML its definition was saved to first.</summary>
internal sealed record RemovedTask(string Path, string Exe, string File);

/// <summary>
/// One program's removal, or one clear of what programs left: what went and where to find it again. Files: to the Recycle
/// Bin. RegFiles: the .reg each hive's entries were saved to first. Keys: every key those may name (<see cref="RemovalHistory.RegStaysInside"/>).
/// Tasks: the scheduled tasks deleted, each saved first (<see cref="LeftoverScan.TaskStaysInside"/>); null in records written before.
/// </summary>
internal sealed record Removal(string Id, string Name, DateTime At, LeftoverSummary Cleared, List<RemovedPath> Files, List<string> RegFiles, List<string> Keys, bool Undone = false, bool Automatic = false, bool Told = false, List<RemovedTask>? Tasks = null);

/// <summary>What Put back managed: what came back, what couldn't (emptied from the bin, or something is there again).</summary>
internal sealed record PutBack(int Back, int Lost, bool Declined);

/// <summary>
/// PC › Apps' Removed list: what Exo removed from this PC and what it takes to put it back, kept in native code (apps.history
/// tells the page names, dates and how much). Put back moves a file out of the Recycle Bin by the bin's own record of where
/// it came from, imports the saved registry entries and registers the saved scheduled tasks again; only what an administrator
/// may touch asks Windows, in one prompt. It never overwrites: where something is there again, what was removed stays in the
/// bin, and a task of the same name that is there again stays as it is.
/// </summary>
internal sealed class RemovalHistory(string file, Func<string, IEnumerable<string>> bins)
{
    private const int Kept = 50;
    private readonly Lock _gate = new();

    internal static RemovalHistory ForThisPc { get; } = new(Path.Combine(PathHelper.AppDataDir, "removals.json"), BinsOf);

    /// <summary>This user's Recycle Bin on a path's drive.</summary>
    private static IEnumerable<string> BinsOf(string path)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        return Path.GetPathRoot(path) is { Length: > 0 } root && sid is not null ? [Path.Combine(root, "$Recycle.Bin", sid)] : [];
    }

    internal List<Removal> All()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(file) && JsonSerializer.Deserialize<List<Removal>>(File.ReadAllText(file)) is { } read) return read;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppLog.Debug("Removals: not read back: " + ex.GetType().Name); }
            return [];
        }
    }

    private void Save(List<Removal> all)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(all.Take(Kept).ToList()));
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Removals: not saved: " + ex.GetType().Name); }
        }
    }

    /// <summary>What just went, on the same record as this name's last one when that's within the hour (an uninstall, then its clear).</summary>
    internal string Add(string name, LeftoverSummary cleared, IEnumerable<RemovedPath> files, IEnumerable<string> regFiles, IEnumerable<string> keys, DateTime now, bool automatic = false,
        IEnumerable<RemovedTask>? tasks = null)
    {
        if (HostMode.IsDisposable) return "";
        lock (_gate)
        {
            var all = All();
            var last = all.FirstOrDefault(record => record.Name == name && !record.Undone && now - record.At < TimeSpan.FromHours(1));
            if (last is not null) all.Remove(last);
            List<RemovedTask> saved = [.. tasks ?? []];
            var record = last is null
                ? new Removal(Guid.NewGuid().ToString("N"), name, now, cleared, [.. files], [.. regFiles], [.. keys], Automatic: automatic, Tasks: saved)
                : last with
                {
                    At = now,
                    Cleared = new(last.Cleared.Folders + cleared.Folders, last.Cleared.Files + cleared.Files, last.Cleared.Registry + cleared.Registry, last.Cleared.Shortcuts + cleared.Shortcuts, last.Cleared.Bytes + cleared.Bytes),
                    Files = [.. last.Files, .. files],
                    RegFiles = [.. last.RegFiles, .. regFiles],
                    Keys = [.. last.Keys, .. keys],
                    Tasks = [.. last.Tasks ?? [], .. saved],
                    Told = false,
                };
            all.Insert(0, record);
            Save(all);
            return record.Id;
        }
    }

    /// <summary>What went by itself and the page hasn't said yet: said once, then only in Removed.</summary>
    internal List<Removal> NewlyCleared()
    {
        if (HostMode.IsDisposable) return [];
        lock (_gate)
        {
            var all = All();
            var newly = all.Where(record => record.Automatic && !record.Told && !record.Undone && record.Cleared.Count > 0).ToList();
            if (newly.Count > 0) Save(all.Select(record => newly.Contains(record) ? record with { Told = true } : record).ToList());
            return newly;
        }
    }

    /// <summary>Whether anything of it can still come back: a saved .reg, or a file still in the bin (read once, for all the records asked about).</summary>
    internal bool CanPutBack(Removal record, IReadOnlyDictionary<string, RecycleEntry> bin) => !record.Undone &&
        (record.RegFiles.Any(File.Exists) || record.Files.Any(item => bin.ContainsKey(item.Path)) || (record.Tasks ?? []).Any(task => File.Exists(task.File)));

    /// <summary>
    /// Everything of a removal that can come back. <paramref name="import"/> brings back a .reg this user may;
    /// <paramref name="admin"/> runs the elevated steps as one prompt, false when it was declined; <paramref name="taskThere"/>
    /// says whether a scheduled task is there (null asks Task Scheduler).
    /// </summary>
    internal async Task<PutBack?> UndoAsync(string id, Func<string, bool> import, Func<IReadOnlyList<string>, Task<bool>> admin, Func<string, bool>? taskThere = null)
    {
        taskThere ??= LeftoverScan.TaskExists;
        if (HostMode.IsDisposable) return null;
        if (All().FirstOrDefault(record => record.Id == id) is not { Undone: false } removal) return null;
        var back = 0;
        var lost = 0;
        var steps = new List<string>();
        var waiting = new List<RecycleEntry>();
        var bin = Bin(removal.Files.Select(item => item.Path));
        foreach (var item in removal.Files)
        {
            if (!bin.TryGetValue(item.Path, out var entry) || Directory.Exists(item.Path) || File.Exists(item.Path)) { lost++; continue; }
            if (item.Machine)
            {
                if (AdminMove(entry) is { } step) { steps.AddRange(step); waiting.Add(entry); }
                else lost++;
            }
            else if (MoveBack(entry)) back++;
            else lost++;
        }
        foreach (var reg in removal.RegFiles)
        {
            if (!File.Exists(reg) || !RegStaysInside(File.ReadAllText(reg), removal.Keys) || reg.Any(c => char.IsControl(c) || c is '"' or '%' or '!' or '^')) { lost++; continue; }
            if (File.ReadAllText(reg).Contains("[HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)) steps.Add($"reg import \"{reg}\"");
            else if (import(reg)) back++;
            else lost++;
        }
        // A task comes back from its saved XML when it still names only the program it ran and nothing of its name is there again.
        var tasks = new List<RemovedTask>();
        foreach (var task in removal.Tasks ?? [])
        {
            byte[] bytes;
            try { bytes = File.Exists(task.File) && new FileInfo(task.File).Length < 1 << 20 ? File.ReadAllBytes(task.File) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { bytes = []; }
            if (bytes.Length == 0 || taskThere(task.Path) || !LeftoverScan.TaskStaysInside(Encoding.Unicode.GetString(bytes).TrimStart('﻿'), task.Exe) ||
                LeftoverScan.TaskImportStep(task.Path, task.File, bytes) is not { } step) { lost++; continue; }
            steps.Add(step);
            tasks.Add(task);
        }
        var declined = false;
        if (steps.Count > 0)
        {
            if (await admin(steps).ConfigureAwait(false))
            {
                back += waiting.Count(entry => Directory.Exists(entry.Original) || File.Exists(entry.Original));
                lost += waiting.Count(entry => !Directory.Exists(entry.Original) && !File.Exists(entry.Original));
                back += steps.Count(step => step.StartsWith("reg import", StringComparison.Ordinal));
                back += tasks.Count(task => taskThere(task.Path));
                lost += tasks.Count(task => !taskThere(task.Path));
            }
            else declined = true;
        }
        if (!declined) Save(All().Select(record => record.Id == id ? record with { Undone = true } : record).ToList());
        AppLog.Info($"Removals: put back {back} of what {removal.Name} lost; {lost} couldn't.");
        return new PutBack(back, lost, declined);
    }

    /// <summary>
    /// Whether a saved .reg still names only what was removed: it sits in a folder this user can write to, and an administrator
    /// imports HKLM's, so one changed since (a key elsewhere, a deletion) is refused rather than trusted.
    /// </summary>
    internal static bool RegStaysInside(string text, IReadOnlyCollection<string> keys)
    {
        var allowed = keys.Select(Written).ToList();
        foreach (var line in text.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (!line.StartsWith('[')) continue;
            if (!line.EndsWith(']')) return false;
            var key = line[1..^1];
            if (key.StartsWith('-') || !allowed.Any(root => key.Equals(root, StringComparison.OrdinalIgnoreCase) || key.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;

        static string Written(string key) => key.StartsWith("HKLM", StringComparison.Ordinal) ? "HKEY_LOCAL_MACHINE" + key[4..] : "HKEY_CURRENT_USER" + key[4..];
    }

    // ---- the Recycle Bin's own records ----

    /// <summary>A thing in the bin: its record ($I), what it holds ($R) and where it came from.</summary>
    internal sealed record RecycleEntry(string Info, string Held, string Original, DateTime At);

    /// <summary>
    /// A $I record: version (8 bytes), size (8), when it was deleted (8), then the original path: version 2, a count of
    /// characters and that many; version 1 (Windows 8 and before), a fixed 260. Null for anything else.
    /// </summary>
    internal static (string Path, DateTime At)? ReadInfo(byte[] info)
    {
        if (info.Length < 28) return null;
        var version = BitConverter.ToInt64(info, 0);
        var time = BitConverter.ToInt64(info, 16);
        var at = time > 0 && time < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(time) : default;
        string? path = null;
        if (version == 2)
        {
            var count = BitConverter.ToInt32(info, 24);
            if (count > 0 && count < 32768 && 28 + count * 2 <= info.Length) path = Encoding.Unicode.GetString(info, 28, count * 2);
        }
        else if (version == 1) path = Encoding.Unicode.GetString(info, 24, Math.Min(520, info.Length - 24));
        path = path?.Split('\0')[0];
        return string.IsNullOrEmpty(path) ? null : (path, at);
    }

    /// <summary>What the bins of these paths' drives hold, by where each came from: the newest of each, with what it holds still there.</summary>
    internal Dictionary<string, RecycleEntry> Bin(IEnumerable<string> paths)
    {
        var found = new Dictionary<string, RecycleEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var bin in paths.SelectMany(bins).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(bin)) continue;
                foreach (var info in Directory.EnumerateFiles(bin, "$I*"))
                {
                    if (new FileInfo(info).Length > 66_000 || ReadInfo(File.ReadAllBytes(info)) is not { } read) continue;
                    var held = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
                    if ((Directory.Exists(held) || File.Exists(held)) && (!found.TryGetValue(read.Path, out var known) || read.At > known.At)) found[read.Path] = new(info, held, read.Path, read.At);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Removals: a bin wasn't readable: " + ex.GetType().Name); }
        }
        return found;
    }

    private static bool MoveBack(RecycleEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Original)!);
            if (Directory.Exists(entry.Held)) Directory.Move(entry.Held, entry.Original);
            else File.Move(entry.Held, entry.Original);
            DeletionGate.DeleteFile(entry.Info);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Removals: one stayed in the bin: " + ex.GetType().Name);
            return false;
        }
    }

    // Quoted as AdminCommand quotes: what quotes don't hold is refused.
    private static string[]? AdminMove(RecycleEntry entry)
    {
        var parent = Path.GetDirectoryName(entry.Original);
        if (parent is null || $"{entry.Info}{entry.Held}{entry.Original}".Any(c => char.IsControl(c) || c is '"' or '%' or '!' or '^')) return null;
        return [$"(if not exist \"{parent}\" mkdir \"{parent}\")", $"move /y \"{entry.Held}\" \"{entry.Original}\"", $"del /f /q \"{entry.Info}\""];
    }
}

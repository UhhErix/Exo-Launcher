using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// System › Apps, Exo's uninstaller. apps.list: every program on this PC (<see cref="InstalledPrograms"/>), the last
/// list at once and read again behind it (<see cref="AnswerCache"/>, apps.changed); apps.icons: their icons, kept by
/// file (apps-icons.json); apps.uninstall: one removed with its own uninstaller (or Windows', for a Store app), waited out to the end
/// (<see cref="UninstallRun"/>), then the deep clean's find (<see cref="LeftoverScan"/>) handed to the page to look at;
/// apps.scan: the same find for a program whose uninstaller is gone (Revo's forced uninstall); apps.clean: what the
/// user chose of a find deleted, and written down so apps.undo can put it back; apps.tidy: what still waits for a press from
/// programs already gone (<see cref="AppsOrphans"/>); apps.history: what Exo removed. After an uninstall here, what it left goes by itself (<see cref="AutoClear"/>). A find stays on this side under a token: the page only names which of its items go, so
/// nothing the page says becomes a path, and it sees a place's name in place of a user's folder. Removing and deleting are each asked in Exo's own window; apps.confirm asks once
/// for several apps and hands back a pass that apps.uninstall takes in place of asking each one again.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly SemaphoreSlim AppsGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, (string Name, List<Leftover> Items, DateTime At, List<string> Remembered)> Finds = new();

    // apps.list's answer (AnswerCache): the last one at once, from memory or apps-list.json after a restart, while a read
    // behind it refreshes it; a read that differs tells the page (apps.changed). An uninstall or a clean here makes the
    // next answer a fresh read (AppsChangedHere), so the page sees an app go the moment it's gone.
    private AnswerCache? _appsAnswer;
    private Action? _appsAnswerChangedHandler;

    private AnswerCache AppsAnswer => LazyInitializer.EnsureInitialized(ref _appsAnswer, () =>
    {
        var cache = new AnswerCache(Path.Combine(PathHelper.AppDataDir, "apps-list.json"), _services.AppVersion,
            () => JsonSerializer.Serialize(AppsRead(), JsonOpts), TimeSpan.FromSeconds(15));
        _appsAnswerChangedHandler = () => PostEvent("apps.changed", new { });
        cache.Changed += _appsAnswerChangedHandler;
        return cache;
    });

    private void DetachAppsCache()
    {
        if (_appsAnswer is { } cache && _appsAnswerChangedHandler is { } handler)
            cache.Changed -= handler;
        _appsAnswerChangedHandler = null;
    }

    private object AppsList()
    {
        using var answer = JsonDocument.Parse(AppsAnswer.Get());
        return answer.RootElement.Clone();
    }

    /// <summary>Programs were removed or cleaned here: the next apps.list reads them again.</summary>
    private void AppsChangedHere() => AppsAnswer.Invalidate();

    private AppsOrphans Orphans => LazyInitializer.EnsureInitialized(ref _orphans, () => new AppsOrphans(_services));
    private AppsOrphans? _orphans;

    private sealed record ProgramsRead(List<InstalledProgram> Programs, DateTime At);

    // The newest read of the programs (apps.list's), which apps.icons finds icons in rather than reading them again.
    private static ProgramsRead? _lastPrograms;

    private object AppsRead()
    {
        var programs = Orphans.Programs();
        // Sizes the entries don't give, measured once (a few seconds at most), then the list again with them.
        if (InstalledPrograms.Measure(programs, TimeSpan.FromSeconds(3))) programs = Orphans.Programs();
        Volatile.Write(ref _lastPrograms, new ProgramsRead(programs, DateTime.UtcNow));
        var needs = Orphans.KeepNeeds();
        var runs = AppsUsage.Read();
        AppsMemory.ForThisPc.Note(programs, AppsOrphans.ExesOf);
        return new
        {
            ok = true, programs = programs.Select(program => Shown(program, needs, runs)),
            // Edge is gone and Edge Update is told not to bring it back: Allow Edge again takes that back.
            edgeRemoved = EdgeRemoval.Blocked() && !programs.Any(EdgeRemoval.IsEdge),
        };
    }

    /// <summary>One program as the page lists it: why it stays (<see cref="AppsKeep"/>), why it's bloatware, when it was last opened.</summary>
    private static object Shown(InstalledProgram program, AppsKeep.Needs needs, AppsUsage.Runs runs)
    {
        var kept = AppsKeep.Why(program, needs);
        return new
        {
            id = program.Id,
            name = program.Name,
            publisher = program.Publisher,
            version = program.Version,
            installedOn = program.InstalledOn,
            bytes = program.Bytes is > 0 ? program.Bytes : null,
            store = program.Store,
            removable = program.Removable && kept is null,
            kept,
            bloat = kept is null ? AppsBloat.Why(program) : null,
            lastOpened = AppsUsage.LastOpened(program, runs)?.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            quietSince = runs.Paths.Count > 0 && AppsUsage.LastOpened(program, runs) is null && AppsMemory.ForThisPc.WatchedSince(program.Id) is { } watched
                && DateTime.UtcNow - watched > TimeSpan.FromDays(90) ? watched.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
            uninstaller = program.Store || program.Uninstall is not null && UninstallerThere(program.Uninstall),
            care = program.Care,
            // What uninstalling it costs, on its row's detail line, so the in-place "Uninstall?" needs no more words.
            caution = Warning(program).Trim() is { Length: > 0 } caution ? caution : null,
            replaces = program.Replaces,
        };
    }

    // Its uninstaller is on this PC: Windows Installer's (an MSI's command names MsiExec, which is always there), or the file
    // its command starts.
    internal static bool UninstallerThere(string command)
    {
        var text = command.TrimStart().TrimStart('"');
        return text.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase) || text.Contains(@"\msiexec", StringComparison.OrdinalIgnoreCase) || Rooted(command) is not null;
    }

    /// <summary>
    /// The command with its program's full path: a bare name (rundll32.exe advpack.dll,LaunchINFSection …, an INF-installed
    /// runtime's; Prounstl.exe, Intel's) is Windows' own, looked for only in System32 and the Windows folder, never on PATH.
    /// Null when its program isn't there.
    /// </summary>
    private static string? Rooted(string command)
    {
        var (file, arguments) = Adapters.OfficialInstalledLibraries.SplitCommand(command);
        file = Environment.ExpandEnvironmentVariables(file);
        if (Path.IsPathFullyQualified(file)) return File.Exists(file) ? command : null;
        if (file.Length == 0 || file.IndexOfAny(['\\', '/']) >= 0) return null;
        foreach (var folder in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            var full = Path.Combine(folder, file);
            if (!File.Exists(full) && File.Exists(full + ".exe")) full += ".exe";
            if (File.Exists(full)) return $"\"{full}\" {arguments}".TrimEnd();
        }
        return null;
    }

    // apps.icons: each program's icon file, found once a run (IconFiles; null: it has none), and each file's picture by its
    // path and last write (IconPictures), kept in apps-icons.json so the icons show at once after a restart too.
    private static readonly ConcurrentDictionary<string, string?> IconFiles = new(StringComparer.Ordinal);
    private static readonly Lazy<ConcurrentDictionary<string, string?>> IconPictures = new(LoadIconPictures);
    private static readonly ConcurrentDictionary<string, byte> IconsShown = new(StringComparer.OrdinalIgnoreCase);
    private static string IconPicturesPath => Path.Combine(PathHelper.AppDataDir, "apps-icons.json");

    private static async Task<object> AppsIconsAsync(JsonElement p, bool hasParams)
    {
        var ids = hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!).Take(400).ToHashSet() : [];
        var unknown = ids.Where(id => !IconFiles.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        if (unknown.Count > 0)
        {
            // apps.list's read of the programs when it is recent, else one of its own.
            var all = Volatile.Read(ref _lastPrograms) is { } known && DateTime.UtcNow - known.At < TimeSpan.FromMinutes(5) && unknown.All(id => known.Programs.Any(program => program.Id == id))
                ? known.Programs : InstalledPrograms.List();
            // An entry that names no icon gets one the way Installed apps finds it (ProgramIcons); the Start menu is read once.
            var menu = new Lazy<ProgramIcons.StartMenu>(() => new ProgramIcons.StartMenu());
            foreach (var program in all.Where(program => unknown.Contains(program.Id)))
                IconFiles[program.Id] = program.Icon ?? ProgramIcons.Find(program, all, menu.Value);
        }
        var files = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!IconFiles.TryGetValue(id, out var file)) continue;
            var key = file is null ? null : IconKey(file);
            // Its icon file is gone (an update moved it): found again next time.
            if (file is not null && key is null) IconFiles.TryRemove(id, out _);
            files[id] = key;
        }
        var pictures = IconPictures.Value;
        var made = 0;
        // Pictures not kept yet, made side by side by Windows' thumbnailer.
        await Task.WhenAll(files.Values.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(key => !pictures.ContainsKey(key)).Select(async key =>
        {
            // A read that found nothing (the file busy mid-install, Windows' thumbnailer away) is tried again next time, never kept.
            if (await IconAsync(key[..key.LastIndexOf('|')]).ConfigureAwait(false) is not { } picture) return;
            pictures[key] = picture;
            Interlocked.Increment(ref made);
        })).ConfigureAwait(false);
        var icons = new Dictionary<string, string?>();
        foreach (var (id, key) in files)
        {
            if (key is null) { icons[id] = null; continue; }
            IconsShown[key] = 0;
            icons[id] = pictures.GetValueOrDefault(key);
        }
        if (made > 0) SaveIconPictures(pictures);
        return new { ok = true, icons };
    }

    /// <summary>An icon file's key, its path and last write (an update draws its new icon); null when the file is gone.</summary>
    private static string? IconKey(string file)
    {
        try { return StoreLogo(file) is var drawn && File.Exists(drawn) ? $"{file}|{File.GetLastWriteTimeUtc(drawn).Ticks}" : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static ConcurrentDictionary<string, string?> LoadIconPictures()
    {
        try
        {
            if (File.Exists(IconPicturesPath) && new FileInfo(IconPicturesPath).Length < 16 << 20 &&
                JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(IconPicturesPath)) is { } saved)
                return new ConcurrentDictionary<string, string?>(saved.Where(pair => pair.Value?.StartsWith("data:image/", StringComparison.Ordinal) == true), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppLog.Debug("Apps: icons not read back: " + ex.GetType().Name); }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    // Written whole; past 400 pictures, only those shown this run are kept (the rest are of apps gone or updated).
    private static void SaveIconPictures(ConcurrentDictionary<string, string?> pictures)
    {
        try
        {
            var kept = pictures.Count > 400 ? pictures.Where(pair => IconsShown.ContainsKey(pair.Key)) : pictures;
            File.WriteAllText(IconPicturesPath + ".tmp", JsonSerializer.Serialize(kept.ToDictionary(pair => pair.Key, pair => pair.Value)));
            File.Move(IconPicturesPath + ".tmp", IconPicturesPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Apps: icons not saved: " + ex.Message); }
    }

    /// <summary>A program's icon at 32px as a data URL: Windows' own thumbnail of its icon file (an .exe's, an .ico, a Store logo).</summary>
    private static async Task<string?> IconAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(StoreLogo(path));
            using var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 32, Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Size == 0 || thumbnail.Size > 512 * 1024) return null;
            var bytes = new byte[thumbnail.Size];
            using var reader = new Windows.Storage.Streams.DataReader(thumbnail.GetInputStreamAt(0));
            await reader.LoadAsync((uint)thumbnail.Size);
            reader.ReadBytes(bytes);
            return $"data:{(string.IsNullOrEmpty(thumbnail.ContentType) ? "image/png" : thumbnail.ContentType)};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    // A Store logo is named without its scale: the file on disk is Logo.scale-100.png or Logo.targetsize-32.png.
    private static string StoreLogo(string path)
    {
        if (File.Exists(path) || Path.GetDirectoryName(path) is not { } folder || !Directory.Exists(folder)) return path;
        var stem = Path.GetFileNameWithoutExtension(path);
        return Directory.EnumerateFiles(folder, stem + ".*" + Path.GetExtension(path))
            .OrderBy(file => file.Contains("targetsize-32", StringComparison.OrdinalIgnoreCase) ? 0 : file.Contains("scale-100", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .FirstOrDefault() ?? path;
    }

    /// <summary>apps.confirm: of several apps chosen to uninstall one after another (asked once, in place on the page), the ones that can go.</summary>
    private async Task<object> AppsConfirmAsync(JsonElement p, bool hasParams)
    {
        var ids = hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!).Take(50).ToHashSet() : [];
        // Every installed game's runtimes when its 5-minute cache has run out: off the UI thread, as below.
        var needs = await Task.Run(Orphans.KeepNeeds).ConfigureAwait(true);
        var programs = await Task.Run(InstalledPrograms.List).ConfigureAwait(true);
        var apps = new List<InstalledProgram>();
        var skipped = new List<object>();
        foreach (var program in programs.Where(program => ids.Contains(program.Id)))
        {
            NoteProgramName(program);
            if (ConfirmReason(program, needs) is not { } reason) { apps.Add(program); continue; }
            AppLog.Info($"Apps: not uninstalled {program.Name}: {reason}");
            skipped.Add(new { id = program.Id, name = program.Name, reason });
        }
        foreach (var id in ids.Where(id => !programs.Any(program => program.Id == id)))
        {
            AppLog.Info($"Apps: not uninstalled {id}: it isn't on this PC anymore.");
            skipped.Add(new { id, name = id, reason = "That app isn't on this PC anymore." });
        }
        if (apps.Count == 0) return new { ok = false, message = "None of those can be uninstalled here.", skipped };
        // The one press for the run: each of these may then go quietly (QuietUninstall), once, within the hour.
        foreach (var old in QuietPass.Where(pair => DateTime.UtcNow - pair.Value > QuietPassFor).Select(pair => pair.Key).ToList()) QuietPass.TryRemove(old, out _);
        foreach (var app in apps) QuietPass[app.Id] = DateTime.UtcNow;
        return new { ok = true, ids = apps.Select(app => app.Id).ToArray(), skipped };
    }

    // apps.confirm's pass: the apps confirmed together, by id, when; apps.uninstall takes one quietly only while it holds.
    private static readonly ConcurrentDictionary<string, DateTime> QuietPass = new(StringComparer.Ordinal);
    private static readonly TimeSpan QuietPassFor = TimeSpan.FromHours(1);

    /// <summary>The command a confirmed run uses for <paramref name="program"/>: its quiet one where its installer has one, else its own.</summary>
    internal static (string Command, QuietUninstall.Kind Kind) UninstallCommand(InstalledProgram program, bool quiet, Func<string, QuietUninstall.Kind> marker)
    {
        var plain = program.Uninstall!;
        if (!quiet) return (plain, QuietUninstall.Kind.Unknown);
        var entry = new QuietUninstall.Entry(program.Key?[(program.Key.LastIndexOf('\\') + 1)..] ?? "", plain, program.Quiet, program.ProductCode, program.Bundle);
        return QuietUninstall.Command(entry, marker) is { } command ? (command, QuietUninstall.Detect(entry, marker)) : (plain, QuietUninstall.Kind.Unknown);
    }

    /// <summary>apps.restorePoint: a System Restore point before a run, under Windows' prompt; ok only when there is one to go back to.</summary>
    private static async Task<object> AppsRestorePointAsync(CancellationToken ct)
    {
        var outcome = await RestorePoint.MakeAsync(steps => ReplacedApps.RemoveAsAdminAsync(steps, ct)).ConfigureAwait(true);
        return RestorePoint.Problem(outcome) is { } problem
            ? new { ok = false, made = false, declined = outcome == RestorePoint.Outcome.Declined, message = (string?)problem }
            : new { ok = true, made = outcome == RestorePoint.Outcome.Made, declined = false, message = outcome == RestorePoint.Outcome.Recent ? "Windows keeps one restore point a day: the one from the last day stands." : null };
    }

    /// <summary>apps.openDefaults: Windows' Default apps, where a file type an app left pointing at itself gets a new default (Windows signs that choice; Exo never writes it).</summary>
    private static object AppsOpenDefaults()
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
            return new { ok = true, message = (string?)null };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Debug("Apps: Default apps didn't open: " + ex.GetType().Name);
            return new { ok = false, message = (string?)"Windows' Default apps didn't open." };
        }
    }

    /// <summary>apps.installerCache: only reads. How many of Windows Installer's cached copies no product or patch needs, and how big.</summary>
    private static async Task<object> AppsInstallerCacheAsync()
    {
        var found = await Task.Run(InstallerCache.Find).ConfigureAwait(true);
        return new { ok = true, files = found.Count, bytes = found.Sum(file => file.Bytes) };
    }

    /// <summary>apps.installerCacheClean: those, found again now, to the Recycle Bin under one Windows prompt, with Put back.</summary>
    private async Task<object> AppsInstallerCacheCleanAsync(CancellationToken ct)
    {
        var done = await InstallerCache.ClearAsync(steps => ReplacedApps.RemoveAsAdminAsync(steps, ct), RemovalHistory.ForThisPc).ConfigureAwait(true);
        AppsChangedHere();
        if (done.Declined) return new { ok = false, files = 0, bytes = 0L, undo = (string?)null, message = "Windows' permission was declined: nothing was moved." };
        return new
        {
            ok = done.Left == 0, files = done.Files, bytes = done.Bytes, undo = done.Undo,
            message = done.Left == 0 ? null : $"{done.Left} {(done.Left == 1 ? "file" : "files")} stayed: in use, or too big for the Recycle Bin.",
        };
    }

    /// <summary>
    /// Registers a program's name before any log line names it (confirm, uninstall, scan), so a crash excerpt never carries
    /// it: CrashReport replaces each registered name with &lt;name&gt; (ReportNames).
    /// </summary>
    internal static void NoteProgramName(InstalledProgram program) => ReportNames.Add(program.Name);

    /// <summary>
    /// Why apps.confirm leaves <paramref name="program"/> out, in the words the page shows beside it; null when it can go.
    /// Each check here is one a removal would also make, so a removal never does what the confirm refused.
    /// </summary>
    internal static string? ConfirmReason(InstalledProgram program, AppsKeep.Needs needs)
    {
        if (!program.Removable) return Why(program);
        if (AppsKeep.Why(program, needs) is { } kept) return kept + ": it stays.";
        if (program.Replaces is not null) return "Exo replaces it: Clean up removes it, not an uninstall.";
        if (!program.Store && !(program.Uninstall is { } command && UninstallerThere(command))) return "Its uninstaller is gone: clear what it left instead.";
        return null;
    }

    /// <param name="quiet">The page runs several confirmed together (apps.confirm): taken only while that pass holds for this one.</param>
    private async Task<object> AppsUninstallAsync(string id, bool quiet, CancellationToken ct)
    {
        if (!await AppsGate.WaitAsync(0, ct).ConfigureAwait(false)) return new { ok = false, message = "Another app is being removed." };
        try
        {
            if (await Task.Run(() => InstalledPrograms.Find(id), ct).ConfigureAwait(true) is not { } program) return new { ok = false, message = "That app isn't on this PC anymore." };
            NoteProgramName(program);
            if (!program.Removable) return new { ok = false, message = Why(program) };
            if (AppsKeep.Why(program, await Task.Run(Orphans.KeepNeeds, ct).ConfigureAwait(true)) is { } kept) return new { ok = false, message = kept + ": it stays." };
            var edge = EdgeRemoval.IsEdge(program);
            var passed = quiet && QuietPass.TryRemove(program.Id, out var at) && DateTime.UtcNow - at < QuietPassFor;
            // Asked in place on the page (DESIGN.md › Confirm); the uninstaller's own window, or Windows' prompt, follows; in a
            // confirmed run, its quiet switches where its installer has them (Windows' prompt still asks for rights).
            var (command, kind) = program.Store || edge ? ("", QuietUninstall.Kind.Unknown)
                : await Task.Run(() => UninstallCommand(program, passed, QuietUninstall.Marker), ct).ConfigureAwait(true);
            var trail = await Task.Run(() => Orphans.TrailFor(program), ct).ConfigureAwait(true);
            AppsMemory.ForThisPc.Forget([program.Id]);
            AppLog.Info($"Apps: uninstalling {program.Name} ({(program.Store ? "Store" : program.ProductCode is null ? "own uninstaller" : "MSI")}{(passed ? $", quiet: {QuietUninstall.Name(kind)}" : "")}).");
            return await AutoClear.AfterUninstallAsync<object>(
                async () =>
                {
                    if (program.Store) return await new WindowsAppPackages().RemoveAsync(program.PackageFullName!, ct).ConfigureAwait(true) ? null : "Windows didn't remove it.";
                    return edge ? await EdgeRemoval.RunAsync(program, ct).ConfigureAwait(true) : await UninstallRun.RunAsync(Rooted(command) ?? command, TimeSpan.FromMinutes(60), ct).ConfigureAwait(true);
                },
                () => Task.Run(() => StillThere(program), ct),
                () => Task.Delay(TimeSpan.FromSeconds(3), ct),
                30,
                ended =>
                {
                    RemovalHistory.ForThisPc.Add(program.Name, new(0, 0, 0, 0, 0), [], [], [], DateTime.UtcNow);
                    // Something went wrong, even though it's gone: only found, for a press.
                    return ended.MayClear ? ClearWhatItLeftAsync(program.Name, trail, ct) : Task.Run(() => Found(program.Name, trail), ct);
                },
                ended =>
                {
                    AppLog.Info($"Apps: {program.Name} is still installed{(ended.Problem is null ? "" : $" ({ended.Problem})")}.");
                    return new { ok = false, still = true, message = ended.Problem ?? $"{program.Name} is still installed: its uninstaller was closed before it finished." };
                }).ConfigureAwait(true);
        }
        finally
        {
            AppsChangedHere();
            AppsGate.Release();
        }
    }

    /// <summary>
    /// junk.scan: the junk on this PC by kind (<see cref="JunkFiles"/>), with sizes; auto: Exo cleans that kind by itself
    /// (<see cref="JunkAutoClean"/>), cleaned: its last clean. A lot of it found makes the next idle moment clean.
    /// </summary>
    // Onboarding and Apps both ask when they open, sometimes at once (a scan took 14 s and 9 s in one second): one walk answers both.
    private static readonly Lock JunkScanGate = new();
    private static Task<List<JunkFiles.Found>>? _junkScanning;

    private static Task<List<JunkFiles.Found>> JunkScanShared()
    {
        lock (JunkScanGate) return _junkScanning is { IsCompleted: false } going ? going : _junkScanning = Task.Run(JunkFiles.Scan);
    }

    private static async Task<object> JunkScanAsync()
    {
        var found = await JunkScanShared().ConfigureAwait(true);
        JunkAutoClean.Scanned(found);
        var last = JunkAutoClean.Latest();
        return new
        {
            ok = true,
            items = found.Select(item => new { id = item.Id, name = item.Name, detail = item.Detail, bytes = item.Bytes, files = item.Files, auto = JunkAutoClean.Auto(item.Id) }),
            cleaned = last is null ? null : new { at = last.At.ToString("o", System.Globalization.CultureInfo.InvariantCulture), bytes = last.Bytes },
        };
    }

    /// <summary>junk.clean: the chosen kinds deleted (asked in place on the page: it doesn't go to the Recycle Bin); what went.</summary>
    private static async Task<object> JunkCleanAsync(JsonElement p, bool hasParams)
    {
        var ids = hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!).Take(10).ToHashSet() : [];
        var found = (await Task.Run(JunkFiles.Scan).ConfigureAwait(true)).Where(item => ids.Contains(item.Id)).ToList();
        if (found.Count == 0) return new { ok = true, bytes = 0L, files = 0, message = (string?)null };
        var what = string.Join(", ", found.Select(item => item.Name.ToLowerInvariant()));
        long bytes = 0;
        var files = 0;
        var failed = new List<string>();
        foreach (var item in found)
        {
            // Freed is the measured change in free bytes on the system drive, never the sizes added up.
            var (count, freed) = await Task.Run(() =>
            {
                var (cleaned, measured) = DeletionGate.Measure(PathHelper.AppDataDir, () => JunkFiles.Clean(item.Id, failed));
                return (cleaned.Files, measured);
            }).ConfigureAwait(true);
            bytes += freed;
            files += count;
        }
        lock (JunkScanGate) _junkScanning = null; // a walk begun before this is out of date
        AppLog.Info($"Junk: deleted {files} files ({bytes / 1_000_000} MB) of {what}.");
        return new { ok = true, bytes, files, message = KeptNote(failed.Count, await JunkHeldWithinAsync(failed)) };
    }

    /// <summary>
    /// <see cref="JunkHeld"/> off the UI thread, for at most <see cref="JunkHeldCap"/>: Restart Manager can be slow, and the
    /// clean answers without its names when it is (the walk still logs them when it finishes).
    /// </summary>
    internal static readonly TimeSpan JunkHeldCap = TimeSpan.FromSeconds(2);

    /// <summary>The line under a partial clean: how many files stayed, and who holds them (null when none stayed).</summary>
    internal static string? KeptNote(int kept, string? held) => kept switch
    {
        <= 0 => null,
        1 => "1 file kept." + (held is null ? "" : " " + held),
        _ => kept + " files kept." + (held is null ? "" : " " + held),
    };

    internal static Task<string?> JunkHeldWithinAsync(List<string> failed) => HeldWithinAsync(() => JunkHeld(failed), JunkHeldCap);

    /// <summary>The answer of <paramref name="held"/> run on the pool, or null when it is not back within <paramref name="cap"/>.</summary>
    internal static async Task<string?> HeldWithinAsync(Func<string?> held, TimeSpan cap)
    {
        var work = Task.Run(held);
        try { return await work.WaitAsync(cap).ConfigureAwait(true); }
        catch (TimeoutException) { AppLog.Debug("Junk: holders not named within " + cap.TotalSeconds + " s."); return null; }
    }

    /// <summary>
    /// The files a clean couldn't delete and who holds them, for the page's "in use" line: Restart Manager names the
    /// programs (bounded: the first few paths asked, <see cref="LockedFiles.MaxNamed"/> names kept). One Info line per clean (never a Warn: a crash report would carry it)
    /// with how many stayed, their names (never their paths) and the holders; null when nothing stayed.
    /// </summary>
    private static string? JunkHeld(List<string> failed)
    {
        if (failed.Count == 0) return null;
        var holders = new List<LockedFiles.Holder>();
        foreach (var path in failed.Take(10))
        {
            foreach (var holder in LockedFiles.Holders(path))
                if (!holders.Any(h => h.Pid == holder.Pid)) holders.Add(holder);
            if (holders.Count >= LockedFiles.MaxNamed) break;
        }
        var names = string.Join(", ", failed.Take(5).Select(path => Path.GetFileName(path)));
        var message = LockedFiles.Describe("Those files are", holders);
        AppLog.Info($"Junk: {failed.Count} files stayed ({names}).{(message is null ? "" : " " + message)}");
        return message;
    }

    /// <summary>apps.allowEdge: Edge Update may install Edge again (Exo told it not to when Edge was removed here). Removing Edge again blocks it again: no confirm.</summary>
    private async Task<object> AppsAllowEdgeAsync(CancellationToken ct)
    {
        if (!EdgeRemoval.Blocked()) return new { ok = true };
        var allowed = await EdgeRemoval.AllowAgainAsync(ct).ConfigureAwait(true) && !EdgeRemoval.Blocked();
        AppsChangedHere();
        return allowed ? new { ok = true } : new { ok = false, message = "Windows didn't allow it." };
    }

    /// <summary>A program whose uninstaller is gone: what is left of it, found as after an uninstall (its folder included).</summary>
    private async Task<object> AppsScanAsync(string id, CancellationToken ct)
    {
        if (await Task.Run(() => InstalledPrograms.Find(id), ct).ConfigureAwait(true) is not { } program) return new { ok = false, message = "That app isn't on this PC anymore." };
        NoteProgramName(program);
        if (!program.Removable) return new { ok = false, message = Why(program) };
        if (AppsKeep.Why(program, await Task.Run(Orphans.KeepNeeds, ct).ConfigureAwait(true)) is { } kept) return new { ok = false, message = kept + ": it stays." };
        if (program.Store || program.Uninstall is { } command && UninstallerThere(command))
            return new { ok = false, message = $"{program.Name} still has its uninstaller: uninstall it instead." };
        var trail = await Task.Run(() => Orphans.TrailFor(program) with { Entries = program.Key is { } key ? [key] : [] }, ct).ConfigureAwait(true);
        return await Task.Run(() => Found(program.Name, trail), ct).ConfigureAwait(true);
    }

    private static object Found(string name, Trail trail)
    {
        var scan = new LeftoverScan(LeftoverScan.ThisPc());
        var items = scan.Find(trail, deep: true);
        AppLog.Info($"Apps: {name} left {items.Count(item => item.Selected)} things, {items.Count(item => !item.Selected)} more to look at.");
        return Told(scan, name, items.Select(item => (item, name)).ToList(), []);
    }

    /// <summary>
    /// A find as the page reads it: what each is and where (<see cref="LeftoverScan.Said"/>), how big, whose it is, whether one
    /// press takes it and if not why. Five hundred at most, biggest first: nobody reads more, and what isn't told can't be chosen.
    /// </summary>
    private static object Told(LeftoverScan scan, string name, List<(Leftover Item, string From)> found, List<string> remembered, Cleared? done = null, IReadOnlyList<Removal>? autoCleared = null)
    {
        var sizes = LeftoverScan.Sizes(found.Select(pair => pair.Item));
        var told = found.OrderByDescending(pair => pair.Item.Selected).ThenByDescending(pair => sizes.GetValueOrDefault(pair.Item)).Take(500).ToList();
        var token = Guid.NewGuid().ToString("N");
        foreach (var old in Finds.Where(pair => pair.Value.At < DateTime.UtcNow.AddHours(-1)).Select(pair => pair.Key).ToList()) Finds.TryRemove(old, out _);
        Finds[token] = (name, told.Select(pair => pair.Item).ToList(), DateTime.UtcNow, remembered);
        return new
        {
            ok = true,
            token,
            name,
            items = told.Select((pair, index) => new
            {
                id = index,
                kind = pair.Item.Kind.ToString().ToLowerInvariant(),
                path = scan.Said(pair.Item),
                bytes = sizes.GetValueOrDefault(pair.Item),
                selected = pair.Item.Selected,
                note = pair.Item.Held ?? scan.Stayed.GetValueOrDefault(pair.Item),
                // How sure the scan is that it's the app's, and why: only certain is ever selected.
                certainty = pair.Item.Sure.ToString().ToLowerInvariant(),
                because = LeftoverScan.Said(pair.Item.Sure, pair.Item.Because),
                // A driver that goes with its driver store package (exported first; Put back adds it again).
                store = pair.Item.Infs is { Count: > 0 },
                machine = pair.Item.Machine,
                from = pair.From,
            }),
            // File types Windows still opens with it: Default apps changes them (Windows signs that choice; Exo never writes it).
            defaults = scan.Defaults.Take(20).ToArray(),
            // After an uninstall: it cleared by itself, what went, how to put it back, and whether Windows' prompt was declined.
            auto = done is not null,
            cleared = done?.What,
            undo = done?.Undo,
            declined = done?.Declined ?? false,
            autoCleared = autoCleared?.Select(record => new { id = record.Id, name = record.Name, cleared = record.Cleared }),
        };
    }

    /// <summary>
    /// apps.tidy: only reads. What programs already gone left that still waits for a press: what needs an administrator (never a
    /// surprise prompt) or isn't tied firmly enough to go by itself; what is goes with the junk while the PC is idle
    /// (<see cref="JunkAutoClean"/>), so it isn't listed. And what went by itself since this was last read. A row needs
    /// something one press would take: what's matched by name alone never makes one. Read once in ten minutes, in the background.
    /// </summary>
    private async Task<object> AppsTidyAsync(bool fresh)
    {
        Task<object>? running;
        lock (AppsGate)
        {
            if (fresh || _tidy is null || DateTime.UtcNow - _tidyAt > TimeSpan.FromMinutes(10)) { _tidy = Task.Run(AppsTidy); _tidyAt = DateTime.UtcNow; }
            running = _tidy;
        }
        return await running.ConfigureAwait(true);
    }

    private Task<object>? _tidy;
    private DateTime _tidyAt;

    private object AppsTidy()
    {
        var scan = new LeftoverScan(LeftoverScan.ThisPc());
        var (found, remembered) = Orphans.Read(scan);
        var waiting = AutoClear.StillWaiting(found);
        AppLog.Info($"Apps: tidy found {waiting.Count} things waiting from programs that are gone.");
        return Told(scan, "Programs that are gone", waiting.Select(orphan => (orphan.Item, orphan.From)).ToList(), remembered, autoCleared: RemovalHistory.ForThisPc.NewlyCleared());
    }


    /// <summary>apps.leave: the user keeps what a find has left of it: it isn't offered again.</summary>
    private static object AppsLeave(JsonElement p, bool hasParams)
    {
        if (!Finds.TryRemove(ReadString(p, hasParams, "token") ?? "", out var find)) return new { ok = true };
        AppsMemory.ForThisPc.Leave(find.Items.Select(AppsOrphans.Remembered));
        AppsMemory.ForThisPc.Forget(find.Remembered);
        return new { ok = true };
    }

    /// <summary>apps.history: what Exo removed, newest first: names, dates and how much; what it takes to put it back stays here.</summary>
    private static object AppsHistory()
    {
        var history = RemovalHistory.ForThisPc;
        var records = history.All().Take(30).ToList();
        var bin = history.Bin(records.SelectMany(record => record.Files).Select(file => file.Path));
        return new
        {
            ok = true,
            items = records.Select(record => new
            {
                id = record.Id, name = record.Name, at = record.At.ToString("o", System.Globalization.CultureInfo.InvariantCulture), cleared = record.Cleared,
                canPutBack = history.CanPutBack(record, bin), undone = record.Undone,
            }),
        };
    }

    /// <summary>apps.undo: what a clear took comes back; what only an administrator may touch asks Windows, once.</summary>
    private async Task<object> AppsUndoAsync(string id, CancellationToken ct)
    {
        var done = await Task.Run(() => RemovalHistory.ForThisPc.UndoAsync(id, ImportReg, steps => ReplacedApps.RemoveAsAdminAsync(steps, ct)), ct).ConfigureAwait(true);
        AppsChangedHere();
        lock (AppsGate) _tidy = null;
        if (done is null) return new { ok = false, message = "That can't be put back anymore." };
        if (done.Declined) return new { ok = false, message = "Windows' permission was declined: nothing that needed it came back." };
        return new { ok = done.Lost == 0, back = done.Back, message = done.Lost == 0 ? null : $"{done.Lost} couldn't come back: emptied from the Recycle Bin, or something is there again." };
    }

    private static bool ImportReg(string file)
    {
        using var import = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("reg.exe", $"import \"{file}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        return import is not null && import.WaitForExit(15_000) && import.ExitCode == 0;
    }

    private async Task<object> AppsCleanAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var token = ReadString(p, hasParams, "token") ?? "";
        if (!Finds.TryGetValue(token, out var find)) return new { ok = false, message = "Look for its leftovers again." };
        var chosen = hasParams && p.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.Number).Select(id => id.GetInt32()).Where(id => id >= 0 && id < find.Items.Count).Distinct()
                .Select(id => find.Items[id]).ToList() : [];
        if (chosen.Count == 0) return new { ok = false, message = "Nothing was chosen." };
        // No confirm: files go to the Recycle Bin and registry entries to a backup first (Put back); Windows' one prompt asks for machine-wide items
        // and automatically creates a daily restore point in the same UAC prompt when needed.
        var scan = new LeftoverScan(LeftoverScan.ThisPc());
        var done = await Task.Run(() => AutoClear.ClearAsync(scan, find.Name, chosen, steps => ReplacedApps.RemoveAsAdminAsync(steps, ct), RemovalHistory.ForThisPc, DateTime.UtcNow, restore: true, requireRestorePoint: false), ct).ConfigureAwait(true);
        AppsChangedHere();
        if (done.Left.Count == 0)
        {
            Finds.TryRemove(token, out _);
            AppsMemory.ForThisPc.Forget(find.Remembered);
        }
        else
        {
            Finds[token] = (find.Name, find.Items, DateTime.UtcNow, find.Remembered);
        }
        lock (AppsGate) _tidy = null;
        AppLog.Info($"Apps: cleared {done.Removed.Count} of what {find.Name} left; {done.Left.Count} stayed{(done.Why is null ? "" : $" ({done.Why})")}.");
        return new
        {
            ok = done.Left.Count == 0,
            cleared = done.What,
            undo = done.Undo,
            left = done.Left.Count,
            message = done.Left.Count == 0 ? null : done.Declined ? "Windows' permission was declined: what needs it stayed."
                : $"{done.Left.Count} {(done.Left.Count == 1 ? "item" : "items")} stayed: {done.Why ?? LeftoverScan.InUse}.",
        };
    }

    /// <summary>
    /// The press on Uninstall covers this: what the scan ties to the program goes by itself, once its own uninstaller is done, and
    /// what waits on a Windows prompt from old leftovers joins it, since that one prompt is coming anyway. Declined, the rest
    /// still goes. The page gets one line of what went and what's left with why.
    /// </summary>
    private async Task<object> ClearWhatItLeftAsync(string name, Trail trail, CancellationToken ct)
    {
        var scan = new LeftoverScan(LeftoverScan.ThisPc());
        var (found, chosen) = await Task.Run(() =>
        {
            var items = scan.Find(trail, deep: true);
            return (items, AutoClear.WithWaiting(AutoClear.ForUserUninstall(items), AutoClear.Waiting(Orphans.Read(scan).Found)));
        }, ct).ConfigureAwait(true);
        var done = await Task.Run(() => AutoClear.ClearAsync(scan, name, chosen, steps => ReplacedApps.RemoveAsAdminAsync(steps, ct), RemovalHistory.ForThisPc, DateTime.UtcNow), ct).ConfigureAwait(true);
        lock (AppsGate) _tidy = null;
        AppLog.Info($"Apps: {name} gone; cleared {done.Removed.Count} of what it left by itself; {done.Left.Count} stayed{(done.Declined ? ", Windows' permission declined" : "")}.");
        var rest = found.Where(item => !done.Removed.Contains(item) && scan.Exists(item)).Select(item => (item, name)).ToList();
        return await Task.Run(() => Told(scan, name, rest, [], done), ct).ConfigureAwait(true);
    }


    /// <summary>"Stremio" of "Stremio version 5.0.27", "7-Zip" of "7-Zip 26.01 (x64)": a program's name without its version or bitness.</summary>
    internal static string BareName(string name)
    {
        var bare = Regex.Replace(name, @"\s*\((x64|x86|64-bit|32-bit|User|Machine)\)\s*", " ", RegexOptions.IgnoreCase);
        bare = Regex.Replace(bare, @"\s+(version\s+)?v?\d+(\.\d+)+.*$", "", RegexOptions.IgnoreCase);
        return bare.Trim();
    }

    // Still listed with its uninstaller there (a Store app: its package still there).
    private static bool StillThere(InstalledProgram program) =>
        InstalledPrograms.Find(program.Id) is { } now && (now.Store || now.Uninstall is { } command && UninstallerThere(command));

    private static string Why(InstalledProgram program) => EdgeRemoval.IsWebView(program.Name) ? "Exo and other apps run on WebView2: it stays." : program.Care switch
    {
        InstalledPrograms.Cares.Exo => "To remove Exo, use Windows' Installed apps.",
        InstalledPrograms.Cares.AntiCheat => "Anti-cheat goes with its game: Exo never removes it.",
        InstalledPrograms.Cares.Driver => "Your graphics driver is changed in System › Driver.",
        InstalledPrograms.Cares.ExoUses => "Exo uses it (Xbox games need the Xbox app): it stays.",
        _ => "Windows doesn't allow removing it.",
    };

    private static string Warning(InstalledProgram program) => EdgeRemoval.IsEdge(program)
        ? " The WebView2 runtime stays (Exo and other apps run on it), and so does Edge Update, which keeps it current but won't install Edge again. A link Windows opens in Edge (from Widgets or Search) may not open."
        : program.Care switch
    {
        InstalledPrograms.Cares.Runtime => " Games and apps that need it stop working until it's installed again.",
        InstalledPrograms.Cares.Hardware => " The hardware it drives may stop working until it's installed again.",
        InstalledPrograms.Cares.Store => " Your games from it need it to start.",
        _ => "",
    };

    /// <summary>
    /// A game, for clearing what Windows keeps about it once its store has removed it: its folder (anything pointing into
    /// it), its executables (crash reports), a Steam game's link (its desktop shortcut is a steam://rungameid/ one), and
    /// every other game's and program's folder, kept. Null for a game without a folder of its own.
    /// </summary>
    private Trail? GameTrail(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.Path) || !Directory.Exists(game.Path) || !InstalledPrograms.Own(game.Path)) return null;
        var exes = new List<string>();
        try
        {
            exes.AddRange(Directory.EnumerateFiles(game.Path, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                .Select(Path.GetFileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(40));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var others = _services.Library.PeekCachedLibrary().Where(other => other.Id != game.Id && !string.IsNullOrWhiteSpace(other.Path)).Select(other => other.Path!)
            .Concat(InstalledPrograms.List().Where(program => !program.Store && program.Location is not null && !string.Equals(program.Location, game.Path, StringComparison.OrdinalIgnoreCase)).Select(program => program.Location!));
        return new Trail(game.Title)
        {
            Homes = [game.Path],
            Exes = exes,
            Ids = game.Store == StoreKind.Steam && game.Id.StartsWith("steam:", StringComparison.Ordinal) ? [$"steam://rungameid/{game.Id[6..]}"] : [],
            Keep = [.. others, .. Adapters.RecompInstallations.DataFolders()],
        };
    }

    /// <summary>
    /// Once a game is gone: Windows' records of it (shortcuts, compatibility, recent apps, UserAssist, Game Bar's entry,
    /// crash dumps) go, as after any uninstall here, without asking: they point at nothing now. Only this user's; its
    /// folder only when the store left it empty (some games keep saves and mods there); nothing found by name alone.
    /// </summary>
    private static void ClearGameTraces(string title, Trail trail)
    {
        try
        {
            var scan = new LeftoverScan(LeftoverScan.ThisPc());
            var homes = scan.Homes(trail.Homes);
            var found = scan.Find(trail, deep: false).Where(item => !item.Machine && !item.Likely &&
                !(item.Kind == LeftoverKind.Folder && homes.Any(home => string.Equals(home.TrimEnd('\\'), item.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) &&
                  Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories).Any())).ToList();
            if (found.Count == 0) return;
            scan.Backup(title, found);
            var gone = found.Count(scan.Delete);
            AppLog.Info($"Game removed: cleared {gone} of Windows' {found.Count} records of {title}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Debug("Game removed: its records stayed: " + ex.GetType().Name);
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The update's state (UpdateFlow), never applying while a game runs, the free-space check before a download, and the verified
/// installer kept for after a game: staged-update.json in the updates folder names it (version, SHA-256, size, file, and how
/// many times it was started), and the next start runs it while Exo shows its Updating Exo window, checked again first
/// (ApplyKeptAtStartAsync).
///
/// Out of scope on purpose: someone who can write this user's updates folder. Such a program already runs as this user and
/// can change Exo's own files or start anything itself, so the record and the file are checked against a broken download,
/// a replaced release and a stale copy, not against that user. The record's SHA-256 came from the update address when the
/// file was downloaded, and the next start asks the address again for that release's SHA-256 when it is online.
/// </summary>
public sealed partial class AppUpdateService
{
    internal const string StagedRecordName = "staged-update.json";
    internal const string WaitsForGameMessage = "Your update is ready and goes in after your game, the next time Exo opens.";
    /// <summary>Room the installer needs beyond its own parts (its log, the shortcut, Windows' own slack).</summary>
    internal const long SpaceMargin = 256L * 1024 * 1024;
    /// <summary>A kept installer started this many times without the update taking is dropped; the next update downloads anew.</summary>
    internal const int MaxStarts = 2;
    /// <summary>Exo Setup's update mode: its own small "Updating Exo" window while the engine installs silently (setup/src/main.cpp).</summary>
    internal const string InstallerArguments = "/UPDATE";

    internal sealed record StagedUpdate(string Version, string Sha256, long Size, string File, int Starts = 0);

    private readonly Lock _flowGate = new();
    private UpdateFlow.State _state = UpdateFlow.State.Idle;

    internal UpdateFlow.State State { get { lock (_flowGate) return _state; } }

    /// <summary>Each new state, for the UI (WebHostBridge sends it as app.updateState).</summary>
    internal event Action<UpdateFlow.State>? StateChanged;

    /// <summary>A game runs now: one Exo started or launching, or one found running however it was started.</summary>
    internal Func<bool> GameRunning { get; init; } = () =>
        App.Services.Launcher.AnyGameRunning || GameRunningSignal.Running || MainWindow.RunningLibraryGames().Count > 0;

    /// <summary>
    /// The same question as Exo opens, before its own run-state poll has looked: the saved library is read from disk and each
    /// installed game's folder is matched against the running processes (a windowed or alt-tabbed game counts).
    /// </summary>
    internal Func<Task<bool>> GameRunningAtStart { get; init; } = async () =>
    {
        try { await App.Services.Library.WarmFromDiskAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { AppLog.Warn("Update: the saved library did not load in time; games are looked for in what there is."); }
        return MainWindow.RunningLibraryGames().Count > 0;
    };

    /// <summary>Free bytes on the drive that holds this folder; null when Windows can't say.</summary>
    internal Func<string, long?> FreeBytes { get; init; } = folder =>
    {
        try { return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    };

    /// <summary>The installed app folder's size: what the installer stages beside it before the swap; null when it can't be read.</summary>
    internal Func<long?> AppFolderBytes { get; init; } = () => FolderBytes(AppContext.BaseDirectory);

    /// <summary>Where Setup unpacks its engine (%TEMP%).</summary>
    internal string TempFolder { get; init; } = Path.GetTempPath();

    /// <summary>The folder holding app\ (the installer stages the new app beside it).</summary>
    internal string InstallFolder { get; init; } =
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) ?? AppContext.BaseDirectory;

    internal Func<string, bool> IsInstaller { get; init; } = IsExpectedInstaller;

    internal Func<string, bool> StartInstaller { get; init; } = path => RunInstaller(path, Path.GetDirectoryName(path)!);

    /// <summary>The release's SHA-256 as the update address gives it now, for the kept version; null when it can't be asked.</summary>
    internal Func<string, CancellationToken, Task<string?>> ReleaseSha256 { get; init; }

    /// <summary>One step through UpdateFlow, with whether a game runs now; the state it lands in.</summary>
    internal UpdateFlow.State Move(UpdateFlow.Step step)
    {
        // Asked only for a step whose rows depend on it: a game check is a process scan.
        var game = UpdateFlow.Table.Any(row => row.On == step && row.Game is not null) && GameRunning();
        UpdateFlow.State next;
        bool changed;
        lock (_flowGate)
        {
            next = UpdateFlow.Next(_state, step, game);
            changed = next != _state;
            if (changed) AppLog.Info($"Update: {_state} -> {next} ({step}).");
            _state = next;
        }
        if (changed)
            try { StateChanged?.Invoke(next); } catch (Exception ex) { AppLog.Warn("Update: a state listener failed: " + ex.GetType().Name); }
        return next;
    }

    /// <summary>A game started or ended (LeanSignal.Changed): a kept update waits, or is ready again.</summary>
    internal void GameChanged() => Move(GameRunning() ? UpdateFlow.Step.GameStarted : UpdateFlow.Step.GameEnded);

    /// <summary>Exo is closing; after the installer started, that is the update's restart.</summary>
    internal void Exiting() => Move(UpdateFlow.Step.Exited);

    /// <summary>
    /// The space a download and install needs, by drive: the installer in the updates folder, Setup's engine unpacked again in
    /// %TEMP%, and the new app folder staged beside the old one, plus a margin on each drive. A drive Windows can't read is
    /// left out (said in the log) and the update goes on. Null when everything fits.
    /// </summary>
    internal string? SpaceShortfall(long installerBytes)
    {
        var app = AppFolderBytes();
        if (app is null) AppLog.Warn("Update: the app folder's size could not be read; three times the installer is assumed.");
        foreach (var (drive, needed) in SpaceNeeded(installerBytes, app, _updatesDirectory, TempFolder, InstallFolder))
        {
            var free = FreeBytes(drive);
            if (free is null) { AppLog.Warn($"Update: the free space on {drive} could not be read; the update goes on."); continue; }
            if (free >= needed) continue;
            AppLog.Warn($"Update: {needed} bytes needed on {drive}, {free} free; nothing downloaded.");
            return $"Exo needs {needed / 1e9:0.0} GB free on drive {drive.TrimEnd('\\', ':')} to update.";
        }
        return null;
    }

    /// <summary>Bytes needed on each drive root (an unknown app folder size counts as three times the installer).</summary>
    internal static IReadOnlyDictionary<string, long> SpaceNeeded(long installerBytes, long? appFolderBytes, string updatesFolder,
        string tempFolder, string installFolder)
    {
        var needed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Add(string folder, long bytes)
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder;
            needed[drive] = (needed.TryGetValue(drive, out var already) ? already : SpaceMargin) + bytes;
        }
        Add(updatesFolder, installerBytes);
        Add(tempFolder, installerBytes);
        Add(installFolder, appFolderBytes ?? 3 * installerBytes);
        return needed;
    }

    private static long? FolderBytes(string folder)
    {
        try { return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>The kept installer, when it is this release's and still verifies; otherwise null (and it goes).</summary>
    private async Task<(string Path, StagedUpdate Record)?> StagedInstallerAsync(string version, string? sha256, CancellationToken ct)
    {
        if (ReadStaged(_updatesDirectory) is not { } staged) return null;
        var path = Path.Combine(_updatesDirectory, staged.File);
        if (staged.Version == version && staged.Starts < MaxStarts && TimingSafeHashEquals(staged.Sha256, sha256) &&
            File.Exists(path) && new FileInfo(path).Length == staged.Size &&
            TimingSafeHashEquals(await Sha256FileAsync(path, ct).ConfigureAwait(false), staged.Sha256) && IsInstaller(path))
            return (path, staged);
        DiscardStaged(_updatesDirectory, path);
        return null;
    }

    /// <summary>
    /// The record goes on disk with one more start counted before the installer starts, so an installer that fails (and
    /// rolls back) runs at most <see cref="MaxStarts"/> times; false when it could not be written, and then nothing starts.
    /// </summary>
    private static bool CountStart(string updatesDirectory, StagedUpdate staged) =>
        TryWriteStaged(updatesDirectory, staged with { Starts = staged.Starts + 1 }) &&
        ReadStaged(updatesDirectory)?.Starts == staged.Starts + 1;

    /// <summary>The record written; false (said in the log) when Windows refuses even after its read-only mark is cleared.</summary>
    private static bool TryWriteStaged(string updatesDirectory, StagedUpdate staged)
    {
        try
        {
            WriteStaged(updatesDirectory, staged);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Update: the kept-update record could not be written: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>A read-only mark (a backup tool, a copy from a CD) never stops an update: it is cleared before a write or delete.</summary>
    private static void ClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path) && File.GetAttributes(path) is var attributes && (attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Records that could not be removed this run: ignored from then on (said once), so one stuck file stops nothing.</summary>
    private static readonly HashSet<string> StuckRecords = new(StringComparer.OrdinalIgnoreCase);

    private static void WriteStaged(string updatesDirectory, StagedUpdate staged)
    {
        var record = Path.Combine(updatesDirectory, StagedRecordName);
        ClearReadOnly(record);
        var temporary = record + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            schema = 1, version = staged.Version, sha256 = staged.Sha256, size = staged.Size, file = staged.File, starts = staged.Starts,
        }));
        File.Move(temporary, record, overwrite: true);
    }

    /// <summary>staged-update.json, when it is well formed and names an installer in this folder; null otherwise.</summary>
    internal static StagedUpdate? ReadStaged(string updatesDirectory)
    {
        try
        {
            var record = Path.Combine(updatesDirectory, StagedRecordName);
            lock (StuckRecords) if (StuckRecords.Contains(record)) return null;
            if (!File.Exists(record) || new FileInfo(record).Length > 4096 || (File.GetAttributes(record) & FileAttributes.ReparsePoint) != 0) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(record));
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1) return null;
            var version = root.GetProperty("version").GetString() ?? "";
            var sha = NormalizeSha(root.GetProperty("sha256").GetString());
            var size = root.GetProperty("size").GetInt64();
            var file = root.GetProperty("file").GetString() ?? "";
            var starts = root.TryGetProperty("starts", out var count) ? count.GetInt32() : 0;
            if (sha is null || size <= 0 || starts < 0 || VersionNumber.Parse(version) is null || Path.GetFileName(file) != file ||
                !TryClassifyArtifact(file, out var partial) || partial) return null;
            return new StagedUpdate(version, sha, size, file, starts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The record and, when it names one of the updater's own installers, that file.</summary>
    private static void DiscardStaged(string updatesDirectory, string? installer)
    {
        var record = Path.Combine(updatesDirectory, StagedRecordName);
        try
        {
            ClearReadOnly(record);
            if (File.Exists(record)) DeletionGate.DeleteFile(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (StuckRecords)
                if (StuckRecords.Add(record)) AppLog.Warn("Update: the kept-update record can't be removed; it is ignored from now on: " + ex.GetType().Name);
        }
        try
        {
            if (installer is not null && TryClassifyArtifact(installer, out _) && File.Exists(installer))
            {
                ClearReadOnly(installer);
                DeletionGate.DeleteFile(installer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Update: the kept installer stays: " + ex.Message); }
    }

    internal const string SkippedRecordName = "skipped-update.json";

    /// <summary>The version whose update did not take: never offered again by itself (Settings can still install it).</summary>
    internal string? SkippedVersion
    {
        get
        {
            try
            {
                var path = Path.Combine(_updatesDirectory, SkippedRecordName);
                if (!File.Exists(path) || new FileInfo(path).Length > 1024) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.GetProperty("version").GetString() is { } version && VersionNumber.Parse(Normalize(version)) is not null ? Normalize(version) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        }
    }

    internal void Skip(string version)
    {
        try
        {
            Directory.CreateDirectory(_updatesDirectory);
            var path = Path.Combine(_updatesDirectory, SkippedRecordName);
            ClearReadOnly(path);
            File.WriteAllText(path, JsonSerializer.Serialize(new { schema = 1, version = Normalize(version) }));
            AppLog.Info($"Update {Normalize(version)}: did not take; it is not offered again by itself.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Update: the skipped version could not be noted: " + ex.GetType().Name); }
    }

    /// <summary>
    /// Before anything shows at a start: a kept update for a version this build already is (the update took) goes silently,
    /// and one whose last try did not take (update-result.json: rolled back, or never up) goes too and is
    /// skipped, so its rollback's restart never runs it again. True when a kept update is still there to try.
    /// </summary>
    internal bool SettleKeptUpdate(string runningVersion)
    {
        if (ReadStaged(_updatesDirectory) is not { } staged) return false;
        var path = Path.Combine(_updatesDirectory, staged.File);
        if (VersionNumber.Compare(Normalize(runningVersion), Normalize(staged.Version)) >= 0)
        {
            DiscardStaged(_updatesDirectory, path);
            return false;
        }
        var (skip, version) = UpdateOutcome.Peek(InstallFolder, runningVersion);
        if (skip && version is not null && Normalize(version) == Normalize(staged.Version))
        {
            AppLog.Warn($"Update {staged.Version}: its last try did not take; the kept installer goes and is not tried again.");
            DiscardStaged(_updatesDirectory, path);
            Skip(staged.Version);
            return false;
        }
        return ReadStaged(_updatesDirectory) is not null;
    }


    /// <summary>
    /// This start, before Exo's main window: an installer kept for after a game goes in now when it is for a newer version than
    /// this one, has been started fewer than <see cref="MaxStarts"/> times, still has its size, SHA-256 and identity, matches
    /// the SHA-256 the update address gives for that release (when it can be asked), and no game runs. True when the
    /// installer started (Exo then exits; Setup's window shows the update and opens the new version).
    /// </summary>
    internal async Task<bool> ApplyKeptAtStartAsync(string runningVersion, CancellationToken ct = default)
    {
        if (!SettleKeptUpdate(runningVersion) || ReadStaged(_updatesDirectory) is not { } staged) return false;
        var path = Path.Combine(_updatesDirectory, staged.File);
        try
        {
            if (VersionNumber.Compare(Normalize(runningVersion), Normalize(staged.Version)) >= 0 || staged.Starts >= MaxStarts ||
                !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length != staged.Size)
            {
                AppLog.Info($"Update {staged.Version}: the kept installer is done with (started {staged.Starts} times, or stale); removed.");
                DiscardStaged(_updatesDirectory, path);
                return false;
            }
            if (await GameRunningAtStart().ConfigureAwait(false))
            {
                AppLog.Info($"Update {staged.Version}: kept; a game is running, so it waits for the next start.");
                return false;
            }
            var hash = await Sha256FileAsync(path, ct).ConfigureAwait(false);
            if (!TimingSafeHashEquals(hash, staged.Sha256) || !IsInstaller(path))
            {
                AppLog.Warn($"Update {staged.Version}: the kept installer no longer verifies; removed.");
                DiscardStaged(_updatesDirectory, path);
                return false;
            }
            // Online, the release must still be the one the file was downloaded for; offline, the record's SHA-256 stands.
            if (await ReleaseSha256(staged.Version, ct).ConfigureAwait(false) is { } now && !TimingSafeHashEquals(now, staged.Sha256))
            {
                AppLog.Warn($"Update {staged.Version}: the update address now gives another SHA-256 for this release; the kept installer is removed.");
                DiscardStaged(_updatesDirectory, path);
                return false;
            }
            if (!CountStart(_updatesDirectory, staged)) return false;
            if (!StartInstaller(path)) return false;
            AppLog.Info($"Update {staged.Version}: the installer kept from a game is running; Exo closes for it.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("Update: the kept installer could not start: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>The release's SHA-256 from its .sha256 on the update address, within 4 seconds; null offline or on any doubt.</summary>
    private async Task<string?> FetchReleaseSha256Async(string version, CancellationToken ct)
    {
        var tag = "v" + Normalize(version);
        if (!ReleaseTag().IsMatch(tag)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateAddressFile(tag, InstallerAssetName) + ".sha256");
            using var response = await SendFollowingAllowedRedirectsAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            return ParseSha256File(await ReadBoundedTextAsync(response.Content, 4096, timeout.Token).ConfigureAwait(false), InstallerAssetName);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            AppLog.Info("Update: the release's SHA-256 could not be asked for (offline?); the kept record's stands.");
            return null;
        }
    }

    /// <summary>Exo Setup in its update mode: its Updating Exo window, the engine's silent install, the new Exo at the end.</summary>
    internal static bool RunInstaller(string setupPath, string workingDirectory)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = setupPath,
            // The updater must never copy the installer itself over ExoLauncher.exe: Setup owns the app.incoming to app swap.
            Arguments = InstallerArguments,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            ErrorDialog = false,
        };
        // Exo's own marks never reach Setup (and from it the new Exo and its games).
        processInfo.Environment.Remove("EXO_SILENT_INSTALL");
        using var installer = Process.Start(processInfo);
        return installer is not null;
    }
}

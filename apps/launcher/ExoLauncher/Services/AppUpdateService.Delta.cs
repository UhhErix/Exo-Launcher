using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The file-level update: when the update address lists a release's files.json and files.pack (AppFiles), Exo compares
/// that list with the files it is running from, downloads only the files that changed (byte ranges of the pack, several at
/// once), checks every file of the new folder against its SHA-256, and builds that folder beside the installed one from
/// hard links to the unchanged files plus the new ones. Exo then closes and tools/installer/Apply-ExoDelta.ps1 swaps the
/// folders as Setup does (app.previous kept until the new build reports a healthy start, Complete-ExoUpdate.ps1), updates
/// Apps &amp; features and opens the new Exo.
///
/// The full installer is used instead whenever any of this can't be trusted or done: no list for the release, Exo isn't
/// running from its installed folder, the installed copy wasn't put there by an installer that records its revision, the
/// release's installer changed (it may do something outside the app folder), a game runs (a kept installer is how an update
/// waits for one), the last file-level try at this version failed, the changed files aren't smaller than the installer, or
/// any request, range, file or check fails. Ways it could fail, tested in AppFilesTests and AppUpdateDeltaTests: a list
/// from another host or for another version is used; a range the server didn't send as asked is read; one changed file
/// is missed (a stale hash cache); the old folder is touched before the swap; a failure leaves the staging folder behind or
/// Lucid stopped.
/// </summary>
public sealed partial class AppUpdateService
{
    internal const string DeltaFailedName = "delta-failed.json";
    /// <summary>In the updates folder: the files an interrupted file-level try downloaded and checked, each named by its SHA-256.</summary>
    internal const string DeltaPartsFolder = "delta-parts";
    /// <summary>The installer records its revision here (tools/ExoLauncher.nsi); a delta needs the same one in files.json.</summary>
    internal const string InstallerRevisionValue = "ExoInstaller";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ExoLauncher";
    private const int RangesAtOnce = 4;
    private static readonly TimeSpan RangeStall = TimeSpan.FromSeconds(60);

    /// <param name="ManifestUrl">files.json on the update address, as Exo builds it.</param>
    /// <param name="ManifestSize">From the address's checks, when it gives them.</param>
    /// <param name="PackSize">The pack's size and SHA-256 from the address's checks, held to what files.json says.</param>
    internal sealed record DeltaOffer(string ManifestUrl, string PackUrl, long? ManifestSize = null, string? ManifestSha256 = null,
        long? PackSize = null, string? PackSha256 = null);

    internal sealed record DeltaPlan(AppFiles.Manifest Manifest, List<AppFiles.Entry> Reuse, List<AppFiles.Entry> Fetch)
    {
        internal long FetchBytes => Fetch.Sum(e => e.Length);
        internal long FetchRawBytes => Fetch.Sum(e => e.Size);
    }

    /// <summary>What the swap script is started with.</summary>
    internal sealed record DeltaApply(string InstallRoot, string Incoming, string Setup, string Version, string UpdatesDirectory, int ProcessId);

    /// <summary>The folder Exo runs from (the installed app folder for a real install).</summary>
    internal string AppFolder { get; init; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>Apps &amp; features' entry for Exo: where it installed the app and its installer revision (null when not there).</summary>
    internal Func<(string? Location, string? Revision)> InstalledSetup { get; init; } = ReadInstalledSetup;

    /// <summary>Starts Apply-ExoDelta.ps1, which waits for Exo to close; false when Windows did not start it.</summary>
    internal Func<DeltaApply, bool> StartDeltaApply { get; init; } = RunDeltaApply;

    /// <summary>Hard-links a file (a copy when Windows can't link it).</summary>
    internal Action<string, string> LinkOrCopy { get; init; } = DefaultLinkOrCopy;

    private readonly HttpClient _rangeHttp;
    private readonly Lock _planGate = new();
    private (string Tag, Task<DeltaPlan> Plan)? _plan;

    /// <summary>
    /// The files.json and files.pack the update address lists for <paramref name="tag"/>, as URLs Exo builds itself
    /// (<c>/v1/launcher/&lt;tag&gt;/files.json</c> or <c>/v1/launcher/download/&lt;tag&gt;/files.json</c>): the answer's own URL
    /// only says which of the two it serves (the exo-updates Worker names them files.filesJson and files.filesPack). Its checks
    /// (checks.filesJson, checks.filesPack) may give each one's size and SHA-256. Null when either file is missing.
    /// </summary>
    internal static DeltaOffer? ReadDeltaOffer(JsonElement root, string tag)
    {
        if (!root.TryGetProperty("files", out var files)) return null;
        var urls = files.ValueKind switch
        {
            JsonValueKind.Object => files.EnumerateObject().Select(p => p.Value),
            JsonValueKind.Array => files.EnumerateArray(),
            _ => [],
        };
        var named = urls.Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
        if (DeltaUrl(named, tag, AppFiles.ManifestName) is not { } manifest || DeltaUrl(named, tag, AppFiles.PackName) is not { } pack) return null;
        var (size, sha) = Check(root, AppFiles.MaxManifestBytes, "filesJson", "files", "manifest", AppFiles.ManifestName);
        var (packSize, packSha) = Check(root, long.MaxValue, "filesPack", "pack", AppFiles.PackName);
        return new DeltaOffer(manifest, pack, size, sha, packSize, packSha);

        // Both or neither: a size without a valid digest (or the reverse) is not taken.
        static (long?, string?) Check(JsonElement root, long max, params string[] keys)
        {
            if (!root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Object) return (null, null);
            foreach (var key in keys)
                if (checks.TryGetProperty(key, out var check) && check.ValueKind == JsonValueKind.Object &&
                    check.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) && n > 0 && n <= max &&
                    check.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String && NormalizeSha(h.GetString()) is { } digest)
                    return (n, digest);
            return (null, null);
        }
    }

    private static string? DeltaUrl(List<string> urls, string tag, string name)
    {
        string[] built = [UpdateAddress.AbsoluteUri + tag + "/" + name, UpdateAddressFile(tag, name)];
        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedDownloadUri(uri) || !uri.IsDefaultPort ||
                uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) continue;
            var path = uri.GetLeftPart(UriPartial.Path);
            foreach (var mine in built)
                if (string.Equals(new Uri(mine).AbsolutePath, uri.AbsolutePath, StringComparison.Ordinal) &&
                    string.Equals(new Uri(mine).GetLeftPart(UriPartial.Path), path, StringComparison.OrdinalIgnoreCase))
                    return mine;
        }
        return null;
    }

    /// <summary>
    /// Why this install can't take a file-level update (the installer is used), or null when it can. Before files.json is read:
    /// Exo must run from <c>&lt;install folder&gt;\app</c>, Apps &amp; features must name that folder and an installer revision,
    /// and the last file-level try must not have been for this version.
    /// </summary>
    internal static string? DeltaRefusal(string appFolder, string installFolder, (string? Location, string? Revision) setup, string? failedVersion, string offered)
    {
        static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            var app = Full(appFolder);
            if (!string.Equals(app, Full(Path.Combine(installFolder, "app")), StringComparison.OrdinalIgnoreCase))
                return "Exo isn't running from its installed app folder";
            if (setup.Location is null || !string.Equals(Full(setup.Location), app, StringComparison.OrdinalIgnoreCase))
                return "Apps & features doesn't name this app folder";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "the app folder can't be read"; }
        if (AppFiles.NormalizeSha(setup.Revision) is null) return "this copy was installed before file-level updates";
        if (failedVersion is not null && Normalize(failedVersion) == Normalize(offered)) return "the last file-level try at this version failed";
        return null;
    }

    /// <summary>Why this list can't be used for this install (the installer is used), or null.</summary>
    internal static string? PlanRefusal(DeltaPlan plan, string offered, string? installedRevision, long? installerBytes)
    {
        if (plan.Manifest.Version != Normalize(offered)) return "files.json is for another version";
        if (!string.Equals(plan.Manifest.Installer, AppFiles.NormalizeSha(installedRevision), StringComparison.Ordinal)) return "the release's installer changed";
        if (installerBytes is > 0 && plan.FetchBytes >= installerBytes) return "the changed files aren't smaller than the installer";
        return null;
    }

    /// <summary>The version whose last file-level try failed after Exo closed (Apply-ExoDelta.ps1 writes it); null when none.</summary>
    internal string? DeltaFailedVersion
    {
        get
        {
            try
            {
                var path = Path.Combine(_updatesDirectory, DeltaFailedName);
                if (!File.Exists(path) || new FileInfo(path).Length > 1024) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.GetProperty("version").GetString() is { } version && VersionNumber.Parse(Normalize(version)) is not null ? Normalize(version) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        }
    }

    /// <summary>Starts working out what changed for this release in the background (the update was found), so Update now starts at once.</summary>
    private void PrepareDelta(string version, DeltaOffer offer)
    {
        if (HostMode.IsDisposable || DeltaRefusal(AppFolder, InstallFolder, InstalledSetup(), DeltaFailedVersion, version) is not null) return;
        _ = PlanFor(version, offer, fresh: false).ContinueWith(t => AppLog.Debug("Update: the file list could not be read yet: " + t.Exception?.InnerException?.GetType().Name),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private Task<DeltaPlan> PlanFor(string version, DeltaOffer offer, bool fresh)
    {
        lock (_planGate)
        {
            if (!fresh && _plan is { } known && known.Tag == offer.ManifestUrl && !known.Plan.IsFaulted && !known.Plan.IsCanceled) return known.Plan;
            var plan = Task.Run(() => PlanDeltaAsync(version, offer, CancellationToken.None));
            _plan = (offer.ManifestUrl, plan);
            return plan;
        }
    }

    /// <summary>files.json from the address (checked against the address's size and SHA-256 when it gives them), then the installed folder hashed and compared.</summary>
    internal async Task<DeltaPlan> PlanDeltaAsync(string version, DeltaOffer offer, CancellationToken ct)
    {
        byte[] json;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var request = new HttpRequestMessage(HttpMethod.Get, offer.ManifestUrl);
            using var response = await SendFollowingAllowedRedirectsAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException($"files.json answered {(int)response.StatusCode}.");
            json = await ReadBoundedBytesAsync(response.Content, AppFiles.MaxManifestBytes, timeout.Token).ConfigureAwait(false);
        }
        if (offer.ManifestSize is { } size && json.LongLength != size) throw new InvalidDataException("files.json is not the size the address gave.");
        if (offer.ManifestSha256 is { } sha && !TimingSafeHashEquals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(json)), sha))
            throw new InvalidDataException("files.json does not match the address's SHA-256.");
        var manifest = AppFiles.Parse(json) ?? throw new InvalidDataException("files.json is not a valid file list.");
        if (manifest.Version != Normalize(version)) throw new InvalidDataException("files.json is for another version.");
        if ((offer.PackSize is { } packSize && packSize != manifest.PackSize) ||
            (offer.PackSha256 is { } packSha && !TimingSafeHashEquals(packSha, manifest.PackSha256)))
            throw new InvalidDataException("files.json names another pack than the address does.");
        var sizes = manifest.Files.ToDictionary(f => f.Path, f => f.Size, StringComparer.OrdinalIgnoreCase);
        var installed = await AppFileHashes.HashAsync(AppFolder, Path.Combine(_updatesDirectory, AppFileHashes.CacheName),
            (path, length) => sizes.TryGetValue(path, out var wanted) && wanted == length, ct).ConfigureAwait(false);
        var (reuse, fetch) = AppFiles.Diff(manifest, installed);
        return new DeltaPlan(manifest, reuse, fetch);
    }

    /// <summary>
    /// The file-level update, up to Exo closing for the swap. Null (said in the log) when the installer is to be used instead;
    /// a cancel by the user propagates. Nothing in the installed app folder is changed here.
    /// </summary>
    private async Task<CheckResult?> TryDeltaAsync(CheckResult check, DeltaOffer offer, IProgress<(string status, double? percent)>? progress, CancellationToken ct)
    {
        var offered = check.RemoteVersion;
        var setup = InstalledSetup();
        if (DeltaRefusal(AppFolder, InstallFolder, setup, DeltaFailedVersion, offered) is { } refused) return Refuse(refused);
        if (GameRunning()) return Refuse("a game is running, and a kept installer is how an update waits for one");

        string? staging = null, setupFolder = null;
        string? lucid = null;
        var started = false;
        var parts = Path.Combine(_updatesDirectory, DeltaPartsFolder);
        var fetched = new System.Collections.Concurrent.ConcurrentDictionary<string, AppFiles.Entry>(StringComparer.OrdinalIgnoreCase);
        Func<AppFiles.Entry, string>? target = null;
        try
        {
            RemoveStaleDeltaFolders();
            progress?.Report(("Checking what changed…", null));
            DeltaPlan plan;
            try { plan = await PlanFor(offered, offer, fresh: false).WaitAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                AppLog.Debug("Update: reading the file list again after " + ex.GetType().Name);
                plan = await PlanFor(offered, offer, fresh: true).WaitAsync(ct).ConfigureAwait(false);
            }
            if (PlanRefusal(plan, offered, setup.Revision, check.DownloadSize) is { } no) return Refuse(no);
            var free = FreeBytes(InstallFolder);
            if (free is not null && free < plan.FetchRawBytes + SpaceMargin) return Refuse("there isn't room for the changed files");
            AppLog.Info($"Update {offered}: {plan.Fetch.Count} of {plan.Manifest.Files.Count} files changed; downloading {Mb(plan.FetchBytes)} " +
                        $"instead of the {Mb(check.DownloadSize ?? 0)} installer.");

            var id = Guid.NewGuid().ToString("N");
            staging = Path.Combine(InstallFolder, "app.incoming-" + id);
            setupFolder = staging + "setup";
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(setupFolder);
            string Target(AppFiles.Entry entry) => StagedPath(entry.Setup ? setupFolder : staging, entry.Path);
            target = Target;

            foreach (var entry in plan.Reuse)
            {
                var dest = Target(entry);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                LinkOrCopy(Path.Combine(AppFolder, entry.Path.Replace('/', '\\')), dest);
            }
            // NSIS writes its uninstaller into the app folder after the swap; it is the same while the installer revision is.
            var uninstaller = Path.Combine(AppFolder, "Uninstall.exe");
            if (File.Exists(uninstaller) && !plan.Manifest.Files.Any(f => f.Path.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)))
                File.Copy(uninstaller, Path.Combine(setupFolder, "Uninstall.exe"));

            // Files an interrupted try already downloaded and checked come back first; only the rest is asked for.
            var remaining = ResumeFromParts(parts, plan.Fetch, Target, fetched);
            if (remaining.Count < plan.Fetch.Count)
                AppLog.Info($"Update {offered}: {plan.Fetch.Count - remaining.Count} files kept from the last try.");
            await DownloadChangedAsync(offer.PackUrl, plan.Manifest.PackSize, remaining, Target, fetched, progress, ct).ConfigureAwait(false);

            progress?.Report(("Verifying…", null));
            await VerifyStagedAsync(plan.Manifest, staging, setupFolder, Target, ct).ConfigureAwait(false);
            Move(UpdateFlow.Step.Verified);

            if (GameRunning() || Move(UpdateFlow.Step.Apply) != UpdateFlow.State.Applying)
                return Refuse("a game started during the download");
            progress?.Report(("Installing…", null));
            lucid = await StopLucid().ConfigureAwait(false);
            if (!StartDeltaApply(new DeltaApply(InstallFolder, staging, setupFolder, plan.Manifest.Version, _updatesDirectory, Environment.ProcessId)))
                throw new InvalidOperationException("Windows did not start the update's swap.");
            started = true;
            DeleteParts(parts);
            Move(UpdateFlow.Step.InstallerStarted);
            progress?.Report(("Restarting…", null));
            Helpers.Telemetry.Track("update.result", ("from", check.LocalVersion ?? ""), ("to", check.RemoteVersion ?? ""), ("ok", true), ("error_kind", "none"));
            return new CheckResult
            {
                UpdateAvailable = true,
                Installed = true,
                ShouldExit = true,
                LocalVersion = check.LocalVersion ?? "",
                RemoteVersion = offered,
                Message = $"Applying v{offered}… Exo Launcher will close and reopen.",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Refuse($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!started)
            {
                if (target is not null) KeepParts(parts, fetched.Values, target);
                foreach (var folder in new[] { staging, setupFolder })
                    if (folder is not null && Directory.Exists(folder))
                        try { DeletionGate.DeleteDirectory(folder, recursive: true); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Update: a staging folder stays for the next cleanup: " + ex.GetType().Name); }
                if (lucid is not null) StartLucid(lucid);
            }
        }

        static CheckResult? Refuse(string why)
        {
            AppLog.Info("Update: the full installer is used: " + why + ".");
            return null;
        }
    }

    private static string StagedPath(string folder, string relative)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A file path leaves the app folder.");
        return path;
    }

    /// <summary>
    /// The files an interrupted try downloaded and checked, kept in the updates folder by SHA-256 (<see cref="KeepParts"/>):
    /// each one this plan needs is read again against its SHA-256 and copied into the new folder; the rest of the folder is
    /// removed. The files still to download.
    /// </summary>
    internal static List<AppFiles.Entry> ResumeFromParts(string parts, IReadOnlyList<AppFiles.Entry> fetch, Func<AppFiles.Entry, string> target,
        System.Collections.Concurrent.ConcurrentDictionary<string, AppFiles.Entry> fetched)
    {
        if (!Directory.Exists(parts)) return [.. fetch];
        var remaining = new List<AppFiles.Entry>();
        var good = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            if ((File.GetAttributes(parts) & FileAttributes.ReparsePoint) != 0) { DeleteParts(parts); return [.. fetch]; }
            foreach (var entry in fetch)
            {
                var kept = Path.Combine(parts, entry.Sha256);
                if (!good.TryGetValue(entry.Sha256, out var ok))
                {
                    ok = File.Exists(kept) && (File.GetAttributes(kept) & FileAttributes.ReparsePoint) == 0 && new FileInfo(kept).Length == entry.Size &&
                         AppFiles.FixedTimeHexEquals(HashFile(kept), entry.Sha256);
                    good[entry.Sha256] = ok;
                }
                if (!ok) { remaining.Add(entry); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target(entry))!);
                File.Copy(kept, target(entry), overwrite: true);
                fetched[entry.Path] = entry;
            }
            // Anything else in the folder belongs to another version's try.
            foreach (var file in Directory.EnumerateFiles(parts))
                if (!good.TryGetValue(Path.GetFileName(file), out var used) || !used) DeletionGate.DeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Update: the last try's files were not used: " + ex.GetType().Name);
            var done = fetched.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. fetch.Where(e => !done.Contains(e.Path))];
        }
        return remaining;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 17, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>After a try that did not reach the swap: the files it downloaded and checked, kept by SHA-256 for the next try.</summary>
    internal static void KeepParts(string parts, IEnumerable<AppFiles.Entry> fetched, Func<AppFiles.Entry, string> target)
    {
        try
        {
            Directory.CreateDirectory(parts);
            if ((File.GetAttributes(parts) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var entry in fetched)
            {
                var from = target(entry);
                var kept = Path.Combine(parts, entry.Sha256);
                if (File.Exists(from) && !File.Exists(kept)) File.Move(from, kept);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Debug("Update: the downloaded files were not kept for the next try: " + ex.GetType().Name);
        }
    }

    private static void DeleteParts(string parts)
    {
        try { if (Directory.Exists(parts)) DeletionGate.DeleteDirectory(parts, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Update: the last try's files stay for now: " + ex.GetType().Name); }
    }

    /// <summary>The changed files: merged byte ranges of the pack, <see cref="RangesAtOnce"/> at a time, each tried up to three times.</summary>
    private async Task DownloadChangedAsync(string packUrl, long packSize, IReadOnlyList<AppFiles.Entry> files, Func<AppFiles.Entry, string> target,
        System.Collections.Concurrent.ConcurrentDictionary<string, AppFiles.Entry> fetched,
        IProgress<(string status, double? percent)>? progress, CancellationToken ct)
    {
        var spans = AppFiles.Ranges(files);
        var total = spans.Sum(s => s.Length);
        long done = 0;
        var lastReport = Stopwatch.StartNew();
        void Progressed(long bytes)
        {
            var now = Interlocked.Add(ref done, bytes);
            if (lastReport.ElapsedMilliseconds < 200 && now < total) return;
            lastReport.Restart();
            progress?.Report(($"Downloading {Mb(now)} of {Mb(total)}", total > 0 ? Math.Min(100, now * 100d / total) : null));
        }
        progress?.Report(($"Downloading {Mb(0)} of {Mb(total)}", 0));
        foreach (var entry in files) Directory.CreateDirectory(Path.GetDirectoryName(target(entry))!);
        await Parallel.ForEachAsync(spans, new ParallelOptions { MaxDegreeOfParallelism = RangesAtOnce, CancellationToken = ct }, async (span, token) =>
        {
            for (var attempt = 1; ; attempt++)
            {
                long counted = 0;
                try
                {
                    await FetchSpanAsync(packUrl, packSize, span, target, fetched, n => { counted += n; Progressed(n); }, token).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempt < 3 && !token.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    Progressed(-counted);
                    AppLog.Debug($"Update: a range of the pack is asked for again ({ex.GetType().Name}).");
                    await Task.Delay(TimeSpan.FromSeconds(attempt), token).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
    }

    private async Task FetchSpanAsync(string packUrl, long packSize, AppFiles.Span span, Func<AppFiles.Entry, string> target,
        System.Collections.Concurrent.ConcurrentDictionary<string, AppFiles.Entry> fetched, Action<long> progressed, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(RangeStall);
        using var request = new HttpRequestMessage(HttpMethod.Get, packUrl);
        request.Headers.Range = new RangeHeaderValue(span.Start, span.End - 1);
        using var response = await SendFollowingAllowedRedirectsAsync(request, stall.Token, _rangeHttp).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            var code = (int)response.StatusCode;
            var message = $"The pack answered {code} to a range.";
            if (code >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException(message, null, response.StatusCode);
            throw new InvalidDataException(message);
        }
        var range = response.Content.Headers.ContentRange;
        if (range is not { Unit: "bytes", HasRange: true, HasLength: true } || range.From != span.Start || range.To != span.End - 1 || range.Length != packSize)
            throw new InvalidDataException("The pack sent another range than the one asked for.");
        if (response.Content.Headers.ContentEncoding.Count > 0) throw new InvalidDataException("The pack's range came encoded.");
        await using var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
        var counting = new CountingStream(body, n => { progressed(n); stall.CancelAfter(RangeStall); });
        var position = span.Start;
        foreach (var entry in span.Entries)
        {
            if (entry.Offset > position)
            {
                var gap = new AppFiles.ExactStream(counting, entry.Offset - position);
                await gap.CopyToAsync(Stream.Null, stall.Token).ConfigureAwait(false);
            }
            // A file asked for again (its range is retried) counts as fetched only once it checks out again.
            fetched.TryRemove(entry.Path, out _);
            await AppFiles.WriteVerifiedAsync(counting, entry, target(entry), stall.Token).ConfigureAwait(false);
            fetched[entry.Path] = entry;
            position = entry.End;
        }
    }

    /// <summary>Every file of the new folder, linked or downloaded, read again and checked against files.json; nothing else is in it.</summary>
    private static async Task VerifyStagedAsync(AppFiles.Manifest manifest, string staging, string setupFolder, Func<AppFiles.Entry, string> target, CancellationToken ct)
    {
        await Parallel.ForEachAsync(manifest.Files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (entry, token) =>
        {
            var path = target(entry);
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Size ||
                !AppFiles.FixedTimeHexEquals(await AppFiles.Sha256FileAsync(path, token).ConfigureAwait(false), entry.Sha256))
                throw new InvalidDataException($"{entry.Path} in the new folder did not verify.");
        }).ConfigureAwait(false);
        var payload = manifest.Files.Count(f => !f.Setup);
        if (AppFiles.Folder(staging).Count != payload) throw new InvalidDataException("The new folder has other files than files.json lists.");
        var setup = AppFiles.Folder(setupFolder).Count(f => !f.Path.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase));
        if (setup != manifest.Files.Count - payload) throw new InvalidDataException("Setup's files are not as files.json lists.");
    }

    private static readonly Regex StaleDeltaFolder = new(@"^app\.incoming-[0-9a-f]{32}(setup)?\z", RegexOptions.CultureInvariant);

    /// <summary>Staging folders an earlier file-level try left (a crash, power loss), and swap scripts older than an hour.</summary>
    private void RemoveStaleDeltaFolders()
    {
        void Remove(DirectoryInfo folder)
        {
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) return;
            try { DeletionGate.DeleteDirectory(folder.FullName, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Update: an old staging folder stays: " + ex.GetType().Name); }
        }
        try
        {
            if (Directory.Exists(InstallFolder))
                foreach (var folder in new DirectoryInfo(InstallFolder).EnumerateDirectories("app.incoming-*"))
                    if (StaleDeltaFolder.IsMatch(folder.Name)) Remove(folder);
            if (Directory.Exists(_updatesDirectory))
                foreach (var folder in new DirectoryInfo(_updatesDirectory).EnumerateDirectories("apply-*"))
                    if (DateTime.UtcNow - folder.CreationTimeUtc > TimeSpan.FromHours(1)) Remove(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Update: old staging folders not looked at: " + ex.GetType().Name); }
    }

    private static string Mb(long bytes) => bytes / 1048576d is var mb && mb < 10 ? $"{mb:0.0} MB" : $"{mb:0} MB";

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("Release metadata is too large.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > maximum) throw new InvalidDataException("Release metadata is too large.");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    private static (string? Location, string? Revision) ReadInstalledSetup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey);
            return (key?.GetValue("InstallLocation") as string, key?.GetValue(InstallerRevisionValue) as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return (null, null); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    private static void DefaultLinkOrCopy(string source, string target)
    {
        if (!CreateHardLink(target, source, IntPtr.Zero)) File.Copy(source, target);
    }

    /// <summary>The scripts the swap runs, from the installer's own folder (tools/installer), embedded in Exo.</summary>
    private static readonly string[] DeltaScripts = ["Apply-ExoDelta.ps1", "Complete-ExoUpdate.ps1", "Verify-Payload.ps1", "Close-ExoLauncher.ps1", "Clean-ExoInstall.ps1"];

    private static bool RunDeltaApply(DeltaApply apply)
    {
        var folder = Path.Combine(apply.UpdatesDirectory, "apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var name in DeltaScripts)
        {
            using var resource = typeof(AppUpdateService).Assembly.GetManifestResourceStream("installer/" + name)
                ?? throw new InvalidOperationException("The update's script " + name + " is not in this build.");
            using var file = File.Create(Path.Combine(folder, name));
            resource.CopyTo(file);
        }
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            // Never the app folder: the script renames it.
            WorkingDirectory = folder,
            ErrorDialog = false,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass",
                     "-File", Path.Combine(folder, "Apply-ExoDelta.ps1"), "-InstallRoot", apply.InstallRoot, "-Incoming", apply.Incoming,
                     "-SetupFiles", apply.Setup, "-Version", apply.Version, "-UpdatesDirectory", apply.UpdatesDirectory,
                     "-WaitProcessId", apply.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);
        start.Environment.Remove("EXO_SILENT_INSTALL");
        using var process = Process.Start(start);
        return process is not null;
    }

    /// <summary>A read-only stream that tells how many bytes went through it.</summary>
    private sealed class CountingStream(Stream inner, Action<long> counted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private int Count(int read)
        {
            if (read > 0) counted(read);
            return read;
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// In-app update: check + download + quiet SFX install. Never opens a browser. Asks Exo's update address
/// (cloud/updates), the one place Exo's updates come from.
/// </summary>
public sealed partial class AppUpdateService
{
    private const int MaxRetainedInstallers = 2;
    private static readonly TimeSpan InstallerRetentionAge = TimeSpan.FromDays(14);
    private static readonly HttpClient SharedHttp = CreateClient();

    private readonly HttpClient _http;
    private readonly string _updatesDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>An update of Exo itself is downloading or installing now (the Drivers list waits for it).</summary>
    internal bool Installing => _installGate.CurrentCount == 0;
    private static readonly TimeSpan ReleaseCacheAge = TimeSpan.FromMinutes(10);
    private sealed record CachedRelease(ParsedRelease Release, DateTimeOffset FetchedAt);
    private volatile CachedRelease? _cachedRelease;
    private sealed record CachedNotes(string Tag, ExoWhatsNewRelease Notes);
    private volatile CachedNotes? _cachedNotes;
    // Startup builds this before the window: the folder's cleanup runs beside it, and an install waits for it.
    private readonly Task _cleanup;
    private readonly Lock _openGate = new();
    private Task<CheckResult>? _openCheck;

    /// <summary>Closes the Lucid that Exo hosts before an installer renames its folder; the exe it ran, or null when none ran.</summary>
    internal Func<Task<string?>> StopLucid { get; init; } = async () => (await LucidCompanion.StopHostedAsync().ConfigureAwait(false)).File;

    /// <summary>Starts that Lucid again when the installer did not start.</summary>
    internal Action<string> StartLucid { get; init; } = path => LucidCompanion.Start(path);

    /// <summary>Whether this launch's check opens the update sheet (UpdateOnOpen).</summary>
    internal UpdateOnOpen OnOpen { get; } = new();

    /// <summary>
    /// The launch's one check: started as Exo opens (App.OnLaunched, before the page), then the answer the page reads as it
    /// starts, and again after a reload. Settings' Check for updates asks anew; nothing else asks while Exo runs.
    /// </summary>
    internal Task<CheckResult> OpenCheck(string localVersion)
    {
        // read-only: OpenCheck only fetches the release (HTTP GET to the update address) and keeps the answer in
        // memory (_openCheck, _cachedRelease, _cachedNotes); CheckAsync writes no file, registry value or process.
        lock (_openGate) return _openCheck ??= Task.Run(() => CheckAsync(localVersion));
    }

    public AppUpdateService()
        : this(SharedHttp, GetDefaultUpdatesDirectory(), TimeProvider.System)
    {
    }

    internal AppUpdateService(
        HttpClient http,
        string updatesDirectory,
        TimeProvider timeProvider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(updatesDirectory))
            throw new ArgumentException("An updater working directory is required.", nameof(updatesDirectory));
        _updatesDirectory = Path.GetFullPath(updatesDirectory);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        ReleaseSha256 = FetchReleaseSha256Async;
        _cleanup = Task.Run(CleanupUpdateArtifacts);
    }

    /// <summary>Exo's update address: /latest names the newest tag, /download/&lt;tag&gt;/&lt;file&gt; sends a release file.</summary>
    private static readonly Uri UpdateAddress = new("https://updates.getexo.app/v1/launcher/");
    internal const string InstallerAssetName = "ExoLauncher-Setup.exe";
    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            EnableMultipleHttp2Connections = false,
        };
        var c = ExoHttp.Create(TimeSpan.FromMinutes(30), handler);
        c.DefaultRequestVersion = HttpVersion.Version11;
        c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExoLauncher", "1.0"));
        return c;
    }

    public sealed class CheckResult
    {
        public bool UpdateAvailable { get; init; }
        public bool AlreadyLatest { get; init; }
        public string LocalVersion { get; init; } = "";
        public string RemoteVersion { get; init; } = "";
        public string Message { get; init; } = "";
        public string? DownloadUrl { get; init; }
        public long? DownloadSize { get; init; }
        public string? Sha256 { get; init; }
        public bool ShouldExit { get; init; }
        public bool Installed { get; init; }
        /// <summary>Downloaded and verified, and kept: a game is running, so it goes in later (UpdateFlow.WaitingForGame).</summary>
        public bool Staged { get; init; }
        /// <summary>The offered version's What's New; null when its release has none or it failed validation.</summary>
        internal ExoWhatsNewRelease? Upcoming { get; init; }
    }

    /// <param name="fresh">A check the user asked for skips the release cache.</param>
    public async Task<CheckResult> CheckAsync(string localVersion, CancellationToken ct = default, bool fresh = false)
    {
        Move(UpdateFlow.Step.Check);
        CheckResult result;
        try { result = await CheckOnceAsync(localVersion, ct, fresh).ConfigureAwait(false); }
        catch { Move(UpdateFlow.Step.CheckFailed); throw; }
        Move(result.UpdateAvailable || result.AlreadyLatest ? UpdateFlow.Step.CheckDone : UpdateFlow.Step.CheckFailed);
        return result;
    }

    private async Task<CheckResult> CheckOnceAsync(string localVersion, CancellationToken ct, bool fresh)
    {
        var local = Normalize(localVersion);
        try
        {
            // The update address's 10 seconds, then its .sha256 and What's New, within one budget.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var release = await LoadLatestReleaseAsync(local, timeout.Token, fresh).ConfigureAwait(false);
            if (release is null)
            {
                return new CheckResult
                {
                    LocalVersion = local,
                    RemoteVersion = "?",
                    Message = "Could not check for updates. Try again in a minute.",
                };
            }

            var remote = Normalize(release.Tag);
            if (VersionNumber.Parse(local) is null || VersionNumber.Parse(remote) is null)
            {
                return new CheckResult
                {
                    LocalVersion = local,
                    RemoteVersion = remote,
                    Message = $"Could not compare versions. The latest is v{remote}; you have v{local}.",
                };
            }

            var order = VersionNumber.Compare(local, remote);
            if (order > 0)
            {
                return new CheckResult
                {
                    AlreadyLatest = true,
                    LocalVersion = local,
                    RemoteVersion = remote,
                    Message = $"You have v{local}, which is newer than the latest release (v{remote}).",
                };
            }

            if (order == 0)
            {
                return new CheckResult
                {
                    AlreadyLatest = true,
                    LocalVersion = local,
                    RemoteVersion = remote,
                    Message = $"You're up to date (v{local}).",
                };
            }

            var available = HasVerifiedInstallerMetadata(release);
            return new CheckResult
            {
                UpdateAvailable = available,
                LocalVersion = local,
                RemoteVersion = remote,
                DownloadUrl = available ? release.DownloadUrl : null,
                DownloadSize = release.Size,
                Sha256 = release.Sha256,
                Upcoming = available ? await LoadWhatsNewAsync(release, ct).ConfigureAwait(false) : null,
                Message = available
                    ? $"Version {remote} is available (you have v{local})."
                    : $"Version {remote} is out, but it cannot be verified yet. Check again later.",
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CheckResult
            {
                LocalVersion = local,
                Message = "The update check timed out. Try again in a minute.",
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn("Update check failed: " + ex.GetType().Name + ": " + ex.Message);
            return new CheckResult
            {
                LocalVersion = local,
                Message = "Could not check for updates. Try again in a minute.",
            };
        }
    }

    public async Task<CheckResult> InstallAsync(
        string localVersion,
        IProgress<(string status, double? percent)>? progress = null,
        CancellationToken ct = default)
    {
        if (HostMode.IsDisposable) return new CheckResult { Message = HostMode.Refusal };
        var entered = false;
        try
        {
            await _installGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            return await InstallSerializedAsync(localVersion, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = Normalize(localVersion),
                Message = "Update canceled. Nothing was installed.",
            };
        }
        finally
        {
            if (entered)
                _installGate.Release();
        }
    }

    private async Task<CheckResult> InstallSerializedAsync(
        string localVersion,
        IProgress<(string status, double? percent)>? progress,
        CancellationToken ct)
    {
        // CheckAsync bounds the metadata requests itself. The installer body has
        // no whole-transfer deadline here; ResumableDownload's stall timeout
        // ends a dead connection, so a slow one can still finish.
        var check = await CheckAsync(localVersion, ct).ConfigureAwait(false);
        if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.DownloadUrl))
        {
            // Update now was pressed and could not even begin (offline): that is a failed install, said as one. A check
            // alone failing is not (UpdateFlow: Checking + CheckFailed is Idle).
            if (!check.AlreadyLatest && State is not (UpdateFlow.State.Restarting or UpdateFlow.State.Applying))
            {
                Move(UpdateFlow.Step.Install);
                Move(UpdateFlow.Step.DownloadFailed);
            }
            return check;
        }
        // The two versions never change during an install; copied once so every later result reads a plain string.
        var installedVersion = check.LocalVersion;
        var offeredVersion = check.RemoteVersion;

        if (!Uri.TryCreate(check.DownloadUrl, UriKind.Absolute, out var uri) ||
            !IsAllowedDownloadUri(uri))
        {
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = "The update is not from Exo's official releases. Nothing was installed.",
            };
        }

        if (string.IsNullOrWhiteSpace(check.Sha256))
        {
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = "The update could not be verified. Nothing was installed.",
            };
        }

        // The installer already runs and Exo is closing for it: nothing more starts.
        if (State is UpdateFlow.State.Restarting or UpdateFlow.State.Applying)
            return new CheckResult { UpdateAvailable = true, LocalVersion = installedVersion, RemoteVersion = offeredVersion, Message = "Exo is restarting for the update." };
        Move(UpdateFlow.Step.Install);
        string? artifactPath = null;
        var installerStarted = false;
        var keepStaged = false;
        var swapping = false;
        string? lucidStopped = null;
        try
        {
            await _cleanup.ConfigureAwait(false);
            Directory.CreateDirectory(_updatesDirectory);
            // An installer this same release already downloaded and verified (a game was running then) is used as it is.
            var kept = await StagedInstallerAsync(offeredVersion, check.Sha256, ct).ConfigureAwait(false);
            var setupPath = kept?.Path;
            var record = kept?.Record;
            if (setupPath is null)
            {
                if (check.DownloadSize is > 0 && SpaceShortfall(check.DownloadSize.Value) is { } space)
                {
                    Move(UpdateFlow.Step.DownloadFailed);
                    return new CheckResult
                    {
                        UpdateAvailable = true,
                        LocalVersion = installedVersion,
                        RemoteVersion = offeredVersion,
                        Message = space,
                    };
                }
                progress?.Report(("Downloading…", 0));
                var artifactId = Guid.NewGuid().ToString("N");
                var partialPath = Path.Combine(_updatesDirectory, $"ExoLauncher-Setup-{artifactId}.partial");
                artifactPath = partialPath;
                await ResumableDownload.DownloadAsync(uri, partialPath, _updatesDirectory,
                    (request, token) => SendFollowingAllowedRedirectsAsync(request, token),
                    check.DownloadSize, check.Sha256, (written, total) =>
                    {
                        if (total is > 0) progress?.Report(("Downloading…", Math.Min(100, written * 100d / total.Value)));
                        else progress?.Report(($"Downloading… {written / 1048576d:0.0} MB", null));
                    }, ct).ConfigureAwait(false);

                if (check.DownloadSize is > 0 && new FileInfo(partialPath).Length != check.DownloadSize.Value)
                {
                    CrashReport.Submit("UpdateFailure", "Update download was the wrong size", "update");
                    Move(UpdateFlow.Step.DownloadFailed);
                    return new CheckResult
                    {
                        UpdateAvailable = true,
                        LocalVersion = check.LocalVersion,
                        RemoteVersion = check.RemoteVersion,
                        Message = "The update download was the wrong size. Nothing was installed.",
                    };
                }

                progress?.Report(("Verifying…", null));
                var hash = await Sha256FileAsync(partialPath, ct).ConfigureAwait(false);
                if (!TimingSafeHashEquals(hash, check.Sha256))
                {
                    CrashReport.Submit("UpdateFailure", "Update download did not match its SHA-256", "update");
                    Move(UpdateFlow.Step.DownloadFailed);
                    return new CheckResult
                    {
                        UpdateAvailable = true,
                        LocalVersion = check.LocalVersion,
                        RemoteVersion = check.RemoteVersion,
                        Message = "The update download did not match the release. Nothing was installed.",
                    };
                }

                if (!IsInstaller(partialPath))
                {
                    Move(UpdateFlow.Step.DownloadFailed);
                    return new CheckResult
                    {
                        UpdateAvailable = true,
                        LocalVersion = check.LocalVersion,
                        RemoteVersion = check.RemoteVersion,
                        Message = "The update file is not an Exo installer. Nothing was installed.",
                    };
                }

                // The executable name only appears after every pre-launch check has passed. If anything below fails
                // before the installer starts (and it is not kept for after a game), the finally block removes it.
                setupPath = Path.Combine(_updatesDirectory, $"ExoLauncher-Setup-{artifactId}.exe");
                File.Move(partialPath, setupPath);
                artifactPath = setupPath;
                record = new StagedUpdate(offeredVersion, check.Sha256!, new FileInfo(setupPath).Length, Path.GetFileName(setupPath));
                _ = TryWriteStaged(_updatesDirectory, record);
                Move(UpdateFlow.Step.Verified);
            }
            else
            {
                artifactPath = setupPath;
                Move(UpdateFlow.Step.Verified);
            }
            var hasRecord = ReadStaged(_updatesDirectory) is not null;

            // Never while a game runs: the verified installer waits, and goes in at the next start or the next Update now.
            if (Move(UpdateFlow.Step.Apply) != UpdateFlow.State.Applying)
            {
                // Kept only with its record: without one the next start would not know of it.
                if (!hasRecord) throw new IOException("The kept-update record could not be written.");
                keepStaged = true;
                AppLog.Info($"Update {offeredVersion}: verified and kept; a game is running, so it goes in later.");
                return new CheckResult
                {
                    UpdateAvailable = true,
                    Staged = true,
                    LocalVersion = installedVersion,
                    RemoteVersion = offeredVersion,
                    Message = WaitsForGameMessage,
                };
            }

            swapping = true;
            progress?.Report(("Installing…", null));
            // Lucid runs from the app folder the installer renames and outlives a close it can't answer: stopped here, where
            // it can still be started again if the installer doesn't. A finished update starts Exo, which starts Lucid.
            lucidStopped = await StopLucid().ConfigureAwait(false);
            // One more start counted on disk first: an installer that fails and rolls back runs at most MaxStarts times. A record
            // Windows won't let Exo write does not stop the update the player asked for (the next start ignores such a record).
            if (!CountStart(_updatesDirectory, record!)) AppLog.Warn("Update: this start of the installer is not counted.");
            if (!StartInstaller(setupPath))
                throw new InvalidOperationException("Windows did not start the verified installer.");
            installerStarted = true;
            Move(UpdateFlow.Step.InstallerStarted);

            try { await Task.Delay(400, ct).ConfigureAwait(false); } catch { /* */ }
            progress?.Report(("Restarting…", null));
            Helpers.Telemetry.Track("update.result", ("from", check.LocalVersion ?? ""), ("to", check.RemoteVersion ?? ""), ("ok", true), ("error_kind", "none"));
            return new CheckResult
            {
                UpdateAvailable = true,
                Installed = true,
                ShouldExit = true,
                LocalVersion = installedVersion,
                RemoteVersion = offeredVersion,
                Message = $"Applying v{check.RemoteVersion}… Exo Launcher will close and reopen.",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Move(UpdateFlow.Step.DownloadFailed);
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            Move(UpdateFlow.Step.DownloadFailed);
            CrashReport.Submit("UpdateFailure", "Update download timed out", "update");
            Helpers.Telemetry.Track("update.result", ("from", check.LocalVersion ?? ""), ("to", check.RemoteVersion ?? ""), ("ok", false), ("error_kind", "timeout"));
            // A stalled or timed-out transfer is not a user cancel.
            // ResumableDownload keeps its SHA-keyed partial (strong ETag only),
            // so a retry resumes it.
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = installedVersion,
                RemoteVersion = offeredVersion,
                Message = "The update download timed out. Try again.",
            };
        }
        catch (Exception ex)
        {
            Move(swapping ? UpdateFlow.Step.ApplyFailed : UpdateFlow.Step.DownloadFailed);
            // A file or Windows error names a path under the updates folder: the log has it, the page does not.
            AppLog.Warn("Update install failed: " + ex.GetType().Name + ": " + ex.Message);
            Helpers.Telemetry.Track("update.result", ("from", check.LocalVersion ?? ""), ("to", check.RemoteVersion ?? ""), ("ok", false),
                ("error_kind", swapping ? "install" : "other"));
            CrashReport.Submit("UpdateFailure", swapping ? "Update install failed" : FailureReportRules.UpdateDownloadReason(ex), "update");
            return new CheckResult
            {
                UpdateAvailable = true,
                LocalVersion = installedVersion,
                RemoteVersion = offeredVersion,
                Message = "The update didn't install. Try again; the details are in the log.",
            };
        }
        finally
        {
            // A successfully launched installer may still be executing and Windows will not let it delete itself: kept for the
            // bounded age/count cleanup on the next start. One kept for after a game stays too, with its record.
            if (!installerStarted && !keepStaged && artifactPath is not null)
            {
                TryDeleteOwnedArtifact(artifactPath);
                if (artifactPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) DiscardStaged(_updatesDirectory, null);
            }
            if (!installerStarted && lucidStopped is not null) StartLucid(lucidStopped);
        }
    }

    /// <summary>The release asset tools/Build-Release.ps1 writes beside the installer: that version's What's New.</summary>
    internal const string WhatsNewAssetName = "whats-new.json";
    internal const int MaxWhatsNewBytes = 64 * 1024;
    internal const int MaxWhatsNewItems = 30;

    internal static bool IsUpdateAssetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Equals("ExoLauncher.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("ExoLauncher-Setup.exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAllowedDownloadUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.Host.Equals(UpdateAddress.Host, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendFollowingAllowedRedirectsAsync(
        HttpRequestMessage initialRequest,
        CancellationToken ct)
    {
        var current = initialRequest.RequestUri ?? throw new InvalidOperationException("Missing download URL.");
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsAllowedDownloadUri(current))
                throw new InvalidOperationException("Update redirected outside Exo's update hosts.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            foreach (var header in initialRequest.Headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400)
            {
                if (response.RequestMessage?.RequestUri is { } final && !IsAllowedDownloadUri(final))
                {
                    response.Dispose();
                    throw new InvalidOperationException("Update redirected outside Exo's update hosts.");
                }
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new InvalidOperationException("Update redirect did not include a destination.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new InvalidOperationException("Update redirected too many times.");
    }

    internal static string DefaultUpdatesDirectory => GetDefaultUpdatesDirectory();

    private static string GetDefaultUpdatesDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExoLauncher",
            "updates");

    private void CleanupUpdateArtifacts()
    {
        // The updates folder is the installed Exo's (not the data folder): a test instance deletes nothing there.
        if (HostMode.IsDisposable) return;
        try
        {
            if (!Directory.Exists(_updatesDirectory))
                return;

            // A cancelled or stalled update leaves its partial download (up to 512 MB); it is cleaned here, not only by the next one.
            ResumableDownload.CleanupAbandoned(_updatesDirectory);
            var installers = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(_updatesDirectory, "ExoLauncher-Setup-*", SearchOption.TopDirectoryOnly))
            {
                if (!TryClassifyArtifact(path, out var partial))
                    continue;

                if (partial)
                {
                    // An active download is opened FileShare.None and cannot be
                    // acquired here. Any unlocked partial belongs to an
                    // interrupted updater and is safe to remove on startup.
                    TryDeleteOwnedArtifact(path);
                    continue;
                }

                try { installers.Add(new FileInfo(path)); }
                catch { /* cleanup is best effort */ }
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var ordered = installers
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var age = now - ordered[index].LastWriteTimeUtc;
                if (age >= InstallerRetentionAge || index >= MaxRetainedInstallers)
                    TryDeleteOwnedArtifact(ordered[index].FullName);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Updater artifact cleanup skipped: " + ex.Message);
        }
    }

    private bool TryDeleteOwnedArtifact(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(
                    Path.GetDirectoryName(fullPath),
                    _updatesDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !TryClassifyArtifact(fullPath, out _))
            {
                return false;
            }

            if (!File.Exists(fullPath))
                return true;
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                return false;

            // DeleteOnClose makes the exclusivity check and deletion one file
            // operation. Locked downloads and running installers are skipped.
            using var owned = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryClassifyArtifact(string path, out bool partial)
    {
        partial = false;
        var fileName = Path.GetFileName(path);
        const string prefix = "ExoLauncher-Setup-";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string id;
        if (fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
        {
            partial = true;
            id = fileName[prefix.Length..^".partial".Length];
        }
        else if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            id = fileName[prefix.Length..^".exe".Length];
        }
        else
        {
            return false;
        }

        return Guid.TryParseExact(id, "N", out _);
    }

    internal static bool IsExpectedInstaller(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(info.ProductName, "Exo Launcher", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(info.FileDescription, "Exo Launcher Setup", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TimingSafeHashEquals(string? computed, string? expected)
    {
        if (computed is null || expected is null || computed.Length != expected.Length) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(computed),
                Convert.FromHexString(expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal sealed record ParsedRelease(
        string Tag,
        string? DownloadUrl,
        string? AssetName,
        long? Size,
        string? Sha256,
        string? WhatsNewUrl = null);

    private static bool HasVerifiedInstallerMetadata(ParsedRelease release) =>
        NormalizeSha(release.Sha256) is not null && IsOfferedInstaller(release);

    /// <summary>
    /// The update address's installer, with no size (its .sha256 is asked for) or with the size its manifest gives.
    /// Either way the download is held to ResumableDownload's limit.
    /// </summary>
    private static bool IsOfferedInstaller(ParsedRelease release) =>
        (release.Size is null && ReleaseTag().IsMatch(release.Tag) && release.AssetName == InstallerAssetName &&
         release.DownloadUrl == UpdateAddressFile(release.Tag, InstallerAssetName)) ||
        // The update address's own installer, with the size and SHA-256 its manifest gives (see ParseUpdateAddressReply).
        (release.Size is > 0 and <= ResumableDownload.MaxInstallerBytes && ReleaseTag().IsMatch(release.Tag) &&
         release.AssetName == InstallerAssetName && release.DownloadUrl == UpdateAddressFile(release.Tag, InstallerAssetName));

    /// <summary>A file of the release, on the update address. The tag is a checked vX.Y.Z, so nothing needs escaping.</summary>
    internal static string UpdateAddressFile(string tag, string name) => UpdateAddress.AbsoluteUri + "download/" + tag + "/" + name;

    /// <summary>
    /// The update address's answer, { "app": "launcher", "tag": "vX.Y.Z", ... }: that release, with its file URLs built
    /// here on the address. The answer's file URLs are never read; its "checks" (the installer's size and SHA-256, from the
    /// manifest) are, when both are valid, so the check needs no second request. Null for another app, any other tag, or not JSON.
    /// </summary>
    internal static ParsedRelease? ParseUpdateAddressReply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.String || app.GetString() != "launcher" ||
                !root.TryGetProperty("tag", out var t) || t.ValueKind != JsonValueKind.String ||
                t.GetString() is not { } tag || !ReleaseTag().IsMatch(tag))
                return null;
            var (size, sha) = ReadInstallerCheck(root);
            return new ParsedRelease(tag, UpdateAddressFile(tag, InstallerAssetName), InstallerAssetName, size, sha,
                UpdateAddressFile(tag, WhatsNewAssetName));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The installer's size and SHA-256 from the update address's "checks" (schema 2). Both or neither: a size without a
    /// valid digest (or the reverse) is ignored, and the .sha256 is then asked for as before.
    /// </summary>
    private static (long? Size, string? Sha256) ReadInstallerCheck(JsonElement root)
    {
        if (!root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Object ||
            !checks.TryGetProperty("setup", out var setup) || setup.ValueKind != JsonValueKind.Object)
            return (null, null);
        if (!setup.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number ||
            !sizeValue.TryGetInt64(out var size) || size is <= 0 or > ResumableDownload.MaxInstallerBytes)
            return (null, null);
        if (!setup.TryGetProperty("sha256", out var shaValue) || shaValue.ValueKind != JsonValueKind.String ||
            NormalizeSha(shaValue.GetString()) is not { } sha)
            return (null, null);
        return (size, sha);
    }

    [GeneratedRegex(@"^v[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTag();

    private async Task<ParsedRelease?> LoadLatestReleaseAsync(string local, CancellationToken ct, bool fresh = false)
    {
        // The startup check and Install ask for the same release. A user-initiated check
        // always asks again, and refreshes the cache Install then reads.
        if (!fresh && _cachedRelease is { } cached && _timeProvider.GetUtcNow() - cached.FetchedAt < ReleaseCacheAge)
            return cached.Release;
        var release = await FetchFromUpdateAddressAsync(local, ct).ConfigureAwait(false);
        if (release is not null && HasVerifiedInstallerMetadata(release))
            _cachedRelease = new CachedRelease(release, _timeProvider.GetUtcNow());
        return release;
    }

    /// <summary>
    /// The release Exo's update address names, with its SHA-256 when it is newer than <paramref name="local"/> (the
    /// .sha256 comes through the address too). Null when the address doesn't answer within 10 seconds, answers wrong, or
    /// can't send a newer release's .sha256: the check then fails cleanly. A manifest's size and SHA-256 (checks) are taken
    /// as they are; without them the .sha256 is asked for here.
    /// </summary>
    private async Task<ParsedRelease?> FetchFromUpdateAddressAsync(string local, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(UpdateAddress, "latest"));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            var release = ParseUpdateAddressReply(await ReadBoundedTextAsync(resp.Content, 4096, timeout.Token).ConfigureAwait(false));
            if (release is null) return null;
            // Up to date: the files aren't needed, so the address isn't asked for them.
            if (VersionNumber.AtLeast(local, release.Tag)) return release;
            release = await CompleteDigestAsync(release, timeout.Token).ConfigureAwait(false);
            return HasVerifiedInstallerMetadata(release) ? release : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Debug("Exo's update address timed out.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Debug("Exo's update address unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    private async Task<ParsedRelease> CompleteDigestAsync(ParsedRelease release, CancellationToken ct)
    {
        if (release.Sha256 is not null || !IsOfferedInstaller(release)) return release;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl + ".sha256");
            using var response = await SendFollowingAllowedRedirectsAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return release;
            var text = await ReadBoundedTextAsync(response.Content, 4096, ct).ConfigureAwait(false);
            return ParseSha256File(text, release.AssetName) is { } sha ? release with { Sha256 = sha } : release;
        }
        catch (OperationCanceledException) { throw; }
        catch { return release; }
    }

    /// <summary>
    /// The offered release's whats-new.json, through the installer's own host and redirect rules, at most 64 KB.
    /// Anything wrong (no asset, an error, a timeout, a file that fails ParseWhatsNew) is no notes, never a failed check.
    /// </summary>
    private async Task<ExoWhatsNewRelease?> LoadWhatsNewAsync(ParsedRelease release, CancellationToken ct)
    {
        if (release.WhatsNewUrl is null) return null;
        if (_cachedNotes is { } cached && cached.Tag == release.Tag) return cached.Notes;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, release.WhatsNewUrl);
            using var response = await SendFollowingAllowedRedirectsAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var text = await ReadBoundedTextAsync(response.Content, MaxWhatsNewBytes, timeout.Token).ConfigureAwait(false);
            var notes = ParseWhatsNew(text, release.Tag);
            if (notes is not null) _cachedNotes = new CachedNotes(release.Tag, notes);
            return notes;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AppLog.Debug("What's New for the update unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// A whats-new.json: { "version": "X.Y.Z" (the release's tag), "items": [1 to 30 of { "icon": a short Latin name,
    /// "title": up to 80 characters, "detail": up to 240 }] }. Null for anything else: the UI shows only what passed.
    /// </summary>
    internal static ExoWhatsNewRelease? ParseWhatsNew(string json, string tag)
    {
        try
        {
            if (json.Length > MaxWhatsNewBytes) return null;
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            var version = v.GetString()!;
            if (version.Length > 20 || !version.All(c => char.IsAsciiDigit(c) || c == '.') ||
                !Version.TryParse(version, out _) || version != Normalize(tag))
                return null;
            var count = list.GetArrayLength();
            if (count is < 1 or > MaxWhatsNewItems) return null;
            var items = new ExoWhatsNewItem[count];
            var index = 0;
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) return null;
                var icon = Text(entry, "icon", 32);
                var title = Text(entry, "title", 80);
                var detail = Text(entry, "detail", 240);
                if (icon is null || title is null || detail is null || !icon.All(char.IsAsciiLetter)) return null;
                items[index++] = new ExoWhatsNewItem { Icon = icon, Title = title, Detail = detail };
            }
            return new ExoWhatsNewRelease { Version = version, Items = items };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }

        static string? Text(JsonElement entry, string name, int max) =>
            entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text && text.Length <= max && !string.IsNullOrWhiteSpace(text) &&
            !text.Any(char.IsControl) ? text : null;
    }

    private static async Task<string> ReadBoundedTextAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("Release metadata is too large.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > maximum) throw new InvalidDataException("Release metadata is too large.");
            bytes.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    internal static string Normalize(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "0.0.0";
        var core = v.Trim();
        if (core.StartsWith("v", StringComparison.OrdinalIgnoreCase) ||
            core.StartsWith("V", StringComparison.Ordinal))
            core = core[1..];
        var cut = core.IndexOfAny(['+', '-']);
        if (cut >= 0) core = core[..cut];
        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        while (parts.Length < 3) parts = [.. parts, "0"];
        return string.Join('.', parts.Take(3));
    }

    /// <summary>A "<hex> [*name]" .sha256 file: the hash when it is well formed and names this asset, else null.</summary>
    internal static string? ParseSha256File(string text, string? assetName)
    {
        var tokens = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > 2 || tokens.Length == 2 && tokens[1].TrimStart('*') != assetName)
            return null;
        return NormalizeSha(tokens[0]);
    }

    internal static string? NormalizeSha(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var s = digest.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            s = s["sha256:".Length..];
        s = s.ToLowerInvariant();
        return s.Length == 64 && s.All(Uri.IsHexDigit) ? s : null;
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

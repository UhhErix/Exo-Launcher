using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// New upscaler SDKs reach people without an Exo update. When Exo opens (at most hourly) it asks each vendor's
/// repository (mirrored in Exo's cache, which holds each GitHub path) for its latest release, which Exo's cache lists (the vendor's GitHub release): NVIDIA's DLSS, AMD's FSR SDK (FSR 4, which DLSS Swapper's manifest
/// does not carry) and Intel's XeSS. A release newer than every one Exo knows has its DLLs fetched from the release's
/// commit, only those a scanned game ships. Each must match the size and git blob hash its listing states and pass
/// the checks every upscaler file passes (the vendor's Authenticode signature, identity, exports, x64); its version
/// is then read from the file and the release is kept in vendor-releases.json with the SHA-256 it was verified at,
/// used from then on like a pin.
/// </summary>
public sealed partial class DlssSwapService
{
    /// <summary>A vendor's SDK repository: where its DLLs sit in a release, and how Exo names the release.</summary>
    internal sealed record VendorSdk(string Repo, string Folder, string[] Files, string ReleasePrefix, string Name, int MinMajor = 0);

    internal static readonly VendorSdk[] VendorSdks =
    [
        new(DlssRepo, "lib/Windows_x86_64/rel", ["nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll"], "dlss-", "DLSS"),
        // FSR SDK 2 and later; FSR 3.1's own builds come from DLSS Swapper's manifest.
        new(FfxRepo, "Kits/FidelityFX/signedbin", ["amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll",
            "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_denoiser_dx12.dll", "amd_fidelityfx_radiancecache_dx12.dll"],
            "fsr-", "FSR SDK", MinMajor: 2),
        new(XessRepo, "bin", ["libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "libxell.dll"], "xess-", "XeSS SDK"),
    ];

    private static readonly TimeSpan VendorWatchMinGap = TimeSpan.FromHours(1);
    private static IReadOnlyList<PinnedFile>? _watched;
    private static Task? _vendorWatch;

    private static string WatchedPath => Path.Combine(CacheRoot, "vendor-releases.json");
    private static string WatchedCheckedPath => Path.Combine(CacheRoot, "vendor-checked");

    private static IReadOnlyList<PinnedFile> LoadWatched()
    {
        try
        {
            if (!File.Exists(WatchedPath)) return [];
            var files = JsonSerializer.Deserialize<List<PinnedFile>>(File.ReadAllText(WatchedPath)) ?? [];
            // Only what the watch writes: a vendor's repository at a commit, with the SHA-256 it was verified at.
            return files.Where(file => file.Sha256.Length == 64 && file.Zip is null && IsVendorSdkUrl(file.Url)).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Debug("Upscaler releases from the cache unreadable: " + ex.Message);
            return [];
        }
    }

    /// <summary>One of a vendor's SDK DLLs at a commit: the only place the watch fetches from.</summary>
    internal static bool IsVendorSdkUrl(string url)
    {
        foreach (var sdk in VendorSdks)
        {
            var prefix = $"https://{VerifiedGitHubReleaseDownloader.CacheHost}/v1/cache/{sdk.Repo}/";
            if (!url.StartsWith(prefix, StringComparison.Ordinal) || url.Length < prefix.Length + 41) continue;
            var commit = url.AsSpan(prefix.Length, 40);
            if (url[prefix.Length + 40] != '/' || !commit.ToString().All(char.IsAsciiHexDigitLower)) continue;
            var rest = url[(prefix.Length + 41)..];
            if (sdk.Files.Any(name => rest == sdk.Folder + "/" + name)) return true;
        }
        return false;
    }

    /// <summary>The newest release of a vendor's SDK Exo has, pinned or found: what a GitHub release must beat.</summary>
    private static Version NewestKnown(VendorSdk sdk) =>
        AllReleases
            .Where(file => file.Repo == sdk.Repo && file.Release.StartsWith(sdk.ReleasePrefix, StringComparison.Ordinal))
            .Select(file => TryParseVersion(file.Release[sdk.ReleasePrefix.Length..]) ?? new Version(0, 0))
            .Where(version => version.Major >= sdk.MinMajor)
            .DefaultIfEmpty(new Version(0, 0))
            .Max()!;

    private static IReadOnlyList<PinnedFile> Watched()
    {
        lock (CatalogGate)
            return _watched ??= LoadWatched();
    }

    private static readonly TimeSpan StartupQuiet = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GameWait = TimeSpan.FromMinutes(1);
    private static int _watchAsked;
    private static int _watchQueued;

    /// <summary>
    /// On opening Exo (App.OnLaunched), and again for a status read an hour later: DLSS Swapper's list, then
    /// each vendor's latest SDK, in the background. Never on the caller's thread (App calls it on the UI thread),
    /// Exo's start only once its first screen has settled, and never while a game runs: a new SDK's files are
    /// tens of megabytes. One ask waits at a time.
    /// </summary>
    internal static void WatchUpscalerReleases()
    {
        var startup = Interlocked.Exchange(ref _watchAsked, 1) == 0;
        if (Interlocked.Exchange(ref _watchQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (startup) await Task.Delay(StartupQuiet).ConfigureAwait(false);
                while (App.Services.Launcher.AnyGameRunning || LeanSignal.Active) await Task.Delay(GameWait).ConfigureAwait(false);
            }
            finally { Volatile.Write(ref _watchQueued, 0); }
            lock (CatalogGate)
            {
                if (_vendorWatch is { IsCompleted: false }) return;
                try
                {
                    var info = new FileInfo(WatchedCheckedPath);
                    if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < VendorWatchMinGap) return;
                }
                catch { return; }
                _vendorWatch = Task.Run(WatchVendorsAsync);
            }
        });
    }

    private static async Task WatchVendorsAsync()
    {
        try
        {
            // The setting decides what a pass may fetch. On: the DLLs and archives download and verify. Off (the default): only the small
            // answers (each vendor's newest release, the Swapper catalog) are read, and a newer version is listed for the user to download.
            var download = App.Services.Settings.Current.AutoUpdateUpscalers;
            var used = CurrentDetectionCache().FileNames();
            var found = new List<AvailableUpscaler>();
            Directory.CreateDirectory(CacheRoot);
            File.WriteAllText(WatchedCheckedPath, "");
            foreach (var sdk in VendorSdks)
            {
                if (!sdk.Files.Any(used.Contains)) continue;
                try { if (download) await WatchVendorAsync(sdk, used).ConfigureAwait(false); else await CheckVendorAsync(sdk, found).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Info($"{sdk.Name} releases not checked (the ones Exo has still apply): {ex.Message}"); }
            }
            try { if (download) await WatchSwapperCatalogAsync(used).ConfigureAwait(false); else await CheckSwapperCatalogAsync(used, found).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Info("DLSS Swapper archives not checked (the ones Exo has still apply): " + ex.Message); }
            SaveAvailable(found);
        }
        catch (Exception ex) { AppLog.Info("Upscaler releases not checked: " + ex.Message); }
    }

    /// <summary>A newer upscaler the watch found but did not download (the setting is off). Family is DLSS, FSR or XeSS.</summary>
    internal sealed record AvailableUpscaler(string Family, string Version);

    private static string AvailablePath => Path.Combine(CacheRoot, "upscaler-available.json");

    /// <summary>What the last pass found newer and left on disk for the user to download. Unreadable or absent: nothing.</summary>
    internal static IReadOnlyList<AvailableUpscaler> AvailableUpscalers()
    {
        try
        {
            return File.Exists(AvailablePath)
                ? JsonSerializer.Deserialize<List<AvailableUpscaler>>(File.ReadAllText(AvailablePath)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            AppLog.Debug("Upscaler availability unreadable: " + ex.Message);
            return [];
        }
    }

    private static void SaveAvailable(IReadOnlyList<AvailableUpscaler> found)
    {
        try
        {
            Directory.CreateDirectory(CacheRoot);
            File.WriteAllText(AvailablePath, JsonSerializer.Serialize(found));
        }
        catch (Exception ex)
        {
            AppLog.Debug("Upscaler availability not saved: " + ex.Message);
        }
    }

    private static string FamilyOf(VendorSdk sdk) => sdk.Name switch { "DLSS" => "DLSS", "FSR SDK" => "FSR", _ => "XeSS" };

    private static string FamilyOfFile(string file) => file.StartsWith("nvngx_", StringComparison.Ordinal) ? "DLSS" : "XeSS";

    /// <summary>The setting-off check: the vendor's newest release, one small answer. A tag newer than every known one is listed; nothing is downloaded.</summary>
    private static async Task CheckVendorAsync(VendorSdk sdk, List<AvailableUpscaler> found)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var api = $"https://{VerifiedGitHubReleaseDownloader.CacheHost}/v1/cache/" + sdk.Repo;
        var tag = (await GetCacheJsonAsync(api + "/releases/latest", timeout.Token).ConfigureAwait(false))?["tag_name"]?.GetValue<string>();
        if (tag is null || TryParseVersion(tag.TrimStart('v', 'V')) is not { } version || version.Major < sdk.MinMajor || version <= NewestKnown(sdk)) return;
        found.Add(new AvailableUpscaler(FamilyOf(sdk), TrimVersion(tag.TrimStart('v', 'V'))!));
    }

    /// <summary>The setting-off check for DLSS Swapper: the catalog (one small answer), listing the newest archive per file that a scanned game ships and that is not refused.</summary>
    private static async Task CheckSwapperCatalogAsync(IReadOnlySet<string> used, List<AvailableUpscaler> found)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var entries = await ReadCatalogEntriesAsync(timeout.Token).ConfigureAwait(false);
        if (entries is null) return;
        var refused = LoadRefusals();
        var now = DateTime.UtcNow;
        foreach (var file in SwapperFiles.Where(name => used.Contains(name)))
        {
            var known = AllReleases.Where(pin => pin.FileName.Equals(file, StringComparison.OrdinalIgnoreCase)).Select(pin => pin.Version).ToList();
            if (SwapperChoices(entries, used, known, refused, now, file) is { Count: > 0 } choices)
                found.Add(new AvailableUpscaler(FamilyOfFile(file), choices[0].Version));
        }
    }

    /// <summary>
    /// The release check for one DLL: the vendor's own rule, except XeSS, whose DLL major differs from its SDK tag (3.0.2 ships libxess.dll 2.0.2.68).
    /// </summary>
    private static string? ReleaseRejectionFor(VendorSdk sdk, string release, string fileVersion, string name)
    {
        var dllVersions = AllReleases.Where(known => known.FileName == name).Select(known => known.Version).ToList();
        if (sdk.ReleasePrefix != "xess-") return VendorReleaseRejection(release, fileVersion, dllVersions);
        var tags = AllReleases.Where(known => known.Repo == sdk.Repo && known.Release.StartsWith(sdk.ReleasePrefix, StringComparison.Ordinal))
            .Select(known => known.Release[sdk.ReleasePrefix.Length..]).ToList();
        return DllTagRejection(release, fileVersion, dllVersions, tags);
    }

    /// <summary>
    /// The user's click on "A newer DLSS is available": downloads and verifies what the last check listed, through the same checks as the
    /// setting-on pass. Null when it ran; the failure's message otherwise. The list is cleared once the attempt has run.
    /// </summary>
    internal static async Task<string?> DownloadAvailableUpscalersAsync(CancellationToken ct)
    {
        var wanted = AvailableUpscalers();
        if (wanted.Count == 0) return "Nothing newer to download.";
        try
        {
            var used = CurrentDetectionCache().FileNames();
            foreach (var sdk in VendorSdks.Where(sdk => wanted.Any(item => item.Family == FamilyOf(sdk)) && sdk.Files.Any(used.Contains)))
                await WatchVendorAsync(sdk, used).ConfigureAwait(false);
            if (wanted.Any(item => item.Family is "DLSS" or "XeSS")) await WatchSwapperCatalogAsync(used).ConfigureAwait(false);
            SaveAvailable([]);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Info("Upscaler download not finished: " + ex.Message);
            return ex.Message;
        }
    }

    private static async Task WatchVendorAsync(VendorSdk sdk, IReadOnlySet<string> used)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        var api = $"https://{VerifiedGitHubReleaseDownloader.CacheHost}/v1/cache/" + sdk.Repo;
        var tag = (await GetCacheJsonAsync(api + "/releases/latest", ct).ConfigureAwait(false))?["tag_name"]?.GetValue<string>();
        if (tag is null || TryParseVersion(tag.TrimStart('v', 'V')) is not { } version || version.Major < sdk.MinMajor || version <= NewestKnown(sdk)) return;
        var commit = (await GetCacheJsonAsync(api + "/commits/" + Uri.EscapeDataString(tag), ct).ConfigureAwait(false))?["sha"]?.GetValue<string>();
        if (commit is not { Length: 40 } || !commit.All(char.IsAsciiHexDigit)) return;
        commit = commit.ToLowerInvariant();
        var wanted = SdkListing(await GetCacheJsonAsync($"{api}/contents/{sdk.Folder}?ref={commit}", ct).ConfigureAwait(false), sdk, used);
        if (wanted.Count == 0) return;

        var release = TrimVersion(tag.TrimStart('v', 'V'))!;
        var incoming = Path.Combine(CacheRoot, "incoming-" + Guid.NewGuid().ToString("N"));
        var found = new List<PinnedFile>();
        var staged = new List<(PinnedFile File, string Staging)>();
        try
        {
            Directory.CreateDirectory(incoming);
            foreach (var (name, size, blob) in wanted)
            {
                var path = Path.Combine(incoming, name);
                AppLog.Info($"{sdk.Name} {release} from Exo's cache: fetching {name} ({size} bytes).");
                await DownloadToFileAsync($"https://{VerifiedGitHubReleaseDownloader.CacheHost}/v1/cache/{sdk.Repo}/{commit}/{sdk.Folder}/{name}", path, size, ct).ConfigureAwait(false);
                if (!blob.Equals(GitBlobSha1(path), StringComparison.OrdinalIgnoreCase) || !IsTrustedReplacementDll(path, name, revocationChecked: true) ||
                    TryFileVersion(path) is not { } fileVersion || FileSha256(path) is not { } sha)
                    throw new InvalidDataException($"{name} from {sdk.Name} {release} did not match the cache's listing or the vendor's signature.");
                if (ReleaseRejectionFor(sdk, release, fileVersion, name) is { } stale)
                    throw new InvalidDataException($"{name} from {sdk.Name} {release}: {stale}.");
                var file = new PinnedFile(name, sdk.ReleasePrefix + release, sdk.Name + " " + release, fileVersion, TrimVersion(fileVersion)!,
                    sdk.Repo, commit, sdk.Folder + "/" + name, size, sha.ToLowerInvariant());
                staged.Add((file, path));
            }
            // Every file has passed its checks before any is moved into the pinned cache.
            foreach (var (pinned, staging) in staged)
            {
                var dest = PinnedCachePath(pinned);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(staging, dest, overwrite: true);
                found.Add(pinned);
            }
        }
        finally
        {
            try { DeletionGate.DeleteDirectory(incoming, recursive: true); } catch { }
        }

        lock (CatalogGate)
        {
            var kept = found.Concat(_watched ?? LoadWatched()).ToList();
            var temp = WatchedPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(kept));
            File.Move(temp, WatchedPath, overwrite: true);
            _watched = kept;
            _allReleases = null;
        }
        AppLog.Info($"{sdk.Name} {release} found in Exo's cache: {string.Join(", ", found.Select(file => file.FileName + " " + file.Display))}.");
    }

    /// <summary>
    /// The files to fetch from a release folder's listing (GitHub's contents API): the SDK's DLLs a scanned game
    /// ships, each with its size and git blob hash.
    /// </summary>
    internal static List<(string Name, long Size, string Blob)> SdkListing(JsonNode? listing, VendorSdk sdk, IReadOnlySet<string> used)
    {
        var files = new List<(string, long, string)>();
        if (listing is not JsonArray items) return files;
        foreach (var node in items)
        {
            if (node is not JsonObject item || item["type"]?.GetValue<string>() != "file") continue;
            var name = item["name"]?.GetValue<string>()?.ToLowerInvariant();
            var size = item["size"]?.GetValue<long>() ?? 0;
            var blob = item["sha"]?.GetValue<string>();
            if (name is null || !sdk.Files.Contains(name) || !used.Contains(name)) continue;
            if (size <= 0 || size > 256L << 20 || blob is not { Length: 40 } || !blob.All(char.IsAsciiHexDigit)) continue;
            files.Add((name, size, blob));
        }
        return files;
    }

    private const int CacheJsonMaxChars = 8 << 20;

    private static async Task<JsonNode?> GetCacheJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } declared && declared > CacheJsonMaxChars) return null;
        var text = await ReadCappedTextAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), CacheJsonMaxChars, ct).ConfigureAwait(false);
        return text is null ? null : JsonNode.Parse(text);
    }

    /// <summary>Git's blob hash of a file: SHA-1 over "blob {size}\0" and its bytes, as a git listing states it.</summary>
    internal static string? GitBlobSha1(string path)
    {
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var fs = File.OpenRead(path);
            sha.AppendData(Encoding.ASCII.GetBytes($"blob {fs.Length}\0"));
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = fs.Read(buffer)) > 0) sha.AppendData(buffer, 0, read);
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch { return null; }
    }
}

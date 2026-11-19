using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The immutable trust contract for one executable published on GitHub by its
/// upstream project. Owner and Repository name that release; the bytes come from
/// Exo's own copy in its cache (<see cref="VerifiedGitHubReleaseDownloader.CacheHost"/>),
/// pinned here by size and SHA-256. Asset names are intentionally case-sensitive.
/// </summary>
internal sealed record GitHubReleaseAsset(
    string Owner,
    string Repository,
    string Tag,
    string AssetName,
    long ExpectedSize,
    string ExpectedSha256,
    IReadOnlyList<string>? OwnerAliases = null);

/// <summary>
/// Downloads an exact pinned release asset from Exo's cache, checks its size and
/// SHA-256 against the pins in code, and promotes the download only after all
/// integrity and executable validation succeeds. No request goes to GitHub: the
/// cache holds each GitHub path at the same path under <see cref="CacheHost"/>.
/// </summary>
internal sealed class VerifiedGitHubReleaseDownloader : IDisposable
{
    /// <summary>Exo's cache (Cloudflare R2 behind updates.getexo.app): GitHub's paths, served from Exo's copy.</summary>
    internal const string CacheHost = "updates.getexo.app";
    private const int BufferSize = 128 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DestinationGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (GitHubReleaseAsset Asset, Func<string, bool> Validate)> ExecutionContracts =
        new(StringComparer.OrdinalIgnoreCase);

    internal static IDisposable? AcquireExecutionLease(string path)
    {
        if (!ExecutionContracts.TryGetValue(Path.GetFullPath(path), out var contract)) return null;
        var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // The lease blocks writers for the whole run. Under it, an unchanged
            // stamp read from its own handle (length, last-write and change time,
            // file id) reuses the earlier verification instead of
            // hashing a 12-18 MB binary on every CLI call.
            if (!Adapters.PinnedToolCache.IsPinnedAsset(contract.Asset, path, contract.Validate, lease.SafeFileHandle))
                throw new SecurityException("The store tool no longer matches its pinned release. Reinstall the tool from Settings.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static VerifiedGitHubReleaseDownloader Shared { get; } = new(CreateProductionHandler());

    /// <summary>For a download too big for <see cref="Shared"/>'s five minutes on a slow line (frame generation's files).</summary>
    internal static VerifiedGitHubReleaseDownloader Large { get; } = new(CreateProductionHandler(), TimeSpan.FromHours(3));

    /// <summary>
    /// Frame generation's mods: the release asset from GitHub first (github.com, redirected only to GitHub's release asset
    /// hosts), Exo's cache when GitHub fails or its bytes fail the checks. Same size, SHA-256 and executable checks either way.
    /// </summary>
    internal static VerifiedGitHubReleaseDownloader Upstream { get; } = new(CreateProductionHandler(), TimeSpan.FromHours(3), upstreamFirst: true);

    private readonly HttpClient _http;
    private readonly bool _upstreamFirst;

    internal VerifiedGitHubReleaseDownloader(HttpMessageHandler handler, TimeSpan? timeout = null, bool upstreamFirst = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _upstreamFirst = upstreamFirst;
        _http = ExoHttp.Create(timeout ?? TimeSpan.FromMinutes(5), handler, disposeHandler: true);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExoLauncher", "1.0"));
    }

    public void Dispose() => _http.Dispose();

    internal async Task<string> DownloadPinnedAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        Func<string, bool> validateExecutable,
        CancellationToken ct,
        Action<long>? progress = null)
    {
        ValidateAssetContract(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(validateExecutable);
        using var awake = KeepAwake.Hold("download " + asset.AssetName);

        using var deadline = ExoHttpTransport.Deadline(_http, ct);
        ct = deadline.Token;

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new ArgumentException("Destination must have a parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);

        var gate = DestinationGates.GetOrAdd(destination, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A PE-shaped file is not a trusted cache entry. A second caller can
            // skip the network only when the existing managed file still matches
            // the production-pinned size and SHA-256 byte for byte. Hashed on the pool: an uncontended gate returns on the
            // caller's thread, and a model is hundreds of megabytes.
            if (await Task.Run(() => IsPinnedAssetFile(asset, destination, validateExecutable), ct).ConfigureAwait(false)) return destination;

            var sources = _upstreamFirst ? new[] { UpstreamDownloadUri(asset), PinnedDownloadUri(asset) } : new[] { PinnedDownloadUri(asset) };
            Exception? last = null;
            foreach (var downloadUri in sources)
            {
                var upstream = !downloadUri.Host.Equals(CacheHost, StringComparison.OrdinalIgnoreCase);
                var temporaryPath = CreateUniqueTemporaryPath(destination);
                try
                {
                    await DownloadAndVerifyAsync(asset, downloadUri, temporaryPath, ct, progress, upstream).ConfigureAwait(false);
                    if (!TryValidate(validateExecutable, temporaryPath))
                        throw new InvalidDataException("The verified release asset failed executable validation.");

                    // The unique temporary file is created beside the destination,
                    // so this same-volume move is the only promotion step observed by
                    // other callers. Existing files remain intact until now.
                    File.Move(temporaryPath, destination, overwrite: true);
                    ExecutionContracts[destination] = (asset, validateExecutable);
                    return destination;
                }
                catch (Exception ex) when (upstream && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
                {
                    last = ex;
                    AppLog.Info($"{asset.AssetName}: GitHub failed ({ex.Message}); using Exo's cache.");
                }
                finally
                {
                    TryDelete(temporaryPath);
                }
            }
            throw last ?? new InvalidDataException("The release asset could not be downloaded.");
        }
        finally
        {
            gate.Release();
        }
    }

    internal static byte[]? ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrEmpty(digest) ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return null;

        var hex = digest["sha256:".Length..];
        if (hex.Length != 64 || hex.Any(static c => !Uri.IsHexDigit(c))) return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static bool IsPinnedAssetFile(
        GitHubReleaseAsset asset,
        string path,
        Func<string, bool> validateExecutable)
    {
        try
        {
            ValidateAssetContract(asset);
            if (string.IsNullOrWhiteSpace(path) ||
                !File.Exists(path) ||
                new FileInfo(path).Length != asset.ExpectedSize ||
                !validateExecutable(path))
                return false;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);
            var actual = SHA256.HashData(stream);
            var verified = CryptographicOperations.FixedTimeEquals(actual, ExpectedDigest(asset));
            if (verified) ExecutionContracts[Path.GetFullPath(path)] = (asset, validateExecutable);
            return verified;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Only Exo's cache, at the release's own path: <c>/v1/cache/{owner}/{repo}/releases/download/{tag}/{asset}</c>.</summary>
    internal static bool IsExpectedAssetDownloadUri(
        Uri uri,
        GitHubReleaseAsset asset)
    {
        if (!HasPinnedHttpsOrigin(uri, CacheHost) ||
            !string.IsNullOrEmpty(uri.Query))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 8) return false;

        try
        {
            var decoded = segments.Select(Uri.UnescapeDataString).ToArray();
            if (decoded.Any(static segment =>
                    segment.Length == 0 || segment.Contains('/') || segment.Contains('\\')))
                return false;

            return decoded[0].Equals("v1", StringComparison.Ordinal)
                   && decoded[1].Equals("cache", StringComparison.Ordinal)
                   && IsExpectedOwner(decoded[2], asset)
                   && decoded[3].Equals(asset.Repository, StringComparison.OrdinalIgnoreCase)
                   && decoded[4].Equals("releases", StringComparison.Ordinal)
                   && decoded[5].Equals("download", StringComparison.Ordinal)
                   && decoded[6].Equals(asset.Tag, StringComparison.Ordinal)
                   && decoded[7].Equals(asset.AssetName, StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    internal static Uri PinnedDownloadUri(GitHubReleaseAsset asset)
    {
        ValidateAssetContract(asset);
        var uri = new Uri(
            $"https://{CacheHost}/v1/cache/{asset.Owner}/{asset.Repository}/releases/download/{asset.Tag}/{asset.AssetName}",
            UriKind.Absolute);
        if (!IsExpectedAssetDownloadUri(uri, asset))
            throw new SecurityException("Pinned download URL is not Exo's cached copy of the release asset.");
        return uri;
    }

    /// <summary>The release asset on GitHub itself: <c>https://github.com/{owner}/{repo}/releases/download/{tag}/{asset}</c>.</summary>
    internal static Uri UpstreamDownloadUri(GitHubReleaseAsset asset)
    {
        ValidateAssetContract(asset);
        return new Uri(
            $"https://{UpstreamSource.GitHub}/{asset.Owner}/{asset.Repository}/releases/download/{Uri.EscapeDataString(asset.Tag)}/{Uri.EscapeDataString(asset.AssetName)}",
            UriKind.Absolute);
    }

    internal static string CreateUniqueTemporaryPath(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new ArgumentException("Destination must have a parent directory.", nameof(destinationPath));
        var fileName = Path.GetFileName(destination);
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentException("Destination must include a file name.", nameof(destinationPath));
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.download");
    }

    private async Task DownloadAndVerifyAsync(
        GitHubReleaseAsset asset,
        Uri downloadUri,
        string temporaryPath,
        CancellationToken ct,
        Action<long>? progress,
        bool upstream = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        // GitHub redirects a release download to its asset hosts, each hop checked; the cache answers directly.
        using var response = upstream
            ? await UpstreamSource.GetAsync(_http, downloadUri.AbsoluteUri, UpstreamSource.IsUpstreamHop, ct).ConfigureAwait(false)
            : await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // A redirect left over could only send the bytes somewhere Exo did not pin.
        if (IsRedirect(response.StatusCode))
            throw new SecurityException("The cached release asset redirected; Exo accepts only its own copy.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The cached release asset request failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);

        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != asset.ExpectedSize)
            throw new InvalidDataException(
                "The cached release asset's Content-Length did not match its pinned size.");

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(
            temporaryPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (written > asset.ExpectedSize - read)
                throw new InvalidDataException("The cached release asset exceeded its pinned size.");
            hasher.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            progress?.Invoke(written);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(flushToDisk: true);

        if (written != asset.ExpectedSize)
            throw new InvalidDataException("The cached release asset did not match its pinned size.");
        var actualDigest = hasher.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, ExpectedDigest(asset)))
            throw new InvalidDataException("The cached release asset's SHA-256 did not match the pin.");
    }

    private static bool HasPinnedHttpsOrigin(Uri uri, string expectedHost) =>
        uri.IsAbsoluteUri
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and < 400;

    private static bool TryValidate(Func<string, bool> validateExecutable, string path)
    {
        try
        {
            return File.Exists(path) && validateExecutable(path);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) DeletionGate.DeleteFile(path);
        }
        catch
        {
            // Best effort. A GUID-scoped file cannot collide with another download.
        }
    }

    private static void ValidateAssetContract(GitHubReleaseAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!IsSafeRepositoryComponent(asset.Owner) ||
            !IsSafeRepositoryComponent(asset.Repository))
            throw new ArgumentException("GitHub owner and repository must be simple path components.", nameof(asset));
        if (asset.OwnerAliases is { Count: > 0 })
        {
            if (asset.OwnerAliases.Count > 8)
                throw new ArgumentException("GitHub owner aliases are limited to eight names.", nameof(asset));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { asset.Owner };
            foreach (var alias in asset.OwnerAliases)
            {
                if (!IsSafeRepositoryComponent(alias) || !seen.Add(alias))
                    throw new ArgumentException(
                        "GitHub owner aliases must be unique simple path components.",
                        nameof(asset));
            }
        }
        if (string.IsNullOrWhiteSpace(asset.Tag) ||
            asset.Tag.Contains('/') ||
            asset.Tag.Contains('\\'))
            throw new ArgumentException("GitHub release tag must be one exact path component.", nameof(asset));
        if (string.IsNullOrWhiteSpace(asset.AssetName) ||
            !string.Equals(Path.GetFileName(asset.AssetName), asset.AssetName, StringComparison.Ordinal) ||
            asset.AssetName.Contains('/') ||
            asset.AssetName.Contains('\\'))
            throw new ArgumentException("GitHub asset name must be one exact file name.", nameof(asset));
        if (asset.ExpectedSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(asset), "Expected asset size must be positive.");
        if (string.IsNullOrWhiteSpace(asset.ExpectedSha256) ||
            asset.ExpectedSha256.Length != 64 ||
            asset.ExpectedSha256.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Pinned SHA-256 must contain exactly 64 hexadecimal characters.", nameof(asset));
    }

    private static byte[] ExpectedDigest(GitHubReleaseAsset asset) =>
        Convert.FromHexString(asset.ExpectedSha256);

    private static bool IsExpectedOwner(string owner, GitHubReleaseAsset asset)
    {
        if (owner.Equals(asset.Owner, StringComparison.OrdinalIgnoreCase)) return true;
        if (asset.OwnerAliases is not { Count: > 0 }) return false;
        foreach (var alias in asset.OwnerAliases)
        {
            if (owner.Equals(alias, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsSafeRepositoryComponent(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static SocketsHttpHandler CreateProductionHandler() => new()
    {
        // Redirects are refused, not followed: the cache answers directly.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    };
}

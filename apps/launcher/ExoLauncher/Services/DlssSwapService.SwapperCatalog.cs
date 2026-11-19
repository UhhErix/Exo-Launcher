using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// DLSS Swapper's newer archives, from Exo's cache. The cache watch (cloud/updates/src/watch.js) lists each archive newer than the pins
/// in cache/exo/dlss-swapper/catalog.json, with its zip's URL and size, the DLL's size, and the zip's SHA-256 when the watch streamed it.
/// Exo takes one only when the DLL inside passes the checks a vendor release passes (VendorWatch): its vendor's signature (revocation
/// checked, failing closed), its organization, identity, exports, x64, a version matching the release, strictly newer than every version
/// Exo knows, and the catalog's size. Pinned archives keep their SHA-256 (ZipSource.Sha256, required). Ways this could fail: a catalog
/// entry for a file no scanned game ships is fetched; a file outside the watched list, or an archive off Exo's cache path, is downloaded;
/// the catalog is read past its cap; an archive whose zip is not a small zip is opened; a refused archive is downloaded again at once;
/// more than two archives are downloaded in one run; a DLL unsigned by its vendor, a version not the release's or not newer, or one of
/// another size, becomes a pin.
/// </summary>
public sealed partial class DlssSwapService
{
    /// <summary>How one archive's attempt ended: accepted; refused by a check (remembered for 24 hours); or failed for now (tried again next pass).</summary>
    internal enum SwapperOutcome { Accepted, Refused, Transient }

    /// <summary>A download that failed for now: no answer, a server error, a timeout, or the disk. A 4xx answer is the archive's own refusal.</summary>
    internal static bool IsTransientDownloadFailure(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException http => http.StatusCode is null || (int)http.StatusCode >= 500,
        TimeoutException or TaskCanceledException => true,
        System.IO.IOException => true,
        _ => false,
    };

    /// <summary>One catalog entry: the DLL's name and version, the DLL's size, and the zip's URL, size and SHA-256.</summary>
    internal sealed record SwapperEntry(string FileName, string Version, long DllSize, string ZipUrl, long ZipSize, string ZipSha256);

    private sealed record SwapperCatalogFile(int Schema, List<SwapperCatalogArchive>? Archives);
    private sealed record SwapperCatalogArchive(string? File, string? Version, long DllSize, SwapperCatalogZip? Zip);
    private sealed record SwapperCatalogZip(string? Url, long Size, string? Sha256);

    /// <summary>The DLSS and XeSS files the watch covers. FSR 3.1 is not here: its loader versions do not order like its releases.</summary>
    private static readonly string[] SwapperFiles =
        ["nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll", "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "libxell.dll"];
    private const long SwapperMaxBytes = 256L << 20;
    private const int SwapperCatalogMaxBytes = 4 << 20;
    /// <summary>The most archives this Launcher downloads in one pass of the hourly watch.</summary>
    private const int SwapperDownloadsPerRun = 2;
    /// <summary>An archive that was refused (or failed to download) is not tried again for this long.</summary>
    private static readonly TimeSpan SwapperRefusalMemory = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions SwapperJson = new() { PropertyNameCaseInsensitive = true };

    internal static string SwapperCatalogUrl => $"https://{VerifiedGitHubReleaseDownloader.CacheHost}/v1/cache/exo/dlss-swapper/catalog.json";

    private static string SwapperWatchedPath => Path.Combine(CacheRoot, "swapper-releases.json");
    private static string SwapperRefusedPath => Path.Combine(CacheRoot, "swapper-refused.json");
    private static IReadOnlyList<PinnedFile>? _swapper;

    /// <summary>
    /// The catalog's entries that are well formed: a watched file, an archive on Exo's cache path with no "..", sizes within the cap,
    /// and a SHA-256 only when it is 64 lower-case hex digits. Anything else is dropped; a catalog that does not parse lists nothing.
    /// </summary>
    internal static IReadOnlyList<SwapperEntry> ParseSwapperCatalog(string json)
    {
        SwapperCatalogFile? catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<SwapperCatalogFile>(json, SwapperJson);
        }
        catch (JsonException)
        {
            return [];
        }
        var entries = new List<SwapperEntry>();
        foreach (var item in catalog?.Archives ?? [])
        {
            if (item.File is null || item.Version is null || item.Zip?.Url is null) continue;
            if (!SwapperFiles.Contains(item.File, StringComparer.OrdinalIgnoreCase)) continue;
            if (TryParseVersion(item.Version) is null || !IsSwapperArchiveUrl(item.Zip.Url)) continue;
            if (item.DllSize <= 0 || item.DllSize > SwapperMaxBytes || item.Zip.Size <= 0 || item.Zip.Size > SwapperMaxBytes) continue;
            var sha = item.Zip.Sha256;
            if (sha is null || sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower)) continue;
            entries.Add(new SwapperEntry(item.File, item.Version, item.DllSize, item.Zip.Url, item.Zip.Size, sha));
        }
        return entries;
    }

    /// <summary>An archive on Exo's cache under DLSS Swapper's path: nothing else, and no parent segment.</summary>
    internal static bool IsSwapperArchiveUrl(string url) =>
        url.StartsWith(DlssSwapper, StringComparison.Ordinal) && url.Length > DlssSwapper.Length &&
        !url.Contains("..", StringComparison.Ordinal) &&
        url[DlssSwapper.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-');

    /// <summary>
    /// Why an entry is not downloaded, null when it may be: a watched file a scanned game ships, from Exo's archive path, whose version
    /// is strictly newer than every version of that file Exo knows (the pins and the vendor releases it has found).
    /// </summary>
    internal static string? SwapperRejection(SwapperEntry entry, IReadOnlySet<string> used, IEnumerable<string> knownVersions)
    {
        if (!SwapperFiles.Contains(entry.FileName, StringComparer.OrdinalIgnoreCase)) return "it is not a file the catalog covers";
        if (!used.Contains(entry.FileName)) return "no scanned game ships it";
        if (!IsSwapperArchiveUrl(entry.ZipUrl)) return "its archive is not on Exo's cache";
        if (TryParseVersion(entry.Version) is not { } version) return $"its version {entry.Version} is not readable";
        var newest = knownVersions.Select(TryParseVersion).Where(known => known is not null).Select(known => known!)
            .DefaultIfEmpty(new Version(0, 0)).Max();
        return version <= newest ? $"{entry.Version} is not newer than {newest}" : null;
    }

    /// <summary>
    /// The entries to try for one file, newest first: each passes SwapperRejection, and none was refused within the last 24 hours
    /// (<paramref name="refusedAt"/>, keyed by zip URL). The caller tries them in order and stops at the first one accepted.
    /// </summary>
    internal static IReadOnlyList<SwapperEntry> SwapperChoices(
        IEnumerable<SwapperEntry> entries,
        IReadOnlySet<string> used,
        IEnumerable<string> knownVersions,
        IReadOnlyDictionary<string, DateTime> refusedAt,
        DateTime now,
        string file)
    {
        var known = knownVersions.ToList();
        return entries
            .Where(entry => entry.FileName.Equals(file, StringComparison.OrdinalIgnoreCase) && SwapperRejection(entry, used, known) is null)
            .Where(entry => !(refusedAt.TryGetValue(entry.ZipUrl, out var at) && now - at < SwapperRefusalMemory))
            .OrderByDescending(entry => TryParseVersion(entry.Version))
            .ToList();
    }

    /// <summary>
    /// The archive's DLL as a pin, or the reason it is refused. The archive is first checked as a small zip (ZipShapeRefusal); its SHA-256 is
    /// checked when the catalog gives one. The DLL (the one entry at the top, read through a stream capped at the catalog's size) must pass the
    /// vendor signature (revocation checked, failing closed), organization, identity and version checks a vendor release passes. The file at
    /// <paramref name="incoming"/> is written here and left for the caller to delete. <paramref name="trusted"/> and <paramref name="versionOf"/>
    /// exist for tests; production reads the file's own signature and version resource.
    /// </summary>
    internal static async Task<(PinnedFile? Pin, string? Reason)> BuildSwapperPinAsync(
        string archive,
        SwapperEntry entry,
        string incoming,
        IEnumerable<string> knownVersions,
        Func<string, string, bool>? trusted = null,
        Func<string, string?>? versionOf = null,
        CancellationToken ct = default)
    {
        // The hash is checked first: the archive's bytes are verified before ZipFile reads any of its structure.
        if (!ArchiveMatches(archive, entry.ZipSha256)) return (null, "the archive is not the SHA-256 the catalog states");
        if (ZipShapeRefusal(archive) is { } shape) return (null, $"the archive is refused: {shape}");
        using (var zip = ZipFile.OpenRead(archive))
        {
            var inner = zip.Entries.SingleOrDefault(e => e.FullName.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase));
            if (inner is null) return (null, $"the archive has no {entry.FileName} at its top");
            if (inner.Length != entry.DllSize) return (null, $"{entry.FileName} in the archive is not the {entry.DllSize} bytes the catalog states");
            using var source = inner.Open();
            await using var target = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyCappedAsync(source, target, entry.DllSize, ct).ConfigureAwait(false);
        }
        var isTrusted = trusted ?? ((path, name) => IsTrustedReplacementDll(path, name, revocationChecked: true));
        if (!isTrusted(incoming, entry.FileName)) return (null, $"{entry.FileName} is not signed by its vendor, or is not the file it claims to be");
        var fileVersion = (versionOf ?? TryFileVersion)(incoming) ?? string.Empty;
        if (VendorReleaseRejection(TrimVersion(entry.Version)!, fileVersion, knownVersions) is { } stale) return (null, stale);
        if (FileSha256(incoming) is not { } sha) return (null, $"{entry.FileName} could not be read");
        var display = TrimVersion(fileVersion)!;
        var pin = new PinnedFile(entry.FileName, "dlss-" + TrimVersion(entry.Version), "DLSS " + display, fileVersion, display,
            DlssRepo, "", "", entry.DllSize, sha.ToLowerInvariant(), new ZipSource(entry.ZipUrl, entry.ZipSize, entry.ZipSha256));
        return (pin, null);
    }

    /// <summary>
    /// Downloads archives for each watched file a scanned game ships, at most <see cref="SwapperDownloadsPerRun"/> in a pass, newest first;
    /// each refusal is remembered for 24 hours, and the first accepted archive ends that file. Runs after the vendor watch, inside its gate.
    /// </summary>
    private static async Task WatchSwapperCatalogAsync(IReadOnlySet<string> used)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        var entries = await ReadCatalogEntriesAsync(ct).ConfigureAwait(false);
        if (entries is null) return;
        var refused = LoadRefusals();
        var now = DateTime.UtcNow;
        var downloads = 0;
        var changed = false;
        foreach (var file in SwapperFiles.Where(name => used.Contains(name)))
        {
            var known = AllReleases.Where(pin => pin.FileName.Equals(file, StringComparison.OrdinalIgnoreCase)).Select(pin => pin.Version).ToList();
            foreach (var pick in SwapperChoices(entries, used, known, refused, now, file))
            {
                if (downloads >= SwapperDownloadsPerRun) break;
                downloads++;
                var outcome = await AcceptSwapperAsync(pick, known, ct).ConfigureAwait(false);
                if (outcome == SwapperOutcome.Accepted) break;
                // A transient failure is tried again on the next pass; only a verification failure is remembered.
                if (outcome == SwapperOutcome.Transient) break;
                refused[pick.ZipUrl] = now;
                changed = true;
            }
            if (downloads >= SwapperDownloadsPerRun) break;
        }
        if (changed) SaveRefusals(refused);
    }

    /// <summary>The catalog's entries, read once with the cap (Content-Length first). Null when it is over the cap.</summary>
    internal static async Task<IReadOnlyList<SwapperEntry>?> ReadCatalogEntriesAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync(SwapperCatalogUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } declared && declared > SwapperCatalogMaxBytes)
        {
            AppLog.Info($"DLSS Swapper catalog not read: {declared} bytes is over the {SwapperCatalogMaxBytes} byte cap.");
            return null;
        }
        var text = await ReadCappedTextAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), SwapperCatalogMaxBytes, ct).ConfigureAwait(false);
        if (text is null)
        {
            AppLog.Info($"DLSS Swapper catalog not read: it is over the {SwapperCatalogMaxBytes} byte cap.");
            return null;
        }
        return ParseSwapperCatalog(text);
    }

    /// <summary>Downloads one archive and keeps its DLL if it passes. A verification failure is refused; a failure for now is transient.</summary>
    private static async Task<SwapperOutcome> AcceptSwapperAsync(SwapperEntry pick, IReadOnlyList<string> known, CancellationToken ct)
    {
        var stem = Path.Combine(CacheRoot, "incoming-" + Guid.NewGuid().ToString("N"));
        var archive = stem + ".zip";
        var dll = stem + ".dll";
        try
        {
            Directory.CreateDirectory(CacheRoot);
            await DownloadToFileAsync(pick.ZipUrl, archive, pick.ZipSize, ct).ConfigureAwait(false);
            var (pin, reason) = await BuildSwapperPinAsync(archive, pick, dll, known, ct: ct).ConfigureAwait(false);
            if (pin is null)
            {
                AppLog.Info($"DLSS Swapper {pick.FileName} {pick.Version} not taken from Exo's cache: {reason}.");
                return SwapperOutcome.Refused;
            }
            var dest = PinnedCachePath(pin);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(dll, dest, overwrite: true);
            lock (CatalogGate)
            {
                var kept = SwapperWatched().Where(found => !(found.FileName == pin.FileName && found.Release == pin.Release)).Append(pin).ToList();
                var temp = SwapperWatchedPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(kept));
                File.Move(temp, SwapperWatchedPath, overwrite: true);
                _swapper = kept;
                _allReleases = null;
            }
            AppLog.Info($"DLSS Swapper {pin.FileName} {pin.Display} found in Exo's cache.");
            return SwapperOutcome.Accepted;
        }
        catch (Exception ex)
        {
            var transient = IsTransientDownloadFailure(ex);
            var suffix = transient ? " for now (it is tried again next pass)" : string.Empty;
            AppLog.Info($"DLSS Swapper {pick.FileName} {pick.Version} not taken from Exo's cache{suffix}: {ex.Message}");
            return transient ? SwapperOutcome.Transient : SwapperOutcome.Refused;
        }
        finally
        {
            TryDelete(archive);
            TryDelete(dll);
        }
    }

    /// <summary>The refusals kept on this PC: zip URL to when it was refused. A missing or unreadable file is an empty record.</summary>
    private static Dictionary<string, DateTime> LoadRefusals()
    {
        try
        {
            if (!File.Exists(SwapperRefusedPath)) return new Dictionary<string, DateTime>();
            return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(SwapperRefusedPath)) ?? new Dictionary<string, DateTime>();
        }
        catch (Exception ex)
        {
            AppLog.Debug("DLSS Swapper refusals unreadable: " + ex.Message);
            return new Dictionary<string, DateTime>();
        }
    }

    private static void SaveRefusals(Dictionary<string, DateTime> refused)
    {
        try
        {
            // Only the last day's refusals are kept.
            var now = DateTime.UtcNow;
            var kept = refused.Where(pair => now - pair.Value < SwapperRefusalMemory).ToDictionary(pair => pair.Key, pair => pair.Value);
            var temp = SwapperRefusedPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(kept));
            File.Move(temp, SwapperRefusedPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug("DLSS Swapper refusals not saved: " + ex.Message);
        }
    }

    /// <summary>
    /// What makes an archive not a small, single-disk zip, checked before ZipFile opens it, from its end-of-central-directory record and a
    /// walk of its central directory (at most 1,000 headers). Refused: zip64 markers; a multi-disk archive; entry counts that differ between
    /// the record's fields; more than 1,000 entries; a central directory that does not end exactly where the record starts (the comment
    /// follows the record, so its length must make the record end the file); a header that is not where the record says; a header count
    /// other than the record's total. Null when the archive is a small zip with a consistent shape.
    /// </summary>
    internal static string? ZipShapeRefusal(string path)
    {
        using var stream = File.OpenRead(path);
        var length = stream.Length;
        if (length < 22) return "it is not a zip";
        var tailLength = (int)Math.Min(length, 22 + 65535);
        var tail = new byte[tailLength];
        stream.Seek(length - tailLength, SeekOrigin.Begin);
        stream.ReadExactly(tail);
        for (var i = tailLength - 22; i >= 0; i--)
        {
            if (tail[i] != 0x50 || tail[i + 1] != 0x4b || tail[i + 2] != 0x05 || tail[i + 3] != 0x06) continue;
            var disk = BitConverter.ToUInt16(tail, i + 4);
            var directoryDisk = BitConverter.ToUInt16(tail, i + 6);
            var onDisk = BitConverter.ToUInt16(tail, i + 8);
            var total = BitConverter.ToUInt16(tail, i + 10);
            var directorySize = BitConverter.ToUInt32(tail, i + 12);
            var directoryOffset = BitConverter.ToUInt32(tail, i + 16);
            var commentLength = BitConverter.ToUInt16(tail, i + 20);
            if (disk == 0xFFFF || directoryDisk == 0xFFFF || onDisk == 0xFFFF || total == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF)
                return "it is zip64";
            if (i >= 20 && tail[i - 20] == 0x50 && tail[i - 19] == 0x4b && tail[i - 18] == 0x06 && tail[i - 17] == 0x07) return "it is zip64";
            if (disk != 0 || directoryDisk != 0) return "it spans disks";
            if (onDisk != total) return "its entry counts differ";
            if (total > 1000) return $"it has {total} entries, more than 1000";
            var recordAt = length - tailLength + i;
            if (recordAt + 22 + commentLength != length) return "its comment length does not end the file";
            if ((long)directoryOffset + directorySize != recordAt) return "its central directory does not end where its record starts";
            if (directorySize > 1024L * 1024) return "its central directory is too large";
            return WalkCentralDirectory(stream, directoryOffset, directorySize, total);
        }
        return "it has no end of central directory";
    }

    /// <summary>Walks the central directory's headers (at most 1,000) and checks each signature, and that the count is the record's total.</summary>
    private static string? WalkCentralDirectory(FileStream stream, uint offset, uint size, int total)
    {
        var directory = new byte[size];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(directory);
        var at = 0;
        var headers = 0;
        while (at < directory.Length)
        {
            if (headers >= 1000) return "it has more than 1000 central directory headers";
            if (at + 46 > directory.Length) return "its central directory is cut short";
            if (directory[at] != 0x50 || directory[at + 1] != 0x4b || directory[at + 2] != 0x01 || directory[at + 3] != 0x02)
                return "a central directory header is not where its record says";
            var name = BitConverter.ToUInt16(directory, at + 28);
            var extra = BitConverter.ToUInt16(directory, at + 30);
            var comment = BitConverter.ToUInt16(directory, at + 32);
            at += 46 + name + extra + comment;
            headers++;
        }
        if (at != directory.Length) return "its central directory is cut short";
        if (headers != total) return $"it has {headers} central directory headers, its record says {total}";
        return null;
    }

    /// <summary>
    /// An archive for a release Exo pins (same DLL and release as a compiled-in pin) must carry its SHA-256. Only a catalog archive for a newer release
    /// may have none: the DLL inside it is then the check.
    /// </summary>
    internal static string? ZipHashRefusal(PinnedFile file, ZipSource zip) =>
        zip.Sha256 is null && PinnedFiles.Any(pin => pin.FileName == file.FileName && pin.Release == file.Release)
            ? $"{file.FileName} from {file.Sdk} is pinned without its archive's SHA-256" : null;

    /// <summary>The stream's text, or null when it is longer than <paramref name="maxBytes"/> (read at most that far, then stopped).</summary>
    internal static async Task<string?> ReadCappedTextAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The archives Exo has accepted from the catalog: each is a pin like the compiled-in ones, reloaded on start.</summary>
    private static IReadOnlyList<PinnedFile> SwapperWatched()
    {
        lock (CatalogGate)
            return _swapper ??= LoadSwapperWatched();
    }

    private static IReadOnlyList<PinnedFile> LoadSwapperWatched()
    {
        try
        {
            if (!File.Exists(SwapperWatchedPath)) return [];
            var files = JsonSerializer.Deserialize<List<PinnedFile>>(File.ReadAllText(SwapperWatchedPath)) ?? [];
            return files.Where(file => file.Zip is not null && file.Sha256.Length == 64 && IsSwapperArchiveUrl(file.Zip.Url) &&
                SwapperFiles.Contains(file.FileName, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Debug("DLSS Swapper releases from the cache unreadable: " + ex.Message);
            return [];
        }
    }
}

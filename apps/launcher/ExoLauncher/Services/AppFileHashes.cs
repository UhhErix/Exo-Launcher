using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// The SHA-256 of each file in the installed app folder, for comparing it with a release's files.json. Hashing ~500 MB takes
/// a few seconds, so each hash is kept in app-file-hashes.json in the updates folder under the file's path, size and last
/// write time, and only files whose size or time moved are read again. Ways it could fail: a cached hash survives a change to
/// the file (same path, other size or time); a cache for another folder is used; a broken or huge cache file stops the
/// update instead of being ignored; a file that can't be read counts as unchanged.
/// </summary>
internal static class AppFileHashes
{
    internal const string CacheName = "app-file-hashes.json";
    private const int MaxCacheBytes = 16 * 1024 * 1024;

    private sealed record Cached(long Size, long Ticks, string Sha256);

    /// <summary>
    /// The hash of each file of <paramref name="appFolder"/> that <paramref name="wanted"/> asks for (by relative path and
    /// size), keyed by relative path in any capitals. A file that can't be read is left out (so it is fetched).
    /// </summary>
    internal static async Task<Dictionary<string, (long Size, string Sha256)>> HashAsync(string appFolder, string cachePath,
        Func<string, long, bool> wanted, CancellationToken ct, int parallel = 2)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appFolder));
        var cache = Read(cachePath, root);
        var result = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        var fresh = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        var work = new List<(string Relative, FileInfo Info)>();
        foreach (var source in AppFiles.Folder(root))
        {
            FileInfo info;
            try { info = new FileInfo(source.FullPath); _ = info.Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (!wanted(source.Path, info.Length)) continue;
            if (cache.TryGetValue(source.Path, out var known) && known.Size == info.Length && known.Ticks == info.LastWriteTimeUtc.Ticks)
            {
                result[source.Path] = (known.Size, known.Sha256);
                fresh[source.Path] = known;
            }
            else work.Add((source.Path, info));
        }

        var gate = new Lock();
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (item, token) =>
        {
            try
            {
                var sha = await AppFiles.Sha256FileAsync(item.Info.FullName, token).ConfigureAwait(false);
                item.Info.Refresh();
                lock (gate)
                {
                    result[item.Relative] = (item.Info.Length, sha);
                    fresh[item.Relative] = new Cached(item.Info.Length, item.Info.LastWriteTimeUtc.Ticks, sha);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not readable: fetched */ }
        }).ConfigureAwait(false);

        if (work.Count > 0) Write(cachePath, root, fresh);
        return result;
    }

    private static Dictionary<string, Cached> Read(string cachePath, string root)
    {
        var cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length > MaxCacheBytes) return cache;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(cachePath));
            var top = doc.RootElement;
            if (top.GetProperty("schema").GetInt32() != 1 ||
                !string.Equals(top.GetProperty("root").GetString(), root, StringComparison.OrdinalIgnoreCase)) return cache;
            foreach (var file in top.GetProperty("files").EnumerateObject())
            {
                var value = file.Value;
                if (AppFiles.NormalizeSha(value.GetProperty("sha256").GetString()) is { } sha)
                    cache[file.Name] = new Cached(value.GetProperty("size").GetInt64(), value.GetProperty("ticks").GetInt64(), sha);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            cache.Clear();
        }
        return cache;
    }

    private static void Write(string cachePath, string root, Dictionary<string, Cached> files)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                schema = 1,
                root,
                files = files.OrderBy(f => f.Key, StringComparer.Ordinal)
                    .ToDictionary(f => f.Key, f => new { size = f.Value.Size, ticks = f.Value.Ticks, sha256 = f.Value.Sha256 }),
            }));
            File.Move(temporary, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Helpers.AppLog.Debug("Update: the file hash cache was not saved: " + ex.GetType().Name); }
    }
}

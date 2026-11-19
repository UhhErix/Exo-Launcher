using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace ExoLauncher.Services;

/// <summary>
/// A release's app folder as two files beside its installer, so an update fetches only what changed (AppUpdateService.Delta):
/// <c>files.json</c> lists every file of the installed app folder (path, size, SHA-256, and where its bytes are in the pack) and
/// <c>files.pack</c> is every file compressed with zstd on its own, one after another in the list's order. Built by
/// tools/AppFiles (Build-Release.ps1) and read here; this file needs nothing else from Exo, so the build tool compiles it too.
///
/// Ways it could fail, each checked here or in AppFilesTests: the same folder builds a different list or pack (file order, a
/// compression level, the clock); a list names a path outside the app folder (<c>..</c>, a drive, a backslash, a device name),
/// the same file twice in other capitals, offsets that overlap, leave a gap or run past the pack; a file is taken whose
/// bytes don't decompress to exactly its size and SHA-256, or whose compressed bytes carry anything after the frame; adjacent
/// files are asked for one request each, or ranges merge past the gap or span they may.
/// </summary>
internal static class AppFiles
{
    internal const string ManifestName = "files.json";
    internal const string PackName = "files.pack";
    internal const int Schema = 1;
    /// <summary>The list for ~2,000 files is ~400 KB; anything past this is not Exo's.</summary>
    internal const int MaxManifestBytes = 8 * 1024 * 1024;
    internal const int MaxFiles = 50_000;
    internal const long MaxFileBytes = 4L * 1024 * 1024 * 1024;
    /// <summary>zstd's level for the pack: fixed, so the same folder always packs to the same bytes.</summary>
    internal const int Level = 19;
    /// <summary>The installer's own copy of the list (tools/installer/New-PayloadManifest.ps1); a release without it has no delta.</summary>
    internal const string PayloadManifestName = "payload-manifest.json";

    /// <param name="Path">Relative to the app folder, with forward slashes.</param>
    /// <param name="Setup">Exo Setup's own file in the app folder (ExoSetup.exe): not in the installer's payload list, placed after the update's health check.</param>
    internal sealed record Entry(string Path, long Size, string Sha256, long Offset, long Length, bool Setup = false)
    {
        internal long End => Offset + Length;
    }

    internal sealed record Manifest(string Version, string Installer, long PackSize, string PackSha256, IReadOnlyList<Entry> Files);

    /// <param name="Path">The path the file has in the app folder (forward slashes).</param>
    internal sealed record Source(string Path, string FullPath, bool Setup = false);

    /// <summary>A run of the pack fetched with one request: [Start, End), holding these files in order.</summary>
    internal sealed record Span(long Start, long End, IReadOnlyList<Entry> Entries)
    {
        internal long Length => End - Start;
    }

    /// <summary>Every file under <paramref name="root"/> as its relative path, in ordinal order; a link anywhere stops it (as Verify-Payload.ps1 does).</summary>
    internal static List<Source> Folder(string root)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        var found = new List<Source>();
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("App folder links are not allowed.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("App folder links are not allowed.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else found.Add(new Source(entry[(full.Length + 1)..].Replace('\\', '/'), entry));
            }
        }
        found.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return found;
    }

    /// <summary>
    /// Writes files.json and files.pack for these files into <paramref name="outDirectory"/>: sorted by path, each compressed
    /// on its own at <paramref name="level"/> (several at once; the pack is written in order, so it is the same bytes every time).
    /// </summary>
    internal static Manifest Build(IReadOnlyList<Source> sources, string version, string installer, string outDirectory, int level = Level)
    {
        if (!IsVersion(version)) throw new ArgumentException("A version is X.Y.Z.", nameof(version));
        if (NormalizeSha(installer) is not { } revision) throw new ArgumentException("The installer revision is a SHA-256.", nameof(installer));
        var ordered = sources.OrderBy(s => s.Path, StringComparer.Ordinal).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in ordered)
            if (!IsSafePath(source.Path) || !seen.Add(source.Path)) throw new InvalidDataException("Bad or repeated app path: " + source.Path);
        if (ordered.Length is 0 or > MaxFiles) throw new InvalidDataException("An app folder has 1 to " + MaxFiles + " files.");

        var compressed = new byte[ordered.Length][];
        var raw = new (long Size, string Sha)[ordered.Length];
        Parallel.For(0, ordered.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var bytes = File.ReadAllBytes(ordered[i].FullPath);
            raw[i] = (bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            using var compressor = new Compressor(level);
            compressed[i] = compressor.Wrap(bytes).ToArray();
        });

        Directory.CreateDirectory(outDirectory);
        var entries = new List<Entry>(ordered.Length);
        var packPath = System.IO.Path.Combine(outDirectory, PackName);
        using var packHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        using (var pack = new FileStream(packPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            for (var i = 0; i < ordered.Length; i++)
            {
                pack.Write(compressed[i]);
                packHash.AppendData(compressed[i]);
                entries.Add(new Entry(ordered[i].Path, raw[i].Size, raw[i].Sha, offset, compressed[i].LongLength, ordered[i].Setup));
                offset += compressed[i].LongLength;
            }
        }
        var manifest = new Manifest(version, revision, offset, Convert.ToHexStringLower(packHash.GetHashAndReset()), entries);
        File.WriteAllBytes(System.IO.Path.Combine(outDirectory, ManifestName), Write(manifest));
        return manifest;
    }

    /// <summary>The list as UTF-8 JSON, field by field in a fixed order.</summary>
    internal static byte[] Write(Manifest manifest)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            json.WriteStartObject();
            json.WriteNumber("schema", Schema);
            json.WriteString("app", "launcher");
            json.WriteString("version", manifest.Version);
            json.WriteString("installer", manifest.Installer);
            json.WriteStartObject("pack");
            json.WriteString("name", PackName);
            json.WriteNumber("size", manifest.PackSize);
            json.WriteString("sha256", manifest.PackSha256);
            json.WriteString("compression", "zstd");
            json.WriteEndObject();
            json.WriteStartArray("files");
            foreach (var entry in manifest.Files)
            {
                json.WriteStartObject();
                json.WriteString("path", entry.Path);
                json.WriteNumber("size", entry.Size);
                json.WriteString("sha256", entry.Sha256);
                json.WriteNumber("offset", entry.Offset);
                json.WriteNumber("length", entry.Length);
                if (entry.Setup) json.WriteBoolean("setup", true);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// A files.json, checked whole: schema 1 for the launcher, a plain X.Y.Z, the installer revision, the pack's size and
    /// SHA-256, and 1 to <see cref="MaxFiles"/> files in strict path order, each a safe relative path met once (in any
    /// capitals), whose compressed bytes follow the previous file's with no gap and end exactly at the pack's end. The
    /// installer's payload list and ExoLauncher.exe must be among them. Null for anything else.
    /// </summary>
    internal static Manifest? Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > MaxManifestBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Int(root, "schema") != Schema || Str(root, "app") != "launcher") return null;
            if (Str(root, "version") is not { } version || !IsVersion(version)) return null;
            if (NormalizeSha(Str(root, "installer")) is not { } installer) return null;
            if (!root.TryGetProperty("pack", out var pack) || pack.ValueKind != JsonValueKind.Object ||
                Str(pack, "name") is not (null or PackName) || Str(pack, "compression") is not (null or "zstd") ||
                Int(pack, "size") is not { } packSize || packSize <= 0 || NormalizeSha(Str(pack, "sha256")) is not { } packSha)
                return null;
            if (!root.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var count = list.GetArrayLength();
            if (count is 0 or > MaxFiles) return null;
            var entries = new List<Entry>(count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long next = 0;
            string? previous = null;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Str(item, "path") is not { } path || !IsSafePath(path) || !seen.Add(path) ||
                    (previous is not null && string.CompareOrdinal(previous, path) >= 0) ||
                    Int(item, "size") is not { } size || size is < 0 or > MaxFileBytes ||
                    NormalizeSha(Str(item, "sha256")) is not { } sha ||
                    Int(item, "offset") is not { } offset || offset != next ||
                    Int(item, "length") is not { } length || length <= 0 || length > packSize - offset)
                    return null;
                var setup = item.TryGetProperty("setup", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (item.TryGetProperty("setup", out flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                entries.Add(new Entry(path, size, sha, offset, length, setup));
                next = offset + length;
                previous = path;
            }
            if (next != packSize) return null;
            if (!entries.Any(e => !e.Setup && e.Path == PayloadManifestName) || !entries.Any(e => !e.Setup && e.Path == "ExoLauncher.exe")) return null;
            return new Manifest(version, installer, packSize, packSha, entries);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static long? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    }

    /// <summary>Which files the installed folder already has (same path in any capitals, size and SHA-256) and which must be fetched.</summary>
    internal static (List<Entry> Reuse, List<Entry> Fetch) Diff(Manifest manifest, IReadOnlyDictionary<string, (long Size, string Sha256)> installed)
    {
        var reuse = new List<Entry>();
        var fetch = new List<Entry>();
        foreach (var entry in manifest.Files)
        {
            if (installed.TryGetValue(entry.Path, out var have) && have.Size == entry.Size &&
                string.Equals(have.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                reuse.Add(entry);
            else
                fetch.Add(entry);
        }
        return (reuse, fetch);
    }

    /// <summary>
    /// The requests for these files: sorted by offset, one span while the next file starts within <paramref name="maxGap"/>
    /// bytes of the span's end (the gap is downloaded and skipped) and the span stays within <paramref name="maxSpan"/>. A
    /// file larger than <paramref name="maxSpan"/> is a span of its own.
    /// </summary>
    internal static List<Span> Ranges(IEnumerable<Entry> files, long maxGap = 64 * 1024, long maxSpan = 32L * 1024 * 1024)
    {
        var spans = new List<Span>();
        List<Entry>? run = null;
        long start = 0, end = 0;
        foreach (var entry in files.OrderBy(e => e.Offset))
        {
            if (run is not null && entry.Offset >= end && entry.Offset - end <= maxGap && entry.End - start <= maxSpan)
            {
                run.Add(entry);
                end = entry.End;
                continue;
            }
            if (run is not null) spans.Add(new Span(start, end, run));
            run = [entry];
            start = entry.Offset;
            end = entry.End;
        }
        if (run is not null) spans.Add(new Span(start, end, run));
        return spans;
    }

    /// <summary>
    /// Reads exactly <paramref name="entry"/>.Length compressed bytes from <paramref name="compressed"/>, decompresses them to
    /// <paramref name="destination"/> and checks the result is exactly the file's size and SHA-256 and that the bytes were one
    /// whole zstd frame; InvalidDataException otherwise (the caller drops the whole staging folder).
    /// </summary>
    internal static async Task WriteVerifiedAsync(Stream compressed, Entry entry, string destination, CancellationToken ct)
    {
        var bounded = new ExactStream(compressed, entry.Length);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            await using (var zstd = new DecompressionStream(bounded, leaveOpen: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await zstd.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > entry.Size) throw new InvalidDataException($"{entry.Path} decompressed past its size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            if (bounded.Remaining != 0) throw new InvalidDataException($"{entry.Path} has bytes after its compressed frame.");
            if (written != entry.Size || !FixedTimeHexEquals(Convert.ToHexStringLower(hash.GetHashAndReset()), entry.Sha256))
                throw new InvalidDataException($"{entry.Path} did not match its SHA-256.");
        }
        catch (ZstdException ex) { throw new InvalidDataException($"{entry.Path} is not a zstd frame: {ex.Message}", ex); }
    }

    internal static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 17, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    internal static bool FixedTimeHexEquals(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a.ToLowerInvariant()), Encoding.ASCII.GetBytes(b.ToLowerInvariant()));
    }

    internal static string? NormalizeSha(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var s = digest.Trim().ToLowerInvariant();
        return s.Length == 64 && s.All(Uri.IsHexDigit) ? s : null;
    }

    internal static bool IsVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 3 && parts.All(p => p.Length is > 0 and <= 9 && p.All(char.IsAsciiDigit));
    }

    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>A relative path inside the app folder: forward slashes, no empty, "." or ".." part, no drive, stream or device name.</summary>
    internal static bool IsSafePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 400) return false;
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith('.') || part.EndsWith(' ') || part.StartsWith(' ')) return false;
            if (part.Any(c => c < 0x20 || "\\:*?\"<>|".Contains(c))) return false;
            var stem = part.Split('.')[0];
            if (DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Exactly <c>length</c> bytes of another stream (left open): an early end throws, nothing past it is read.</summary>
    internal sealed class ExactStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        internal long Remaining { get; private set; } = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - Remaining; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (Remaining == 0 || buffer.Length == 0) return 0;
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
            if (read == 0) throw new EndOfStreamException("The pack ended early.");
            Remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Remaining == 0 || buffer.Length == 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, Remaining)], ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The pack ended early.");
            Remaining -= read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

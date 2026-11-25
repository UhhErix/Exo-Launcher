using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using ZstdSharp;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the file-level update's format could fail (AppFiles, AppFileHashes):
/// - the same folder builds a different files.json or files.pack (the order files were found in, the clock, parallel compression);
/// - a list is accepted that names a path outside the app folder (.., a drive, a backslash, a device name, a stream), the same
///   file twice in other capitals, files out of order, offsets with a gap or overlap or past the pack, a bad SHA-256, or
///   no payload-manifest.json;
/// - an unchanged file is fetched, a changed one (same size, other bytes) or a new one is not, or another capital counts as new;
/// - adjacent files cost a request each, ranges merge across a gap that is too wide or grow past their span limit;
/// - a file is written whose bytes don't decompress to exactly its size and SHA-256, a truncated frame passes, or bytes after
///   the frame go unnoticed;
/// - a cached hash survives a change to its file, a cache for another folder or a broken cache is used.
/// </summary>
public sealed class AppFilesTests : IDisposable
{
    private const string Revision = "1111111111111111111111111111111111111111111111111111111111111111";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-appfiles-" + Guid.NewGuid().ToString("N"));

    public AppFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Folder(string name, params (string Path, string Text)[] files)
    {
        var folder = Path.Combine(_root, name);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(folder, path.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return folder;
    }

    private static (string, string)[] App(string exe = "exe v1", params (string, string)[] more) =>
        [("ExoLauncher.exe", exe), ("payload-manifest.json", "{}"), .. more];

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void TheSameFolderAlwaysBuildsTheSameListAndPack()
    {
        var app = Folder("app", App(more: [("wwwroot/index.html", new string('x', 50_000)), ("b.dll", "bee"), ("A/z.txt", "zed")]));
        var first = AppFiles.Build(AppFiles.Folder(app), "3.27.0", Revision, Path.Combine(_root, "one"));
        // Found in another order, and compressed on other threads: still the same bytes.
        var reversed = AppFiles.Folder(app);
        reversed.Reverse();
        AppFiles.Build(reversed, "3.27.0", Revision, Path.Combine(_root, "two"));

        foreach (var name in new[] { AppFiles.ManifestName, AppFiles.PackName })
            Assert.Equal(File.ReadAllBytes(Path.Combine(_root, "one", name)), File.ReadAllBytes(Path.Combine(_root, "two", name)));
        Assert.Equal(["A/z.txt", "ExoLauncher.exe", "b.dll", "payload-manifest.json", "wwwroot/index.html"], first.Files.Select(f => f.Path));
        Assert.Equal(new FileInfo(Path.Combine(_root, "one", AppFiles.PackName)).Length, first.PackSize);

        var parsed = AppFiles.Parse(File.ReadAllBytes(Path.Combine(_root, "one", AppFiles.ManifestName)));
        Assert.NotNull(parsed);
        Assert.Equal(first.Files, parsed.Files);
        Assert.Equal(first.PackSha256, parsed.PackSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "one", AppFiles.PackName)))), parsed.PackSha256);
    }

    [Fact]
    public void SetupFilesAreMarkedAndRoundTrip()
    {
        var app = Folder("app", App());
        var shell = Folder("shell", ("ExoSetup.exe", "setup"));
        var sources = AppFiles.Folder(app);
        sources.Add(new AppFiles.Source("ExoSetup.exe", Path.Combine(shell, "ExoSetup.exe"), Setup: true));
        AppFiles.Build(sources, "3.27.0", Revision, Path.Combine(_root, "out"));
        var parsed = AppFiles.Parse(File.ReadAllBytes(Path.Combine(_root, "out", AppFiles.ManifestName)))!;
        Assert.True(parsed.Files.Single(f => f.Path == "ExoSetup.exe").Setup);
        Assert.False(parsed.Files.Single(f => f.Path == "ExoLauncher.exe").Setup);
    }

    [Fact]
    public void BuildRefusesTheSamePathInOtherCapitals()
    {
        var a = Folder("a", ("x.dll", "1"));
        Assert.Throws<InvalidDataException>(() => AppFiles.Build(
            [new AppFiles.Source("x.dll", Path.Combine(a, "x.dll")), new AppFiles.Source("X.DLL", Path.Combine(a, "x.dll"))],
            "3.27.0", Revision, Path.Combine(_root, "out")));
    }

    private static string List(string files, long packSize = 30, string extra = "") =>
        $$"""{"schema":1,"app":"launcher","version":"3.27.0","installer":"{{Revision}}","pack":{"name":"files.pack","size":{{packSize}},"sha256":"{{Revision}}","compression":"zstd"}{{extra}},"files":[{{files}}]}""";

    private static string File_(string path, long offset, long length, string sha = Revision, long size = 5) =>
        $$"""{"path":"{{path}}","size":{{size}},"sha256":"{{sha}}","offset":{{offset}},"length":{{length}}}""";

    private static string Good(string middle = "") =>
        File_("ExoLauncher.exe", 0, 10) + "," + (middle.Length > 0 ? middle + "," : "") + File_("payload-manifest.json", 10, 20);

    [Fact]
    public void ParsesAWellFormedList() => Assert.NotNull(AppFiles.Parse(Encoding.UTF8.GetBytes(List(Good()))));

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("C:/evil.dll")]
    [InlineData("a\\\\b.dll")]
    [InlineData("/rooted.dll")]
    [InlineData("a//b.dll")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("a/file.dll:stream")]
    [InlineData("trailing. ")]
    [InlineData("dot.")]
    public void RefusesAPathOutsideTheAppFolder(string path)
    {
        Assert.False(AppFiles.IsSafePath(path));
        // Sorted last, so only the path itself is wrong with this list.
        var list = List(File_("ExoLauncher.exe", 0, 10) + "," + File_("payload-manifest.json", 10, 10) + "," + File_("zz/" + path, 20, 10));
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(list.Replace("\\", "\\\\"))));
        Assert.NotNull(AppFiles.Parse(Encoding.UTF8.GetBytes(List(File_("ExoLauncher.exe", 0, 10) + "," + File_("payload-manifest.json", 10, 10) + "," + File_("zz/fine.dll", 20, 10)))));
    }

    [Fact]
    public void RefusesBadLayouts()
    {
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(File_("ExoLauncher.exe", 0, 10) + "," + File_("payload-manifest.json", 12, 18))))); // a gap
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(File_("ExoLauncher.exe", 0, 10) + "," + File_("payload-manifest.json", 8, 22))))); // overlap
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(Good(), packSize: 31)))); // the pack is longer than its files
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(File_("ExoLauncher.exe", 0, 10) + "," + File_("payload-manifest.json", 10, 30))))); // past the pack
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(File_("payload-manifest.json", 0, 10) + "," + File_("ExoLauncher.exe", 10, 20))))); // out of order
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(File_("ExoLauncher.exe", 0, 10) + "," + File_("exolauncher.EXE", 10, 10) + "," + File_("payload-manifest.json", 20, 10))))); // twice
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(File_("ExoLauncher.exe", 0, 10, sha: "abc") + "," + File_("payload-manifest.json", 10, 20)))));
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(File_("ExoLauncher.exe", 0, 10, size: -1) + "," + File_("payload-manifest.json", 10, 20)))));
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(File_("ExoLauncher.exe", 0, 30), packSize: 30)))); // no payload list
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(Good()).Replace("\"launcher\"", "\"cinema\""))));
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(Good()).Replace("3.27.0", "3.27"))));
        Assert.Null(AppFiles.Parse(Encoding.UTF8.GetBytes(List(Good()).Replace("\"zstd\"", "\"gzip\""))));
        Assert.Null(AppFiles.Parse("not json"u8));
        Assert.Null(AppFiles.Parse(new byte[AppFiles.MaxManifestBytes + 1]));
    }

    private static AppFiles.Entry E(string path, long offset, long length, long size = 5, string sha = Revision) => new(path, size, sha, offset, length);

    [Fact]
    public void DiffKeepsWhatIsInstalledAndFetchesWhatChanged()
    {
        var manifest = new AppFiles.Manifest("3.27.0", Revision, 40, Revision,
        [
            E("Same.dll", 0, 10, 3, Sha("abc")),
            E("changed.dll", 10, 10, 3, Sha("new")),
            E("new/file.txt", 20, 10, 3, Sha("abc")),
            E("other-size.dll", 30, 10, 4, Sha("abcd")),
        ]);
        var installed = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["same.DLL"] = (3, Sha("abc").ToUpperInvariant()),
            ["changed.dll"] = (3, Sha("old")),
            ["other-size.dll"] = (5, Sha("abcd")),
            ["deleted.dll"] = (1, Sha("x")),
        };
        var (reuse, fetch) = AppFiles.Diff(manifest, installed);
        Assert.Equal(["Same.dll"], reuse.Select(e => e.Path));
        Assert.Equal(["changed.dll", "new/file.txt", "other-size.dll"], fetch.Select(e => e.Path));
    }

    [Fact]
    public void RangesMergeAdjacentFilesWithinTheirLimits()
    {
        // Adjacent, a small gap, then a gap too wide.
        var spans = AppFiles.Ranges([E("c", 300, 50), E("a", 0, 100), E("b", 100, 100), E("d", 1000, 10)], maxGap: 100, maxSpan: 1000);
        Assert.Equal(2, spans.Count);
        Assert.Equal((0L, 350L), (spans[0].Start, spans[0].End));
        Assert.Equal(["a", "b", "c"], spans[0].Entries.Select(e => e.Path));
        Assert.Equal((1000L, 1010L), (spans[1].Start, spans[1].End));

        // A span stops growing at its limit; a file larger than the limit is a span of its own.
        spans = AppFiles.Ranges([E("a", 0, 60), E("b", 60, 60), E("c", 120, 500), E("d", 620, 10)], maxGap: 0, maxSpan: 130);
        Assert.Equal([(0L, 120L), (120L, 620L), (620L, 630L)], spans.Select(s => (s.Start, s.End)));
        Assert.Empty(AppFiles.Ranges([]));
    }

    private static byte[] Frame(byte[] bytes)
    {
        using var compressor = new Compressor(3);
        return compressor.Wrap(bytes).ToArray();
    }

    [Fact]
    public async Task DecompressesAndVerifiesEachFile()
    {
        var data = Encoding.UTF8.GetBytes(new string('q', 100_000) + "end");
        var frame = Frame(data);
        var good = new AppFiles.Entry("q.bin", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), 0, frame.Length);
        var target = Path.Combine(_root, "q.bin");
        var ct = TestContext.Current.CancellationToken;

        // Followed by the next file's bytes in the same stream: only this frame is read.
        using (var stream = new MemoryStream([.. frame, 1, 2, 3]))
        {
            await AppFiles.WriteVerifiedAsync(stream, good, target, ct);
            Assert.Equal(frame.Length, stream.Position);
        }
        Assert.Equal(data, File.ReadAllBytes(target));

        await Assert.ThrowsAsync<InvalidDataException>(() => AppFiles.WriteVerifiedAsync(new MemoryStream(frame), good with { Sha256 = Revision }, target, ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppFiles.WriteVerifiedAsync(new MemoryStream(frame), good with { Size = data.Length - 1 }, target, ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppFiles.WriteVerifiedAsync(new MemoryStream(frame), good with { Size = data.Length + 1 }, target, ct));
        // Truncated: the pack ended early.
        await Assert.ThrowsAnyAsync<IOException>(() => AppFiles.WriteVerifiedAsync(new MemoryStream(frame[..^4]), good, target, ct));
        // A flipped byte in the frame.
        var broken = frame.ToArray();
        broken[broken.Length / 2] ^= 0x5a;
        await Assert.ThrowsAnyAsync<Exception>(() => AppFiles.WriteVerifiedAsync(new MemoryStream(broken), good, target, ct));
        // Bytes after the frame inside the file's own length.
        var padded = new AppFiles.Entry("q.bin", data.Length, good.Sha256, 0, frame.Length + 3);
        await Assert.ThrowsAnyAsync<Exception>(() => AppFiles.WriteVerifiedAsync(new MemoryStream([.. frame, 0, 0, 0]), padded, target, ct));
    }

    [Fact]
    public async Task HashCacheReadsAgainOnlyWhatChanged()
    {
        var app = Folder("app", ("a.dll", "aaaa"), ("b.dll", "bbbb"));
        var cache = Path.Combine(_root, "updates", AppFileHashes.CacheName);
        var ct = TestContext.Current.CancellationToken;
        var first = await AppFileHashes.HashAsync(app, cache, (_, _) => true, ct);
        Assert.Equal(Sha("aaaa"), first["A.DLL"].Sha256);
        Assert.True(File.Exists(cache));

        // Same size, other bytes, later time: read again. A cache entry with a wrong hash but the right size and time wins,
        // which is what makes the cache a cache; the size and time are what a change moves.
        File.WriteAllText(Path.Combine(app, "a.dll"), "AAAA");
        File.SetLastWriteTimeUtc(Path.Combine(app, "a.dll"), DateTime.UtcNow.AddMinutes(1));
        var second = await AppFileHashes.HashAsync(app, cache, (_, _) => true, ct);
        Assert.Equal(Sha("AAAA"), second["a.dll"].Sha256);
        Assert.Equal(Sha("bbbb"), second["b.dll"].Sha256);

        // Only what is asked for is hashed.
        var some = await AppFileHashes.HashAsync(app, cache, (path, _) => path == "b.dll", ct);
        Assert.Equal(["b.dll"], some.Keys);

        // A cache for another folder, or a broken one, is not used.
        var other = Folder("other", ("a.dll", "zzzz"));
        File.SetLastWriteTimeUtc(Path.Combine(other, "a.dll"), File.GetLastWriteTimeUtc(Path.Combine(app, "a.dll")));
        Assert.Equal(Sha("zzzz"), (await AppFileHashes.HashAsync(other, cache, (_, _) => true, ct))["a.dll"].Sha256);
        File.WriteAllText(cache, "{ broken");
        Assert.Equal(Sha("AAAA"), (await AppFileHashes.HashAsync(app, cache, (_, _) => true, ct))["a.dll"].Sha256);
    }

    [Fact]
    public async Task UnpackReconstructsExactAppTreeAndRejectsTamperedPack()
    {
        var src = Folder("src", ("ExoLauncher.exe", "bin-exe"), ("payload-manifest.json", "{}"), ("sub/data.bin", "hello-world"));
        var packDir = Path.Combine(_root, "pack");
        var outDir = Path.Combine(_root, "unpacked");
        var ct = TestContext.Current.CancellationToken;
        var built = AppFiles.Build(AppFiles.Folder(src), "3.30.3", Revision, packDir, level: 3);

        var manifestPath = Path.Combine(packDir, AppFiles.ManifestName);
        var packPath = Path.Combine(packDir, AppFiles.PackName);
        var unpacked = await AppFiles.UnpackAsync(manifestPath, packPath, outDir, ct);
        Assert.Equal(built.Files.Count, unpacked.Files.Count);
        Assert.Equal("bin-exe", File.ReadAllText(Path.Combine(outDir, "ExoLauncher.exe")));
        Assert.Equal("hello-world", File.ReadAllText(Path.Combine(outDir, "sub", "data.bin")));

        var tamperedPack = File.ReadAllBytes(packPath);
        tamperedPack[^1] ^= 0xff;
        var badPackPath = Path.Combine(packDir, "bad.pack");
        File.WriteAllBytes(badPackPath, tamperedPack);
        await Assert.ThrowsAsync<InvalidDataException>(() => AppFiles.UnpackAsync(manifestPath, badPackPath, Path.Combine(_root, "bad-out"), ct));
    }
}

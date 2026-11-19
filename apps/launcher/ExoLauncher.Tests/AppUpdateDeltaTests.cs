using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the file-level update could fail (AppUpdateService.Delta, tools/installer/Apply-ExoDelta.ps1):
/// - files.json or files.pack is taken from a host other than Exo's update address, from another tag, or with a query;
/// - it runs when it must not: Exo not in its installed folder, no installer record or revision, the release's installer
///   changed, a game runs, the last try at this version failed, or the changed files are no smaller than the installer;
/// - a server that ignores the range (a whole 200), sends another range, or corrupt bytes still gets a new folder built;
///   any failure leaves a staging folder behind, touches the installed folder, or skips the full installer fallback;
/// - the new folder misses a new file, keeps a deleted one, copies an unchanged file instead of linking it, or lacks Setup's files;
/// - skipping versions compares with the release before instead of what is installed;
/// - the swap renames the old folder when the staged one fails its payload check, or loses the old folder when the second
///   rename fails.
/// The end-to-end case builds two releases' files from fake app folders, serves them from a local HTTP server with byte
/// ranges (Exo's own URLs, rewritten to the server below the HTTP client), and updates one to the other through InstallAsync.
/// </summary>
public sealed class AppUpdateDeltaTests : IDisposable
{
    private const string Revision = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string Address = "https://updates.getexo.app/v1/launcher/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ExoLauncher-delta-" + Guid.NewGuid().ToString("N"));

    public AppUpdateDeltaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    /// <summary>JSON written with single quotes, so it reads inside a C# string.</summary>
    private static string Q(string json) => json.Replace('\'', '"');

    [Fact]
    public void TakesTheFileListOnlyAsExoBuildsItOnItsAddress()
    {
        var offer = AppUpdateService.ReadDeltaOffer(Json(Q($"{{'files':{{'setup':'x','manifest':'{Address}v3.27.0/files.json','pack':'{Address}v3.27.0/files.pack'}}}}")), "v3.27.0");
        Assert.NotNull(offer);
        Assert.Equal(Address + "v3.27.0/files.json", offer.ManifestUrl);
        Assert.Equal(Address + "v3.27.0/files.pack", offer.PackUrl);
        Assert.Null(offer.ManifestSha256);

        // The download/ form too, and the address's checks for files.json.
        var sha = new string('c', 64);
        offer = AppUpdateService.ReadDeltaOffer(Json(Q($"{{'files':['{Address}download/v3.27.0/files.json','{Address}download/v3.27.0/files.pack'],'checks':{{'files':{{'size':12,'sha256':'{sha}'}}}}}}")), "v3.27.0");
        Assert.Equal(Address + "download/v3.27.0/files.pack", offer!.PackUrl);
        Assert.Equal((12L, sha), (offer.ManifestSize!.Value, offer.ManifestSha256!));

        // As the exo-updates Worker answers: files.filesJson and files.filesPack, each with its check.
        offer = AppUpdateService.ReadDeltaOffer(Json(Q($"{{'files':{{'setup':'{Address}download/v3.27.0/ExoLauncher-Setup.exe','filesJson':'{Address}download/v3.27.0/files.json','filesPack':'{Address}download/v3.27.0/files.pack'}},'checks':{{'filesJson':{{'name':'files.json','size':12,'sha256':'{sha}'}},'filesPack':{{'name':'files.pack','size':99,'sha256':'{sha}'}}}}}}")), "v3.27.0");
        Assert.Equal((12L, 99L), (offer!.ManifestSize!.Value, offer.PackSize!.Value));
        Assert.Equal(sha, offer.PackSha256);

        foreach (var bad in new[]
                 {
                     "https://evil.example/v1/launcher/v3.27.0/files.json",
                     "http://updates.getexo.app/v1/launcher/v3.27.0/files.json",
                     Address + "v3.26.0/files.json",
                     Address + "v3.27.0/files.json?x=1",
                     "https://updates.getexo.app:8443/v1/launcher/v3.27.0/files.json",
                     "https://user@updates.getexo.app/v1/launcher/v3.27.0/files.json",
                     Address + "v3.27.0/../v3.27.0/files.jsonx",
                 })
            Assert.Null(AppUpdateService.ReadDeltaOffer(Json(Q($"{{'files':{{'manifest':'{bad}','pack':'{Address}v3.27.0/files.pack'}}}}")), "v3.27.0"));
        Assert.Null(AppUpdateService.ReadDeltaOffer(Json(Q($"{{'files':{{'manifest':'{Address}v3.27.0/files.json'}}}}")), "v3.27.0"));
        Assert.Null(AppUpdateService.ReadDeltaOffer(Json("""{"tag":"v3.27.0"}"""), "v3.27.0"));
    }

    [Fact]
    public void FallsBackToTheInstallerWhenTheInstallCantTakeFiles()
    {
        var install = @"C:\Users\x\AppData\Local\ExoLauncher";
        var app = install + @"\app";
        Assert.Null(AppUpdateService.DeltaRefusal(app + @"\", install, (app, Revision), null, "3.27.0"));
        Assert.NotNull(AppUpdateService.DeltaRefusal(@"C:\src\bin\Debug", install, (app, Revision), null, "3.27.0"));
        Assert.NotNull(AppUpdateService.DeltaRefusal(app, install, (null, Revision), null, "3.27.0"));
        Assert.NotNull(AppUpdateService.DeltaRefusal(app, install, (@"D:\Other\app", Revision), null, "3.27.0"));
        Assert.NotNull(AppUpdateService.DeltaRefusal(app, install, (app, null), null, "3.27.0"));
        Assert.NotNull(AppUpdateService.DeltaRefusal(app, install, (app, "not a hash"), null, "3.27.0"));
        Assert.NotNull(AppUpdateService.DeltaRefusal(app, install, (app, Revision), "3.27.0", "v3.27.0"));
        Assert.Null(AppUpdateService.DeltaRefusal(app, install, (app, Revision), "3.26.0", "3.27.0"));

        var manifest = new AppFiles.Manifest("3.27.0", Revision, 100, Revision, []);
        var plan = new AppUpdateService.DeltaPlan(manifest, [], [new AppFiles.Entry("a", 10, Revision, 0, 40)]);
        Assert.Null(AppUpdateService.PlanRefusal(plan, "3.27.0", Revision, 1000));
        Assert.NotNull(AppUpdateService.PlanRefusal(plan, "3.28.0", Revision, 1000));
        Assert.NotNull(AppUpdateService.PlanRefusal(plan, "3.27.0", new string('3', 64), 1000));
        Assert.NotNull(AppUpdateService.PlanRefusal(plan, "3.27.0", Revision, 40));
    }

    // ---- The end-to-end run ------------------------------------------------------------------------------------------

    private static void Write(string folder, string path, byte[] bytes)
    {
        var full = Path.Combine(folder, path.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static void Write(string folder, string path, string text) => Write(folder, path, Encoding.UTF8.GetBytes(text));

    /// <summary>The installer's payload list for a folder, as New-PayloadManifest.ps1 writes it.</summary>
    private static void PayloadList(string folder, string version)
    {
        var files = AppFiles.Folder(folder).Where(f => f.Path != AppFiles.PayloadManifestName).Select(f => new
        {
            path = f.Path,
            size = new FileInfo(f.FullPath).Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f.FullPath))),
        });
        File.WriteAllText(Path.Combine(folder, AppFiles.PayloadManifestName), JsonSerializer.Serialize(new { schema = 1, version, files }));
    }

    private static readonly byte[] Big = RandomNumberGenerator.GetBytes(300_000);

    /// <summary>A release's app folder: <paramref name="version"/> changes ExoLauncher.exe and the page; Same.dll never changes.</summary>
    private string Release(string version, bool withOld = false, bool withNew = false)
    {
        var folder = Path.Combine(_root, "release-" + version);
        Write(folder, "ExoLauncher.exe", "exe " + version);
        Write(folder, "Same.dll", Big);
        Write(folder, "wwwroot/index.html", "<p>" + version + "</p>");
        if (withOld) Write(folder, "Old.dll", "only in the old release");
        if (withNew) Write(folder, "wwwroot/added/app.js", "new in " + version);
        PayloadList(folder, version);
        return folder;
    }

    /// <summary>The files a release publishes: files.json and files.pack, with the Setup window as its one Setup file.</summary>
    private string Publish(string releaseFolder, string version, string revision = Revision)
    {
        var shell = Path.Combine(_root, "shell-" + version);
        Write(shell, "ExoSetup.exe", "setup window " + version);
        var sources = AppFiles.Folder(releaseFolder);
        sources.Add(new AppFiles.Source("ExoSetup.exe", Path.Combine(shell, "ExoSetup.exe"), Setup: true));
        var served = Path.Combine(_root, "served-" + version);
        AppFiles.Build(sources, version, revision, served, level: 3);
        return served;
    }

    /// <summary>An installed app folder as the installer leaves it: the payload, plus Setup's window and NSIS's uninstaller.</summary>
    private string Install(string releaseFolder, string version)
    {
        var app = Path.Combine(_root, "install", "app");
        foreach (var file in AppFiles.Folder(releaseFolder)) Write(app, file.Path, File.ReadAllBytes(file.FullPath));
        Write(app, "ExoSetup.exe", "setup window " + version);
        Write(app, "Uninstall.exe", "nsis uninstaller");
        return app;
    }

    /// <summary>A small HTTP server on loopback that serves files.json, files.pack (with byte ranges) and the latest answer.</summary>
    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loop;
        internal int Port { get; }
        internal string Served { get; set; } = "";
        internal string Tag { get; set; } = "";
        internal bool IgnoreRanges { get; set; }
        internal int CorruptAt { get; set; } = -1;
        /// <summary>A range starting here is answered 500 (the connection drops on that part of the pack).</summary>
        internal long? FailFrom { get; set; }
        /// <summary>Another SHA-256 for files.pack in the latest answer than files.json names.</summary>
        internal string? PackSha { get; set; }
        internal List<string> Ranges { get; } = [];
        internal List<string> Paths { get; } = [];

        internal Server()
        {
            for (var attempt = 0; ; attempt++)
            {
                Port = Random.Shared.Next(20000, 60000);
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                try { listener.Start(); _listener = listener; break; }
                catch (HttpListenerException) when (attempt < 10) { listener.Close(); }
            }
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                    try { Answer(context); } catch (Exception ex) when (ex is HttpListenerException or IOException) { }
                }
            });
        }

        private void Answer(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            lock (Paths) Paths.Add(path);
            var response = context.Response;
            byte[]? body = null;
            if (path == "/v1/launcher/latest")
            {
                // As the exo-updates Worker answers (cloud/updates README): files.filesJson and files.filesPack with their checks.
                var list = File.ReadAllBytes(Path.Combine(Served, AppFiles.ManifestName));
                var pack = new FileInfo(Path.Combine(Served, AppFiles.PackName)).Length;
                var packSha = PackSha ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Served, AppFiles.PackName))));
                var at = $"{Address}download/{Tag}/";
                body = Encoding.UTF8.GetBytes(Q($"{{'app':'launcher','tag':'{Tag}','version':'{Tag[1..]}','source':'mirror'," +
                    $"'files':{{'setup':'{at}ExoLauncher-Setup.exe','filesJson':'{at}files.json','filesPack':'{at}files.pack'}}," +
                    $"'checks':{{'setup':{{'size':5000000,'sha256':'{Revision}'}},'filesJson':{{'name':'files.json','size':{list.Length},'sha256':'{Convert.ToHexStringLower(SHA256.HashData(list))}'}}," +
                    $"'filesPack':{{'name':'files.pack','size':{pack},'sha256':'{packSha}'}}}}}}"));
            }
            else if (path == $"/v1/launcher/download/{Tag}/files.json") body = File.ReadAllBytes(Path.Combine(Served, AppFiles.ManifestName));
            else if (path == $"/v1/launcher/download/{Tag}/files.pack")
            {
                var pack = File.ReadAllBytes(Path.Combine(Served, AppFiles.PackName));
                if (CorruptAt >= 0) pack[CorruptAt] ^= 0x5a;
                var range = context.Request.Headers["Range"];
                lock (Ranges) Ranges.Add(range ?? "");
                if (range is not null && !IgnoreRanges && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var parts = range["bytes=".Length..].Split('-');
                    var from = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    var to = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (from == FailFrom)
                    {
                        response.StatusCode = 500;
                        response.Close();
                        return;
                    }
                    response.StatusCode = 206;
                    response.AddHeader("Content-Range", $"bytes {from}-{to}/{pack.Length}");
                    body = pack[(int)from..(int)(to + 1)];
                }
                else body = pack;
            }
            if (body is null) response.StatusCode = 404;
            else
            {
                response.ContentLength64 = body.Length;
                response.OutputStream.Write(body);
            }
            response.Close();
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Exo's own https URLs, sent to the local server instead; any other host fails the test.</summary>
    private sealed class ToLocal(int port) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var original = request.RequestUri!;
            Assert.Equal("https://updates.getexo.app", original.GetLeftPart(UriPartial.Authority));
            request.RequestUri = new Uri($"http://127.0.0.1:{port}{original.PathAndQuery}");
            var response = await base.SendAsync(request, ct);
            request.RequestUri = original;
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class Run
    {
        internal AppUpdateService.DeltaApply? Applied;
        internal bool InstallerStarted;
        internal readonly List<string> Progress = [];
    }

    private AppUpdateService Service(Server server, string app, Run run, string? revision = Revision, bool game = false)
    {
        var http = ExoHttp.Create(TimeSpan.FromMinutes(1), new ToLocal(server.Port));
        return new AppUpdateService(http, Path.Combine(_root, "install", "updates"), TimeProvider.System, http)
        {
            AppFolder = app,
            InstallFolder = Path.GetDirectoryName(app)!,
            InstalledSetup = () => (app, revision),
            StartDeltaApply = apply => { run.Applied = apply; return true; },
            StartInstaller = _ => { run.InstallerStarted = true; return true; },
            IsInstaller = _ => true,
            GameRunning = () => game,
            FreeBytes = _ => long.MaxValue,
            AppFolderBytes = () => 1_000_000,
            StopLucid = () => Task.FromResult<string?>(null),
            StartLucid = _ => { },
        };
    }

    private sealed class Progress(List<string> into) : IProgress<(string status, double? percent)>
    {
        public void Report((string status, double? percent) value) { lock (into) into.Add(value.status); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public long Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation information);

    private static uint Links(string path)
    {
        using var handle = File.OpenHandle(path);
        Assert.True(GetFileInformationByHandle(handle, out var info));
        return info.Links;
    }

    private static Dictionary<string, string> Tree(string folder) =>
        AppFiles.Folder(folder).ToDictionary(f => f.Path, f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f.FullPath))));

    [Fact]
    public async Task UpdatesFromOneReleaseToTheNextDownloadingOnlyWhatChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var v1 = Release("3.26.0", withOld: true);
        var app = Install(v1, "3.26.0");
        var before = Tree(app);
        // Skipping a version: 3.27.0 is never installed; 3.28.0 is compared with what is.
        Release("3.27.0");
        var v3 = Release("3.28.0", withNew: true);
        using var server = new Server { Served = Publish(v3, "3.28.0"), Tag = "v3.28.0" };
        var run = new Run();
        var service = Service(server, app, run);

        var result = await service.InstallAsync("3.26.0", new Progress(run.Progress), ct);

        Assert.True(result.Installed, result.Message);
        Assert.True(result.ShouldExit);
        Assert.False(run.InstallerStarted);
        Assert.DoesNotContain("/v1/launcher/download/v3.28.0/ExoLauncher-Setup.exe", server.Paths);
        var applied = Assert.IsType<AppUpdateService.DeltaApply>(run.Applied);
        Assert.Equal("3.28.0", applied.Version);
        Assert.Equal(Path.GetDirectoryName(app), applied.InstallRoot);
        Assert.Matches(@"\\app\.incoming-[0-9a-f]{32}$", applied.Incoming);

        // The new folder is exactly the release's payload; Setup's files wait beside it.
        Assert.Equal(Tree(v3), Tree(applied.Incoming));
        Assert.False(File.Exists(Path.Combine(applied.Incoming, "Old.dll")));
        Assert.Equal("setup window 3.28.0", File.ReadAllText(Path.Combine(applied.Setup, "ExoSetup.exe")));
        Assert.Equal("nsis uninstaller", File.ReadAllText(Path.Combine(applied.Setup, "Uninstall.exe")));
        // The unchanged file is a hard link to the installed one, not a download or a copy.
        Assert.True(Links(Path.Combine(applied.Incoming, "Same.dll")) >= 2);
        // Only the changed files' bytes were asked for (the 300 KB Same.dll is not among them), merged into one range.
        var pack = AppFiles.Parse(File.ReadAllBytes(Path.Combine(server.Served, AppFiles.ManifestName)))!;
        var same = pack.Files.Single(f => f.Path == "Same.dll");
        Assert.All(server.Ranges, r => Assert.StartsWith("bytes=", r));
        Assert.DoesNotContain(server.Ranges, r => RangeCovers(r, same));
        Assert.True(server.Ranges.Count <= 2, string.Join(" ", server.Ranges));
        Assert.Contains(run.Progress, p => p.StartsWith("Downloading ", StringComparison.Ordinal) && p.Contains(" of ", StringComparison.Ordinal));
        // The installed folder was not touched.
        Assert.Equal(before, Tree(app));

        // Then the swap, as Apply-ExoDelta.ps1 does it once Exo has closed.
        var previous = Swap(applied, "3.28.0");
        Assert.Equal(Tree(v3), Tree(app));
        Assert.Equal(before, Tree(previous));
    }

    private static bool RangeCovers(string range, AppFiles.Entry entry)
    {
        var parts = range["bytes=".Length..].Split('-');
        return long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) <= entry.Offset &&
               long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) >= entry.End - 1;
    }

    [Theory]
    [InlineData("ignores ranges")]
    [InlineData("corrupt pack")]
    [InlineData("installer changed")]
    [InlineData("game running")]
    [InlineData("no installer record")]
    [InlineData("another pack")]
    public async Task AnyDoubtUsesTheFullInstallerAndLeavesNothingBehind(string why)
    {
        var ct = TestContext.Current.CancellationToken;
        var v1 = Release("3.26.0", withOld: true);
        var app = Install(v1, "3.26.0");
        var before = Tree(app);
        var v2 = Release("3.27.0", withNew: true);
        using var server = new Server { Served = Publish(v2, "3.27.0", why == "installer changed" ? new string('9', 64) : Revision), Tag = "v3.27.0" };
        server.IgnoreRanges = why == "ignores ranges";
        if (why == "another pack") server.PackSha = new string('e', 64);
        if (why == "corrupt pack")
        {
            var manifest = AppFiles.Parse(File.ReadAllBytes(Path.Combine(server.Served, AppFiles.ManifestName)))!;
            server.CorruptAt = (int)manifest.Files.Single(f => f.Path == "ExoLauncher.exe").Offset + 4;
        }
        var run = new Run();
        var service = Service(server, app, run, revision: why == "no installer record" ? null : Revision, game: why == "game running");

        var result = await service.InstallAsync("3.26.0", null, ct);

        Assert.Null(run.Applied);
        // The installer was asked for instead (this server has none, so the update fails cleanly).
        Assert.Contains("/v1/launcher/download/v3.27.0/ExoLauncher-Setup.exe", server.Paths);
        Assert.False(result.Installed);
        Assert.Equal(before, Tree(app));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(app)!, "app.incoming-*"));
    }

    [Fact]
    public async Task AnInterruptedTryKeepsWhatItCheckedAndTheNextOneDownloadsOnlyTheRest()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = Install(Release("3.26.0"), "3.26.0");
        var v2 = Release("3.27.0", withNew: true);
        using var server = new Server { Served = Publish(v2, "3.27.0"), Tag = "v3.27.0" };
        var manifest = AppFiles.Parse(File.ReadAllBytes(Path.Combine(server.Served, AppFiles.ManifestName)))!;
        var payload = manifest.Files.Single(f => f.Path == AppFiles.PayloadManifestName);
        // The unchanged 300 KB Same.dll splits the changed files into two ranges; the first one fails every time.
        server.FailFrom = 0;
        var run = new Run();
        var first = await Service(server, app, run).InstallAsync("3.26.0", null, ct);
        Assert.False(first.Installed);
        Assert.Null(run.Applied);
        Assert.Contains(server.Ranges, r => RangeCovers(r, payload));
        var parts = Path.Combine(_root, "install", "updates", AppUpdateService.DeltaPartsFolder);
        Assert.True(File.Exists(Path.Combine(parts, payload.Sha256)));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(app)!, "app.incoming-*"));

        // The next try: the kept file is checked and used; only the first range is asked for again.
        server.FailFrom = null;
        server.Ranges.Clear();
        run = new Run();
        var second = await Service(server, app, run).InstallAsync("3.26.0", null, ct);
        Assert.True(second.Installed, second.Message);
        var applied = Assert.IsType<AppUpdateService.DeltaApply>(run.Applied);
        Assert.Equal(Tree(v2), Tree(applied.Incoming));
        Assert.DoesNotContain(server.Ranges, r => RangeCovers(r, payload));
        Assert.False(Directory.Exists(parts));
    }

    [Fact]
    public void AKeptFileThatNoLongerChecksOutIsDownloadedAgain()
    {
        var parts = Path.Combine(_root, "parts");
        var staged = Path.Combine(_root, "staged");
        Directory.CreateDirectory(parts);
        var good = Encoding.UTF8.GetBytes("good bytes");
        var goodSha = Convert.ToHexStringLower(SHA256.HashData(good));
        var badSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("what it should be")));
        File.WriteAllBytes(Path.Combine(parts, goodSha), good);
        File.WriteAllText(Path.Combine(parts, badSha), "what it is now!!!");
        File.WriteAllText(Path.Combine(parts, new string('a', 64)), "another version's file");
        var fetch = new List<AppFiles.Entry>
        {
            new("a/good.dll", good.Length, goodSha, 0, 10),
            new("bad.dll", 17, badSha, 10, 10),
        };
        var fetched = new System.Collections.Concurrent.ConcurrentDictionary<string, AppFiles.Entry>();
        var remaining = AppUpdateService.ResumeFromParts(parts, fetch, e => Path.Combine(staged, e.Path.Replace('/', '\\')), fetched);
        Assert.Equal(["bad.dll"], remaining.Select(e => e.Path));
        Assert.Equal("good bytes", File.ReadAllText(Path.Combine(staged, "a", "good.dll")));
        Assert.Equal(["a/good.dll"], fetched.Keys);
        // Only the file this plan uses stays.
        Assert.Equal([goodSha], Directory.GetFiles(parts).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ALastFailedTryAtThisVersionUsesTheInstaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = Install(Release("3.26.0"), "3.26.0");
        using var server = new Server { Served = Publish(Release("3.27.0"), "3.27.0"), Tag = "v3.27.0" };
        var updates = Path.Combine(_root, "install", "updates");
        Directory.CreateDirectory(updates);
        File.WriteAllText(Path.Combine(updates, AppUpdateService.DeltaFailedName), """{"schema":1,"version":"3.27.0"}""");
        var run = new Run();
        await Service(server, app, run).InstallAsync("3.26.0", null, ct);
        Assert.Null(run.Applied);
        Assert.DoesNotContain("/v1/launcher/download/v3.27.0/files.pack", server.Paths);
    }

    // ---- The swap script ---------------------------------------------------------------------------------------------

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Runs Invoke-ExoDeltaSwap from the script (definitions only); the backup folder it made.</summary>
    private static string Swap(AppUpdateService.DeltaApply apply, string version, bool expectFailure = false)
    {
        var command = $". {Quote(FindScript())} -DefinitionsOnly; $p = Get-ExoDeltaPaths {Quote(apply.InstallRoot)} {Quote(apply.Incoming)} {Quote(apply.Setup)}; " +
                      $"try {{ Invoke-ExoDeltaSwap $p {Quote(version)}; Write-Output $p.previous }} catch {{ Write-Output ('FAILED ' + $_.Exception.Message); exit 3 }}";
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (expectFailure)
        {
            Assert.Equal(3, process.ExitCode);
            return output;
        }
        Assert.True(process.ExitCode == 0, output + error);
        return output.Split('\n').Last().Trim();
    }

    private static string FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "installer", "Apply-ExoDelta.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Apply-ExoDelta.ps1");
    }

    private AppUpdateService.DeltaApply Staged(string app, string releaseFolder, string version)
    {
        var root = Path.GetDirectoryName(app)!;
        var incoming = Path.Combine(root, "app.incoming-" + Guid.NewGuid().ToString("N"));
        foreach (var file in AppFiles.Folder(releaseFolder)) Write(incoming, file.Path, File.ReadAllBytes(file.FullPath));
        Directory.CreateDirectory(incoming + "setup");
        return new AppUpdateService.DeltaApply(root, incoming, incoming + "setup", version, Path.Combine(root, "updates"), 0);
    }

    [Fact]
    public void TheSwapLeavesTheOldFolderWhenTheNewOneFailsItsCheck()
    {
        var app = Install(Release("3.26.0"), "3.26.0");
        var before = Tree(app);
        var apply = Staged(app, Release("3.27.0"), "3.27.0");
        // One staged file is not what the payload list says.
        File.AppendAllText(Path.Combine(apply.Incoming, "wwwroot", "index.html"), "tampered");
        var said = Swap(apply, "3.27.0", expectFailure: true);
        Assert.StartsWith("FAILED", said);
        Assert.Equal(before, Tree(app));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(app)!, "app.previous-*"));

        // Staged for another version than the one asked: refused too.
        var other = Staged(app, Release("3.28.0"), "3.28.0");
        Assert.StartsWith("FAILED", Swap(other, "3.27.0", expectFailure: true));
        Assert.Equal(before, Tree(app));
    }

    [Fact]
    public void TheSwapPutsTheOldFolderBackWhenTheNewOneCantMoveIn()
    {
        var app = Install(Release("3.26.0"), "3.26.0");
        var before = Tree(app);
        var apply = Staged(app, Release("3.27.0"), "3.27.0");
        // A file in the staged folder held open without delete sharing: Windows refuses to rename the folder.
        using (new FileStream(Path.Combine(apply.Incoming, "Same.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.StartsWith("FAILED", Swap(apply, "3.27.0", expectFailure: true));
        Assert.Equal(before, Tree(app));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(app)!, "app.previous-*"));
        Assert.True(Directory.Exists(apply.Incoming));
    }

    [Fact]
    public void TheSwapRefusesFoldersOutsideTheInstall()
    {
        var app = Install(Release("3.26.0"), "3.26.0");
        var apply = Staged(app, Release("3.27.0"), "3.27.0");
        var elsewhere = apply with { Incoming = Path.Combine(_root, "app.incoming-" + Guid.NewGuid().ToString("N")) };
        Directory.CreateDirectory(elsewhere.Incoming);
        Assert.ThrowsAny<Exception>(() => Swap(elsewhere with { Setup = elsewhere.Incoming + "setup" }, "3.27.0"));
        var badName = apply with { Incoming = Path.Combine(apply.InstallRoot, "app.incoming-123"), Setup = Path.Combine(apply.InstallRoot, "app.incoming-123setup") };
        Assert.ThrowsAny<Exception>(() => Swap(badName, "3.27.0"));
    }
}

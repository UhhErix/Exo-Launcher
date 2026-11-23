using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

public sealed partial class HostV1GoldenTests
{
    private const string UpdateEnvVar = "EXO_UPDATE_GOLDEN";

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> VolatilePathKeys = new(StringComparer.Ordinal)
    {
        "coversDir",
        "devicesDir",
        "notesDir",
        "capturesDir",
        "captureThumbsDir",
        "defaultInstallRoot",
    };

    private static readonly HashSet<string> VolatileTimestampKeys = new(StringComparer.Ordinal)
    {
        "checkedAtUtc",
        "updatedAt",
        "createdAt",
        "timestamp",
    };

    private static readonly HashSet<string> VolatilePipeKeys = new(StringComparer.Ordinal)
    {
        "binaryMediaPipe",
    };

    private static string FindGoldenDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var testsDir = Path.Combine(dir.FullName, "ExoLauncher.Tests");
            if (File.Exists(Path.Combine(testsDir, "ExoLauncher.Tests.csproj")))
                return Path.Combine(testsDir, "Golden");
            if (File.Exists(Path.Combine(dir.FullName, "ExoLauncher.Tests.csproj")))
                return Path.Combine(dir.FullName, "Golden");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate ExoLauncher.Tests project directory for Golden files.");
    }

    internal static string NormalizeGoldenJson(string rawJson)
    {
        var root = JsonNode.Parse(rawJson)
            ?? throw new InvalidOperationException("Host reply was empty JSON.");
        NormalizeNode(root, parentKey: null, isRoot: true);
        return root.ToJsonString(PrettyJson).Replace("\r\n", "\n") + "\n";
    }

    private static void NormalizeNode(JsonNode? node, string? parentKey, bool isRoot)
    {
        if (node is JsonObject obj)
        {
            var keys = obj.Select(kv => kv.Key).ToList();
            foreach (var key in keys)
            {
                var child = obj[key];
                if (isRoot && key == "id" && child is JsonValue)
                {
                    obj[key] = "<REQUEST_ID>";
                    continue;
                }
                if (child is JsonValue val && val.TryGetValue<string>(out var str))
                {
                    obj[key] = NormalizeString(key, str);
                }
                else
                {
                    NormalizeNode(child, key, isRoot: false);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is JsonValue val && val.TryGetValue<string>(out var str))
                    arr[i] = NormalizeString(parentKey, str);
                else
                    NormalizeNode(arr[i], parentKey, isRoot: false);
            }
        }
    }

    private static string NormalizeString(string? key, string value)
    {
        if (key is not null && VolatilePipeKeys.Contains(key))
            return "<PIPE_NAME>";
        if (key is not null && VolatilePathKeys.Contains(key) && value.Length > 0)
            return "<PATH>";
        if (key is not null && VolatileTimestampKeys.Contains(key) && IsoTimestampRegex().IsMatch(value))
            return "<TIMESTAMP>";

        var normalized = LocalFileTokenRegex().Replace(value, "$1<TOKEN>/");
        if (IsoTimestampRegex().IsMatch(normalized))
            return "<TIMESTAMP>";
        if (WindowsPathRegex().IsMatch(normalized))
            return "<PATH>";
        if (GuidRegex().IsMatch(normalized))
            return GuidRegex().Replace(normalized, "<GUID>");
        return normalized;
    }

    private static AppSettings FixedSettings() => new()
    {
        WhatsNewSeen = "3.29.0",
        ReduceMotion = false,
        StartMaximized = true,
        StartWithWindows = false,
        FramelessPinTop = false,
        FramelessPinSide = false,
        TextColour = "white",
        TextSize = "default",
        Background = "void",
        SortMode = "recent",
        DefaultInstallRoot = @"C:\Games\ExoLibrary",
        PreferredGameSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["portal 2"] = "steam",
        },
        Recent = ["steam:620"],
        OnboardingComplete = true,
        ShowLibraryTools = true,
        AchievementBanners = true,
        AchievementVolume = 75,
        FpsCounter = false,
        FpsCounterPosition = "top-right",
        FpsCounterItems = ["fps", "frametime"],
        FpsCounterHideOverBuiltIn = true,
        HdrForGames = true,
        FrameGenEverywhere = false,
        HdrSdrGamma = true,
        DiscordNoiseSuppression = true,
        DiscordQuietWhilePlaying = true,
        PauseDownloadsWhilePlaying = true,
        DownloadHoursStart = null,
        DownloadHoursEnd = null,
        AutoUpdateGames = true,
        AutoUpdateChipset = true,
        AutoUpdateUpscalers = true,
        UpscalerAntiCheatEverywhere = false,
        KeepAwakeDownloads = true,
        StoreClientsQuiet = true,
        LibraryFolders =
        [
            new LibraryFolder
            {
                Id = "folder-coop",
                Name = "Co-op",
                Color = "blue",
                Sort = "folder",
                Art = null,
                Games = ["steam:620"],
            },
        ],
        HiddenGames = [],
    };

    private static object FixedSettingsResponse() =>
        WebHostBridge.BuildSettingsResponse(
            FixedSettings(),
            appVersion: "3.29.0",
            musicService: "spotify",
            riotId: null,
            riotBaseline: null,
            frameGenEverywhereCard: true,
            chipset: "amd",
            resolveFolderArt: _ => null);

    private static object FixedLibraryResponse()
    {
        var portal = new GameEntry
        {
            Id = "steam:620",
            Title = "Portal 2",
            Store = StoreKind.Steam,
            Installed = true,
            Owned = true,
            LaunchTarget = "620",
            Status = "Ready",
            PlaytimeMinutes = 245,
            SizeBytes = 12_884_901_888,
        };
        var hollow = new GameEntry
        {
            Id = "gog:1308320804",
            Title = "Hollow Knight",
            Store = StoreKind.Gog,
            Installed = false,
            Owned = true,
            LaunchTarget = "1308320804",
            Status = "Not installed",
            PlaytimeMinutes = 0,
        };
        var games = new List<GameEntry> { portal, hollow };
        var mapped = games
            .Select(g => WebHostBridge.GameForPage(g, new GameRunState(false, false), _ => new GameRunState(false, false)))
            .ToList();
        var stores = new List<LibraryService.StoreBackendStatus>
        {
            new(
                store: "steam",
                displayName: "Steam",
                agentPresent: true,
                clientPresent: true,
                backendPresent: true,
                signedIn: true,
                cachePresent: true,
                detail: "Ready",
                checkCode: "ok",
                checkedAtUtc: new DateTimeOffset(2026, 11, 22, 18, 30, 0, TimeSpan.Zero)),
            new(
                store: "gog",
                displayName: "GOG",
                agentPresent: true,
                clientPresent: false,
                backendPresent: true,
                signedIn: true,
                cachePresent: true,
                detail: "Ready",
                checkCode: "ok",
                checkedAtUtc: new DateTimeOffset(2026, 11, 22, 18, 30, 0, TimeSpan.Zero)),
        };
        var progress = new InstallProgress
        {
            GameId = "",
            Phase = InstallPhase.Idle,
            Status = "",
            CanCancel = false,
        };
        return WebHostBridge.BuildLibraryResponse(
            mapped,
            games.Count,
            stores,
            progress,
            queuedGameIds: [],
            pausedDownloads: [],
            sortMode: "recent",
            recent: ["steam:620"]);
    }

    private static object FixedStoreGameResponse()
    {
        var detail = new StoreService.GameDetail(
            AppId: "620",
            Name: "Portal 2",
            Description: "The Perpetual Testing Initiative has been expanded to allow you to design co-op puzzles.",
            Header: "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/620/header.jpg",
            ComingSoon: false,
            Released: "Apr 18, 2011",
            Developers: ["Valve"],
            Publishers: ["Valve"],
            Genres: ["Action", "Adventure"],
            Metacritic: 95,
            ReviewWord: "Overwhelmingly Positive",
            ReviewPercent: 98,
            ReviewCount: 340000,
            Free: false,
            Offers:
            [
                new StoreService.Offer(
                    Store: "steam",
                    Name: "Steam",
                    Final: 999,
                    Initial: 999,
                    Discount: 0,
                    Url: "https://store.steampowered.com/app/620")
                {
                    Currency = "USD",
                    PriceText = "$9.99",
                },
            ],
            Media:
            [
                new StoreService.Media(
                    Thumb: "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/256658589/movie.293x165.jpg",
                    Hls: "https://video.fastly.steamstatic.com/store_trailers/256658589/hls_264_master.m3u8",
                    Trailer: true),
            ],
            Screenshots:
            [
                "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/620/ss_1.1920x1080.jpg",
            ])
        {
            Art = new StoreService.Art(
                Poster: "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/620/library_600x900.jpg",
                Hero: "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/620/library_hero.jpg",
                Capsule: "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/620/capsule_616x353.jpg",
                Header: "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/620/header.jpg"),
            Specs =
            [
                new StoreService.Spec("OS", "Windows 10", "Windows 11"),
                new StoreService.Spec("Memory", "2 GB RAM", "4 GB RAM"),
                new StoreService.Spec("Storage", "8 GB available space", "8 GB available space"),
            ],
            Tags = ["Puzzle", "Co-op", "Sci-fi"],
            Controller = "full",
            PlayStation = true,
            Languages = ["English", "Spanish"],
        };
        var pc = new StoreService.ThisPc(
            Cpu: "AMD Ryzen 7 9800X3D",
            Gpu: "NVIDIA GeForce RTX 4090",
            RamGb: 64,
            Windows: 11,
            FreeGb: 512,
            VramGb: 24,
            DirectX: 12,
            Online: true);
        return WebHostBridge.BuildStoreGameResponse(detail, pc);
    }

    private static object FixedDiscordStatusResponse() =>
        DiscordSession.BuildStatusResponse(state: "ready", message: null, signedIn: true);

    private static object FixedDiscordHomeResponse()
    {
        var cache = new DiscordCache();
        using var ready = JsonDocument.Parse("""
        {
          "session_id": "golden-session",
          "user": {
            "id": "100000000000000001",
            "username": "exoplayer",
            "global_name": "Exo Player",
            "avatar": null
          },
          "guilds": [
            {
              "id": "200000000000000001",
              "name": "Exo Testing",
              "owner_id": "100000000000000001",
              "roles": [
                { "id": "200000000000000001", "name": "@everyone", "permissions": "3072" }
              ],
              "members": [
                { "user": { "id": "100000000000000001" }, "roles": [] }
              ],
              "channels": [
                { "id": "300000000000000001", "type": 0, "name": "general", "position": 0, "last_message_id": "400000000000000001" }
              ]
            }
          ],
          "private_channels": [
            {
              "id": "500000000000000001",
              "type": 1,
              "last_message_id": "600000000000000001",
              "recipients": [
                { "id": "100000000000000002", "username": "ally", "global_name": "Ally", "avatar": null }
              ]
            }
          ],
          "read_state": {
            "entries": [
              { "id": "300000000000000001", "last_message_id": "400000000000000001" }
            ]
          }
        }
        """);
        cache.LoadReady(ready.RootElement);
        return cache.Home();
    }

    private static object FixedMusicPlaybackResponse() =>
        HeadlessMusicService.BuildPlaybackResponse(
            HeadlessMusicService.BuildPlaybackDto(
                provider: "spotify",
                status: "playing",
                title: "Still Alive",
                artist: "Jonathan Coulton",
                album: "Portal Soundtrack",
                positionMs: 42_000,
                durationMs: 176_000,
                canPlay: true,
                canPause: true,
                canPrev: true,
                canNext: true,
                canSeek: true));

    private static object FixedArtBaseResponse() =>
        WebHostBridge.BuildArtBaseResponse(
            isNamedPipe: true,
            covers: LocalFileHost.Url(CoverArtService.VirtualHost, ""),
            coversDir: @"C:\Users\Fixture\AppData\Local\ExoLauncher\covers",
            devicesDir: @"C:\Users\Fixture\AppData\Local\ExoLauncher\devices",
            notesDir: @"C:\Users\Fixture\AppData\Local\ExoLauncher\notes",
            capturesDir: @"C:\Users\Fixture\Videos\Exo Captures",
            captureThumbsDir: @"C:\Users\Fixture\AppData\Local\ExoLauncher\thumbs");

    private static object DispatchFixed(NamedPipeHostTransport pipe, string method, JsonElement paramsEl, bool hasParams) =>
        method switch
        {
            "shell.bootstrap" => WebHostBridge.BuildShellBootstrapResponse(
                FixedArtBaseResponse(),
                FixedSettingsResponse(),
                FixedLibraryResponse(),
                FixedMusicPlaybackResponse(),
                FixedDiscordStatusResponse(),
                pipe.MediaPipeName),
            "library.get" => FixedLibraryResponse(),
            "store.game" when hasParams && paramsEl.TryGetProperty("appId", out var appId) && appId.GetString() == "620" => FixedStoreGameResponse(),
            "store.game" => new { ok = false, message = "No game." },
            "discord.status" => FixedDiscordStatusResponse(),
            "discord.home" => FixedDiscordHomeResponse(),
            "music.playback" => FixedMusicPlaybackResponse(),
            "settings.get" => FixedSettingsResponse(),
            _ => throw new InvalidOperationException($"Unsupported test method: {method}"),
        };

    private static async Task<string> RoundTripPipeAsync(string method, object? parameters, CancellationToken ct)
    {
        var pipeName = $"exo-golden-{Guid.NewGuid():N}";
        using var transport = new NamedPipeHostTransport(pipeName);
        transport.MessageReceived += raw =>
        {
            if (!ExoBridgeProtocol.TryParseRequest(raw, out var req))
                return;
            try
            {
                var result = DispatchFixed(transport, req.Method, req.Params, req.HasParams);
                transport.PostJsonUtf8(ExoBridgeProtocol.SerializeResponseUtf8(req.Id, ok: true, result, error: null));
            }
            catch (Exception ex)
            {
                transport.PostJsonUtf8(ExoBridgeProtocol.SerializeResponseUtf8(req.Id, ok: false, result: null, error: ex.Message));
            }
        };
        transport.Start();

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var requestId = $"req-{Guid.NewGuid():N}";
        var requestJson = parameters is null
            ? JsonSerializer.Serialize(new { version = ExoBridgeProtocol.Version, id = requestId, method })
            : JsonSerializer.Serialize(new { version = ExoBridgeProtocol.Version, id = requestId, method, @params = parameters });

        await writer.WriteLineAsync(requestJson.AsMemory(), ct);
        var reply = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.NotNull(reply);
        return reply!;
    }

    private static async Task AssertMatchesGoldenAsync(string method, object? parameters = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var rawReply = await RoundTripPipeAsync(method, parameters, ct);
        var actual = NormalizeGoldenJson(rawReply);

        var goldenDir = FindGoldenDirectory();
        var goldenPath = Path.Combine(goldenDir, $"{method}.json");
        var update = string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1", StringComparison.Ordinal);

        if (update)
        {
            Directory.CreateDirectory(goldenDir);
            await File.WriteAllTextAsync(goldenPath, actual, new UTF8Encoding(false), ct);
            return;
        }

        Assert.True(
            File.Exists(goldenPath),
            $"Golden file missing: {goldenPath}. Run tests with {UpdateEnvVar}=1 to generate it.");

        var expected = (await File.ReadAllTextAsync(goldenPath, ct)).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public Task ShellBootstrapMatchesGolden() => AssertMatchesGoldenAsync("shell.bootstrap");

    [Fact]
    public Task LibraryGetMatchesGolden() => AssertMatchesGoldenAsync("library.get");

    [Fact]
    public Task StoreGameMatchesGolden() => AssertMatchesGoldenAsync("store.game", new { appId = "620" });

    [Fact]
    public Task DiscordStatusMatchesGolden() => AssertMatchesGoldenAsync("discord.status");

    [Fact]
    public Task DiscordHomeMatchesGolden() => AssertMatchesGoldenAsync("discord.home");

    [Fact]
    public Task MusicPlaybackMatchesGolden() => AssertMatchesGoldenAsync("music.playback");

    [Fact]
    public Task SettingsGetMatchesGolden() => AssertMatchesGoldenAsync("settings.get");

    [Fact]
    public void NormalizerScrubsVolatileIdsTimestampsPathsTokensAndPipes()
    {
        var sample = $$"""
        {
          "version": 1,
          "id": "req-550e8400-e29b-41d4-a716-446655440000",
          "ok": true,
          "result": {
            "checkedAtUtc": "2026-11-22T23:10:00.1234567+00:00",
            "covers": "https://covers.exo-launcher.local/{{LocalFileHost.Token}}/",
            "coversDir": "C:\\Users\\Alice\\AppData\\Local\\ExoLauncher\\covers",
            "binaryMediaPipe": "exo-golden-abcdef123456-media"
          }
        }
        """;

        var normalized = NormalizeGoldenJson(sample);
        using var doc = JsonDocument.Parse(normalized);
        var root = doc.RootElement;
        var res = root.GetProperty("result");

        Assert.Equal("<REQUEST_ID>", root.GetProperty("id").GetString());
        Assert.Equal("<TIMESTAMP>", res.GetProperty("checkedAtUtc").GetString());
        Assert.Equal("https://covers.exo-launcher.local/<TOKEN>/", res.GetProperty("covers").GetString());
        Assert.Equal("<PATH>", res.GetProperty("coversDir").GetString());
        Assert.Equal("<PIPE_NAME>", res.GetProperty("binaryMediaPipe").GetString());
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTimestampRegex();

    [GeneratedRegex(@"(https://[a-z0-9.-]+\.exo-launcher\.local/)[0-9a-f]{32}/", RegexOptions.CultureInvariant)]
    private static partial Regex LocalFileTokenRegex();

    [GeneratedRegex(@"^(?:[A-Za-z]:\\|\\\\)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();
}

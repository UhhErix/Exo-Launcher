using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

[Collection("AppDataIsolation")]
public sealed class RiotHoursSyncTests
{
    private const string CloudSuccessJson = """
        {
          "ok": true,
          "cached": false,
          "games": {
            "riot:valorant": {
              "minutes": 4200,
              "matches": 140,
              "partial": true,
              "source": "henrikdev",
              "latestMatchAt": "2026-10-15T18:30:00Z",
              "modes": { "competitive": 3000, "unrated": 1200 }
            },
            "riot:league_of_legends": {
              "minutes": 1800,
              "matches": 60,
              "partial": true,
              "source": "riot-api",
              "latestMatchAt": "2026-10-16T12:00:00Z",
              "modes": { "ranked solo": 1200, "aram": 600 }
            },
            "riot:teamfighttactics": {
              "minutes": 360,
              "matches": 12,
              "partial": true,
              "source": "riot-api"
            }
          }
        }
        """;

    [Fact]
    public void RequestTimeoutExceedsWorkerTotalBudget()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? limitsPath = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "cloud", "riot", "src", "limits.json");
            if (File.Exists(candidate))
            {
                limitsPath = candidate;
                break;
            }
            dir = dir.Parent;
        }
        Assert.NotNull(limitsPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(limitsPath!));
        var budgetMs = doc.RootElement.GetProperty("totalBudgetMs").GetInt32();
        Assert.True(RiotHoursClient.RequestTimeout.TotalMilliseconds > budgetMs);
    }

    [Fact]
    public void ResolveEndpointAllowsHttpsAndLoopbackHttpOnly()
    {
        Assert.Equal("https://riot.getexo.app/v1/hours", RiotHoursClient.ResolveEndpoint("").AbsoluteUri);
        Assert.Equal("http://127.0.0.1:8787/v1/hours", RiotHoursClient.ResolveEndpoint("http://127.0.0.1:8787/v1/hours").AbsoluteUri);
        Assert.Equal("https://staging.getexo.app/v1/hours", RiotHoursClient.ResolveEndpoint("https://staging.getexo.app/v1/hours").AbsoluteUri);
        Assert.Equal(RiotHoursClient.DefaultEndpoint, RiotHoursClient.ResolveEndpoint("http://evil.example/v1/hours"));
    }

    [Fact]
    public async Task CloudClientParsesGamesModesAndSeedsCleanly()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, CloudSuccessJson));
        var backoff = new CoverCacheClient.Backoff();

        var result = await RiotHoursClient.LoadAsync("Player#NA1", handler, TestContext.Current.CancellationToken, "na", backoff: backoff);

        Assert.True(result.Ok);
        Assert.Equal(3, result.Readings.Count);
        Assert.Equal(4200, result.Readings["riot:valorant"].Minutes);
        Assert.Equal(3000, result.Readings["riot:valorant"].Modes!["competitive"]);
        Assert.Equal(1800, result.Readings["riot:league_of_legends"].Minutes);
        Assert.Equal(360, result.Readings["riot:teamfighttactics"].Minutes);
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T12:00:00Z"), result.LatestMatchUtc());

        var seeds = result.ToSeeds();
        Assert.Equal(4200, seeds["riot:valorant"].Minutes);
        Assert.True(seeds["riot:valorant"].Partial);
    }

    [Fact]
    public async Task CloudClientBacksOffOnUnconfiguredAndRateLimit()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return (HttpStatusCode.ServiceUnavailable, """{"ok":false,"unconfigured":true,"message":"Not configured."}""");
        });
        var backoff = new CoverCacheClient.Backoff();
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

        var first = await RiotHoursClient.LoadAsync("Player#NA1", handler, TestContext.Current.CancellationToken, backoff: backoff, nowUtc: now);
        var second = await RiotHoursClient.LoadAsync("Player#NA1", handler, TestContext.Current.CancellationToken, backoff: backoff, nowUtc: now.AddSeconds(30));

        Assert.False(first.Ok);
        Assert.True(first.Unconfigured);
        Assert.False(second.Ok);
        Assert.True(second.TransientFailure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ExportImporterAggregatesZipDeduplicatesMatchesAndCapturesLatestTimestamp()
    {
        var tempZip = Path.Combine(Path.GetTempPath(), "exo-riot-export-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                WriteZipEntry(archive, "valorant/valorant_matches.json", """
                    {
                      "matches": [
                        { "matchInfo": { "matchId": "val-1", "queueId": "competitive", "gameLengthMillis": 2400000, "gameStartMillis": 1760000000000 } },
                        { "matchInfo": { "matchId": "val-1", "queueId": "competitive", "gameLengthMillis": 2400000, "gameStartMillis": 1760000000000 } },
                        { "matchInfo": { "matchId": "val-2", "queueId": "unrated", "gameLengthMillis": 1800000, "gameStartMillis": 1760100000000 } }
                      ]
                    }
                    """);
                WriteZipEntry(archive, "league/lol_match_history.json", """
                    [
                      { "metadata": { "matchId": "NA1_1" }, "info": { "gameDuration": 1800, "queueId": 420, "gameEndTimestamp": 1760200000000 } },
                      { "metadata": { "matchId": "NA1_2" }, "info": { "gameDuration": 1200000, "queueId": 450 } }
                    ]
                    """);
                WriteZipEntry(archive, "tft/tft_matches.json", """
                    [
                      { "metadata": { "match_id": "NA1_TFT_9" }, "info": { "game_length": 2100, "tft_set_number": 12, "game_datetime": 1760300000000 } }
                    ]
                    """);
                WriteZipEntry(archive, "account/profile.json", """{"gameName":"Player","tagLine":"NA1"}""");
            }

            var imported = RiotDataExportImporter.ImportFromFile(tempZip, TestContext.Current.CancellationToken);

            Assert.True(imported.Ok);
            Assert.Equal(70, imported.Seeds["riot:valorant"].Minutes);
            Assert.False(imported.Seeds["riot:valorant"].Partial);
            Assert.Equal(40, imported.Seeds["riot:valorant"].Modes!["competitive"]);
            Assert.Equal(30, imported.Seeds["riot:valorant"].Modes!["unrated"]);
            Assert.Equal(2, imported.MatchCounts["riot:valorant"]);

            Assert.Equal(50, imported.Seeds["riot:league_of_legends"].Minutes);
            Assert.Equal(30, imported.Seeds["riot:league_of_legends"].Modes!["ranked solo"]);
            Assert.Equal(20, imported.Seeds["riot:league_of_legends"].Modes!["aram"]);
            Assert.Equal(2, imported.MatchCounts["riot:league_of_legends"]);

            Assert.Equal(35, imported.Seeds["riot:teamfighttactics"].Minutes);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760300000000), imported.LatestMatchUtc);
        }
        finally
        {
            if (File.Exists(tempZip)) DeletionGate.DeleteFile(tempZip);
        }
    }

    [Fact]
    public void SeedIfHigherNeverShrinksExistingHigherBaseline()
    {
        var dir = Path.Combine(Path.GetTempPath(), "exo-riot-ledger-" + Guid.NewGuid().ToString("N"));
        var prev = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        try
        {
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, dir);
            RiotLedger.ForgetLoaded();
            var account = RiotLedger.AccountKey("Player#NA1")!;
            var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

            Assert.True(RiotLedger.Seed(account, new Dictionary<string, GameSeed>
            {
                ["riot:valorant"] = new(6000),
                ["riot:league_of_legends"] = new(600),
            }, t0, RiotLedger.ExportSource));

            var wrote = RiotLedger.SeedIfHigher(account, new Dictionary<string, GameSeed>
            {
                ["riot:valorant"] = new(1200, Partial: true),
                ["riot:league_of_legends"] = new(1800, Partial: true),
                ["riot:teamfighttactics"] = new(300, Partial: true),
            }, t0.AddDays(5), RiotLedger.CloudSource);

            Assert.True(wrote);
            Assert.Equal(6000, RiotLedger.Totals(account, "riot:valorant"));
            Assert.Equal(1800, RiotLedger.Totals(account, "riot:league_of_legends"));
            Assert.Equal(300, RiotLedger.Totals(account, "riot:teamfighttactics"));
        }
        finally
        {
            RiotLedger.ForgetLoaded();
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, prev);
            if (Directory.Exists(dir)) DeletionGate.DeleteDirectory(dir, recursive: true);
        }
    }

    private static void WriteZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private sealed class StubHandler(Func<Uri, (HttpStatusCode Status, string Body)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = responder(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

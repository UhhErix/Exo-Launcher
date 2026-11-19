using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways app telemetry could leak or misreport (it is sent by every install, so what it holds is the whole safeguard): an
/// event or prop outside the schema, a personal value that rides in a prop, an oversized batch, a queue that grows without
/// bound, an install ID kept past 90 days or made from a failed random source. Each test uses a temporary data folder. The
/// HTTP send and the delete request are not run here (no network in a unit test); what they send is the batch checked below.
/// </summary>
[Collection("Telemetry")]
public sealed class TelemetryTests : IDisposable
{
    private readonly string? _prior = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-telemetry-" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, CancellationToken, Task<int>> _poster = Telemetry.Poster;
    private readonly Telemetry.FillRandom _random = Telemetry.RandomFill;

    public TelemetryTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
        Telemetry.ForgetQueuedForTests();
    }

    public void Dispose()
    {
        Telemetry.Poster = _poster;
        Telemetry.RandomFill = _random;
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _prior);
        try { DeletionGate.DeleteDirectory(_dir, true); } catch { }
    }

    private string TelemetryDir => Path.Combine(_dir, "telemetry");

    private string BatchEvents() => Telemetry.TakeBatch().Body ?? "";

    [Fact]
    public void ShareAppDataQueuesASchemaEventWithItsProps()
    {

        Telemetry.Track("app.exit", ("session_s", 5L), ("clean", true));

        var body = JsonNode.Parse(BatchEvents())!;
        var envelope = body["events"]![0]!;
        Assert.Equal("launcher", envelope["app"]!.GetValue<string>());
        Assert.Equal("app.exit", envelope["event"]!.GetValue<string>());
        Assert.Equal(5, envelope["props"]!["session_s"]!.GetValue<long>());
        Assert.True(envelope["props"]!["clean"]!.GetValue<bool>());
        Assert.Matches("^[0-9a-f]{16}$", envelope["session_id"]!.GetValue<string>());
    }

    [Fact]
    public void AnUnknownEventOrPropIsDropped()
    {

        Telemetry.Track("made.up", ("x", 1L));
        Telemetry.Track("app.exit", ("session_s", 5L), ("secret_path", "a value"));

        var body = BatchEvents();
        Assert.DoesNotContain("made.up", body);
        Assert.DoesNotContain("secret_path", body);
        Assert.Contains("session_s", body);
    }

    [Fact]
    public void AValueThatLooksPersonalDropsThatPropNotTheEvent()
    {

        // A path in a version prop fails its rule; the event's ok flag is still sent.
        Telemetry.Track("update.result", ("from", @"C:\Users\Erix\Launcher"), ("to", "3.26.0"), ("ok", true), ("error_kind", "none"));

        var body = BatchEvents();
        Assert.DoesNotContain("Erix", body);
        Assert.DoesNotContain("\"from\"", body);
        Assert.Contains("\"to\":\"3.26.0\"", body);
    }

    [Fact]
    public void AnEventWhosePropsAllDropIsNotSent()
    {

        Telemetry.Track("update.result", ("from", @"C:\Users\Erix"), ("to", "https://example.com/x"));

        Assert.Null(Telemetry.TakeBatch().Body);
    }

    [Fact]
    public void ANumberAboveTheCapIsDropped()
    {

        Telemetry.Track("app.exit", ("session_s", 1e16));

        Assert.Null(Telemetry.TakeBatch().Body);
    }

    [Theory]
    [InlineData(0, "under 256")]
    [InlineData(255, "under 256")]
    [InlineData(256, "256-511")]
    [InlineData(1023, "512-1023")]
    [InlineData(1024, "1024-2047")]
    [InlineData(2048, "2048+")]
    public void MemoryIsSentAsABucketNeverAnExactSize(long megabytes, string bucket) =>
        Assert.Equal(bucket, Telemetry.MemoryBucket(megabytes * 1048576));

    [Theory]
    [InlineData(999_999L, "under 1 MB")]
    [InlineData(1_000_000L, "1-99 MB")]
    [InlineData(1_000_000_000L, "1 GB+")]
    public void DownloadSizeIsABucket(long bytes, string bucket) => Assert.Equal(bucket, Telemetry.SizeBucket(bytes));

    [Theory]
    [InlineData(49, "0-49")]
    [InlineData(50, "50-199")]
    [InlineData(1000, "1000+")]
    public void GameCountIsABucket(int games, string bucket) => Assert.Equal(bucket, Telemetry.GameCountBucket(games));

    [Fact]
    public void ABatchHoldsAtMost64KbAndTheRestWaits()
    {
        for (var i = 0; i < 400; i++)
            Telemetry.Track("update.result", ("from", "3.25.0"), ("to", "3.26.0"), ("ok", true), ("error_kind", "none"));

        var first = Telemetry.TakeBatch();
        var second = Telemetry.TakeBatch();

        Assert.NotNull(first.Body);
        Assert.True(Encoding.UTF8.GetByteCount(first.Body!) <= Telemetry.MaxBatchBytes);
        Assert.NotNull(second.Body);
        Assert.Equal(400, first.Taken.Count + second.Taken.Count);
        Assert.True(first.Taken.Count < 400);
    }

    [Fact]
    public void AFullQueueDropsTheOldestAndSaysHowManyWereLost()
    {
        for (var i = 0; i < 4000; i++)
            Telemetry.Track("update.result", ("from", "3.25.0"), ("to", "3.26.0"), ("ok", true), ("error_kind", "none"));

        var batch = Telemetry.TakeBatch();

        Assert.NotNull(batch.Note);
        Assert.True(batch.Count > 0);
        Assert.Contains("\"event\":\"telemetry.dropped\"", batch.Body);
        Assert.Contains($"\"count\":{batch.Count}", batch.Note);
    }

    [Fact]
    public void AnInstallIdIsKeptForNinetyDaysThenReplaced()
    {
        Directory.CreateDirectory(TelemetryDir);
        var kept = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(TelemetryDir, "install.json"), $"{{\"id\":\"{kept}\",\"createdAt\":\"{DateTime.UtcNow.AddDays(-1):o}\"}}");
        Assert.Equal(kept, Telemetry.InstallId());

        var old = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(TelemetryDir, "install.json"), $"{{\"id\":\"{old}\",\"createdAt\":\"{DateTime.UtcNow.AddDays(-91):o}\"}}");
        var rotated = Telemetry.InstallId();
        Assert.NotEqual(old, rotated);
        Assert.True(Guid.TryParse(rotated, out _));
    }

    [Fact]
    public void AnInstallIdThatIsNotAGuidIsReplaced()
    {
        Directory.CreateDirectory(TelemetryDir);
        File.WriteAllText(Path.Combine(TelemetryDir, "install.json"), $"{{\"id\":\"C:\\\\Users\\\\Erix\",\"createdAt\":\"{DateTime.UtcNow:o}\"}}");

        var id = Telemetry.InstallId();

        Assert.True(Guid.TryParse(id, out _));
        Assert.DoesNotContain("Erix", id);
    }

    // ---- The choice changing while events are queued or in flight -------------------------------------------------

    [Fact]
    public async Task OneFlushSendsOneBatchAndLeavesTheRestForTheNextInterval()
    {
        for (var i = 0; i < 400; i++)
            Telemetry.Track("update.result", ("from", "3.25.0"), ("to", "3.26.0"), ("ok", true), ("error_kind", "none"));
        var posts = 0;
        Telemetry.Poster = (_, _) =>
        {
            posts++;
            return Task.FromResult(202);
        };

        await Telemetry.FlushAsync(CancellationToken.None);

        Assert.Equal(1, posts);
        Assert.NotNull(Telemetry.TakeBatch().Body);
    }

    [Fact]
    public async Task AFailedSendOfAnyKindKeepsTheBatchInsteadOfLosingIt()
    {
        Telemetry.Track("app.exit", ("session_s", 5L));
        Telemetry.Poster = (_, _) => throw new InvalidOperationException("the post itself failed");

        await Telemetry.FlushAsync(CancellationToken.None);

        Assert.Contains("\"session_s\":5", BatchEvents());
    }

    [Fact]
    public async Task AnAcceptedBatchIsRecordedInViewWhatsSent()
    {
        Telemetry.Track("app.exit", ("session_s", 7L));
        Telemetry.Poster = (_, _) => Task.FromResult(202);

        await Telemetry.FlushAsync(CancellationToken.None);

        Assert.Contains(Telemetry.Sent(), line => line.Contains("\"session_s\":7", StringComparison.Ordinal));
        Assert.Null(Telemetry.TakeBatch().Body);
    }

    // ---- The random source: an ID is made only from it, and a failure makes no ID and sends nothing ----------------

    [Fact]
    public void WhenTheRandomSourceFailsNoInstallIdIsMadeAndNothingIsQueued()
    {
        Telemetry.RandomFill = _ => throw new System.Security.Cryptography.CryptographicException("no random source");

        Telemetry.Track("app.exit", ("session_s", 5L));

        Assert.Null(Telemetry.InstallId());
        Assert.False(File.Exists(Path.Combine(TelemetryDir, "install.json")));
        Assert.Null(Telemetry.TakeBatch().Body);
    }

    [Fact]
    public void ARandomSourceThatGivesOnlyZerosMakesNoInstallId()
    {
        Telemetry.RandomFill = bytes => bytes.Clear();

        Assert.Null(Telemetry.NewUuid());
    }

    [Fact]
    public void WithoutASessionIdNothingIsQueuedEvenWithAnInstallId()
    {
        Assert.NotNull(Telemetry.InstallId());
        Telemetry.ForgetQueuedForTests();
        Telemetry.RandomFill = _ => throw new System.Security.Cryptography.CryptographicException("no random source");

        Telemetry.Track("app.exit", ("session_s", 5L));

        Assert.Null(Telemetry.TakeBatch().Body);
    }

    [Fact]
    public void AWorkingRandomSourceMakesAVersion4InstallId()
    {
        var id = Telemetry.InstallId();

        Assert.NotNull(id);
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal('4', id![14]);
        Assert.Contains(id[19], "89ab");
    }

    [Fact]
    public void NoSettingChangeIsSentUntilItsValuesAreAllowlisted()
    {
        // No setting.changed event exists in Launcher's schema, so a setting's name and its value (free text in some
        // settings) never leave the PC through a telemetry event.

        Telemetry.Track("setting.changed", ("setting", "notes-title"), ("value", "my private note"));

        Assert.Null(Telemetry.TakeBatch().Body);
    }

    [Fact]
    public void AQueueSavedByTheLastRunIsLoadedAtStart()
    {
        Directory.CreateDirectory(TelemetryDir);
        File.WriteAllText(Path.Combine(TelemetryDir, "queue.jsonl"), "{\"event\":\"app.exit\",\"props\":{\"session_s\":5}}\n");

        Telemetry.LoadQueue();

        Assert.Contains("\"session_s\":5", BatchEvents());
    }
}

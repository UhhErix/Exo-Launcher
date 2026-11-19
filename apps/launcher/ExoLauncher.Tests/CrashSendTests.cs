using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a crash report could go wrong on its way out (every install sends them): a report sent with no install ID (so
/// Delete my data would not reach it), an accepted report missing from View what's sent, or a pending file deleted or
/// counted when its send was blocked and it should wait.
/// </summary>
[Collection("Telemetry")]
public sealed class CrashSendTests : IDisposable
{
    private readonly string? _prior = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-crash-send-" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, CancellationToken, Task<int>> _poster = CrashReport.Poster;
    private readonly Telemetry.FillRandom _random = Telemetry.RandomFill;
    private readonly Action<CrashReport.Payload> _keep = CrashReport.KeepForLater;

    public CrashSendTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
        Telemetry.ForgetQueuedForTests();
    }

    public void Dispose()
    {
        CrashReport.Poster = _poster;
        CrashReport.KeepForLater = _keep;
        Telemetry.RandomFill = _random;
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _prior);
        try { DeletionGate.DeleteDirectory(_dir, true); } catch { }
    }

    [Fact]
    public async Task ACrashReportWithNoInstallIdToCarryIsNotSent()
    {
        // Without an install ID the report could not be deleted later, so it is not sent at all.
        Telemetry.RandomFill = _ => throw new System.Security.Cryptography.CryptographicException("no random source");
        var posted = false;
        CrashReport.Poster = (_, _) =>
        {
            posted = true;
            return Task.FromResult(202);
        };

        var outcome = await CrashReport.SendOnceAsync(Payload(), TestContext.Current.CancellationToken);

        Assert.Equal(SendOutcome.Failed, outcome);
        Assert.False(posted);
        Assert.Empty(Telemetry.Sent());
    }

    [Fact]
    public async Task AnAcceptedCrashReportAppearsInViewWhatsSentWithItsInstallId()
    {
        CrashReport.Poster = (_, _) => Task.FromResult(202);

        var outcome = await CrashReport.SendOnceAsync(Payload(), TestContext.Current.CancellationToken);

        Assert.Equal(SendOutcome.Accepted, outcome);
        var line = Assert.Single(Telemetry.Sent());
        Assert.Contains("\"installId\":", line);
        Assert.Contains("\"errorType\":\"Hang\"", line);
    }

    private static CrashReport.Payload Payload() => new("launcher", "3.25.0", "10.0.26300.0", "Hang", "UI thread stopped responding for 6 s");

    [Fact]
    public async Task AWaitingReportKeepsItsFileAndCountsNothing()
    {
        var store = new PendingReports(Path.Combine(_dir, "pending"), () => new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc),
            () => new PendingReports.Identity("3.25.0", "10.0.26300.0", "Erix", @"C:\Users\Erix", "DESKTOP-K3J9"), () => []);
        store.Write(new InvalidOperationException("boom"));

        var accepted = await store.SendPendingAsync(_ => Task.FromResult(SendOutcome.Blocked));

        Assert.Equal(0, accepted);
        var files = Directory.GetFiles(Path.Combine(_dir, "pending"), "*.json");
        Assert.Single(files);
        Assert.Equal(0, JsonNode.Parse(File.ReadAllText(files[0]))!["sends"]!.GetValue<int>());
    }

    [Fact]
    public void TheInstallIdTravelsOnlyWhenSetAndIsNamedForTheService()
    {
        var without = JsonNode.Parse(CrashReport.ToJson(Payload()))!.AsObject();
        Assert.False(without.ContainsKey("installId"));

        var with = JsonNode.Parse(CrashReport.ToJson(Payload() with { InstallId = "5b0f3c9e-6a2d-4f7e-9c1b-2d8e4a7f0b13" }))!.AsObject();
        Assert.Equal("5b0f3c9e-6a2d-4f7e-9c1b-2d8e4a7f0b13", with["installId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(413, false)]
    [InlineData(422, false)]
    public async Task ALiveReportTheServiceRefusedForLoadIsKeptForTheNextStartAndOneItWillNeverTakeIsNot(int status, bool kept)
    {
        CrashReport.Poster = (_, _) => Task.FromResult(status);
        var keptReports = new List<CrashReport.Payload>();
        CrashReport.KeepForLater = keptReports.Add;

        var outcome = await CrashReport.SendOnceAsync(Payload(), TestContext.Current.CancellationToken);

        Assert.Equal(SendOutcome.Failed, outcome);
        Assert.Equal(kept ? 1 : 0, keptReports.Count);
        if (kept) Assert.Equal("Hang", keptReports[0].ErrorType);
    }

    [Fact]
    public async Task AReportWithNoAnswerIsKeptAndAPendingOneSentAtStartIsNotKeptTwice()
    {
        var keptReports = new List<CrashReport.Payload>();
        CrashReport.KeepForLater = keptReports.Add;
        CrashReport.Poster = (_, _) => throw new HttpRequestException("offline");
        await CrashReport.SendOnceAsync(Payload() with { ErrorType = "WebView2Crash" }, TestContext.Current.CancellationToken);
        Assert.Single(keptReports);

        CrashReport.Poster = (_, _) => Task.FromResult(429);
        await CrashReport.SendOnceAsync(Payload() with { ErrorType = "UpdateFailure" }, TestContext.Current.CancellationToken, pending: true);
        Assert.Single(keptReports);
    }

    [Fact]
    public void KeptReportsAreBoundedOldestDroppedAndCarryNoInstallId()
    {
        var at = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
        var store = new PendingReports(Path.Combine(_dir, "kept"), () => at = at.AddSeconds(1),
            () => new PendingReports.Identity("3.25.0", "10.0.26300.0", "Erix", @"C:\Users\Erix", "DESKTOP-K3J9"), () => []);
        for (var i = 0; i < PendingReports.MaxPending + 3; i++)
            store.Add(Payload() with { ErrorType = "Kept" + i, InstallId = "5b0f3c9e-6a2d-4f7e-9c1b-2d8e4a7f0b13" });

        var files = Directory.GetFiles(Path.Combine(_dir, "kept"), "*.json").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(PendingReports.MaxPending, files.Length);
        var first = JsonNode.Parse(File.ReadAllText(files[0]))!.AsObject();
        Assert.Equal("Kept3", first["errorType"]!.GetValue<string>());
        Assert.False(first.ContainsKey("installId"));
        Assert.Equal(0, first["sends"]!.GetValue<int>());
    }
}
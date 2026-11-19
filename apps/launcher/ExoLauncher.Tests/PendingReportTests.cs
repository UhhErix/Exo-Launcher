using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a crash report could be lost, leak or pile up: a pending file that holds raw text (a path in the exception's
/// message), a crash that is sent twice, a send that is retried forever, a failed POST that is dropped at once, a folder
/// that grows without bound, a corrupt file that stops the start. Each test uses a temporary folder, a fixed clock and a
/// fixed identity, never the real AppDataDir.
/// </summary>
public sealed class PendingReportTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 5, 19, 50, 0, DateTimeKind.Utc);
    private static readonly PendingReports.Identity Id = new("3.24.1", "10.0.26300.0", "Erix", @"C:\Users\Erix", "DESKTOP-K3J9");
    private static readonly string[] Log =
    [
        @"[2026-11-05T19:42:10.1230000Z] WARN Launch: started C:\Users\Erix\Games\Elden Ring\eldenring.exe for Erix.",
        "[2026-11-05T19:42:11.0010000Z] INFO Library: fetched https://api.example.com/v1/library?token=abc123XYZ",
        "",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-pending-" + Guid.NewGuid().ToString("N"));

    public PendingReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>A store over the temp folder; a new one each call, as a new start would make.</summary>
    private PendingReports Store() => new(_dir, () => Now, () => Id, () => Log);

    private string[] Files() => Directory.GetFiles(_dir, "*.json");

    private int SendsOfFirstFile() => JsonNode.Parse(File.ReadAllText(Files()[0]))!["sends"]!.GetValue<int>();

    private static Task<SendOutcome> Accept(CrashReport.Payload _) => Task.FromResult(SendOutcome.Accepted);

    private static Task<SendOutcome> Fail(CrashReport.Payload _) => Task.FromResult(SendOutcome.Failed);

    [Fact]
    public void AThrowOnTheDispatcherWritesOneFileHoldingOnlyTheStrippedFields()
    {
        Store().Write(new InvalidOperationException(@"Could not read C:\Users\Erix\Games\Elden Ring\eldenring.exe"));

        var files = Files();
        Assert.Single(files);
        var text = File.ReadAllText(files[0]);
        var json = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(new[] { "app", "errorType", "excerpt", "osBuild", "sends", "version" },
            json.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.Equal("launcher", json["app"]!.GetValue<string>());
        Assert.Equal("InvalidOperationException", json["errorType"]!.GetValue<string>());
        Assert.Equal(0, json["sends"]!.GetValue<int>());
        Assert.DoesNotContain("Erix", text);
        Assert.DoesNotContain(@"C:\", text);
        Assert.DoesNotContain("eldenring", text);
        Assert.DoesNotContain("abc123XYZ", text);
    }

    [Fact]
    public async Task TheNextStartSendsItOnceAndTheFileGoes()
    {
        Store().Write(new InvalidOperationException("boom"));

        var sent = 0;
        var accepted = await Store().SendPendingAsync(payload =>
        {
            sent++;
            return Accept(payload);
        });

        Assert.Equal(1, sent);
        Assert.Equal(1, accepted);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task ASecondStartSendsNothing()
    {
        Store().Write(new InvalidOperationException("boom"));
        await Store().SendPendingAsync(Accept);

        var sent = 0;
        await Store().SendPendingAsync(payload =>
        {
            sent++;
            return Accept(payload);
        });

        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task AFailedSendKeepsTheFileAndTheThirdFailureDeletesIt()
    {
        Store().Write(new InvalidOperationException("boom"));

        await Store().SendPendingAsync(Fail);
        Assert.Single(Files());
        Assert.Equal(1, SendsOfFirstFile());

        await Store().SendPendingAsync(Fail);
        Assert.Single(Files());
        Assert.Equal(2, SendsOfFirstFile());

        await Store().SendPendingAsync(Fail);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task ASenderThatThrowsCountsAsAFailedSend()
    {
        Store().Write(new InvalidOperationException("boom"));

        await Store().SendPendingAsync(_ => throw new HttpRequestException("offline"));

        Assert.Single(Files());
        Assert.Equal(1, SendsOfFirstFile());
    }

    [Fact]
    public async Task ARefusedReportIsDeletedWithoutCountingAsAFailure()
    {
        Store().Write(new InvalidOperationException("boom"));

        await Store().SendPendingAsync(_ => Task.FromResult(SendOutcome.Refused));

        Assert.Empty(Files());
    }

    [Fact]
    public void MoreThanFivePendingFilesDropsTheOldest()
    {
        // One clock reading for all six: the order must come from the counter in the name, not the time.
        var store = Store();
        for (var i = 0; i < 6; i++) store.Write(new InvalidOperationException("failure number " + i));

        Assert.Equal(PendingReports.MaxPending, Files().Length);
    }

    [Fact]
    public async Task TheOldestSurvivorIsSentFirstAndTheDroppedOneIsGone()
    {
        var store = Store();
        for (var i = 0; i < 6; i++) store.Write(new InvalidOperationException("failure number " + i));

        var excerpts = new List<string>();
        await Store().SendPendingAsync(payload =>
        {
            excerpts.Add(payload.Excerpt);
            return Accept(payload);
        });

        // One send per run: the oldest survivor, and the other four wait for later starts.
        Assert.Single(excerpts);
        Assert.DoesNotContain(excerpts, excerpt => excerpt.Contains("failure number 0", StringComparison.Ordinal));
        Assert.Contains("failure number 1", excerpts[0]);
        Assert.Equal(PendingReports.MaxPending - 1, Files().Length);
    }

    [Fact]
    public async Task ABlockedSendKeepsTheFileAndCountsNothing()
    {
        Store().Write(new InvalidOperationException("boom"));

        var accepted = await Store().SendPendingAsync(_ => Task.FromResult(SendOutcome.Blocked));

        Assert.Equal(0, accepted);
        Assert.Single(Files());
        Assert.Equal(0, SendsOfFirstFile());
    }

    [Fact]
    public async Task ADisposableHostWithoutTheSmokeAllowanceSendsNothingAndKeepsTheFile()
    {
        // ForceForTest makes this a disposable host; the real sender must answer Blocked before any POST.
        Store().Write(new InvalidOperationException("boom"));
        var saved = Environment.GetEnvironmentVariable("EXO_REPORT_SMOKE");
        try
        {
            Environment.SetEnvironmentVariable("EXO_REPORT_SMOKE", null);
            using var forced = HostMode.ForceForTest(true);
            Assert.True(HostMode.IsDisposable);

            var accepted = await Store().SendPendingAsync(payload => CrashReport.SendOnceAsync(payload, pending: true));

            Assert.Equal(0, accepted);
            Assert.Single(Files());
            Assert.Equal(0, SendsOfFirstFile());
        }
        finally { Environment.SetEnvironmentVariable("EXO_REPORT_SMOKE", saved); }
    }

    [Fact]
    public void ADisposableHostSendsOnlyWithTheSmokeAllowance()
    {
        // The send itself is not run here (no POST in a unit test); the gate decides whether it may leave.
        Assert.False(CrashReport.MayLeave(disposable: true, smokeAllowsSends: false, errorType: "SmokeTestHang"));
        Assert.True(CrashReport.MayLeave(disposable: true, smokeAllowsSends: true, errorType: "SmokeTestHang"));
        Assert.False(CrashReport.MayLeave(disposable: true, smokeAllowsSends: true, errorType: "Hang"));
        Assert.False(CrashReport.MayLeave(disposable: true, smokeAllowsSends: true, errorType: "DiscordGateway"));
        Assert.True(CrashReport.MayLeave(disposable: false, smokeAllowsSends: false, errorType: "Hang"));
    }

    [Fact]
    public async Task ACorruptFileIsDeletedWithoutThrowingOrBeingSent()
    {
        File.WriteAllText(Path.Combine(_dir, "0000000000000000001-000001.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "0000000000000000002-000002.json"), "{\"app\":\"launcher\"}");

        var sent = 0;
        var accepted = await Store().SendPendingAsync(payload =>
        {
            sent++;
            return Accept(payload);
        });

        Assert.Equal(0, sent);
        Assert.Equal(0, accepted);
        Assert.Empty(Files());
    }
}

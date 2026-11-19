using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the crash report limits could fail: the same error sent twice in a day; the daily cap not holding; a count that
/// does not reset at the UTC day; a failed POST that keeps its reservation; a corrupt state file that lets sends through;
/// a state file that holds a path, an account or excerpt text. The pure record is tested directly, the file through
/// CrashReportLimiter with a temporary folder.
/// </summary>
public sealed class CrashReportLimiterTests : IDisposable
{
    private static readonly DateTime Day1 = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day1Late = new(2026, 11, 5, 23, 59, 59, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 11, 6, 0, 0, 1, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-limit-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "crash-report-limit.json");

    public CrashReportLimiterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_dir, recursive: true); } catch { /* temporary folder */ }
    }

    [Fact]
    public void SameErrorTwiceInOneDaySendsOnce()
    {
        var limit = CrashReportLimit.Empty(Day1);

        Assert.True(limit.TryReserve("SteamGameException", "Could not start Elden Ring", Day1, out _));
        Assert.False(limit.TryReserve("SteamGameException", "Could not start Elden Ring", Day1Late, out _));
        Assert.Equal(1, limit.Count);
    }

    [Fact]
    public void DifferentSignatureOfTheSameTypeSends()
    {
        var limit = CrashReportLimit.Empty(Day1);

        Assert.True(limit.TryReserve("AppError", "Could not start Elden Ring", Day1, out _));
        Assert.True(limit.TryReserve("AppError", "Could not open the store", Day1, out _));
        Assert.Equal(2, limit.Count);
    }

    [Fact]
    public void SixthDistinctReportInAUtcDayIsRefused()
    {
        var limit = CrashReportLimit.Empty(Day1);
        for (var i = 0; i < CrashReportLimit.MaxPerDay; i++)
            Assert.True(limit.TryReserve("AppError", $"error {i}", Day1, out _));

        Assert.False(limit.TryReserve("AppError", "error 5", Day1Late, out _));
        Assert.Equal(CrashReportLimit.MaxPerDay, limit.Count);
    }

    [Fact]
    public void CountResetsAtTheNextUtcDay()
    {
        var limit = CrashReportLimit.Empty(Day1);
        for (var i = 0; i < CrashReportLimit.MaxPerDay; i++)
            limit.TryReserve("AppError", $"error {i}", Day1, out _);

        // Just after UTC midnight the day is new: the cap and the same error's key both start again.
        Assert.True(limit.TryReserve("AppError", "error 0", Day2, out _));
        Assert.Equal(1, limit.Count);
        Assert.Equal("2026-11-06", limit.Day);
    }

    [Fact]
    public void ReleasedReservationDoesNotCountAgainstTheDay()
    {
        var limit = CrashReportLimit.Empty(Day1);
        Assert.True(limit.TryReserve("AppError", "error 0", Day1, out var key));
        for (var i = 1; i < CrashReportLimit.MaxPerDay; i++)
            limit.TryReserve("AppError", $"error {i}", Day1, out _);

        limit.Release(key);

        Assert.True(limit.TryReserve("AppError", "error 0", Day1, out _));
        Assert.False(limit.TryReserve("AppError", "error 6", Day1, out _));
    }

    [Fact]
    public void SignatureIsTheErrorLineNotTheHeaderOrATimestamp()
    {
        const string excerpt = "Exo Launcher 3.24.1 - Windows 10.0.26300.0 - 2026-11-05 19:50 UTC\n"
            + "Error (library): Could not start Elden Ring\n\n"
            + "Exo's log (1 entries):\n"
            + "[2026-11-05T19:42:10.4560000Z] ERROR Library: refused.";

        var signature = CrashReportLimit.SignatureOf(excerpt);
        Assert.Equal("Error (library): Could not start Elden Ring", signature);
        // The same error a few minutes later has the same header minute change, but the same signature.
        Assert.Equal(signature, CrashReportLimit.SignatureOf(excerpt.Replace("19:50", "20:17")));
    }

    [Fact]
    public void MissingStateFileIsAnEmptyDay()
    {
        Assert.False(File.Exists(StatePath));

        Assert.True(CrashReportLimiter.TryReserve("AppError", "first", Day1, StatePath, out _));
        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public void FailedPostReleasesItsReservation()
    {
        Assert.True(CrashReportLimiter.TryReserve("AppError", "boom", Day1, StatePath, out var key));
        Assert.False(CrashReportLimiter.TryReserve("AppError", "boom", Day1, StatePath, out _));

        // The POST answered something other than 202: the same error may be sent again today.
        CrashReportLimiter.Release(key, Day1, StatePath);

        Assert.True(CrashReportLimiter.TryReserve("AppError", "boom", Day1, StatePath, out _));
    }

    [Fact]
    public void CorruptStateFileRefusesThatDayAndTheNextDaySendsAgain()
    {
        File.WriteAllText(StatePath, "{ not json");

        Assert.False(CrashReportLimiter.TryReserve("AppError", "first", Day1, StatePath, out _));
        Assert.False(CrashReportLimiter.TryReserve("AppError", "second", Day1Late, StatePath, out _));
        Assert.True(CrashReportLimiter.TryReserve("AppError", "first", Day2, StatePath, out _));
    }

    [Fact]
    public void StateFileHoldsNoIdentifyingText()
    {
        const string excerptLine = @"Could not read C:\Users\Erix\Games\Elden Ring\eldenring.exe for Erix at https://example.com/x?token=abc";
        Assert.True(CrashReportLimiter.TryReserve("SteamGameException", excerptLine, Day1, StatePath, out _));
        Assert.True(CrashReportLimiter.TryReserve("AppError", @"Failed in C:\Users\Erix\Code\exo", Day1, StatePath, out _));

        var text = File.ReadAllText(StatePath);

        foreach (var forbidden in new[] { @"C:\", "Users", "Erix", "Elden", "Games", "example.com", "token", "Ring" })
            Assert.DoesNotContain(forbidden, text);
        Assert.Matches(@"""SteamGameException\|[0-9a-f]{16}""", text);
    }
}

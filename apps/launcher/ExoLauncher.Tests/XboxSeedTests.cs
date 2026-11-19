using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Xbox hours seed (reported-hours.json, converted once into playtime.json) can go wrong:
/// - the seed is added on top of Exo's own minutes it already counts, so a session is counted twice;
/// - a session Exo measures after the conversion is lost, or the seed is replaced by a smaller figure;
/// - the file is deleted before its hours reach playtime.json, or it stays after they do and is read again;
/// - a file that cannot be read is deleted and its hours lost;
/// - converting again on the next start adds the hours a second time.
/// Titles and ids are fixtures that match no real Xbox title.
/// </summary>
public sealed class XboxSeedTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-xbox-seed-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public XboxSeedTests()
    {
        Directory.CreateDirectory(_folder);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _folder);
        PlaytimeService.ForgetLoaded();
    }

    public void Dispose()
    {
        PlaytimeService.ForgetLoaded();
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch { /* temp */ }
    }

    private const string GameId = "xbox:exo-fixture-forza";
    private const string Title = "Exo Xbox Fixture Qx";

    private string ReportedFile => Path.Combine(_folder, "reported-hours.json");

    private void WriteReportedFile(int minutes) => File.WriteAllText(ReportedFile, $$"""
        {"xboxAt":"2026-10-01T00:00:00Z","xbox":[{"titleId":"1001","name":"{{Title}}","minutes":{{minutes}},"lastPlayedUtc":"2026-09-30T20:00:00Z"}]}
        """);

    private static GameEntry Xbox() => new() { Id = GameId, Title = Title, Store = StoreKind.Xbox };

    private static int Minutes() => Assert.Single(PlaytimeService.Enrich([Xbox()])).PlaytimeMinutes ?? 0;

    [Fact]
    public void TheFileBecomesASeedAndIsRemoved()
    {
        WriteReportedFile(600);

        Assert.Equal(600, Minutes());
        Assert.False(File.Exists(ReportedFile));
        Assert.Contains("1001", File.ReadAllText(Path.Combine(_folder, "playtime.json")));
    }

    [Fact]
    public void ExoSessionsAddOnTopOfTheSeedOnce()
    {
        WriteReportedFile(600);
        Assert.Equal(600, Minutes());

        // A 30 minute session after the conversion adds to the seed; an earlier Enrich never counts it twice.
        PlaytimeService.BeginSession(GameId);
        PlaytimeService.EndSession(GameId, DateTimeOffset.UtcNow.AddMinutes(30));

        Assert.Equal(630, Minutes());
        Assert.Equal(630, Minutes());
    }

    [Fact]
    public void ConvertingAgainOnTheNextStartAddsNothing()
    {
        WriteReportedFile(600);
        Assert.Equal(600, Minutes());
        PlaytimeService.BeginSession(GameId);
        PlaytimeService.EndSession(GameId, DateTimeOffset.UtcNow.AddMinutes(30));

        // A restart with the same file back (its removal failed, or a copy came back): nothing is added again.
        WriteReportedFile(600);
        PlaytimeService.ForgetLoaded();

        Assert.Equal(630, Minutes());
        Assert.False(File.Exists(ReportedFile));
    }

    [Fact]
    public void AnUnreadableFileIsKeptForAnotherTry()
    {
        File.WriteAllText(ReportedFile, "{ not json");

        _ = Minutes();

        Assert.True(File.Exists(ReportedFile));
    }
}

using ExoLauncher.Models;
using ExoLauncher.Services.Achievements;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the local Steam achievement list can show the wrong words: an api name shown where Steam has a display name,
/// a locked hidden achievement's text revealed, a name Steam already gave overwritten, a torn or foreign schema file
/// applied. Fixture: UserGameStatsSchema_1110910.bin, copied unchanged from this PC's appcache\stats (public game
/// metadata; checked for personal strings).
/// </summary>
public sealed class SteamAchievementSchemaNamesTests
{
    private const string AppId = "1110910";

    private static readonly byte[] Schema = File.ReadAllBytes(
        Repo.PathOf("ExoLauncher.Tests", "Fixtures", "Steam", "UserGameStatsSchema_1110910.bin"));

    private static AchievementSnapshot Snapshot(params AchievementEntry[] entries) => new()
    {
        ProviderId = "steam",
        SourceGameId = AppId,
        CoverageKey = "steam:test",
        Coverage = AchievementCoverageStatus.Complete,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        Entries = entries,
    };

    private static AchievementEntry Entry(string externalId, string name, string description = "", bool hidden = false, bool unlocked = false) => new()
    {
        Definition = new AchievementDefinition
        {
            ProviderId = "steam",
            SourceGameId = AppId,
            ExternalId = externalId,
            Name = name,
            Description = description,
            Hidden = hidden,
        },
        State = new AchievementState { ExternalId = externalId, Unlocked = unlocked, ObservedAtUtc = DateTimeOffset.UtcNow },
    };

    [Fact]
    public void AnApiNameIsReplacedByTheSchemasDisplayNameAndDescription()
    {
        var result = SteamLibraryCacheAchievementProvider.WithSchemaNames(
            Snapshot(Entry("ForeverAlone", "ForeverAlone")), Schema, AppId, "english");

        var row = Assert.Single(result.Entries).Definition;
        Assert.Equal("Forever Alone", row.Name);
        Assert.Equal("Beat the game without ever bonding with a shell", row.Description);
    }

    [Fact]
    public void ANameSteamAlreadyGaveIsKept()
    {
        var result = SteamLibraryCacheAchievementProvider.WithSchemaNames(
            Snapshot(Entry("ForeverAlone", "Lone Wolf", "Their words")), Schema, AppId, "english");

        var row = Assert.Single(result.Entries).Definition;
        Assert.Equal("Lone Wolf", row.Name);
        Assert.Equal("Their words", row.Description);
    }

    [Fact]
    public void AHiddenLockedAchievementStaysHidden()
    {
        var result = SteamLibraryCacheAchievementProvider.WithSchemaNames(
            Snapshot(Entry("ForeverAlone", "Hidden achievement", "", hidden: true, unlocked: false)), Schema, AppId, "english");

        var row = Assert.Single(result.Entries).Definition;
        Assert.Equal("Hidden achievement", row.Name);
        Assert.Equal(string.Empty, row.Description);
    }

    [Fact]
    public void AnAchievementTheSchemaDoesNotNameIsLeftAlone()
    {
        var result = SteamLibraryCacheAchievementProvider.WithSchemaNames(
            Snapshot(Entry("NOT_IN_SCHEMA", "NOT_IN_SCHEMA")), Schema, AppId, "english");

        Assert.Equal("NOT_IN_SCHEMA", Assert.Single(result.Entries).Definition.Name);
    }

    [Fact]
    public void AnUnreadableOrForeignSchemaChangesNothing()
    {
        var snapshot = Snapshot(Entry("ForeverAlone", "ForeverAlone"));

        Assert.Same(snapshot, SteamLibraryCacheAchievementProvider.WithSchemaNames(snapshot, [1, 2, 3], AppId, "english"));
        Assert.Same(snapshot, SteamLibraryCacheAchievementProvider.WithSchemaNames(snapshot, Schema, "999999", "english"));
    }

    [Fact]
    public void EveryAchievementInThisPcsSchemaHasAName()
    {
        var names = SteamStatsCache.Schema(Schema, AppId, "english");

        Assert.NotNull(names);
        Assert.All(names!.Values, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Name)));
        Assert.True(names.Count > 0);
    }
}

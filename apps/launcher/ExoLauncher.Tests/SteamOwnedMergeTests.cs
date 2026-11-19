using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// The owned list from the account's sign-in and from the public profile, merged: a game counts once, its hours are the
/// larger of the two (never summed, so a game in both lists doesn't double), and a source that didn't answer adds nothing
/// and never empties the others.
/// </summary>
public sealed class SteamOwnedMergeTests
{
    private static SteamCommunityGames.OwnedGamesResult List(
        IEnumerable<string> ids, IDictionary<string, int>? minutes = null, IDictionary<string, string>? names = null) =>
        new(true, ids.ToHashSet(StringComparer.Ordinal),
            minutes is null ? new Dictionary<string, int>(StringComparer.Ordinal) : new Dictionary<string, int>(minutes, StringComparer.Ordinal),
            names is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(names, StringComparer.Ordinal));

    [Fact]
    public void AGameInBothListsCountsOnceWithTheLargerHours()
    {
        var account = List(["10", "20"], minutes: new Dictionary<string, int> { ["10"] = 300 });
        var profile = List(["10", "30"], minutes: new Dictionary<string, int> { ["10"] = 120, ["30"] = 45 });

        var merged = SteamCommunityGames.Merge(account, profile);

        Assert.Equal(["10", "20", "30"], merged.AppIds.Order(StringComparer.Ordinal));
        Assert.Equal(300, merged.PlaytimeMinutes["10"]);
        Assert.Equal(45, merged.PlaytimeMinutes["30"]);
        Assert.False(merged.PlaytimeMinutes.ContainsKey("20"));
    }

    [Fact]
    public void TheFirstNonEmptyNameWins()
    {
        var account = List(["10"], names: new Dictionary<string, string> { ["10"] = "" });
        var profile = List(["10"], names: new Dictionary<string, string> { ["10"] = "Counter-Strike" });

        Assert.Equal("Counter-Strike", SteamCommunityGames.Merge(account, profile).Names["10"]);
    }

    [Fact]
    public void AnUnavailableSourceAddsNothingAndDoesNotEmptyTheOthers()
    {
        var account = List(["10"]);

        var merged = SteamCommunityGames.Merge(SteamCommunityGames.OwnedGamesResult.Unavailable, account, null);

        Assert.True(merged.Authoritative);
        Assert.Equal(["10"], merged.AppIds);
    }

    [Fact]
    public void NoAnsweringSourceIsUnavailableNotEmpty()
    {
        var merged = SteamCommunityGames.Merge(SteamCommunityGames.OwnedGamesResult.Unavailable, null);

        Assert.False(merged.Authoritative);
    }

    [Fact]
    public void MergingTheSameListTwiceChangesNothing()
    {
        var once = List(["10", "20"], minutes: new Dictionary<string, int> { ["20"] = 9 });

        var twice = SteamCommunityGames.Merge(once, once);

        Assert.Equal(once.AppIds.Order(StringComparer.Ordinal), twice.AppIds.Order(StringComparer.Ordinal));
        Assert.Equal(9, twice.PlaytimeMinutes["20"]);
    }
}

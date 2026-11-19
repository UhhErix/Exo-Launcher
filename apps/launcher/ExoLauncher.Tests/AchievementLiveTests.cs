using ExoLauncher.Models;
using ExoLauncher.Services.Achievements;

namespace ExoLauncher.Tests;

/// <summary>
/// Live Epic and GOG unlocks (two reads of the account's achievements) and the OBS browser source's routing.
/// Ways it could fail: the first read of a session bannering everything already unlocked; a row a partial catalog left
/// out of the earlier read looking new; an account switch mid-game bannering the other account's unlocks; an unusable
/// read (Epic or GOG down) making the next good one look all new; a hidden achievement, renamed when it unlocks, missed;
/// the loopback page answering a request for another host (DNS rebinding) or a path it doesn't serve.
/// </summary>
public sealed class AchievementLiveTests
{
    private static AchievementEntry Row(string id, bool unlocked, string? name = null, DateTimeOffset? at = null) => new()
    {
        Definition = new AchievementDefinition { ProviderId = "gog", SourceGameId = "1", ExternalId = id, Name = name ?? id },
        State = new AchievementState { ExternalId = id, Unlocked = unlocked, UnlockedAtUtc = unlocked ? at : null },
    };

    private static AchievementSnapshot Read(string account, AchievementCoverageStatus coverage, params AchievementEntry[] rows) => new()
    {
        ProviderId = "gog", SourceGameId = "1", CoverageKey = account, Coverage = coverage, Entries = rows,
    };

    [Fact]
    public void The_first_read_is_the_baseline_not_a_banner() =>
        Assert.Empty(AchievementWatch.NewlyUnlocked(null, Read("a", AchievementCoverageStatus.Complete, Row("x", true))));

    [Fact]
    public void Only_rows_that_were_locked_before_are_new_oldest_first()
    {
        var before = Read("a", AchievementCoverageStatus.Complete, Row("old", true), Row("b", false), Row("c", false), Row("d", false));
        var now = Read("a", AchievementCoverageStatus.Complete, Row("old", true), Row("b", true, at: DateTimeOffset.FromUnixTimeSeconds(200)),
            Row("c", true, at: DateTimeOffset.FromUnixTimeSeconds(100)), Row("d", false));
        Assert.Equal(["c", "b"], AchievementWatch.NewlyUnlocked(before, now).Select(e => e.Definition.ExternalId));
    }

    [Fact]
    public void A_row_the_earlier_partial_read_lacked_is_not_new()
    {
        var before = Read("a", AchievementCoverageStatus.Partial, Row("b", false));
        var now = Read("a", AchievementCoverageStatus.Complete, Row("b", false), Row("z", true));
        Assert.Empty(AchievementWatch.NewlyUnlocked(before, now));
    }

    [Fact]
    public void A_hidden_achievement_shows_by_its_real_name_once_unlocked()
    {
        var before = Read("a", AchievementCoverageStatus.Complete, Row("h", false, "Hidden achievement"));
        var now = Read("a", AchievementCoverageStatus.Complete, Row("h", true, "The Secret Room"));
        Assert.Equal("The Secret Room", Assert.Single(AchievementWatch.NewlyUnlocked(before, now)).Definition.Name);
    }

    [Theory]
    [InlineData("b", AchievementCoverageStatus.Complete)]
    [InlineData("a", AchievementCoverageStatus.Unavailable)]
    public void Another_account_or_an_unusable_read_is_never_a_banner(string account, AchievementCoverageStatus coverage)
    {
        var before = Read("a", AchievementCoverageStatus.Complete, Row("b", false));
        Assert.Empty(AchievementWatch.NewlyUnlocked(before, Read(account, coverage, Row("b", true))));
        Assert.Empty(AchievementWatch.NewlyUnlocked(Read("a", AchievementCoverageStatus.Unavailable), Read("a", AchievementCoverageStatus.Complete, Row("b", true))));
    }

    [Theory]
    [InlineData("GET /achievements HTTP/1.1\r\nHost: 127.0.0.1:9364", "Page")]
    [InlineData("GET /achievements?sound=1 HTTP/1.1\r\nhost: localhost:9364\r\nUser-Agent: OBS", "Page")]
    [InlineData("GET /achievements/events HTTP/1.1\r\nHost: 127.0.0.1:9364", "Events")]
    [InlineData("GET /Goeist-Variable.woff2 HTTP/1.1\r\nHost: 127.0.0.1:9364", "Asset")]
    [InlineData("GET /sounds/common.wav HTTP/1.1\r\nHost: 127.0.0.1:9364", "Asset")]
    [InlineData("GET /sounds/rare.wav HTTP/1.1\r\nHost: localhost:9364", "Asset")]
    [InlineData("GET /sounds/completion.wav HTTP/1.1\r\nHost: 127.0.0.1:9364", "Asset")]
    [InlineData("GET /sounds/other.wav HTTP/1.1\r\nHost: 127.0.0.1:9364", "NotFound")]
    [InlineData("GET /sounds/rare.wav HTTP/1.1\r\nHost: evil.example:9364", "Refused")]
    [InlineData("GET /sounds/../../settings.json HTTP/1.1\r\nHost: 127.0.0.1:9364", "NotFound")]
    [InlineData("GET /sample-icon.png HTTP/1.1\r\nHost: 127.0.0.1:9364", "NotFound")]
    [InlineData("GET /achievements HTTP/1.1\r\nHost: evil.example:9364", "Refused")]
    [InlineData("GET /achievements HTTP/1.1\r\nHost: 127.0.0.1", "Refused")]
    [InlineData("GET /achievements HTTP/1.1", "Refused")]
    [InlineData("GET /achievements HTTP/1.1\r\nHost: 127.0.0.1:9364\r\nHost: evil.example", "Refused")]
    [InlineData("POST /achievements HTTP/1.1\r\nHost: 127.0.0.1:9364", "NotFound")]
    [InlineData("GET /banner.html HTTP/1.1\r\nHost: 127.0.0.1:9364", "NotFound")]
    [InlineData("GET /../settings.json HTTP/1.1\r\nHost: 127.0.0.1:9364", "NotFound")]
    [InlineData("nonsense", "NotFound")]
    public void The_loopback_page_answers_only_its_own_paths_on_its_own_host(string head, string route) =>
        Assert.Equal(route, AchievementSource.RouteOf(head).ToString());
}

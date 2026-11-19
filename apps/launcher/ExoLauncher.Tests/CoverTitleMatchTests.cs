using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// CoverArtService.ScoreTitleMatch — decides which Steam app a title-only game gets its cover from. Ways a wrong
/// score makes a wrong-game cover, silently:
/// - a prefix rule on long tokens ("dark" in "darkness") scores a different game 100%, and it clears the accept score;
/// - a sub-title (a sequel or a spin-off with the same first words) scores as a substring and must stay under accept;
/// - an equal title after normalisation (case, punctuation, the space between "Gravity Rush" and "GravityRush") must match.
/// The cloud cover cache (cloud/covers, src/match.js) carries the same table; keep the two in step.
/// </summary>
public sealed class CoverTitleMatchTests
{
    // Callers pass normalised keys (NormalizeTitleKey: lower case, letters and digits, single spaces).
    [Theory]
    [InlineData("portal 2", "portal 2", 100)]
    [InlineData("sid meiers civilization vi", "sid meiers civilization vi", 100)]
    [InlineData("gravity rush", "gravityrush", 100)]
    public void EqualTitlesMatchExactly(string want, string got, int score) =>
        Assert.Equal(score, CoverArtService.ScoreTitleMatch(want, got));

    [Theory]
    [InlineData("dark souls", "darkness souls")]          // a long-token prefix used to score this 100
    [InlineData("metro exodus", "metro 2033")]
    [InlineData("halo infinite", "halo infinity")]
    [InlineData("need for speed heat", "need for speed hot pursuit")]
    [InlineData("war", "warhammer")]
    [InlineData("the witcher", "the witcher 3 wild hunt")]
    [InlineData("tomb raider", "rise of the tomb raider")]
    [InlineData("dying light", "dying light 2 stay human")]
    [InlineData("dying light", "dying light the following")]
    [InlineData("need for speed", "need for speed heat")]
    public void DifferentGamesScoreBelowTheAcceptThreshold(string want, string got)
    {
        var accept = CoverArtService.AcceptSteamTitleScore(want);
        Assert.True(CoverArtService.ScoreTitleMatch(want, got) < accept,
            $"'{want}' must not take '{got}' as its cover");
    }

    [Fact]
    public void AnEditionOfTheSameGameStillMatchesHigh()
    {
        Assert.Equal(92, CoverArtService.ScoreTitleMatch("assassins creed origins", "assassins creed origins gold edition"));
        Assert.Equal(92, CoverArtService.ScoreTitleMatch("assassins creed origins gold edition", "assassins creed origins"));
    }

    [Fact]
    public void ShortTitlesNeedAlmostPerfectMatches()
    {
        Assert.Equal(90, CoverArtService.AcceptSteamTitleScore("doom"));
        Assert.Equal(82, CoverArtService.AcceptSteamTitleScore("doom eternal"));
    }

    [Fact]
    public void EmptyTitlesNeverMatch()
    {
        Assert.Equal(0, CoverArtService.ScoreTitleMatch("", "portal"));
        Assert.Equal(0, CoverArtService.ScoreTitleMatch("portal", ""));
    }
}

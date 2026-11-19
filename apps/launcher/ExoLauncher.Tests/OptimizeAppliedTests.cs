using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Optimize line's Applied can lie:
/// - a misspelt line is skipped as it's written, so the groups that were found all read on and the line says Applied;
/// - a line that's there but won't take Exo's value (an update dropped the choice) counts as found;
/// - a whole group with nothing in the file disappears from the states, so what's left says Applied;
/// - a file that holds none of it reads Applied, or Not optimized with a press that can never help;
/// - the lines a game may simply not hold (an HDR peak) stop it ever saying Applied;
/// - the real profiles, against their real files, don't reach Applied because Exo's own list is off.
/// </summary>
public sealed class OptimizeAppliedTests
{
    private static readonly GameSettings.Pc Rtx = new("nvidia", 5, 8, 1, 165, Gpu: "NVIDIA GeForce RTX 3070");

    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal) { ["A"] = "0 to 4", ["B"] = "0 to 4", ["C"] = "one of Low, High" };

    private static GameSettings.Profile Asking(params GameSettings.Group[] groups) =>
        new("t", "T", "", _ => groups, [], Format: bytes => GameSettings.ParseIni(bytes, "S", Keys));

    private static GameSettings.SettingsFile File(string body) => GameSettings.ParseIni(Encoding.ASCII.GetBytes("[S]\r\n" + body), "S", Keys);

    private static GameSettings.Standing Stands(GameSettings.Profile profile, GameSettings.SettingsFile file)
    {
        List<GameSettings.OpenFile> files = [new("", "", file)];
        return GameSettings.Stands(GameSettings.States(profile, files, new Dictionary<string, GameSettings.Remembered>()), GameSettings.Missing(profile, files).Count);
    }

    [Fact]
    public void AllFoundAndInPlaceIsApplied() =>
        Assert.Equal(GameSettings.Standing.Applied, Stands(Asking(new GameSettings.Group("g", "G", [new("A", "1"), new("B", "2")])), File("A=1\r\nB=2")));

    [Fact]
    public void AMisspeltLineIsNotApplied()
    {
        var profile = Asking(new GameSettings.Group("g", "G", [new("A", "1"), new("B", "2")]));
        var file = File("A=1\r\nBee=2");
        Assert.Equal(["B"], GameSettings.Missing(profile, [new("", "", file)]));
        Assert.Equal(GameSettings.Standing.Partly, Stands(profile, file));
    }

    [Fact]
    public void MostLinesMissingIsNotApplied()
    {
        var profile = Asking(new GameSettings.Group("g", "G", [new("A", "1"), new("B", "2"), new("C", "High"), new("D", "1"), new("E", "1")]));
        Assert.Equal(GameSettings.Standing.Partly, Stands(profile, File("A=1")));
    }

    [Fact]
    public void AGroupWithNothingInTheFileStillCounts()
    {
        var profile = Asking(new GameSettings.Group("g", "G", [new("A", "1")]), new GameSettings.Group("h", "H", [new("D", "1")]));
        Assert.Equal(GameSettings.Standing.Partly, Stands(profile, File("A=1")));
    }

    [Fact]
    public void ALineThatWontTakeTheValueIsMissing()
    {
        var profile = Asking(new GameSettings.Group("g", "G", [new("C", "Ultra")]), new GameSettings.Group("h", "H", [new("A", "1")]));
        var file = File("C=Low\r\nA=1");
        Assert.Equal(["C"], GameSettings.Missing(profile, [new("", "", file)]));
        Assert.Equal(GameSettings.Standing.Partly, Stands(profile, file));
    }

    [Fact]
    public void AFileHoldingNoneOfItSaysSo() =>
        Assert.Equal(GameSettings.Standing.Unfound, Stands(Asking(new GameSettings.Group("g", "G", [new("D", "1")])), File("A=1")));

    [Fact]
    public void NotYetSetIsOffWhateverIsMissing()
    {
        var profile = Asking(new GameSettings.Group("g", "G", [new("A", "1"), new("D", "1")]));
        Assert.Equal(GameSettings.Standing.Off, Stands(profile, File("A=0")));
    }

    [Fact]
    public void ASpareLineThatIsntThereDoesntStopApplied()
    {
        var profile = Asking(new GameSettings.Group("g", "G", [new("A", "1")]), GameSettings.UnrealHdrGroup(1000));
        Assert.Equal(GameSettings.Standing.Applied, Stands(profile, File("A=1")));
    }

    /// <summary>Each of the game-by-game profiles, against the fixture of its own file, finds every line it asks for.</summary>
    [Fact]
    public void TheWrittenProfilesFindAllTheyAskFor()
    {
        (GameSettings.Profile Profile, string[] Fixture)[] profiles =
        [
            (GameSettings.HellIsUs(Rtx), ["HellIsUs", "GameUserSettings.ini"]),
            (GameSettings.PhantomBladeZero(), ["PhantomBladeZero", "GameUserSettings.ini"]),
            (GameSettings.RocketLeague(Rtx), ["RocketLeague", "TASystemSettings.ini"]),
        ];
        foreach (var (profile, fixture) in profiles)
        {
            var bytes = Encoding.ASCII.GetBytes(System.IO.File.ReadAllText(Repo.PathOf(["ExoLauncher.Tests", "Fixtures", .. fixture])).ReplaceLineEndings("\r\n"));
            Assert.Empty(GameSettings.Missing(profile, [new("", "", profile.Format!(bytes))]));
        }
    }
}

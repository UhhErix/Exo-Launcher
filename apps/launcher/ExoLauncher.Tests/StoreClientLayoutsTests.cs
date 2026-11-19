using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Game clients detection can go wrong, on fixture file sets:
/// - the old machine-wide Epic launcher is read as the new one, so the card never asks for the upgrade;
/// - the new per-user launcher is missed, so a finished upgrade keeps asking;
/// - the old launcher left beside the new one is treated as a reason to fix, or is silently ignored (it must warn only);
/// - Steam's Beta is read from the wrong file or a stable Steam reads as Beta, or Steam not installed still asks;
/// - the card shows for a user with neither store, or a fix runs while a game plays.
/// </summary>
public sealed class StoreClientLayoutsTests
{
    private const string LocalAppData = @"C:\Users\Erix\AppData\Local";
    private const string ProgramFiles = @"C:\Program Files";
    private const string ProgramFilesX86 = @"C:\Program Files (x86)";
    private static readonly string NewExe = StoreClientLayouts.NewEpicExe(LocalAppData);
    private static readonly IReadOnlyList<string> OldExes = StoreClientLayouts.OldEpicExes(ProgramFiles, ProgramFilesX86);

    private static StoreClientLayouts.EpicState Epic(IEnumerable<string> files)
    {
        var present = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return StoreClientLayouts.EvaluateEpic(present.Contains, path => present.Contains(path) ? "0.113.0+g2164614" : null, NewExe, OldExes);
    }

    [Fact]
    public void TheNewPerUserLauncherIsNewWithItsVersion()
    {
        var state = Epic([NewExe]);
        Assert.Equal(StoreClientLayouts.EpicLayout.New, state.Layout);
        Assert.Equal("0.113.0+g2164614", state.Version);
        Assert.False(state.OldStillThere);
    }

    [Theory]
    [InlineData("0.112.9")]
    [InlineData("0.99.0")]
    public void ANewPerUserLauncherBelowZeroPointOneOneThreeIsOld(string? version)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NewExe };
        var state = StoreClientLayouts.EvaluateEpic(present.Contains, _ => version, NewExe, OldExes);
        Assert.Equal(StoreClientLayouts.EpicLayout.Old, state.Layout);
        Assert.Equal(version, state.Version);
    }

    [Fact]
    public void ANewLauncherAtZeroPointOneOneThreeIsNew()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NewExe };
        Assert.Equal(StoreClientLayouts.EpicLayout.New, StoreClientLayouts.EvaluateEpic(present.Contains, _ => "0.113.0", NewExe, OldExes).Layout);
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe")]
    public void TheOldMachineWideLauncherIsOld(string oldExe)
    {
        var state = Epic([oldExe]);
        Assert.Equal(StoreClientLayouts.EpicLayout.Old, state.Layout);
        Assert.False(state.OldStillThere);
    }

    [Fact]
    public void NewWinsWhenOldIsLeftBesideItAndOnlyWarns()
    {
        var state = Epic([NewExe, OldExes[0]]);
        Assert.Equal(StoreClientLayouts.EpicLayout.New, state.Layout);
        Assert.True(state.OldStillThere);
        // The leftover is a warning, not a fix: a Steam already on Beta leaves the card off.
        Assert.False(new StoreClientLayouts.Status(state, new(StoreClientLayouts.SteamChannel.Beta, @"C:\Steam")).NeedsFix);
    }

    [Fact]
    public void NoEpicLauncherIsNone()
    {
        var state = Epic([]);
        Assert.Equal(StoreClientLayouts.EpicLayout.None, state.Layout);
        Assert.Null(state.Version);
    }

    [Fact]
    public void SteamIsBetaOnlyWhenPackageBetaNamesTheBeta()
    {
        Assert.Equal(StoreClientLayouts.SteamChannel.Beta, StoreClientLayouts.EvaluateSteam(@"C:\Steam", "publicbeta").Channel);
        Assert.Equal(StoreClientLayouts.SteamChannel.Stable, StoreClientLayouts.EvaluateSteam(@"C:\Steam", null).Channel);
        Assert.Equal(StoreClientLayouts.SteamChannel.Stable, StoreClientLayouts.EvaluateSteam(@"C:\Steam", "steamdeck_publicbeta").Channel);
        Assert.Equal(StoreClientLayouts.SteamChannel.None, StoreClientLayouts.EvaluateSteam(null, null).Channel);
    }

    [Fact]
    public void TheCardIsDueForAnOldEpicOrAStableSteamOnly()
    {
        var beta = new StoreClientLayouts.SteamState(StoreClientLayouts.SteamChannel.Beta, @"C:\Steam");
        var stable = new StoreClientLayouts.SteamState(StoreClientLayouts.SteamChannel.Stable, @"C:\Steam");
        var none = new StoreClientLayouts.SteamState(StoreClientLayouts.SteamChannel.None, null);
        var oldEpic = new StoreClientLayouts.EpicState(StoreClientLayouts.EpicLayout.Old, null, false);
        var newEpic = new StoreClientLayouts.EpicState(StoreClientLayouts.EpicLayout.New, null, false);
        var noEpic = new StoreClientLayouts.EpicState(StoreClientLayouts.EpicLayout.None, null, false);

        Assert.True(new StoreClientLayouts.Status(oldEpic, beta).NeedsFix);
        Assert.True(new StoreClientLayouts.Status(newEpic, stable).NeedsFix);
        Assert.False(new StoreClientLayouts.Status(noEpic, none).NeedsFix); // neither store: no card at all
        Assert.False(new StoreClientLayouts.Status(newEpic, beta).NeedsFix);
        Assert.False(new StoreClientLayouts.Status(noEpic, beta).NeedsFix);
    }

    [Fact]
    public void AnUnreadableSteamBranchIsUnknownWithItsReasonAndNotAFix()
    {
        // package\beta exists but can't be read: the reason is kept for the card, but nothing is confirmed old, so no Fix is due.
        var state = StoreClientLayouts.ReadSteamState(@"C:\Steam", _ => throw new UnauthorizedAccessException("denied"));
        Assert.Equal(StoreClientLayouts.SteamChannel.Unknown, state.Channel);
        Assert.Equal(@"C:\Steam", state.Root);
        Assert.False(string.IsNullOrWhiteSpace(state.Problem));
        var newEpic = new StoreClientLayouts.EpicState(StoreClientLayouts.EpicLayout.New, null, false);
        var status = new StoreClientLayouts.Status(newEpic, state);
        Assert.False(status.NeedsFix);
        Assert.Equal(state.Problem, status.Problem);
    }

    [Fact]
    public void AnUnreadableNewEpicVersionIsUnknownNotOld()
    {
        // The launcher is there but its version can't be read: a read glitch must not download a 220 MB installer.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NewExe };
        var state = StoreClientLayouts.EvaluateEpic(present.Contains, _ => null, NewExe, OldExes);
        Assert.Equal(StoreClientLayouts.EpicLayout.Unknown, state.Layout);
        var status = new StoreClientLayouts.Status(state, new(StoreClientLayouts.SteamChannel.Beta, @"C:\Steam"));
        Assert.False(status.NeedsFix);
        Assert.False(string.IsNullOrWhiteSpace(status.Problem));
    }

    [Fact]
    public void AKnownClientHasNoProblem()
    {
        var status = new StoreClientLayouts.Status(
            new StoreClientLayouts.EpicState(StoreClientLayouts.EpicLayout.New, "0.113.0", false),
            new StoreClientLayouts.SteamState(StoreClientLayouts.SteamChannel.Beta, @"C:\Steam"));
        Assert.Null(status.Problem);
    }

    [Fact]
    public void AReadableSteamBranchReadsAsBefore()
    {
        var beta = StoreClientLayouts.ReadSteamState(@"C:\Steam", _ => "publicbeta");
        Assert.Equal(StoreClientLayouts.SteamChannel.Beta, beta.Channel);
        Assert.Null(beta.Problem);
        Assert.Equal(StoreClientLayouts.SteamChannel.None, StoreClientLayouts.ReadSteamState(null, _ => null).Channel);
    }

    [Fact]
    public void AGameRunningBlocksEveryClientFix()
    {
        Assert.Null(StoreClientLayouts.FixBlockedBy(exoGameRunning: false, steamGameRunning: false));
        Assert.NotNull(StoreClientLayouts.FixBlockedBy(exoGameRunning: true, steamGameRunning: false));
        Assert.NotNull(StoreClientLayouts.FixBlockedBy(exoGameRunning: false, steamGameRunning: true));
    }
}

using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Steam's branch file can be left on Beta when the switch did not finish:
/// - Steam is closed for the switch and the operation is cancelled, leaving package\beta on the new branch;
/// - Steam fails to start on Beta, and the old branch is not put back;
/// - a switch that did finish is undone, or a file that was never there is left behind.
/// </summary>
public sealed class SteamBetaFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-steam-beta-" + Guid.NewGuid().ToString("N"));

    public SteamBetaFileTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "steam.exe"), "");
    }

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, recursive: true); } catch { /* temp */ }
    }

    private string BetaFile => Path.Combine(_root, "package", "beta");

    private void WriteBranch(string branch)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BetaFile)!);
        File.WriteAllText(BetaFile, branch);
    }

    [Fact]
    public async Task ACancelledSwitchPutsTheOldBranchBack()
    {
        WriteBranch("stable");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SteamBetaFile.SwitchAsync(_root, "beta", () => throw new OperationCanceledException()));

        Assert.Equal("stable", File.ReadAllText(BetaFile));
    }

    [Fact]
    public async Task ACancelledSwitchRemovesTheFileItCreated()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SteamBetaFile.SwitchAsync(_root, "beta", () => throw new OperationCanceledException()));

        Assert.False(File.Exists(BetaFile));
    }

    [Fact]
    public async Task ASwitchThatDidNotReachBetaIsUndone()
    {
        WriteBranch("stable");

        var onBeta = await SteamBetaFile.SwitchAsync(_root, "beta", () => Task.FromResult(false));

        Assert.False(onBeta);
        Assert.Equal("stable", File.ReadAllText(BetaFile));
    }

    [Fact]
    public async Task ASwitchThatReachedBetaIsKept()
    {
        WriteBranch("stable");

        var onBeta = await SteamBetaFile.SwitchAsync(_root, "beta", () => Task.FromResult(true));

        Assert.True(onBeta);
        Assert.Equal("beta", File.ReadAllText(BetaFile));
    }
}

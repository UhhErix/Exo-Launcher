using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Lucid's own update is dormant: no release is looked up, offered or downloaded unless Exo holds a SHA-256 pin for it (none is
/// held: AGENTS.md has no GitHub and no keys). Ways this could fail: a look that asks the release list anyway, so a GitHub
/// address is contacted and a release offered without any pin. Every step is a stub that records a call it should not make.
/// </summary>
public sealed class LucidUpdateDormantTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-lucid-dormant-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void NoReleasePinIsHeld()
    {
        Assert.Empty(LucidUpdate.Pins);
    }

    [Fact]
    public async Task WithNoPinTheLookAsksForNoReleaseAndOffersNothing()
    {
        var asked = false;
        var downloaded = false;
        var steps = new LucidUpdateSteps(
            () => DateTime.UtcNow,
            () => true,
            () => false,
            _ => { asked = true; return Task.FromResult<string?>("{}"); },
            (_, _, _) => { downloaded = true; return Task.CompletedTask; },
            _ => throw new InvalidOperationException("No file is inspected."),
            () => null,
            () => false,
            () => Task.FromResult(true),
            _ => false,
            () => Task.FromResult(true),
            () => Task.CompletedTask,
            () => { }) { Pinned = false };
        var store = new LucidStore(Path.Combine(_folder, "lucid-current"), Path.Combine(_folder, "lucid-update.json"));

        var outcome = await LucidUpdate.RunAsync(store, steps, CancellationToken.None);

        Assert.Equal(LucidUpdateOutcome.Off, outcome);
        Assert.False(asked);
        Assert.False(downloaded);
    }
}

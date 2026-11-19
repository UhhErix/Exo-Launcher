using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Minutes played before a Riot ID was known sit in the unassigned ledger until an ID claims them. An ID becomes known
/// on read too (saved by an earlier build, or read from the Riot client), not only when it is saved. Ways this could fail:
/// the minutes stay unassigned until someone saves the ID again; a repeat read moves them twice.
/// Also: a backup entry name with a trailing newline is not a valid cover or ledger name (the regex must end at \z).
/// </summary>
public sealed class RiotIdClaimTests : IDisposable
{
    private const string Valorant = "riot:valorant";
    private const string RiotId = "Player#EUW1";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-riot-claim-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public RiotIdClaimTests()
    {
        Directory.CreateDirectory(_folder);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _folder);
        RiotLedger.ForgetLoaded();
    }

    public void Dispose()
    {
        RiotLedger.ForgetLoaded();
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public void AnIdKnownOnReadClaimsTheMinutesPlayedBeforeIt()
    {
        var start = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        RiotLedger.Begin(RiotLedger.Unassigned, Valorant, start);
        RiotLedger.End(Valorant, start.AddHours(1));
        // Stored without a claim, as an earlier build left it.
        Assert.True(StoreSecretStore.Save(RiotIdStore.FileName, RiotId, RiotIdStore.Normalize));

        Assert.Equal(RiotId, RiotIdStore.TryRead());

        Assert.Equal(60, RiotLedger.Totals(RiotLedger.AccountKey(RiotId)!, Valorant));
    }

    [Fact]
    public void ARepeatReadMovesTheMinutesOnce()
    {
        var start = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        RiotLedger.Begin(RiotLedger.Unassigned, Valorant, start);
        RiotLedger.End(Valorant, start.AddHours(1));
        Assert.True(StoreSecretStore.Save(RiotIdStore.FileName, RiotId, RiotIdStore.Normalize));

        RiotIdStore.TryRead();
        RiotIdStore.TryRead();

        Assert.Equal(60, RiotLedger.Totals(RiotLedger.AccountKey(RiotId)!, Valorant));
    }

    [Fact]
    public void ABackupNameWithATrailingNewlineIsNotACoverOrLedgerName()
    {
        var sha = new string('a', 64);
        Assert.True(LauncherBackup.IsAllowedName("covers/custom-cover-" + sha + ".png"));
        Assert.False(LauncherBackup.IsAllowedName("covers/custom-cover-" + sha + ".png\n"));
        Assert.False(LauncherBackup.IsAllowedName("riot-ledger/" + RiotLedger.Unassigned + ".jsonl\n"));
    }
}

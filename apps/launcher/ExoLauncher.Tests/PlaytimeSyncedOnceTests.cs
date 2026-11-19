using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a synced playtime entry adds its minutes twice. account-sync.json (what says an entry is already here) is not in
/// the backups, so after a restore playtime.json has the minutes but the sync queue does not, and the next pull would
/// credit this PC's own entries again. Also: another PC's entry replayed by a second pull, or after a restart.
/// Fixture game id and times match nothing on this PC.
/// </summary>
public sealed class PlaytimeSyncedOnceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-synced-once-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    private const string Game = "steam:999999931";
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    public PlaytimeSyncedOnceTests()
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

    /// <summary>The minutes playtime.json holds for the game, read from disk so a restart is part of the check.</summary>
    private int ExoMinutes(string game)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(_folder, "playtime.json")))!;
        return root["minutes"]?[game]?.GetValue<int>() ?? 0;
    }

    /// <summary>A playtime entry as a pull hands it over, new to this PC (the sync queue has no copy of it).</summary>
    private static void Pull(string game, DateTimeOffset start, int minutes) =>
        AccountSyncApply.Pulled(
            new AccountSyncHost.PulledItem("playtime", AccountSyncRules.LogEntryId("cccccccccccccccccccccccccccccccc", game + "|" + start.ToString("O")),
                AccountSyncMap.PlaytimeValue(game, start, start.AddMinutes(minutes), minutes), IsNew: true),
            null!);

    [Fact]
    public void AnEntryThisPcPlayedIsNotCreditedAgainAfterARestore()
    {
        // Offline first run: the session is counted here (its entry is queued for the account later).
        PlaytimeService.AdoptSession(Game, Start);
        PlaytimeService.EndSession(Game, Start.AddMinutes(30));
        Assert.Equal(30, ExoMinutes(Game));

        // Restart from a backup, then sign in: the server hands back this PC's own entry.
        PlaytimeService.ForgetLoaded();
        Pull(Game, Start, 30);

        Assert.Equal(30, ExoMinutes(Game));
    }

    [Fact]
    public void AnotherPcsEntryIsCreditedOnceEvenWhenPulledAgain()
    {
        var other = Start.AddDays(1);

        Pull(Game, other, 20);
        PlaytimeService.ForgetLoaded();
        Pull(Game, other, 20);

        Assert.Equal(20, ExoMinutes(Game));
    }

    [Fact]
    public void AnotherPcsEntryAddsToThisPcsOwnMinutes()
    {
        PlaytimeService.AdoptSession(Game, Start);
        PlaytimeService.EndSession(Game, Start.AddMinutes(30));
        PlaytimeService.ForgetLoaded();

        Pull(Game, Start, 30);
        Pull(Game, Start.AddDays(1), 20);

        Assert.Equal(50, ExoMinutes(Game));
    }
}

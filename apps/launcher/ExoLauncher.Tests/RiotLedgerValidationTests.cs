using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// A ledger line a sync pull hands over is written only when it is one Exo would write: schema 1, a play or seed, a riot game,
/// and 1 to 100000 minutes. Without it a line from another PC or the service could add any minutes to a Riot title. Lifetime
/// totals are summed without overflow: a ledger that holds more than an int of minutes reads as the int maximum, not negative.
/// Fixture account key and dates match no real Riot ID.
/// </summary>
public sealed class RiotLedgerValidationTests : IDisposable
{
    private const string Account = "0123456789abcdef0123";
    private const string Game = "riot:valorant";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-riot-validate-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public RiotLedgerValidationTests()
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

    private static string Line(string key = "k1", string kind = "play", int schema = 1, string game = Game, string minutes = "5") =>
        "{\"account\":\"" + Account + "\",\"schema\":" + schema + ",\"kind\":\"" + kind + "\",\"key\":\"" + key + "\",\"game\":\"" + game +
        "\",\"minutes\":" + minutes + ",\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\",\"source\":\"exo\"}";

    [Fact]
    public void AWellFormedLineIsWritten()
    {
        RiotLedger.Merge(Line());

        Assert.Equal(5, RiotLedger.Totals(Account, Game));
    }

    [Theory]
    [InlineData("100001")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("2147483647")]
    public void AnImpossibleMinuteCountIsRefused(string minutes)
    {
        RiotLedger.Merge(Line(minutes: minutes));

        Assert.Equal(0, RiotLedger.Totals(Account, Game));
    }

    [Fact]
    public void AForeignSchemaKindOrGameIsRefused()
    {
        RiotLedger.Merge(Line(key: "a", schema: 2));
        RiotLedger.Merge(Line(key: "b", kind: "trophy"));
        RiotLedger.Merge(Line(key: "c", game: "steam:730"));

        Assert.Equal(0, RiotLedger.Totals(Account, Game));
        Assert.Equal(0, RiotLedger.Totals(Account, "steam:730"));
    }

    [Fact]
    public void TotalsDoNotOverflowWhenTheLedgerIsLarge()
    {
        var path = Path.Combine(_folder, "riot-ledger", Account + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"schema\":1,\"kind\":\"play\",\"key\":\"p1\",\"game\":\"" + Game + "\",\"minutes\":2147483647,\"startUtc\":\"2026-11-01T10:00:00Z\",\"mode\":\"unknown\"}\n" +
            "{\"schema\":1,\"kind\":\"play\",\"key\":\"p2\",\"game\":\"" + Game + "\",\"minutes\":2147483647,\"startUtc\":\"2026-11-02T10:00:00Z\",\"mode\":\"unknown\"}\n");
        RiotLedger.ForgetLoaded();

        Assert.Equal(int.MaxValue, RiotLedger.Totals(Account, Game));
    }
}

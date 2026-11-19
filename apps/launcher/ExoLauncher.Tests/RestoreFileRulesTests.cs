using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// What a restore keeps from the backup's files. Ways this could fail: a session the backup held open is restored, so this PC
/// counts it again (another PC's checkpoint, a second credit); an install folder from another PC is restored although it is not
/// on this one (every install then goes to a missing place); a ledger line this Launcher would not write is restored and counted.
/// Fixture folders and minutes are made up.
/// </summary>
public sealed class RestoreFileRulesTests
{
    private static JsonNode Prepared(string name, string json) =>
        JsonNode.Parse(Encoding.UTF8.GetString(LauncherBackup.Prepare(name, Encoding.UTF8.GetBytes(json), null)))!;

    [Fact]
    public void ARestoredPlaytimeFileDropsTheSessionsItHeldOpen()
    {
        var restored = Prepared("playtime.json", "{\"minutes\":{\"steam:1\":5},\"activeSessions\":{\"steam:1\":{\"startUtc\":\"2026-11-09T10:00:00Z\",\"checkpointUtc\":\"2026-11-09T10:30:00Z\"}}}");

        Assert.Equal(5, restored["minutes"]!["steam:1"]!.GetValue<int>());
        Assert.False(restored.AsObject().ContainsKey("activeSessions"));
    }

    [Fact]
    public void AMissingInstallRootIsDroppedAndAnExistingOneKept()
    {
        var missing = Path.Combine(Path.GetTempPath(), "exo-no-such-root-" + Guid.NewGuid().ToString("N"));
        var present = Path.GetTempPath();

        var gone = Prepared("settings.json", new JsonObject { ["defaultInstallRoot"] = missing }.ToJsonString());
        var kept = Prepared("settings.json", new JsonObject { ["defaultInstallRoot"] = present }.ToJsonString());

        Assert.False(gone.AsObject().ContainsKey("defaultInstallRoot"));
        Assert.Equal(present, kept["defaultInstallRoot"]!.GetValue<string>());
    }

    [Fact]
    public void ALedgerLineThisLauncherWouldNotWriteIsNotRestored()
    {
        const string good = "{\"schema\":1,\"kind\":\"play\",\"key\":\"k1\",\"game\":\"riot:valorant\",\"minutes\":5,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}";
        const string bad = "{\"schema\":1,\"kind\":\"play\",\"key\":\"k2\",\"game\":\"riot:valorant\",\"minutes\":20000,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}";
        var json = new JsonObject
        {
            ["kind"] = LauncherBackup.Kind,
            ["schema"] = LauncherBackup.Schema,
            ["files"] = new JsonObject { ["riot-ledger/unassigned.jsonl"] = new JsonObject { ["text"] = good + "\n" + bad + "\n" } },
        }.ToJsonString();

        var read = LauncherBackup.Validate(json);

        Assert.True(read.Ok, read.Message);
        var ledger = Encoding.UTF8.GetString(Assert.Single(read.Files).Bytes);
        Assert.Contains("\"k1\"", ledger);
        Assert.DoesNotContain("\"k2\"", ledger);
    }
    [Fact]
    public void AnInstallRootOnAShareIsDroppedWithoutBeingProbed()
    {
        var share = "\\\\attacker\\share";

        var restored = Prepared("settings.json", new JsonObject { ["defaultInstallRoot"] = share }.ToJsonString());

        Assert.False(restored.AsObject().ContainsKey("defaultInstallRoot"));
    }
}

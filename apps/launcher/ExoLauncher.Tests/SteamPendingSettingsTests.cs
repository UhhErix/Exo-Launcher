using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo could still take Steam from the player to write its own settings:
/// - a pass with Steam running closes it (or restarts it) to land a switch, whatever asked: Play, Properties, a library
///   read; a setting picked in Properties waits for the next close and says so, rather than closing Steam;
/// - a pass with Steam closed writes different bytes than it did before the close was removed: another game, another key
///   or the layout changes;
/// - Steam started by Exo reads its old settings because what waited was not put in first;
/// - the waiting check runs with nothing waiting, or never runs with something waiting.
/// </summary>
public sealed class SteamPendingSettingsTests
{
    private static string OverlaySource => File.ReadAllText(Repo.PathOf("ExoLauncher", "Services", "WebHostBridge.SteamOverlay.cs"));

    private static string LocalConfig() =>
        File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "Steam", "localconfig.vdf")).ReplaceLineEndings("\n");

    [Fact]
    public void ARunningSteamIsNeverClosedToWriteSettings()
    {
        // Running and something to write: the pass waits, at every caller's asking.
        Assert.Equal(OverlayPass.Plan.Wait, OverlayPass.Decide(steamRunning: true, anythingToWrite: true));
        // Running with nothing to write: nothing at all (and Steam is not touched).
        Assert.Equal(OverlayPass.Plan.Nothing, OverlayPass.Decide(steamRunning: true, anythingToWrite: false));

        // The pass itself holds no way to close Steam: no shutdown, no restart, no close.
        var source = OverlaySource;
        Assert.DoesNotContain("CloseSteamAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StartSteamHidden", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-shutdown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OverlayRestartSpacing", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_overlayRestartedAt", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingSettingsAreWrittenWhenSteamIsNotRunning()
    {
        Assert.Equal(OverlayPass.Plan.WriteNow, OverlayPass.Decide(steamRunning: false, anythingToWrite: true));
        Assert.Equal(OverlayPass.Plan.Nothing, OverlayPass.Decide(steamRunning: false, anythingToWrite: false));
        // Running, it is kept for the next close rather than written now.
        Assert.Equal(OverlayPass.Plan.Wait, OverlayPass.Decide(steamRunning: true, anythingToWrite: true));

        // A pass that waits writes nothing: no file writer runs in its arm, and the "?" stays in the record.
        var waitArm = Arm(OverlaySource, "case OverlayPass.Plan.Wait:");
        Assert.DoesNotContain("WriteFile", waitArm, StringComparison.Ordinal);
        Assert.Contains("SetOverlayPending(true)", waitArm, StringComparison.Ordinal);

        // What waited goes in as soon as the record is read with Steam closed.
        var text = LocalConfig();
        var (writes, record) = SteamOverlayConfig.Plan(SteamOverlayConfig.Apps(text),
            new Dictionary<string, bool> { ["1790600"] = true },
            new Dictionary<string, string> { ["1790600"] = "on?" });
        Assert.Equal(new Dictionary<string, bool> { ["1790600"] = true }, writes);
        Assert.Equal("on?", record["1790600"]);
        var (written, landed) = OverlayPass.Written(text, new OverlayPass.Wanted(writes, record, new Dictionary<string, string>()));
        Assert.True(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "1790600"));
        Assert.Equal("on", landed["1790600"]);
    }

    [Fact]
    public void ThePerGameOverlaySwitchSaysItWaitsWhileSteamRuns()
    {
        var (waits, message) = OverlayPass.SwitchAnswer(steamRunning: true);
        Assert.True(waits);
        Assert.Equal("Steam takes it when it next closes.", message);
        Assert.Equal((false, ""), OverlayPass.SwitchAnswer(steamRunning: false));

        // The page's answer carries the flag, and never when it does not wait (the serializer drops a null).
        var pending = ExoBridgeProtocol.SerializeEvent("game.properties.set", new { ok = true, pending = (bool?)true });
        Assert.Contains("\"pending\":true", pending, StringComparison.Ordinal);
        var plain = ExoBridgeProtocol.SerializeEvent("game.properties.set", new { ok = true, pending = (bool?)null });
        Assert.DoesNotContain("pending", plain, StringComparison.Ordinal);
        Assert.Contains("pending?: boolean", File.ReadAllText(Repo.PathOf("ui", "src", "lib", "host.ts")), StringComparison.Ordinal);
    }

    [Fact]
    public void ASwitchWrittenWithSteamClosedIsTheSameFileButForTheSwitch()
    {
        // The fixture as Steam wrote it, with the game's switch off.
        var text = LocalConfig();
        Assert.False(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(text), "1790600"));

        var (writes, record) = SteamOverlayConfig.Plan(SteamOverlayConfig.Apps(text),
            new Dictionary<string, bool> { ["1790600"] = true },
            new Dictionary<string, string> { ["1790600"] = "on?" });
        var (written, _) = OverlayPass.Written(text, new OverlayPass.Wanted(writes, record, new Dictionary<string, string>()));

        // Every byte but the switch is Steam's own: putting Steam's value back gives the file exactly.
        Assert.NotEqual(text, written);
        var back = written.Replace("\t\t\t\"OverlayAppEnable\"\t\t\"1\"", "\t\t\t\"OverlayAppEnable\"\t\t\"0\"", StringComparison.Ordinal);
        Assert.Equal(text, back);
        Assert.True(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "1790600"));
    }

    [Fact]
    public void EverySteamStartPutsWhatWaitedInFirst()
    {
        // The host's own starts all go through StartSteamHiddenAsync, which writes first.
        var start = Body(File.ReadAllText(Repo.PathOf("ExoLauncher", "Services", "WebHostBridge.Accounts.cs")), "private async Task StartSteamHiddenAsync(");
        Assert.Contains("SteamPendingSettings.BeforeStartAsync", start, StringComparison.Ordinal);

        // The adapter starts Steam without the bridge, so it asks for the write itself.
        var adapter = File.ReadAllText(Repo.PathOf("ExoLauncher", "Adapters", "SteamAdapter.cs"));
        Assert.Contains("SteamPendingSettings.BeforeStartAsync", adapter, StringComparison.Ordinal);
    }

    /// <summary>The text of one case arm: from its label to the next label or the switch's closing brace.</summary>
    private static string Arm(string source, string label)
    {
        var start = source.IndexOf(label, StringComparison.Ordinal);
        Assert.True(start >= 0, label + " is gone from the pass");
        var after = source[(start + label.Length)..];
        var ends = new[] { "\n            case ", "\n            default:", "\n        }" }
            .Select(marker => after.IndexOf(marker, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(after.Length)
            .Min();
        return after[..ends];
    }

    /// <summary>A method or property's own body, braces balanced, from its signature.</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " is gone");
        var open = source.IndexOf('{', start + signature.Length);
        Assert.True(open >= 0, signature + " has no body");
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail(signature + " is not closed");
        return string.Empty;
    }
}
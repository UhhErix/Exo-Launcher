using System.Diagnostics;
using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways it could fail: a settings pass accepts pending licences, rewrites existing consent, or stops writing overlay
/// settings; a launch watcher still selects an EULA Accept button. Steam alone owns the player's licence decision.
/// </summary>
public sealed class SteamLicenceTests
{
    private const string LocalConfig =
        "\"UserLocalConfigStore\"\n{\n\t\"apps\"\n\t{\n\t\t\"1593500\"\n\t\t{\n\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n\t\t}\n\t}\n" +
        "\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";

    [Fact]
    public void OverlaySettingsAreWrittenWithoutAcceptingPendingLicences()
    {
        var (written, _) = OverlayPass.Written(LocalConfig, new OverlayPass.Wanted(
            new Dictionary<string, bool> { ["1593500"] = true },
            new Dictionary<string, string> { ["1593500"] = "on?" }, new Dictionary<string, string>()));
        Assert.True(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "1593500"));
        Assert.DoesNotContain("1593500_eula_0", written);
        Assert.DoesNotContain("3768760_eula_1", written);
    }

    [Fact]
    public void ThePassAcceptsNothingByItself()
    {
        var wanted = new OverlayPass.Wanted(
            new Dictionary<string, bool>(), new Dictionary<string, string>(), new Dictionary<string, string>());
        Assert.False(wanted.Anything);
        Assert.Equal(LocalConfig, OverlayPass.Written(LocalConfig, wanted).Text);
        Assert.DoesNotContain("1593500_eula_0", OverlayPass.Written(LocalConfig, wanted).Text);
        Assert.DoesNotContain("3768760_eula_1", OverlayPass.Written(LocalConfig, wanted).Text);
    }

    [Fact]
    public void WaitingForSteamConsentDoesNotCountAsAnOfflineFailure()
    {
        Assert.False(SteamOfflineMode.ShouldRetryOnline(SteamProcessPath.LaunchNeedsAttention));
        Assert.True(SteamOfflineMode.ShouldRetryOnline("Steam refused to start the game offline."));
        Assert.True(SteamOfflineMode.ShouldRetryOnline(string.Empty));
    }

    [Fact]
    public async Task AgreementWordsInGameTitlesDoNotVetoNonAgreementPrompts()
    {
        const string fixtures = """
            $steam = 'C:\Steam\steam.exe'
            foreach ($title in @('License to Kill', 'Terms', 'EULA', 'Agreement')) {
              foreach ($label in @($title, ('Play ' + $title), ('Launch ' + $title))) {
                $dialog = [pscustomobject]@{
                  IsDialog = $true; Complete = $true; OwnerPath = $steam
                  Names = @($label, 'Launch options')
                  Buttons = @([pscustomobject]@{ Name = 'Play'; Enabled = $true; Invokable = $true; OwnerPath = $steam })
                }
                Select-SteamGameLaunch $steam $title $dialog
                $dialog.Names += 'Please read this license agreement'
                Select-SteamGameLaunch $steam $title $dialog
                $dialog.Names = @($label, 'Cloud sync failure')
                $dialog.Buttons[0].Name = 'Play anyway'
                Select-SteamCloudPlayAnyway $steam $dialog $title
                $dialog.Names += 'Please read this license agreement'
                Select-SteamCloudPlayAnyway $steam $dialog $title
              }
            }
            """;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoLogo -NoProfile -NonInteractive -Command -",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.StandardInput.WriteLineAsync((SteamGameEulaPromptAutomator.SelectionScript + "\n" + fixtures + "\n\n").AsMemory(), TestContext.Current.CancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, await errors);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(Enumerable.Range(0, 12).SelectMany(_ => new[] { "0", "-1", "0", "-1" }),
            (await output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void LaunchWatcherHasNoEulaAcceptAction()
    {
        var script = SteamGameEulaPromptAutomator.BuildScript(@"C:\Steam\steam.exe", "Game", 30);
        Assert.DoesNotContain("Select-SteamGameEulaAccept", script);
        Assert.DoesNotContain("accepted-game-eula", script);
        Assert.DoesNotContain("'Accept'", script);
        Assert.Contains("Select-SteamGameLaunch", script);
        Assert.Contains("Select-SteamCloudPlayAnyway", script);
    }

    [Fact]
    public void AGameThePlayerSwitchedOnKeepsItsOverlay()
    {
        // 1593500 needs no overlay by the rule (single player: off), and the player switched it on in Properties ("on=").
        var rule = new Dictionary<string, bool> { ["1593500"] = false };
        var record = new Dictionary<string, string> { ["1593500"] = "on=?" };
        var text = LocalConfig;
        for (var pass = 0; pass < 3; pass++)
        {
            var (writes, next) = SteamOverlayConfig.Plan(SteamOverlayConfig.Apps(text), rule, record);
            // Steam has it off (Exo's earlier write): the first pass writes the player's choice; later ones write nothing.
            Assert.Equal(pass == 0 ? 1 : 0, writes.Count);
            text = SteamOverlayConfig.Write(text, writes);
            record = next.ToDictionary(entry => entry.Key, entry => entry.Value.TrimEnd('?'));
            Assert.Equal("on=", record["1593500"]);
        }
        Assert.True(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(text), "1593500"));

        // A Steam update rewrites the file and drops the key, or writes it on again: still on, nothing written, still the player's.
        foreach (var updated in new[] { SteamOverlayConfig.RemoveValue(text, ["UserLocalConfigStore", "apps", "1593500"], "OverlayAppEnable"), text })
        {
            var after = SteamOverlayConfig.Plan(SteamOverlayConfig.Apps(updated), rule, record);
            Assert.Empty(after.Writes);
            Assert.Equal("on=", after.Record["1593500"]);
        }

        // Another game with no choice of the player's is off by the rule: the default is off unless the game needs it.
        var other = SteamOverlayConfig.Plan(SteamOverlayConfig.Apps(LocalConfig), new Dictionary<string, bool> { ["3768760"] = false }, new Dictionary<string, string>());
        Assert.False(other.Writes["3768760"]);
    }
}

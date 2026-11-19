using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// What a restore may write from a backup file, and which paths a backup may name. Ways a restored file could hurt: a launch override restored with
/// RunAsAdmin or a working folder elsewhere runs a game elevated or from another folder; launch arguments of any length or
/// with control characters; a ledger name outside the account keys runs a file out of the data folder; a backup file so big
/// it is read whole into memory. Fixtures are not real paths or accounts.
/// </summary>
public sealed class LauncherBackupDeviceOnlyTests
{
    private static JsonNode Restored(string json)
    {
        var bytes = LauncherBackup.Prepare("settings.json", Encoding.UTF8.GetBytes(json), null);
        return JsonNode.Parse(Encoding.UTF8.GetString(bytes))!;
    }

    [Fact]
    public void ARestoredLaunchOverrideKeepsItsArgsButNotRunAsAdminOrFolder()
    {
        var restored = Restored(@"{""launchOverrides"":{""steam:1"":{""extraArgs"":""-novid"",""workingDirectory"":""C:\\Temp"",""runAsAdmin"":true}}}");

        var launch = restored["launchOverrides"]!["steam:1"]!.AsObject();
        Assert.Equal("-novid", launch["extraArgs"]!.GetValue<string>());
        Assert.False(launch.ContainsKey("workingDirectory"));
        Assert.False(launch.ContainsKey("runAsAdmin"));
    }

    [Fact]
    public void RestoredLaunchArgsAreClampedToAKilobyteWithoutControlCharacters()
    {
        var long_ = new string('a', 2000);

        Assert.Equal(1024, LauncherBackup.ClampExtraArgs(long_)!.Length);
        Assert.Equal("-novid-x", LauncherBackup.ClampExtraArgs("-novid\u0007-x"));
        Assert.Null(LauncherBackup.ClampExtraArgs("   "));
    }

    [Theory]
    [InlineData("riot-ledger/0123456789abcdef0123.jsonl", true)]
    [InlineData("riot-ledger/unassigned.jsonl", true)]
    [InlineData("riot-ledger/0123456789ABCDEF0123.jsonl", false)]
    [InlineData("riot-ledger/0123456789abcdef012.jsonl", false)]
    [InlineData(@"riot-ledger/..\..\startup.jsonl", false)]
    [InlineData("riot-ledger/0123456789abcdef0123.jsonl.exe", false)]
    public void ALedgerNameIsAnAccountKeyOnly(string name, bool allowed) =>
        Assert.Equal(allowed, LauncherBackup.IsAllowedName(name));

    [Theory]
    [InlineData(256L * 1024 * 1024, true)]
    [InlineData(256L * 1024 * 1024 + 1, false)]
    public void ABackupFileOverTheCapIsNotReadWhole(long length, bool fits) =>
        Assert.Equal(fits, LauncherBackup.FitsBackupLimit(length));
}

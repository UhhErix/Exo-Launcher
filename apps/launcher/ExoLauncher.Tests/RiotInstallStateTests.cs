using ExoLauncher.Adapters.Riot;

namespace ExoLauncher.Tests;

/// <summary>
/// Riot's install-state answer, read by Exo before it launches VALORANT or League. Ways this read fails on a normal install:
/// an answer wrapped in JSON (the probe kept the whole body, so it read as "unknown"); a quoted value; a bare value with
/// spaces or a newline. An empty or unreadable answer stays null (unknown).
/// </summary>
public sealed class RiotInstallStateTests
{
    [Theory]
    [InlineData("installed", "installed")]
    [InlineData("\"installed\"", "installed")]
    [InlineData("  installed\n", "installed")]
    [InlineData("{\"state\":\"installed\"}", "installed")]
    [InlineData("{\"installState\":\"installed\",\"patchline\":\"live\"}", "installed")]
    [InlineData("{\"state\":\"Installed\"}", "Installed")]
    [InlineData("{\"other\":1}", null)]
    [InlineData("{ not json", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheInstallStateIsReadWhateverItsWrapping(string? body, string? expected) =>
        Assert.Equal(expected, RiotClientApi.ParseInstallState(body));
}

using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The answer to a restore that wrote its files. A restart the SDK refuses must not answer restarting:true, or the page waits
/// for a restart that never comes and shows a busy Restore step forever. Ways this could fail: a refused restart answers
/// restarting:true; the refusal answers ok:true, so the Restore step shows success without the "close and reopen" line; the
/// message is not the one the Restore step shows.
/// </summary>
public sealed class BackupRestoreAnswerTests
{
    private static JsonNode Answer(bool restartRefused) =>
        JsonSerializer.SerializeToNode(WebHostBridge.RestoreAnswer(new LauncherBackup.Outcome(true, "Restored.", 9), restartRefused))!;

    [Fact]
    public void ARefusedRestartAnswersAnErrorTheRestoreStepShows()
    {
        var answer = Answer(restartRefused: true);

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.False(answer["restarting"]!.GetValue<bool>());
        Assert.Equal("Restored. Close and reopen Exo to finish.", answer["message"]!.GetValue<string>());
        // The data is restored and this run's saves are frozen: the Restore step holds (no Start fresh, no Continue).
        Assert.True(answer["restored"]!.GetValue<bool>());
    }

    [Fact]
    public void ARestartThatGoesThroughAnswersRestarting()
    {
        var answer = Answer(restartRefused: false);

        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.True(answer["restarting"]!.GetValue<bool>());
    }
}

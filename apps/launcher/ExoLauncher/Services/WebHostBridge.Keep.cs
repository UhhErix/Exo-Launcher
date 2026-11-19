using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// keep.status: one read-only answer for the page, the PC's word and each game's by game id (<see cref="KeepStatus"/>), read
/// from the record alone (<see cref="Keep.Load"/>). It writes nothing, walks no game's install folder, and it sends no profile
/// id, no setting's name, no value and no count: a game with nothing to say is left out. A record that can't be read answers
/// none for the PC and no games, which is what a game Exo never optimized says too.
/// </summary>
public sealed partial class WebHostBridge
{
    private object KeepStatusForPage(JsonElement p, bool hasParams)
    {
        try
        {
            var doc = Keep.Load();
            var report = KeepStatus.For(doc, DriverKeep.PutBackShows(doc.Pc, DriverIdentity.Current(), DriverKeep.LastGameEnded(), DateTime.UtcNow));
            return new { pc = report.Pc, games = report.Games };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Keep: status couldn't be read: " + ex.GetType().Name);
            return new { pc = "none", games = new Dictionary<string, string>(StringComparer.Ordinal) };
        }
    }
}
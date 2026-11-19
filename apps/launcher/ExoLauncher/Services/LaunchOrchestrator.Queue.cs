namespace ExoLauncher.Services;

/// <summary>
/// The download manager (owner, 2026-11-19: "a download manager and being able to stop updates and auto updates and queued
/// updates"): one list of what runs, what waits in line and what is paused; a queued item moves up or down the line; the
/// player's own Pause all holds every download Exo runs until Resume all. Ways it could fail: a move loses or duplicates a
/// queued job; Pause all is undone by another hold's release; the list names a job that already finished.
/// </summary>
public sealed partial class LaunchOrchestrator
{
    /// <summary>The hold the player's Pause all puts on (<see cref="Hold"/>); only Resume all lifts it.</summary>
    internal const string PlayerHold = "player";

    /// <summary>One line of the download manager.</summary>
    public sealed record DownloadLine(string GameId, string? Title, string Store, string State, bool CanPause);

    /// <summary>What runs (state "running"), the line in its order ("queued"), then what is paused ("paused"); and why downloads wait.</summary>
    public (IReadOnlyList<DownloadLine> Lines, string? Held, bool PausedByPlayer) DownloadsSnapshot()
    {
        lock (_gate)
        {
            var lines = new List<DownloadLine>();
            if (_activeJob is { PublishProgress: true } job)
                lines.Add(new DownloadLine(job.GameId, job.DownloadIntent?.Title, job.Store.ToString().ToLowerInvariant(), "running",
                    job.DownloadIntent is not null && ExoDownloads(job.Store)));
            foreach (var pending in _pendingJobs.Where(p => p.PublishProgress))
                lines.Add(new DownloadLine(pending.Game.Id, pending.Game.Title, pending.Game.Store.ToString().ToLowerInvariant(), "queued", false));
            foreach (var intent in _downloadIntents.Values.Where(intent => _pausedDownloadTokens.Contains(intent.Token)).OrderBy(intent => intent.CreatedAtUtc))
                if (!lines.Any(line => string.Equals(line.GameId, intent.GameId, StringComparison.OrdinalIgnoreCase)))
                    lines.Add(new DownloadLine(intent.GameId, intent.Title, intent.Store.ToString().ToLowerInvariant(), "paused", false));
            return (lines, _holds.Count == 0 ? null : _holds.Values.Last(), _holds.ContainsKey(PlayerHold));
        }
    }

    /// <summary>
    /// Moves a queued game <paramref name="by"/> places (negative is sooner); the running job stays where it is. Every other
    /// job keeps its order.
    /// </summary>
    public object MoveQueued(string gameId, int by)
    {
        lock (_gate)
        {
            var line = _pendingJobs.ToList();
            var at = line.FindIndex(job => string.Equals(job.Game.Id, gameId, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return new { ok = false, message = "That title is not queued." };
            var to = Math.Clamp(at + by, 0, line.Count - 1);
            if (to == at) return new { ok = true, message = "Already there." };
            var job = line[at];
            line.RemoveAt(at);
            line.Insert(to, job);
            _pendingJobs.Clear();
            foreach (var queued in line) _pendingJobs.Enqueue(queued);
            AppLog.Info($"Queue: gameId={gameId} moved from {at + 1} to {to + 1}.");
            PublishDownloadQueueChangedLocked();
            return new { ok = true, message = "Moved." };
        }
    }

    /// <summary>The player's Pause all: what runs pauses where its store allows it, and nothing new starts until Resume all.</summary>
    public object PauseAll()
    {
        Hold(PlayerHold, "Paused by you");
        AppLog.Info("Downloads: paused by the player.");
        return new { ok = true, message = "Downloads paused." };
    }

    /// <summary>The player's Resume all: lifts their hold; what it paused carries on, oldest first, unless another hold still holds.</summary>
    public object ResumeAll()
    {
        Release(PlayerHold);
        AppLog.Info("Downloads: resumed by the player.");
        return new { ok = true, message = "Downloads resumed." };
    }
}

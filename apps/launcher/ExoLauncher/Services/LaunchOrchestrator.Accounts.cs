using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// A download belongs to the store account that started it (<see cref="DownloadIntent.AccountScope"/>). Owner, 2026-11-19:
/// "downloading app then switching to another account doesn't register, then it auto resumed when it should". When the
/// store's signed-in account changes (in Exo's Accounts, or in the store's own app, seen on the next check), what runs for
/// the old account pauses, what waits in line for it leaves the line paused, and a download a hold paused stays paused;
/// each says which account it waits for. When that account is signed in again, they carry on by themselves. Ways it could
/// fail: a queued download for the old account reaches the front and is dropped as "account changed"; a hold's release
/// resumes a download into a refusal that leaves it paused for review forever; a switch back never resumes anything; a
/// download the player paused themselves resumes on a switch back.
/// </summary>
public sealed partial class LaunchOrchestrator
{
    /// <summary>Saved state of a download paused because its store account is not the signed-in one: it carries on when it is.</summary>
    internal const string AccountState = "account";

    /// <summary>Downloads Exo paused for their account, in the order they paused: each resumes when its account is back.</summary>
    private readonly List<string> _accountHeldTokens = [];

    internal string AccountReason(StoreKind store) =>
        $"Paused: this belongs to your other {StoreName(store)} account. It carries on by itself when you switch back to it.";

    private string StoreName(StoreKind store) => Find(store)?.DisplayName is { Length: > 0 } name ? name : store.ToString();

    /// <summary>
    /// Compares every download with its store's signed-in account, and pauses or carries on accordingly. Cheap when nothing
    /// downloads; called after Exo's own account changes, and on the download policy's poll for changes made in the store.
    /// </summary>
    public void CheckDownloadAccounts()
    {
        HashSet<StoreKind> stores;
        lock (_gate)
        {
            // Only what this check can change: a download the player paused, or one paused for review, is left alone.
            stores = _heldTokens.Concat(_accountHeldTokens)
                .Select(token => _downloadIntents.TryGetValue(token, out var intent) ? intent : null)
                .OfType<DownloadIntent>().Select(intent => intent.Store).ToHashSet();
            foreach (var pending in _pendingJobs) if (pending.DownloadIntent is { } intent) stores.Add(intent.Store);
            if (_activeJob?.DownloadIntent is { } running) stores.Add(running.Store);
        }
        if (stores.Count == 0) return;
        // Store files are read outside the lock.
        var active = new Dictionary<StoreKind, string?>();
        foreach (var store in stores)
        {
            if (Find(store) is not IStoreAccountScope scoped) continue;
            try { active[store] = scoped.GetActiveAccountScope(); }
            catch { /* unreadable this time: leave that store as it is */ }
        }
        if (active.Count == 0) return;

        JobState? paused = null;
        var resume = new List<string>();
        lock (_gate)
        {
            bool Elsewhere(DownloadIntent intent) =>
                !string.IsNullOrWhiteSpace(intent.AccountScope) && active.TryGetValue(intent.Store, out var now) &&
                !string.Equals(now, intent.AccountScope, StringComparison.Ordinal);
            var changed = false;

            // The running download, where Exo drives it. Steam's own client keeps its downloads.
            if (_activeJob is { DownloadIntent: { } runningIntent } job && job.PauseReason is null && ExoDownloads(job.Store) && Elsewhere(runningIntent))
            {
                _accountHeldTokens.Add(runningIntent.Token);
                paused = PauseActiveLocked(AccountReason(job.Store), held: true);
                _downloadIntents[runningIntent.Token] = _downloadIntents[runningIntent.Token] with { State = AccountState };
                AppLog.Info($"Download account: {job.Store} account changed; paused gameId={job.GameId}.");
                changed = true;
            }

            // What waits in line for the old account leaves the line, paused, instead of being dropped at the front.
            if (_pendingJobs.Any(pending => pending.DownloadIntent is { } intent && Elsewhere(intent)))
            {
                var kept = new Queue<PendingJob>();
                while (_pendingJobs.TryDequeue(out var pending))
                {
                    if (pending.DownloadIntent is not { } intent || !Elsewhere(intent)) { kept.Enqueue(pending); continue; }
                    ParkForAccountLocked(intent);
                    pending.Completion?.TrySetResult(new InstallResult { Ok = false, Message = AccountReason(intent.Store) });
                    AppLog.Info($"Download account: {intent.Store} account changed; queued gameId={intent.GameId} paused.");
                }
                foreach (var pending in kept) _pendingJobs.Enqueue(pending);
                changed = true;
            }

            // A download a hold paused waits for its account too, not for the hold.
            foreach (var token in _heldTokens.ToArray())
            {
                if (!_downloadIntents.TryGetValue(token, out var intent) || !_pausedDownloadTokens.Contains(token) || !Elsewhere(intent)) continue;
                _heldTokens.Remove(token);
                ParkForAccountLocked(intent);
                changed = true;
            }

            // Its account is back: it carries on, or waits for the hold that still holds.
            foreach (var token in _accountHeldTokens.ToArray())
            {
                if (!_downloadIntents.TryGetValue(token, out var intent) || !_pausedDownloadTokens.Contains(token))
                {
                    _accountHeldTokens.Remove(token);
                    continue;
                }
                if (!active.TryGetValue(intent.Store, out var now) || !string.Equals(now, intent.AccountScope, StringComparison.Ordinal)) continue;
                _accountHeldTokens.Remove(token);
                changed = true;
                if (_holds.Count > 0)
                {
                    _downloadIntents[token] = intent with { State = HeldState, ReviewReason = TrimReviewReason(_holds.Values.Last()) };
                    _heldTokens.Add(token);
                }
                else resume.Add(intent.GameId);
            }
            if (resume.Count > 0) AppLog.Info($"Download account: the account is back; resuming {resume.Count} download(s).");
            if (changed)
            {
                SaveDownloadIntentsLocked();
                PublishDownloadQueueChangedLocked();
            }
        }
        if (paused is not null) CancelJob(paused);
        if (resume.Count > 0) ResumeInOrder(resume, "Resume after account switch");
        else if (paused is null) DrainQueue();
    }

    /// <summary>A queued or held download that waits for its account (under _gate).</summary>
    private void ParkForAccountLocked(DownloadIntent intent)
    {
        _downloadIntents[intent.Token] = intent with { State = AccountState, ReviewReason = TrimReviewReason(AccountReason(intent.Store)) };
        _pausedDownloadTokens.Add(intent.Token);
        if (!_accountHeldTokens.Contains(intent.Token)) _accountHeldTokens.Add(intent.Token);
    }

    /// <summary>Resumes each in order off the caller's thread (each claims its place before the next), then moves the queue.</summary>
    private void ResumeInOrder(IReadOnlyList<string> gameIds, string what)
    {
        _ = Task.Run(() =>
        {
            foreach (var gameId in gameIds)
                _ = ResumeDownloadAsync(gameId).ContinueWith(task =>
                {
                    if (task.IsFaulted) AppLog.Info($"{what} failed: " + task.Exception?.GetBaseException().Message);
                    else if (!task.Result.Ok && !task.Result.Queued) AppLog.Info($"{what}: gameId={gameId}; {task.Result.Message}");
                }, TaskScheduler.Default);
            DrainQueue();
        });
    }

    /// <summary>True while a download waits for its store account (for the paused list's words).</summary>
    private bool WaitsForAccountLocked(DownloadIntent intent) => _accountHeldTokens.Contains(intent.Token);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher;

/// <summary>
/// Highlight reel: the clips the page picked (a game's session, or a selection) become one new video beside them, joined by
/// Exo Capture (<see cref="Services.CaptureReel"/> decides how). Never while a game runs (it can wait for the game to end;
/// a game starting stops it), one at a time, at the add-on's below-normal priority. The clips are only read; the new file
/// is a clip like any other, in the captures folder. The page hears of it as capture.reel: waiting, working (percent),
/// done (the item), stopped, or failed (why; wait: a game was why).
/// </summary>
public sealed partial class MainWindow
{
    private readonly Lock _reelGate = new();
    /// <summary>Set while a reel is being made.</summary>
    private CancellationTokenSource? _reelCts;
    /// <summary>Set while a reel waits for a game to end: the clips it was asked for.</summary>
    private List<string>? _reelWaiting;
    private bool _reelStoppedByGame;

    /// <summary>capture.reel: ids (clips, in any order) and wait (hold the reel until the game ends). The progress comes as capture.reel.</summary>
    internal object CaptureReelStart(JsonElement p)
    {
        if (CaptureAddon is null) return new { ok = false, message = "Exo Capture is not installed." };
        var ids = new List<string>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var id in list.EnumerateArray())
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: 16 } text && !ids.Contains(text, StringComparer.Ordinal) && ids.Count <= Services.CaptureReel.MostClips)
                    ids.Add(text);
        if (ids.Count < 2) return new { ok = false, message = "Pick at least two clips." };
        var wait = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("wait", out var asked) && asked.ValueKind == JsonValueKind.True;
        lock (_reelGate)
        {
            var gate = Services.CaptureReel.Admit(_reelCts is not null || _reelWaiting is not null, ReelGameRunning(), ReelRecording(), wait);
            switch (gate.Go)
            {
                case Services.CaptureReel.Go.Refuse:
                    return new { ok = false, message = gate.Message, wait = gate.Waits };
                case Services.CaptureReel.Go.Wait:
                    _reelWaiting = ids;
                    PostReel("waiting");
                    return new { ok = true };
            }
            BeginReel(ids);
        }
        return new { ok = true };
    }

    /// <summary>capture.reel.cancel: stops the reel being made, or the one waiting for a game.</summary>
    internal object CaptureReelCancel()
    {
        lock (_reelGate)
        {
            if (_reelWaiting is not null)
            {
                _reelWaiting = null;
                PostReel("stopped");
            }
            else _reelCts?.Cancel();
        }
        return new { ok = true };
    }

    private bool ReelGameRunning() => _captureGameId is not null || RunningLibraryGames().Count > 0;
    private bool ReelRecording() => _captureState?["recording"]?.GetValue<bool>() == true;

    /// <summary>A game is starting: the add-on stops the join within a second, deletes its partial file, and the page can offer to wait.</summary>
    private void StopReelForGame()
    {
        lock (_reelGate)
        {
            if (_reelCts is null) return;
            _reelStoppedByGame = true;
            _reelCts.Cancel();
        }
    }

    /// <summary>The game ended: a reel that waited for it starts once nothing else holds it.</summary>
    private void ResumeReel()
    {
        if (Volatile.Read(ref _reelWaiting) is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // Windows lists the game as running for a moment after it ends.
                await Task.Delay(TimeSpan.FromSeconds(10), _lifetimeCts.Token).ConfigureAwait(false);
                lock (_reelGate)
                {
                    if (_reelWaiting is not { } ids) return;
                    var gate = Services.CaptureReel.Admit(_reelCts is not null, ReelGameRunning(), ReelRecording(), wait: true);
                    if (gate.Go == Services.CaptureReel.Go.Wait) return;
                    _reelWaiting = null;
                    if (gate.Go == Services.CaptureReel.Go.Refuse) PostReel("failed", message: gate.Message);
                    else BeginReel(ids);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Under <see cref="_reelGate"/>.</summary>
    private void BeginReel(List<string> ids)
    {
        var cts = _reelCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _reelStoppedByGame = false;
        PostReel("working");
        _ = Task.Run(async () =>
        {
            Services.CaptureReel.Outcome outcome;
            try { outcome = await RunReelAsync(ids, cts.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                AppLog.Warn("Capture reel: " + ex.GetType().Name + ": " + ex.Message);
                outcome = new Services.CaptureReel.Outcome(false, null, "Exo could not finish that. The details are in the log.");
            }
            bool byGame;
            lock (_reelGate)
            {
                _reelCts = null;
                byGame = _reelStoppedByGame;
            }
            cts.Dispose();
            if (outcome.Detail is not null) AppLog.Warn("Capture reel: " + outcome.Detail);
            if (outcome.Ok && outcome.Path is { } made) PostReel("done", item: ItemFor(made));
            else if (outcome.Cancelled && byGame) PostReel("failed", message: "A game started, so Exo stopped the reel.", wait: true);
            else if (outcome.Cancelled) PostReel("stopped");
            else PostReel("failed", message: outcome.Message);
        });
    }

    private async Task<Services.CaptureReel.Outcome> RunReelAsync(List<string> ids, CancellationToken ct)
    {
        if (CaptureAddon is not { } addon) return new Services.CaptureReel.Outcome(false, null, "Exo Capture is not installed.");
        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        var clips = new List<Services.CaptureReel.Clip>();
        foreach (var path in CaptureFiles())
        {
            if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.GetFullPath(path);
            if (!wanted.Contains(CaptureId(full)) || RelativeCapture(full) is not { } relative) continue;
            var info = new FileInfo(full);
            CaptureMeta? meta;
            lock (_captureGate) CaptureIndex.TryGetValue(relative, out meta);
            clips.Add(new Services.CaptureReel.Clip(full, info.CreationTimeUtc, info.Length, relative.Split(Path.DirectorySeparatorChar)[0], meta?.GameId ?? GameOfFolder(relative)));
        }
        if (clips.Count != ids.Count) return new Services.CaptureReel.Outcome(false, null, "A clip is gone.");
        return await Services.CaptureReel.MakeAsync(clips, CaptureFolder, FreeBytes(CaptureFolder), new AddonReelEngine(addon), ct).ConfigureAwait(false);
    }

    /// <summary>What is free on the captures folder's drive; plenty when Windows will not say.</summary>
    private static long FreeBytes(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return long.MaxValue; }
    }

    private void PostReel(string state, double percent = 0, string? message = null, bool wait = false, object? item = null) =>
        _bridge?.PostCaptureEvent("capture.reel", new { state, percent, message, wait, item });

    /// <summary>The add-on's concat events: how far the join is.</summary>
    private void OnReelProgress(JsonNode? data)
    {
        if (data?["percent"]?.GetValue<double>() is { } percent) PostReel("working", percent);
    }

    /// <summary>Exo Capture, as <see cref="Services.CaptureReel"/> needs it.</summary>
    private sealed class AddonReelEngine(Services.CaptureAddon addon) : Services.CaptureReel.IReelEngine
    {
        public async Task<IReadOnlyList<Services.CaptureReel.Facts?>> FactsAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            var reply = await addon.CallAsync("concat.facts", new JsonObject { ["paths"] = Paths(paths) }, ct).ConfigureAwait(false);
            var facts = new List<Services.CaptureReel.Facts?>();
            foreach (var node in reply as JsonArray ?? [])
                facts.Add(node?["ok"]?.GetValue<bool>() == true
                    ? new Services.CaptureReel.Facts(node["signature"]?.GetValue<string>() ?? "", (int)(node["width"]?.GetValue<double>() ?? 0), (int)(node["height"]?.GetValue<double>() ?? 0),
                        node["fps"]?.GetValue<double>() ?? 0, (int)(node["soundTracks"]?.GetValue<double>() ?? 0), node["hdr"]?.GetValue<bool>() ?? false, node["durationMs"]?.GetValue<double>() ?? 0)
                    : null);
            return facts;
        }

        public async Task<Services.CaptureReel.JoinResult> JoinAsync(Services.CaptureReel.JoinJob job, CancellationToken ct)
        {
            // The add-on stops within a second and answers; the answer is what comes back here.
            using var stop = ct.Register(() => _ = addon.CallAsync("concat.cancel", new JsonObject(), CancellationToken.None)
                .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted));
            var reply = await addon.CallAsync("concat", new JsonObject
            {
                ["paths"] = Paths(job.Paths),
                ["output"] = job.Output,
                ["mode"] = job.Mode == Services.CaptureReel.JoinMode.Copy ? "copy" : "reencode",
                ["width"] = job.Width,
                ["height"] = job.Height,
                ["game"] = job.GameId ?? "",
            }, CancellationToken.None).ConfigureAwait(false);
            return reply?["ok"]?.GetValue<bool>() == true
                ? new Services.CaptureReel.JoinResult(reply["path"]?.GetValue<string>(), null, null)
                : new Services.CaptureReel.JoinResult(null, reply?["skipped"]?.GetValue<string>() ?? "failed", reply?["detail"]?.GetValue<string>());
        }

        private static JsonArray Paths(IReadOnlyList<string> paths) => new(paths.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
    }
}

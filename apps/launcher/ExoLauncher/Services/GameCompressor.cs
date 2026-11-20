using System.IO.Enumeration;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using static ExoLauncher.Services.GameCompression;
using Store = ExoLauncher.Services.GameCompressionStore;

namespace ExoLauncher.Services;

/// <summary>
/// Compress games, run: one game at a time (a queue for Compress all), its files on several threads below normal priority
/// (the player's CPU budget, one thread on a hard drive), pausable and cancellable, picked up where it stopped after a
/// restart. Never while the game runs or its store writes it, and every job waits while any game runs. While the PC is
/// really idle (<see cref="PcIdle"/>, the junk clean's rule) it can compress the games worth it and the files an update
/// wrote back, each its own switch, both off until the player turns them on. The decisions are <see cref="GameCompression"/>'s.
/// </summary>
internal sealed class GameCompressor(AppServices services) : IDisposable
{
    /// <summary>A game's state changed (progress, a job ended, an estimate in): the page reads it again.</summary>
    internal event Action<string>? Changed;

    private sealed class Job(GameEntry game, bool compress, bool idle)
    {
        public GameEntry Game { get; } = game;
        public bool Compress { get; } = compress;
        public bool Idle { get; } = idle;
        public CancellationTokenSource Stop { get; } = new();
        public ManualResetEventSlim Go { get; } = new(true);
        public long Done;
        public long Total;
        public volatile bool UserPaused;
        public volatile string? Held;
        public volatile string? Failed;
    }

    private readonly Lock _gate = new();
    private Job? _job;
    private readonly List<string> _queue = [];
    private readonly Dictionary<string, (string Stamp, long Before, long After)> _quick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime At, long Bytes)> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _tick;
    private Timer? _watch;
    private int _ticking;

    internal void Start()
    {
        services.Launcher.GameLaunching += OnGameLaunching;
        services.Launcher.ProgressChanged += OnInstallProgress;
        lock (_gate)
        {
            _tick ??= new Timer(_ => Tick(), null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(2));
            _watch ??= new Timer(_ => Watch(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_job is not null) ArmWatchLocked();
        }
    }

    private void ArmWatchLocked()
    {
        try { _watch?.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)); }
        catch (ObjectDisposedException) { }
    }

    private void PauseWatchLocked()
    {
        try { _watch?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ what the page reads

    /// <summary>One game's compression for the page, bounded: no path.</summary>
    internal sealed record View(
        string State, double? Percent, long Saved, long Before, long After, long? EstimateBefore, long? EstimateAfter, bool Sampled,
        string? Algorithm, long Changed, string? Message, string? AntiCheat, string? Held);

    internal View ViewOf(GameEntry game)
    {
        var saved = Store.Of(game.Id);
        Job? job;
        lock (_gate) job = _job is { } j && j.Game.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase) ? j : null;
        var estimate = saved.Estimate;
        double? percent = job is { Total: > 0 } ? Math.Clamp((double)Interlocked.Read(ref job.Done) / job.Total, 0, 1) : null;
        long changed;
        lock (_gate) changed = _changed.GetValueOrDefault(game.Id).Bytes;
        var (state, message, antiCheat) = job is not null
            ? (job.UserPaused ? Store.Status.Paused : job.Compress ? Store.Status.Compressing : Store.Status.Uncompressing, job.Failed, (string?)null)
            : Settled(game, saved, changed);
        lock (_gate)
        {
            if (job is null && _queue.Contains(game.Id, StringComparer.OrdinalIgnoreCase)) state = "queued";
            // What stopped the last job stays on the row until the next one starts.
            if (job is null && message is null && _failed.TryGetValue(game.Id, out var failed)) message = failed;
        }
        return new View(state, percent, Math.Max(0, saved.Before - saved.After), saved.Before, saved.After,
            estimate?.Before, estimate?.After, estimate?.Sampled == true, saved.Algorithm, changed, message, antiCheat, job?.Held);
    }

    /// <summary>A game with no job: refused, waiting for the player's yes, changed by an update, compressed, paused or not.</summary>
    private static (string State, string? Message, string? AntiCheat) Settled(GameEntry game, Store.Game saved, long changed)
    {
        if (RefusalOf(game) is { } refused) return ("refused", refused, null);
        var answer = AntiCheatCheck.Cached(game);
        if (GuardFor(game, answer, saved.Consent) == Guard.Nothing)
        {
            var consentable = !DlssSwapService.NeverAllowed(game, answer.Label ?? "") && answer.Kind is AntiCheatCheck.Kind.Found or AntiCheatCheck.Kind.PossiblyVac;
            return (consentable ? "needsConsent" : "refused", AntiCheatCheck.Sentence(game, answer, AntiCheatCheck.Change.Compress, saved.Consent), consentable ? answer.Label : null);
        }
        if (saved.Status == Store.Status.Compressed && WorthAgain(changed, saved.Before)) return ("changed", null, null);
        return (saved.Status is Store.Status.Compressed or Store.Status.Paused or Store.Status.Kept ? saved.Status : "none", null, null);
    }

    /// <summary>Why this game can't be compressed at all (its folder, its drive), or null.</summary>
    internal static string? RefusalOf(GameEntry game)
    {
        if (!game.Installed || DlssSwapService.ResolveInstallRoot(game.Path) is not { } root) return "Exo couldn't find this game's folder.";
        if (AntiCheatCheck.IsRiot(game)) return "Exo never changes Riot games or games with Vanguard.";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            var oneDrives = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(Environment.GetEnvironmentVariable).OfType<string>();
            return Refusal(root, drive.IsReady ? drive.DriveFormat : null, drive.DriveType is DriveType.Fixed or DriveType.Removable,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), oneDrives);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "Exo couldn't read this game's drive."; }
    }

    /// <summary>
    /// Every installed game for the PC room: what each saved, or might save by the local prior (a walk of names and sizes,
    /// no file opened), kept until the game changes.
    /// </summary>
    internal IReadOnlyList<(GameEntry Game, View View)> List()
    {
        var games = services.Library.PeekCachedLibrary().Where(g => g.Installed && !string.IsNullOrWhiteSpace(g.Path)).ToList();
        var rows = new (GameEntry, View)[games.Count];
        Parallel.For(0, games.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            var game = games[i];
            var view = ViewOf(game);
            if (view.EstimateBefore is null && view.State is "none" or "needsConsent" && QuickEstimate(game) is { } quick)
                view = view with { EstimateBefore = quick.Before, EstimateAfter = quick.After };
            rows[i] = (game, view);
        });
        return rows;
    }

    private (long Before, long After)? QuickEstimate(GameEntry game)
    {
        if (DlssSwapService.ResolveInstallRoot(game.Path) is not { } root) return null;
        var stamp = AntiCheatCheck.AnswerCache.Stamp(game);
        lock (_gate) if (_quick.TryGetValue(game.Id, out var kept) && kept.Stamp == stamp) return (kept.Before, kept.After);
        var files = Walk(root, deep: false, CancellationToken.None);
        if (files.Count == 0) return null;
        var cluster = GameCompressionNative.Cluster(root);
        var guard = GuardFor(game, AntiCheatCheck.Cached(game), Store.Of(game.Id).Consent);
        var poor = files.Select(f => ExtensionOf(f.Relative)).Where(ext => PriorRatio(ext, Engine.Unknown) > 1 - PoorGain).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var algorithm = AlgorithmFor(root, files);
        var plan = MakePlan(files, algorithm, cluster, guard == Guard.Nothing ? Guard.DataOnly : guard, DirectStorage(files), poor);
        var (before, after) = Saving(files, plan, new Dictionary<string, Sample>(), EngineOf(files.Select(f => f.Relative)), cluster);
        lock (_gate) _quick[game.Id] = (stamp, before, after);
        return (before, after);
    }

    // ------------------------------------------------------------------ the estimate

    /// <summary>
    /// The real estimate: a walk of the install (each file's state asked of WOF), then a few MB of each extension through the
    /// XPRESS codec at WOF's own chunk (<see cref="GameCompressionNative.Sampler"/>). Its poor extensions are kept for the job.
    /// </summary>
    internal View Estimate(GameEntry game, CancellationToken ct)
    {
        if (RefusalOf(game) is null && DlssSwapService.ResolveInstallRoot(game.Path) is { } root)
        {
            var saved = Store.Of(game.Id);
            var files = Walk(root, deep: true, ct);
            var cluster = GameCompressionNative.Cluster(root);
            var algorithm = Parse(saved.Algorithm) is { } kept && saved.Status is Store.Status.Compressed or Store.Status.Paused ? kept : AlgorithmFor(root, files);
            var samples = SampleExtensions(root, files, algorithm, cluster, ct);
            var poor = GameCompression.Poor(samples);
            var guard = GuardFor(game, AntiCheatCheck.Cached(game), saved.Consent);
            var plan = MakePlan(files, algorithm, cluster, guard == Guard.Nothing ? Guard.DataOnly : guard, DirectStorage(files), poor);
            var (before, after) = Saving(files, plan, samples, EngineOf(files.Select(f => f.Relative)), cluster);
            if (saved.Status == Store.Status.Compressed)
                lock (_gate) _changed[game.Id] = (DateTime.UtcNow, GameCompression.Changed(files, saved.CompressedUtc ?? DateTime.MinValue, algorithm, cluster, guard, DirectStorage(files), poor.ToHashSet(StringComparer.OrdinalIgnoreCase)).Bytes);
            Store.Update(game.Id, g => g with
            {
                Poor = [.. poor.Order(StringComparer.Ordinal)],
                Estimate = new Store.Estimate(before, after, samples.Count > 0, AntiCheatCheck.AnswerCache.Stamp(game), DateTime.UtcNow),
            });
            Changed?.Invoke(game.Id);
        }
        return ViewOf(game);
    }

    /// <summary>Per extension: up to 12 files, 4 MB in all, 96 MB for the whole game; LZX's chunk at LZX's tighter packing.</summary>
    private static Dictionary<string, Sample> SampleExtensions(string root, IReadOnlyList<FileFact> files, Algorithm algorithm, long cluster, CancellationToken ct)
    {
        var samples = new Dictionary<string, Sample>(StringComparer.OrdinalIgnoreCase);
        using var sampler = GameCompressionNative.Sampler.Open();
        if (sampler is null) return samples;
        var chunk = ChunkOf(algorithm);
        const long perExtension = 4L << 20, perGame = 96L << 20;
        long read = 0;
        foreach (var group in files.Where(f => f.Length > cluster && f.Current is null).GroupBy(f => ExtensionOf(f.Relative))
                     .OrderByDescending(g => g.Sum(f => f.Length)))
        {
            if (ct.IsCancellationRequested || read >= perGame) break;
            // Spread over the group: its biggest files and the rest in name order, so a rerun reads the same.
            var picks = group.OrderByDescending(f => f.Length).Take(4).Concat(group.OrderBy(f => f.Relative, StringComparer.Ordinal).Where((_, i) => i % 7 == 0)).Distinct().Take(12).ToList();
            var maxChunks = (int)Math.Max(1, perExtension / picks.Count / chunk);
            long raw = 0, packed = 0;
            foreach (var file in picks)
            {
                var sample = sampler.Measure(Path.Combine(root, file.Relative), file.Length, chunk, maxChunks);
                raw += sample.Raw;
                packed += sample.Packed;
            }
            read += raw;
            if (algorithm == Algorithm.Lzx) packed = (long)(packed * LzxOverXpress);
            if (raw > 0) samples[group.Key] = new Sample(raw, packed);
        }
        return samples;
    }

    private static Algorithm AlgorithmFor(string root, IReadOnlyList<FileFact> files) =>
        Choose(files.Sum(f => f.Length), Environment.ProcessorCount, HardDriveOf(root), files.Count == 0 ? DateTime.MinValue : files.Max(f => f.WrittenUtc), DateTime.UtcNow);

    private static readonly Dictionary<string, bool> Spinning = new(StringComparer.OrdinalIgnoreCase);
    private static bool HardDriveOf(string root)
    {
        var volume = Path.GetPathRoot(root) ?? root;
        lock (Spinning)
            if (Spinning.TryGetValue(volume, out var known)) return known;
        var hard = GameCompressionNative.HardDrive(root);
        lock (Spinning) Spinning[volume] = hard;
        return hard;
    }

    private static bool DirectStorage(IReadOnlyList<FileFact> files) =>
        files.Any(f => Path.GetFileName(f.Relative).Equals("dstorage.dll", StringComparison.OrdinalIgnoreCase));

    /// <summary>The install's files, links not followed. Deep: each file bigger than a cluster asked of WOF (WOF hides its own reparse point).</summary>
    internal static List<FileFact> Walk(string root, bool deep, CancellationToken ct)
    {
        var cluster = deep ? GameCompressionNative.Cluster(root) : 4096;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var start = Path.TrimEndingDirectorySeparator(root).Length + 1;
        var found = new FileSystemEnumerable<(string Full, long Length, FileAttributes Attributes, DateTime Written)>(root,
            (ref FileSystemEntry e) => (e.ToFullPath(), e.Length, e.Attributes, e.LastWriteTimeUtc.UtcDateTime), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };
        var files = new List<FileFact>();
        try
        {
            foreach (var (full, length, attributes, written) in found)
            {
                ct.ThrowIfCancellationRequested();
                var current = deep && length > cluster ? GameCompressionNative.Current(full) : null;
                var onDisk = current is null ? RoundUp(length, cluster) : GameCompressionNative.OnDisk(full);
                files.Add(new FileFact(full[start..], length, attributes, written, current, onDisk < 0 ? RoundUp(length, cluster) : onDisk));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Compress: a walk stopped: " + ex.Message); }
        return files;
    }

    // ------------------------------------------------------------------ jobs

    /// <summary>Why the job can't start now (the game runs, its store writes it, the drive's too full), or null once it starts.</summary>
    internal string? Begin(GameEntry game, bool compress, bool idle = false)
    {
        if (compress && Settled(game, Store.Of(game.Id), 0) is { State: "refused" or "needsConsent" } no) return no.Message ?? "Exo can't compress this game.";
        if (!compress && RefusalOf(game) is { } refused) return refused;
        if (Blocked(game) is { } why) return why;
        lock (_gate)
        {
            if (_job is { } running)
            {
                if (running.Game.Id.Equals(game.Id, StringComparison.OrdinalIgnoreCase)) return null;
                if (!running.Idle || !compress)
                {
                    if (!compress) return $"Wait for {running.Game.Title} to finish.";
                    if (!_queue.Contains(game.Id, StringComparer.OrdinalIgnoreCase)) _queue.Add(game.Id);
                    Changed?.Invoke(game.Id);
                    return null;
                }
                // The player's own press goes before an idle job: that one stops and starts again later.
                running.Stop.Cancel();
            }
            var job = new Job(game, compress, idle);
            _job = job;
            _queue.RemoveAll(id => id.Equals(game.Id, StringComparison.OrdinalIgnoreCase));
            _failed.Remove(game.Id);
            var thread = new Thread(() => Run(job)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Exo compress" };
            thread.Start();
            ArmWatchLocked();
        }
        return null;
    }

    /// <summary>The game runs, or its store installs or updates it: one sentence, else null.</summary>
    private string? Blocked(GameEntry game)
    {
        if (services.Launcher.IsGameBusy(game)) return $"Wait for {game.Title} to finish updating.";
        if (AntiCheatCheck.GameOrAntiCheatRunning(game, AntiCheatCheck.Cached(game).Names)) return $"Close {game.Title} first.";
        return null;
    }

    internal bool Pause(string id, bool pause)
    {
        lock (_gate)
        {
            if (_job is not { } job || !job.Game.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return false;
            job.UserPaused = pause;
            if (pause) job.Go.Reset(); else if (job.Held is null) job.Go.Set();
        }
        Changed?.Invoke(id);
        return true;
    }

    /// <summary>Stops the job (what is compressed stays so) or takes the game out of the queue.</summary>
    internal void Cancel(string id)
    {
        lock (_gate)
        {
            _queue.RemoveAll(q => q.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (_job is { } job && job.Game.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) { job.Stop.Cancel(); job.Go.Set(); }
        }
        Changed?.Invoke(id);
    }

    /// <summary>The player's yes (the anti-cheat set shown) or no for a game with anti-cheat. Null when it took; else why not.</summary>
    internal string? Consent(GameEntry game, bool allow, string? shown)
    {
        if (!allow)
        {
            Store.Update(game.Id, g => g with { Consent = null });
            Changed?.Invoke(game.Id);
            return null;
        }
        var answer = AntiCheatCheck.Of(game, CancellationToken.None);
        if (answer.Kind is not (AntiCheatCheck.Kind.Found or AntiCheatCheck.Kind.PossiblyVac) || DlssSwapService.NeverAllowed(game, answer.Label ?? ""))
            return AntiCheatCheck.Sentence(game, answer, AntiCheatCheck.Change.Compress) ?? "Exo found no anti-cheat in this game.";
        if (!string.Equals(shown, answer.Label, StringComparison.OrdinalIgnoreCase)) return $"This game now shows {answer.Name}. Look again before allowing it.";
        Store.Update(game.Id, g => g with { Consent = answer.Label });
        AppLog.Info($"Compress: allowed for gameId={game.Id}; anti-cheat {answer.Label}; data files only.");
        Changed?.Invoke(game.Id);
        return null;
    }

    /// <summary>Compress all: every game worth it goes in the queue, the first one starts. How many were queued.</summary>
    internal int CompressAll()
    {
        var due = List().Where(row => Eligible(row.View)).OrderByDescending(row => Gain(row.View)).Select(row => row.Game).ToList();
        foreach (var game in due) Begin(game, compress: true);
        return due.Count;
    }

    /// <summary>Worth compressing: not refused, not compressed, kept or waiting for the player, and its estimate saves 1 GB or a tenth.</summary>
    internal static bool Eligible(View view) =>
        view.State is "none" or "paused" && Gain(view) is var gain && view.EstimateBefore is { } before && (gain >= GB || (before > 0 && gain >= before / 10));

    internal static long Gain(View view) => Math.Max(0, (view.EstimateBefore ?? 0) - (view.EstimateAfter ?? 0));

    private void Run(Job job)
    {
        var id = job.Game.Id;
        try
        {
            if (DlssSwapService.ResolveInstallRoot(job.Game.Path) is not { } root) { job.Failed = "Exo couldn't find this game's folder."; return; }
            var saved = Store.Of(id);
            var files = Walk(root, deep: true, job.Stop.Token);
            var cluster = GameCompressionNative.Cluster(root);
            var hard = HardDriveOf(root);
            var threads = Threads(Environment.ProcessorCount, hard, job.Idle);
            if (job.Compress) Compress(job, root, files, saved, cluster, threads);
            else Uncompress(job, root, files, saved, threads);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            job.Failed = "Exo couldn't finish. Try again.";
            AppLog.Warn($"Compress: {job.Game.Title} failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            string? next = null;
            lock (_gate)
            {
                if (_job == job) _job = null;
                if (job.Failed is { } failed) _failed[id] = failed;
                if (_queue.Count > 0) { next = _queue[0]; _queue.RemoveAt(0); }
                if (_job is null && next is null) PauseWatchLocked();
            }
            Changed?.Invoke(id);
            if (next is not null && services.Library.Find(next) is { } game && Begin(game, compress: true) is { } why) AppLog.Info($"Compress: {game.Title} skipped: {why}");
        }
    }

    private void Compress(Job job, string root, List<FileFact> files, Store.Game saved, long cluster, int threads)
    {
        var id = job.Game.Id;
        var algorithm = Parse(saved.Algorithm) is { } kept && saved.Status is Store.Status.Paused or Store.Status.Compressed ? kept : AlgorithmFor(root, files);
        var poor = saved.Poor.Count > 0 || saved.Estimate?.Sampled == true
            ? saved.Poor.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : GameCompression.Poor(SampleExtensions(root, files, algorithm, cluster, job.Stop.Token));
        var guard = GuardFor(job.Game, AntiCheatCheck.Of(job.Game, job.Stop.Token), saved.Consent);
        if (guard == Guard.Nothing) { job.Failed = "Exo can't compress this game."; return; }
        var plan = MakePlan(files, algorithm, cluster, guard, DirectStorage(files), poor);
        job.Total = plan.Files.Sum(f => f.Length) + plan.DoneBytes;
        Interlocked.Exchange(ref job.Done, plan.DoneBytes);
        Store.Update(id, g => g with { Status = Store.Status.Compressing, Algorithm = Name(algorithm), Poor = [.. poor.Order(StringComparer.Ordinal)] });
        AppLog.Info($"Compress: {job.Game.Title}: {plan.Files.Count} files, {Name(algorithm)}, {threads} thread(s){(guard == Guard.DataOnly ? ", data only (anti-cheat allowed)" : "")}.");
        var tally = Each(job, plan.Files, threads, file =>
        {
            var full = Path.Combine(root, file.Relative);
            return File.GetLastWriteTimeUtc(full) != file.WrittenUtc ? GameCompressionNative.Outcome.InUse : GameCompressionNative.Compress(full, algorithm);
        });
        var finished = !job.Stop.IsCancellationRequested && job.Failed is null;
        var after = Walk(root, deep: true, CancellationToken.None);
        var before = after.Sum(f => RoundUp(f.Length, cluster));
        var onDisk = after.Sum(f => f.OnDisk);
        Store.Update(id, g => g with
        {
            Status = finished ? Store.Status.Compressed : Store.Status.Paused,
            CompressedUtc = finished ? DateTime.UtcNow : g.CompressedUtc,
            Before = before,
            After = onDisk,
        });
        lock (_gate) _changed.Remove(id);
        AppLog.Info($"Compress: {job.Game.Title} {(finished ? "done" : "stopped")}: {(before - onDisk) / 1_000_000} MB saved; {tally}.");
    }

    private void Uncompress(Job job, string root, List<FileFact> files, Store.Game saved, int threads)
    {
        var compressed = files.Where(f => f.Current is not null).ToList();
        var grows = compressed.Sum(f => f.Length - f.OnDisk);
        var free = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        if (free < grows + GB) { job.Failed = $"Free {Math.Ceiling((grows + GB - free) / (double)GB)} GB on this drive first."; return; }
        job.Total = compressed.Sum(f => f.Length);
        Store.Update(job.Game.Id, g => g with { Status = Store.Status.Uncompressing });
        var tally = Each(job, compressed, threads, file => GameCompressionNative.Uncompress(Path.Combine(root, file.Relative)));
        var finished = !job.Stop.IsCancellationRequested && job.Failed is null;
        // Put back by the player: idle leaves it as it is from now on.
        Store.Update(job.Game.Id, g => g with { Status = finished ? Store.Status.Kept : null, Before = 0, After = 0, CompressedUtc = null });
        AppLog.Info($"Compress: {job.Game.Title} put back {(finished ? "" : "in part ")}; {tally}.");
    }

    /// <summary>The files on <paramref name="threads"/> threads below normal priority, each waiting while paused or held; what came of them.</summary>
    private string Each(Job job, IReadOnlyList<FileFact> files, int threads, Func<FileFact, GameCompressionNative.Outcome> one)
    {
        var next = -1;
        var outcomes = new int[Enum.GetValues<GameCompressionNative.Outcome>().Length];
        var lastSaid = Environment.TickCount64;
        void Work()
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < files.Count)
            {
                job.Go.Wait(job.Stop.Token);
                if (job.Stop.IsCancellationRequested) return;
                var outcome = one(files[i]);
                Interlocked.Increment(ref outcomes[(int)outcome]);
                Interlocked.Add(ref job.Done, files[i].Length);
                if (outcome == GameCompressionNative.Outcome.DiskFull) { job.Failed = "The drive is full."; job.Stop.Cancel(); return; }
                if (outcome == GameCompressionNative.Outcome.Unsupported) { job.Failed = "This drive can't compress files."; job.Stop.Cancel(); return; }
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastSaid) >= 750) { Interlocked.Exchange(ref lastSaid, now); Changed?.Invoke(job.Game.Id); }
            }
        }
        var workers = Enumerable.Range(0, Math.Max(1, threads)).Select(_ => new Thread(() =>
        {
            try { Work(); } catch (OperationCanceledException) { }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Exo compress file" }).ToList();
        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());
        return string.Join(", ", Enum.GetValues<GameCompressionNative.Outcome>().Where(o => outcomes[(int)o] > 0).Select(o => $"{o} {outcomes[(int)o]}"));
    }

    // ------------------------------------------------------------------ holds

    private void OnGameLaunching(GameEntry game)
    {
        Volatile.Write(ref _launchedAt, Environment.TickCount64);
        Job? job;
        lock (_gate) job = _job;
        if (job is null) return;
        job.Held = $"Waiting for {game.Title} to close.";
        job.Go.Reset();
        Changed?.Invoke(job.Game.Id);
    }

    /// <summary>
    /// An update or repair a store finished: a compressed game is looked at again at the next idle pass (what it wrote
    /// back, for Compress again), not six hours on.
    /// </summary>
    private void OnInstallProgress(InstallProgress progress)
    {
        if (progress.Phase != InstallPhase.Completed || Store.Of(progress.GameId).Status != Store.Status.Compressed) return;
        lock (_gate) _changed[progress.GameId] = (DateTime.MinValue, 0);
    }

    private long _launchedAt = long.MinValue / 2;
    private long _lastDeepLook = long.MinValue / 2;
    private long _lastIdleLook = long.MinValue / 2;
    private string? _blocked;
    private bool _notIdle;

    /// <summary>
    /// Every 2 s while a job runs: a game starting (30 s) or running holds it; every 10 s the game itself and its store;
    /// every 30 s an idle job asks whether the PC is still idle.
    /// </summary>
    private void Watch()
    {
        Job? job;
        lock (_gate)
        {
            job = _job;
            if (job is null)
            {
                PauseWatchLocked();
                return;
            }
        }
        string? held;
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastDeepLook >= 10_000) { _lastDeepLook = now; _blocked = Blocked(job.Game); }
            if (job.Idle && now - _lastIdleLook >= 30_000) { _lastIdleLook = now; _notIdle = !Quiet(); }
            var playing = services.Launcher.AnyGameRunning || GameRunningSignal.Running || LeanSignal.Active || now - Volatile.Read(ref _launchedAt) < 30_000;
            held = playing
                ? job.Held is { } starting && starting.StartsWith("Waiting for", StringComparison.Ordinal) ? starting : "Waiting for the game to close."
                : _blocked ?? (job.Idle && _notIdle ? "Waits until the PC is idle." : null);
        }
        catch (Exception ex) { AppLog.Debug("Compress: watch failed: " + ex.Message); return; }
        if (held == job.Held) return;
        job.Held = held;
        if (held is not null) job.Go.Reset();
        else if (!job.UserPaused) job.Go.Set();
        Changed?.Invoke(job.Game.Id);
    }

    /// <summary>The junk clean's rule: no game, call, download or install, and the PC really idle.</summary>
    private bool Quiet()
    {
        var launcher = services.Launcher;
        return !launcher.AnyGameRunning && !LeanSignal.Active && !GameRunningSignal.Running && DiscordClient.DiscordSession.Current?.InCall != true
            && !launcher.IsBusy && !launcher.HasQueuedJobs && !services.Updater.Installing && !DriverInstallSignal.Installing
            && PcIdle.Now(() => launcher.AnyGameRunning || LeanSignal.Active);
    }

    /// <summary>
    /// Every 2 minutes, only with a switch on, nothing running and the PC idle: first a compressed game an update changed
    /// (Compress again), then a paused one, then the game that saves most; a game not yet sampled is sampled first.
    /// </summary>
    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var settings = services.Settings.Current;
            if (!settings.CompressWhileIdle && !settings.RecompressAfterUpdates) return;
            lock (_gate) if (_job is not null || _queue.Count > 0) return;
            if (!Quiet()) return;
            var games = services.Library.PeekCachedLibrary().Where(g => g.Installed).ToList();
            if (settings.RecompressAfterUpdates)
            {
                foreach (var game in games.Where(g => Store.Of(g.Id).Status == Store.Status.Compressed))
                {
                    (DateTime At, long Bytes) looked;
                    lock (_gate) looked = _changed.GetValueOrDefault(game.Id);
                    if (DateTime.UtcNow - looked.At > TimeSpan.FromHours(6)) { Estimate(game, CancellationToken.None); return; }   // one look a tick
                    if (WorthAgain(looked.Bytes, Store.Of(game.Id).Before)) { Begin(game, compress: true, idle: true); return; }
                }
            }
            if (!settings.CompressWhileIdle) return;
            var rows = List();
            if (rows.FirstOrDefault(r => r.View.State == Store.Status.Paused).Game is { } paused) { Begin(paused, compress: true, idle: true); return; }
            if (rows.FirstOrDefault(r => r.View.State == "none" && !r.View.Sampled && r.View.EstimateBefore is not null && Gain(r.View) >= GB).Game is { } unsampled)
            {
                Estimate(unsampled, CancellationToken.None);
                return;
            }
            if (rows.Where(r => r.View.Sampled && Eligible(r.View)).OrderByDescending(r => Gain(r.View)).FirstOrDefault().Game is { } best)
                Begin(best, compress: true, idle: true);
        }
        catch (Exception ex) { AppLog.Warn("Compress: the idle pass failed: " + ex.Message); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    public void Dispose()
    {
        services.Launcher.GameLaunching -= OnGameLaunching;
        services.Launcher.ProgressChanged -= OnInstallProgress;
        Timer? tick;
        Timer? watch;
        lock (_gate)
        {
            tick = _tick;
            watch = _watch;
            _tick = _watch = null;
            _queue.Clear();
            if (_job is { } job) { job.Stop.Cancel(); job.Go.Set(); }
        }
        tick?.Dispose();
        watch?.Dispose();
    }
}

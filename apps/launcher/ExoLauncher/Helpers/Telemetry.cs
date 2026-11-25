using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

/// <summary>
/// App telemetry for the Launcher (misc/diagnostics/SPEC.md section 5), sent to reports.getexo.app by every install: the
/// owner decided on 2026-11-09 that app data and crash reports are essential and have no switch (PRIVACY.md says so, Settings
/// › Privacy shows what was sent and deletes it). Events are checked against their schema (section 8.1) and scrubbed
/// (section 3) before they are queued; a queued event waits on this PC (<c>telemetry\queue.jsonl</c>) until a batch goes
/// out, at most every five minutes, in requests of up to 64 KB. An event carries a random install ID, never an account,
/// a hardware ID or a name.
/// </summary>
internal static partial class Telemetry
{
    internal const string Endpoint = "https://reports.getexo.app/v1/telemetry";
    internal const string DeleteEndpoint = "https://reports.getexo.app/v1/telemetry/delete";
    internal const int MaxBatchBytes = 64 * 1024;
    internal const int MaxEventBytes = 1024;
    internal const long MaxQueueBytes = 1024 * 1024;
    internal const long MaxSentBytes = 256 * 1024;
    /// <summary>How many payloads "View what's sent" shows.</summary>
    internal const int SentShown = 20;
    internal static readonly TimeSpan MinGap = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);
    internal static readonly TimeSpan InstallLife = TimeSpan.FromDays(90);
    private static readonly TimeSpan PerfEvery = TimeSpan.FromMinutes(10);
    private const string Channel = "stable";

    private static readonly Lock Gate = new();
    private static readonly List<string> Queue = [];
    private static long _queueBytes;
    private static int _dropped;
    private static bool _queueDirty;
    private static DateTime _nextSend = DateTime.MinValue;
    private static DateTime _nextPerfSample = DateTime.MinValue;
    private static TimeSpan _backoff = MinGap;
    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var http = ExoHttp.Create(TimeSpan.FromSeconds(15), new System.Net.Http.SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/" + AppServices.RunningVersion);
        return http;
    });
    // The session ID is made once, on first use; null until a random source gives one (see RandomFill).
    private static string? _sessionId;

    /// <summary>
    /// The random bytes IDs are made from. The system generator, unless a test replaces it to make it fail. A call that throws
    /// (the source is unavailable) gives no ID, and so nothing is queued or sent that needs one.
    /// </summary>
    internal delegate void FillRandom(Span<byte> bytes);
    internal static FillRandom RandomFill = RandomNumberGenerator.Fill;
    private static readonly DateTime Started = DateTime.UtcNow;
    private static Timer? _ticks;
    private static bool _perfSampled;
    /// <summary>How a batch is posted (the URL and body). A test replaces it to make a send fail.</summary>
    internal static Func<string, CancellationToken, Task<int>> Poster = (body, ct) => PostAsync(Endpoint, body, ct);

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}", RegexOptions.CultureInvariant)] private static partial Regex VersionHead();

    private static readonly string[] SizeBuckets = ["under 1 MB", "1-99 MB", "100 MB-1 GB", "1 GB+"];
    private static readonly string[] MemoryBuckets = ["under 256", "256-511", "512-1023", "1024-2047", "2048+"];
    private static readonly string[] ErrorKinds = ["none", "network", "size", "hash", "install", "timeout", "other"];

    /// <summary>
    /// The schema of each event this app sends: its props, each with the rule a value must pass. Anything else is dropped
    /// (an unknown event is never sent; an unknown prop is dropped from its event). The Launcher's events from section 8.1.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, Func<object, object?>>> Schema = new(StringComparer.Ordinal)
    {
        ["app.start"] = new() { ["startup_ms"] = Number },
        ["app.exit"] = new() { ["session_s"] = Number, ["clean"] = Flag },
        ["action.used"] = new()
        {
            ["action"] = Of("install", "update", "launch", "scan", "capture", "frame_gen_on", "shader_on", "mouse_profile_apply", "activate_key"),
        },
        ["room.opened"] = new() { ["room"] = Of("home", "library", "store", "discord", "music", "pc", "devices", "settings") },
        ["library.loaded"] = new() { ["ms"] = Number, ["games"] = Of("0-49", "50-199", "200-999", "1000+"), ["stores"] = Number },
        ["download.finished"] = new()
        {
            ["kind"] = Of("game", "tool", "driver", "update", "runtime", "model"), ["ms"] = Number,
            ["bytes"] = Of(SizeBuckets), ["ok"] = Flag,
        },
        ["update.result"] = new() { ["from"] = VersionText, ["to"] = VersionText, ["ok"] = Flag, ["error_kind"] = Of(ErrorKinds) },
        ["store.sync_failed"] = new() { ["store"] = Of("steam", "epic", "gog", "amazon", "riot", "xbox", "other"), ["error_kind"] = Of(ErrorKinds) },
        ["perf.memory"] = new() { ["working_set_mb"] = Of(MemoryBuckets), ["private_mb"] = Of(MemoryBuckets) },
        ["telemetry.dropped"] = new() { ["count"] = Number },
    };

    private static Func<object, object?> Of(params string[] allowed) => value => value is string s && allowed.Contains(s, StringComparer.Ordinal) ? s : null;
    private static object? Flag(object value) => value is bool ? value : null;
    private static object? VersionText(object value) => value is string s && s.Length <= 32 && VersionHead().Match(s) is { Success: true } ? s : null;
    private static object? Number(object value) => value switch
    {
        int i => i,
        long l when Math.Abs((double)l) < 1e15 => l,
        double d when double.IsFinite(d) && Math.Abs(d) < 1e15 => d,
        _ => null,
    };

    /// <summary>What a string prop may hold: exactly what the scrubber (CrashReport.Scrub) leaves alone. A value it would change is dropped.</summary>
    private static object? Scrubbed(object? value) => value is string s
        ? CrashReport.Scrub(s, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.MachineName) == s ? s : null
        : value;

    /// <summary>
    /// Queues one event. Props the schema does not list, or whose value does not pass its rule, are dropped; an event with
    /// props where all drop is not sent; an event over 1 KB is dropped whole. Never throws.
    /// </summary>
    internal static void Track(string name, params (string Key, object Value)[] props)
    {
        try
        {
            if (!Schema.TryGetValue(name, out var rules)) return;
            var kept = new JsonObject();
            foreach (var (key, value) in props)
                if (rules.TryGetValue(key, out var rule) && Scrubbed(rule(value)) is { } ok) kept[key] = JsonValue.Create(ok);
            if (props.Length > 0 && kept.Count == 0) return;
            lock (Gate)
            {
                if (Envelope(name, kept) is not { } envelope) return;
                var text = envelope.ToJsonString();
                if (Encoding.UTF8.GetByteCount(text) > MaxEventBytes) return;
                Queue.Add(text);
                _queueBytes += Encoding.UTF8.GetByteCount(text);
                _queueDirty = true;
                while (_queueBytes > MaxQueueBytes && Queue.Count > 0)
                {
                    _queueBytes -= Encoding.UTF8.GetByteCount(Queue[0]);
                    Queue.RemoveAt(0);
                    _dropped++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Debug("Telemetry: event not queued: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// One event as the service takes it, or null when no install ID or session ID can be made: an event is never sent
    /// without the IDs it names, and no ID is made from anything but the random source.
    /// </summary>
    private static JsonObject? Envelope(string name, JsonObject props)
    {
        if (InstallId() is not { } install || SessionIdOrNull() is not { } session) return null;
        return new()
        {
            ["schema"] = 1,
            ["app"] = "launcher",
            ["version"] = VersionHead().Match(AppServices.RunningVersion).Value,
            ["channel"] = Channel,
            ["install_id"] = install,
            ["session_id"] = session,
            ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["event"] = name,
            ["props"] = props,
        };
    }

    /// <summary>This run's session ID, made on first use; null when the random source gives none.</summary>
    private static string? SessionIdOrNull()
    {
        lock (Gate) return _sessionId ??= RandomHex(8);
    }

    /// <summary>
    /// <paramref name="bytes"/> random bytes as lower-case hex, or null when the random source fails or gives nothing. The
    /// failure is logged; nothing is thrown.
    /// </summary>
    private static string? RandomHex(int bytes)
    {
        try
        {
            var buffer = new byte[bytes];
            RandomFill(buffer);
            return buffer.All(b => b == 0) ? null : Convert.ToHexString(buffer).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn($"Telemetry: no random source ({ex.GetType().Name}); no ID made.");
            return null;
        }
    }

    /// <summary>
    /// A random UUID version 4, or null when the random source fails. Built from the random bytes directly (version and variant
    /// bits set), so a failure is a null result rather than an exception that a caller might not expect.
    /// </summary>
    internal static string? NewUuid()
    {
        try
        {
            var bytes = new byte[16];
            RandomFill(bytes);
            if (bytes.All(b => b == 0)) return null;
            // Guid's string form reads bytes 6 and 7 as the third group (byte 7 is its high byte), and byte 8 starts the fourth.
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes).ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn($"Telemetry: no random source ({ex.GetType().Name}); no install ID made.");
            return null;
        }
    }

    // ---- Files (telemetry\) -----------------------------------------------------------------------------------------

    private static string Dir => Path.Combine(PathHelper.AppDataDir, "telemetry");
    private static string InstallPath => Path.Combine(Dir, "install.json");
    private static string QueuePath => Path.Combine(Dir, "queue.jsonl");
    private static string SentPath => Path.Combine(Dir, "sent.jsonl");

    /// <summary>Tests only: an empty queue and a fresh send schedule, as a new process starts with (the queue is static).</summary>
    internal static void ForgetQueuedForTests()
    {
        lock (Gate)
        {
            ClearQueueLocked();
            _nextSend = DateTime.MinValue;
            _nextPerfSample = DateTime.MinValue;
            _backoff = MinGap;
            _sessionId = null;
        }
    }

    private static void ClearQueueLocked()
    {
        Queue.Clear();
        _queueBytes = 0;
        _dropped = 0;
        _queueDirty = false;
        TryDelete(QueuePath);
    }

    // ---- Install ID (telemetry\install.json) ----------------------------------------------------------------------

    /// <summary>
    /// This PC's install ID for Launcher: a random UUID v4, kept until it is 90 days old or Delete my data makes a new one.
    /// Not derived from an account, hardware, MAC address, SID or disk.
    /// </summary>
    internal static string? InstallId()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(InstallPath) && JsonNode.Parse(File.ReadAllText(InstallPath)) is JsonObject root
                    && root["id"]?.GetValue<string>() is { } id && Guid.TryParse(id, out _)
                    && root["createdAt"]?.GetValue<string>() is { } created && DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                    && DateTime.UtcNow - at.ToUniversalTime() < InstallLife)
                    return id;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
            {
                AppLog.Debug("Telemetry: install ID unreadable: " + ex.GetType().Name);
            }
            return RotateInstallIdLocked();
        }
    }

    /// <summary>A new install ID, written to install.json; null (and nothing written) when the random source fails.</summary>
    private static string? RotateInstallIdLocked()
    {
        var id = NewUuid();
        if (id is null) return null;
        WriteAtomic(InstallPath, new JsonObject
        {
            ["id"] = id,
            ["createdAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        }.ToJsonString());
        return id;
    }

    // ---- Sending ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Loads the offline queue, rotates an old install ID and starts the five-minute send and the 10-minute memory sample.
    /// The memory sample runs in 10% of sessions only (SPEC section 8: perf events are sampled). The consent file of the
    /// builds that had switches (3.25) is removed: nothing reads it.
    /// </summary>
    internal static void Start()
    {
        try
        {
            lock (Gate)
            {
                TryDelete(Path.Combine(Dir, "consent.json"));
                InstallId();
                LoadQueueLocked();
                _perfSampled = RandomNumberGenerator.GetInt32(10) == 0;
                _nextPerfSample = DateTime.UtcNow + PerfEvery;
            }
            _ticks = new Timer(_ => Tick(), null, MinGap, MinGap);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Telemetry not started ({ex.GetType().Name}).");
        }
    }

    /// <summary>The queue saved by the last run.</summary>
    internal static void LoadQueue()
    {
        lock (Gate) LoadQueueLocked();
    }

    private static void LoadQueueLocked()
    {
        if (!File.Exists(QueuePath)) return;
        foreach (var line in File.ReadAllLines(QueuePath).Where(line => line.Length > 0 && line.Length <= MaxEventBytes))
        {
            Queue.Add(line);
            _queueBytes += Encoding.UTF8.GetByteCount(line);
        }
    }

    private static void Tick()
    {
        try
        {
            if (_perfSampled && ShouldSamplePerf(DateTime.UtcNow)) SampleMemory();
            _ = FlushAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Debug("Telemetry tick: " + ex.GetType().Name);
        }
    }

    internal static bool ShouldSamplePerf(DateTime now)
    {
        lock (Gate)
        {
            if (now < _nextPerfSample) return false;
            _nextPerfSample = now + PerfEvery;
            return true;
        }
    }

    private static void SampleMemory()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Track("perf.memory", ("working_set_mb", MemoryBucket(process.WorkingSet64)), ("private_mb", MemoryBucket(process.PrivateMemorySize64)));
    }

    /// <summary>A byte count as its bucket (download.finished's bytes).</summary>
    internal static string SizeBucket(long bytes) =>
        bytes < 1_000_000 ? SizeBuckets[0] : bytes < 100_000_000 ? SizeBuckets[1] : bytes < 1_000_000_000 ? SizeBuckets[2] : SizeBuckets[3];

    /// <summary>A game count as its bucket (SPEC 8.1, library.loaded).</summary>
    internal static string GameCountBucket(int games) => games < 50 ? "0-49" : games < 200 ? "50-199" : games < 1000 ? "200-999" : "1000+";

    /// <summary>A megabyte count as its bucket (the schema takes buckets, never exact sizes).</summary>
    internal static string MemoryBucket(long bytes)
    {
        var mb = bytes / 1048576;
        return mb < 256 ? "under 256" : mb < 512 ? "256-511" : mb < 1024 ? "512-1023" : mb < 2048 ? "1024-2047" : "2048+";
    }

    /// <summary>
    /// Sends what is queued, in requests of up to 64 KB, at most once every five minutes. A 2xx drops the batch and writes it to
    /// <c>sent.jsonl</c>; 429, 5xx and no answer keep it and wait (5 minutes, doubling to 6 hours); any other 4xx drops it.
    /// Returns when nothing more is sent this time. Never throws.
    /// </summary>
    internal static async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            lock (Gate) { if (DateTime.UtcNow < _nextSend) return; }
            var (body, envelopes, _, count, taken) = TakeBatch();
            if (body is null) return;
            var outcome = await PostOrNoAnswerAsync(body, ct).ConfigureAwait(false);
            lock (Gate) SettleLocked(envelopes, taken, count, outcome);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn($"Telemetry: not sent ({ex.GetType().Name}).");
        }
        finally
        {
            PersistQueue();
        }
    }

    /// <summary>
    /// The batch's answer decides it. A 2xx records it in sent.jsonl. 429, 5xx and no answer (0, including any exception) put
    /// it back in front of the queue with the count of events lost before it, and wait. Any other refusal drops it: the same
    /// events would only be refused again.
    /// </summary>
    private static void SettleLocked(List<string> envelopes, List<string> taken, int count, int outcome)
    {
        _nextSend = DateTime.UtcNow + MinGap;
        if (outcome is >= 200 and < 300)
        {
            _backoff = MinGap;
            AppendSentLocked(envelopes);
            return;
        }
        if (outcome is 429 or >= 500 or 0)
        {
            Queue.InsertRange(0, taken);
            _queueBytes += taken.Sum(text => (long)Encoding.UTF8.GetByteCount(text));
            _dropped += count;
            _queueDirty = true;
            _nextSend = DateTime.UtcNow + _backoff;
            _backoff = _backoff + _backoff > MaxBackoff ? MaxBackoff : _backoff + _backoff;
            AppLog.Info($"Telemetry: {taken.Count} events kept for a later try ({outcome}).");
            return;
        }
        AppLog.Warn($"Telemetry: a batch of {taken.Count} events was refused ({outcome}); dropped.");
    }

    /// <summary>Posts a batch. Any exception, the test seam's included, is no answer (0): the batch is kept, never lost.</summary>
    private static async Task<int> PostOrNoAnswerAsync(string body, CancellationToken ct)
    {
        try
        {
            return await Poster(body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn($"Telemetry: no answer ({ex.GetType().Name}); kept for a later try.");
            return 0;
        }
    }

    /// <summary>
    /// The batch that goes out next, as the request body, with the events it holds. A count of events lost to the queue cap is
    /// sent as one telemetry.dropped event first (SPEC section 5.5), and it is given back if the batch does not arrive.
    /// </summary>
    internal static (string? Body, List<string> Envelopes, string? Note, int Count, List<string> Taken) TakeBatch()
    {
        lock (Gate)
        {
            if (Queue.Count == 0 && _dropped == 0) return (null, [], null, 0, []);
            var count = _dropped;
            // The count of lost events travels in its own event; without the IDs that event needs, nothing goes out yet.
            string? note = null;
            if (count > 0)
            {
                if (Envelope("telemetry.dropped", new JsonObject { ["count"] = count }) is not { } dropped) return (null, [], null, 0, []);
                note = dropped.ToJsonString();
            }
            _dropped = 0;
            _queueDirty = true;
            var taken = new List<string>();
            var size = 32 + (note is null ? 0 : Encoding.UTF8.GetByteCount(note) + 1);
            while (Queue.Count > 0)
            {
                var next = Queue[0];
                var bytes = Encoding.UTF8.GetByteCount(next) + 1;
                if (size + bytes > MaxBatchBytes && taken.Count > 0) break;
                taken.Add(next);
                size += bytes;
                Queue.RemoveAt(0);
                _queueBytes -= Encoding.UTF8.GetByteCount(next);
            }
            var envelopes = note is null ? taken : new List<string> { note }.Concat(taken).ToList();
            return ("{\"schema\":1,\"events\":[" + string.Join(',', envelopes) + "]}", envelopes, note, count, taken);
        }
    }

    private static async Task<int> PostAsync(string url, string body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await SharedHttp.Value.PostAsync(url, content, ct).ConfigureAwait(false);
            return (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure is no answer: the batch is kept by the caller (SettleLocked), never dropped here.
            AppLog.Warn($"Telemetry: no answer ({ex.GetType().Name}).");
            return 0;
        }
    }

    /// <summary>app.exit (each exit), then the flush before exit.</summary>
    internal static void Exit(bool clean)
    {
        Track("app.exit", ("session_s", (long)(DateTime.UtcNow - Started).TotalSeconds), ("clean", clean));
        FlushAtExit();
    }

    /// <summary>Lets one flush go out before exit, for at most two seconds, then writes the queue. Never delays exit longer.</summary>
    internal static void FlushAtExit()
    {
        try
        {
            var flush = Task.Run(() => FlushAsync(CancellationToken.None));
            flush.Wait(TimeSpan.FromSeconds(2));
            lock (Gate) PersistQueueLocked();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("Telemetry exit: " + ex.GetType().Name); }
    }

    private static void PersistQueue() { lock (Gate) PersistQueueLocked(); }

    private static void PersistQueueLocked()
    {
        if (!_queueDirty) return;
        _queueDirty = false;
        if (Queue.Count == 0) TryDelete(QueuePath);
        else WriteAtomic(QueuePath, string.Join('\n', Queue) + "\n");
    }

    private static void AppendSentLocked(IEnumerable<string> envelopes)
    {
        var lines = (File.Exists(SentPath) ? File.ReadAllLines(SentPath) : []).Concat(envelopes).ToList();
        var total = lines.Sum(line => (long)Encoding.UTF8.GetByteCount(line) + 1);
        var first = 0;
        while (total > MaxSentBytes && first < lines.Count) { total -= Encoding.UTF8.GetByteCount(lines[first]) + 1; first++; }
        WriteAtomic(SentPath, string.Join('\n', lines.Skip(first)) + "\n");
    }

    /// <summary>
    /// A crash report the service accepted, recorded in sent.jsonl as one compact line so View what's sent shows it.
    /// </summary>
    internal static void RecordSent(string compactJson)
    {
        try
        {
            lock (Gate) AppendSentLocked([compactJson]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Telemetry: sent copy not written: " + ex.GetType().Name);
        }
    }

    /// <summary>"View what's sent": the last payloads exactly as they were sent, oldest first (read from sent.jsonl).</summary>
    internal static IReadOnlyList<string> Sent()
    {
        lock (Gate)
        {
            if (!File.Exists(SentPath)) return [];
            return File.ReadAllLines(SentPath).Where(line => line.Length > 0).TakeLast(SentShown).ToList();
        }
    }

    /// <summary>
    /// Delete my data: asks the server to delete this install's stored events and crash reports. On 202 the queue, the sent
    /// copy and the install ID are replaced; otherwise nothing local changes, so the ID can ask again. Null when done, else
    /// the words to show.
    /// </summary>
    internal static async Task<string?> DeleteAsync(CancellationToken ct)
    {
        string body;
        lock (Gate)
        {
            if (InstallId() is not { } install) return "Exo couldn't make an install ID on this PC, so nothing was deleted.";
            body = new JsonObject { ["schema"] = 1, ["app"] = "launcher", ["install_id"] = install }.ToJsonString();
        }
        var status = await PostAsync(DeleteEndpoint, body, ct).ConfigureAwait(false);
        if (status != 202) return "Exo couldn't reach its server, so nothing was deleted. Try again.";
        lock (Gate)
        {
            ClearQueueLocked();
            TryDelete(SentPath);
            RotateInstallIdLocked();
        }
        return null;
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { DeletionGate.DeleteFile(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

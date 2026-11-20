using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The shader add-on for System > Shaders (Exo Shaders, GPL-3.0-or-later; its source is this repository's
/// <c>shaders\</c>, published at getexo.app/source): its own program, shipped in Exo's app folder under
/// <c>addons\shaders</c> or installed beside Exo's data under <c>%LOCALAPPDATA%\ExoLauncher\addons\shaders</c>: an engine
/// (<c>engine\ExoShaders.Service.exe</c>) that Exo's own Shaders page drives. Exo runs no part of it in-process. It starts the engine hidden at below-normal
/// priority, speaks newline-delimited JSON over its stdin and stdout, and passes
/// requests and events between the engine and Exo's Shaders page. Without the add-on, Shaders says it is not installed.
/// The engine starts when you press Compile or Refresh on the page (<see cref="StartMethods"/>), and for
/// <see cref="ShaderRebuildWatch"/> while nobody uses the PC (a driver change, a new install or a game update to compile):
/// never for the page opening, a game exiting or the time of day. It exits by itself
/// <see cref="IdleExitSeconds"/> after its last scan or compile ends, and leaves its last state for the page.
/// </summary>
internal sealed class ShadersAddon : IDisposable
{
    /// <summary>
    /// What the add-on's page (ShadersBoard) may ask its engine: read the board, say it is on screen, look for installed
    /// games again (Refresh), and queue, pause, resume, stop or compile games it lists. The engine can do far more (add a
    /// program or folder by path, install its recorder, delete caches, change its settings, stop itself): those are Exo's to
    /// ask, never the page's. ShadersPageMethodsTests checks every method the page calls is here.
    /// </summary>
    internal static readonly IReadOnlySet<string> PageMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "snapshot", "ui.visible", "scan", "queue.add", "queue.move", "queue.remove", "queue.start", "queue.pause", "queue.resume",
        "queue.stop", "game.compile", "game.diagnostics", "recorder.auto", "recorder.set",
        "driver.setCacheLimit", "driver.setCleanupGuard", "cache.freeOldDriver", "driver.setAutoShader",
    };

    /// <summary>
    /// The page's methods that start a stopped engine: Refresh (scan), Compile (game.compile; queue.add then queue.start;
    /// queue.resume), and what you press on the page for recording and how compiles did. Every other
    /// one is answered without it: the board from the last state it left, the rest as done.
    /// </summary>
    internal static readonly IReadOnlySet<string> StartMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "scan", "game.compile", "queue.add", "queue.start", "queue.resume", "game.diagnostics", "recorder.auto", "recorder.set",
        "driver.setCacheLimit", "driver.setCleanupGuard", "cache.freeOldDriver", "driver.setAutoShader",
    };

    /// <summary>An add-on installed beside Exo's data (a newer or development build) wins over the copy that ships in Exo's app folder.</summary>
    internal static string Root => File.Exists(Path.Combine(LocalRoot, "engine", "ExoShaders.Service.exe")) ? LocalRoot : BundledRoot;

    private static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "addons", "shaders");

    /// <summary>The release installer carries Exo Shaders under <c>addons\shaders</c> in the app folder.</summary>
    private static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "addons", "shaders");
    internal static string EnginePath => Path.Combine(Root, "engine", "ExoShaders.Service.exe");

    /// <summary>The engine is on this PC.</summary>
    internal static bool Present => File.Exists(EnginePath);

    /// <summary>The engine exits with this when another engine (SCSKiller's own app) holds the shader data.</summary>
    private const int OtherInstanceExit = 3;

    /// <summary>The engine exits by itself once nothing has needed it for this long (its exitWhenIdle, at least 5).</summary>
    internal const int IdleExitSeconds = 15;

    /// <summary>The page's methods that compile: refused while a graphics driver installs (the swap would crash the compile).</summary>
    private static readonly IReadOnlySet<string> CompileMethods = new HashSet<string>(StringComparer.Ordinal) { "game.compile", "queue.start", "queue.resume" };

    private static ShadersAddon? _live;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, (Process Engine, TaskCompletionSource<JsonNode?> Reply)> _pending = new();
    private Process? _engine;
    // The engine that sent its "exiting" event: it refuses new work, then exits. A press waits for it and starts the next.
    private Process? _leaving;
    private long _nextId;
    private bool _disposed;

    /// <summary>Exo makes one, the first time Shaders or its engine is asked for.</summary>
    internal ShadersAddon() : this(EnginePath, []) { }

    /// <summary>Tests: another engine program and its arguments (the built service, <c>--data-dir</c> and <c>--safe</c>).</summary>
    internal ShadersAddon(string enginePath, IReadOnlyList<string> engineArgs)
    {
        _enginePath = enginePath;
        _engineArgs = engineArgs;
        Volatile.Write(ref _live, this);
    }

    private readonly string _enginePath;
    private readonly IReadOnlyList<string> _engineArgs;

    /// <summary>The add-on Exo made, or null: none yet means its engine never ran.</summary>
    internal static ShadersAddon? Live => Volatile.Read(ref _live);

    /// <summary>
    /// Whether the engine compiles a game now: asked of the running engine (its board's status), so a Refresh, a plan check
    /// or a paused queue isn't one. False when it doesn't run, or doesn't answer within 5 seconds.
    /// </summary>
    internal async Task<bool> CompilingAsync(CancellationToken ct)
    {
        if (!Running)
        {
            ShaderCacheLock.ReportEngineCompiling(false, DateTime.UtcNow);
            return false;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var compiling = Compiling(await CallAsync("snapshot", null, timeout.Token).ConfigureAwait(false));
            ShaderCacheLock.ReportEngineCompiling(compiling, DateTime.UtcNow);
            return compiling;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            AppLog.Debug("Shaders add-on compile state: " + ex.Message);
            return false;
        }
    }

    /// <summary>The engine's board says it compiles (app.status "compiling"; not scanning, checking, paused or idle), or runs its short check of a compile (app.verifying).</summary>
    internal static bool Compiling(JsonNode? snapshot) =>
        snapshot?["app"]?["status"] is JsonValue status && status.TryGetValue<string>(out var text) && text == "compiling" ||
        snapshot?["app"]?["verifying"] is JsonNode verifying && !(verifying is JsonValue flag && flag.TryGetValue<bool>(out var on) && !on);

    /// <summary>notRunning, starting, running, blocked (SCSKiller's app is open), failed.</summary>
    internal string State { get; private set; } = "notRunning";
    internal string? Message { get; private set; }

    /// <summary>The engine runs now and has not announced it is exiting.</summary>
    internal bool Running { get { lock (_gate) return _engine is { HasExited: false } && !ReferenceEquals(_leaving, _engine); } }

    internal static async Task<(bool UsedAtRest, JsonNode? Result)> InvokeWithStoppingFallbackAsync(
        string method,
        bool starts,
        Func<bool> isRunning,
        Func<Task<JsonNode?>> invoke,
        Func<JsonNode?> atRest)
    {
        if (!starts && !isRunning()) return (true, atRest());
        try { return (false, await invoke().ConfigureAwait(false)); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) when (!starts)
        {
            // Between the check and the call, the engine started stopping or exited.
            AppLog.Debug("Exo Shaders call " + method + " while stopping: " + ex.Message);
            return (true, atRest());
        }
    }

    /// <summary>An engine event line (<c>{"event":..., "data":...}</c>), or a state change of Exo's own (<c>addon</c>).</summary>
    internal event Action<string, JsonNode?>? Event;

    /// <summary>Starts the engine if it isn't running. Safe to call often. <paramref name="reason"/> goes to the log.</summary>
    private void Start(string reason)
    {
        lock (_gate)
        {
            if (_disposed || !File.Exists(_enginePath) || _engine is { HasExited: false }) return;
            SetState("starting", null);
            try
            {
                var info = new ProcessStartInfo(_enginePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(_enginePath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardInputEncoding = new UTF8Encoding(false),
                };
                foreach (var arg in _engineArgs) info.ArgumentList.Add(arg);
                var engine = Process.Start(info) ?? throw new InvalidOperationException("The shader engine did not start.");
                try { engine.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var reading = Task.Run(() => ReadLoop(engine));
                engine.EnableRaisingEvents = true;
                engine.Exited += (_, _) => OnExited(engine, reading);
                engine.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Debug("Shaders engine: " + Trim(e.Data, 300)); };
                engine.BeginErrorReadLine();
                _engine = engine;
                AppLog.Info("Shaders add-on engine started for " + reason + ": pid " + engine.Id + ".");
                SetState("running", null);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Shaders add-on engine failed to start: " + ex.Message);
                SetState("failed", "The shader engine could not start.");
            }
        }
    }

    /// <summary>
    /// One request to the engine; the reply's <c>result</c>, or an exception with its <c>error</c>. Only a running engine is
    /// asked, unless <paramref name="startFor"/> (what the user pressed, for the log) starts a stopped one.
    /// </summary>
    internal async Task<JsonNode?> CallAsync(string method, JsonNode? parameters, CancellationToken ct, string? startFor = null)
    {
        if (DriverInstallSignal.Installing && CompileMethods.Contains(method))
            throw new InvalidOperationException("Your graphics driver is updating. Compile when it's done.");
        if (startFor is null) return await SendAsync(method, parameters, ct, null).ConfigureAwait(false);
        // A press that meets an engine on its way out (its idle wait ended a moment ago) goes to the next one, once.
        if (Leaving() is { } leaving) await GoneAsync(leaving, ct).ConfigureAwait(false);
        try { return await SendAsync(method, parameters, ct, startFor).ConfigureAwait(false); }
        catch (InvalidOperationException) when (Leaving() is { } left)
        {
            await GoneAsync(left, ct).ConfigureAwait(false);
            return await SendAsync(method, parameters, ct, startFor).ConfigureAwait(false);
        }
    }

    private Process? Leaving() { lock (_gate) return _leaving; }

    /// <summary>Waits for an exiting engine to be gone: its quit waits up to two minutes for a running compile to stop.</summary>
    private static async Task GoneAsync(Process engine, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try { await engine.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("The shader engine is still stopping. Try again in a minute."); }
        catch (InvalidOperationException) { }   // it is gone already
    }

    private async Task<JsonNode?> SendAsync(string method, JsonNode? parameters, CancellationToken ct, string? startFor)
    {
        Process? engine;
        lock (_gate) engine = _engine is { HasExited: false } live ? live : null;
        if (engine is null)
        {
            if (startFor is null) throw new InvalidOperationException("The shader engine is not running.");
            // Compile and Refresh arrive on the UI thread: the engine's process starts off it.
            await Task.Run(() => Start(startFor)).ConfigureAwait(false);
            lock (_gate) engine = _engine is { HasExited: false } live ? live : null;
            if (engine is null) throw new InvalidOperationException(Message ?? "The shader engine is not running.");
        }
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (engine, reply);
        try
        {
            var line = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() ?? new JsonObject() }.ToJsonString();
            lock (_gate)
            {
                engine.StandardInput.WriteLine(line);
                engine.StandardInput.Flush();
            }
            using var reg = ct.Register(() => reply.TrySetCanceled(ct));
            return await reply.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoop(Process engine)
    {
        try
        {
            while (await engine.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); } catch { AppLog.Debug("Shaders engine wrote a line that isn't JSON."); continue; }
                if (node is not JsonObject message) continue;
                // The line's own nodes are handed on, taken out of it (a board can be hundreds of KB, four times a second).
                if (message["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id))
                {
                    if (!_pending.TryGetValue(id, out var pending) || !ReferenceEquals(pending.Engine, engine)) continue;
                    var reply = pending.Reply;
                    if (message["error"] is { } error) reply.TrySetException(new InvalidOperationException(Trim(error.ToString(), 400)));
                    else reply.TrySetResult(Detach(message, "result"));
                }
                else if (message["event"]?.GetValue<string>() is { } name)
                {
                    if (name == "exiting") lock (_gate) _leaving = engine;
                    Event?.Invoke(name, Detach(message, "data"));
                }
            }
        }
        catch (Exception ex) { AppLog.Debug("Shaders engine read ended: " + ex.Message); }
    }

    private void OnExited(Process engine, Task reading)
    {
        int code;
        try { code = engine.ExitCode; } catch { code = -1; }
        // Its last lines (replies, its exiting event) are read before the requests still open are failed.
        try { reading.Wait(TimeSpan.FromSeconds(2)); } catch { }
        bool current;
        lock (_gate)
        {
            current = _engine is null || ReferenceEquals(_engine, engine) || _engine.HasExited;
            if (ReferenceEquals(_engine, engine)) _engine = null;
            if (ReferenceEquals(_leaving, engine)) _leaving = null;
        }
        var blocked = code == OtherInstanceExit ? "SCSKiller is open. Quit it from its tray icon, then try again." : null;
        // Only this engine's own requests fail: a press may already have started the next one.
        foreach (var pending in _pending.Values.Where(p => ReferenceEquals(p.Engine, engine)))
            pending.Reply.TrySetException(new InvalidOperationException(blocked ?? "The shader engine stopped."));
        if (_disposed) return;
        AppLog.Info("Shaders add-on engine exited: code " + code + ".");
        if (!current) return;
        // The next engine knows none of Exo's games.
        Volatile.Write(ref _syncedGames, null);
        if (blocked is not null) SetState("blocked", blocked);
        else SetState(code == 0 ? "notRunning" : "failed", code == 0 ? null : "The shader engine stopped.");
    }

    private void SetState(string state, string? message)
    {
        State = state;
        Message = message;
        Event?.Invoke("addon", new JsonObject { ["state"] = state, ["message"] = message });
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>A property's node, taken out of its line so it can go on without a copy.</summary>
    private static JsonNode? Detach(JsonObject message, string name)
    {
        var node = message[name];
        message.Remove(name);
        return node;
    }

    private static string StoreName(Models.StoreKind store) => store switch
    {
        Models.StoreKind.Epic => "Epic Games",
        Models.StoreKind.Gog => "GOG",
        Models.StoreKind.Xbox => "Xbox",
        Models.StoreKind.Ea => "EA app",
        Models.StoreKind.Ubisoft => "Ubisoft Connect",
        Models.StoreKind.BattleNet => "Battle.net",
        Models.StoreKind.Amazon => "Amazon Games",
        Models.StoreKind.Rockstar => "Rockstar Games",
        Models.StoreKind.Itch => "itch.io",
        Models.StoreKind.Local => "Added in Exo",
        _ => store.ToString(),
    };

    private int _syncPending;
    private int _syncForced;
    private string? _syncedGames;

    /// <summary>
    /// Exo's installed games (every installed store copy), so a running engine can compile
    /// games its own launcher readers miss. Coalesced: library updates come in bursts.
    /// Most library updates (cover warms, size measurements) leave the installed
    /// list as it was; that list is not sent again unless the engine just started
    /// (<paramref name="force"/>, its "ready" event). A stopped engine is never started for
    /// it: the next one is sent the list when ready. An engine without exo.library answers
    /// with an error, which is ignored.
    /// </summary>
    internal void SyncLibrary(Func<IReadOnlyList<Models.GameEntry>> library, bool force = false)
    {
        if (force) Interlocked.Exchange(ref _syncForced, 1);
        if (_disposed || !Running || Interlocked.Exchange(ref _syncPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Interlocked.Exchange(ref _syncPending, 0);
            var forced = Interlocked.Exchange(ref _syncForced, 0) == 1;
            try
            {
                var games = new JsonArray();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Riot's games are never handed to the engine.
                foreach (var g in library().Where(g => !AntiCheatCheck.IsRiot(g)))
                {
                    var noRecorder = KeepsRecorderOut(g);
                    var copies = g.Variants.Count > 0
                        ? g.Variants.Select(v => (v.Id, v.Store, v.Installed, v.Path, v.LaunchTarget))
                        : [(g.Id, g.Store, g.Installed, g.Path, g.LaunchTarget)];
                    foreach (var (id, store, installed, path, target) in copies)
                    {
                        if (!installed || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || !seen.Add(path)) continue;
                        var exe = target is { } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(t) ? t : null;
                        var entry = new JsonObject { ["id"] = id, ["title"] = g.Title, ["store"] = StoreName(store), ["installDir"] = path, ["exe"] = exe };
                        if (noRecorder) entry["noRecorder"] = true;
                        games.Add(entry);
                    }
                }
                var payload = games.ToJsonString();
                if (!Running || !forced && payload == Volatile.Read(ref _syncedGames)) return;
                try { await CallAsync("exo.library", new JsonObject { ["games"] = games }, CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) when (Running) { }   // an engine without exo.library: it has the list all the same
                Volatile.Write(ref _syncedGames, payload);
            }
            catch (Exception ex)
            {
                // The engine stopped before it took the list: the next one is sent it when ready.
                AppLog.Debug("Shaders add-on library sync: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// Whether the engine must keep its recorder out of this game between Exo's sessions (exo.library's <c>noRecorder</c>):
    /// Exo's anti-cheat answer refuses it (anti-cheat found, or one Exo can't read), or the game is a Steam game set to
    /// start without its anti-cheat, whose default entry still runs it, so the recorder is in only for a session Exo starts
    /// (<see cref="DlssSwapService"/>'s session mods, which let it in with <c>recorder.set {session: true}</c>). With it, the
    /// engine's "Record games" never puts the recorder back between sessions.
    /// </summary>
    internal static bool KeepsRecorderOut(Models.GameEntry game, Func<Models.GameEntry, AntiCheatCheck.Answer>? check = null) =>
        AntiCheatCheck.WithoutAntiCheatLaunch(game) is not null ||
        !AntiCheatCheck.Allows(game, (check ?? AntiCheatCheck.Cached)(game), AntiCheatCheck.Change.ShaderRecorder);

    /// <summary>
    /// Why the page may not turn the recorder on for this game, or null: the recorder puts a file beside the game's program,
    /// so a game with anti-cheat, or one Exo doesn't know or can't check, never gets it (<see cref="AntiCheatCheck"/>).
    /// Turning it off is always allowed.
    /// </summary>
    internal static string? RecorderRefusal(string method, JsonNode? parameters, Func<string, Models.GameEntry?> find, Func<Models.GameEntry, AntiCheatCheck.Answer> check)
    {
        if (method != "recorder.set" || parameters?["on"] is not JsonValue on || !on.TryGetValue<bool>(out var turnOn) || !turnOn) return null;
        var id = parameters?["id"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (string.IsNullOrWhiteSpace(id) || find(id) is not { } game) return "Exo could not check this game for anti-cheat, so it adds no recorder.";
        var answer = check(game);
        if (AntiCheatCheck.Allows(game, answer, AntiCheatCheck.Change.ShaderRecorder)) return null;
        AppLog.Info($"Shader recorder refused: gameId={game.Id}; anti-cheat {answer.Name ?? "unknown"} ({answer.Evidence}).");
        return answer.Kind == AntiCheatCheck.Kind.Unknown
            ? "Exo could not check this game for anti-cheat, so it adds no recorder."
            : $"This game uses {answer.Name}, so Exo adds no recorder.";
    }

    private volatile bool _uiVisible;

    /// <summary>Whether the add-on's page is on screen, as the page last told the engine.</summary>
    internal bool UiVisible => _uiVisible;

    /// <summary>The page opened or closed.</summary>
    internal void SetUiVisible(bool visible) => _uiVisible = visible;

    /// <summary>The engine's last state, which it leaves when it exits (<c>exo-snapshot.json</c>, rev 0, no queue): the
    /// page shows it until the running engine's own states replace it. Null when there is none.</summary>
    internal static JsonNode? AtRest()
    {
        try
        {
            var path = Path.Combine(ScsKillerStatus.DataDir, "exo-snapshot.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Asks the engine to stop its queue and exit, as SCSKiller's own app does on quit; waits a little.</summary>
    public void Dispose()
    {
        Process? engine;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            engine = _engine;
        }
        Interlocked.CompareExchange(ref _live, null, this);
        if (engine is null) return;
        try
        {
            if (!engine.HasExited)
            {
                engine.StandardInput.WriteLine(JsonSerializer.Serialize(new { id = 0, method = "shutdown", @params = new { } }));
                engine.StandardInput.Close();
                if (!engine.WaitForExit(8000))
                    AppLog.Warn("Shaders add-on engine still finishing after Exo closed; it exits on its own.");
            }
        }
        catch (Exception ex) { AppLog.Debug("Shaders add-on engine shutdown: " + ex.Message); }
        finally { engine.Dispose(); }
    }
}

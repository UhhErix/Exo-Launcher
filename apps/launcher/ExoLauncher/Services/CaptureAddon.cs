using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Exo Capture (instant replay, recording, snips): its own program, under Exo's own usage terms
/// (<c>capture/LICENSE</c>), source in <c>capture/</c>; the OBS game-capture hook it runs stays GPL-2.0-or-later, a separate file. Exo's core never captures the screen (BannedSymbols.txt);
/// all of that lives in this add-on, shipped under <c>addons\capture</c> in the app folder
/// or installed beside Exo's data under <c>%LOCALAPPDATA%\ExoLauncher\addons\capture</c>.
/// Exo starts it hidden and speaks newline-delimited JSON over its stdin and stdout:
/// anonymous pipes only the two processes hold, so no other program can reach it
/// (no port, no named pipe); Go Live's frames come back on one more such pipe
/// (<see cref="ShareHandle"/>). Exo closes it on exit, which also closes a recording.
/// </summary>
internal sealed class CaptureAddon(Func<ProcessStartInfo?> getStartInfo) : IDisposable
{
    internal CaptureAddon() : this(DefaultStartInfo) { }

    /// <summary>
    /// The newer of the copy installed beside Exo's data (a development build) and the one in the app folder: an old copy
    /// left there kept every later Exo on it.
    /// </summary>
    internal static string Root => Newer(LocalRoot, BundledRoot);

    internal static string Newer(string local, string bundled)
    {
        var mine = new FileInfo(Path.Combine(local, "ExoCapture.exe"));
        var shipped = new FileInfo(Path.Combine(bundled, "ExoCapture.exe"));
        if (!mine.Exists) return bundled;
        return !shipped.Exists || mine.LastWriteTimeUtc >= shipped.LastWriteTimeUtc ? local : bundled;
    }

    private static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "addons", "capture");

    private static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "addons", "capture");
    internal static string ExePath => Path.Combine(Root, "ExoCapture.exe");
    internal static bool Present => File.Exists(ExePath);

    private readonly object _gate = new();
    private sealed class Run(Process process, long generation)
    {
        internal readonly Process Process = process;
        internal readonly long Generation = generation;
        internal readonly SemaphoreSlim Writer = new(1, 1);
        internal readonly ConcurrentDictionary<long, Request> Pending = new();
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool SupportsReplayBudget;
        internal volatile bool Ready;
        internal volatile bool Ended;
    }

    private sealed record Request(TaskCompletionSource<JsonNode?> Reply, bool ApplyState);

    private Run? _run;
    private long _generation;
    private long _exitedAt;
    private long _nextId;
    private bool _disposed;
    private int _restarts;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    internal long? ReadyGeneration
    {
        get { lock (_gate) return !_disposed && _run is { Ready: true, Ended: false } run ? run.Generation : null; }
    }

    internal bool SupportsReplayBudget
    {
        get { lock (_gate) return _run is { Ready: true, Ended: false, SupportsReplayBudget: true }; }
    }

    /// <summary>An add-on event (<c>ready</c>, <c>state</c>, <c>saved</c>, <c>action</c>, <c>failed</c>), or <c>exited</c>.</summary>
    internal event Action<string, JsonNode?>? Event;

    private static ProcessStartInfo? DefaultStartInfo() => Present ? new ProcessStartInfo(ExePath)
    {
        // Not the app folder, so a running add-on never holds it during an update.
        WorkingDirectory = Path.GetTempPath(),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardInputEncoding = new UTF8Encoding(false),
    } : null;

    internal void Start()
    {
        lock (_gate)
        {
            // After a second crash it rests: nothing starts it again for 30 seconds (no crash loop), then what needs it
            // (Go Live, a recording, a page) may, so one bad moment doesn't leave it down until Exo restarts.
            if (_disposed || _run is not null) return;
            if (_restarts > 1 && Environment.TickCount64 - _exitedAt < 30_000) return;
            try
            {
                if (getStartInfo() is not { } info) return;
                var process = Process.Start(info) ?? throw new InvalidOperationException("Exo Capture did not start.");
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Info("Capture: " + Trim(e.Data, 300)); };
                var run = _run = new Run(process, ++_generation);
                _ = Task.Run(() => ReadLoop(run));
                process.BeginErrorReadLine();
                AppLog.Info("Capture add-on started: pid " + process.Id + ".");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Capture add-on failed to start: " + ex.Message);
            }
        }
    }

    private Run Running()
    {
        Start();
        lock (_gate)
            return !_disposed && _run is { Ended: false } run && !run.Process.HasExited
                ? run : throw new InvalidOperationException("Exo Capture is not running.");
    }

    /// <summary>
    /// One of Exo's handles duplicated into the add-on (Go Live's pipe), and its value there; the add-on
    /// owns and closes that copy. Still only the two processes hold it.
    /// </summary>
    internal nint ShareHandle(SafeHandle handle)
    {
        var process = Running().Process;
        if (!DuplicateHandle(GetCurrentProcess(), handle, process.Handle, out var copy, 0, false, 2 /* DUPLICATE_SAME_ACCESS */))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return copy;
    }

    /// <summary>
    /// Lets the add-on bring its own window to the front (the snip overlay, which needs the keyboard for Esc and Enter).
    /// Windows gives the foreground only to the process the user is in, so Exo, in front when its Snip is pressed, passes it on.
    /// </summary>
    internal void AllowForeground() => AllowSetForegroundWindow((uint)Running().Process.Id);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(nint sourceProcess, SafeHandle source, nint targetProcess, out nint target, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    internal async Task<JsonNode?> CallAsync(string method, JsonNode? parameters, CancellationToken ct, long? generation = null,
        bool applyState = false)
    {
        ct.ThrowIfCancellationRequested();
        var run = Running();
        if (generation is long expected && (run.Generation != expected || !run.Ready))
            throw Stopped();
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Pending[id] = new Request(reply, applyState);
        try
        {
            if (run.Ended) throw Stopped();
            var line = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() ?? new JsonObject() }.ToJsonString();
            using var writeLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            writeLimit.CancelAfter(WriteTimeout);
            var writeToken = writeLimit.Token;
            var write = Task.Run(() => WriteAsync(run, line, writeToken));
            _ = IgnoreWriteFailureAsync(write);
            // EOF must release callers even if a pipe write is still blocked in Windows.
            var first = await Task.WhenAny(write, reply.Task).WaitAsync(writeToken).ConfigureAwait(false);
            if (first == reply.Task) return await reply.Task.ConfigureAwait(false);
            await write.ConfigureAwait(false);
            writeLimit.CancelAfter(Timeout.InfiniteTimeSpan);
            return await reply.Task.WaitAsync(ReplyTimeout(method), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw NotResponding(); }
        catch (TimeoutException) { throw NotResponding(); }
        catch (IOException) { throw Stopped(); }
        catch (ObjectDisposedException) { throw Stopped(); }
        finally { run.Pending.TryRemove(id, out _); }
    }

    private static TimeSpan ReplyTimeout(string method) => method switch
    {
        "export" or "shrink" or "concat" or "trim" => Timeout.InfiniteTimeSpan,
        "snip" => TimeSpan.FromMinutes(5),
        "replay.save" or "record.toggle" => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(10),
    };

    private static InvalidOperationException Stopped() => new("Exo Capture stopped.");
    private static InvalidOperationException NotResponding() =>
        new("Exo Capture didn't respond. Check Capture status before trying again.");

    private static async Task IgnoreWriteFailureAsync(Task write) =>
        await write.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private static async Task WriteAsync(Run run, string line, CancellationToken ct)
    {
        await run.Writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (run.Ended) throw Stopped();
            run.Process.StandardInput.WriteLine(line);
            run.Process.StandardInput.Flush();
        }
        finally { run.Writer.Release(); }
    }

    private async Task ReadLoop(Run run)
    {
        try
        {
            while (await run.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                try { ReadMessage(run, line); }
                catch (ArgumentException) { } // JSON can have duplicate object keys.
            }
        }
        catch (Exception ex) { AppLog.Debug("Capture add-on read ended: " + ex.Message); }
        finally { await EndRunAsync(run).ConfigureAwait(false); }
    }

    private void ReadMessage(Run run, string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); } catch (System.Text.Json.JsonException) { return; }
        if (node is not JsonObject message) return;
        if (message["id"] is JsonValue id && id.TryGetValue<double>(out var rawId))
        {
            if (rawId != Math.Truncate(rawId) || !run.Pending.TryGetValue((long)rawId, out var request)) return;
            var reply = request.Reply;
            if (message["error"] is JsonValue error && error.TryGetValue<string>(out var reason))
                reply.TrySetException(new InvalidOperationException(Trim(reason, 300)));
            else if (message["error"] is not null)
                reply.TrySetException(new InvalidOperationException("Exo Capture returned an unreadable reply."));
            else
            {
                var result = Detach(message, "result");
                if (request.ApplyState && result is JsonObject state &&
                    state["buffering"] is JsonValue && state["recording"] is JsonValue)
                    Report("state", state.DeepClone());
                reply.TrySetResult(result);
            }
        }
        else if (message["event"] is JsonValue evt && evt.TryGetValue<string>(out var name))
        {
            var data = Detach(message, "data");
            if (name == "ready")
            {
                run.SupportsReplayBudget = data is JsonObject ready && ready["replayMemoryBudget"] is JsonValue budget
                    && budget.TryGetValue<bool>(out var supported) && supported;
                run.Ready = true;
            }
            Report(name, data);
        }
    }

    private async Task EndRunAsync(Run run)
    {
        run.Ended = true;
        foreach (var pending in run.Pending.Values) pending.Reply.TrySetException(Stopped());
        bool closing;
        lock (_gate) closing = _disposed;
        // Stdout can close before the process exits. Let it finish on stdin EOF; never kill a recording.
        var process = run.Process;
        if (!closing)
        {
            Report("exited", null);
            _ = Task.Run(() => CloseInputAsync(run, shutdown: false));
        }
        int code;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            code = process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { code = -1; }
        var restart = false;
        lock (_gate)
        {
            _run = null;
            _exitedAt = Environment.TickCount64;
            if (!_disposed) restart = ++_restarts == 1;
            closing = _disposed;
        }
        process.Dispose();
        run.Completion.TrySetResult();
        if (closing) return;
        AppLog.Warn("Capture add-on exited: code " + code + ".");
        if (FailureReportRules.IsCaptureCrash(code, stoppedByExo: false))
            CrashReport.Submit("CaptureCrash", "Exo Capture stopped with exit code " + code, "capture");
        if (restart)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Start();
        }
    }

    private void Report(string name, JsonNode? data)
    {
        try { Event?.Invoke(name, data); }
        catch (Exception ex) { AppLog.Warn("Capture event " + name + ": " + ex.Message); }
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>A property's node, taken out of its line so it can go on without a copy.</summary>
    private static JsonNode? Detach(JsonObject message, string name)
    {
        var node = message[name];
        message.Remove(name);
        return node;
    }

    /// <summary>Asks the add-on to finish a recording and exit; waits up to 10 seconds.</summary>
    public void Dispose()
    {
        Run? run;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            run = _run;
            if (run is not null) run.Ended = true;
        }
        if (run is null) return;
        foreach (var pending in run.Pending.Values) pending.Reply.TrySetException(Stopped());
        _ = Task.Run(() => CloseInputAsync(run, shutdown: true));
        if (!run.Completion.Task.Wait(TimeSpan.FromSeconds(10)))
            AppLog.Warn("Capture add-on still finishing after Exo closed; it exits on its own.");
    }

    private static async Task CloseInputAsync(Run run, bool shutdown)
    {
        using var limit = new CancellationTokenSource(WriteTimeout);
        try
        {
            await run.Writer.WaitAsync(limit.Token).ConfigureAwait(false);
            try
            {
                if (run.Process.HasExited) return;
                var input = run.Process.StandardInput;
                if (shutdown) input.WriteLine("{\"id\":0,\"method\":\"shutdown\"}");
                input.Close();
            }
            finally { run.Writer.Release(); }
        }
        catch (Exception ex)
        {
            if (!run.Completion.Task.IsCompleted) AppLog.Debug("Capture add-on shutdown: " + ex.Message);
        }
    }
}

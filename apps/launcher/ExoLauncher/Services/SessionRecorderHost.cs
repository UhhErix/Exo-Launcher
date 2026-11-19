using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>What the recorder asks of the trace: to send while the game is in front, and the frame gaps since the last look.</summary>
internal interface IFrameTrace
{
    void Run(int who, bool on);
    int DrainFrames(int front, List<double> gapsMs);
}

/// <summary>
/// Runs the <see cref="SessionRecorder"/> on the trace <see cref="FpsCounter"/> already owns (Windows allows one session of that
/// name, so there is no second one). <see cref="FpsCounter.Follow"/> tells it which library game runs, every five seconds;
/// its own one-second timer exists only while a game is followed, looks at which window is in front, asks the trace to send
/// while the game is, and hands the recorder the frame gaps since the last look. It reads present times Windows already
/// reports, as the overlay does: nothing is opened, injected or written in a game, and nothing is sent anywhere.
/// <see cref="End"/> (a game closed, a web page rebuilt) saves the session and the next <see cref="Follow"/> records again;
/// only <see cref="Exit"/>, from Exo's real shutdown, stops the recorder for good.
/// Ways it could fail: a timer that never stops (it ends when the game does and when Exo exits); a trace that stays open (the
/// trace is <see cref="FpsCounter"/>'s, ended with the game); a Riot game recorded (it is not); the recorder stopped for the
/// rest of the run by a page rebuild (<see cref="End"/> never stops it).
/// </summary>
internal sealed class SessionRecorderHost
{
    private const int LookEveryMs = 1000;
    private const long VersionEveryMs = 60_000;

    /// <summary>The one the app uses. Tests put their own here, and put the old one back.</summary>
    internal static SessionRecorderHost Shared { get; set; } = new(
        record => SessionStore.Add(SessionStore.DefaultPath, record, DateTime.UtcNow),
        () => (int)ForegroundWatcher.ForegroundProcess(),
        game =>
        {
            try { return SettingsVersion.ForGame(Keep.Load(), game.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return SettingsVersion.None; }
        },
        () => DateTime.UtcNow,
        look => new Timer(_ => look(), null, LookEveryMs, LookEveryMs));

    private readonly Lock _gate = new();
    private readonly SessionRecorder _recorder;
    private readonly Func<int> _front;
    private readonly Func<GameEntry, string> _version;
    private readonly Func<DateTime> _now;
    private readonly Func<Action, IDisposable> _startTimer;
    private readonly List<double> _gaps = [];
    private IDisposable? _timer;
    private IFrameTrace? _trace;
    private string? _game, _currentVersion;
    private long _versionAt;
    private HashSet<int> _tree = [];

    internal SessionRecorderHost(Func<SessionRecord, bool> save, Func<int> front, Func<GameEntry, string> version, Func<DateTime> now, Func<Action, IDisposable> startTimer)
    {
        _recorder = new SessionRecorder(save);
        (_front, _version, _now, _startTimer) = (front, version, now, startTimer);
    }

    internal RecorderState State => _recorder.State;

    /// <summary>
    /// Whether this game is recorded: every library game but Riot's. The answer comes from the game's identity and the Riot rule
    /// alone (its store, and the names of its folder and program); it never opens, reads or lists a game's process or folder.
    /// Anti-cheat games are recorded: Windows' own present events are all that is read.
    /// </summary>
    internal static bool Records(GameEntry game) => !AntiCheatCheck.IsRiot(game);

    /// <summary>The game runs and Windows lets Exo count its frames. From <see cref="FpsCounter.Follow"/>.</summary>
    internal void Follow(IFrameTrace trace, GameEntry game, IReadOnlyCollection<int> tree)
    {
        lock (_gate)
        {
            _trace = trace;
            Note(game, tree);
            _timer ??= _startTimer(Look);
        }
    }

    /// <summary>The game runs and Windows won't let Exo count: the recorder says so by its state and records nothing.</summary>
    internal void NoRights(GameEntry game, IReadOnlyCollection<int> tree)
    {
        lock (_gate)
        {
            _trace = null;
            Note(game, tree);
            LookLocked();
        }
    }

    /// <summary>No game is recorded now (it closed, nothing wants it, or the page was rebuilt): the last frames are read and the session is saved. The trace is still open here. The recorder goes on: the next <see cref="Follow"/> records again.</summary>
    internal void End(IFrameTrace? trace)
    {
        lock (_gate)
        {
            if (_game is null && _timer is null) return;
            if (trace is not null) _trace = trace;
            LookLocked();
            _game = null;
            LookLocked();
            _timer?.Dispose();
            _timer = null;
            _trace?.Run(PresentTrace.ForRecorder, false);
            _trace = null;
            _tree = [];
        }
    }

    /// <summary>Exo is really closing: whatever is being recorded is saved and the recorder takes no more.</summary>
    internal void Exit()
    {
        lock (_gate)
        {
            End(_trace);
            _recorder.Exit();
        }
    }

    /// <summary>One look, as the timer makes it.</summary>
    internal void Look()
    {
        lock (_gate) LookLocked();
    }

    private void Note(GameEntry game, IReadOnlyCollection<int> tree)
    {
        _tree = [.. tree];
        if (_game == game.Id && Environment.TickCount64 - _versionAt < VersionEveryMs) return;
        _game = game.Id;
        _versionAt = Environment.TickCount64;
        _currentVersion = _version(game);
    }

    private void LookLocked()
    {
        try
        {
            var front = _front();
            var inFront = _game is not null && _tree.Contains(front);
            _gaps.Clear();
            if (_trace is { } trace)
            {
                trace.Run(PresentTrace.ForRecorder, inFront);
                // Read even when the game is behind, so what it drew meanwhile is not read as the first frames after the return.
                trace.DrainFrames(front, _gaps);
                if (!inFront) _gaps.Clear();
            }
            _recorder.Tick(new Observation(_game, _currentVersion ?? SettingsVersion.None, inFront, _trace is not null, _gaps, _now()));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Session recorder: a look failed: " + ex.GetType().Name);
        }
    }
}

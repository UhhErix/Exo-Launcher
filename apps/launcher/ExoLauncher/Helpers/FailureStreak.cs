namespace ExoLauncher.Helpers;

/// <summary>
/// Counts a run of failed connection attempts. <see cref="Record"/> returns true exactly once when the run reaches
/// <see cref="DefaultThreshold"/> failures (or the threshold given) whose span is within the window; the report it
/// triggers is then sent once, and a success ends the run and re-arms it. Failures spread wider than the window do not
/// report. Pure: the caller supplies the time. One instance per thing being watched; instances never share a count.
/// </summary>
public sealed class FailureStreak
{
    public const int DefaultThreshold = 5;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    private readonly int _threshold;
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private readonly Queue<DateTime> _failures = new();
    private bool _reported;

    public FailureStreak(int threshold = DefaultThreshold, TimeSpan? window = null)
    {
        if (threshold < 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
        _window = window ?? DefaultWindow;
    }

    /// <summary>Records one attempt. Returns true only for the failure that completes a reportable run.</summary>
    public bool Record(bool ok, DateTime now)
    {
        lock (_gate)
        {
            if (ok)
            {
                _failures.Clear();
                _reported = false;
                return false;
            }
            _failures.Enqueue(now);
            while (_failures.Count > _threshold) _failures.Dequeue();
            if (_reported || _failures.Count < _threshold) return false;
            if (now - _failures.Peek() > _window) return false;
            _reported = true;
            return true;
        }
    }
}

using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// A launch's time limit that stands still while the client is waiting for the player: with a prompt of that client on
/// screen (a sign-in, a licence, a payment) the wait does not time out, for up to 10 minutes in all, and the launch says
/// once that the player is needed.
/// </summary>
internal sealed class PromptAwareDeadline
{
    internal static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(10);

    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _promptVisible;
    private readonly Action<bool>? _promptChanged;
    private DateTimeOffset _deadline;
    private DateTimeOffset _lastLook;
    private TimeSpan _paused;
    private bool _prompt;

    internal PromptAwareDeadline(TimeSpan timeout, Func<DateTimeOffset>? utcNow = null,
        Func<bool>? promptVisible = null, Action<bool>? promptChanged = null)
    {
        _now = utcNow ?? (() => DateTimeOffset.UtcNow);
        _promptVisible = promptVisible ?? (() => false);
        _promptChanged = promptChanged;
        _lastLook = _now();
        _deadline = _lastLook + timeout;
    }

    /// <summary>Looks at the clock and the client: true when the time is up.</summary>
    internal bool Expired
    {
        get
        {
            var now = _now();
            var prompt = _promptVisible();
            // The stretch since the last look counts as the player's when a prompt was up at either end of it.
            var players = prompt || _prompt;
            if (prompt != _prompt)
            {
                _prompt = prompt;
                _promptChanged?.Invoke(prompt);
            }
            if (players && _paused < MaxPause)
            {
                var step = now - _lastLook;
                if (step > TimeSpan.Zero)
                {
                    var allowed = step < MaxPause - _paused ? step : MaxPause - _paused;
                    _paused += allowed;
                    _deadline += allowed;
                }
            }
            _lastLook = now;
            return now >= _deadline;
        }
    }

    internal TimeSpan Remaining
    {
        get
        {
            var left = _deadline - _now();
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>The one sentence the launch status shows while a prompt of the client is waiting for the player.</summary>
    internal static string NeedsYou(string storeName) => $"{storeName} needs you to finish something in its window.";
}

/// <summary>A launch's view of one client's prompts: looked up at most once a second, and said once on the launch status.</summary>
internal sealed class PromptWatch(StoreKind store, string storeName, Action<string>? notice)
{
    private long _nextLook;
    private bool _seen;

    internal bool Visible()
    {
        var now = Environment.TickCount64;
        if (now < _nextLook) return _seen;
        _nextLook = now + 1000;
        try { _seen = StoreWindowHider.PromptVisible(store); }
        catch { _seen = false; }
        return _seen;
    }

    /// <summary>Said on the launch status (the one the page already shows): that the player is needed, then that the launch goes on.</summary>
    internal void Changed(bool shown) =>
        notice?.Invoke(shown ? PromptAwareDeadline.NeedsYou(storeName) : "Preparing launch…");
}

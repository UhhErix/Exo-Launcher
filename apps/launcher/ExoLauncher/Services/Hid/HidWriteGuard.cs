namespace ExoLauncher.Services.Hid;

internal readonly record struct HidWriteDecision(bool Allowed, int ShortCount, int LongCount, int ShortLimit, int LongLimit);

internal sealed class HidWriteGuard
{
    internal const int DefaultMaxShortWindow = 48;
    internal const int DefaultMaxLongWindow = 180;
    internal static readonly TimeSpan DefaultShortWindow = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultLongWindow = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<long>> _windows = new(StringComparer.Ordinal);
    private readonly long _shortTicks;
    private readonly long _longTicks;
    private readonly int _maxShort;
    private readonly int _maxLong;
    private readonly Func<long> _utcNowTicks;

    internal HidWriteGuard(
        TimeSpan? shortWindow = null,
        int maxShort = DefaultMaxShortWindow,
        TimeSpan? longWindow = null,
        int maxLong = DefaultMaxLongWindow,
        Func<long>? utcNowTicks = null)
    {
        _shortTicks = (shortWindow ?? DefaultShortWindow).Ticks;
        _longTicks = (longWindow ?? DefaultLongWindow).Ticks;
        _maxShort = maxShort;
        _maxLong = maxLong;
        _utcNowTicks = utcNowTicks ?? (() => DateTime.UtcNow.Ticks);
    }

    internal HidWriteDecision TryAcquire(string deviceKey)
    {
        var now = _utcNowTicks();
        var longCutoff = now - _longTicks;
        var shortCutoff = now - _shortTicks;
        lock (_gate)
        {
            if (!_windows.TryGetValue(deviceKey, out var queue))
            {
                queue = new Queue<long>();
                _windows[deviceKey] = queue;
            }
            while (queue.Count > 0 && queue.Peek() <= longCutoff) queue.Dequeue();
            var longCount = queue.Count;
            var shortCount = 0;
            foreach (var ts in queue)
            {
                if (ts > shortCutoff) shortCount++;
            }
            if (shortCount >= _maxShort || longCount >= _maxLong)
            {
                return new HidWriteDecision(false, shortCount, longCount, _maxShort, _maxLong);
            }
            queue.Enqueue(now);
            return new HidWriteDecision(true, shortCount + 1, longCount + 1, _maxShort, _maxLong);
        }
    }

    internal void Reset(string deviceKey)
    {
        lock (_gate) _windows.Remove(deviceKey);
    }

    internal static string FormatHead(ReadOnlySpan<byte> data, int maxBytes = 8)
    {
        var count = Math.Min(data.Length, maxBytes);
        if (count <= 0) return "-";
        return Convert.ToHexString(data[..count]).ToLowerInvariant();
    }
}

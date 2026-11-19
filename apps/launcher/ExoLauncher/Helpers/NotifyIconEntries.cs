namespace ExoLauncher.Helpers;

/// <summary>
/// The notification-area entries Windows keeps, read once and kept until Windows says they changed or they are too
/// old. A read that finds nothing (the key is missing) is not kept. A change that lands while a read runs makes the
/// result stale, so the next call reads again.
/// </summary>
internal sealed class NotifyIconEntries(Func<List<(string? Executable, string? Guid, int? Uid)>?> read, Func<long>? now = null, long maxAgeMs = 120_000)
{
    private readonly object _gate = new();
    private readonly Func<long> _now = now ?? (() => Environment.TickCount64);
    private List<(string? Executable, string? Guid, int? Uid)>? _kept;
    private long _readAt;
    private int _version, _keptVersion = -1;

    internal void Changed() { lock (_gate) _version++; }

    internal IReadOnlyList<(string? Executable, string? Guid, int? Uid)> Get()
    {
        lock (_gate)
        {
            if (_kept is not null && _keptVersion == _version && _now() - _readAt <= maxAgeMs) return _kept;
            var version = _version;
            var fresh = read();
            if (fresh is null) { _kept = null; return []; }
            _kept = fresh;
            _keptVersion = version;
            _readAt = _now();
            return fresh;
        }
    }
}

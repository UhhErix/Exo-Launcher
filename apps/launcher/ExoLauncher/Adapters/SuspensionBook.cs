using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// "Leave this store's windows alone": every suspension has a reason and ends with it (its handle is disposed), the time
/// limit is the backstop, and one that waits for a client to close ends when the client has. A store stays suspended
/// while any of its suspensions is open, so one ending never cuts another short.
/// </summary>
internal sealed class SuspensionBook(Func<DateTimeOffset>? utcNow = null)
{
    /// <summary>A client Exo has only just started may not be up yet: a suspension that ends with the client waits this long.</summary>
    internal static readonly TimeSpan ClientGrace = TimeSpan.FromSeconds(30);

    private sealed class Entry(StoreKind store, string reason, DateTimeOffset since, DateTimeOffset until, bool endsWithClient)
    {
        public StoreKind Store { get; } = store;
        public string Reason { get; } = reason;
        public DateTimeOffset Since { get; } = since;
        public DateTimeOffset Until { get; set; } = until;
        public bool EndsWithClient { get; } = endsWithClient;
    }

    private sealed class Handle(SuspensionBook book, Entry entry) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) book.End(entry);
        }
    }

    private readonly Func<DateTimeOffset> _now = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    /// <summary>
    /// Begin (or, for the same store and reason, extend) a suspension. Dispose the handle when the thing it waits for is
    /// over; <paramref name="upTo"/> ends it anyway. With <paramref name="endsWhenClientExits"/> it also ends once the
    /// client has been seen not running (<see cref="Prune"/>), for a hand-off the caller does not stay to watch.
    /// </summary>
    internal IDisposable Hold(StoreKind store, string reason, TimeSpan upTo, bool endsWhenClientExits = false)
    {
        var now = _now();
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Store == store && e.Reason == reason);
            if (entry is null)
            {
                entry = new Entry(store, reason, now, now + upTo, endsWhenClientExits);
                _entries.Add(entry);
            }
            else if (now + upTo > entry.Until) entry.Until = now + upTo;
            return new Handle(this, entry);
        }
    }

    private void End(Entry entry)
    {
        lock (_gate) _entries.Remove(entry);
    }

    internal bool IsSuspended(StoreKind store)
    {
        var now = _now();
        lock (_gate) return _entries.Any(e => e.Store == store && now < e.Until);
    }

    internal bool Has(StoreKind store, string reason)
    {
        var now = _now();
        lock (_gate) return _entries.Any(e => e.Store == store && e.Reason == reason && now < e.Until);
    }

    internal IReadOnlyList<StoreKind> StoresWith(string reason)
    {
        var now = _now();
        lock (_gate) return _entries.Where(e => e.Reason == reason && now < e.Until).Select(e => e.Store).Distinct().ToArray();
    }

    internal bool IsEmpty
    {
        get { lock (_gate) return _entries.Count == 0; }
    }

    /// <summary>Drops what has run out, and what waited for a client that is no longer running.</summary>
    internal void Prune(Func<StoreKind, bool> clientRunning)
    {
        var now = _now();
        lock (_gate)
            _entries.RemoveAll(e => now >= e.Until ||
                                    e.EndsWithClient && now - e.Since > ClientGrace && !clientRunning(e.Store));
    }
}

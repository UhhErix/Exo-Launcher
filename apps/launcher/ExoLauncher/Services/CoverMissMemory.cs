using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// Games whose art search found nothing, remembered for a day so a warm does not ask the shared cache, the store
/// searches and the catalogs again on every launch. Kept in <c>cover-misses.json</c> beside the covers, so it survives
/// a restart. "Find cover" (CoverArtService.RefetchComputedAsync) forgets a game at once. Ways this could fail silently:
/// - a game is suppressed forever (the file never expires an entry, so an old miss blocks art a store added later);
/// - a corrupt file throws and every warm fails (a corrupt file must read as empty);
/// - "Find cover" does not clear the entry, so the button does nothing.
/// </summary>
internal sealed class CoverMissMemory
{
    internal static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, DateTimeOffset>? _entries;

    internal CoverMissMemory(string path) => _path = path;

    internal static CoverMissMemory Default { get; } = new(Path.Combine(CoverArtService.CacheRoot, "cover-misses.json"));

    /// <summary>True when the game missed within the last day and should not be searched again yet.</summary>
    internal bool Suppressed(string gameId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var entries = Load();
            return entries.TryGetValue(gameId, out var missedAt) && now - missedAt < Window;
        }
    }

    internal void Remember(string gameId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var entries = Load();
            entries[gameId] = now;
            Save(entries);
        }
    }

    internal void Forget(string gameId)
    {
        lock (_gate)
        {
            var entries = Load();
            if (entries.Remove(gameId)) Save(entries);
        }
    }

    private Dictionary<string, DateTimeOffset> Load()
    {
        if (_entries is not null) return _entries;
        _entries = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path))
                _entries = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_path))
                           ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Cover miss memory unreadable, starting empty: " + ex.Message);
        }
        return _entries;
    }

    private void Save(Dictionary<string, DateTimeOffset> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Cover miss memory not saved: " + ex.Message);
        }
    }
}

/// <summary>
/// Notes whether a cover request failed on the network during the warm that is running. A store that answered (a match, or
/// a 404 for "no such art") is a real answer; an unreachable or failing one (no response, a timeout, a 5xx) is not, so the warm
/// remembers no miss for that game.
/// </summary>
internal sealed class CoverNetworkWatch(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    /// <summary>The warm running in this async flow; null outside a warm.</summary>
    internal static readonly System.Threading.AsyncLocal<WarmFlag?> Current = new();

    internal sealed class WarmFlag
    {
        public bool Failed { get; private set; }
        internal void Mark() => Failed = true;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            Current.Value?.Mark();
            throw;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, not the caller's cancellation.
            Current.Value?.Mark();
            throw;
        }
        if ((int)response.StatusCode >= 500) Current.Value?.Mark();
        return response;
    }
}

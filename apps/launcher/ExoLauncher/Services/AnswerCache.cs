using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// An RPC answer kept as its JSON (apps.list's), stale while it revalidates: the last answer comes back at once, from
/// memory or, after a restart, from its file, while a read behind it refreshes it (at most one at a time, none within
/// <c>fresh</c> of the last); a read that differs is kept, saved and <see cref="Changed"/> says so. A change made here
/// (<see cref="Invalidate"/>: an uninstall, a clean) makes the next answer a fresh read, never the answer from before it,
/// and a read that started before the change never replaces one made after it. A file from another version of Exo is
/// never answered.
/// </summary>
internal sealed class AnswerCache(string path, string version, Func<string> read, TimeSpan fresh)
{
    /// <summary>A read behind the answer found something else (on the reading thread).</summary>
    internal event Action? Changed;

    private readonly Lock _gate = new();
    private string? _json;
    private bool _loaded;
    // Bumped by every change made here; the answer holds the one its read started at.
    private int _changes;
    private int _answered = -1;
    private DateTime _at;
    private Task? _behind;

    /// <summary>The answer: the kept one (and a read behind it), else a read now.</summary>
    internal string Get()
    {
        string? json;
        lock (_gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                if (Load() is { } saved) (_json, _answered) = (saved, _changes);
            }
            json = _answered == _changes ? _json : null;
        }
        if (json is null) return Read(tell: false);
        Behind();
        return json;
    }

    /// <summary>Something changed here: the next answer is read fresh.</summary>
    internal void Invalidate() => Interlocked.Increment(ref _changes);

    /// <summary>The read behind the answer, when none runs and the answer is older than fresh.</summary>
    internal Task Behind()
    {
        lock (_gate)
        {
            if (_behind is { IsCompleted: false } || DateTime.UtcNow - _at < fresh) return _behind ?? Task.CompletedTask;
            return _behind = Task.Run(() =>
            {
                try { Read(tell: true); }
                catch (Exception ex) { AppLog.Warn($"Cached answer: the read behind {Path.GetFileName(path)} failed: {ex.Message}"); }
            });
        }
    }

    private string Read(bool tell)
    {
        var changes = Volatile.Read(ref _changes);
        var json = read();
        bool changed;
        lock (_gate)
        {
            // A read that started before a newer one's (and before a change since) never replaces it.
            if (changes < _answered) return json;
            changed = json != _json;
            (_json, _answered, _at) = (json, changes, DateTime.UtcNow);
        }
        if (!changed) return json;
        Save(json);
        if (tell) Changed?.Invoke();
        return json;
    }

    private string? Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8 << 20) return null;
            var saved = JsonNode.Parse(File.ReadAllText(path));
            return saved?["version"]?.GetValue<string>() == version && saved["answer"] is { } answer ? answer.ToJsonString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    private void Save(string json)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, $"{{\"version\":{JsonSerializer.Serialize(version)},\"answer\":{json}}}");
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug($"Cached answer: {Path.GetFileName(path)} not saved: {ex.Message}"); }
    }
}

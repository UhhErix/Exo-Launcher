using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.Account;

/// <summary>A change made while no account is signed in (the value null is a deletion).</summary>
internal sealed record PendingItem(string Type, string Id, string? Value, long Updated, List<string>? Changed = null, Dictionary<string, long>? ChangedAt = null);

/// <summary>
/// The changes made with no account signed in, kept on this PC (account-sync-pending.json) until one is. Logs (a play session,
/// a Riot ledger line) are never dropped. Object items (settings, covers, seeds) replace their earlier value by id, and when the
/// file would pass its size cap the oldest object items go first. A file that cannot be parsed is moved aside, never overwritten;
/// a file that cannot be read at all is thrown, so the caller keeps it.
/// </summary>
internal static class AccountSyncPending
{
    internal const long MaxBytes = 32L * 1024 * 1024;

    internal static bool IsLog(string type) => type is "playtime" or "riot-ledger";

    /// <summary>The waiting changes. No file is empty; an unparseable file is moved aside and read as empty.</summary>
    internal static List<PendingItem> Load(string path)
    {
        if (!File.Exists(path)) return [];
        var text = File.ReadAllText(path);
        try { return JsonSerializer.Deserialize<List<PendingItem>>(text) ?? []; }
        catch (JsonException)
        {
            var aside = path + ".unreadable-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.Move(path, aside);
            AppLog.Warn("Sync: the waiting changes file could not be parsed; it was kept as " + Path.GetFileName(aside) + ".");
            return [];
        }
    }

    /// <summary>Writes the whole list to a temp file beside the path, then moves it over: a reader sees the old file or the new one.</summary>
    internal static void Save(string path, IReadOnlyList<PendingItem> items)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(items));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) DeletionGate.DeleteFile(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Adds the item (a repeat of its id replaces it), then drops the oldest object items while the list is over the cap.</summary>
    internal static void Add(List<PendingItem> items, PendingItem item, long cap)
    {
        var prior = items.FirstOrDefault(existing => existing.Type == item.Type && existing.Id == item.Id);
        if (prior is not null && prior.Changed is not null && item.Changed is not null)
        {
            var mergedKeys = prior.Changed.Union(item.Changed, StringComparer.Ordinal).ToList();
            var mergedAt = new Dictionary<string, long>(StringComparer.Ordinal);
            if (prior.ChangedAt is not null)
                foreach (var pair in prior.ChangedAt) mergedAt[pair.Key] = pair.Value;
            else
                foreach (var key in prior.Changed) mergedAt[key] = prior.Updated;
            if (item.ChangedAt is not null)
                foreach (var pair in item.ChangedAt) mergedAt[pair.Key] = pair.Value;
            else
                foreach (var key in item.Changed) mergedAt[key] = item.Updated;
            item = item with { Changed = mergedKeys, ChangedAt = mergedAt };
        }
        items.RemoveAll(existing => existing.Type == item.Type && existing.Id == item.Id);
        items.Add(item);
        var size = items.Sum(Size);
        for (var i = 0; size > cap && i < items.Count;)
        {
            if (IsLog(items[i].Type)) { i++; continue; }
            size -= Size(items[i]);
            items.RemoveAt(i);
        }
    }

    private static long Size(PendingItem item) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item));
}

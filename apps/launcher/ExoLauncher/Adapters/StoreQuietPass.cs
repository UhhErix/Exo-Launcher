using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// The part of a Steam pass that keeps Steam's quiet values: written while Steam is closed, each with what it held before
/// Exo's first write recorded in Keep (<see cref="Keep.KeepStore"/>, "steam/&lt;account id&gt;"), read back from the disk
/// after the write, written again whenever Steam, an update or Steam Cloud changed one back, and put back by Off exactly
/// as it was (a key that was absent is removed, with a section only Exo added). While quiet is off nothing is written
/// but the put back. The caller decides that Steam is closed; nothing here closes it.
/// Ways it could fail, each with a test: a value overwritten with nothing recorded of it; a record made after the write
/// (a crash between leaves a change that cannot be undone); a record overwritten by a second write with Exo's own value
/// as "before"; a write Steam did not take reported as taken; Off that leaves a key, an added section or a changed byte
/// elsewhere in the file; Off that forgets values it did not put back; quiet written while it is off.
/// </summary>
internal static class StoreQuietPass
{
    internal const string Client = "steam";

    private sealed record Position(string Account, bool Local, string Path);

    private static IEnumerable<Position> Files(string root)
    {
        foreach (var account in StoreQuiet.Accounts(root))
        {
            var id = Path.GetFileName(account);
            yield return new Position(id, false, Path.Combine(account, "7", "remote", "sharedconfig.vdf"));
            yield return new Position(id, true, Path.Combine(account, "config", "localconfig.vdf"));
        }
    }

    private static string Target(bool local, string[] path, string key) =>
        (local ? "local" : "shared") + "|" + string.Join('/', path) + "|" + key;

    /// <summary>How many files need a write: Exo's values to go in (<paramref name="wanted"/>), or the recorded values to go back.</summary>
    internal static int Pending(string root, bool wanted)
    {
        if (wanted) return StoreQuiet.SteamEdits(root).Count;
        var record = Keep.Load().Stores;
        return Files(root).Count(file => File.Exists(file.Path) &&
            record.TryGetValue(Client + "/" + file.Account, out var store) && store.Held.Values.Any(held => held.File == Name(file.Local)));
    }

    private static string Name(bool local) => local ? "local" : "shared";

    /// <summary>
    /// One pass with Steam closed. <paramref name="wanted"/>: Exo's quiet values are put in wherever a file does not hold them.
    /// Not wanted: what the record holds goes back. Returns the files written and the values a write did not take, by key.
    /// </summary>
    internal static (int Written, IReadOnlyList<string> NotTaken) Run(string root, bool wanted)
    {
        if (HostMode.IsDisposable) return (0, []);
        return Run(root, wanted, SteamOverlayConfig.WriteFile);
    }

    /// <summary>The same with the write named (the tests' seam: a write Steam does not take).</summary>
    internal static (int Written, IReadOnlyList<string> NotTaken) Run(string root, bool wanted, Action<string, string> write)
    {
        if (HostMode.IsDisposable) return (0, []);
        return wanted ? Apply(root, write) : PutBack(root, write);
    }

    private static (int, IReadOnlyList<string>) Apply(string root, Action<string, string> write)
    {
        var written = 0;
        var notTaken = new List<string>();
        foreach (var edit in StoreQuiet.SteamEdits(root))
        {
            // Recorded first: a crash after the write must still leave Exo able to put every value back.
            var key = Client + "/" + edit.Account;
            var held = edit.Changes
                .GroupBy(change => Target(edit.Local, change.Path, change.Key), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Held(edit, group.First()), StringComparer.Ordinal);
            if (!Keep.Update(doc =>
                {
                    var store = doc.Stores.TryGetValue(key, out var existing) ? existing : doc.Stores[key] = new Keep.KeepStore();
                    foreach (var (target, was) in held) store.Held.TryAdd(target, was);
                }))
            {
                AppLog.Warn("Steam: quiet values not written; what they hold could not be recorded first.");
                continue;
            }
            write(edit.Path, edit.Text);
            written++;
            var back = SteamPlaytime.ReadSharedText(edit.Path) ?? "";
            foreach (var change in edit.Changes)
                if (!Took(back, change)) notTaken.Add(change.Key);
        }
        if (notTaken.Count > 0) AppLog.Warn("Steam: did not keep " + string.Join(", ", notTaken.Distinct()) + " after a write.");
        return (written, notTaken);
    }

    private static bool Took(string text, StoreQuiet.Change change)
    {
        try { return SteamOverlayConfig.Probe(text, change.Path, change.Key).Raw == change.Value; }
        catch (InvalidDataException) { return false; }
    }

    private static Keep.KeepHeld Held(StoreQuiet.Edit edit, StoreQuiet.Change change)
    {
        var (raw, depth) = SteamOverlayConfig.Probe(edit.Original, change.Path, change.Key);
        return new Keep.KeepHeld { File = Name(edit.Local), Path = change.Path, Key = change.Key, Absent = raw is null, Raw = raw ?? "", Depth = depth };
    }

    private static (int, IReadOnlyList<string>) PutBack(string root, Action<string, string> write)
    {
        var written = 0;
        var notBack = new List<string>();
        var record = Keep.Load().Stores;
        foreach (var file in Files(root))
        {
            var key = Client + "/" + file.Account;
            if (!record.TryGetValue(key, out var store)) continue;
            var mine = store.Held.Where(entry => entry.Value.File == Name(file.Local)).ToList();
            if (mine.Count == 0 || SteamPlaytime.ReadSharedText(file.Path) is not { Length: > 0 } text) continue;
            string next;
            try { next = Restored(text, mine.Select(entry => entry.Value)); }
            catch (InvalidDataException) { continue; /* Steam writes the file whole as it next exits */ }
            if (next != text)
            {
                write(file.Path, next);
                written++;
            }
            var back = SteamPlaytime.ReadSharedText(file.Path) ?? "";
            var done = mine.Where(entry => Holds(back, entry.Value)).Select(entry => entry.Key).ToList();
            notBack.AddRange(mine.Where(entry => !done.Contains(entry.Key)).Select(entry => entry.Value.Key));
            // Forgotten only once the file holds them again.
            Keep.Update(doc =>
            {
                if (!doc.Stores.TryGetValue(key, out var kept)) return;
                foreach (var target in done) kept.Held.Remove(target);
                if (kept.Held.Count == 0) doc.Stores.Remove(key);
            });
        }
        if (notBack.Count > 0) AppLog.Warn("Steam: did not put back " + string.Join(", ", notBack.Distinct()) + "; Exo tries again when Steam is closed.");
        return (written, notBack);
    }

    /// <summary>The text with every recorded value as it was: the old text back, or the key (and a section only Exo added) gone.</summary>
    internal static string Restored(string text, IEnumerable<Keep.KeepHeld> held)
    {
        foreach (var value in held)
        {
            if (!value.Absent)
            {
                text = SteamOverlayConfig.SetValue(text, value.Path, value.Key, value.Raw);
                continue;
            }
            text = SteamOverlayConfig.RemoveValue(text, value.Path, value.Key);
            for (var depth = value.Path.Length; depth > value.Depth; depth--)
                text = SteamOverlayConfig.RemoveEmptySection(text, value.Path[..depth]);
        }
        return text;
    }

    private static bool Holds(string text, Keep.KeepHeld value)
    {
        try
        {
            var raw = SteamOverlayConfig.Probe(text, value.Path, value.Key).Raw;
            return value.Absent ? raw is null : raw == value.Raw;
        }
        catch (InvalidDataException) { return false; }
    }
}
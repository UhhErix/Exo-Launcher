using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal static partial class GameSettings
{
    // ------------------------------------------------------------------ the record: what Optimize changed and what it keeps on

    /// <summary>A line's shape of its file: its setting lines' ids in order, to tell a file the game rebuilt from one it only edited.</summary>
    internal static string ShapeOf(SettingsFile file) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', file.Settings.Select(line => line.Id)))))[..16];

    /// <summary>
    /// The record of a game, brought in from the old files first when its own record does not exist yet; null when Exo holds
    /// nothing for it. Never over a record that is merely locked (another program holds it): whether the game is absent and the
    /// write are one read under one lock, so a record locked for a moment when the look first read it is found and kept.
    /// </summary>
    private static Keep.KeepGame? GameRecord(string profileId)
    {
        var doc = Keep.Load();
        if (doc.Games.TryGetValue(profileId, out var game)) return game;
        // The old files are read outside the lock (they are never written). The migration itself is one update that first asks
        // whether the game is absent in the record it is about to save, so the stale values of the older Exo never stand over a
        // record that was there all along.
        if (MigrateFrom(profileId) is not { } brought) return null;
        Keep.Update(saved =>
        {
            if (!saved.Games.ContainsKey(profileId)) saved.Games[profileId] = brought;
        });
        // What the record holds after the update: the brought-in game, or the one that was there after all; and nothing when the
        // record could not be read as it was written.
        return Keep.Load().Games.GetValueOrDefault(profileId);
    }

    /// <summary>
    /// kept.json (what Optimize changed, by game) and {id}.on.json (what it keeps on) become one game's record: every kept line an
    /// entry (what it replaced, what Exo set), a line the player changed in the game an entry that is theirs (yielded). A
    /// migrated entry's owner is the profile id alone: the old files don't say which group a line belongs to, and only the
    /// game's file can, so the first look that has it fills the group in (<see cref="FillOwners"/>). The old files are read,
    /// never changed or removed, so Undo stays exact for one more release. Null when neither holds the game.
    /// </summary>
    internal static Keep.KeepGame? MigrateFrom(string profileId)
    {
        var kept = LegacyKept().GetValueOrDefault(profileId);
        var keeping = LegacyKeeping(profileId);
        if ((kept is null || kept.Count == 0) && keeping is null) return null;
        var game = new Keep.KeepGame { Migrated = true };
        foreach (var (key, remembered) in kept ?? [])
            game.Entries[key] = new Keep.KeepEntry
            {
                Target = key, Owner = profileId, Intended = remembered.Set, Before = remembered.Was, ExoVersion = "migrated",
                State = keeping?.Theirs.Contains(key) == true ? KeepState.Yielded : KeepState.Held,
            };
        if (keeping is not null)
            game.Keeping = new Keep.Keeping
            {
                Groups = [.. keeping.Groups], Marks = new(keeping.Marks, StringComparer.Ordinal), Program = keeping.Program, Built = keeping.Built,
                Again = keeping.Again, Pending = keeping.Pending,
            };
        return game;
    }

    /// <summary>
    /// The first look that has the game's file fills in what the old files could not say: the group of every entry they
    /// brought in without one (the same lookup <see cref="SyncRecord"/> uses) and each file's shape as this look finds it, so
    /// a file the old Exo already wrote is not read as one the game rebuilt. Only a record brought in from the old files has
    /// no shapes, so this happens once; true when the record changed, and the caller goes on with the record as it now is.
    /// </summary>
    private static bool FillOwners(Profile profile, IReadOnlyList<OpenFile> files)
    {
        if (files.Count == 0) return false;
        var record = GameRecord(profile.Id);
        if (record is not { Migrated: true } || record.Shapes.Count > 0) return false;
        var groups = GroupsByLine(profile, files[0].File);
        var shapes = files.ToDictionary(open => open.Key, open => ShapeOf(open.File), StringComparer.Ordinal);
        return Keep.Update(doc =>
        {
            if (!doc.Games.TryGetValue(profile.Id, out var game) || game.Shapes.Count > 0) return;
            foreach (var entry in game.Entries.Values)
                if (GroupOf(entry).Length == 0 && groups.TryGetValue(LineNameOf(entry.Target), out var group)) entry.Owner = profile.Id + "/" + group;
            game.Shapes = shapes;
        });
    }

    /// <summary>What Optimize changed for a game, as Put back needs it: each line's value before Exo and what Exo set.</summary>
    internal static Dictionary<string, Remembered> KeptOf(string profileId) =>
        (GameRecord(profileId)?.Entries ?? []).ToDictionary(pair => pair.Key, pair => new Remembered(pair.Value.Before, pair.Value.Intended), StringComparer.Ordinal);

    internal static Dictionary<string, Remembered> Kept(string profileId)
    {
        lock (Gate) return KeptOf(profileId);
    }

    /// <summary>
    /// The record remembers the library game this profile was pressed for, so the page's status is told under it without
    /// walking the game's folder (the profile id alone is not a game id). One update, and none at all when it is there.
    /// </summary>
    internal static void NoteGame(string profileId, string gameId)
    {
        lock (Gate)
        {
            if (Keep.Load().Games.GetValueOrDefault(profileId) is not { } game || game.GameIds.Contains(gameId, StringComparer.Ordinal)) return;
            Keep.Update(doc =>
            {
                if (doc.Games.TryGetValue(profileId, out var noted) && !noted.GameIds.Contains(gameId, StringComparer.Ordinal)) noted.GameIds.Add(gameId);
            });
        }
    }

    /// <summary>What Optimize keeps on for a game as the older code saw it (Theirs: the lines that are the player's or the game's to keep).</summary>
    internal static Keeping? KeepingFor(string profileId)
    {
        var game = GameRecord(profileId);
        if (game?.Keeping is not { } keeping) return null;
        var theirs = game.Entries.Where(pair => pair.Value.State is KeepState.Yielded or KeepState.Refused).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToList();
        return new Keeping([.. keeping.Groups], theirs, new Dictionary<string, string>(keeping.Marks), keeping.Program, keeping.Built, keeping.Again, keeping.Pending);
    }

    private static Keeping? LoadKeeping(Profile profile) => KeepingFor(profile.Id);

    /// <summary>Keeps what Optimize keeps on (the groups, each file's mark, the game's program and facts) in the record; false when it couldn't be saved.</summary>
    private static bool SaveKeeping(Profile profile, IReadOnlyList<OpenFile> files, List<string> groups, string? program, DateTime? built, DateTime? again,
        IReadOnlyDictionary<string, string>? facts, bool pending = false)
    {
        _ = GameRecord(profile.Id);
        var marks = new Dictionary<string, string>(StringComparer.Ordinal);
        var shapes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var open in files)
        {
            if (profile.Mark?.Invoke(open.File) is { } mark) marks[open.Key] = mark;
            shapes[open.Key] = ShapeOf(open.File);
        }
        return Keep.Update(doc =>
        {
            var game = doc.Games.TryGetValue(profile.Id, out var existing) ? existing : doc.Games[profile.Id] = new Keep.KeepGame { Migrated = true };
            game.Migrated = true;
            game.Keeping = new Keep.Keeping
            {
                Groups = groups, Marks = marks, Program = program, Built = built, Again = again, Pending = pending,
                Facts = facts is null ? game.Keeping?.Facts ?? [] : new Dictionary<string, string>(facts, StringComparer.Ordinal),
            };
            if (files.Count > 0) game.Shapes = shapes;
        });
    }

    /// <summary>Put back, or nothing left to keep: what Optimize keeps on is forgotten; what it changed stays as the record of what to put back. False when it couldn't be saved.</summary>
    private static bool DeleteKeeping(Profile profile)
    {
        return Keep.Update(doc =>
        {
            if (!doc.Games.TryGetValue(profile.Id, out var game)) return;
            game.Keeping = null;
            game.RefusedSaid = false;
            game.Signals = [];
            // A game with nothing left in it is dropped; its old files are not read again (Migrated says it was brought in).
            if (game.Entries.Count == 0) game.Context = [];
        });
    }

    /// <summary>The old files, read only: kept.json by game, empty when it is not there or can't be read.</summary>
    private static Dictionary<string, Dictionary<string, Remembered>> LegacyKept()
    {
        try
        {
            return File.Exists(KeptPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Remembered>>>(File.ReadAllText(KeptPath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Game settings: what Exo kept couldn't be read: {ex.GetType().Name}");
            return [];
        }
    }

    private static Keeping? LegacyKeeping(string profileId)
    {
        var path = Path.Combine(Folder, profileId + ".on.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Keeping>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Game settings: what Exo keeps on for {profileId} couldn't be read: {ex.GetType().Name}");
            return null;
        }
    }
}

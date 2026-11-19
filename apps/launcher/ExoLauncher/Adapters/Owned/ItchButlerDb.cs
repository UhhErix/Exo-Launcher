using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Data.Sqlite;

namespace ExoLauncher.Adapters;

/// <summary>
/// The games the active itch profile owns, read from the itch app's own database (<c>%APPDATA%\itch\db\butler.db</c>):
/// its download keys for the profile's user, joined to the games table. Nothing is asked of itch.io and no key is
/// needed. The database is copied (with its WAL) to a temporary folder and read there read-only, so a running itch
/// app is never locked or changed. The stored API key is never selected. A missing database is no games; a damaged
/// one throws, so the scan keeps its last result instead of emptying the shelf.
/// Unverified on device: itch is not installed on the development PC.
/// </summary>
internal static class ItchButlerDb
{
    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch", "db", "butler.db");

    internal static IReadOnlyList<GameEntry> ReadOwned(string databasePath) => ReadOwned(databasePath, Path.GetTempPath());

    /// <summary>
    /// Reads the live database in place, read-only (WAL-safe, nothing written). Only when SQLite cannot open it there (a
    /// lock, no access to its folder) is a copy read, from <paramref name="tempRoot"/>, and that copy is deleted on every path.
    /// </summary>
    internal static IReadOnlyList<GameEntry> ReadOwned(string databasePath, string tempRoot)
    {
        if (!File.Exists(databasePath)) return [];
        SweepStale(tempRoot);
        try
        {
            return Query(databasePath);
        }
        catch (SqliteException ex)
        {
            AppLog.Debug("itch database not read in place: " + ex.SqliteErrorCode);
            return QueryCopy(databasePath, tempRoot);
        }
    }

    private static IReadOnlyList<GameEntry> QueryCopy(string databasePath, string tempRoot)
    {
        var folder = Path.Combine(tempRoot, "exo-itch-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, "butler.db");
            File.Copy(databasePath, copy);
            var wal = databasePath + "-wal";
            if (File.Exists(wal)) File.Copy(wal, copy + "-wal");
            return Query(copy);
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    /// <summary>Deletes copies an earlier read left behind (a crash or a kill) once they are over an hour old; a fresh one may be in use.</summary>
    private static void SweepStale(string tempRoot)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var folder in Directory.EnumerateDirectories(tempRoot, "exo-itch-*"))
            {
                if (Directory.GetCreationTimeUtc(folder) < cutoff) DeleteFolder(folder);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("itch temp sweep skipped: " + ex.GetType().Name);
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) DeletionGate.DeleteDirectory(folder, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug("itch temp folder not removed: " + ex.GetType().Name);
        }
    }

    private static IReadOnlyList<GameEntry> Query(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        using var profile = connection.CreateCommand();
        profile.CommandText = "SELECT user_id FROM profiles ORDER BY last_connected DESC LIMIT 1";
        if (profile.ExecuteScalar() is not long userId) return [];

        using var keys = connection.CreateCommand();
        keys.CommandText = """
            SELECT g.id, g.title, g.cover_url
            FROM download_keys k
            JOIN games g ON g.id = k.game_id
            WHERE k.owner_id = $user AND (g.classification IS NULL OR g.classification = 'game')
            GROUP BY g.id
            ORDER BY g.title
            """;
        keys.Parameters.AddWithValue("$user", userId);

        var games = new List<GameEntry>();
        using var reader = keys.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            var id = reader.GetInt64(0);
            var title = reader.GetString(1).Trim();
            if (id <= 0 || title.Length == 0) continue;
            games.Add(new GameEntry
            {
                Id = "itch:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Title = title,
                Store = StoreKind.Itch,
                Installed = false,
                Owned = true,
                EntitlementState = EntitlementState.Owned,
                CanInstall = true,
                CoverUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                LaunchTarget = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Status = "Not installed",
                LaunchNote = "Owned on itch.io. Install through the itch app.",
            });
        }
        return games;
    }
}

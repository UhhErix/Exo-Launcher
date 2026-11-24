using Microsoft.Data.Sqlite;

namespace ExoLauncher.Helpers;

internal static class ExoSqlite
{
    internal const long MmapSizeBytes = 64L * 1024 * 1024;
    internal const int CacheSizeKib = -16384;
    internal const int SmallCacheSizeKib = -2048;
    internal const int BusyTimeoutMs = 5000;
    internal const int WalAutoCheckpointPages = 1000;

    internal const string ReadWritePragmasSql =
        "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA mmap_size=67108864; PRAGMA cache_size=-16384; PRAGMA temp_store=MEMORY; PRAGMA foreign_keys=ON; PRAGMA wal_autocheckpoint=1000;";

    private const string ReadOnlyPragmasDefaultSql =
        "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF; PRAGMA mmap_size=67108864; PRAGMA cache_size=-16384; PRAGMA temp_store=MEMORY;";

    private const string ReadOnlyPragmasSmallCacheSql =
        "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF; PRAGMA mmap_size=67108864; PRAGMA cache_size=-2048; PRAGMA temp_store=MEMORY;";

    internal static SqliteConnection OpenReadWrite(string databasePath, int defaultTimeoutSeconds = 5)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = defaultTimeoutSeconds,
        }.ToString());
        try
        {
            connection.Open();
            ApplyReadWritePragmas(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static SqliteConnection OpenReadOnly(string databasePath, int defaultTimeoutSeconds = 1, int cacheSizeKib = CacheSizeKib)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = defaultTimeoutSeconds,
        }.ToString());
        try
        {
            connection.Open();
            ApplyReadOnlyPragmas(connection, cacheSizeKib);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static void ApplyReadWritePragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ReadWritePragmasSql;
        command.ExecuteNonQuery();
    }

    internal static void ApplyReadOnlyPragmas(SqliteConnection connection, int cacheSizeKib = CacheSizeKib)
    {
        using var command = connection.CreateCommand();
        if (cacheSizeKib == SmallCacheSizeKib)
        {
            command.CommandText = ReadOnlyPragmasSmallCacheSql;
        }
        else if (cacheSizeKib == CacheSizeKib)
        {
            command.CommandText = ReadOnlyPragmasDefaultSql;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(cacheSizeKib));
        }
        command.ExecuteNonQuery();
    }
}

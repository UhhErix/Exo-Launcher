using Microsoft.Data.Sqlite;

namespace ExoLauncher.Helpers;

internal static class ExoSqlite
{
    internal const long MmapSizeBytes = 64L * 1024 * 1024;
    internal const int CacheSizeKib = -16384;
    internal const int BusyTimeoutMs = 5000;
    internal const int WalAutoCheckpointPages = 1000;

    internal static readonly string ReadWritePragmasSql = $"""
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;
        PRAGMA busy_timeout={BusyTimeoutMs};
        PRAGMA mmap_size={MmapSizeBytes};
        PRAGMA cache_size={CacheSizeKib};
        PRAGMA temp_store=MEMORY;
        PRAGMA foreign_keys=ON;
        PRAGMA wal_autocheckpoint={WalAutoCheckpointPages};
        """;

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
        command.CommandText = $"""
            PRAGMA query_only=ON;
            PRAGMA trusted_schema=OFF;
            PRAGMA mmap_size={MmapSizeBytes};
            PRAGMA cache_size={cacheSizeKib};
            PRAGMA temp_store=MEMORY;
            """;
        command.ExecuteNonQuery();
    }
}

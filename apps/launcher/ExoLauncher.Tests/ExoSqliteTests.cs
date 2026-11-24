using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace ExoLauncher.Tests;

public sealed class ExoSqliteTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-sqlite-test-" + Guid.NewGuid().ToString("N"));

    public ExoSqliteTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) DeletionGate.DeleteDirectory(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ReadWritePragmasEnableWalMmapAndNormalSync()
    {
        var path = Path.Combine(_folder, "rw.db");
        using var connection = ExoSqlite.OpenReadWrite(path);

        Assert.Equal("wal", ScalarString(connection, "PRAGMA journal_mode;").ToLowerInvariant());
        Assert.Equal(1L, ScalarInt64(connection, "PRAGMA synchronous;"));
        Assert.Equal(ExoSqlite.MmapSizeBytes, ScalarInt64(connection, "PRAGMA mmap_size;"));
        Assert.Equal(ExoSqlite.CacheSizeKib, ScalarInt64(connection, "PRAGMA cache_size;"));
        Assert.Equal(1L, ScalarInt64(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(ExoSqlite.BusyTimeoutMs, ScalarInt64(connection, "PRAGMA busy_timeout;"));
        Assert.Equal(ExoSqlite.WalAutoCheckpointPages, ScalarInt64(connection, "PRAGMA wal_autocheckpoint;"));

        Exec(connection, "CREATE TABLE kv (k INTEGER PRIMARY KEY, v TEXT NOT NULL);");
        using (var tx = connection.BeginTransaction())
        {
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO kv (k, v) VALUES ($k, $v);";
            var pk = ins.Parameters.Add("$k", SqliteType.Integer);
            var pv = ins.Parameters.Add("$v", SqliteType.Text);
            for (var i = 0; i < 500; i++)
            {
                pk.Value = i;
                pv.Value = "val-" + i;
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }

        Assert.Equal(500L, ScalarInt64(connection, "SELECT COUNT(*) FROM kv;"));
        Assert.Equal("val-499", ScalarString(connection, "SELECT v FROM kv WHERE k = 499;"));
    }

    [Fact]
    public void ReadOnlyPragmasEnforceQueryOnlyAndReadCopiedWal()
    {
        var source = Path.Combine(_folder, "src.db");
        var copy = Path.Combine(_folder, "copy.db");
        using (var writer = ExoSqlite.OpenReadWrite(source))
        {
            Exec(writer, "PRAGMA wal_autocheckpoint=0; CREATE TABLE app_id (exe_path TEXT NOT NULL);");
            Exec(writer, "INSERT INTO app_id (exe_path) VALUES ('C:\\Games\\Sample\\game.exe');");
            using var readerHold = ExoSqlite.OpenReadOnly(source);
            Assert.Equal("C:\\Games\\Sample\\game.exe", ScalarString(readerHold, "SELECT exe_path FROM app_id LIMIT 1;"));
            Assert.Equal(1L, ScalarInt64(readerHold, "PRAGMA query_only;"));
            Assert.Equal(ExoSqlite.MmapSizeBytes, ScalarInt64(readerHold, "PRAGMA mmap_size;"));
            Assert.Throws<SqliteException>(() => Exec(readerHold, "INSERT INTO app_id (exe_path) VALUES ('nope');"));

            File.Copy(source, copy);
            Assert.True(File.Exists(source + "-wal"));
            File.Copy(source + "-wal", copy + "-wal");
        }

        using var copyConn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString());
        copyConn.Open();
        ExoSqlite.ApplyReadOnlyPragmas(copyConn);
        Assert.Equal("C:\\Games\\Sample\\game.exe", ScalarString(copyConn, "SELECT exe_path FROM app_id LIMIT 1;"));
        Assert.Throws<SqliteException>(() => Exec(copyConn, "INSERT INTO app_id (exe_path) VALUES ('blocked');"));
    }

    [Fact]
    public void TruncatedWalTailFrameRecoversAllCommittedRows()
    {
        var live = Path.Combine(_folder, "live.db");
        var recovered = Path.Combine(_folder, "recovered.db");
        using (var writer = ExoSqlite.OpenReadWrite(live))
        {
            Exec(writer, "PRAGMA wal_autocheckpoint=0; CREATE TABLE items (id INTEGER PRIMARY KEY, batch TEXT NOT NULL, payload TEXT NOT NULL);");
            using var readerHold = ExoSqlite.OpenReadOnly(live);
            _ = ScalarInt64(readerHold, "SELECT COUNT(*) FROM items;");

            InsertBatch(writer, 1, 50, "batch-1");
            InsertBatch(writer, 51, 50, "batch-2");

            File.Copy(live, recovered);
            Assert.True(File.Exists(live + "-wal"));
            File.Copy(live + "-wal", recovered + "-wal");
        }

        using (var walStream = new FileStream(recovered + "-wal", FileMode.Append, FileAccess.Write, FileShare.None))
        {
            var partialFrame = new byte[1536];
            for (var i = 0; i < partialFrame.Length; i++) partialFrame[i] = (byte)((i * 37) ^ 0x5A);
            walStream.Write(partialFrame);
            walStream.Flush(flushToDisk: true);
        }

        using var reopened = ExoSqlite.OpenReadWrite(recovered);
        Assert.Equal("ok", ScalarString(reopened, "PRAGMA integrity_check;"));
        Assert.Equal(100L, ScalarInt64(reopened, "SELECT COUNT(*) FROM items;"));
        Assert.Equal(50L, ScalarInt64(reopened, "SELECT COUNT(*) FROM items WHERE batch = 'batch-1';"));
        Assert.Equal(50L, ScalarInt64(reopened, "SELECT COUNT(*) FROM items WHERE batch = 'batch-2';"));

        InsertBatch(reopened, 101, 25, "post-recovery");
        Assert.Equal(125L, ScalarInt64(reopened, "SELECT COUNT(*) FROM items;"));
        Exec(reopened, "PRAGMA wal_checkpoint(TRUNCATE);");
        Assert.Equal("ok", ScalarString(reopened, "PRAGMA integrity_check;"));
    }

    [Fact]
    public void ProcessCrashMidWritePreservesCommittedRowsAndRollsBackUncommittedTransaction()
    {
        var dbPath = Path.Combine(_folder, "crash.db");
        using (var init = ExoSqlite.OpenReadWrite(dbPath))
        {
            Exec(init, "CREATE TABLE items (id INTEGER PRIMARY KEY, batch TEXT NOT NULL, payload TEXT NOT NULL);");
            InsertBatch(init, 1, 100, "baseline");
        }

        var sqliteDll = LocateSqliteDll();
        var scriptPath = Path.Combine(_folder, "crash-writer.ps1");
        File.WriteAllText(scriptPath, """
            param([string]$DllPath, [string]$DbPath)
            $code = @"
            using System;
            using System.Runtime.InteropServices;
            public static class SqliteCrasher
            {
                [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                public static extern IntPtr LoadLibraryW(string path);

                [DllImport("kernel32.dll")]
                public static extern IntPtr GetCurrentProcess();

                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                public static extern bool TerminateProcess(IntPtr process, uint exitCode);

                [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
                public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);

                [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
                public static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, out IntPtr err);

                public static void Exec(IntPtr db, string sql)
                {
                    int rc = sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out _);
                    if (rc != 0) throw new InvalidOperationException("sqlite3_exec failed: " + rc);
                }
            }
            "@
            Add-Type -TypeDefinition $code -Language CSharp
            if ([SqliteCrasher]::LoadLibraryW($DllPath) -eq [IntPtr]::Zero) { exit 70 }
            $db = [IntPtr]::Zero
            if ([SqliteCrasher]::sqlite3_open_v2($DbPath, [ref]$db, 6, [IntPtr]::Zero) -ne 0) { exit 71 }
            [SqliteCrasher]::Exec($db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA mmap_size=67108864; PRAGMA cache_size=-16384; PRAGMA temp_store=MEMORY; PRAGMA foreign_keys=ON; PRAGMA wal_autocheckpoint=1000;")
            [SqliteCrasher]::Exec($db, "BEGIN IMMEDIATE;")
            $payload = "X" * 2048
            for ($i = 101; $i -le 200; $i++) {
                [SqliteCrasher]::Exec($db, "INSERT INTO items (id, batch, payload) VALUES ($i, 'committed-child', '$payload');")
            }
            [SqliteCrasher]::Exec($db, "COMMIT;")
            [SqliteCrasher]::Exec($db, "BEGIN IMMEDIATE;")
            for ($i = 201; $i -le 450; $i++) {
                [SqliteCrasher]::Exec($db, "INSERT INTO items (id, batch, payload) VALUES ($i, 'uncommitted-crash', '$payload');")
            }
            [void][SqliteCrasher]::TerminateProcess([SqliteCrasher]::GetCurrentProcess(), 99)
            """);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-File", scriptPath, sqliteDll, dbPath },
        });
        Assert.NotNull(process);
        var stderr = process!.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "crash writer did not exit in time: " + stderr);
        Assert.Equal(99, process.ExitCode);

        using var reopened = ExoSqlite.OpenReadWrite(dbPath);
        Assert.Equal("ok", ScalarString(reopened, "PRAGMA integrity_check;"));
        Assert.Equal(100L, ScalarInt64(reopened, "SELECT COUNT(*) FROM items WHERE batch = 'baseline';"));
        Assert.Equal(100L, ScalarInt64(reopened, "SELECT COUNT(*) FROM items WHERE batch = 'committed-child';"));
        Assert.Equal(0L, ScalarInt64(reopened, "SELECT COUNT(*) FROM items WHERE batch = 'uncommitted-crash';"));
        Assert.Equal(200L, ScalarInt64(reopened, "SELECT COUNT(*) FROM items;"));

        InsertBatch(reopened, 501, 20, "after-crash");
        Assert.Equal(220L, ScalarInt64(reopened, "SELECT COUNT(*) FROM items;"));
        Exec(reopened, "PRAGMA wal_checkpoint(TRUNCATE);");
        Assert.Equal("ok", ScalarString(reopened, "PRAGMA integrity_check;"));
    }

    private static string LocateSqliteDll()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
        if (File.Exists(direct)) return direct;
        var rid = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "e_sqlite3.dll");
        if (File.Exists(rid)) return rid;
        throw new FileNotFoundException("e_sqlite3.dll was not found beside the test output.", direct);
    }

    private static void InsertBatch(SqliteConnection connection, int startId, int count, string batch)
    {
        var payload = new string('A', 512);
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO items (id, batch, payload) VALUES ($id, $batch, $payload);";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pBatch = cmd.Parameters.Add("$batch", SqliteType.Text);
        var pPayload = cmd.Parameters.Add("$payload", SqliteType.Text);
        pBatch.Value = batch;
        pPayload.Value = payload;
        for (var i = 0; i < count; i++)
        {
            pId.Value = startId + i;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ScalarInt64(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }
}

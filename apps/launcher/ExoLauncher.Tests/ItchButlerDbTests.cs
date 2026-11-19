using ExoLauncher.Adapters;
using ExoLauncher.Models;
using Microsoft.Data.Sqlite;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the itch app's own database can mislead the library: another profile's keys shown as ours, tools and
/// assets on the games shelf, one game listed once per key, a stored API key read or shown, the live file
/// written or locked, and a damaged file read as "no games" (every owned row would drop out).
/// Fixture: the table and column names butler writes (itchio/butler database/models: hades snake_case of
/// Profile, itchio.DownloadKey, the games table). Unverified on device: itch is not installed on the test PC.
/// </summary>
public sealed class ItchButlerDbTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "exo-itch-tests-" + Guid.NewGuid().ToString("N"));

    public ItchButlerDbTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_folder, recursive: true); } catch { /* temp */ }
    }

    private string Database(params string[] statements)
    {
        var path = Path.Combine(_folder, "butler.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE profiles (id INTEGER PRIMARY KEY, user_id INTEGER, last_connected TEXT, api_key TEXT);
            CREATE TABLE games (id INTEGER PRIMARY KEY, title TEXT, classification TEXT, cover_url TEXT);
            CREATE TABLE download_keys (id INTEGER PRIMARY KEY, game_id INTEGER, owner_id INTEGER, created_at TEXT, updated_at TEXT);
            INSERT INTO profiles VALUES (1, 7, '2026-11-01T10:00:00Z', 'SECRET-API-KEY-OLD');
            INSERT INTO profiles VALUES (2, 9, '2026-11-05T10:00:00Z', 'SECRET-API-KEY-NEW');
            INSERT INTO games VALUES (100, 'Celeste', 'game', 'https://img.itch.zone/celeste.png');
            INSERT INTO games VALUES (101, 'Asset Pack', 'assets', NULL);
            INSERT INTO games VALUES (102, 'Soundtrack Tool', 'tool', NULL);
            INSERT INTO games VALUES (103, 'Other Profile Game', 'game', NULL);
            INSERT INTO games VALUES (104, 'Untyped Game', NULL, NULL);
            INSERT INTO download_keys VALUES (1, 100, 9, NULL, NULL);
            INSERT INTO download_keys VALUES (2, 100, 9, NULL, NULL);
            INSERT INTO download_keys VALUES (3, 101, 9, NULL, NULL);
            INSERT INTO download_keys VALUES (4, 102, 9, NULL, NULL);
            INSERT INTO download_keys VALUES (5, 103, 7, NULL, NULL);
            INSERT INTO download_keys VALUES (6, 104, 9, NULL, NULL);
            """;
        command.ExecuteNonQuery();
        foreach (var statement in statements)
        {
            using var extra = connection.CreateCommand();
            extra.CommandText = statement;
            extra.ExecuteNonQuery();
        }
        return path;
    }

    [Fact]
    public void OnlyTheMostRecentProfilesKeysAreOwned()
    {
        var owned = ItchButlerDb.ReadOwned(Database());

        Assert.DoesNotContain(owned, game => game.Title == "Other Profile Game");
        Assert.Contains(owned, game => game.Title == "Celeste");
    }

    [Fact]
    public void ToolsAndAssetsAreNotOnTheGamesShelfButUntypedGamesAre()
    {
        var titles = ItchButlerDb.ReadOwned(Database()).Select(game => game.Title).ToArray();

        Assert.DoesNotContain("Asset Pack", titles);
        Assert.DoesNotContain("Soundtrack Tool", titles);
        Assert.Contains("Untyped Game", titles);
    }

    [Fact]
    public void AGameWithTwoKeysIsListedOnce()
    {
        var celeste = ItchButlerDb.ReadOwned(Database()).Where(game => game.Title == "Celeste").ToArray();

        var only = Assert.Single(celeste);
        Assert.Equal("itch:100", only.Id);
        Assert.Equal(StoreKind.Itch, only.Store);
        Assert.False(only.Installed);
        Assert.True(only.Owned);
        Assert.Equal("https://img.itch.zone/celeste.png", only.CoverUrl);
    }

    [Fact]
    public void TheStoredApiKeyIsNeverRead()
    {
        var owned = ItchButlerDb.ReadOwned(Database());

        Assert.All(owned, game => Assert.DoesNotContain("SECRET", game.Id + game.Title + game.LaunchTarget + game.LaunchNote, StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingDatabaseIsNoGamesAndTheFileIsLeftAlone()
    {
        var path = Path.Combine(_folder, "missing", "butler.db");

        Assert.Empty(ItchButlerDb.ReadOwned(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ADamagedDatabaseFailsTheScanInsteadOfEmptyingTheShelf()
    {
        var path = Path.Combine(_folder, "butler.db");
        File.WriteAllText(path, "not a sqlite file at all");

        Assert.ThrowsAny<Exception>(() => ItchButlerDb.ReadOwned(path));
    }

    [Fact]
    public void ADatabaseReadInPlaceLeavesNoTempFolder()
    {
        var temp = Path.Combine(_folder, "temp");
        Directory.CreateDirectory(temp);

        var owned = ItchButlerDb.ReadOwned(Database(), temp);

        Assert.NotEmpty(owned);
        Assert.Empty(Directory.GetDirectories(temp, "exo-itch-*"));
    }

    [Fact]
    public void AStaleTempCopyIsSweptAndAFreshOneIsKept()
    {
        var temp = Path.Combine(_folder, "temp");
        var stale = Path.Combine(temp, "exo-itch-" + Guid.NewGuid().ToString("N"));
        var fresh = Path.Combine(temp, "exo-itch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        Directory.SetCreationTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        _ = ItchButlerDb.ReadOwned(Database(), temp);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void ADamagedDatabaseLeavesNoTempFolderBehind()
    {
        var temp = Path.Combine(_folder, "temp");
        Directory.CreateDirectory(temp);
        var path = Path.Combine(_folder, "butler.db");
        File.WriteAllText(path, "not a sqlite file at all");

        Assert.ThrowsAny<Exception>(() => ItchButlerDb.ReadOwned(path, temp));
        Assert.Empty(Directory.GetDirectories(temp, "exo-itch-*"));
    }

    [Fact]
    public void TheLiveDatabaseIsNotChanged()
    {
        var path = Database();
        var before = File.ReadAllBytes(path);

        _ = ItchButlerDb.ReadOwned(path);

        Assert.Equal(before, File.ReadAllBytes(path));
    }
}

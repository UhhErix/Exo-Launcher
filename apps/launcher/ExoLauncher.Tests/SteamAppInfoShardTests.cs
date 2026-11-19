using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using static ExoLauncher.Services.AntiCheatCheck;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways reading Steam's sharded appinfo (<c>appcache/appinfo/appinfo_XX.vdf</c>, magic 0x0756442A, since November 2026)
/// could fail, on small shards built the way Steam lays them out (no real records):
/// - the old single-file path is still the only one read, so every name, record and anti-cheat word is silently missing;
/// - a stale appinfo.vdf beside the shards wins over them, or a file that only looks like a shard is read;
/// - an index entry longer than usual, an unknown key or value type, or a key table entry Exo doesn't know throws the
///   walk off;
/// - a shard cut short or with a record Steam is still writing (its hash doesn't match) is taken as whole, so an
///   anti-cheat word goes missing instead of "cannot tell";
/// - a header that lies (sizes, counts, shard index) reads past the file;
/// - an app in a later shard is not found, or launch entries lose their per-entry OS and branch.
/// </summary>
public sealed class SteamAppInfoShardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("exo-shards-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_root, recursive: true);

    private const uint Magic = 0x0756442A;

    /// <summary>Binary KeyValues whose keys are indexes into a shard's key table, added as they are first used.</summary>
    private sealed class Kv(List<string> keys)
    {
        private readonly MemoryStream _s = new();
        private Kv Key(byte kind, string key)
        {
            var at = keys.IndexOf(key);
            if (at < 0) { at = keys.Count; keys.Add(key); }
            _s.WriteByte(kind);
            _s.Write(BitConverter.GetBytes(at));
            return this;
        }
        internal Kv Open(string key) => Key(0, key);
        internal Kv Str(string key, string value) { Key(1, key); _s.Write(Encoding.UTF8.GetBytes(value)); _s.WriteByte(0); return this; }
        internal Kv Int(string key, int value) { Key(2, key); _s.Write(BitConverter.GetBytes(value)); return this; }
        internal Kv U64(string key, ulong value) { Key(7, key); _s.Write(BitConverter.GetBytes(value)); return this; }
        internal Kv Close() { _s.WriteByte(8); return this; }
        internal byte[] Bytes => _s.ToArray();
    }

    /// <summary>
    /// A shard as Steam writes it: the 44-byte header, the index (each entry with <paramref name="more"/> bytes after its
    /// first 16), the shared key table, then each record as a SHA-1 of its key values and the key values.
    /// </summary>
    private static byte[] Shard(uint index, List<string> keys, (uint AppId, byte[] Kv)[] apps, int more = 64)
    {
        var data = new MemoryStream();
        var entries = new MemoryStream();
        foreach (var (appId, kv) in apps)
        {
            var offset = (uint)data.Length;
            data.Write(SHA1.HashData(kv));
            data.Write(kv);
            var head = new byte[16].AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(head, appId);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], offset);
            BinaryPrimitives.WriteUInt32LittleEndian(head[8..], (uint)(data.Length - offset));
            BinaryPrimitives.WriteUInt32LittleEndian(head[12..], (uint)more);
            entries.Write(head);
            var rest = new byte[more];
            rest[0] = 0x2a; // a change number Exo doesn't read
            entries.Write(rest);
        }
        var table = new MemoryStream();
        foreach (var key in keys) { table.Write(Encoding.UTF8.GetBytes(key)); table.WriteByte(0); }
        var header = new byte[44];
        uint[] fields = [Magic, 1, index, 256, (uint)apps.Length, (uint)entries.Length, (uint)keys.Count, (uint)table.Length, (uint)data.Length];
        for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 4), fields[i]);
        return [.. header, .. entries.ToArray(), .. table.ToArray(), .. data.ToArray()];
    }

    private static byte[] Game(List<string> keys, uint appId, string name, bool vac = false) =>
        new Kv(keys).Open("appinfo").Int("appid", (int)appId).Open("common").Str("name", name).Str("type", "Game")
            .Open("category").Str(vac ? "category_8" : "category_2", "1").Close().Close().Close().Bytes;

    /// <summary>A game with Steam's two Play entries for a game that ships without its anti-cheat for offline play.</summary>
    private static byte[] TwoWayGame(List<string> keys) =>
        new Kv(keys).Open("appinfo").Int("appid", 9100).Open("common").Str("name", "Court Game").Str("type", "Game").Close()
            .Open("config").Str("installdir", "Court Game").Open("launch")
                .Open("0").Str("executable", "start_protected_game.exe").Str("type", "default")
                    .Open("description_loc").Str("english", "Play Court Game").Close().Str("description", "Play Court Game")
                    .Open("config").Str("oslist", "windows").Close().Close()
                .Open("1").Str("executable", "CourtGame.exe").Str("arguments", "-offline").Str("type", "option1")
                    .Open("description_loc").Str("english", "Court Game without anti-cheat (offline only)").Close()
                    .Str("workingdir", "bin").Str("vacmodulefilename", "none")
                    .Open("config").Str("oslist", "windows").Str("BetaKey", "public_test").Str("realm", "Steam").Close().Close()
                .Open("2").Str("type", "none").Str("description", "No program").Close()
            .Close().Close()
            .Open("extended").U64("some_future_id", 1UL << 40).Close()
            .Close().Bytes;

    private void WriteShard(uint index, byte[] bytes)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "appcache", "appinfo")).FullName;
        File.WriteAllBytes(Path.Combine(folder, $"appinfo_{index:x2}.vdf"), bytes);
    }

    [Fact]
    public void ReadsNamesTypesAndParentsFromAShard()
    {
        var keys = new List<string>();
        var dlc = new Kv(keys).Open("appinfo").Open("common").Str("name", " Bonus Pack ").Str("type", "DLC").Int("parent", 9100).Close().Close().Bytes;
        var names = SteamAppInfoNames.Parse(Shard(0x12, keys, [(9100, TwoWayGame(keys)), (9101, dlc)]));
        Assert.Equal(new SteamAppInfoNames.Entry("Court Game", "Game", ""), names["9100"]);
        Assert.Equal(new SteamAppInfoNames.Entry("Bonus Pack", "DLC", "9100"), names["9101"]);
    }

    [Fact]
    public void ReadsLaunchEntriesWithUnknownFieldsAndALongerIndexEntry()
    {
        var keys = new List<string>();
        var shard = Shard(0x12, keys, [(9000, Game(keys, 9000, "Other")), (9100, TwoWayGame(keys))], more: 0x44);
        var app = SteamAppInfoNames.ReadApps(shard, new HashSet<uint> { 9100 })[9100];
        Assert.Equal("Court Game", app.Section("common").Value("name"));
        Assert.Equal(
            [
                new SteamAppInfoNames.LaunchEntry("0", "start_protected_game.exe", "", "Play Court Game", "default", "", "windows", ""),
                new SteamAppInfoNames.LaunchEntry("1", "CourtGame.exe", "-offline", "Court Game without anti-cheat (offline only)", "option1", "bin", "windows", "public_test"),
            ],
            SteamAppInfoNames.LaunchEntries(app));
        Assert.Equal((1UL << 40).ToString(System.Globalization.CultureInfo.InvariantCulture), app.Section("extended").Value("some_future_id"));
        Assert.Empty(SteamAppInfoNames.LaunchEntries(SteamKeyValues.Empty));
    }

    [Fact]
    public void AShardCutShortOrMidWriteIsNotWhole()
    {
        var keys = new List<string>();
        var whole = Shard(3, keys, [(1000, Game(keys, 1000, "First", vac: true)), (2000, Game(keys, 2000, "Second"))]);
        Assert.True(SteamAppInfoNames.EachApp(whole, (_, _) => { }));
        foreach (var cut in new[] { 1, 30, whole.Length - 44, whole.Length - 1 })
        {
            Assert.False(SteamAppInfoNames.EachApp(whole[..^cut], (_, _) => { }));
            Assert.Empty(SteamAppInfoNames.Parse(whole[..^cut]));
        }
        // Steam rewriting the second record in place: its hash no longer matches. The first was read, the file isn't whole.
        var torn = (byte[])whole.Clone();
        torn[^3] ^= 0xff;
        var seen = new List<uint>();
        Assert.False(SteamAppInfoNames.EachApp(torn, (id, _) => seen.Add(id)));
        Assert.Equal([1000u], seen);
    }

    [Fact]
    public void AHeaderThatLiesIsRefused()
    {
        var keys = new List<string>();
        var good = Shard(5, keys, [(1000, Game(keys, 1000, "Only"))]);
        Assert.Single(SteamAppInfoNames.Parse(good));
        byte[] With(int at, uint value)
        {
            var copy = (byte[])good.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(at), value);
            return copy;
        }
        Assert.False(SteamAppInfoNames.EachApp(With(8, 256), (_, _) => { }));           // shard index past the count
        Assert.False(SteamAppInfoNames.EachApp(With(12, 0), (_, _) => { }));            // no shards
        Assert.False(SteamAppInfoNames.EachApp(With(16, 2), (_, _) => { }));            // more apps than the index holds
        Assert.False(SteamAppInfoNames.EachApp(With(20, uint.MaxValue), (_, _) => { })); // sizes past the file
        Assert.False(SteamAppInfoNames.EachApp(With(24, 1_000_001), (_, _) => { }));    // absurd key count
        Assert.False(SteamAppInfoNames.EachApp(With(24, (uint)keys.Count + 1), (_, _) => { })); // a key the table lacks
        Assert.False(SteamAppInfoNames.EachApp(With(44 + 12, 0x10000), (_, _) => { })); // an entry running past the index
        Assert.False(SteamAppInfoNames.EachApp(With(44 + 8, 4), (_, _) => { }));        // a record too short for its hash
        Assert.False(SteamAppInfoNames.EachApp(good[..43], (_, _) => { }));
        Assert.False(SteamAppInfoNames.IsKnownFormat(With(0, 0x0756442B)));
        Assert.True(SteamAppInfoNames.IsKnownFormat(good));
    }

    [Fact]
    public void TheShardsAreReadAndAStaleSingleFileBesideThemIsNot()
    {
        Assert.Empty(SteamAppInfoNames.Files(_root));
        var appcache = Directory.CreateDirectory(Path.Combine(_root, "appcache")).FullName;
        File.WriteAllBytes(Path.Combine(appcache, "appinfo.vdf"), [1, 2, 3]);
        Assert.Equal([Path.Combine(appcache, "appinfo.vdf")], SteamAppInfoNames.Files(_root));

        var k1 = new List<string>();
        var k2 = new List<string>();
        WriteShard(0x00, Shard(0x00, k1, [(1000, Game(k1, 1000, "First"))]));
        WriteShard(0xfe, Shard(0xfe, k2, [(9100, TwoWayGame(k2))]));
        File.WriteAllText(Path.Combine(appcache, "appinfo", "appinfo_zz.vdf"), "not a shard");
        File.WriteAllText(Path.Combine(appcache, "appinfo", "appinfo_100.vdf"), "not a shard");
        var files = SteamAppInfoNames.Files(_root).Select(Path.GetFileName);
        Assert.Equal(["appinfo_00.vdf", "appinfo_fe.vdf"], files);

        var apps = SteamAppInfoNames.ReadApps(_root, new HashSet<uint> { 9100, 1000, 77 });
        Assert.Equal([1000u, 9100u], apps.Keys.Order());
        Assert.Equal("CourtGame.exe", SteamAppInfoNames.LaunchEntries(apps[9100])[1].Executable);
        Assert.Empty(SteamAppInfoNames.ReadApps(_root, new HashSet<uint>()));

        var names = SteamAppInfoNames.LoadAt(_root, DateTime.UtcNow);
        Assert.Equal("First", names["1000"].Name);
        Assert.Equal("Court Game", names["9100"].Name);
    }

    [Fact]
    public void AntiCheatWordsComeFromEveryShardAndAHalfWrittenOneSaysCannotTell()
    {
        var k1 = new List<string>();
        var k2 = new List<string>();
        WriteShard(0x01, Shard(0x01, k1, [(1000, Game(k1, 1000, "Shooter", vac: true))]));
        var second = Shard(0x02, k2, [(2000, Game(k2, 2000, "Puzzle"))]);
        WriteShard(0x02, second[..^5]);
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var steam = new SteamVacFile(() => SteamAppInfoNames.Files(_root), () => now);
        // One shard mid-write: nothing is kept, every Steam game is "cannot tell" (possibly VAC), never "none".
        Assert.Equal(SteamWord.CannotTell, steam.Word(1000));
        Assert.Equal(SteamWord.CannotTell, steam.Word(2000));
        WriteShard(0x02, second);
        now += SteamVacFile.RetryAfter + TimeSpan.FromSeconds(1);
        Assert.Equal(SteamWord.Vac, steam.Word(1000));
        Assert.Equal(SteamWord.None, steam.Word(2000));
        Assert.Equal(SteamWord.CannotTell, steam.Word(3000));
        Assert.Equal(2, steam.Parses);
        // Nothing changed: not parsed again.
        Assert.Equal(SteamWord.Vac, steam.Word(1000));
        Assert.Equal(2, steam.Parses);
    }
}

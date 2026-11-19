using System.Buffers.Binary;
using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Services.AntiCheatCheck;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways reading Steam's appinfo.vdf for Valve Anti-Cheat could fail, through the real reader on files shaped as Steam
/// writes them (format 41: a header, records of an app id, a size, a 60-byte header and binary key values, then the
/// string table):
/// - one failed or half-written read makes Steam games unknown, or is kept as if it were the whole file;
/// - a newer format or a very large file makes every Steam game unknown for good (it must be "possibly VAC");
/// - each game re-reads and parses the whole file;
/// - a changed file is not read again;
/// - Valve's own VAC titles depend on the file at all.
/// </summary>
public sealed class SteamVacFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-appinfo-").FullName;
    private string File_ => Path.Combine(_dir, "appinfo.vdf");

    public void Dispose() => DeletionGate.DeleteDirectory(_dir, recursive: true);

    private static readonly string[] Table = ["appinfo", "common", "name", "category", "category_8", "category_2", "extended", "anti_cheat_support_url"];

    private static int K(string key) => Array.IndexOf(Table, key);

    private sealed class Kv
    {
        private readonly MemoryStream _s = new();
        private Kv Key(byte kind, string key) { _s.WriteByte(kind); _s.Write(BitConverter.GetBytes(K(key))); return this; }
        internal Kv Open(string key) => Key(0, key);
        internal Kv Str(string key, string value) { Key(1, key); _s.Write(Encoding.UTF8.GetBytes(value)); _s.WriteByte(0); return this; }
        internal Kv Close() { _s.WriteByte(8); return this; }
        internal byte[] Bytes => _s.ToArray();
    }

    private static byte[] App(string name, bool vac = false, bool listed = false)
    {
        var kv = new Kv().Open("appinfo").Open("common").Str("name", name).Open("category").Str(vac ? "category_8" : "category_2", "1").Close().Close();
        if (listed) kv.Open("extended").Str("anti_cheat_support_url", "https://example.invalid/ac").Close();
        return kv.Close().Bytes;
    }

    /// <summary>appinfo.vdf as Steam writes it; <paramref name="magic"/> its format, <paramref name="cut"/> bytes taken off the end of the records (Steam mid-write).</summary>
    private static byte[] AppInfo((uint AppId, byte[] Kv)[] apps, uint magic = 0x07564429, int cut = 0)
    {
        var body = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); body.Write(b); }
        body.Write(new byte[16]);
        foreach (var (appId, kv) in apps)
        {
            U32(appId);
            U32((uint)(60 + kv.Length));
            body.Write(new byte[60]);
            body.Write(kv);
        }
        if (cut > 0) body.SetLength(body.Length - cut);
        else U32(0);
        var tableOffset = body.Length;
        U32((uint)Table.Length);
        foreach (var s in Table) { body.Write(Encoding.UTF8.GetBytes(s)); body.WriteByte(0); }
        var data = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data, magic);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), tableOffset);
        return data;
    }

    private static readonly (uint, byte[])[] ThreeGames = [(1000, App("Shooter", vac: true)), (2000, App("Puzzle")), (3000, App("Arena", listed: true))];

    private void Write(byte[] data, DateTime? when = null)
    {
        File.WriteAllBytes(File_, data);
        File.SetLastWriteTimeUtc(File_, when ?? new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void TheFileIsReadOnceAndEachAppGetsSteamsWord()
    {
        Write(AppInfo(ThreeGames));
        var steam = new SteamVacFile(() => [File_]);
        Assert.Equal(SteamWord.Vac, steam.Word(1000));
        Assert.Equal(SteamWord.None, steam.Word(2000));
        Assert.Equal(SteamWord.Listed, steam.Word(3000));
        Assert.Equal(SteamWord.CannotTell, steam.Word(4000));
        Assert.Equal(1, steam.Parses);
        // The file changes (Steam learned a new app): read again, once.
        Write(AppInfo([.. ThreeGames, (4000, App("New", vac: true))]), new DateTime(2026, 11, 10, 13, 0, 0, DateTimeKind.Utc));
        Assert.Equal(SteamWord.Vac, steam.Word(4000));
        Assert.Equal(SteamWord.Vac, steam.Word(1000));
        Assert.Equal(2, steam.Parses);
    }

    [Fact]
    public void AHalfWrittenFileIsNotKeptAndIsReadAgainSoon()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        Write(AppInfo(ThreeGames));
        var steam = new SteamVacFile(() => [File_], () => now);
        Assert.Equal(SteamWord.Vac, steam.Word(1000));
        // Steam rewrites it and Exo reads it mid-write: the last whole read stays.
        Write(AppInfo([(4000, App("New", vac: true)), .. ThreeGames], cut: 30), now.AddHours(1));
        Assert.Equal(SteamWord.Vac, steam.Word(1000));
        Assert.Equal(SteamWord.Listed, steam.Word(3000));
        Assert.Equal(SteamWord.CannotTell, steam.Word(4000));
        // Finished: read again after the wait, not before.
        Write(AppInfo([.. ThreeGames, (4000, App("New", vac: true))]), now.AddHours(2));
        Assert.Equal(SteamWord.CannotTell, steam.Word(4000));
        now += SteamVacFile.RetryAfter + TimeSpan.FromSeconds(1);
        Assert.Equal(SteamWord.Vac, steam.Word(4000));
    }

    [Fact]
    public void AHalfWrittenFirstReadSaysCannotTell()
    {
        Write(AppInfo(ThreeGames, cut: 30));
        Assert.Equal(SteamWord.CannotTell, new SteamVacFile(() => [File_]).Word(1000));
    }

    [Fact]
    public void ANewerFormatOrAHugeFileIsPossiblyVacNeverUnknown()
    {
        Write(AppInfo(ThreeGames, magic: 0x0756442B));
        var newer = new SteamVacFile(() => [File_]);
        Assert.Equal(SteamWord.CannotTell, newer.Word(1000));
        Assert.Equal(0, newer.Parses);
        Write(AppInfo(ThreeGames));
        var huge = new SteamVacFile(() => [File_], maxBytes: 64);
        Assert.Equal(SteamWord.CannotTell, huge.Word(1000));
        Assert.Equal(0, huge.Parses);
        // Through the whole check: a Steam game is possibly VAC, not unknown, and Valve's own titles are VAC whatever the file says.
        var install = Directory.CreateDirectory(Path.Combine(_dir, "game")).FullName;
        File.WriteAllText(Path.Combine(install, "game.exe"), "x");
        GameEntry Steam(string id) => new() { Id = "steam:" + id, Title = "A game", Store = StoreKind.Steam, Installed = true, Path = install, LaunchTarget = id };
        var answer = Of(Steam("2000"), [], newer.Word, TestContext.Current.CancellationToken);
        Assert.Equal(Kind.PossiblyVac, answer.Kind);
        Assert.True(Allows(Steam("2000"), answer, Change.GameSettingsFile));
        Assert.False(Allows(Steam("2000"), answer, Change.FrameGenMod));
        Assert.Equal(Kind.Found, Of(Steam("440"), [], newer.Word, TestContext.Current.CancellationToken).Kind);
    }

    [Fact]
    public void NoFileSaysCannotTell() => Assert.Equal(SteamWord.CannotTell, new SteamVacFile(() => [Path.Combine(_dir, "missing.vdf")]).Word(1000));
}

using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using static ExoLauncher.Services.GameCompression;

namespace ExoLauncher.Tests;

/// <summary>
/// Compress games' decisions, fed fixtures (no game folder is touched), and Windows' half on a temp folder of this PC.
/// Ways it could fail:
/// - LZX on a game that is still being updated (a second full write on an SSD each patch), or on a weak processor; XPRESS
///   on a hard drive that would load faster with more packing;
/// - a hard drive given many threads (seeking), or a CPU budget of zero threads;
/// - a program, library or anti-cheat file compressed in a game with anti-cheat; a game with anti-cheat compressed without
///   the player's yes, with a yes for another set, or a Riot game or an unreadable install compressed at all;
/// - a file of one cluster, a sparse, encrypted or LZNT1 file, a link, or DirectStorage's big files compressed; an
///   extension the sample found under 5% compressed anyway, or a good one skipped;
/// - a stopped job starting over (files already in the algorithm not counted as done), a job running when Exo closed
///   reading back as running, or the player's yes lost on a restart;
/// - an update's rewritten files not found (or every file counted, compressed ones too), and a trickle offered as worth it;
/// - an estimate over the file's own size, or a chunk that didn't shrink counted smaller;
/// - a drive root, Windows, OneDrive, WindowsApps, FAT/exFAT or a network share accepted;
/// - the WOF calls misdeclared: a file isn't compressed, reads back different, or doesn't come back.
/// </summary>
public sealed class GameCompressionTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddDays(-90);
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static FileFact F(string path, long length, Algorithm? current = null, FileAttributes attributes = FileAttributes.Archive, DateTime? written = null) =>
        new(path, length, attributes, written ?? Old, current, current is null ? RoundUp(length, 4096) : length / 2);

    // ------------------------------------------------------------------ algorithm and threads

    [Fact]
    public void Xpress8k_by_default_lzx_only_for_big_settled_games_on_a_strong_cpu()
    {
        Assert.Equal(Algorithm.Xpress8K, Choose(20 * GB, 16, false, Old, Now));
        Assert.Equal(Algorithm.Lzx, Choose(60 * GB, 16, false, Old, Now));
        // Written last week: still being patched, so no LZX on the SSD.
        Assert.Equal(Algorithm.Xpress8K, Choose(60 * GB, 16, false, Now.AddDays(-7), Now));
        Assert.Equal(Algorithm.Xpress8K, Choose(60 * GB, 8, false, Old, Now));
        Assert.Equal(Algorithm.Xpress4K, Choose(60 * GB, 4, false, Old, Now));
    }

    [Fact]
    public void A_hard_drive_packs_tighter()
    {
        Assert.Equal(Algorithm.Lzx, Choose(12 * GB, 8, true, Old, Now));
        Assert.Equal(Algorithm.Xpress16K, Choose(5 * GB, 8, true, Old, Now));
        Assert.Equal(Algorithm.Xpress16K, Choose(50 * GB, 8, true, Now.AddDays(-1), Now));
        Assert.Equal(Algorithm.Xpress4K, Choose(50 * GB, 2, true, Old, Now));
    }

    [Fact]
    public void Threads_follow_the_budget_and_a_hard_drive_gets_one()
    {
        Assert.Equal(4, Threads("gentle", 16, false));
        Assert.Equal(8, Threads("balanced", 16, false));
        Assert.Equal(8, Threads(null, 16, false));
        Assert.Equal(15, Threads("fast", 16, false));
        Assert.Equal(1, Threads("fast", 16, true));
        Assert.Equal(1, Threads("gentle", 2, false));
        Assert.Equal(1, Threads("fast", 1, false));
        Assert.Equal(1, Threads("balanced", 0, false));
    }

    [Theory]
    [InlineData("XPRESS4K", 0, 4096)]
    [InlineData("LZX", 1, 32768)]
    [InlineData("XPRESS8K", 2, 8192)]
    [InlineData("XPRESS16K", 3, 16384)]
    public void Wofs_own_numbers_and_chunks(string name, int wof, int chunk)
    {
        var algorithm = Parse(name)!.Value;
        Assert.Equal(wof, (int)algorithm);
        Assert.Equal(chunk, ChunkOf(algorithm));
        Assert.Equal(algorithm, Parse(Name(algorithm)));
    }

    // ------------------------------------------------------------------ where

    [Theory]
    [InlineData(@"D:\", "NTFS", true, "Exo doesn't compress a whole drive.")]
    [InlineData(@"D:\Games\Hades", "exFAT", true, "This drive can't compress files (it isn't NTFS).")]
    [InlineData(@"D:\Games\Hades", "ReFS", true, "This drive can't compress files (it isn't NTFS).")]
    [InlineData(@"\\nas\games\Hades", "NTFS", false, "Exo compresses only games on this PC's own drives.")]
    [InlineData(@"C:\Windows\Games\Solitaire", "NTFS", true, "Exo doesn't compress Windows' own folder.")]
    [InlineData(@"C:\Program Files\WindowsApps\Game_1.0_x64", "NTFS", true, "Microsoft Store games keep their own files.")]
    [InlineData(@"C:\Users\p\OneDrive\Games\Hades", "NTFS", true, "Exo doesn't compress what OneDrive syncs.")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Hades", "NTFS", true, null)]
    [InlineData(@"C:\WindowsGames\Hades", "NTFS", true, null)]
    public void Where_it_never_runs(string root, string format, bool fixedDrive, string? said) =>
        Assert.Equal(said, Refusal(root, format, fixedDrive, @"C:\Windows", [@"C:\Users\p\OneDrive"]));

    // ------------------------------------------------------------------ which files

    [Fact]
    public void Small_odd_and_done_files_stay()
    {
        Assert.Equal(Skip.Small, Decide(F("a.txt", 4096), Algorithm.Xpress8K, 4096, Guard.All, false, None));
        Assert.Equal(Skip.None, Decide(F("a.txt", 4097), Algorithm.Xpress8K, 4096, Guard.All, false, None));
        Assert.Equal(Skip.Small, Decide(F("a.txt", 60_000), Algorithm.Xpress8K, 65536, Guard.All, false, None));
        Assert.Equal(Skip.Odd, Decide(F("a.dat", 1 << 20, attributes: FileAttributes.SparseFile), Algorithm.Xpress8K, 4096, Guard.All, false, None));
        Assert.Equal(Skip.Odd, Decide(F("a.dat", 1 << 20, attributes: FileAttributes.Encrypted), Algorithm.Xpress8K, 4096, Guard.All, false, None));
        Assert.Equal(Skip.Odd, Decide(F("a.dat", 1 << 20, attributes: FileAttributes.Compressed), Algorithm.Xpress8K, 4096, Guard.All, false, None));
        Assert.Equal(Skip.Odd, Decide(F("link.dat", 1 << 20, attributes: FileAttributes.ReparsePoint), Algorithm.Xpress8K, 4096, Guard.All, false, None));
        Assert.Equal(Skip.Done, Decide(F("a.dat", 1 << 20, Algorithm.Xpress8K), Algorithm.Xpress8K, 4096, Guard.All, false, None));
        // In another algorithm: compressed again in the chosen one.
        Assert.Equal(Skip.None, Decide(F("a.dat", 1 << 20, Algorithm.Xpress4K), Algorithm.Xpress8K, 4096, Guard.All, false, None));
    }

    [Fact]
    public void With_anti_cheat_allowed_only_data_goes()
    {
        foreach (var program in new[] { "Game.exe", @"Binaries\Win64\engine.dll", "driver.sys", "plugin.asi" })
            Assert.Equal(Skip.Program, Decide(F(program, 1 << 20), Algorithm.Xpress8K, 4096, Guard.DataOnly, false, None));
        Assert.Equal(Skip.AntiCheat, Decide(F(@"EasyAntiCheat\Certificates\base.cer", 1 << 20), Algorithm.Xpress8K, 4096, Guard.DataOnly, false, None));
        Assert.Equal(Skip.AntiCheat, Decide(F(@"BattlEye\data.bin", 1 << 20), Algorithm.Xpress8K, 4096, Guard.DataOnly, false, None));
        Assert.Equal(Skip.None, Decide(F(@"Content\Paks\data.pak", 1 << 20), Algorithm.Xpress8K, 4096, Guard.DataOnly, false, None));
        // Without anti-cheat a program is compressed like the rest.
        Assert.Equal(Skip.None, Decide(F("Game.exe", 1 << 20), Algorithm.Xpress8K, 4096, Guard.All, false, None));
    }

    [Fact]
    public void Directstorage_keeps_its_big_files_and_poor_extensions_stay()
    {
        Assert.Equal(Skip.DirectStorage, Decide(F("chunk0.bin", DirectStorageBig), Algorithm.Xpress8K, 4096, Guard.All, true, None));
        Assert.Equal(Skip.None, Decide(F("chunk0.bin", DirectStorageBig - 1), Algorithm.Xpress8K, 4096, Guard.All, true, None));
        Assert.Equal(Skip.None, Decide(F("chunk0.bin", DirectStorageBig), Algorithm.Xpress8K, 4096, Guard.All, false, None));
        var poor = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bk2" };
        Assert.Equal(Skip.Poor, Decide(F(@"Movies\Intro.BK2", 1 << 20), Algorithm.Xpress8K, 4096, Guard.All, false, poor));
    }

    [Fact]
    public void The_sample_decides_poor_under_five_percent()
    {
        var samples = new Dictionary<string, Sample>
        {
            [".pak"] = new(4 << 20, (long)((4 << 20) * 0.97)),  // Oodle inside: 3% saved
            [".ucas"] = new(4 << 20, (4 << 20) / 2),             // uncompressed IoStore: the prior was wrong, the sample wins
            [".assets"] = new(4 << 20, (4 << 20) / 3),
            [".bk2"] = new(1024, 1024),                         // too small a sample: the prior (already packed) decides
            [".txt"] = new(1024, 100),                          // too small, prior says it shrinks
        };
        var poor = GameCompression.Poor(samples);
        Assert.Equal(new[] { ".bk2", ".pak" }, poor.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_plan_counts_what_is_done_so_a_stopped_job_goes_on()
    {
        var files = new[]
        {
            F("a.dat", 10 << 20, Algorithm.Xpress8K),
            F("b.dat", 20 << 20),
            F("tiny.ini", 100),
            F("intro.bk2", 30 << 20),
        };
        var poor = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bk2" };
        var plan = MakePlan(files, Algorithm.Xpress8K, 4096, Guard.All, false, poor);
        Assert.Equal(["b.dat"], plan.Files.Select(f => f.Relative));
        Assert.Equal(10 << 20, plan.DoneBytes);
        Assert.Equal((10 << 20) + (20 << 20) + 100 + (30 << 20), plan.Total);
        Assert.Equal(30 << 20, plan.Left[Skip.Poor]);
        Assert.Equal(100, plan.Left[Skip.Small]);
    }

    [Fact]
    public void An_update_is_the_files_it_wrote_back_and_only_those()
    {
        var compressedAt = Now.AddDays(-10);
        var files = new[]
        {
            F("old.dat", 50 << 20, Algorithm.Xpress8K, written: Now.AddDays(-20)),
            F("patched.dat", 300 << 20, written: Now.AddDays(-1)),
            F("patched-but-compressed.dat", 80 << 20, Algorithm.Xpress8K, written: Now.AddDays(-1)),
            F("new-movie.bk2", 900 << 20, written: Now.AddDays(-1)),
            F("never-worth-it.dat", 40 << 20, written: Now.AddDays(-20)),
        };
        var poor = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bk2" };
        var (count, bytes) = Changed(files, compressedAt, Algorithm.Xpress8K, 4096, Guard.All, false, poor);
        Assert.Equal(1, count);
        Assert.Equal(300 << 20, bytes);
        Assert.True(WorthAgain(bytes, 50 * GB));
        Assert.False(WorthAgain(20 << 20, 50 * GB));
        Assert.True(WorthAgain(20 << 20, 200 << 20));
    }

    // ------------------------------------------------------------------ what it saves

    [Fact]
    public void Estimates_never_exceed_the_file_and_round_to_clusters()
    {
        Assert.Equal(0, Estimate(0, 0.5, Algorithm.Xpress8K, 4096));
        // 1 MB at half: 512 KB + 128 chunks * 8 bytes, rounded up to 4 KB.
        Assert.Equal(RoundUp((1 << 19) + 128 * 8, 4096), Estimate(1 << 20, 0.5, Algorithm.Xpress8K, 4096));
        Assert.Equal(1 << 20, Estimate(1 << 20, 1.0, Algorithm.Xpress8K, 4096));
        Assert.Equal(1 << 20, Estimate(1 << 20, 1.5, Algorithm.Xpress8K, 4096));
        Assert.Equal(8192, ChunkCost(8192, 9000));
        Assert.Equal(8192, ChunkCost(8192, null));
        Assert.Equal(1200, ChunkCost(8192, 1200));
    }

    [Fact]
    public void Saving_uses_the_sample_then_the_prior_and_counts_what_stays()
    {
        var files = new[] { F("data.assets", 100 << 20), F("intro.bk2", 50 << 20), F("Game.exe", 10 << 20) };
        var poor = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".bk2" };
        var plan = MakePlan(files, Algorithm.Xpress8K, 4096, Guard.All, false, poor);
        var samples = new Dictionary<string, Sample> { [".assets"] = new(4 << 20, 1 << 20) };
        var (before, after) = Saving(files, plan, samples, Engine.Unity, 4096);
        Assert.Equal(160 << 20, before);
        var expected = Estimate(100 << 20, 0.25, Algorithm.Xpress8K, 4096) + (50 << 20) + Estimate(10 << 20, PriorRatio(".exe", Engine.Unity), Algorithm.Xpress8K, 4096);
        Assert.Equal(expected, after);
    }

    [Fact]
    public void Engines_by_their_files()
    {
        Assert.Equal(Engine.Unreal, EngineOf([@"Game\Content\Paks\pakchunk0-Windows.pak"]));
        Assert.Equal(Engine.Unity, EngineOf(["UnityPlayer.dll", @"Game_Data\sharedassets0.assets"]));
        Assert.Equal(Engine.Source, EngineOf([@"hl2\hl2_misc_dir.vpk"]));
        Assert.Equal(Engine.Source2, EngineOf([@"game\dota\pak01_dir.vpk"]));
        Assert.Equal(Engine.Red, EngineOf([@"archive\pc\content\basegame_1_engine.archive"]));
        Assert.Equal(Engine.Unknown, EngineOf(["readme.txt"]));
        Assert.True(PriorRatio(".bk2", Engine.Unknown) > 1 - PoorGain);
        Assert.True(PriorRatio(".dll", Engine.Unknown) < 0.6);
        Assert.True(PriorRatio(".pak", Engine.Unreal) > PriorRatio(".pak", Engine.Unknown));
    }

    // ------------------------------------------------------------------ anti-cheat

    private static GameEntry Game(StoreKind store = StoreKind.Steam) => new() { Id = "steam:1", Title = "A game", Store = store, Installed = true, Path = @"D:\Games\A" };

    [Fact]
    public void Anti_cheat_needs_the_players_yes_for_that_exact_set()
    {
        var clean = AntiCheatCheck.Answer.Clean;
        var eac = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, "Easy Anti-Cheat", "files");
        var two = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, ["BattlEye", "Easy Anti-Cheat"], "files");
        var vac = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.PossiblyVac, AntiCheatCheck.PossiblyVacName, "steam");
        var unknown = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Unknown, (string?)null, AntiCheatCheck.Unreadable);
        var vanguard = new AntiCheatCheck.Answer(AntiCheatCheck.Kind.Found, DlssSwapService.RiotVanguard, "store");
        Assert.Equal(Guard.All, GuardFor(Game(), clean, null));
        Assert.Equal(Guard.Nothing, GuardFor(Game(), eac, null));
        Assert.Equal(Guard.DataOnly, GuardFor(Game(), eac, eac.Label));
        Assert.Equal(Guard.Nothing, GuardFor(Game(), two, eac.Label));
        Assert.Equal(Guard.DataOnly, GuardFor(Game(), two, two.Label));
        Assert.Equal(Guard.Nothing, GuardFor(Game(), vac, null));
        Assert.Equal(Guard.DataOnly, GuardFor(Game(), vac, vac.Label));
        Assert.Equal(Guard.Nothing, GuardFor(Game(), unknown, unknown.Label));
        Assert.Equal(Guard.Nothing, GuardFor(Game(StoreKind.Riot), vanguard, vanguard.Label));
        Assert.Equal(Guard.Nothing, GuardFor(Game(StoreKind.Riot), clean, null));
        Assert.Equal("This game uses Easy Anti-Cheat, so Exo compresses it only if you allow it for this game.",
            AntiCheatCheck.Sentence(Game(), eac, AntiCheatCheck.Change.Compress));
    }

    // ------------------------------------------------------------------ resume state

    [Fact]
    public void A_job_left_running_reads_back_paused_and_the_yes_stays()
    {
        var saved = new GameCompressionStore.Saved
        {
            Games =
            {
                ["steam:1"] = new() { Status = GameCompressionStore.Status.Compressing, Algorithm = "LZX", Poor = [".bk2"], Consent = "BattlEye" },
                ["steam:2"] = new() { Status = GameCompressionStore.Status.Uncompressing },
                ["steam:3"] = new() { Status = GameCompressionStore.Status.Compressed, Before = 100, After = 60, CompressedUtc = Now },
            },
        };
        var back = GameCompressionStore.Parse(GameCompressionStore.Serialize(saved));
        Assert.Equal(GameCompressionStore.Status.Paused, back.Games["steam:1"].Status);
        Assert.Equal("LZX", back.Games["steam:1"].Algorithm);
        Assert.Equal([".bk2"], back.Games["steam:1"].Poor);
        Assert.Equal("BattlEye", back.Games["steam:1"].Consent);
        Assert.Null(back.Games["steam:2"].Status);
        Assert.Equal(40, back.Games["steam:3"].Before - back.Games["steam:3"].After);
        Assert.Equal(Now, back.Games["steam:3"].CompressedUtc);
        Assert.True(back.Games.ContainsKey("STEAM:3"));
        Assert.Empty(GameCompressionStore.Parse("not json").Games);
        Assert.Empty(GameCompressionStore.Parse(null).Games);
    }

    [Fact]
    public void Worth_compressing_needs_a_gigabyte_or_a_tenth()
    {
        GameCompressor.View V(string state, long before, long after) => new(state, null, 0, 0, 0, before, after, true, null, 0, null, null, null);
        Assert.True(GameCompressor.Eligible(V("none", 50 * GB, 48 * GB)));
        Assert.False(GameCompressor.Eligible(V("none", 50 * GB, 49 * GB + GB / 2)));
        Assert.True(GameCompressor.Eligible(V("none", 2 * GB, GB + GB / 2)));
        Assert.True(GameCompressor.Eligible(V("paused", 50 * GB, 30 * GB)));
        Assert.False(GameCompressor.Eligible(V("compressed", 50 * GB, 30 * GB)));
        Assert.False(GameCompressor.Eligible(V("needsConsent", 50 * GB, 30 * GB)));
        Assert.False(GameCompressor.Eligible(V("kept", 50 * GB, 30 * GB)));
    }

    // ------------------------------------------------------------------ Windows, on a temp folder

    [Fact]
    public void Wof_compresses_reads_back_the_same_and_puts_it_back()
    {
        var folder = Path.Combine(Path.GetTempPath(), "exo-wof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            if (!string.Equals(new DriveInfo(Path.GetPathRoot(folder)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) Assert.Skip("The temp folder isn't on NTFS.");
            var path = Path.Combine(folder, "data.txt");
            var bytes = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 40_000).Select(i => $"line {i % 97} of the same words\n")));
            File.WriteAllBytes(path, bytes);
            var written = File.GetLastWriteTimeUtc(path);
            var outcome = GameCompressionNative.Compress(path, Algorithm.Xpress8K);
            if (outcome == GameCompressionNative.Outcome.Unsupported) Assert.Skip("This volume has no WOF.");
            Assert.Equal(GameCompressionNative.Outcome.Done, outcome);
            Assert.Equal(Algorithm.Xpress8K, GameCompressionNative.Current(path));
            Assert.True(GameCompressionNative.OnDisk(path) < bytes.Length / 4);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
            // The walk sees it (WOF hides its reparse point, so each file is asked).
            var walked = GameCompressor.Walk(folder, deep: true, CancellationToken.None).Single();
            Assert.Equal(Algorithm.Xpress8K, walked.Current);
            Assert.True(walked.OnDisk < walked.Length);
            Assert.Equal(GameCompressionNative.Outcome.Done, GameCompressionNative.Uncompress(path));
            Assert.Null(GameCompressionNative.Current(path));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(GameCompressionNative.Outcome.Done, GameCompressionNative.Uncompress(path));
        }
        finally { try { DeletionGate.DeleteDirectory(folder, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void A_file_held_for_writing_is_in_use()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-wof-" + Guid.NewGuid().ToString("N") + ".dat");
        File.WriteAllBytes(path, new byte[64 * 1024]);
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                Assert.Equal(GameCompressionNative.Outcome.InUse, GameCompressionNative.Compress(path, Algorithm.Xpress8K));
            Assert.Null(GameCompressionNative.Current(path));
        }
        finally { DeletionGate.DeleteFile(path); }
    }

    [Fact]
    public void The_sampler_tells_text_from_noise()
    {
        using var sampler = GameCompressionNative.Sampler.Open();
        if (sampler is null) Assert.Skip("No Compression API on this Windows.");
        var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog ", 200)))[..8192];
        var noise = new byte[8192];
        new Random(7).NextBytes(noise);
        Assert.True(sampler.Cost(text) < 8192 / 4);
        Assert.Equal(8192, sampler.Cost(noise));
        var path = Path.Combine(Path.GetTempPath(), "exo-sample-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, [.. Enumerable.Repeat(text, 64).SelectMany(b => b), .. noise]);
            var sample = sampler.Measure(path, new FileInfo(path).Length, 8192, 16);
            Assert.True(sample.Raw >= 8192 * 10);
            Assert.True(sample.Gain > 0.5);
            Assert.Equal(default, sampler.Measure(path + ".missing", 1 << 20, 8192, 4));
        }
        finally { DeletionGate.DeleteFile(path); }
    }
}

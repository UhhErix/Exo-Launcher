using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The first-run restore offer and the torn-line rules of LauncherBackup, in temp folders only. Ways it could fail: this
/// install's own first backup is offered back to it (a fresh install would restore an empty copy of itself); a backup from
/// another PC is offered after setup is done; the restore takes a different file than the one shown; one torn Riot ledger line
/// makes every later backup unrestorable; a snapshot that does not read back replaces the last good backup of the day.
/// </summary>
public sealed class LauncherBackupOfferTests : IDisposable
{
    private const string Own = "bbbbbbbb";
    private const string Other = "aaaaaaaa";
    private const string Version = "3.25.0";
    private const string SettingsJson = """{"onboardingComplete":true,"textSize":"large"}""";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-backup-offer-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Folder => Path.Combine(_root, "Documents", "Backups");

    public LauncherBackupOfferTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Data, "backup-device.txt"), Own);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) DeletionGate.DeleteDirectory(_root, recursive: true);
    }

    private void WriteDaily(DateTimeOffset day, string device, params LauncherBackup.Entry[] entries)
    {
        var files = entries.Length == 0 ? new[] { new LauncherBackup.Entry("settings.json", Encoding.UTF8.GetBytes(SettingsJson)) } : entries;
        File.WriteAllText(Path.Combine(Folder, LauncherBackup.DailyFileName(day, device)), LauncherBackup.Serialize(files, day, Version));
    }

    [Fact]
    public void A_fresh_install_is_never_offered_its_own_backup()
    {
        WriteDaily(Now, Own);
        WriteDaily(Now.AddDays(-1), Other);

        var offer = LauncherBackup.OfferFor(Data, Folder);

        Assert.NotNull(offer);
        Assert.Equal(Other, offer.Device);
        Assert.Equal("2026-11-08", offer.Day);
    }

    [Fact]
    public void Only_this_installs_backups_means_no_offer()
    {
        WriteDaily(Now, Own);

        Assert.Null(LauncherBackup.OfferFor(Data, Folder));
    }

    private static readonly LauncherBackup.Here Pc = new(@"C:\Users\Player", @"PC\Player", "machine");

    [Fact]
    public void A_fresh_install_never_overwrites_a_backup_file_it_did_not_write_while_the_offer_is_open()
    {
        // A fixture that forces the same name: a file already there for today and this install's own id, with the real data in it.
        var real = LauncherBackup.Serialize([new LauncherBackup.Entry("settings.json", Encoding.UTF8.GetBytes(SettingsJson))], Now, Version);
        var path = Path.Combine(Folder, LauncherBackup.DailyFileName(Now, Own));
        File.WriteAllText(path, real);
        File.WriteAllText(Path.Combine(Data, "settings.json"), "{}");

        var outcome = LauncherBackup.BackUpDaily(Data, Folder, Now, Version, replace: true, Pc);

        Assert.Equal(LauncherBackup.Result.Recent, outcome.Kind);
        Assert.Equal(real, File.ReadAllText(path));
        Assert.Equal("2026-11-09", LauncherBackup.Latest(Folder)!.Day);

        // Once the offer is answered the install's own daily backup may be replaced again.
        LauncherBackup.CloseOffer(Data);
        Assert.Equal(LauncherBackup.Result.Written, LauncherBackup.BackUpDaily(Data, Folder, Now, Version, replace: true, Pc).Kind);
        Assert.NotEqual(real, File.ReadAllText(path));
    }

    [Fact]
    public void The_restore_takes_exactly_the_day_and_pc_that_was_shown()
    {
        WriteDaily(Now.AddDays(-1), Other);
        WriteDaily(Now, Other);

        var path = LauncherBackup.DailyPath(Folder, "2026-11-08", Other);

        Assert.NotNull(path);
        Assert.Equal(Path.Combine(Folder, LauncherBackup.DailyFileName(Now.AddDays(-1), Other)), path);
        Assert.Null(LauncherBackup.DailyPath(Folder, "2026-11-08", Own));
        Assert.Null(LauncherBackup.DailyPath(Folder, "../2026-11-08", Other));
        Assert.Null(LauncherBackup.DailyPath(Folder, "2026-11-08", "..\\x"));
    }

    [Fact]
    public void Once_setup_is_done_the_offer_is_closed_and_no_other_pcs_backup_is_offered_later()
    {
        WriteDaily(Now.AddDays(-1), Other);
        Assert.NotNull(LauncherBackup.OfferFor(Data, Folder));

        LauncherBackup.CloseOffer(Data);

        Assert.Null(LauncherBackup.OfferFor(Data, Folder));
        Assert.NotNull(LauncherBackup.Latest(Folder));
    }

    [Fact]
    public void A_torn_riot_ledger_line_is_dropped_and_the_backup_still_restores()
    {
        Directory.CreateDirectory(Path.Combine(Data, "riot-ledger"));
        File.WriteAllText(Path.Combine(Data, "riot-ledger", "0123456789abcdef0123.jsonl"), "{\"schema\":1,\"kind\":\"play\",\"key\":\"k1\",\"game\":\"riot:valorant\",\"minutes\":5,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n{\"key\":\"k2\",\"minu\n{\"schema\":1,\"kind\":\"play\",\"key\":\"k3\",\"game\":\"riot:valorant\",\"minutes\":1,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n");

        var entries = LauncherBackup.Gather(Data);
        var ledger = Encoding.UTF8.GetString(entries.Single(entry => entry.Name.StartsWith("riot-ledger/", StringComparison.Ordinal)).Bytes);
        Assert.Equal("{\"schema\":1,\"kind\":\"play\",\"key\":\"k1\",\"game\":\"riot:valorant\",\"minutes\":5,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n{\"schema\":1,\"kind\":\"play\",\"key\":\"k3\",\"game\":\"riot:valorant\",\"minutes\":1,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n", ledger);

        // An old backup written with the torn line still reads back whole, with the torn line gone.
        var torn = """{"kind":"exo-launcher-backup","schema":1,"files":{"riot-ledger/0123456789abcdef0123.jsonl":{"text":"{\"schema\":1,\"kind\":\"play\",\"key\":\"k1\",\"game\":\"riot:valorant\",\"minutes\":1,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n{\"key\":\"k2\",\"minu\n"}}}""";
        var read = LauncherBackup.Validate(torn);
        Assert.True(read.Ok, read.Message);
        Assert.Equal("{\"schema\":1,\"kind\":\"play\",\"key\":\"k1\",\"game\":\"riot:valorant\",\"minutes\":1,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n", Encoding.UTF8.GetString(read.Files.Single().Bytes));
    }

    [Fact]
    public void A_merge_of_ledgers_skips_a_torn_line_instead_of_throwing()
    {
        Directory.CreateDirectory(Path.Combine(Data, "riot-ledger"));
        File.WriteAllText(Path.Combine(Data, "riot-ledger", "0123456789abcdef0123.jsonl"), "{\"schema\":1,\"kind\":\"play\",\"key\":\"k1\",\"game\":\"riot:valorant\",\"minutes\":1,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n{\"torn\n");
        var files = LauncherBackup.Validate(LauncherBackup.Serialize(
            [new LauncherBackup.Entry("riot-ledger/0123456789abcdef0123.jsonl", Encoding.UTF8.GetBytes("{\"schema\":1,\"kind\":\"play\",\"key\":\"k2\",\"game\":\"riot:valorant\",\"minutes\":1,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n{\"tor\n"))], Now, Version));
        Assert.True(files.Ok, files.Message);

        var outcome = LauncherBackup.Restore(Data, files.Files, Path.Combine(_root, "before"), Now, Version);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal("{\"schema\":1,\"kind\":\"play\",\"key\":\"k1\",\"game\":\"riot:valorant\",\"minutes\":1,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n{\"schema\":1,\"kind\":\"play\",\"key\":\"k2\",\"game\":\"riot:valorant\",\"minutes\":1,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n", File.ReadAllText(Path.Combine(Data, "riot-ledger", "0123456789abcdef0123.jsonl")));
    }
}

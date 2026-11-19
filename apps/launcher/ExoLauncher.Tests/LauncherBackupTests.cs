using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Exo Launcher's backups (LauncherBackup), in temp folders only: the data folder, the backups folder and the before-restore
/// copies all sit under a temp root, never the real %LOCALAPPDATA%\ExoLauncher or Documents. Ways it could fail: a token or
/// sign-in file lands in a backup; a restore brings back bytes other than the ones saved; a file that fails its shape check,
/// a name outside the list, or a path that climbs out of the data folder is restored; a restore that fails part way leaves
/// some files replaced; the daily backup is written twice in a day, never pruned, or prunes another PC's files; a restore
/// that cannot keep a copy of the data it replaces goes ahead anyway.
/// </summary>
public sealed class LauncherBackupTests : IDisposable
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Version = "3.25.0";
    private const string SettingsJson = """
        {"defaultInstallRoot":"D:\\Games","libraryFolders":[{"id":"f1","name":"Favourites","games":["steam:1"]}],
         "customCoverImages":{"steam:1":"custom-cover-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png"},
         "onboardingComplete":true,"textSize":"large","discordGamesShared":["a"]}
        """;

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Regex DeviceShape = new("^[0-9a-f]{8}$", RegexOptions.CultureInvariant);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-backup-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Folder => Path.Combine(_root, "Documents", "Exo", "Backups", "Launcher");
    private string Before => Path.Combine(_root, "before");

    public LauncherBackupTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(Before);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) DeletionGate.DeleteDirectory(_root, recursive: true);
    }

    private static byte[] Png()
    {
        var bytes = new byte[256];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static LauncherBackup.Entry Text(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    private static string Doc(params LauncherBackup.Entry[] entries) => LauncherBackup.Serialize(entries, Now, Version);

    private void Put(string relative, string text)
    {
        var path = Path.Combine(Data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private void Populate()
    {
        Put("settings.json", SettingsJson);
        Put("playtime.json", """{"exoMinutes":{"steam:1":120}}""");
        Put("reported-hours.json", """{"xboxAt":null,"xbox":[]}""");
        Put("window-placement.json", """{"x":10,"y":20}""");
        Put("notes/notes.json", """{"notes":[]}""");
        Put("riot-ledger/0123456789abcdef0123.jsonl", """{"at":"2026-11-01T00:00:00Z","minutes":5}""" + "\n");
        Directory.CreateDirectory(Path.Combine(Data, "covers"));
        File.WriteAllBytes(Path.Combine(Data, "covers", $"custom-cover-{Hash}.png"), Png());
        // Sign-in, token and credential files: none of them is in a backup.
        Put("discord-token.dat", "secret-discord");
        Put("identity.json", """{"secret":"id"}""");
        Put("riot-accounts/erix.json", """{"secret":"riot"}""");
        Put("epic-auth.json", """{"secret":"epic"}""");
        Put("gogdl/token.json", """{"secret":"gog"}""");
        Put("Credentials/steam.bin", "secret-steam");
    }

    [Fact]
    public void A_snapshot_holds_the_device_only_data_and_never_a_token()
    {
        Populate();

        var entries = LauncherBackup.Gather(Data);

        Assert.Equal(
            new[]
            {
                $"covers/custom-cover-{Hash}.png", "notes/notes.json", "playtime.json", "reported-hours.json",
                "riot-ledger/0123456789abcdef0123.jsonl", "settings.json", "window-placement.json",
            },
            entries.Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("secret", LauncherBackup.Serialize(entries, Now, Version));
    }

    [Fact]
    public void A_reinstall_restores_every_file_byte_for_byte_and_no_token()
    {
        Populate();
        var file = Path.Combine(_root, "exo-launcher-export.json");
        File.WriteAllText(file, Doc(LauncherBackup.Gather(Data).ToArray()));
        var fresh = Path.Combine(_root, "reinstalled");
        Directory.CreateDirectory(fresh);

        var read = LauncherBackup.Validate(File.ReadAllText(file));
        Assert.True(read.Ok, read.Message);
        var outcome = LauncherBackup.Restore(fresh, read.Files, Before, Now, Version);

        Assert.True(outcome.Ok, outcome.Message);
        foreach (var entry in LauncherBackup.Gather(Data))
        {
            var restored = File.ReadAllBytes(Path.Combine(fresh, entry.Name));
            if (entry.Name != "settings.json") { Assert.Equal(entry.Bytes, restored); continue; }
            // Settings come back as saved, except that a restore never carries "onboarding complete".
            var expected = JsonNode.Parse(entry.Bytes)!.AsObject();
            expected["onboardingComplete"] = false;
            // A restore drops an install folder that is not on this PC.
            if (!Directory.Exists(@"D:\Games")) expected.Remove("defaultInstallRoot");
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(restored)));
        }
        Assert.False(File.Exists(Path.Combine(fresh, "discord-token.dat")));
        Assert.False(File.Exists(Path.Combine(fresh, "identity.json")));
        Assert.Empty(Directory.GetFiles(fresh, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_restore_merges_playtime_and_the_riot_ledger_with_what_is_here()
    {
        // Played after the backup: the live file has minutes and a ledger line the backup does not.
        Put("playtime.json", """{"exoMinutes":{"a":100,"b":5}}""");
        Put("riot-ledger/0123456789abcdef0123.jsonl", "{\"schema\":1,\"kind\":\"play\",\"key\":\"k1\",\"game\":\"riot:valorant\",\"minutes\":5,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n{\"schema\":1,\"kind\":\"play\",\"key\":\"k2\",\"game\":\"riot:valorant\",\"minutes\":3,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n");
        var files = LauncherBackup.Validate(Doc(
            Text("playtime.json", """{"exoMinutes":{"a":60,"c":7}}"""),
            Text("riot-ledger/0123456789abcdef0123.jsonl", "{\"schema\":1,\"kind\":\"play\",\"key\":\"k2\",\"game\":\"riot:valorant\",\"minutes\":3,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n{\"schema\":1,\"kind\":\"play\",\"key\":\"k3\",\"game\":\"riot:valorant\",\"minutes\":9,\"startUtc\":\"2026-11-08T10:00:00Z\",\"mode\":\"unknown\"}\n")));
        Assert.True(files.Ok, files.Message);

        var outcome = LauncherBackup.Restore(Data, files.Files, Before, Now, Version);

        Assert.True(outcome.Ok, outcome.Message);
        var minutes = JsonNode.Parse(File.ReadAllText(Path.Combine(Data, "playtime.json")))!["exoMinutes"]!.AsObject();
        Assert.Equal(100, minutes["a"]!.GetValue<int>());
        Assert.Equal(5, minutes["b"]!.GetValue<int>());
        Assert.Equal(7, minutes["c"]!.GetValue<int>());
        var keys = File.ReadAllLines(Path.Combine(Data, "riot-ledger", "0123456789abcdef0123.jsonl"))
            .Where(line => line.Length > 0)
            .Select(line => JsonNode.Parse(line)!["key"]!.GetValue<string>());
        Assert.Equal(new[] { "k1", "k2", "k3" }, keys);
    }

    [Fact]
    public void A_restore_never_carries_onboarding_complete_so_the_steps_still_run()
    {
        Put("settings.json", SettingsJson.Replace("\"onboardingComplete\":true", "\"onboardingComplete\":false"));
        var files = LauncherBackup.Validate(Doc(Text("settings.json", SettingsJson)));
        Assert.True(files.Ok, files.Message);

        var fresh = Path.Combine(_root, "reinstalled");
        Directory.CreateDirectory(fresh);
        Assert.True(LauncherBackup.Restore(fresh, files.Files, Before, Now, Version).Ok);

        var restored = JsonNode.Parse(File.ReadAllText(Path.Combine(fresh, "settings.json")))!.AsObject();
        Assert.False(restored["onboardingComplete"]!.GetValue<bool>());
        Assert.Equal("large", restored["textSize"]!.GetValue<string>());
        // The install folder is not on this PC, so the restore drops it.
        Assert.False(restored.ContainsKey("defaultInstallRoot"));
    }

    [Fact]
    public void Once_a_restore_or_a_decline_is_answered_the_offer_is_gone_and_a_restore_does_not_undo_it()
    {
        Populate();
        File.WriteAllText(Path.Combine(Folder, LauncherBackup.DailyFileName(Now, "aaaaaaaa")), Doc(Text("settings.json", SettingsJson)));
        Assert.False(LauncherBackup.Answered(Data));
        Assert.NotNull(LauncherBackup.OfferFor(Data, Folder));

        LauncherBackup.MarkAnswered(Data);

        Assert.True(LauncherBackup.Answered(Data));
        Assert.Null(LauncherBackup.OfferFor(Data, Folder));
        // A restore brings back the backup's files, never the answer: the marker is not one of them, and it survives the restore.
        var files = LauncherBackup.Validate(Doc(Text("settings.json", SettingsJson)));
        Assert.True(LauncherBackup.Restore(Data, files.Files, Before, Now, Version).Ok);
        Assert.True(LauncherBackup.Answered(Data));
        Assert.DoesNotContain(LauncherBackup.Gather(Data).Select(entry => entry.Name), name => name.Contains("answered", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_that_fails_its_shape_check_refuses_the_whole_backup()
    {
        var cases = new Dictionary<string, string>
        {
            ["settings is not an object"] = Doc(Text("settings.json", "[]")),
            ["playtime is not an object"] = Doc(Text("settings.json", SettingsJson), Text("playtime.json", "[1,2]")),
            ["a token file name"] = Doc(Text("discord-token.dat", "x")),
            ["a path that climbs out"] = Doc(Text("../escape.json", "{}")),
            ["a cover that is not an image"] = Doc(new LauncherBackup.Entry($"covers/custom-cover-{Hash}.png", new byte[64])),
            ["the wrong kind"] = """{"kind":"other","schema":1,"files":{"settings.json":{"text":"{}"}}}""",
            ["a newer schema"] = """{"kind":"exo-launcher-backup","schema":9,"files":{"settings.json":{"text":"{}"}}}""",
            ["no files"] = Doc(),
            ["not a backup at all"] = "hello",
        };
        foreach (var (why, text) in cases)
            Assert.False(LauncherBackup.Validate(text).Ok, why);

        Assert.True(LauncherBackup.Validate(Doc(Text("settings.json", SettingsJson),
            new LauncherBackup.Entry($"covers/custom-cover-{Hash}.png", Png()))).Ok);
    }

    [Fact]
    public void A_restore_that_fails_part_way_puts_back_what_it_replaced()
    {
        Put("settings.json", SettingsJson);
        var original = File.ReadAllBytes(Path.Combine(Data, "settings.json"));
        // playtime.json is a folder here: its file cannot be put in place after settings.json has been replaced.
        Directory.CreateDirectory(Path.Combine(Data, "playtime.json"));
        var files = LauncherBackup.Validate(Doc(
            Text("settings.json", """{"textSize":"small"}"""),
            Text("playtime.json", """{"exoMinutes":{}}""")));
        Assert.True(files.Ok);

        var outcome = LauncherBackup.Restore(Data, files.Files, Before, Now, Version);

        Assert.False(outcome.Ok);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Data, "settings.json")));
        Assert.Empty(Directory.GetFiles(Data, "*.restore-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_restore_keeps_a_copy_of_the_data_it_replaces_and_only_the_newest_three()
    {
        Populate();
        var files = LauncherBackup.Validate(Doc(Text("settings.json", SettingsJson)));
        Assert.True(files.Ok);
        for (var i = 0; i < 5; i++)
            Assert.True(LauncherBackup.Restore(Data, files.Files, Before, Now.AddMinutes(i), Version).Ok);

        var copies = Directory.GetFiles(Before, "exo-launcher-before-restore-*.json").Order().ToArray();
        Assert.Equal(3, copies.Length);
        Assert.All(copies, copy => Assert.EndsWith("-" + LauncherBackup.DeviceId(Data) + ".json", copy, StringComparison.Ordinal));
    }

    [Fact]
    public void The_daily_backup_is_written_once_a_day_and_keeps_thirty_of_this_pc()
    {
        Populate();
        var device = LauncherBackup.DeviceId(Data);
        var today = Path.Combine(Folder, LauncherBackup.DailyFileName(Now, device));

        Assert.True(LauncherBackup.BackUpDaily(Data, Folder, Now, Version, replace: false).Ok);
        Assert.True(File.Exists(today));
        var first = File.ReadAllText(today);
        Assert.True(LauncherBackup.BackUpDaily(Data, Folder, Now.AddHours(3), Version, replace: false).Ok);
        Assert.Equal(first, File.ReadAllText(today));

        for (var i = 1; i <= 35; i++)
            File.WriteAllText(Path.Combine(Folder, LauncherBackup.DailyFileName(Now.AddDays(-i), device)), first);
        File.WriteAllText(Path.Combine(Folder, "exo-launcher-backup-20260101-ffffffff.json"), first);

        Assert.True(LauncherBackup.BackUpDaily(Data, Folder, Now.AddDays(1), Version, replace: false).Ok);

        Assert.Equal(30, Directory.GetFiles(Folder, $"exo-launcher-backup-*-{device}.json").Length);
        Assert.False(File.Exists(Path.Combine(Folder, LauncherBackup.DailyFileName(Now.AddDays(-35), device))));
        Assert.True(File.Exists(Path.Combine(Folder, "exo-launcher-backup-20260101-ffffffff.json")));
    }

    [Fact]
    public void A_daily_backup_is_refused_when_a_file_cannot_be_read_and_nothing_is_written()
    {
        Populate();
        // An exclusive hold on settings.json: the snapshot cannot read it, so no backup of the day is written over the good one.
        using var hold = new FileStream(Path.Combine(Data, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = LauncherBackup.BackUpDaily(Data, Folder, Now, Version, replace: true);

        Assert.False(result.Ok);
        Assert.Empty(Directory.GetFiles(Folder, "*.json"));
    }

    [Fact]
    public void The_offer_is_the_newest_daily_backup_that_reads_back_whole()
    {
        const string device = "0badf00d";
        File.WriteAllText(Path.Combine(Folder, LauncherBackup.DailyFileName(Now.AddDays(-1), device)), Doc(Text("settings.json", SettingsJson)));
        File.WriteAllText(Path.Combine(Folder, LauncherBackup.DailyFileName(Now, device)), "half a file {");
        File.WriteAllText(Path.Combine(Before, $"exo-launcher-before-restore-1-{device}.json"), Doc(Text("settings.json", SettingsJson)));

        var offer = LauncherBackup.Latest(Folder);

        Assert.NotNull(offer);
        Assert.Equal("2026-11-08", offer.Day);
        Assert.Equal(1, offer.Files);
    }

    [Fact]
    public void A_device_id_is_eight_hex_and_stays_the_same()
    {
        var first = LauncherBackup.DeviceId(Data);
        Assert.Matches(DeviceShape, first);
        Assert.Equal(first, LauncherBackup.DeviceId(Data));
    }

    [Fact]
    public void An_export_is_the_file_a_restore_reads()
    {
        Populate();
        var path = Path.Combine(_root, "exo-launcher-export.json");

        Assert.True(LauncherBackup.ExportTo(Data, path, Now, Version).Ok);

        var read = LauncherBackup.Validate(File.ReadAllText(path));
        Assert.True(read.Ok, read.Message);
        Assert.Equal(LauncherBackup.Gather(Data).Count, read.Files.Count);
    }

    [Fact]
    public void Only_backup_named_files_in_the_backups_folder_can_be_pruned()
    {
        var daily = Path.Combine(Folder, "exo-launcher-backup-20261101-abcdef12.json");
        File.WriteAllText(daily, "{}");
        var stranger = Path.Combine(Folder, "keep-me.txt");
        File.WriteAllText(stranger, "mine");
        Put("settings.json", SettingsJson);

        DeletionGate.DeleteBackup(daily, Folder);
        Assert.False(File.Exists(daily));
        Assert.Throws<DeletionRefusedException>(() => DeletionGate.DeleteBackup(stranger, Folder));
        Assert.Throws<DeletionRefusedException>(() => DeletionGate.DeleteBackup(Path.Combine(Data, "settings.json"), Folder));
        Assert.True(File.Exists(stranger));
    }
}

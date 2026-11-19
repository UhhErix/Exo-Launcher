using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The registry of the player's files (PlayerData) and the backup that reads it. Ways it could fail: a new settings file is
/// added to Exo and nobody puts it in the registry, so a reinstall loses it; the registry names a file Exo no longer has;
/// a file the registry names is not in the backup or does not come back byte for byte; a token lands in a backup; a name
/// that climbs out of its folder, or a file half written, is taken for the player's.
/// </summary>
public sealed class PlayerDataTests : IDisposable
{
    /// <summary>A file name Exo's code uses: one file part with an extension Exo writes or reads.</summary>
    private static readonly Regex FileShaped = new(@"^[A-Za-z0-9_.\-]+\.(json|jsonl|txt|bin|dat|db|ini|cfg|xml|lnk|png|jpg)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A folder name, as Path.Combine takes it just before a file name.</summary>
    private static readonly Regex FolderShaped = new(@"^[A-Za-z0-9_\-]{1,40}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Every file path Exo's compiled code names, with the type that names it (its outermost type), outside the registry itself
    /// (whose own list would otherwise always find itself). A path is the file name with the one or two folder names loaded
    /// right before it (Path.Combine(dir, "frame-gen", "games.json") is frame-gen/games.json); a constant is its bare name.
    /// </summary>
    internal static HashSet<(string Path, string Place)> FilesInExo()
    {
        using var stream = File.OpenRead(typeof(PlayerData).Assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var found = new HashSet<(string, string)>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var place = Outermost(reader, typeHandle);
            if (place == nameof(PlayerData)) continue;
            var type = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;
                var run = new List<string>();
                foreach (var token in LoadedStrings(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!))
                {
                    // A run of strings loaded one right after another; anything else between two ends the run.
                    if (token is null) { run.Clear(); continue; }
                    var text = reader.GetUserString(MetadataTokens.UserStringHandle(token.Value & 0x00FFFFFF));
                    if (FileShaped.IsMatch(text))
                    {
                        var folders = run.TakeLast(2).Reverse().TakeWhile(name => FolderShaped.IsMatch(name)).Reverse();
                        found.Add((string.Join('/', folders.Append(text)), place));
                    }
                    run.Add(text);
                }
            }
            foreach (var fieldHandle in type.GetFields())
            {
                var constantHandle = reader.GetFieldDefinition(fieldHandle).GetDefaultValue();
                if (constantHandle.IsNil) continue;
                var constant = reader.GetConstant(constantHandle);
                if (constant.TypeCode != ConstantTypeCode.String) continue;
                var blob = reader.GetBlobReader(constant.Value);
                var text = blob.ReadUTF16(blob.Length);
                if (FileShaped.IsMatch(text)) found.Add((text, place));
            }
        }
        return found;
    }

    private static string Outermost(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        while (!type.GetDeclaringType().IsNil) type = reader.GetTypeDefinition(type.GetDeclaringType());
        return reader.GetString(type.Name);
    }

    /// <summary>The operand size of every IL opcode, from the runtime's own opcode list.</summary>
    private static readonly Dictionary<short, System.Reflection.Emit.OpCode> OpCodes = typeof(System.Reflection.Emit.OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(field => (System.Reflection.Emit.OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    /// <summary>The user-string token of each ldstr in a method body, and null for every other instruction.</summary>
    private static IEnumerable<int?> LoadedStrings(byte[] il)
    {
        for (var at = 0; at < il.Length;)
        {
            short value = il[at++];
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il[at++]));
            var code = OpCodes[value];
            yield return code == System.Reflection.Emit.OpCodes.Ldstr ? BitConverter.ToInt32(il, at) : null;
            at += code.OperandType switch
            {
                System.Reflection.Emit.OperandType.InlineNone => 0,
                System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                System.Reflection.Emit.OperandType.InlineVar => 2,
                System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                System.Reflection.Emit.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
                _ => 4,
            };
        }
    }

    [Fact]
    public void The_file_reader_finds_paths_with_their_folders_and_constants()
    {
        var found = FilesInExo();

        Assert.Contains(("frame-gen/games.json", "DlssSwapService"), found);  // two literals in one Path.Combine
        Assert.Contains(("controllers/games.json", "ControllerLab"), found);
        Assert.Contains(("riot-id.txt", "RiotIdStore"), found);               // a constant
        Assert.DoesNotContain(found, pair => pair.Place == nameof(PlayerData)); // the registry's own list is not read
    }

    [Fact]
    public void Every_file_Exo_names_is_claimed_by_exactly_one_entry()
    {
        var wrong = FilesInExo()
            .Select(pair => (pair.Path, pair.Place, Claims: PlayerData.Claims(pair.Path, pair.Place)))
            .Where(pair => pair.Claims != 1)
            .Select(pair => $"{pair.Path} in {pair.Place} ({(pair.Claims == 0 ? "in neither list" : $"claimed {pair.Claims} times")})")
            .Order(StringComparer.Ordinal).ToList();

        Assert.True(wrong.Count == 0,
            "Each goes in PlayerData.Files (the player's choice: backed up, and synced where it can be) or PlayerData.NotPlayerData (with why not), once: "
            + string.Join(", ", wrong));
    }

    [Fact]
    public void The_registry_claims_nothing_Exo_no_longer_names()
    {
        var found = FilesInExo();

        Assert.Empty(PlayerData.AllClaims().Where(claim => !found.Contains(claim)).Select(claim => claim.Path + " in " + claim.Place));
    }

    [Fact]
    public void Every_file_that_is_not_the_players_says_its_own_reason()
    {
        Assert.Empty(PlayerData.NotPlayerData.Where(entry => entry.Reason.Trim().Length < 12).Select(entry => entry.Path));
    }



    [Theory]
    [InlineData("game-settings/kept.json", "game-settings/*.json")]
    [InlineData("game-settings/cs2.original.cfg", "game-settings/*")]
    [InlineData("controllers/games.json", "controllers/games.json")]
    [InlineData("controllers/054c_0ce6.json", "controllers/*.json")]
    [InlineData("startup-removed/task-0f.xml", "startup-removed/*")]
    [InlineData("startup-removed/20261110/Discord.lnk", "startup-removed/*/*")]
    [InlineData("covers/folder-art-f1-0123456789abcdef.jpg", "covers/folder-art-*")]
    public void A_name_belongs_to_the_first_entry_it_fits(string name, string path) =>
        Assert.Equal(path, PlayerData.Of(name)?.Path);

    [Theory]
    [InlineData("game-settings/kept.json.tmp")]
    [InlineData("game-settings/../identity.json")]
    [InlineData("startup-removed/change.reg")]
    [InlineData("startup-removed/20261110/run.exe")]
    [InlineData("covers/folder-art-f1.webp")]
    [InlineData("discord-client.bin")]
    [InlineData("account-session.bin")]
    [InlineData(@"game-settings\kept.json")]
    public void A_name_outside_the_registry_is_refused(string name) => Assert.Null(PlayerData.Of(name));

    // ------------------------------------------------------------------ the round trip

    private const string Version = "3.26.0";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 22, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-playerdata-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Backups => Path.Combine(_root, "Documents", "Exo", "Backups", "Launcher");

    /// <summary>The PC and Windows user a fixture is backed up on, and another PC and user it may be restored on.</summary>
    private const string ProfileA = @"C:\Users\Player One";
    private static readonly LauncherBackup.Here PcA = new(ProfileA, @"PC-A\Player One", "machine-a");
    private static readonly LauncherBackup.Here PcB = new(@"D:\Profiles\Someone Else", @"PC-B\Someone Else", "machine-b");

    public PlayerDataTests() => Directory.CreateDirectory(Data);

    public void Dispose()
    {
        if (Directory.Exists(_root)) DeletionGate.DeleteDirectory(_root, recursive: true);
    }

    private void Put(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private string At(string relative) => Path.Combine(Data, relative.Replace('/', Path.DirectorySeparatorChar));

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Jpeg(byte fill, int size = 300)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.CopyTo(bytes, 0);
        return bytes;
    }

    private static readonly string CoverFile = "custom-cover-" + new string('b', 64) + ".jpg";
    private const string FolderArt = "folder-art-f1-0123456789abcdef.jpg";
    private const string Shortcut = "startup-removed/20261110/Discord.lnk";
    private static readonly ShellLinkFile.Link DiscordLink = new(ProfileA + @"\AppData\Local\Discord\Update.exe", "--processStart Discord.exe", ProfileA + @"\AppData\Local\Discord", "", 0, "Discord");

    /// <summary>A player's profile with one file of every kind the registry names (paths under PC A's user), and the sign-in files a backup must never hold.</summary>
    private static Dictionary<string, byte[]> Profile() => new(StringComparer.Ordinal)
    {
        ["settings.json"] = Utf8($$$$"""{"textSize":"large","customCoverImages":{"steam:1":"{{{{CoverFile}}}}"},"libraryFolders":[{"id":"f1","name":"Co-op","art":"{{{{FolderArt}}}}","games":["steam:1"]}],"uiPrefs":{"discord":{"streamerMode":true}}}"""),
        ["playtime.json"] = Utf8("""{"exoMinutes":{"steam:1":120}}"""),
        ["reported-hours.json"] = Utf8("""{"xbox":[]}"""),
        ["riot-ledger/""" + "012345" + "6789ab" + "cdef01" + "23" + """.jsonl"] = Utf8("""{"schema":1,"kind":"play","game":"riot:valorant","key":"a","minutes":5}""" + "\n"),
        ["riot-id.txt"] = Utf8("Erix#EUW"),
        ["exo-imported-lifetime.json"] = Utf8("""{"accountKey":"""" + "012345" + "6789ab" + "cdef01" + "23" + """","minutes":{"riot:valorant":600}}"""),
        ["replaced.json"] = Utf8("""{"removed":["nvidia-app"]}"""),
        ["window-placement.json"] = Utf8("""{"x":10,"y":20}"""),
        ["notes/notes.json"] = Utf8("""{"notes":[{"id":"n1","text":"boss at 3"}]}"""),
        ["covers/" + CoverFile] = Jpeg(1),
        ["covers/" + FolderArt] = Jpeg(2),
        ["installed-games.json"] = Utf8("""{"games":[{"id":"local:abc","title":"Celeste","path":"C:\\Users\\Player One\\Games\\Celeste\\Celeste.exe"}]}"""),
        ["store/wishlist-v2.json"] = Utf8("""[{"appId":"620","name":"Portal 2","addedAt":"2026-11-01T00:00:00+00:00"}]"""),
        ["web-client-preferences.json"] = Utf8("""{"musicService":"youtube","microphone":"allow"}"""),
        ["mouse/profiles.json"] = Utf8("""{"m1":[{"id":"p1","name":"Desktop","steps":[]}]}"""),
        ["mouse/presets.json"] = Utf8("""{"hits-presets":[{"name":"Fast"}]}"""),
        ["controllers/games.json"] = Utf8("""[{"game":"steam:1","layout":"xbox"}]"""),
        ["controllers/054c_0ce6.json"] = Utf8("""{"original":{"deadzone":4}}"""),
        ["lucid-held.json"] = Utf8("""{"gameMode":true}"""),
        ["frame-gen/games.json"] = Utf8("""{"steam:1":{"on":true,"wrote":["C:\\Users\\Player One\\Games\\x\\dlssg.dll"]}}"""),
        ["keep.json"] = Utf8("""{"version":1,"games":{"cs2":{"entries":{"fps_max":{"target":"fps_max","intended":"0"}}}}}"""),
        ["session-recorder.json"] = Utf8("""{"version":1,"sessions":[{"game":"steam:1","version":"none","seconds":600,"frames":60000,"histogram":[0]}]}"""),
        ["game-settings/kept.json"] = Utf8("""{"cs2":{"file":"C:/Users/Player One/Saved/cs2_video.txt"}}"""),
        ["game-settings/window-modes.json"] = Utf8("""{"steam:730":"borderless"}"""),
        ["game-settings/cs2.on.json"] = Utf8("""{"lines":[]}"""),
        ["game-settings/cs2.nvidia.json"] = Utf8("""{"values":[]}"""),
        ["game-settings/cs2.original.cfg"] = Utf8("fps_max 0\r\n"),
        ["startup-removed.json"] = Utf8("""[{"id":"task:\\Foo","name":"Foo"}]"""),
        ["shader-rebuild.json"] = Utf8("""{"Driver":"32.0.15.7270","State":3,"Queue":["steam:1"],"StalledStarts":0,"ReadyAtStart":0,"Ready":0}"""),
        ["startup-guard.json"] = Utf8("""{"Version":1,"Items":[{"Id":"task:\\Foo","Mechanism":"task","State":0,"Since":"2026-11-01T00:00:00+00:00","Comebacks":[]}]}"""),
        ["startup-removed/task-0f.xml"] = Utf8("<?xml version=\"1.0\"?><Task><Principals><Principal><UserId>PC-A\\Player One</UserId></Principal></Principals><Actions><Exec><Command>C:\\Users\\Player One\\foo.exe</Command></Exec></Actions></Task>"),
    };

    private static readonly string[] Secrets = ["account-session.bin", "discord-client.bin", "discord-accounts.bin", "steam-account.bin", "identity/identity.json", "my-voice.bin"];

    private Dictionary<string, byte[]> PutProfile()
    {
        var profile = Profile();
        foreach (var (name, bytes) in profile) Put(Data, name, bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(At(Shortcut))!);
        ShellLinkFile.Write(At(Shortcut), DiscordLink);
        return profile;
    }

    private LauncherBackup.Read BackUpAndRead(LauncherBackup.Here on)
    {
        var backedUp = LauncherBackup.BackUpDaily(Data, Backups, Now, Version, replace: true, on);
        Assert.True(backedUp.Ok, backedUp.Message);
        var read = LauncherBackup.Validate(File.ReadAllText(LauncherBackup.Latest(Backups)!.Path), Backups);
        Assert.True(read.Ok, read.Message);
        return read;
    }

    private void Wipe()
    {
        DeletionGate.DeleteDirectory(Data, recursive: true);
        Directory.CreateDirectory(Data);
    }

    [Fact]
    public void Every_registry_entry_has_a_file_in_the_fixture_profile()
    {
        var names = Profile().Keys.Append(Shortcut).ToList();

        Assert.Empty(PlayerData.Files.Where(file => !names.Any(name => PlayerData.Of(name) == file)).Select(file => file.Path));
    }

    [Fact]
    public void Back_up_wipe_restore_on_the_same_PC_brings_every_file_back_and_no_token()
    {
        var profile = PutProfile();
        foreach (var secret in Secrets) Put(Data, secret, Utf8("secret-" + secret));
        // A picture in the cache that no setting chooses stays out.
        Put(Data, "covers/custom-cover-" + new string('c', 64) + ".jpg", Jpeg(3));

        var read = BackUpAndRead(PcA);
        Assert.Equal(profile.Count + 1, read.Files.Count);
        Assert.DoesNotContain("secret-", File.ReadAllText(LauncherBackup.Latest(Backups)!.Path));
        Wipe();
        var restored = LauncherBackup.Restore(Data, read.Files, Path.Combine(_root, "before"), Now, Version, PcA, read.Machine);
        Assert.True(restored.Ok, restored.Message);

        foreach (var (name, bytes) in profile)
        {
            Assert.True(File.Exists(At(name)), name + " did not come back");
            // settings.json comes back with onboarding left to run again; everything else byte for byte.
            if (name != "settings.json") Assert.Equal(bytes, File.ReadAllBytes(At(name)));
        }
        Assert.Equal(DiscordLink, ShellLinkFile.Read(At(Shortcut)));
        Assert.True(JsonNode.Parse(File.ReadAllText(At("settings.json")))!["uiPrefs"]!["discord"]!["streamerMode"]!.GetValue<bool>());
        foreach (var secret in Secrets) Assert.False(File.Exists(At(secret)), secret + " was restored");
    }

    [Fact]
    public void A_backup_holds_no_user_name_no_profile_path_and_no_shortcut_bytes()
    {
        PutProfile();

        BackUpAndRead(PcA);
        var text = File.ReadAllText(LauncherBackup.Latest(Backups)!.Path);

        Assert.DoesNotContain("Player One", text);
        Assert.DoesNotContain("PC-A", text);
        Assert.DoesNotContain(@"C:\\Users", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:/Users", text, StringComparison.OrdinalIgnoreCase);
        // The shortcut is kept as what it does; its own bytes (this PC's name, volume and tracker ids) are not in the file.
        Assert.Contains("--processStart Discord.exe", text);
        Assert.DoesNotContain(Convert.ToBase64String(File.ReadAllBytes(At(Shortcut)))[..40], text);
    }

    [Fact]
    public void Another_PCs_backup_lands_under_this_user_and_brings_none_of_its_undo_records()
    {
        PutProfile();
        var read = BackUpAndRead(PcA);
        Wipe();

        var restored = LauncherBackup.Restore(Data, read.Files, Path.Combine(_root, "before"), Now, Version, PcB, read.Machine);

        Assert.True(restored.Ok, restored.Message);
        // That PC's Optimize originals and frame generation records would write its files into this PC's games: left out.
        foreach (var undo in new[] { "game-settings/kept.json", "game-settings/cs2.on.json", "game-settings/cs2.nvidia.json", "game-settings/cs2.original.cfg", "frame-gen/games.json", "keep.json" })
            Assert.False(File.Exists(At(undo)), undo + " came from another PC");
        // Choices still come: each game's window mode, the games added by hand, under this PC's profile and user.
        Assert.True(File.Exists(At("game-settings/window-modes.json")));
        Assert.Contains(@"D:\\Profiles\\Someone Else\\Games\\Celeste", File.ReadAllText(At("installed-games.json")));
        Assert.Contains(@"<UserId>PC-B\Someone Else</UserId>", File.ReadAllText(At("startup-removed/task-0f.xml")));
        Assert.Equal(@"D:\Profiles\Someone Else\AppData\Local\Discord\Update.exe", ShellLinkFile.Read(At(Shortcut))!.Target);
    }

    [Fact]
    public void A_backup_too_large_to_restore_is_never_written_as_one()
    {
        Put(Data, "settings.json", Utf8("{}"));
        Put(Data, "store/wishlist-v2.json", Utf8("[]"));
        Put(Data, "game-settings/huge.original.sav", new byte[600_000]);

        var backedUp = LauncherBackup.BackUpDaily(Data, Backups, Now, Version, replace: true, PcA, cap: 100_000);

        // The game's own file went, so the backup fits and is offered.
        Assert.True(backedUp.Ok, backedUp.Message);
        var offer = LauncherBackup.Latest(Backups);
        Assert.NotNull(offer);
        Assert.True(new FileInfo(offer!.Path).Length <= 100_000);
        Assert.Equal(["settings.json", "store/wishlist-v2.json"], LauncherBackup.Validate(File.ReadAllText(offer.Path), Backups).Files.Select(file => file.Name));

        // When even the choices alone do not fit, nothing is written and the last good backup stays.
        Put(Data, "notes/notes.json", Utf8("""{"notes":[{"text":""" + "\"" + new string('n', 200_000) + "\"}]}"));
        var refused = LauncherBackup.BackUpDaily(Data, Backups, Now, Version, replace: true, PcA, cap: 100_000);
        Assert.False(refused.Ok);
        Assert.Equal(offer.Path, LauncherBackup.Latest(Backups)!.Path);
        Assert.DoesNotContain("notes/notes.json", LauncherBackup.Validate(File.ReadAllText(offer.Path), Backups).Files.Select(file => file.Name));
    }

    [Fact]
    public void A_picture_is_kept_once_for_every_daily_copy_and_goes_when_none_names_it()
    {
        Put(Data, "settings.json", Utf8($$$"""{"customCoverImages":{"steam:1":"{{{CoverFile}}}"}}"""));
        Put(Data, "covers/" + CoverFile, Jpeg(1, 50_000));

        LauncherBackup.BackUpDaily(Data, Backups, Now, Version, replace: true, PcA);
        LauncherBackup.BackUpDaily(Data, Backups, Now.AddDays(1), Version, replace: true, PcA);

        var pictures = Directory.GetFiles(Backups, "exo-launcher-picture-*");
        Assert.Single(pictures);
        Assert.All(Directory.GetFiles(Backups, "exo-launcher-backup-*.json"), file => Assert.True(new FileInfo(file).Length < 5_000));
        Assert.Equal(Jpeg(1, 50_000), LauncherBackup.Validate(File.ReadAllText(LauncherBackup.Latest(Backups)!.Path), Backups).Files.Single(file => file.Name.StartsWith("covers/")).Bytes);

        // The cover went back to the store's: the next day's copies name no picture, and once the old copies are pruned it goes.
        Put(Data, "settings.json", Utf8("{}"));
        // Written a day ago (the clock ticks only every 15 ms, so a picture and a backup written together can share a time).
        foreach (var picture in Directory.GetFiles(Backups, "exo-launcher-picture-*")) Assert.Equal(Now.UtcDateTime, File.GetLastWriteTimeUtc(picture));
        for (var day = 2; day < 2 + LauncherBackup.KeepDaily; day++) LauncherBackup.BackUpDaily(Data, Backups, Now.AddDays(day), Version, replace: true, PcA);
        Assert.Empty(Directory.GetFiles(Backups, "exo-launcher-picture-*"));
    }

    [Theory]
    [InlineData("Start", false, false, 0, true)]
    [InlineData("Start", true, false, 0, false)]
    [InlineData("Start", true, true, 600, false)]
    [InlineData("GameClosed", true, false, 0, true)]
    [InlineData("GameClosed", false, false, 0, true)]
    [InlineData("GameClosed", true, true, 600, false)]
    [InlineData("Exit", false, false, 0, true)]
    [InlineData("Exit", true, false, 60, false)]
    [InlineData("Exit", true, false, 600, true)]
    [InlineData("Exit", true, true, 599, false)]
    public void Todays_backup_is_written_when_the_table_says(string moment, bool todayWritten, bool gameRunning, int secondsSince, bool due) =>
        Assert.Equal(due, LauncherBackup.Due(Enum.Parse<LauncherBackup.Moment>(moment), todayWritten, gameRunning, TimeSpan.FromSeconds(secondsSince)));

    [Fact]
    public void Lucids_settings_go_into_the_backup_as_the_change_that_puts_them_back()
    {
        var lucid = Path.Combine(_root, "LucidEQ", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(lucid)!);
        File.WriteAllText(lucid, """{"AudioSourceType":1,"SelectedHeadphoneId":"hd600","SelectedHeadphoneName":"HD 600","HasCompletedAudioSourceOnboarding":true,"OptimizeForEsports":false,"NightShiftEnabled":true,"HotkeyModifiers":2,"HotkeyVirtualKey":76}""");
        Put(Data, "settings.json", Utf8("{}"));

        var entry = Assert.Single(LauncherBackup.Gather(Data, PcA with { LucidSettings = lucid }), e => e.Name == "lucid-held.json");
        var change = LucidSettings.FromStored(Encoding.UTF8.GetString(entry.Bytes));

        Assert.NotNull(change);
        Assert.Equal("hd600", change!.Headphone);
        Assert.False(change.GameMode);
        Assert.True(change.NightShift);
        Assert.Equal((2, 76), change.Hotkey);
    }

    [Fact]
    public void A_file_past_its_limit_stays_out_and_the_rest_is_still_backed_up()
    {
        Put(Data, "settings.json", Utf8("{}"));
        Put(Data, "game-settings/huge.original.sav", new byte[5 * 1024 * 1024]);

        var entries = LauncherBackup.Gather(Data, PcA);

        Assert.Equal(["settings.json"], entries.Select(entry => entry.Name));
    }

    [Fact]
    public void A_damaged_file_stays_out_and_the_backup_still_restores()
    {
        Put(Data, "settings.json", Utf8("{}"));
        Put(Data, "controllers/054c_0ce6.json", Utf8("{\"original\":"));
        Put(Data, "store/wishlist-v2.json", Utf8("[]"));

        var read = BackUpAndRead(PcA);

        Assert.Equal(["settings.json", "store/wishlist-v2.json"], read.Files.Select(file => file.Name));
    }

    [Theory]
    [InlineData("riot-id.txt", "not a riot id")]
    [InlineData("store/wishlist-v2.json", "{\"appId\":\"620\"}")]
    [InlineData("installed-games.json", "[]")]
    [InlineData("startup-removed/task-0f.xml", "MZ not a task")]
    [InlineData(Shortcut, "{\"Arguments\":\"no target\"}")]
    public void A_file_of_the_wrong_shape_refuses_the_whole_restore(string name, string text)
    {
        var document = LauncherBackup.Serialize([new LauncherBackup.Entry("settings.json", Utf8("{}")), new LauncherBackup.Entry(name, Utf8(text))], Now, Version);

        Assert.False(LauncherBackup.Validate(document).Ok);
    }
    // ------------------------------------------------------------------ review round 2

    [Fact]
    public void A_PC_removes_only_its_own_old_pictures_and_none_while_a_backup_cannot_be_read()
    {
        // Every time here is fixed: a backup and the pictures it writes are stamped with the backup's own time (Now), and the
        // other files with dates set below, so nothing depends on the clock's tick or on today's date.
        static DateTime Day(int day) => new(2026, 10, day, 12, 0, 0, DateTimeKind.Utc);
        Put(Data, "settings.json", Utf8("{}"));
        Directory.CreateDirectory(Backups);
        var device = LauncherBackup.DeviceId(Data);
        var mineOld = Path.Combine(Backups, $"exo-launcher-picture-{device}-{new string('1', 64)}.jpg");
        var mineNew = Path.Combine(Backups, $"exo-launcher-picture-{device}-{new string('2', 64)}.jpg");
        var otherPc = Path.Combine(Backups, $"exo-launcher-picture-abcdef12-{new string('3', 64)}.jpg");
        foreach (var picture in new[] { mineOld, mineNew, otherPc }) File.WriteAllBytes(picture, Jpeg(9));
        File.SetLastWriteTimeUtc(mineOld, Day(1));
        File.SetLastWriteTimeUtc(mineNew, Day(20));
        File.SetLastWriteTimeUtc(otherPc, Day(1));
        // Another PC's backup of October 10, kept in the folder: a picture of this PC's newer than it may belong to a backup
        // of this PC's that OneDrive is still bringing, so it stays.
        var kept = Path.Combine(Backups, "exo-launcher-backup-20261010-abcdef12.json");
        File.WriteAllText(kept, LauncherBackup.Serialize([new LauncherBackup.Entry("settings.json", Utf8("{}"))], Day(10), Version));
        File.SetLastWriteTimeUtc(kept, Day(10));
        // And one OneDrive has not finished bringing: this PC cannot read it.
        var torn = Path.Combine(Backups, "exo-launcher-backup-20261101-abcdef12.json");
        File.WriteAllText(torn, "{\"kind\":\"exo-launcher-backup\",\"files\":{");
        File.SetLastWriteTimeUtc(torn, Day(25));

        LauncherBackup.BackUpDaily(Data, Backups, Now, Version, replace: true, PcA);
        Assert.True(File.Exists(mineOld), "nothing goes while a backup cannot be read");

        DeletionGate.DeleteBackup(torn, Backups);
        LauncherBackup.BackUpDaily(Data, Backups, Now, Version, replace: true, PcA);

        Assert.False(File.Exists(mineOld), "this PC's own picture, older than every backup kept and named by none, goes");
        Assert.True(File.Exists(mineNew), "a picture newer than a backup kept may belong to a backup still arriving");
        Assert.True(File.Exists(otherPc), "another PC's picture is never this PC's to remove");
        Assert.Equal(Now.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(Backups, LauncherBackup.DailyFileName(Now, device))));
    }
    [Fact]
    public void A_file_that_is_not_Exos_own_backup_never_stops_the_picture_cleanup()
    {
        // An export the player kept there (too big to read, or half arrived) has Exo's prefix but is not a backup of the folder.
        Put(Data, "settings.json", Utf8("{}"));
        Directory.CreateDirectory(Backups);
        var device = LauncherBackup.DeviceId(Data);
        var old = Path.Combine(Backups, $"exo-launcher-picture-{device}-{new string('1', 64)}.jpg");
        File.WriteAllBytes(old, Jpeg(9));
        File.SetLastWriteTimeUtc(old, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        var stray = Path.Combine(Backups, "exo-launcher-export-my-pc.json");
        File.WriteAllText(stray, "{\"kind\":\"exo-launcher-backup\",\"files\":{");
        File.SetLastWriteTimeUtc(stray, new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc));

        LauncherBackup.BackUpDaily(Data, Backups, Now, Version, replace: true, PcA);

        Assert.False(File.Exists(old), "the old picture goes although a file that is not a backup of Exo's sits beside it");
        Assert.True(File.Exists(stray), "and that file is left alone");
    }

    [Fact]
    public async Task Back_up_now_waits_for_a_backup_already_running()
    {
        Put(Data, "settings.json", Utf8("{}"));
        // The automatic backup, running: it holds the backups' lock on its own thread.
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = new Thread(() => { lock (LauncherBackup.Gate) { holding.Set(); release.Wait(); } });
        first.Start();
        holding.Wait(TestContext.Current.CancellationToken);

        var second = Task.Run(() => LauncherBackup.BackUpDaily(Data, Backups, Now, Version, replace: true, PcA), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted, "Back up now ran beside the backup already running");
        release.Set();
        Assert.True((await second).Ok);
    }

    [Fact]
    public void A_restore_that_finds_a_picture_gone_says_so()
    {
        Put(Data, "settings.json", Utf8($$$"""{"customCoverImages":{"steam:1":"{{{CoverFile}}}"}}"""));
        Put(Data, "covers/" + CoverFile, Jpeg(1));
        LauncherBackup.BackUpDaily(Data, Backups, Now, Version, replace: true, PcA);
        foreach (var picture in Directory.GetFiles(Backups, "exo-launcher-picture-*")) DeletionGate.DeleteBackup(picture, Backups);
        Wipe();

        var read = LauncherBackup.Validate(File.ReadAllText(LauncherBackup.Latest(Backups)!.Path), Backups);
        var restored = LauncherBackup.Restore(Data, read.Files, Path.Combine(_root, "before"), Now, Version, PcA, read.Machine, read.MissingPictures);

        Assert.True(restored.Ok);
        Assert.Equal("Restored. One cover or folder picture was not in the backups folder, so it shows the store's art.", restored.Message);
    }

    [Theory]
    [InlineData("Written", false, false)]
    [InlineData("Recent", false, false)]
    [InlineData("NothingYet", false, false)]
    [InlineData("Shrunk", false, true)]
    [InlineData("TooLarge", true, true)]
    [InlineData("Unreadable", true, true)]
    [InlineData("DidNotCheckOut", true, true)]
    [InlineData("CouldNotWrite", true, true)]
    public void Every_backup_result_says_whether_backups_stopped(string result, bool stopped, bool says)
    {
        var (isStopped, sentence) = LauncherBackup.Say(Enum.Parse<LauncherBackup.Result>(result));

        Assert.Equal(stopped, isStopped);
        Assert.Equal(says, sentence is not null);
        if (stopped) Assert.StartsWith("Backups stopped: ", sentence);
        if (sentence is not null) Assert.DoesNotContain("—", sentence);
    }

    [Fact]
    public void A_backup_that_stops_in_the_middle_of_a_session_is_said_once_when_it_happens()
    {
        var t0 = Now;
        var t1 = Now.AddDays(1);
        LauncherBackup.Last Done(LauncherBackup.Result kind, DateTimeOffset? since = null) => new(kind, Now, since);
        var written = Done(LauncherBackup.Result.Written);
        var stoppedAt0 = Done(LauncherBackup.Result.TooLarge, t0);
        var otherReasonSameStop = Done(LauncherBackup.Result.CouldNotWrite, t0);
        var stoppedAt1 = Done(LauncherBackup.Result.TooLarge, t1);
        var shrunk = Done(LauncherBackup.Result.Shrunk);

        // Every row: the backup before, the backup now, and whether a stop is said.
        (LauncherBackup.Last? Before, LauncherBackup.Last? After, bool Said)[] rows =
        [
            (null, written, false), (null, stoppedAt0, true), (null, null, false),
            (written, written, false), (written, stoppedAt0, true), (written, shrunk, false),
            (stoppedAt0, written, false), (stoppedAt0, stoppedAt0, false), (stoppedAt0, otherReasonSameStop, false),
            (stoppedAt0, stoppedAt1, true), (shrunk, stoppedAt0, true),
        ];
        foreach (var (before, after, said) in rows)
        {
            var stop = LauncherBackup.StopToAnnounce(before, after);
            Assert.Equal(said, stop is not null);
            if (stop is { } announced)
            {
                Assert.Equal(LauncherBackup.Say(after!.Kind).Sentence, announced.Sentence);
                Assert.Equal(after.StoppedSince, announced.Since);
            }
        }

        // And through the event the shell listens to: only a new stop raises it.
        var heard = new List<string>();
        void Listen(string sentence, DateTimeOffset since) => heard.Add(sentence);
        LauncherBackup.StopAnnounced += Listen;
        try
        {
            LauncherBackup.AnnounceStop(written, stoppedAt0);
            LauncherBackup.AnnounceStop(stoppedAt0, stoppedAt0);
        }
        finally { LauncherBackup.StopAnnounced -= Listen; }
        Assert.Equal([LauncherBackup.Say(LauncherBackup.Result.TooLarge).Sentence!], heard);
    }

    [Fact]
    public void A_stopped_backup_is_kept_with_when_it_stopped_and_cleared_by_the_next_good_one()
    {
        Put(Data, "settings.json", Utf8("{}"));
        Put(Data, "notes/notes.json", Utf8("""{"notes":[{"text":""" + "\"" + new string('n', 200_000) + "\"}]}"));

        LauncherBackup.BackUpDaily(Data, Backups, Now, Version, replace: true, PcA, cap: 100_000);
        LauncherBackup.BackUpDaily(Data, Backups, Now.AddHours(1), Version, replace: true, PcA, cap: 100_000);
        var stopped = LauncherBackup.LastResult(Data);
        Assert.Equal(LauncherBackup.Result.TooLarge, stopped!.Kind);
        Assert.Equal(Now, stopped.StoppedSince);

        LauncherBackup.BackUpDaily(Data, Backups, Now.AddHours(2), Version, replace: true, PcA);
        Assert.Equal(LauncherBackup.Result.Written, LauncherBackup.LastResult(Data)!.Kind);
        Assert.Null(LauncherBackup.LastResult(Data)!.StoppedSince);
    }

    [Fact]
    public void A_user_name_with_accents_written_escaped_is_still_scrubbed_and_restored()
    {
        var jose = PcA with { Profile = @"C:\Users\José", User = @"PC-A\José" };
        // The default JSON writer escapes é as \u00E9, as SettingsService and most of Exo's files write it.
        var games = System.Text.Json.JsonSerializer.Serialize(new { games = new[] { new { path = @"C:\Users\José\Games\a.exe" } } });
        Assert.Contains("\\u00E9", games);
        Put(Data, "settings.json", Utf8("{}"));
        Put(Data, "installed-games.json", Utf8(games));

        var read = BackUpAndRead(jose);
        Assert.DoesNotContain("Jos", File.ReadAllText(LauncherBackup.Latest(Backups)!.Path));
        Wipe();
        LauncherBackup.Restore(Data, read.Files, Path.Combine(_root, "before"), Now, Version, PcB, read.Machine);

        Assert.Equal(@"D:\Profiles\Someone Else\Games\a.exe", JsonNode.Parse(File.ReadAllText(At("installed-games.json")))!["games"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void A_profile_is_replaced_on_a_path_boundary_and_a_path_outside_it_is_kept()
    {
        var al = PcA with { Profile = @"C:\Users\Al" };
        Put(Data, "settings.json", Utf8("{}"));
        Put(Data, "installed-games.json", Utf8("""{"games":[{"path":"C:\\Users\\Al\\Games\\a.exe"},{"path":"C:\\Users\\Alice\\Games\\b.exe"},{"path":"E:\\Games\\c.exe"}]}"""));

        var read = BackUpAndRead(al);
        var text = File.ReadAllText(LauncherBackup.Latest(Backups)!.Path);
        Wipe();
        LauncherBackup.Restore(Data, read.Files, Path.Combine(_root, "before"), Now, Version, PcB, read.Machine);
        var games = JsonNode.Parse(File.ReadAllText(At("installed-games.json")))!["games"]!.AsArray().Select(game => game!["path"]!.GetValue<string>()).ToList();

        Assert.Equal([@"D:\Profiles\Someone Else\Games\a.exe", @"C:\Users\Alice\Games\b.exe", @"E:\Games\c.exe"], games);
        Assert.Contains(@"C:\\\\Users\\\\Alice", text);
    }

    [Fact]
    public void Two_backups_of_one_PC_carry_different_marks_and_both_are_known_as_this_PC()
    {
        var first = LauncherBackup.MachineMark.Of("machine-a");
        var second = LauncherBackup.MachineMark.Of("machine-a");

        Assert.NotEqual(first.Mark, second.Mark);
        Assert.True(first.Is("machine-a") && second.Is("machine-a"));
        Assert.False(first.Is("machine-b"));
        Assert.DoesNotContain("machine-a", first.Mark + first.Salt);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", true)]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", true)]
    [InlineData("03000200-0400-0500-0006-000700080009", true)]
    [InlineData("4c4c4544-0042-3510-8051-b4c04f4d5432", false)]
    public void A_placeholder_board_uuid_marks_no_PC(string uuid, bool placeholder) =>
        Assert.Equal(placeholder, PcHealth.IsPlaceholderUuid(Guid.Parse(uuid).ToByteArray()));

    [Fact]
    public void A_shortcut_that_cannot_be_made_again_and_a_task_that_ran_as_Windows_stay_out_and_are_said()
    {
        Put(Data, "settings.json", Utf8("{}"));
        var system = "<?xml version=\"1.0\"?><Task><Principals><Principal><UserId>S-1-5-18</UserId></Principal></Principals></Task>";
        var files = new List<LauncherBackup.Entry>
        {
            new("settings.json", Utf8("{}")),
            new("startup-removed/task-0f.xml", Utf8(system)),
            new(Shortcut, Utf8(JsonSerializer.Serialize(new ShellLinkFile.Link(new string('x', 40_000), "", "", "", 0, "")))),
        };

        var restored = LauncherBackup.Restore(Data, files, Path.Combine(_root, "before"), Now, Version, PcA, LauncherBackup.MachineMark.Of(PcA.Machine));

        Assert.True(restored.Ok, restored.Message);
        Assert.Equal("Restored. One start-up shortcut could not be made again, so it was left out. One start-up task ran as Windows itself, so it was left out.", restored.Message);
        Assert.False(File.Exists(At("startup-removed/task-0f.xml")));
        Assert.False(File.Exists(At(Shortcut)));
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(4.5, 1.5)]
    [InlineData(6, 0)]
    [InlineData(9, 0)]
    public void Lucid_and_the_backup_at_exit_share_one_budget(double lucidTook, double backupMay) =>
        Assert.Equal(TimeSpan.FromSeconds(backupMay), LauncherBackup.BackupMayWait(TimeSpan.FromSeconds(lucidTook)));
}
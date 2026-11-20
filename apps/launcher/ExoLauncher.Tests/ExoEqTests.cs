using System.Text.Json.Nodes;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// AutoEq's list, the sounds and their switching, the Lucid import and the Equalizer APO installer's checks. Ways it could
/// fail: a name with brackets, or a link with unescaped ones, is cut short; a link climbs out of results/; a jack's plain
/// "Headphones" or a name two models fit guesses a headset; a game's sound stays after it exits, or plays on an output it
/// wasn't meant for; Night Shift's hours across midnight are read backwards; Lucid's IEMs name a headphone, a game is matched
/// by a word it only contains, a choice made in Exo is overwritten; a file that isn't the pinned installer passes, or a
/// download that left SourceForge does. Every name, id and file here is made up, not a real PC's.
/// </summary>
public sealed class ExoEqTests
{
    private const string Index = """
        # Recommended Results
        This is a list of recommended results.

        - [1MORE Aero (ANC Off)](./HypetheSonics/GRAS%20RA0045%20in-ear/1MORE%20Aero%20(ANC%20Off))
        - [HyperX Cloud II](./oratory1990/over-ear/HyperX%20Cloud%20II)
        - [HyperX Cloud II Wireless](./Rtings/HMS%20II.3%20over-ear/HyperX%20Cloud%20II%20Wireless)
        - [SteelSeries Arctis Nova 7](./Rtings/Bruel%20&%20Kjaer%205128%20over-ear/SteelSeries%20Arctis%20Nova%207)
        - [ZiiGaat Lush](./Super%20Review/in-ear/ZiiGaat%20Lush)
        - [Sneaky](./x/../../etc/passwd)
        - [Elsewhere](https://example.com/a/b)
        - [Some Buds](./DHRME/earbud/Some%20Buds)
        """;

    private static readonly List<AutoEqCatalog.Headphone> List = AutoEqCatalog.Parse(Index);

    [Fact]
    public void The_list_is_read_with_its_brackets_and_nothing_outside_results()
    {
        Assert.Equal(["1MORE Aero (ANC Off)", "HyperX Cloud II", "HyperX Cloud II Wireless", "SteelSeries Arctis Nova 7", "ZiiGaat Lush", "Some Buds"], List.Select(h => h.Name));
        var aero = List[0];
        Assert.Equal(("HypetheSonics/GRAS RA0045 in-ear/1MORE Aero (ANC Off)", "HypetheSonics", "in-ear"), (aero.Id, aero.Source, aero.Form));
        Assert.Equal("over-ear", List[3].Form);
        Assert.Equal("earbud", List[^1].Form);
    }

    [Fact]
    public void A_preset_address_is_escaped_and_stays_under_results()
    {
        Assert.Equal(AutoEqCatalog.Root + "Super%20Review/in-ear/ZiiGaat%20Lush/ZiiGaat%20Lush%20ParametricEQ.txt", AutoEqCatalog.PresetAddress("Super Review/in-ear/ZiiGaat Lush"));
        Assert.Null(AutoEqCatalog.PresetAddress("a/../b"));
        Assert.Null(AutoEqCatalog.PresetAddress("single"));
        Assert.Null(AutoEqCatalog.PresetAddress(@"a\b/c"));
    }

    [Theory]
    [InlineData("Headphones (SteelSeries Arctis Nova 7)", "SteelSeries Arctis Nova 7")]
    [InlineData("Speakers (Arctis Nova 7 Wireless)", "SteelSeries Arctis Nova 7")]
    [InlineData("Headset Earphone (HyperX Cloud II Wireless)", "HyperX Cloud II Wireless")]
    [InlineData("Speakers (HyperX Cloud II)", "HyperX Cloud II")]
    [InlineData("Headphones (High Definition Audio Device)", null)]
    [InlineData("Speakers (Realtek(R) Audio)", null)]
    [InlineData("", null)]
    public void The_output_name_guesses_one_model_or_none(string output, string? model) =>
        Assert.Equal(model, AutoEqCatalog.Guess(output, List)?.Name);

    [Fact]
    public void Lucids_name_finds_its_model() =>
        Assert.Equal("ZiiGaat Lush", AutoEqCatalog.Named("Ziigaat  lush", List)?.Name);

    private const string Phones = "{11111111-2222-3333-4444-555555555555}";
    private const string Speakers = "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";

    private static EqStore Store()
    {
        var store = new EqStore();
        store.Outputs[Phones] = new EqOutput
        {
            Name = "Headphones",
            Headphone = new EqCorrection { Id = "a/b/Pair", Name = "Pair", Source = "oratory1990", Filters = [new EqBand { Type = "PK", Hz = 3000, Gain = -2, Q = 2 }] },
            Sound = ExoEqSounds.Bass,
        };
        store.Outputs[Speakers] = new EqOutput { Name = "Speakers", Sound = ExoEqSounds.Flat };
        store.Games["steam:1"] = ExoEqSounds.Footsteps;
        return store;
    }

    [Fact]
    public void Each_output_plays_its_own_chain()
    {
        var store = Store();
        var phones = ExoEqSounds.Chain(Phones, store.Outputs[Phones], store, null, night: false);
        Assert.Equal([new(EqKind.Peak, 3000, -2, 2), new(EqKind.LowShelf, 105, 4, 0.71)], phones.Filters);
        Assert.Empty(ExoEqSounds.Chain(Speakers, store.Outputs[Speakers], store, null, night: false).Filters);
        var text = ExoEq.TextFor(store, null, night: false, compare: false);
        Assert.Contains("Device: " + Phones, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Device: " + Speakers, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_games_sound_plays_while_it_runs_and_the_outputs_comes_back_after()
    {
        var store = Store();
        var playing = ExoEqSounds.Chain(Phones, store.Outputs[Phones], store, ("steam:1", "A Game"), night: false);
        Assert.Equal(ExoEqSounds.Filters(ExoEqSounds.Footsteps, []), playing.Filters.Skip(1));
        Assert.Contains("Sound: Footsteps, for A Game", playing.Notes);
        // The correction stays under the game's sound.
        Assert.Equal(new EqFilter(EqKind.Peak, 3000, -2, 2), playing.Filters[0]);
        // A game without its own sound changes nothing.
        Assert.Equal(ExoEqSounds.Chain(Phones, store.Outputs[Phones], store, null, false).Filters,
            ExoEqSounds.Chain(Phones, store.Outputs[Phones], store, ("steam:2", "Other"), false).Filters);
        // On speakers the game's sound plays too, with no correction.
        Assert.Equal(ExoEqSounds.Filters(ExoEqSounds.Footsteps, []), ExoEqSounds.Chain(Speakers, store.Outputs[Speakers], store, ("steam:1", "A Game"), false).Filters);
    }

    [Fact]
    public void Off_and_compare_write_no_filters()
    {
        var store = Store();
        Assert.DoesNotContain("Filter:", ExoEq.TextFor(store, null, false, compare: true), StringComparison.Ordinal);
        store.On = false;
        Assert.DoesNotContain("Filter:", ExoEq.TextFor(store, null, false, compare: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_takes_its_bands_and_skips_flat_ones()
    {
        List<EqBand> custom = [new() { Hz = 62, Gain = 3 }, new() { Hz = 1000, Gain = 0 }, new() { Type = "HSC", Hz = 8000, Gain = -2, Q = 0.7 }];
        Assert.Equal([new(EqKind.Peak, 62, 3, 1.41), new(EqKind.HighShelf, 8000, -2, 0.7)], ExoEqSounds.Filters(ExoEqSounds.Custom, custom));
        Assert.Empty(ExoEqSounds.Filters(ExoEqSounds.Flat, custom));
    }

    [Fact]
    public void Night_Shift_adds_its_filters_inside_its_hours_across_midnight()
    {
        var store = Store();
        Assert.Equal(ExoEqSounds.Night, ExoEqSounds.Chain(Speakers, store.Outputs[Speakers], store, null, night: true).Filters);
        TimeSpan T(int h, int m = 0) => new(h, m, 0);
        Assert.True(ExoEqSounds.InHours(T(23), T(21), T(6)));
        Assert.True(ExoEqSounds.InHours(T(5, 59), T(21), T(6)));
        Assert.False(ExoEqSounds.InHours(T(6), T(21), T(6)));
        Assert.False(ExoEqSounds.InHours(T(12), T(21), T(6)));
        Assert.True(ExoEqSounds.InHours(T(13), T(9), T(17)));
        Assert.False(ExoEqSounds.InHours(T(18), T(9), T(17)));
        Assert.False(ExoEq.NightNow(new EqStore(), DateTime.Today.AddHours(23)));
    }

    [Fact]
    public void The_store_reads_back_and_drops_what_isnt_its_shape()
    {
        var store = Store();
        var back = EqStore.Read(store.Text());
        Assert.Equal("Pair", back.Outputs[Phones.ToUpperInvariant()].Headphone!.Name);
        Assert.Equal(ExoEqSounds.Footsteps, back.Games["STEAM:1"]);
        var odd = EqStore.Read("""{"outputs":{"not-an-id":{"sound":"bass"}},"games":{"steam:9":"loud"}}""");
        Assert.Empty(odd.Outputs);
        Assert.Empty(odd.Games);
    }

    // A Lucid settings file of the shape Lucid 6 writes, made up for the test.
    private const string LucidFile = """
        {
          "AudioSourceType": 1, "SelectedHeadphoneId": "ziigaat_lush", "SelectedHeadphoneName": "ZiiGaat Lush",
          "HasCompletedAudioSourceOnboarding": true, "LastEqState": true, "OptimizeForEsports": true,
          "NightShiftEnabled": true, "NightShiftStartTime": "22:30:00", "NightShiftEndTime": "07:00:00", "NightShiftSyncWithWindows": false,
          "GameSettings": {
            "Valorant": { "AutoDetectEnabled": true, "UseGameSpecificEq": true },
            "CS2": { "AutoDetectEnabled": true, "UseGameSpecificEq": true },
            "Rust": { "AutoDetectEnabled": true, "UseGameSpecificEq": true },
            "Call of Duty": { "AutoDetectEnabled": true, "UseGameSpecificEq": true },
            "Fortnite": { "AutoDetectEnabled": true, "UseGameSpecificEq": false }
          },
          "LucidVoiceEnabled": false
        }
        """;

    private const string LucidConfig = "# Lucid EQ begin\r\nDevice: " + Phones + "\r\nInclude: lucid_temp.txt\r\nDevice: all\r\n# Lucid EQ end\r\n";

    private static readonly (string, string)[] Library =
    [
        ("riot:valorant", "VALORANT"), ("steam:730", "Counter-Strike 2"), ("steam:3", "Rusty Lake Hotel"),
        ("bnet:cod", "Call of Duty: Black Ops 7"), ("epic:fn", "Fortnite"), ("steam:4", "Other Game"),
    ];

    [Fact]
    public void Lucids_settings_come_over_once()
    {
        var store = new EqStore();
        store.Games["riot:valorant"] = ExoEqSounds.Vocal;
        LucidImport.Into(store, (JsonObject)JsonNode.Parse(LucidFile)!, LucidConfig, Library, Speakers);
        Assert.True(store.LucidImported);
        Assert.Equal("ZiiGaat Lush", store.LucidHeadphone);
        Assert.Equal([Phones], store.LucidOutputs);
        Assert.True(store.On);
        Assert.Equal((true, "22:30", "07:00", false), (store.Night.On, store.Night.Start, store.Night.End, store.Night.Sync));
        // Valorant keeps the sound chosen in Exo; Rust is not Rusty Lake; Fortnite had no game EQ.
        Assert.Equal(new Dictionary<string, string> { ["riot:valorant"] = ExoEqSounds.Vocal, ["steam:730"] = ExoEqSounds.Footsteps, ["bnet:cod"] = ExoEqSounds.Footsteps },
            store.Games.OrderBy(g => g.Key).ToDictionary());

        Assert.True(LucidImport.Resolve(store, List, h => new EqCorrection { Id = h.Id, Name = h.Name, Source = h.Source }));
        Assert.Equal("ZiiGaat Lush", store.Outputs[Phones].Headphone!.Name);
        Assert.Null(store.LucidHeadphone);
        Assert.False(LucidImport.Resolve(store, List, _ => null));
    }

    [Fact]
    public void Lucids_own_iems_and_a_block_for_all_outputs()
    {
        var iems = new EqStore();
        LucidImport.Into(iems, (JsonObject)JsonNode.Parse("""{"AudioSourceType":0,"HasCompletedAudioSourceOnboarding":true,"SelectedHeadphoneName":"Lucid IEMs"}""")!, null, Library, Speakers);
        Assert.Null(iems.LucidHeadphone);

        var all = new EqStore();
        LucidImport.Into(all, (JsonObject)JsonNode.Parse(LucidFile)!, "# Lucid EQ begin\r\nDevice: all\r\nInclude: lucid_temp.txt\r\n# Lucid EQ end\r\n", [], Speakers);
        Assert.Equal([Speakers], all.LucidOutputs);
        // An output already corrected in Exo keeps its pick.
        all.Outputs[Speakers] = new EqOutput { Headphone = new EqCorrection { Name = "Mine" } };
        LucidImport.Resolve(all, List, h => new EqCorrection { Name = h.Name });
        Assert.Equal("Mine", all.Outputs[Speakers].Headphone!.Name);

        // A garbled file still marks the import done.
        var odd = new EqStore();
        LucidImport.Into(odd, (JsonObject)JsonNode.Parse("""{"AudioSourceType":"one","GameSettings":[1]}""")!, null, Library, null);
        Assert.True(odd.LucidImported);
        Assert.Empty(odd.Games);
    }

    [Theory]
    [InlineData("\"C:\\Exo\\app\\addons\\lucid\\Lucid.exe\" /startup", "C:\\Exo\\app\\addons\\lucid\\Lucid.exe")]
    [InlineData("C:\\Exo\\app\\addons\\lucid\\Lucid.exe /startup", "C:\\Exo\\app\\addons\\lucid\\Lucid.exe")]
    [InlineData("\"unclosed", null)]
    public void A_sign_in_entrys_program_is_read(string command, string? exe) => Assert.Equal(exe, LucidRetire.ExeOf(command));

    [Fact]
    public void Only_the_pinned_installer_passes()
    {
        var pin = new EqualizerApo.Pin("1", "https://downloads.sourceforge.net/x", 3,
            Md5: "900150983cd24fb0d6963f7d28e17f72", Sha256: "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"); // "abc"
        Assert.True(EqualizerApo.Matches(new MemoryStream("abc"u8.ToArray()), pin));
        Assert.False(EqualizerApo.Matches(new MemoryStream("abd"u8.ToArray()), pin));
        Assert.False(EqualizerApo.Matches(new MemoryStream("abcd"u8.ToArray()), pin));
        Assert.False(EqualizerApo.Matches(new MemoryStream("abc"u8.ToArray()), pin with { Md5 = new string('0', 32) }));
        Assert.Equal(11_980_366, EqualizerApo.Installer.Size);
        Assert.StartsWith("https://downloads.sourceforge.net/project/equalizerapo/", EqualizerApo.Installer.Address, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://downloads.sourceforge.net/project/a.exe", true)]
    [InlineData("https://netix.dl.sourceforge.net/project/a.exe", true)]
    [InlineData("http://netix.dl.sourceforge.net/project/a.exe", false)]
    [InlineData("https://sourceforge.net.example.com/a.exe", false)]
    [InlineData("https://evilsourceforge.net/a.exe", false)]
    public void Only_SourceForge_counts(string address, bool ok) => Assert.Equal(ok, EqualizerApo.FromSourceForge(new Uri(address)));

    [Theory]
    [InlineData("{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}", Phones)]
    [InlineData("{0.0.0.00000000}.{11111111-2222-3333-4444-55555555555Z}", null)]
    [InlineData(null, null)]
    public void An_endpoint_id_gives_its_device_id(string? endpoint, string? guid) => Assert.Equal(guid, EqualizerApo.GuidOf(endpoint));

    [Fact]
    public void Night_light_schedule_parses_cloudstore_protobuf_tags_at_any_offset()
    {
        // Real CloudStore settings payload with 5-byte varint timestamp before inner 43 42 01 00 payload (21:00 to 07:00).
        byte[] real = Convert.FromHexString("434201000A0201002A06CC93EFD7062A2B0E1543420100CA140E1500CA1E0E0700CA3200CA3C0000000000");
        Assert.Equal((new TimeSpan(21, 0, 0), new TimeSpan(7, 0, 0)), ExoEq.NightLightSchedule(real));

        // Shifted offset with extra fields + non-zero minutes (22:30 to 06:45).
        byte[] shifted = Convert.FromHexString("434201000A0201002A0A01020304050607080943420100CA0A02CA140E161ECA1E0E062DCF28801C00000000");
        Assert.Equal((new TimeSpan(22, 30, 0), new TimeSpan(6, 45, 0)), ExoEq.NightLightSchedule(shifted));

        // Zero-minute byte omitted right before 0xCA tag (21:00 to 07:00).
        byte[] compact = Convert.FromHexString("43420100CA140E15CA1E0E07CA3200");
        Assert.Equal((new TimeSpan(21, 0, 0), new TimeSpan(7, 0, 0)), ExoEq.NightLightSchedule(compact));

        // Truncated or missing tags return null cleanly.
        Assert.Null(ExoEq.NightLightSchedule([0x43, 0x42, 0x01, 0x00]));
    }

    [Theory]
    [InlineData("Counter-Strike 2", ExoLauncher.Models.StoreKind.Steam, ExoEqSounds.Footsteps, "Footsteps")]
    [InlineData("VALORANT", ExoLauncher.Models.StoreKind.Riot, ExoEqSounds.Footsteps, "Footsteps")]
    [InlineData("Call of Duty: Black Ops 6", ExoLauncher.Models.StoreKind.Steam, ExoEqSounds.Footsteps, "Footsteps")]
    [InlineData("Baldur's Gate 3", ExoLauncher.Models.StoreKind.Steam, ExoEqSounds.Vocal, "Vocal")]
    [InlineData("Cyberpunk 2077", ExoLauncher.Models.StoreKind.Gog, ExoEqSounds.Vocal, "Vocal")]
    [InlineData("Elden Ring", ExoLauncher.Models.StoreKind.Steam, ExoEqSounds.Cinematic, "Immersion")]
    [InlineData("Forza Horizon 5", ExoLauncher.Models.StoreKind.Xbox, ExoEqSounds.Cinematic, "Immersion")]
    [InlineData("Rocket League", ExoLauncher.Models.StoreKind.Epic, ExoEqSounds.Cinematic, "Immersion")]
    [InlineData("osu!", ExoLauncher.Models.StoreKind.Local, ExoEqSounds.Flat, "Reference")]
    [InlineData("League of Legends", ExoLauncher.Models.StoreKind.Riot, ExoEqSounds.Flat, "Reference")]
    public void Automatic_profiles_classify_known_titles(string title, ExoLauncher.Models.StoreKind store, string expectedSound, string expectedLabel)
    {
        var game = new ExoLauncher.Models.GameEntry { Id = "g:1", Title = title, Store = store, Installed = true };
        var profile = ExoEqProfiles.Resolve(game);
        Assert.Equal(expectedSound, profile.Sound);
        Assert.Equal(expectedLabel, profile.Label);
    }

    [Fact]
    public void Automatic_profiles_use_steam_tags_and_wiki_for_unlisted_titles()
    {
        var unknown = new ExoLauncher.Models.GameEntry { Id = "steam:999999", Title = "Unlisted Indie Title", Store = ExoLauncher.Models.StoreKind.Steam, Installed = true };
        Assert.False(ExoEqProfiles.HasKnownSignal(unknown, null, null));
        Assert.Equal(ExoEqSounds.Flat, ExoEqProfiles.Resolve(unknown).Sound);

        Assert.Equal(ExoEqSounds.Footsteps, ExoEqProfiles.Resolve(unknown, steamTags: [1663, 1775]).Sound);
        Assert.Equal(ExoEqSounds.Vocal, ExoEqProfiles.Resolve(unknown, steamTags: [1742, 122]).Sound);
        Assert.Equal(ExoEqSounds.Cinematic, ExoEqProfiles.Resolve(unknown, steamTags: [699, 19]).Sound);
        Assert.Equal(ExoEqSounds.Flat, ExoEqProfiles.Resolve(unknown, steamTags: [1752, 19]).Sound);

        var tacticalWiki = new GameWiki.Facts(["Multiplayer"], ["tactical shooter"], "false", true, ["dx12"]);
        var rpgWiki = new GameWiki.Facts(["Singleplayer"], ["rpg"], "false", true, ["dx11"]);
        var racerWiki = new GameWiki.Facts(["Singleplayer"], ["racing"], "false", true, ["dx12"]);
        Assert.Equal(ExoEqSounds.Footsteps, ExoEqProfiles.Resolve(unknown, wiki: tacticalWiki).Sound);
        Assert.Equal(ExoEqSounds.Vocal, ExoEqProfiles.Resolve(unknown, wiki: rpgWiki).Sound);
        Assert.Equal(ExoEqSounds.Cinematic, ExoEqProfiles.Resolve(unknown, wiki: racerWiki).Sound);
    }

    [Fact]
    public void Automatic_game_eq_writes_live_on_apo_output_even_with_empty_store_and_returns_flat_after()
    {
        var emptyStore = new EqStore();
        var activeGame = new EqActiveGame("steam:730", "Counter-Strike 2", ExoEqSounds.Footsteps, "Footsteps", "Cuts explosion rumble");
        var whilePlaying = ExoEq.TextFor(emptyStore, activeGame, night: false, compare: false, activeOutput: (Phones, "Headphones"), desktopFlat: true);
        Assert.Contains("Device: " + Phones, whilePlaying, StringComparison.Ordinal);
        Assert.Contains("Filter:", whilePlaying, StringComparison.Ordinal);
        Assert.NotEmpty(ExoEqSounds.Filters(ExoEqSounds.Cinematic, []));

        var desktop = ExoEq.TextFor(emptyStore, activeGame: null, night: false, compare: false, activeOutput: (Phones, "Headphones"), desktopFlat: true);
        Assert.DoesNotContain("Filter:", desktop, StringComparison.Ordinal);
    }
}

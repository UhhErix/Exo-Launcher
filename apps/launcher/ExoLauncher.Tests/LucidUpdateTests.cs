using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Lucid's own updates. Ways it could fail: a version is read as text (6.0.9 beats 6.0.10) or an older release replaces a
/// newer Lucid; a partial, altered, wrong-sized, wrong-named, unsigned-where-it-was-signed or not-a-program download replaces
/// a working Lucid, or a lookalike release, file name, link or digest is taken; a refused release is fetched every day; a
/// game or a call meets the restart, or the check runs while Lucid is off; the swap happens while the old Lucid still runs;
/// a new Lucid that does not start stays in place, or the old one is not started again; the previous version is lost; Exo's
/// own update (a newer shipped Lucid) is shadowed by an older download, or the other way round; the cache asked more than once a day.
/// </summary>
public sealed class LucidUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-lucid-update-" + Guid.NewGuid().ToString("N"));

    public LucidUpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A release must not make the daily GitHub request without PRIVACY.md and the network registry naming it.</summary>
    [Fact]
    public void UpdatesAreOffUntilPrivacyNamesThem()
    {
        var on = LucidCompanion.UpdatesAvailable;
        var inRegistry = NetworkDestinations.Hosts.Any(host => host.PrivacyRow == NetworkDestinations.LucidUpdates);
        var inPrivacy = File.ReadAllText(Repo.PathOf("PRIVACY.md")).Contains(NetworkDestinations.LucidUpdates, StringComparison.Ordinal);
        Assert.True(!on || (inRegistry && inPrivacy), "Lucid's updates are on, but PRIVACY.md or NetworkDestinations does not name them.");
        Assert.True(on || (!inRegistry && !inPrivacy), "Lucid's updates are off, but PRIVACY.md or NetworkDestinations still claims a request.");
    }

    // ---- an exe that says who it is: a PE header, then "version|product|company|signer" ----

    private static byte[] Exe(string version, string product = "Lucid", string company = "tekkusai", string signer = "", string filler = "")
    {
        var bytes = new byte[0x80];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        bytes[0x3C] = 0x40;
        bytes[0x40] = (byte)'P';
        bytes[0x41] = (byte)'E';
        return [.. bytes, .. Encoding.ASCII.GetBytes($"{version}|{product}|{company}|{signer}|{filler}")];
    }

    private static LucidFile Identify(string path)
    {
        var parts = Encoding.ASCII.GetString(File.ReadAllBytes(path)[0x80..]).Split('|');
        return parts.Length >= 4 && Version.TryParse(parts[0], out var version)
            ? new LucidFile(version, parts[1], parts[2], parts[3].Length == 0 ? null : parts[3])
            : new LucidFile(null, null, null, null);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReleaseJson(byte[] file, string name = "Lucid 6.0.5", string tag = "Lucid", Action<Dictionary<string, object?>, Dictionary<string, object?>>? tweak = null)
    {
        var asset = new Dictionary<string, object?>
        {
            ["name"] = "Lucid.exe", ["state"] = "uploaded", ["size"] = file.Length, ["digest"] = "sha256:" + Hex(file),
            ["browser_download_url"] = $"https://updates.getexo.app/v1/cache/tekkusai/Lucid/releases/download/{tag}/Lucid.exe",
        };
        var release = new Dictionary<string, object?> { ["tag_name"] = tag, ["name"] = name, ["draft"] = false, ["prerelease"] = false, ["assets"] = new[] { asset } };
        tweak?.Invoke(release, asset);
        return JsonSerializer.Serialize(release);
    }

    // ---- the release, as the release states it ----

    [Fact]
    public void The_release_GitHub_states_is_taken()
    {
        var file = Exe("6.0.5");

        var (offer, why) = LucidUpdate.ParseRelease(ReleaseJson(file), floor: 1);

        Assert.Null(why);
        Assert.Equal(new LucidOffer(new Version(6, 0, 5, 0), "Lucid", file.Length, Hex(file), "https://updates.getexo.app/v1/cache/tekkusai/Lucid/releases/download/Lucid/Lucid.exe"), offer);
    }

    public static TheoryData<string, string> Lookalikes => new()
    {
        { "draft", "it is a draft" },
        { "prerelease", "it is a prerelease" },
        { "unnamed tag", "its tag isn't a plain name" },
        { "no version", "it names no version" },
        { "other name", "it has 0 files called Lucid.exe" },
        { "twice", "it has 2 files called Lucid.exe" },
        { "not uploaded", "its file isn't uploaded" },
        { "small", "its size isn't one of Lucid's" },
        { "huge", "its size isn't one of Lucid's" },
        { "no digest", "the release states no SHA-256 for it" },
        { "md5 digest", "the release states no SHA-256 for it" },
        { "short digest", "the release states no SHA-256 for it" },
        { "other host", "its link isn't this repository's" },
        { "http link", "its link isn't this repository's" },
        { "other repo", "its link isn't this repository's" },
        { "other tag in link", "its link isn't this repository's" },
    };

    [Theory]
    [MemberData(nameof(Lookalikes))]
    public void A_release_that_is_not_exactly_Lucids_is_refused(string kind, string expected)
    {
        var file = Exe("6.0.5");
        var json = ReleaseJson(file, tweak: (release, asset) =>
        {
            switch (kind)
            {
                case "draft": release["draft"] = true; break;
                case "prerelease": release["prerelease"] = true; break;
                case "unnamed tag": release["tag_name"] = "../evil"; break;
                case "no version": release["name"] = "Lucid"; release["tag_name"] = "latest"; asset["browser_download_url"] = "https://updates.getexo.app/v1/cache/tekkusai/Lucid/releases/download/latest/Lucid.exe"; break;
                case "other name": asset["name"] = "Lucid-setup.exe"; break;
                case "twice": release["assets"] = new[] { asset, new Dictionary<string, object?>(asset) }; break;
                case "not uploaded": asset["state"] = "starter"; break;
                case "small": asset["size"] = 100; break;
                case "huge": asset["size"] = LucidUpdate.Largest + 1; break;
                case "no digest": asset.Remove("digest"); break;
                case "md5 digest": asset["digest"] = "md5:" + new string('a', 32); break;
                case "short digest": asset["digest"] = "sha256:abc"; break;
                case "other host": asset["browser_download_url"] = "https://evil.example/tekkusai/Lucid/releases/download/Lucid/Lucid.exe"; break;
                case "http link": asset["browser_download_url"] = "http://updates.getexo.app/v1/cache/tekkusai/Lucid/releases/download/Lucid/Lucid.exe"; break;
                case "other repo": asset["browser_download_url"] = "https://updates.getexo.app/v1/cache/someone/Lucid/releases/download/Lucid/Lucid.exe"; break;
                case "other tag in link": asset["browser_download_url"] = "https://updates.getexo.app/v1/cache/tekkusai/Lucid/releases/download/old/Lucid.exe"; break;
            }
        });

        var (offer, why) = LucidUpdate.ParseRelease(json, kind is "small" or "huge" ? LucidUpdate.Smallest : 1);

        Assert.Null(offer);
        Assert.Equal(expected, why);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Text_that_is_not_a_release_is_refused(string json) => Assert.Null(LucidUpdate.ParseRelease(json).Offer);

    // ---- versions ----

    [Theory]
    [InlineData("Lucid 6.0.4", "6.0.4.0")]
    [InlineData("v6.1", "6.1.0.0")]
    [InlineData("Lucid 6.0.10", "6.0.10.0")]
    [InlineData("Lucid 6.0.4.2 beta", "6.0.4.2")]
    [InlineData("Lucid", null)]
    [InlineData("Lucid 6", null)]
    [InlineData("1.2.3.4.5", null)]
    [InlineData(null, null)]
    public void A_release_name_gives_its_version(string? text, string? expected) =>
        Assert.Equal(expected, LucidUpdate.ReleaseVersion(text)?.ToString());

    [Fact]
    public void Versions_compare_as_numbers_and_missing_parts_are_zeros()
    {
        Assert.True(LucidUpdate.ReleaseVersion("6.0.10")! > LucidUpdate.ReleaseVersion("6.0.9")!);
        Assert.True(LucidUpdate.ReleaseVersion("6.1")! > LucidUpdate.ReleaseVersion("6.0.99")!);
        Assert.Equal(LucidUpdate.Canonical(new Version(6, 0, 4)), LucidUpdate.Canonical(new Version(6, 0, 4, 0)));
        Assert.Equal(LucidUpdate.ReleaseVersion("6.0.4"), LucidUpdate.Canonical(new Version(6, 0, 4)));
    }

    [Theory]
    [InlineData("6.0.4", "6.0.5", "dl")]
    [InlineData("6.0.5", "6.0.4", "sh")]
    [InlineData("6.0.4", "6.0.4", "sh")]
    [InlineData("6.0.9", "6.0.10", "dl")]
    [InlineData("6.0.4", null, "sh")]
    [InlineData(null, "6.0.4", "dl")]
    public void Exo_runs_the_newest_of_what_it_shipped_and_what_it_downloaded(string? shipped, string? downloaded, string expected)
    {
        Version? Of(string path) => path == "sh" ? Version.Parse(shipped!) : Version.Parse(downloaded!);

        var chosen = LucidUpdate.Newest(shipped is null ? null : "sh", downloaded is null ? null : "dl", Of);

        Assert.Equal(expected, chosen);
    }

    [Fact]
    public void A_download_whose_version_cannot_be_read_never_beats_the_shipped_one() =>
        Assert.Equal("sh", LucidUpdate.Newest("sh", "dl", path => path == "sh" ? new Version(6, 0, 4) : null));

    // ---- the whole update, with fakes ----

    private sealed class World : IDisposable
    {
        private readonly LucidUpdateTests _test;
        public readonly LucidStore Store;
        public readonly string Shipped;
        public readonly string StateFile;
        public DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        public bool On = true, Busy, Running = true, StopWorks = true, StartsUnlessBad = true;
        public bool BusyOnceDownloaded, Downloaded;
        public byte[] Served = [];
        public string? Json;
        public Exception? ReleaseFails;
        public Exception? DownloadFails;

        public HashSet<string> WontStart = [];
        public readonly List<string> Calls = [];
        public int Locks, Unlocks;

        public World(LucidUpdateTests test, string shippedVersion = "6.0.4")
        {
            _test = test;
            StateFile = Path.Combine(test._root, "data", "lucid-update.json");
            Store = new LucidStore(Path.Combine(test._root, "data", "addons", "lucid-current"), StateFile);
            Shipped = Path.Combine(test._root, "app", "addons", "lucid", "Lucid.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(Shipped)!);
            File.WriteAllBytes(Shipped, Exe(shippedVersion, signer: "tekkusai ltd"));
        }

        public void Dispose() { }

        public string? Hosted() => LucidUpdate.Newest(Shipped, File.Exists(Store.Current) ? Store.Current : null, p => Identify(p).Version);

        public string VersionOf(string path) => Identify(path).Version!.ToString(3);

        public LucidUpdateSteps Steps() => new(
            () => Now,
            () => On,
            () => Busy || (BusyOnceDownloaded && Downloaded),
            ct =>
            {
                Calls.Add("release");
                if (ReleaseFails is not null) throw ReleaseFails;
                return Task.FromResult(Json);
            },
            (offer, dest, ct) =>
            {
                Calls.Add("download " + offer.Version.ToString(3));
                if (DownloadFails is not null)
                {
                    File.WriteAllBytes(dest, Served[..(Served.Length / 2)]);
                    throw DownloadFails;
                }
                File.WriteAllBytes(dest, Served);
                Downloaded = true;
                return Task.CompletedTask;
            },
            Identify,
            Hosted,
            () => Running,
            () =>
            {
                Calls.Add("stop");
                if (StopWorks) Running = false;
                return Task.FromResult(StopWorks);
            },
            path =>
            {
                var version = VersionOf(path);
                Calls.Add("start " + version);
                if (WontStart.Contains(version)) return false;
                Running = true;
                return true;
            },
            () => Task.FromResult(Running),
            () => { Locks++; return Task.CompletedTask; },
            () => Unlocks++)
        {
            Floor = 1,
        };

        public Task<LucidUpdateOutcome> Run() => LucidUpdate.RunAsync(Store, Steps(), CancellationToken.None);

        public void Offer(byte[] file, string name = "Lucid 6.0.5", Action<Dictionary<string, object?>, Dictionary<string, object?>>? tweak = null)
        {
            Served = file;
            Json = ReleaseJson(file, name, tweak: tweak);
        }
    }

    [Fact]
    public async Task A_newer_release_is_downloaded_checked_and_swapped_in_after_Lucid_is_closed_and_started_again()
    {
        using var world = new World(this);
        world.Offer(Exe("6.0.5", signer: "tekkusai ltd"));

        var outcome = await world.Run();

        Assert.Equal(LucidUpdateOutcome.Installed, outcome);
        Assert.Equal(["release", "download 6.0.5", "stop", "start 6.0.5"], world.Calls);
        Assert.Equal(world.Served, File.ReadAllBytes(world.Store.Current));
        Assert.Equal(world.Store.Current, world.Hosted());
        Assert.False(File.Exists(world.Store.Pending));
        Assert.False(File.Exists(world.Store.Download));
        var state = world.Store.Load();
        Assert.Equal(Hex(world.Served), state.Sha256);
        Assert.Equal(world.Served.Length, state.Size);
        Assert.Null(state.Note);
        Assert.Equal((1, 1), (world.Locks, world.Unlocks));
    }

    [Fact]
    public async Task the_cache_is_asked_once_a_day_and_the_same_release_is_not_downloaded_again()
    {
        using var world = new World(this);
        world.Offer(Exe("6.0.5", signer: "tekkusai ltd"));
        await world.Run();
        world.Calls.Clear();

        world.Now += TimeSpan.FromHours(23);
        Assert.Equal(LucidUpdateOutcome.NotDue, await world.Run());
        world.Now += TimeSpan.FromHours(1.1);
        Assert.Equal(LucidUpdateOutcome.Nothing, await world.Run());

        Assert.Equal(["release"], world.Calls);
    }

    [Fact]
    public async Task The_next_release_keeps_the_one_before_for_a_put_back()
    {
        using var world = new World(this);
        world.Offer(Exe("6.0.5", signer: "tekkusai ltd"));
        await world.Run();
        var first = world.Served;
        world.Now += TimeSpan.FromDays(1.1);
        world.Offer(Exe("6.0.6", signer: "tekkusai ltd"), "Lucid 6.0.6");

        Assert.Equal(LucidUpdateOutcome.Installed, await world.Run());

        Assert.Equal(world.Served, File.ReadAllBytes(world.Store.Current));
        Assert.Equal(first, File.ReadAllBytes(world.Store.Previous));
    }

    [Fact]
    public async Task A_release_no_newer_than_the_Lucid_Exo_runs_downloads_nothing()
    {
        using var world = new World(this, shippedVersion: "6.0.5");
        world.Offer(Exe("6.0.5"));

        Assert.Equal(LucidUpdateOutcome.Nothing, await world.Run());

        Assert.Equal(["release"], world.Calls);
    }

    [Fact]
    public async Task A_shipped_Lucid_newer_than_the_download_wins_so_Exos_own_update_is_not_shadowed()
    {
        using var world = new World(this);
        world.Offer(Exe("6.0.5", signer: "tekkusai ltd"));
        await world.Run();

        File.WriteAllBytes(world.Shipped, Exe("6.0.7", signer: "tekkusai ltd")); // Exo updated and ships a newer one

        Assert.Equal(world.Shipped, world.Hosted());
    }

    // ---- never during a game or a call, never while Lucid is off ----

    [Fact]
    public async Task While_Lucid_is_off_nothing_is_asked_downloaded_or_stopped()
    {
        using var world = new World(this) { On = false };
        world.Offer(Exe("6.0.5"));

        Assert.Equal(LucidUpdateOutcome.Off, await world.Run());

        Assert.Empty(world.Calls);
        Assert.False(File.Exists(world.StateFile));
    }

    [Fact]
    public async Task During_a_game_or_call_nothing_is_asked_downloaded_stopped_or_started_even_with_a_download_waiting()
    {
        using var world = new World(this);
        world.Offer(Exe("6.0.5", signer: "tekkusai ltd"));
        world.Busy = true;

        Assert.Equal(LucidUpdateOutcome.Busy, await world.Run());
        Assert.Empty(world.Calls);

        // A verified download kept for the swap still waits.
        world.Busy = false;
        world.BusyOnceDownloaded = true;
        Assert.Equal(LucidUpdateOutcome.Waiting, await world.Run());
        world.Calls.Clear();
        world.Busy = true;
        world.BusyOnceDownloaded = false;
        Assert.Equal(LucidUpdateOutcome.Busy, await world.Run());
        Assert.Empty(world.Calls);
        Assert.True(File.Exists(world.Store.Pending));
        Assert.False(File.Exists(world.Store.Current));
    }

    [Fact]
    public async Task A_game_that_starts_during_the_download_keeps_the_download_and_swaps_it_in_when_it_ends_without_downloading_again()
    {
        using var world = new World(this);
        world.Offer(Exe("6.0.5", signer: "tekkusai ltd"));
        world.BusyOnceDownloaded = true;

        Assert.Equal(LucidUpdateOutcome.Waiting, await world.Run());
        Assert.Equal(["release", "download 6.0.5"], world.Calls);
        Assert.False(File.Exists(world.Store.Current));

        world.BusyOnceDownloaded = false; // the game ended
        Assert.Equal(LucidUpdateOutcome.Installed, await world.Run());

        Assert.Equal(["release", "download 6.0.5", "stop", "start 6.0.5"], world.Calls);
        Assert.Equal(world.Served, File.ReadAllBytes(world.Store.Current));
        Assert.False(File.Exists(world.Store.Pending));
    }

    [Fact]
    public async Task A_Lucid_that_will_not_close_is_not_swapped_and_the_update_waits_with_its_download()
    {
        using var world = new World(this) { StopWorks = false };
        world.Offer(Exe("6.0.5", signer: "tekkusai ltd"));

        Assert.Equal(LucidUpdateOutcome.Waiting, await world.Run());
        Assert.False(File.Exists(world.Store.Current));
        Assert.Equal(world.Shipped, world.Hosted());

        world.StopWorks = true;
        world.Calls.Clear();
        Assert.Equal(LucidUpdateOutcome.Installed, await world.Run());

        Assert.DoesNotContain("download 6.0.5", world.Calls);
    }

    // ---- refusal: nothing but a whole, genuine download ever replaces a working Lucid ----

    private async Task<World> Refused(byte[] served, byte[] described, string name = "Lucid 6.0.5")
    {
        var world = new World(this);
        world.Offer(described, name);
        world.Served = served;
        Assert.Equal(LucidUpdateOutcome.Refused, await world.Run());
        // Nothing replaced, nothing closed, nothing left behind, and the same release is not tried again.
        Assert.DoesNotContain("stop", world.Calls);
        Assert.False(File.Exists(world.Store.Current));
        Assert.False(File.Exists(world.Store.Pending));
        Assert.False(File.Exists(world.Store.Download));
        Assert.Equal(world.Shipped, world.Hosted());
        Assert.Contains(Hex(described), world.Store.Load().Refused);
        Assert.NotNull(world.Store.Load().Note);
        world.Calls.Clear();
        world.Now += TimeSpan.FromDays(2);
        Assert.Equal(LucidUpdateOutcome.Nothing, await world.Run());
        Assert.Equal(["release"], world.Calls);
        return world;
    }

    [Fact]
    public async Task A_download_altered_to_the_same_size_is_thrown_away()
    {
        var real = Exe("6.0.5", signer: "tekkusai ltd", filler: "aaaa");
        var altered = Exe("6.0.5", signer: "tekkusai ltd", filler: "aaab");
        Assert.Equal(real.Length, altered.Length);

        using var world = await Refused(altered, real);
    }

    [Fact]
    public async Task A_download_of_another_size_is_thrown_away()
    {
        var real = Exe("6.0.5", signer: "tekkusai ltd", filler: "aaaa");

        using var world = await Refused(Exe("6.0.5", signer: "tekkusai ltd", filler: "aaaaaa"), real);
    }

    [Fact]
    public async Task A_file_that_is_not_a_Windows_program_is_thrown_away_even_when_its_digest_is_the_stated_one()
    {
        var text = Encoding.ASCII.GetBytes(new string('x', 200));

        using var world = await Refused(text, text);
    }

    [Theory]
    [InlineData("6.0.3", "Lucid", "tekkusai", "tekkusai ltd")] // older than the release says
    [InlineData("6.0.4", "Lucid", "tekkusai", "tekkusai ltd")] // no newer than the one running
    [InlineData("6.0.5", "Notepad", "tekkusai", "tekkusai ltd")] // another program
    [InlineData("6.0.5", "Lucid", "Someone Else", "tekkusai ltd")] // another publisher
    [InlineData("6.0.5", "Lucid", "tekkusai", "")] // the running Lucid is signed, this isn't
    [InlineData("6.0.5", "Lucid", "tekkusai", "other signer")] // signed by someone else
    public async Task A_file_that_says_it_is_not_the_next_Lucid_is_thrown_away(string version, string product, string company, string signer)
    {
        var file = Exe(version, product, company, signer);

        using var world = await Refused(file, file);
    }

    [Fact]
    public async Task A_download_that_stops_part_way_leaves_the_working_Lucid_alone_and_is_tried_again_in_an_hour()
    {
        using var world = new World(this);
        world.Offer(Exe("6.0.5", signer: "tekkusai ltd"));
        world.DownloadFails = new IOException("the connection ended");

        Assert.Equal(LucidUpdateOutcome.Offline, await world.Run());

        Assert.DoesNotContain("stop", world.Calls);
        Assert.False(File.Exists(world.Store.Current));
        Assert.False(File.Exists(world.Store.Download));
        Assert.False(File.Exists(world.Store.Pending));
        Assert.Equal(world.Shipped, world.Hosted());
        Assert.Empty(world.Store.Load().Refused);

        world.DownloadFails = null;
        world.Calls.Clear();
        world.Now += TimeSpan.FromMinutes(30);
        Assert.Equal(LucidUpdateOutcome.NotDue, await world.Run());
        world.Now += TimeSpan.FromMinutes(31);
        Assert.Equal(LucidUpdateOutcome.Installed, await world.Run());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_cache_not_answering_is_quiet_and_asked_again_in_an_hour_not_a_day(bool throws)
    {
        using var world = new World(this);
        world.Json = null;
        if (throws) world.ReleaseFails = new HttpRequestException("offline");

        Assert.Equal(LucidUpdateOutcome.Offline, await world.Run());
        Assert.Null(world.Store.Load().Note);

        world.Now += TimeSpan.FromMinutes(61);
        world.Calls.Clear();
        await world.Run();
        Assert.Equal(["release"], world.Calls);
    }

    [Fact]
    public async Task A_release_that_is_not_Lucids_is_refused_without_a_download_or_a_note()
    {
        using var world = new World(this);
        world.Offer(Exe("6.0.5"), tweak: (_, asset) => asset["browser_download_url"] = "https://evil.example/Lucid.exe");

        Assert.Equal(LucidUpdateOutcome.Refused, await world.Run());

        Assert.Equal(["release"], world.Calls);
        Assert.Null(world.Store.Load().Note);
    }

    [Fact]
    public async Task A_stale_pending_file_that_is_not_the_one_checked_is_deleted_and_never_swapped_in()
    {
        using var world = new World(this);
        Directory.CreateDirectory(world.Store.Folder);
        File.WriteAllBytes(world.Store.Pending, Exe("9.9.9"));
        world.Store.Save(new LucidUpdateState { PendingSha256 = Hex(Exe("6.0.5")) });
        world.Json = null;

        await world.Run();

        Assert.False(File.Exists(world.Store.Pending));
        Assert.DoesNotContain("stop", world.Calls);
        Assert.False(File.Exists(world.Store.Current));
    }

    // ---- rollback ----

    [Fact]
    public async Task A_new_Lucid_that_does_not_start_is_replaced_by_the_one_before_which_is_started_again_and_not_fetched_again()
    {
        using var world = new World(this);
        world.Offer(Exe("6.0.5", signer: "tekkusai ltd"));
        await world.Run();
        var good = world.Served;
        world.Now += TimeSpan.FromDays(1.1);
        var bad = Exe("6.0.6", signer: "tekkusai ltd");
        world.Offer(bad, "Lucid 6.0.6");
        world.WontStart.Add("6.0.6");
        world.Calls.Clear();

        var outcome = await world.Run();

        Assert.Equal(LucidUpdateOutcome.RolledBack, outcome);
        Assert.Equal(["release", "download 6.0.6", "stop", "start 6.0.6", "stop", "start 6.0.5"], world.Calls);
        Assert.Equal(good, File.ReadAllBytes(world.Store.Current));
        Assert.Equal(world.Store.Current, world.Hosted());
        Assert.Contains(Hex(bad), world.Store.Load().Refused);
        Assert.Equal(Hex(good), world.Store.Load().Sha256);
        Assert.Contains("6.0.6", world.Store.Load().Note);
        Assert.False(File.Exists(world.Store.Pending));
        Assert.True(world.Running);

        world.Now += TimeSpan.FromDays(2);
        world.Calls.Clear();
        Assert.Equal(LucidUpdateOutcome.Nothing, await world.Run());
        Assert.Equal(["release"], world.Calls);
    }

    [Fact]
    public async Task With_nothing_downloaded_before_a_Lucid_that_does_not_start_is_dropped_and_the_shipped_one_runs_again()
    {
        using var world = new World(this);
        world.Offer(Exe("6.0.5", signer: "tekkusai ltd"));
        world.WontStart.Add("6.0.5");

        Assert.Equal(LucidUpdateOutcome.RolledBack, await world.Run());

        Assert.False(File.Exists(world.Store.Current));
        Assert.Equal(world.Shipped, world.Hosted());
        Assert.Equal("start 6.0.4", world.Calls[^1]);
        Assert.Null(world.Store.Load().Sha256);
        Assert.Equal(0, world.Store.Load().Size);
    }

    // ---- the store's record ----

    [Fact]
    public void The_state_survives_a_restart_and_unreadable_state_is_empty()
    {
        var store = new LucidStore(Path.Combine(_root, "s", "folder"), Path.Combine(_root, "s", "state.json"));
        store.Save(new LucidUpdateState { NextCheck = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc), Refused = ["abc"], Sha256 = "def", Size = 7, Note = "n" });

        var back = store.Load();

        Assert.Equal(new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc), back.NextCheck);
        Assert.Equal(["abc"], back.Refused);
        Assert.Equal(("def", 7L, "n"), (back.Sha256, back.Size, back.Note));

        File.WriteAllText(Path.Combine(_root, "s", "state.json"), "{ not json");
        Assert.Null(store.Load().NextCheck);
    }
}

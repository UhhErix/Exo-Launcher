using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ExoLauncher.Tests;

/// <summary>
/// Refetching a cover (the game menu's Find cover, and Library Refresh for every game) must never leave a game with less art
/// than it had. Ways this could fail silently:
/// - the old cover is deleted first, so an offline or failed refetch wipes it (what 3.26 did);
/// - a new file that is not a real portrait replaces the old one;
/// - a cover that another game also uses is set aside with the first game's;
/// - a crash mid-refetch leaves a stash that is never put back, or deletes a good cover for a broken one;
/// - the old cover comes back beside the new one and wins;
/// and the junk filter that decides which games get art must not hide a game that happens to have a marker word in its name.
/// The refetch tests run the real RefetchComputedAsync against a stub store, in the test data folder.
/// </summary>
[Collection("CoverCacheStatics")]
public sealed class CoverRefetchSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-refetch-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _cacheFiles = [];

    public CoverRefetchSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, true); } catch (IOException) { }
        foreach (var file in _cacheFiles) { try { DeletionGate.DeleteFile(file); } catch (IOException) { } }
    }

    private static GameEntry SteamGame(string id, string title = "Hades", string? appType = null, string? rowId = null, bool installed = true) =>
        new() { Id = rowId ?? "steam:" + id, Title = title, Store = StoreKind.Steam, LaunchTarget = id, Installed = installed, SteamAppType = appType };

    private static Task<byte[]> Jpeg(int seed, int width = 200, int height = 300) => Image(BitmapEncoder.JpegEncoderId, seed, width, height);

    private static async Task<byte[]> Image(Guid encoderId, int seed, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        new Random(seed).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        await output.GetInputStreamAt(0).ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    private string Cover(string name) => Path.Combine(_root, name);

    // ---- the stash, on its own ----

    [Fact]
    public async Task ARefetchThatFindsNothingPutsTheOldCoverBack()
    {
        var old = await Jpeg(1);
        await File.WriteAllBytesAsync(Cover("1145360.jpg"), old, TestContext.Current.CancellationToken);
        var game = SteamGame("1145360");

        var stashed = CoverArtService.StashComputedCacheFiles(_root, [game], [game]);
        Assert.Single(stashed);
        Assert.False(File.Exists(Cover("1145360.jpg"))); // set aside while the refetch looks

        var restored = CoverArtService.SettleStashedCacheFiles(stashed);

        Assert.Equal(1, restored);
        Assert.Equal(old, await File.ReadAllBytesAsync(Cover("1145360.jpg"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*" + CoverArtService.StashSuffix));
    }

    [Fact]
    public async Task AProvenNewCoverReplacesTheOldOne()
    {
        await File.WriteAllBytesAsync(Cover("1145360.jpg"), await Jpeg(1), TestContext.Current.CancellationToken);
        var game = SteamGame("1145360");
        var stashed = CoverArtService.StashComputedCacheFiles(_root, [game], [game]);

        var fresh = await Jpeg(2);
        await File.WriteAllBytesAsync(Cover("1145360.jpg"), fresh, TestContext.Current.CancellationToken);
        var restored = CoverArtService.SettleStashedCacheFiles(stashed);

        Assert.Equal(0, restored);
        Assert.Equal(fresh, await File.ReadAllBytesAsync(Cover("1145360.jpg"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*" + CoverArtService.StashSuffix));
    }

    [Fact]
    public async Task ANewFileThatIsNotARealPortraitDoesNotReplaceTheOldCover()
    {
        var old = await Jpeg(1);
        await File.WriteAllBytesAsync(Cover("1145360.jpg"), old, TestContext.Current.CancellationToken);
        var game = SteamGame("1145360");
        var stashed = CoverArtService.StashComputedCacheFiles(_root, [game], [game]);

        await File.WriteAllBytesAsync(Cover("1145360.jpg"), new byte[20_000], TestContext.Current.CancellationToken); // a broken download
        CoverArtService.SettleStashedCacheFiles(stashed);

        Assert.Equal(old, await File.ReadAllBytesAsync(Cover("1145360.jpg"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACoverAnotherGameAlsoUsesIsNotSetAside()
    {
        await File.WriteAllBytesAsync(Cover("1145360.jpg"), await Jpeg(1), TestContext.Current.CancellationToken);
        var game = SteamGame("1145360");
        var sibling = SteamGame("1145360", "Hades (other edition)", rowId: "steam:other");

        var stashed = CoverArtService.StashComputedCacheFiles(_root, [game], [game, sibling]);

        Assert.Empty(stashed);
        Assert.True(File.Exists(Cover("1145360.jpg")));
    }

    // ---- the real RefetchComputedAsync, against a stub store, in the test data folder ----

    private static readonly string[] CoverNames = ["{0}.jpg", "{0}_2x.jpg", "steam_{0}.jpg", "steam_{0}_2x.jpg"];

    /// <summary>A Steam game with its old covers on disk (two names) under a number only this test uses.</summary>
    private async Task<(GameEntry Game, string[] Paths, byte[] Old)> GameWithOldCovers()
    {
        var id = (7_000_000 + Random.Shared.Next(1, 900_000)).ToString();
        var game = SteamGame(id);
        var old = await Jpeg(11);
        var paths = new List<string>();
        Directory.CreateDirectory(CoverArtService.CacheRoot);
        foreach (var name in CoverNames.Select(n => string.Format(n, id)))
        {
            var path = Path.Combine(CoverArtService.CacheRoot, name);
            _cacheFiles.Add(path);
            _cacheFiles.Add(path + CoverArtService.StashSuffix);
            paths.Add(path);
        }
        await File.WriteAllBytesAsync(paths[0], old, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(paths[1], old, TestContext.Current.CancellationToken);
        return (game, paths.ToArray(), old);
    }

    private static HttpClient Store(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        ExoHttp.Create(TimeSpan.FromSeconds(10), new StubHandler(answer));

    [Fact]
    public async Task ARefetchThatFindsANewPortraitLeavesNoOldCoverBesideIt()
    {
        var (game, paths, old) = await GameWithOldCovers();
        var fresh = await Jpeg(12, 600, 900);
        var http = Store(request => request.RequestUri!.Host.EndsWith("steamstatic.com", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fresh) { Headers = { ContentType = new("image/jpeg") } } }
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        await CoverArtService.RefetchComputedAsync([game], [game], http, TestContext.Current.CancellationToken);

        Assert.All(paths, path => Assert.False(File.Exists(path + CoverArtService.StashSuffix), path));
        var left = paths.Where(File.Exists).ToArray();
        Assert.NotEmpty(left); // the new portrait arrived
        foreach (var path in left)
            Assert.NotEqual(old, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)); // and no old cover sits beside it
    }

    [Fact]
    public async Task AnOfflineRefetchChangesNothingOnDisk()
    {
        var (game, paths, old) = await GameWithOldCovers();
        var http = Store(_ => throw new HttpRequestException("offline"));

        // Offline the refetch may fail with the network's own error (the caller reports it): the old covers must be back either way.
        try { await CoverArtService.RefetchComputedAsync([game], [game], http, TestContext.Current.CancellationToken); }
        catch (HttpRequestException) { }

        Assert.Equal(old, await File.ReadAllBytesAsync(paths[0], TestContext.Current.CancellationToken));
        Assert.Equal(old, await File.ReadAllBytesAsync(paths[1], TestContext.Current.CancellationToken));
        Assert.All(paths, path => Assert.False(File.Exists(path + CoverArtService.StashSuffix), path));
    }

    [Fact]
    public async Task StartUpRecoveryWaitsWhileARefetchIsRunning()
    {
        var (game, paths, old) = await GameWithOldCovers();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = ExoHttp.Create(TimeSpan.FromSeconds(30), new AsyncStubHandler(async _ =>
        {
            arrived.TrySetResult();
            await release.Task;
            throw new HttpRequestException("offline");
        }));

        var refetch = CoverArtService.RefetchComputedAsync([game], [game], http, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        // The refetch holds the old covers aside right now: a recovery pass must leave them alone.
        Assert.False(File.Exists(paths[0]));
        Assert.True(File.Exists(paths[0] + CoverArtService.StashSuffix));
        Assert.Equal(0, CoverArtService.RecoverStashedCacheFiles(CoverArtService.CacheRoot));
        Assert.True(File.Exists(paths[0] + CoverArtService.StashSuffix));

        release.SetResult();
        try { await refetch; }
        catch (HttpRequestException) { }
        Assert.Equal(old, await File.ReadAllBytesAsync(paths[0], TestContext.Current.CancellationToken)); // the refetch itself put them back
    }

    // ---- recovery after a crash ----

    [Fact]
    public async Task AStashAfterACrashIsPutBackWhenItsCoverIsMissingOrBrokenAndDeletedWhenItIsGood()
    {
        var lost = await Jpeg(1);
        var good = await Jpeg(2);
        var brokenPlaceholder = new byte[20_000];
        await File.WriteAllBytesAsync(Cover("lost.jpg" + CoverArtService.StashSuffix), lost, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Cover("kept.jpg" + CoverArtService.StashSuffix), await Jpeg(3), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Cover("kept.jpg"), good, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Cover("broken.jpg" + CoverArtService.StashSuffix), await Jpeg(4), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Cover("broken.jpg"), brokenPlaceholder, TestContext.Current.CancellationToken);

        var restored = CoverArtService.RecoverStashedCacheFiles(_root);

        Assert.Equal(2, restored);
        Assert.Equal(lost, await File.ReadAllBytesAsync(Cover("lost.jpg"), TestContext.Current.CancellationToken));
        Assert.Equal(good, await File.ReadAllBytesAsync(Cover("kept.jpg"), TestContext.Current.CancellationToken));
        Assert.Equal(await Jpeg(4), await File.ReadAllBytesAsync(Cover("broken.jpg"), TestContext.Current.CancellationToken)); // the stash went back over the broken file
        Assert.Empty(Directory.GetFiles(_root, "*" + CoverArtService.StashSuffix));
    }

    [Fact]
    public async Task TheCacheMaintenanceThatRunsAtStartRecoversWhatACrashLeft()
    {
        var name = "crash-" + Guid.NewGuid().ToString("N") + ".jpg";
        var path = Path.Combine(CoverArtService.CacheRoot, name);
        Directory.CreateDirectory(CoverArtService.CacheRoot);
        _cacheFiles.Add(path);
        _cacheFiles.Add(path + CoverArtService.StashSuffix);
        var kept = await Jpeg(5);
        await File.WriteAllBytesAsync(path + CoverArtService.StashSuffix, kept, TestContext.Current.CancellationToken);

        CoverArtService.ResetCacheMaintenanceStart();
        CoverArtService.StartCacheMaintenanceOnce([]);

        Assert.Equal(kept, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path + CoverArtService.StashSuffix));
    }

    // ---- banners and icons are judged as what they are, not as portraits ----

    [Fact]
    public async Task ARefetchThatFindsANewBannerButNoNewPortraitKeepsTheNewBannerAndRestoresThePortrait()
    {
        var game = SteamGame("1145360");
        var banner = CoverArtService.WideArtFileName(game.Id);
        var oldPortrait = await Jpeg(1);
        await File.WriteAllBytesAsync(Cover("1145360.jpg"), oldPortrait, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Cover(banner), await Jpeg(2, 800, 400), TestContext.Current.CancellationToken);
        var stashed = CoverArtService.StashComputedCacheFiles(_root, [game], [game]);
        Assert.Contains(stashed, s => s.Path.EndsWith(banner, StringComparison.OrdinalIgnoreCase));

        var newBanner = await Jpeg(3, 800, 400);
        await File.WriteAllBytesAsync(Cover(banner), newBanner, TestContext.Current.CancellationToken); // only the banner was found
        CoverArtService.SettleStashedCacheFiles(stashed);

        Assert.Equal(newBanner, await File.ReadAllBytesAsync(Cover(banner), TestContext.Current.CancellationToken)); // not put back over
        Assert.Equal(oldPortrait, await File.ReadAllBytesAsync(Cover("1145360.jpg"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*" + CoverArtService.StashSuffix));
    }

    [Fact]
    public async Task StartUpRecoveryKeepsAValidNewBannerAndIconAndRestoresABrokenOne()
    {
        var banner = CoverArtService.WideArtFileName("steam:1145360");
        var icon = GameIconArt.CacheFileName("steam:1145360");
        var newBanner = await Jpeg(4, 800, 400);
        var newIcon = await Image(BitmapEncoder.PngEncoderId, 5, 400, 600);
        await File.WriteAllBytesAsync(Cover(banner), newBanner, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Cover(banner + CoverArtService.StashSuffix), await Jpeg(6, 800, 400), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Cover(icon), newIcon, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Cover(icon + CoverArtService.StashSuffix), await Image(BitmapEncoder.PngEncoderId, 7, 400, 600), TestContext.Current.CancellationToken);
        var oldBanner2 = await Jpeg(8, 800, 400);
        await File.WriteAllBytesAsync(Cover("hero_broken.jpg"), new byte[20_000], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Cover("hero_broken.jpg" + CoverArtService.StashSuffix), oldBanner2, TestContext.Current.CancellationToken);

        var restored = CoverArtService.RecoverStashedCacheFiles(_root);

        Assert.Equal(1, restored);
        Assert.Equal(newBanner, await File.ReadAllBytesAsync(Cover(banner), TestContext.Current.CancellationToken));
        Assert.Equal(newIcon, await File.ReadAllBytesAsync(Cover(icon), TestContext.Current.CancellationToken));
        Assert.Equal(oldBanner2, await File.ReadAllBytesAsync(Cover("hero_broken.jpg"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*" + CoverArtService.StashSuffix));
    }

    // ---- which games get art ----

    [Theory]
    [InlineData("Blueprint Tycoon")]
    [InlineData("DLC Quest Editor")]
    [InlineData("Shader Wars")]
    public void AnInstalledGameWithAMarkerWordInItsTitleGetsArtWhateverTheStore(string title)
    {
        var steamConfirmed = SteamGame("42", title, "game");
        var steamUnknown = SteamGame("43", title);
        var epic = new GameEntry { Id = "epic:x", Title = title, Store = StoreKind.Epic, Installed = true, Owned = true };
        var gog = new GameEntry { Id = "gog:x", Title = title, Store = StoreKind.Gog, Installed = true, Owned = true };
        var local = new GameEntry { Id = "local:x", Title = title, Store = StoreKind.Local, Installed = true };

        foreach (var game in new[] { steamConfirmed, steamUnknown, epic, gog, local })
        {
            Assert.True(CoverArtService.ShouldWarmLibraryCover(game), game.Id);
            Assert.True(CoverArtService.ShouldWarmWideArt(game), game.Id);
            Assert.True(CoverArtService.ShouldSeekOnlineArt(game, requested: false), game.Id);
        }
    }

    [Fact]
    public void AnUninstalledMarketplaceEntitlementAndAnEngineTitleStaySkipped()
    {
        // An Epic account holds Unreal Marketplace assets it never installed: they never go online for art.
        var asset = new GameEntry { Id = "epic:asset", Title = "Blueprint Starter Kit", Store = StoreKind.Epic, Installed = false, Owned = true };
        Assert.True(CoverArtService.LooksLikeEngineAsset(asset));
        Assert.False(CoverArtService.ShouldSeekOnlineArt(asset, requested: false));
        // The engine's own names stay out even when installed.
        var engine = new GameEntry { Id = "epic:ue", Title = "Unreal Engine 5.4", Store = StoreKind.Epic, Installed = true, Owned = true };
        Assert.True(CoverArtService.LooksLikeEngineAsset(engine));
        Assert.False(CoverArtService.ShouldSeekOnlineArt(engine, requested: false));
    }

    [Fact]
    public void AnEngineMarketplaceAssetIsStillSkippedEvenFromSteam()
    {
        var plugin = SteamGame("44", "Unreal Engine Sample Plugin", "tool", installed: false);
        Assert.False(CoverArtService.ShouldWarmLibraryCover(plugin));
    }

    // The same table is in cloud/covers/test/match.test.mjs (REAL_GAMES and JUNK_RESULTS): the Worker and the Launcher split words alike.
    [Theory]
    [InlineData("Observer: System Redux")]
    [InlineData("Democracy 4")]
    [InlineData("Demonologist")]
    [InlineData("Pandemonium")]
    [InlineData("Tools Up!")]
    [InlineData("DLC Quest")]
    [InlineData("Dr. Demo's Tools")]
    public void ARealGameIsNotJunkInASteamSearchResult(string title)
    {
        Assert.False(CoverArtService.IsJunkStoreName(title, title));
    }

    [Theory]
    [InlineData("Hades Soundtrack", "Hades")]
    [InlineData("Hades Soundtracks", "Hades")]
    [InlineData("Hades - Demo", "Hades")]
    [InlineData("Hades Demos", "Hades")]
    [InlineData("Hades-Demo", "Hades")]
    [InlineData("Hades (Dedicated Server)", "Hades")]
    [InlineData("Hades: Playtest", "Hades")]
    [InlineData("DLC Quest", "Hades")]
    [InlineData("Hades OST", "Hades")]
    [InlineData("Hades SDK", "Hades")]
    [InlineData("Hades Tools", "Hades")]
    [InlineData("Hades/Trailer", "Hades")]
    public void AResultWithAJunkWordTheTitleLacksIsJunk(string result, string wanted)
    {
        Assert.True(CoverArtService.IsJunkStoreName(result, wanted));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class AsyncStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}

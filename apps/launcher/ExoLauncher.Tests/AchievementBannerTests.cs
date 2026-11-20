using System.Text.Json;
using ExoLauncher.Services;
using ExoLauncher.Services.Achievements;

namespace ExoLauncher.Tests;

/// <summary>
/// What a banner's page is handed. Ways it could fail, each silent in the app: the Steam address the live watch builds
/// answering 404 (3.22.0 showed a broken picture this way); a banner carrying an address instead of a picture, which the
/// page's CSP refuses; the CSP and the host drifting apart; a server's error page kept as an icon; the volume changing
/// the kept sound, or the WAV's header; a sound the host expects not being the format it assumes; startup's cleanup of
/// old data deleting the icons (the folder name achievement-icons is on its list).
/// </summary>
public sealed class AchievementBannerTests
{
    private static Unlock Sample(string? icon) => new("g", "Game", "api", "Name", "Words", icon, 3.2, "epic", 8, 66, false, DateTimeOffset.UnixEpoch);

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1];

    private static string BannerPage() => File.ReadAllText(Path.Combine(Repo.Root, "ExoLauncher", "Assets", "Overlay", "banner.html"));

    [Fact]
    public void A_steam_icon_is_asked_for_where_steam_serves_it_and_the_policy_allows_it()
    {
        var url = SteamStatsCache.IconUrl("4115450", "b87e7283d3006cba458d80bdf5d3b5b2fe5b402c.jpg");
        Assert.StartsWith("https://shared.steamstatic.com/community_assets/images/apps/4115450/", url);
        Assert.Equal(url, AchievementIconCache.SanitizeProviderImageUrl(url));
    }

    [Fact]
    public void The_picture_reaches_the_page_in_the_form_its_csp_allows()
    {
        Assert.Contains("img-src data:;", BannerPage());
        var picture = AchievementIconCache.DataUri(Jpeg);
        Assert.StartsWith("data:image/jpeg;base64,", picture);
        using var card = JsonDocument.Parse(AchievementWatch.Card(Sample(picture), 60, false));
        Assert.Equal(picture, card.RootElement.GetProperty("icon").GetString());
    }

    [Theory]
    [InlineData("https://shared.steamstatic.com/community_assets/images/apps/1/a.jpg")]
    [InlineData("C:\\Users\\me\\a.jpg")]
    [InlineData("file:///C:/a.jpg")]
    public void An_address_is_never_passed_on_as_a_picture(string icon)
    {
        using var card = JsonDocument.Parse(AchievementWatch.Card(Sample(icon), 60, false));
        Assert.Equal(JsonValueKind.Null, card.RootElement.GetProperty("icon").ValueKind);
    }

    [Theory]
    [InlineData("<html>404 Not Found</html>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("short")]
    public void What_isnt_a_picture_is_never_kept_as_one(string body) =>
        Assert.Null(AchievementIconCache.DataUri(System.Text.Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void The_card_carries_Exos_own_reduce_motion_and_the_page_ignores_windows()
    {
        using var card = JsonDocument.Parse(AchievementWatch.Card(Sample(null), 60, true));
        Assert.True(card.RootElement.GetProperty("reduce").GetBoolean());
        Assert.DoesNotContain("prefers-reduced-motion", BannerPage());
    }

    [Fact]
    public void The_kept_icons_are_not_in_a_folder_startup_cleans_away() =>
        Assert.DoesNotContain("\"achievement-art\"", File.ReadAllText(Path.Combine(Repo.Root, "ExoLauncher", "Services", "LegacyProfileDataCleanup.cs")));

    [Fact]
    public void Volume_scales_samples_and_nothing_else()
    {
        var wav = new byte[48];
        wav[0] = (byte)'R';
        BitConverter.TryWriteBytes(wav.AsSpan(44), (short)1000);
        BitConverter.TryWriteBytes(wav.AsSpan(46), (short)-1000);
        var half = AchievementBanner.AtVolume(wav, 50);
        Assert.Equal(wav[..44], half[..44]);
        Assert.Equal((short)500, BitConverter.ToInt16(half, 44));
        Assert.Equal((short)-500, BitConverter.ToInt16(half, 46));
        Assert.Equal((short)1000, BitConverter.ToInt16(wav, 44));
        Assert.Equal(wav, AchievementBanner.AtVolume(wav, 100));
        Assert.Equal((short)0, BitConverter.ToInt16(AchievementBanner.AtVolume(wav, 0), 44));
    }

    [Theory]
    [InlineData("common")]
    [InlineData("rare")]
    [InlineData("epic")]
    [InlineData("legendary")]
    [InlineData("completion")]
    public void The_shipped_sounds_are_the_format_the_host_scales(string name)
    {
        var wav = File.ReadAllBytes(Path.Combine(Repo.Root, "ExoLauncher", "Assets", "Overlay", "sounds", name + ".wav"));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
        Assert.Equal((2, 48000, 16), (BitConverter.ToInt16(wav, 22), BitConverter.ToInt32(wav, 24), BitConverter.ToInt16(wav, 34)));
    }

    [Fact]
    public void SampleFrom_skips_corrupt_and_small_cached_icons()
    {
        var dir = Directory.CreateTempSubdirectory("exo-ach-sample-").FullName;
        try
        {
            static byte[] PngHeader(int w, int h)
            {
                var bytes = new byte[24];
                byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
                sig.CopyTo(bytes, 0);
                "IHDR"u8.CopyTo(bytes.AsSpan(12));
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), w);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), h);
                return bytes;
            }

            var small = Path.Combine(dir, "small");
            var good = Path.Combine(dir, "good");
            var corrupt = Path.Combine(dir, "corrupt");
            File.WriteAllBytes(good, PngHeader(128, 128));
            File.WriteAllBytes(small, PngHeader(32, 32));
            File.WriteAllBytes(corrupt, [0x01, 0x02, 0x03]);
            File.SetLastWriteTimeUtc(good, DateTime.UtcNow.AddMinutes(-2));
            File.SetLastWriteTimeUtc(small, DateTime.UtcNow.AddMinutes(-1));
            File.SetLastWriteTimeUtc(corrupt, DateTime.UtcNow);

            var picked = AchievementIconCache.SampleFrom(dir);
            Assert.NotNull(picked);
            Assert.StartsWith("data:image/png;base64,", picked, StringComparison.Ordinal);
            Assert.Equal(Convert.ToBase64String(PngHeader(128, 128)), picked["data:image/png;base64,".Length..]);

            DeletionGate.DeleteFile(good);
            Assert.Null(AchievementIconCache.SampleFrom(dir));
        }
        finally { DeletionGate.DeleteDirectory(dir, recursive: true); }
    }

    [Fact]
    public void Banner_page_announces_ready_and_never_removes_art_on_decode()
    {
        var page = BannerPage();
        Assert.Contains("banner.ready", page, StringComparison.Ordinal);
        Assert.DoesNotContain("art.remove()", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_page_wires_host_with_load_and_dom_ready_fallbacks()
    {
        var page = BannerPage();
        Assert.Contains("wireHost", page, StringComparison.Ordinal);
        Assert.Contains("DOMContentLoaded", page, StringComparison.Ordinal);
        Assert.Contains("'load'", page, StringComparison.Ordinal);
        Assert.Contains("banner.ready", page, StringComparison.Ordinal);
    }
}

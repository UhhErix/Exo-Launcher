using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// web-client-preferences.json read through the real WebClientPreferences.
/// Ways it could fail: a music service saved before Cider was removed reads
/// back as "cider", which no room hosts any more, so the Music room and the
/// startup prewarm silently do nothing; the move rewrites other services or
/// drops the permission choices beside it; a file from when Exo could use the
/// Spotify app still holds spotifyApp, and that must not stop it loading.
/// </summary>
public sealed class WebClientPreferencesTests : IDisposable
{
    private readonly string? _prior = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "exo-webprefs-" + Guid.NewGuid().ToString("N"));

    public WebClientPreferencesTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _prior);
        try { DeletionGate.DeleteDirectory(_dir, true); } catch { }
    }

    private static void Write(string json) =>
        File.WriteAllText(Path.Combine(PathHelper.AppDataDir, "web-client-preferences.json"), json);

    [Fact]
    public void A_saved_Cider_choice_reads_as_Apple_Music()
    {
        Write("""{"musicService":"cider","microphone":"allow"}""");
        var options = WebClientPreferences.Reload();
        Assert.Equal("apple", options.MusicService);
        Assert.Equal("allow", options.Microphone);
        Assert.False(MusicWebPolicy.IsService("cider"));
    }

    [Fact]
    public void A_file_saved_with_the_old_Spotify_app_choice_still_loads()
    {
        Write("""{"musicService":"spotify","spotifyApp":true,"microphone":"block"}""");
        var options = WebClientPreferences.Reload();
        Assert.Equal("spotify", options.MusicService);
        Assert.Equal("block", options.Microphone);
    }

    [Theory]
    [InlineData("spotify")]
    [InlineData("apple")]
    [InlineData("youtube")]
    public void Other_services_read_back_unchanged(string service)
    {
        Write("{\"musicService\":\"" + service + "\"}");
        Assert.Equal(service, WebClientPreferences.Reload().MusicService);
    }

    [Theory]
    [InlineData(null, null, "spotify")]
    [InlineData("", null, "spotify")]
    [InlineData("unknown", null, "spotify")]
    [InlineData("spotify", "spotify", "spotify")]
    [InlineData("APPLE", "apple", "apple")]
    [InlineData("cider", "apple", "apple")]
    [InlineData("ytmusic", "youtube", "youtube")]
    [InlineData(" YouTube ", "youtube", "youtube")]
    public void Music_service_normalization_and_default_work(string? input, string? expectedNormalized, string expectedEffective)
    {
        Assert.Equal(expectedNormalized, WebClientPreferences.TryNormalizeMusicService(input));
        Assert.Equal(expectedEffective, WebClientPreferences.EffectiveMusicService(input));
    }

    [Fact]
    public void Settings_uiPrefs_projects_effective_music_service_without_mutating_source()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("true");
        var source = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["voiceChat"] = doc.RootElement.Clone(),
        };

        var merged = WebClientPreferences.BuildSettingsUiPrefs(source, "apple");

        Assert.False(source.ContainsKey(WebClientPreferences.UiPrefMusicServiceKey));
        Assert.Equal("apple", merged[WebClientPreferences.UiPrefMusicServiceKey].GetString());
        Assert.True(merged["voiceChat"].GetBoolean());
        Assert.Equal("spotify", WebClientPreferences.BuildSettingsUiPrefs(source, null)[WebClientPreferences.UiPrefMusicServiceKey].GetString());
    }

    [Fact]
    public void Extracting_music_service_from_uiPrefs_strips_it_and_keeps_other_prefs()
    {
        using var patch = System.Text.Json.JsonDocument.Parse("""{"exo.musicWebService":"youtube","voiceChat":false}""");
        var (service, remaining) = WebClientPreferences.ExtractMusicServiceFromUiPrefs(patch.RootElement);

        Assert.Equal("youtube", service);
        Assert.Single(remaining);
        Assert.False(remaining["voiceChat"].GetBoolean());
    }

    [Fact]
    public void SaveMusicService_normalizes_and_preserves_permissions()
    {
        Write("""{"microphone":"allow"}""");
        var saved = WebClientPreferences.SaveMusicService("ytmusic");

        Assert.Equal("youtube", saved.MusicService);
        Assert.Equal("allow", saved.Microphone);
        Assert.Equal("youtube", WebClientPreferences.Reload().MusicService);
        Assert.Throws<ArgumentException>(() => WebClientPreferences.SaveMusicService("invalid"));
    }
}

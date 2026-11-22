using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

internal sealed partial class MusicEngineService
{
    private SpotifyNative? _spotifyNative;
    private CoreWebView2? _spotifyNativeCore;

    internal Task<object> SpotifyViewAsync(string? view, string? uri, string? query, int offset)
    {
        if (_musicWebClosed || _dispatcher is null)
            return Task.FromResult<object>(new { ok = false, message = "Music engine is unavailable." });
        return OnUiAsync(() => SpotifyViewCoreAsync(view, uri, query, offset));
    }

    internal Task<object> SpotifyActAsync(string? action, string? uri, string? context, int? index, bool? on, string? uid, string? query,
        string? name = null, string? description = null, string? image = null)
    {
        if (_musicWebClosed || _dispatcher is null)
            return Task.FromResult<object>(new { ok = false, message = "Music engine is unavailable." });
        return OnUiAsync(() => SpotifyActCoreAsync(action, uri, context, index, on, uid, query, name, description, image));
    }

    private SpotifyNative? SpotifyBridge()
    {
        if (_musicWebService != "spotify" || !_musicWebReady || MusicCore is not { } core) return null;
        if (_spotifyNative is { } page && ReferenceEquals(_spotifyNativeCore, core)) return page;
        _spotifyNativeCore = core;
        return _spotifyNative = new SpotifyNative(MusicWebEvaluate(core), MusicWebEvaluate(core, click: true));
    }

    private Task<JsonElement?> SpotifyDevicesAsync() =>
        SpotifyCallAsync(bridge => bridge.ViewAsync("devices", null, null, 0, _lifetimeCts.Token));

    private async Task<object> SpotifyViewCoreAsync(string? view, string? uri, string? query, int offset)
    {
        if (view is null || !SpotifyNative.Views.Contains(view)) return new { ok = false, message = "Unknown page." };
        if (view is "playlist" or "album" or "artist" or "show" or "lyrics" or "discography" or "credits" or "radio" && !SpotifyNative.IsUri(uri))
            return new { ok = false, message = "Unknown page." };
        if (view == "songs" && uri is not null && !SpotifyNative.IsId(uri)) return new { ok = false, message = "Unknown page." };
        if (view == "library" && (uri is not null && !SpotifyNative.IsUri(uri) || !SpotifyNative.IsLibraryQuery(query)))
            return new { ok = false, message = "Unknown page." };
        if (view == "songs" && !MusicSongs.IsQuery(query)) return new { ok = false, message = "Unknown page." };
        query = query is { Length: > 200 } ? query[..200] : query;
        if (!await MusicWebUpAsync("spotify")) return new { ok = false, message = "Spotify is not answering. Try again." };
        Task<JsonElement?> Read() => SpotifyCallAsync(bridge => bridge.ViewAsync(view, uri, query, Math.Clamp(offset, 0, 100_000), _lifetimeCts.Token));
        var data = await Read();
        if (data is { ValueKind: JsonValueKind.Object } first && first.TryGetProperty("error", out var missing) &&
            missing.GetString() is { } reason && reason.StartsWith("missing query", StringComparison.Ordinal))
        {
            try { await Task.Delay(2000, _lifetimeCts.Token); } catch (OperationCanceledException) { return new { ok = false, message = "Spotify is not answering. Try again." }; }
            data = await Read();
        }
        if (data is not { ValueKind: JsonValueKind.Object } page) return new { ok = false, message = "Spotify is not answering. Try again." };
        if (page.TryGetProperty("error", out var error))
        {
            AppLog.Warn("Spotify room " + view + ": " + error.GetString());
            return new { ok = false, message = error.GetString() == "signed-out" ? "Sign in to Spotify." : "Spotify could not load this page." };
        }
        if (page.GetRawText().Length > MaxMusicPageCharacters)
        {
            AppLog.Warn("Spotify room " + view + ": page too large, dropped.");
            return new { ok = false, message = "Spotify could not load this page." };
        }
        return new { ok = true, data = page };
    }

    private async Task<object> SpotifyActCoreAsync(string? action, string? uri, string? context, int? index, bool? on, string? uid, string? query,
        string? name = null, string? description = null, string? image = null)
    {
        if (!MusicSongs.IsQuery(query)) return new { ok = false, message = "Nothing to play." };
        if (action is null || !SpotifyNative.Actions.Contains(action)) return new { ok = false, message = "Unknown action." };
        if (!SpotifyNative.IsName(name, 300) || !SpotifyNative.IsName(description, 300) || !SpotifyNative.IsCover(image))
            return new { ok = false, message = "Spotify did not take that. Try again." };
        if (action is "createPlaylist" or "createFolder" or "renameFolder" && string.IsNullOrWhiteSpace(name))
            return new { ok = false, message = "Give it a name." };
        if (action is "createPlaylist" or "createFolder" or "moveToFolder" && context is not null && !SpotifyNative.IsUri(context))
            return new { ok = false, message = "Spotify did not take that. Try again." };
        if (action == "movePlaylistTrack" && (!SpotifyNative.IsUri(context) || name is not { Length: > 6 }))
            return new { ok = false, message = "Spotify did not take that. Try again." };
        if (action == "jamJoin" && !SpotifyNative.IsId(name)) return new { ok = false, message = "That is not a Jam link." };
        if (action == "playlistImage" && image is null) return new { ok = false, message = "Choose a picture." };
        var needsUri = action is "play" or "enqueue" or "skipTo" or "like" or "addToPlaylist" or "removeFromPlaylist" or "playNext" or "dequeue" or "queueMove"
            or "renameFolder" or "deleteFolder" or "moveToFolder" or "editPlaylist" or "playlistImage" or "privacy" or "collaborative" or "movePlaylistTrack"
            or "pin" or "played" or "download";
        if (needsUri && !SpotifyNative.IsUri(uri) && !(action == "play" && SpotifyNative.IsUri(context)))
            return new { ok = false, message = "Nothing to play." };
        if (action is "play" or "addToPlaylist" or "removeFromPlaylist" && context is not null && !SpotifyNative.IsUri(context) ||
            action is "addToPlaylist" or "removeFromPlaylist" && context is null)
            return new { ok = false, message = "Nothing to play." };
        if (action == "transfer" && context is not { Length: > 0 and < 120 }) return new { ok = false, message = "Choose a device." };
        if (action == "queueMove" && context is not { Length: > 6 and < 300 }) return new { ok = false, message = "Spotify did not take that. Try again." };
        if (action == "audio" && context is not ("quality" or "crossfade" or "crossfadeMs" or "gapless" or "autoMix" or "normalize" or "mono" or "smartShuffle" or "exclusive" or "equalizer" or "equalizerGain"))
            return new { ok = false, message = "Unknown setting." };
        if (action is "removeFromPlaylist" or "jamRemove" && !SpotifyNative.IsId(uid)) return new { ok = false, message = "Spotify did not take that. Try again." };
        if (uid is { Length: > 80 }) uid = null;
        var act = new SpotifyAct(action, SpotifyNative.IsUri(uri) ? uri : null, context, index, on, uid, query, name, description, image);
        if (!await MusicWebUpAsync("spotify")) return new { ok = false, message = "Spotify did not take that. Try again." };
        if (await SpotifyCallAsync(bridge => bridge.PingAsync(_lifetimeCts.Token)) is null)
            return new { ok = false, message = "Spotify did not take that. Try again." };
        var reply = await SpotifyCallAsync(bridge => bridge.ActAsync(act, _lifetimeCts.Token), attempts: 1);
        if (reply is { ValueKind: JsonValueKind.Object } answer && answer.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            return new { ok = true, uri = answer.TryGetProperty("uri", out var made) && SpotifyNative.IsUri(made.GetString()) ? made.GetString() : null };
        if (reply is { ValueKind: JsonValueKind.Object } failed && failed.TryGetProperty("error", out var error))
            AppLog.Warn("Spotify room " + action + ": " + error.GetString());
        return new { ok = false, message = "Spotify did not take that. Try again." };
    }

    private async Task<bool> SpotifyJamGuestAsync()
    {
        try
        {
            var page = await SpotifyCallAsync(bridge => bridge.ViewAsync("jam", null, null, 0, _lifetimeCts.Token), attempts: 2);
            return page is { ValueKind: JsonValueKind.Object } state && state.TryGetProperty("session", out var session) &&
                session.ValueKind == JsonValueKind.Object && session.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.False;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private async Task<JsonElement?> SpotifyCallAsync(Func<SpotifyNative, Task<JsonElement?>> call, int attempts = 7)
    {
        for (var attempt = 0; attempt < attempts && !_lifetimeCts.IsCancellationRequested; attempt++)
        {
            if (SpotifyBridge() is not { } bridge) return null;
            WakeMusicWeb();
            if (attempt > 0)
            {
                try { await Task.Delay(500, _lifetimeCts.Token); } catch (OperationCanceledException) { return null; }
            }
            JsonElement? reply;
            try { reply = await call(bridge); }
            catch { reply = null; }
            if (reply is not null) return reply;
        }
        return null;
    }

    private void DropSpotifyBridge()
    {
        _spotifyNative = null;
        _spotifyNativeCore = null;
    }
}

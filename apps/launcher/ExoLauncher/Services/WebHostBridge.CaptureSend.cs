using System.Text.Json;
using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    /// <summary>
    /// capture.send: a capture straight into up to five Discord conversations through Exo's own Discord client, with an
    /// optional message: the file read (or cut to fit) by the window, posted once per conversation. A conversation it went to
    /// is named in <c>sent</c>, so a retry after one failed never sends it twice.
    /// </summary>
    private static async Task<object> CaptureSendAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (App.MainAppWindow is not { } window) return new { ok = false, message = "The app window is unavailable." };
        if (DiscordSession.Current is not { } session) return new { ok = false, message = "Sign in to Discord in Exo to send captures." };
        var channels = hasParams && p.TryGetProperty("channelIds", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => DiscordRest.Id(e.GetString()!)).Distinct().Take(CaptureShare.MaxConversations).ToList()
            : [];
        if (channels.Count == 0) return new { ok = false, message = "Pick where to send it." };
        var note = ReadString(p, hasParams, "content")?.Trim() ?? "";
        if (note.Length > 4000) return new { ok = false, message = "Your message is too long." };
        // Every conversation checked before the file is read or cut: one that can't take it stops the send before anything goes.
        var guilds = new Dictionary<string, string?>();
        foreach (var id in channels)
        {
            try { guilds[id] = session.Cache.CanSend(id) ? session.Cache.GuildOf(id) : throw new UnauthorizedAccessException(); }
            catch (UnauthorizedAccessException) { return new { ok = false, message = "You can't send messages in one of those." }; }
        }
        var upload = await Task.Run(() => window.CaptureUploadAsync(ReadString(p, hasParams, "id"), ReadDouble(p, hasParams, "startMs"), ReadDouble(p, hasParams, "endMs"), ct), ct).ConfigureAwait(true);
        if (upload.File is not { } file) return new { ok = false, message = upload.Error, item = upload.Made };
        var sent = new List<string>();
        foreach (var id in channels)
        {
            var reply = await session.Rest.PostMessageAsync(id, note, null, guilds[id], ct, [file]).ConfigureAwait(true);
            if (reply.Error is not null) return new { ok = false, message = reply.Error, sent, item = upload.Made };
            sent.Add(id);
        }
        return new { ok = true, message = (string?)null, sent, item = upload.Made };
    }
}

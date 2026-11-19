using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// What the room's Home and Servers pages show of right now, from what the connection already holds: the last thing
/// said in a DM and the newest mention in a channel. Nothing here asks Discord for anything, except
/// one read of Mentions per connection (the bridge's, when a mention landed while Exo was away).
/// </summary>
internal sealed partial class DiscordCache
{
    /// <summary>A message as one plain line: names for mentions, emoji by name, spoilers hidden, no markup; what it holds when it has no words.</summary>
    private string PlainLine(JsonElement d, int most)
    {
        var text = Str(d, "content") ?? "";
        foreach (var user in Array(d, "mentions"))
            if (Str(user, "id") is { } uid)
                text = text.Replace($"<@{uid}>", "@" + ReadUser(user).Name).Replace($"<@!{uid}>", "@" + ReadUser(user).Name);
        text = Regex.Replace(text, "<a?:(\\w+):\\d+>", ":$1:");
        text = Regex.Replace(text, "\\|\\|.+?\\|\\|", "Spoiler", RegexOptions.Singleline);
        text = Regex.Replace(text, "(\\*\\*|__|~~|`{1,3})", "");
        text = Regex.Replace(text, "^\\s*>+\\s?", "", RegexOptions.Multiline);
        text = Regex.Replace(text, "\\s+", " ").Trim();
        if (text.Length == 0) text = Array(d, "attachments").Any() ? "Sent an attachment" : Array(d, "sticker_items").Any() ? "Sent a sticker" : "Sent a message";
        return text.Length > most ? text[..most] + "…" : text;
    }

    /// <summary>
    /// A DM's last message, as its row says it: "You: …" for yours, the author's name first in a group. Someone blocked says
    /// nothing. Publishes the row's new line when it changed.
    /// </summary>
    private void KeepDmLine(Channel channel, JsonElement d, List<(string, object)> events)
    {
        var author = d.TryGetProperty("author", out var a) ? ReadUser(a) : null;
        var id = Str(d, "id");
        if (id is null || (channel.SaidId is { } kept && Snowflake(id) < Snowflake(kept))) return;
        var line = author is null || _blocked.Contains(author.Id) ? null
            : author.Id == Me?.Id ? "You: " + PlainLine(d, 140)
            : channel.Recipients.Count > 1 ? $"{author.Name}: {PlainLine(d, 140)}" : PlainLine(d, 140);
        if (channel.SaidId == id && channel.Said == line) return;
        (channel.Said, channel.SaidId) = (line, id);
        events.Add(("discord.dmLine", new { channelId = channel.Id, line, at = SnowflakeMs(id) }));
    }

    /// <summary>The newest messages of a DM were read (opened, or the pointer rested on it): its row learns what it says. The page's order is Discord's, newest first.</summary>
    internal List<(string, object)> KeepNewest(string channelId, JsonElement page)
    {
        var events = new List<(string, object)>();
        lock (_gate)
            if (_private.TryGetValue(channelId, out var channel) && page.ValueKind == JsonValueKind.Array)
                foreach (var message in page.EnumerateArray()) { KeepDmLine(channel, message, events); break; }
        return events;
    }

    private static long SnowflakeMs(string id) => (long)(Snowflake(id) >> 22) + 1420070400000L;
}
